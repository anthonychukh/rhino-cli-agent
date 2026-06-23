using System.Text;
using Rhino;
using RhinoAgent.Providers;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public sealed class AgentSession
{
    private readonly RhinoDoc _doc;
    private readonly AgentConfig _config;
    private readonly IAgentProvider _provider;
    private readonly RhinoToolHost _toolHost;
    private readonly ApprovalService _approvals;
    private readonly List<(string Role, string Text)> _history = [];

    public AgentSession(
        RhinoDoc doc,
        AgentConfig config,
        IAgentProvider provider,
        RhinoToolHost toolHost,
        ApprovalService approvals)
    {
        _doc = doc;
        _config = config;
        _provider = provider;
        _toolHost = toolHost;
        _approvals = approvals;
    }

    public void Clear()
    {
        _history.Clear();
        _provider.Reset();
    }

    public async Task<AgentTurnResult> RunUserTurnAsync(
        string userMessage,
        CancellationToken cancellationToken = default,
        Action<string>? diagnostics = null)
    {
        diagnostics?.Invoke("turn-entered");
        _history.Add(("user", userMessage));
        var toolResults = new List<ToolExecutionResult>();
        var visibleTranscript = new StringBuilder();
        var totalToolCalls = 0;
        var totalToolResults = 0;
        AgentProviderResult? lastProviderResult = null;

        for (var round = 0; round < Math.Max(1, _config.MaxToolRounds); round++)
        {
            var prompt = AgentPromptBuilder.Build(_doc, _config, _history, toolResults, _toolHost.DescribeTools());
            diagnostics?.Invoke($"prompt-built: {prompt.Length} chars");
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
                        prompt,
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
            lastProviderResult = providerResult;

            if (providerResult.ExitCode != 0)
            {
                Debug($"Provider exited with code {providerResult.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(providerResult.StandardError))
                    Debug(providerResult.StandardError.Trim());
                return BuildResult(
                    false,
                    providerResult,
                    visibleTranscript,
                    totalToolCalls,
                    totalToolResults,
                    false,
                    $"Provider exited with code {providerResult.ExitCode}.");
            }

            var parsed = AgentResponseParser.Parse(providerResult.Text);
            var visible = parsed.VisibleText.Trim();
            if (visible.Length > 0)
            {
                CommandLineUi.AgentResponse(visible);
                visibleTranscript.AppendLine(visible);
            }

            PrintUsage(providerResult);
            _history.Add(("assistant", providerResult.Text));
            totalToolCalls += parsed.ToolCalls.Count;

            if (parsed.ToolCalls.Count == 0)
            {
                return BuildResult(
                    true,
                    providerResult,
                    visibleTranscript,
                    totalToolCalls,
                    totalToolResults,
                    false,
                    null);
            }

            toolResults.Clear();
            foreach (var call in parsed.ToolCalls)
            {
                if (!_approvals.ShouldExecute(call, _toolHost, out var reason))
                {
                    Debug($"Plan: {call.Tool} {FormatArguments(call.Arguments)}");
                    toolResults.Add(new ToolExecutionResult(call.Tool, false, reason, false, true));
                    continue;
                }

                if (_approvals.RequiresPrompt(call, _toolHost) && !_approvals.PromptForApproval(call))
                {
                    toolResults.Add(new ToolExecutionResult(call.Tool, false, "User denied tool call.", false, true));
                    continue;
                }

                Debug($"Running tool: {call.Tool}");
                var result = await _toolHost.ExecuteAsync(call);
                Debug(result.Success ? $"Tool complete: {call.Tool}" : $"Tool failed: {call.Tool}");
                if (!string.IsNullOrWhiteSpace(result.Output))
                    Debug(TrimForCommandLine(result.Output));
                toolResults.Add(result);
                totalToolResults++;
            }

            _history.Add(("tool", ToolResultFormatter.Format(toolResults)));
        }

        Debug("Agent stopped after the configured tool-round limit. Raise maxToolRounds in config.json if needed.");
        return BuildResult(
            false,
            lastProviderResult,
            visibleTranscript,
            totalToolCalls,
            totalToolResults,
            true,
            "Agent stopped after the configured tool-round limit.");
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

    private void Debug(string message)
    {
        if (_config.ShowDebugMessages)
            CommandLineUi.Debug(message);
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
