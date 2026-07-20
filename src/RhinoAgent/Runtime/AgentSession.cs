using System.Text;
using System.Text.RegularExpressions;
using Rhino;
using RhinoAgent.Memory;
using RhinoAgent.Providers;
using RhinoAgent.Skills;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class AgentSession
{
    private const int MaxMissingActionRecoveries = 2;
    private const string MissingActionContinuationDirective =
        "Your previous response announced an action but contained no RhinoAgent tool call. Continue from that update now. Emit at least one <rhino-agent> tool block before ending this response, and do not repeat the plan.";
    private static readonly Regex MissingActionAnnouncementRegex = new(
        @"(?:^|[\r\n.!?]\s*)(?:(?:i['\u2019]?ll|i\s+will|i['\u2019]?m\s+going\s+to|let\s+me|next[,]?\s+i['\u2019]?ll)\s+(?:go\s+ahead(?:\s+and)?|start|begin|continue|proceed|model|create|build|add|make|set\s+up|inspect|check|capture|import|edit|update|run|generate|draw|construct|lay\s+out|block\s+out)\b|(?:starting|beginning|continuing|proceeding)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ActionRequestRegex = new(
        @"(?:^\s*(?:(?:please|now)\s+)*(?:go\s+ahead(?:\s+and)?|start|begin|continue|proceed|model|create|build|add|make|set\s+up|inspect|check|capture|import|edit|update|run|generate|draw|construct|lay\s+out|block\s+out)\b|\b(?:can\s+you|could\s+you|would\s+you|i\s+need\s+you\s+to|i\s+want\s+you\s+to|help\s+me(?:\s+to)?|go\s+ahead(?:\s+and)?)\s+(?:start|begin|continue|proceed|model|create|build|add|make|set\s+up|inspect|check|capture|import|edit|update|run|generate|draw|construct|lay\s+out|block\s+out)\b|\bgo\s+ahead\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly RhinoDoc _doc;
    private readonly AgentConfig _config;
    private readonly IAgentProvider _provider;
    private readonly RhinoToolHost _toolHost;
    private readonly ApprovalService _approvals;
    private readonly SkillStore _skillStore;
    private readonly AgentMemoryUpdateService? _memoryUpdater;
    private readonly AgentConversationIndex _conversationIndex = new();
    private readonly object _conversationIndexSync = new();
    private readonly List<(string Role, string Text)> _history = [];
    private readonly List<string> _queuedSkillNames = [];
    private Task _conversationIndexWorker = Task.CompletedTask;
    private bool _conversationIndexWorkerRunning;
    private int _conversationIndexRequestedTurnCount;
    private int _conversationIndexInFlightTurnCount;

    public AgentSession(
        RhinoDoc doc,
        AgentConfig config,
        IAgentProvider provider,
        RhinoToolHost toolHost,
        ApprovalService approvals,
        SkillStore? skillStore = null,
        AgentMemoryUpdateService? memoryUpdater = null)
    {
        _doc = doc;
        _config = config;
        _provider = provider;
        _toolHost = toolHost;
        _approvals = approvals;
        _skillStore = skillStore ?? new SkillStore();
        _memoryUpdater = memoryUpdater;
    }

    public void Clear()
    {
        _history.Clear();
        _queuedSkillNames.Clear();
        _toolHost.AttachmentStore.Clear();
        _provider.Reset();
    }

    public void QueueSkillForNextTurn(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            _queuedSkillNames.Add(name);
    }

    public bool TryContinueLatestProviderConversation(out string message)
    {
        if (!TryGetConversationResumeProvider(out var provider, out message))
            return false;

        _history.Clear();
        _toolHost.AttachmentStore.Clear();
        return provider.TryContinueLatestConversation(out message);
    }

    public bool TryResumeProviderConversation(string sessionId, out string message)
    {
        if (!TryGetConversationResumeProvider(out var provider, out message))
            return false;

        _history.Clear();
        _toolHost.AttachmentStore.Clear();
        return provider.TryResumeConversation(sessionId, out message);
    }

    public string? GetProviderSessionStatus()
    {
        if (_provider is not IConversationResumeProvider provider)
            return null;

        return string.IsNullOrWhiteSpace(provider.ActiveSessionId)
            ? "  Active provider session: none captured yet"
            : $"  Active provider session: {provider.ActiveSessionId}";
    }

    public AgentProviderKind ProviderKind => _provider.Kind;

    public Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        if (_provider is not IModelCatalogProvider provider)
            throw new NotSupportedException($"{_provider.DisplayName} does not expose a model catalog.");

        return provider.GetAvailableModelsAsync(progress, cancellationToken);
    }

    public int PendingConversationIndexTurnCount
    {
        get
        {
            lock (_conversationIndexSync)
                return _conversationIndex.PendingTurnCount + _conversationIndexInFlightTurnCount;
        }
    }

    public bool IsConversationIndexing
    {
        get
        {
            lock (_conversationIndexSync)
                return _conversationIndexWorkerRunning;
        }
    }

    public Task<AgentTurnResult> RunUserTurnAsync(
        string userMessage,
        CancellationToken cancellationToken = default,
        Action<string>? diagnostics = null,
        IReadOnlyList<string>? forcedSkillNames = null)
    {
        return RunUserTurnAsync(
            new AgentUserMessage(userMessage, Array.Empty<AgentAttachment>()),
            cancellationToken,
            diagnostics,
            forcedSkillNames);
    }

    public async Task<AgentTurnResult> RunUserTurnAsync(
        AgentUserMessage userMessage,
        CancellationToken cancellationToken = default,
        Action<string>? diagnostics = null,
        IReadOnlyList<string>? forcedSkillNames = null)
    {
        diagnostics?.Invoke("turn-entered");
        _history.Add(("user", userMessage.Text));
        var selectedSkills = SelectSkillsForTurn(userMessage.Text, forcedSkillNames);
        if (selectedSkills.Count > 0)
            CommandLineUi.Debug("Loaded skill: " + string.Join(", ", selectedSkills.Select(skill => skill.Name)));

        var toolResults = new List<ToolExecutionResult>();
        var visibleTranscript = new StringBuilder();
        var totalToolCalls = 0;
        var totalToolResults = 0;
        var missingActionRecoveries = 0;
        string? continuationDirective = null;
        AgentProviderResult? lastProviderResult = null;

        var maxRounds = Math.Max(1, _config.MaxToolRounds);
        for (var round = 0; round < maxRounds; round++)
        {
            var recoveringMissingAction = !string.IsNullOrWhiteSpace(continuationDirective);
            var prompt = await RhinoUiDispatcher.InvokeAsync(
                () => AgentPromptBuilder.Build(
                    _doc,
                    _config,
                    _history,
                    toolResults,
                    _toolHost.DescribeTools(),
                    selectedSkills,
                    userMessage.Attachments,
                    continuationDirective));
            continuationDirective = null;
            diagnostics?.Invoke($"prompt-built: {prompt.Length} chars");
            WritePromptPackageToDebugger(prompt, round, maxRounds);
            diagnostics?.Invoke("thinking-write-start");
            var lastProgressMessage = "";
            AgentProviderResult providerResult;
            using (CommandLineUi.Thinking(
                recoveringMissingAction
                    ? "Agent is continuing"
                    : round == 0 ? "Agent is thinking" : "Agent is checking tool results",
                _config.ShowDebugMessages))
            {
                diagnostics?.Invoke("thinking-write-complete");
                diagnostics?.Invoke("provider-run-start");
                providerResult = _provider.RunPromptAsync(
                        new AgentProviderPrompt(
                            prompt,
                            round == 0 ? userMessage.Attachments : Array.Empty<AgentAttachment>()),
                        progress =>
                        {
                            diagnostics?.Invoke($"provider-progress: {progress.Message}");
                            if (!progress.IsTransient
                                && !string.IsNullOrWhiteSpace(progress.Message)
                                && !string.Equals(progress.Message, lastProgressMessage, StringComparison.Ordinal))
                            {
                                Debug(progress.Message);
                                lastProgressMessage = progress.Message;
                            }
                        },
                        cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            diagnostics?.Invoke($"provider-run-complete: {providerResult.ExitCode}");
            diagnostics?.Invoke($"provider-text: {providerResult.Text.Length} chars");
            lastProviderResult = providerResult;

            if (providerResult.ExitCode != 0)
            {
                Debug($"Provider exited with code {providerResult.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(providerResult.StandardError))
                    Debug(providerResult.StandardError.Trim());
                var result = BuildResult(
                    false,
                    providerResult,
                    visibleTranscript,
                    totalToolCalls,
                    totalToolResults,
                    false,
                    $"Provider exited with code {providerResult.ExitCode}.");
                CompleteTurnAndScheduleMemory(userMessage.Text, result);
                return result;
            }

            var parsed = AgentResponseParser.Parse(providerResult.Text);
            var visible = parsed.VisibleText.Trim();
            diagnostics?.Invoke($"response-parsed: visible={visible.Length} chars; tool_calls={parsed.ToolCalls.Count}");
            if (visible.Length > 0)
            {
                diagnostics?.Invoke("visible-write-start");
                CommandLineUi.AgentResponse(TrimForCommandLine(visible));
                diagnostics?.Invoke("visible-write-complete");
                visibleTranscript.AppendLine(visible);
            }

            PrintUsage(providerResult);
            _history.Add(("assistant", providerResult.Text));
            totalToolCalls += parsed.ToolCalls.Count;

            if (parsed.ToolCalls.Count == 0)
            {
                if (ShouldRecoverMissingAction(userMessage.Text, visible))
                {
                    if (missingActionRecoveries < MaxMissingActionRecoveries)
                    {
                        missingActionRecoveries++;
                        continuationDirective = MissingActionContinuationDirective;
                        diagnostics?.Invoke($"missing-action-recovery: {missingActionRecoveries}/{MaxMissingActionRecoveries}");
                        Debug($"Agent announced an action without issuing a tool; continuing automatically ({missingActionRecoveries}/{MaxMissingActionRecoveries}).");
                        round--;
                        continue;
                    }

                    const string failureMessage =
                        "The provider repeatedly announced an action without issuing a RhinoAgent tool call, so RhinoAgent could not safely advance the task.";
                    CommandLineUi.AgentResponse(failureMessage);
                    visibleTranscript.AppendLine(failureMessage);
                    diagnostics?.Invoke("turn-failed-missing-action");
                    var failedResult = BuildResult(
                        false,
                        providerResult,
                        visibleTranscript,
                        totalToolCalls,
                        totalToolResults,
                        false,
                        failureMessage);
                    CompleteTurnAndScheduleMemory(userMessage.Text, failedResult);
                    return failedResult;
                }

                diagnostics?.Invoke("turn-complete-no-tools");
                var result = BuildResult(
                    true,
                    providerResult,
                    visibleTranscript,
                    totalToolCalls,
                    totalToolResults,
                    false,
                    null);
                CompleteTurnAndScheduleMemory(userMessage.Text, result);
                return result;
            }

            toolResults.Clear();
            foreach (var call in parsed.ToolCalls)
            {
                diagnostics?.Invoke($"tool-start: {call.Tool}");
                if (!_approvals.ShouldExecute(call, _toolHost, out var reason))
                {
                    Debug($"Plan: {call.Tool} {FormatArguments(call.Arguments)}");
                    toolResults.Add(new ToolExecutionResult(call.Tool, false, reason, false, true));
                    diagnostics?.Invoke($"tool-skipped: {call.Tool}");
                    continue;
                }

                if (_approvals.RequiresPrompt(call, _toolHost)
                    && !_approvals.PromptForApproval(call, _toolHost, cancellationToken))
                {
                    toolResults.Add(new ToolExecutionResult(call.Tool, false, "User denied tool call.", false, true));
                    diagnostics?.Invoke($"tool-denied: {call.Tool}");
                    continue;
                }

                Debug($"Running tool: {call.Tool}");
                var result = await _toolHost.ExecuteAsync(call, cancellationToken);
                Debug(result.Success ? $"Tool complete: {call.Tool}" : $"Tool failed: {call.Tool}");
                if (!string.IsNullOrWhiteSpace(result.Output))
                    Debug(TrimForCommandLine(result.Output));
                toolResults.Add(result);
                totalToolResults++;
                diagnostics?.Invoke($"tool-complete: {call.Tool}; success={result.Success}; output={result.Output.Length} chars");
            }

            diagnostics?.Invoke($"tool-round-complete: {toolResults.Count} results");
            if (round == maxRounds - 1 && CompletedActionTool(toolResults))
            {
                diagnostics?.Invoke("turn-complete-final-tool-success");
                var result = BuildResult(
                    true,
                    providerResult,
                    visibleTranscript,
                    totalToolCalls,
                    totalToolResults,
                    false,
                    null);
                CompleteTurnAndScheduleMemory(userMessage.Text, result);
                return result;
            }
        }

        Debug("Agent stopped after the configured tool-round limit. Raise maxToolRounds in config.json if needed.");
        var stoppedResult = BuildResult(
            false,
            lastProviderResult,
            visibleTranscript,
            totalToolCalls,
            totalToolResults,
            true,
            "Agent stopped after the configured tool-round limit.");
        CompleteTurnAndScheduleMemory(userMessage.Text, stoppedResult);
        return stoppedResult;
    }

    public AgentConversationIndexScheduleResult StartConversationIndexing()
    {
        if (_memoryUpdater is null)
            return new AgentConversationIndexScheduleResult(
                false,
                false,
                0,
                "No memory updater is configured for this session.");

        lock (_conversationIndexSync)
        {
            var pendingTurnCount = _conversationIndex.PendingTurnCount;
            var totalTurnCount = pendingTurnCount + _conversationIndexInFlightTurnCount;
            if (_conversationIndexWorkerRunning)
            {
                _conversationIndexRequestedTurnCount = Math.Max(
                    _conversationIndexRequestedTurnCount,
                    pendingTurnCount);
                return new AgentConversationIndexScheduleResult(
                    false,
                    true,
                    totalTurnCount,
                    pendingTurnCount > 0
                        ? $"Conversation indexing is already running; {pendingTurnCount} queued turn(s) will follow in the background."
                        : "Conversation indexing is already running in the background.");
            }

            if (pendingTurnCount == 0)
                return new AgentConversationIndexScheduleResult(
                    false,
                    false,
                    0,
                    "No unindexed conversation turns.");

            _conversationIndexRequestedTurnCount = pendingTurnCount;
            _conversationIndexWorkerRunning = true;
            _conversationIndexWorker = Task.Run(ProcessConversationIndexAsync);
            return new AgentConversationIndexScheduleResult(
                true,
                true,
                pendingTurnCount,
                $"Started background indexing for {pendingTurnCount} conversation turn(s).");
        }
    }

    public Task WaitForConversationIndexingAsync()
    {
        lock (_conversationIndexSync)
            return _conversationIndexWorker;
    }

    public string DescribeConversationIndexStatus()
    {
        lock (_conversationIndexSync)
        {
            return string.Join(Environment.NewLine,
            [
                "Conversation memory index",
                $"  Background worker: {(_conversationIndexWorkerRunning ? "running" : "idle")}",
                $"  In flight: {_conversationIndexInFlightTurnCount}",
                $"  Queued: {_conversationIndex.PendingTurnCount}",
                $"  Dropped: {_conversationIndex.DroppedTurnCount}"
            ]);
        }
    }

    private async Task ProcessConversationIndexAsync()
    {
        IReadOnlyList<AgentConversationTurn> activeBatch = [];
        try
        {
            while (true)
            {
                lock (_conversationIndexSync)
                {
                    if (_conversationIndexRequestedTurnCount <= 0
                        || _conversationIndex.PendingTurnCount == 0)
                    {
                        _conversationIndexRequestedTurnCount = 0;
                        _conversationIndexInFlightTurnCount = 0;
                        _conversationIndexWorkerRunning = false;
                        return;
                    }

                    var requestedBatchSize = Math.Min(
                        _conversationIndexRequestedTurnCount,
                        _conversationIndex.PendingTurnCount);
                    activeBatch = _conversationIndex.GetNextBatch(requestedBatchSize);
                    _conversationIndex.MarkIndexed(activeBatch);
                    _conversationIndexRequestedTurnCount -= activeBatch.Count;
                    _conversationIndexInFlightTurnCount = activeBatch.Count;
                }

                using var timeoutCancellation = _config.ProviderTurnTimeoutSeconds > 0
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(_config.ProviderTurnTimeoutSeconds))
                    : new CancellationTokenSource();
                var update = await _memoryUpdater!
                    .IndexConversationBatchAsync(activeBatch, timeoutCancellation.Token)
                    .ConfigureAwait(false);
                if (!update.Completed)
                {
                    RestoreActiveBatchAndStop(activeBatch);
                    ReportConversationIndexDeferred(update.Message);
                    return;
                }

                lock (_conversationIndexSync)
                    _conversationIndexInFlightTurnCount = 0;
                activeBatch = [];
                ReportConversationIndexResult(update);
            }
        }
        catch (OperationCanceledException)
        {
            RestoreActiveBatchAndStop(activeBatch);
            ReportConversationIndexDeferred("The background memory update timed out or was canceled.");
        }
        catch (Exception ex)
        {
            RestoreActiveBatchAndStop(activeBatch);
            ReportConversationIndexDeferred(ex.Message);
        }
    }

    private void CompleteTurnAndScheduleMemory(
        string userMessage,
        AgentTurnResult result)
    {
        if (_memoryUpdater is null || !_config.EnableDocumentMemory || !result.Success)
            return;

        int pendingTurnCount;
        bool droppedTurn;
        bool shouldStart;
        lock (_conversationIndexSync)
        {
            var droppedBeforeAdd = _conversationIndex.DroppedTurnCount;
            if (!_conversationIndex.TryAdd(userMessage, result, out _))
                return;

            droppedTurn = _conversationIndex.DroppedTurnCount > droppedBeforeAdd;
            pendingTurnCount = _conversationIndex.PendingTurnCount;
            shouldStart = _conversationIndex.ShouldFlushAutomatically
                || ShouldIndexImmediately(userMessage, result);
        }

        if (droppedTurn)
            Debug("Dropped the oldest unindexed conversation turn because the bounded memory queue was full.");

        if (!shouldStart)
        {
            Debug($"Queued conversation turn for memory indexing ({pendingTurnCount}/{AgentConversationIndex.AutomaticFlushTurnCount}).");
            return;
        }

        var schedule = StartConversationIndexing();
        if (schedule.Started)
            Debug(schedule.Message);
    }

    private void RestoreActiveBatchAndStop(IReadOnlyList<AgentConversationTurn> activeBatch)
    {
        lock (_conversationIndexSync)
        {
            if (activeBatch.Count > 0)
                _conversationIndex.RestoreBatch(activeBatch);
            _conversationIndexInFlightTurnCount = 0;
            _conversationIndexRequestedTurnCount = 0;
            _conversationIndexWorkerRunning = false;
        }
    }

    private void ReportConversationIndexResult(AgentMemoryMaintenanceResult update)
    {
        RhinoUiDispatcher.Post(() =>
        {
            if (update.Updated)
                CommandLineUi.Debug($"Background conversation indexing updated memory: {update.Reason}. Use /memory undo to revert.");
            else if (_config.ShowDebugMessages && !string.IsNullOrWhiteSpace(update.Message))
                CommandLineUi.Debug($"Background conversation indexing completed; memory unchanged: {update.Message}");
        });
    }

    private static void ReportConversationIndexDeferred(string message)
    {
        RhinoUiDispatcher.Post(() =>
            CommandLineUi.Debug($"Background conversation indexing deferred: {message}"));
    }

    private static bool ShouldIndexImmediately(string userMessage, AgentTurnResult result)
    {
        if (result.ToolResultCount > 0)
            return true;

        var text = userMessage.ToLowerInvariant();
        string[] durableWords =
        [
            "remember", "memory", "constraint", "decision", "todo", "prefer",
            "goal", "intent", "deadline", "warning", "important"
        ];
        return durableWords.Any(text.Contains);
    }

    internal static bool ShouldRecoverMissingAction(string userText, string visibleText) =>
        !string.IsNullOrWhiteSpace(userText)
        && !string.IsNullOrWhiteSpace(visibleText)
        && ActionRequestRegex.IsMatch(userText)
        && MissingActionAnnouncementRegex.IsMatch(visibleText);

    private void PrintUsage(AgentProviderResult result)
    {
        if (!_config.ShowUsageMessages)
            return;

        var usage = result.Usage;
        if (usage is null)
            return;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Model))
            parts.Add($"model {result.Model}");
        if (usage.InputTokens.HasValue)
            parts.Add($"in {usage.InputTokens.Value}");
        if (usage.OutputTokens.HasValue)
            parts.Add($"out {usage.OutputTokens.Value}");
        if (usage.CacheCreationInputTokens.HasValue)
            parts.Add($"cache-write {usage.CacheCreationInputTokens.Value}");
        if (usage.CacheReadInputTokens.HasValue)
            parts.Add($"cache-read {usage.CacheReadInputTokens.Value}");
        if (usage.ReasoningOutputTokens.HasValue)
            parts.Add($"reasoning {usage.ReasoningOutputTokens.Value}");
        if (usage.CostUsd.HasValue)
            parts.Add($"cost ${usage.CostUsd.Value:0.######}");

        if (parts.Count > 0)
            CommandLineUi.Usage(string.Join(", ", parts));
    }

    private IReadOnlyList<SkillContext> SelectSkillsForTurn(string userMessage, IReadOnlyList<string>? forcedSkillNames)
    {
        var forced = forcedSkillNames is { Count: > 0 }
            ? forcedSkillNames.ToArray()
            : DrainQueuedSkillNames();
        return _skillStore.SelectRelevantSkills(userMessage, 3, forced);
    }

    private string[] DrainQueuedSkillNames()
    {
        if (_queuedSkillNames.Count == 0)
            return [];

        var names = _queuedSkillNames.ToArray();
        _queuedSkillNames.Clear();
        return names;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void WritePromptPackageToDebugger(string prompt, int round, int maxRounds)
    {
        const int chunkSize = 3000;
        var chunkCount = Math.Max(1, (int)Math.Ceiling(prompt.Length / (double)chunkSize));

        System.Diagnostics.Debug.WriteLine(
            $"[RhinoAgent prompt package begin] round={round + 1}/{maxRounds}; provider={_provider.DisplayName}; chars={prompt.Length}; chunks={chunkCount}");

        for (var offset = 0; offset < prompt.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, prompt.Length - offset);
            var chunk = prompt.Substring(offset, length);
            var chunkNumber = (offset / chunkSize) + 1;
            System.Diagnostics.Debug.WriteLine($"[RhinoAgent prompt package chunk {chunkNumber}/{chunkCount}]");
            System.Diagnostics.Debug.WriteLine(chunk);
        }

        if (prompt.Length == 0)
            System.Diagnostics.Debug.WriteLine("[RhinoAgent prompt package empty]");

        System.Diagnostics.Debug.WriteLine("[RhinoAgent prompt package end]");
    }

    private void Debug(string message)
    {
        if (_config.ShowDebugMessages)
            CommandLineUi.Debug(message);
    }

    private bool TryGetConversationResumeProvider(
        out IConversationResumeProvider provider,
        out string message)
    {
        if (_provider is IConversationResumeProvider resumeProvider)
        {
            provider = resumeProvider;
            message = "";
            return true;
        }

        provider = null!;
        message = _provider.Kind == AgentProviderKind.Codex
            ? _provider.ProcessMode == AgentProviderProcessMode.LongRunning
                ? "Codex already continues within this Agent session. Cross-session resume is not supported by RhinoAgent yet."
                : "Codex stateless mode starts a fresh provider process for each turn. Cross-session resume is not supported by RhinoAgent yet."
            : $"{_provider.DisplayName} does not support conversation resume.";
        return false;
    }

    private static string FormatArguments(Dictionary<string, object?> args) =>
        string.Join(", ", args.Select(kvp => $"{kvp.Key}={kvp.Value}"));

    private static string TrimForCommandLine(string value)
    {
        const int max = 4000;
        if (value.Length <= max)
            return value;
        var builder = new StringBuilder();
        builder.Append(value.AsSpan(0, max));
        builder.AppendLine();
        builder.Append($"... truncated {value.Length - max} characters");
        return builder.ToString();
    }

    private static bool CompletedActionTool(IEnumerable<ToolExecutionResult> results) =>
        results.Any(result => result.Success && IsActionTool(result.Tool));

    private static bool IsActionTool(string tool) =>
        tool.Equals("execute_csharp", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("run_command", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("run_python", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("write_file", StringComparison.OrdinalIgnoreCase);

    private AgentTurnResult BuildResult(
        bool success,
        AgentProviderResult? providerResult,
        StringBuilder visibleTranscript,
        int toolCallCount,
        int toolResultCount,
        bool stoppedAfterToolLimit,
        string? error) =>
        new(
            success,
            _provider.DisplayName,
            providerResult?.Model,
            providerResult?.SessionId,
            providerResult?.Usage,
            providerResult?.ExitCode,
            providerResult?.StandardError ?? "",
            visibleTranscript.ToString().Trim(),
            toolCallCount,
            toolResultCount,
            stoppedAfterToolLimit,
            error);
}
