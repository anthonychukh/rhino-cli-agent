using System.Text;
using Rhino;
using RhinoAgent.Memory;
using RhinoAgent.Providers;
using RhinoAgent.Skills;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class AgentSession
{
    private readonly RhinoDoc _doc;
    private readonly AgentConfig _config;
    private readonly IAgentProvider _provider;
    private readonly RhinoToolHost _toolHost;
    private readonly ApprovalService _approvals;
    private readonly SkillStore _skillStore;
    private readonly AgentMemoryUpdateService? _memoryUpdater;
    private readonly AgentConversationIndex _conversationIndex = new();
    private readonly List<(string Role, string Text)> _history = [];
    private readonly List<string> _queuedSkillNames = [];

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
        return provider.TryContinueLatestConversation(out message);
    }

    public bool TryResumeProviderConversation(string sessionId, out string message)
    {
        if (!TryGetConversationResumeProvider(out var provider, out message))
            return false;

        _history.Clear();
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

    public int PendingConversationIndexTurnCount => _conversationIndex.PendingTurnCount;

    public Task<AgentTurnResult> RunUserTurnAsync(
        string userMessage,
        CancellationToken cancellationToken = default,
        Action<string>? diagnostics = null,
        IReadOnlyList<string>? forcedSkillNames = null)
    {
        return RunUserTurnAsync(
            new AgentUserMessage(userMessage, Array.Empty<AgentImageAttachment>()),
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
        AgentProviderResult? lastProviderResult = null;

        var maxRounds = Math.Max(1, _config.MaxToolRounds);
        for (var round = 0; round < maxRounds; round++)
        {
            var prompt = await RhinoUiDispatcher.InvokeAsync(
                () => AgentPromptBuilder.Build(
                    _doc,
                    _config,
                    _history,
                    toolResults,
                    _toolHost.DescribeTools(),
                    selectedSkills));
            diagnostics?.Invoke($"prompt-built: {prompt.Length} chars");
            WritePromptPackageToDebugger(prompt, round, maxRounds);
            diagnostics?.Invoke("thinking-write-start");
            var lastProgressMessage = "";
            AgentProviderResult providerResult;
            using (CommandLineUi.Thinking(
                round == 0 ? "Agent is thinking" : "Agent is checking tool results",
                _config.ShowDebugMessages))
            {
                diagnostics?.Invoke("thinking-write-complete");
                diagnostics?.Invoke("provider-run-start");
                providerResult = _provider.RunPromptAsync(
                        new AgentProviderPrompt(
                            prompt,
                            round == 0 ? userMessage.Images : Array.Empty<AgentImageAttachment>()),
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
                await CompleteTurnAndIndexMemoryAsync(userMessage.Text, result, cancellationToken);
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
                diagnostics?.Invoke("turn-complete-no-tools");
                var result = BuildResult(
                    true,
                    providerResult,
                    visibleTranscript,
                    totalToolCalls,
                    totalToolResults,
                    false,
                    null);
                await CompleteTurnAndIndexMemoryAsync(userMessage.Text, result, cancellationToken);
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
                await CompleteTurnAndIndexMemoryAsync(userMessage.Text, result, cancellationToken);
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
        await CompleteTurnAndIndexMemoryAsync(userMessage.Text, stoppedResult, cancellationToken);
        return stoppedResult;
    }

    public async Task<AgentMemoryMaintenanceResult> IndexConversationAsync(
        CancellationToken cancellationToken = default)
    {
        if (_memoryUpdater is null)
            return new AgentMemoryMaintenanceResult(false, "No memory updater is configured for this session.", "");

        if (_conversationIndex.PendingTurnCount == 0)
            return new AgentMemoryMaintenanceResult(false, "No unindexed conversation turns.", "");

        var anyUpdated = false;
        var lastMessage = "Conversation index is up to date.";
        var lastReason = "";
        while (_conversationIndex.PendingTurnCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = _conversationIndex.GetNextBatch();
            if (batch.Count == 0)
                break;

            var update = await _memoryUpdater.IndexConversationBatchAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            lastMessage = update.Message;
            lastReason = update.Reason;
            anyUpdated |= update.Updated;
            if (!update.Completed)
                return new AgentMemoryMaintenanceResult(anyUpdated, lastMessage, lastReason, false);

            _conversationIndex.MarkIndexed(batch);
        }

        return new AgentMemoryMaintenanceResult(anyUpdated, lastMessage, lastReason);
    }

    private async Task CompleteTurnAndIndexMemoryAsync(
        string userMessage,
        AgentTurnResult result,
        CancellationToken cancellationToken)
    {
        if (_memoryUpdater is null || !_config.EnableDocumentMemory || !result.Success)
            return;

        var droppedBeforeAdd = _conversationIndex.DroppedTurnCount;
        if (!_conversationIndex.TryAdd(userMessage, result, out _))
            return;
        if (_conversationIndex.DroppedTurnCount > droppedBeforeAdd)
            Debug("Dropped the oldest unindexed conversation turn because the bounded memory queue was full.");

        if (!_conversationIndex.ShouldFlushAutomatically && !ShouldIndexImmediately(userMessage, result))
        {
            Debug($"Queued conversation turn for memory indexing ({_conversationIndex.PendingTurnCount}/{AgentConversationIndex.AutomaticFlushTurnCount}).");
            return;
        }

        try
        {
            var update = await IndexConversationAsync(cancellationToken).ConfigureAwait(false);
            if (update.Updated)
                CommandLineUi.Debug($"Conversation indexed into memory: {update.Reason}. Use /memory undo to revert.");
            else if (!update.Completed)
                CommandLineUi.Debug($"Conversation indexing deferred: {update.Message}");
            else if (_config.ShowDebugMessages && !string.IsNullOrWhiteSpace(update.Message))
                CommandLineUi.Debug($"Conversation indexed; memory unchanged: {update.Message}");
        }
        catch (OperationCanceledException)
        {
            CommandLineUi.Debug("Conversation indexing deferred: canceled.");
        }
        catch (Exception ex)
        {
            CommandLineUi.Debug($"Conversation indexing deferred: {ex.Message}");
        }
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
