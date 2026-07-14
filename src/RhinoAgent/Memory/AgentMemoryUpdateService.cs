using System.Text.Json;
using Rhino;
using RhinoAgent.Providers;
using RhinoAgent.Runtime;
using RhinoAgent.Tools;

namespace RhinoAgent.Memory;

public sealed class AgentMemoryUpdateService
{
    private readonly RhinoDoc _doc;
    private readonly AgentConfig _config;
    private readonly Func<IAgentProvider?> _resolveProvider;

    public AgentMemoryUpdateService(RhinoDoc doc, AgentConfig config, Func<IAgentProvider?> resolveProvider)
    {
        _doc = doc;
        _config = config;
        _resolveProvider = resolveProvider;
    }

    public async Task<AgentMemoryMaintenanceResult> UpdateAfterTurnAsync(
        string userMessage,
        AgentTurnResult result,
        CancellationToken cancellationToken = default)
    {
        if (!_config.EnableDocumentMemory)
            return new AgentMemoryMaintenanceResult(false, "Document memory is disabled in config.", "");
        if (!result.Success)
            return new AgentMemoryMaintenanceResult(false, "Turn did not succeed.", "");
        if (!LooksPotentiallyDurable(userMessage, result))
            return new AgentMemoryMaintenanceResult(false, "Turn did not look durable enough for memory.", "");

        var prompt = await RhinoUiDispatcher.InvokeAsync(
            () => BuildTurnMaintenancePrompt(userMessage, result)).ConfigureAwait(false);
        return await RunMaintenanceAsync(
            "automatic update after meaningful turn",
            prompt,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentMemoryMaintenanceResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.EnableDocumentMemory)
            return new AgentMemoryMaintenanceResult(false, "Document memory is disabled in config.", "");

        var prompt = await RhinoUiDispatcher.InvokeAsync(BuildRefreshPrompt).ConfigureAwait(false);
        return await RunMaintenanceAsync(
            "manual /memory refresh",
            prompt,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentMemoryMaintenanceResult> RunMaintenanceAsync(
        string reason,
        string prompt,
        CancellationToken cancellationToken)
    {
        var state = await RhinoUiDispatcher.InvokeAsync(
            () => AgentMemoryStore.EnsureCreated(_doc)).ConfigureAwait(false);
        if (!state.Enabled)
            return new AgentMemoryMaintenanceResult(false, "RhinoAgent memory is off for this document.", "");

        using var provider = _resolveProvider();
        if (provider is null)
        {
            var save = await RhinoUiDispatcher.InvokeAsync(
                () => AgentMemoryStore.SaveUserMarkdown(
                    _doc,
                    state.Markdown,
                    "Refreshed memory summary without a maintenance provider.")).ConfigureAwait(false);
            return new AgentMemoryMaintenanceResult(save.Changed, "No maintenance provider was available; refreshed deterministic summary.", save.State.LastUpdateReason);
        }

        var providerResult = await provider.RunPromptAsync(
            prompt,
            progress =>
            {
                if (!progress.IsTransient && _config.ShowDebugMessages)
                    CommandLineUi.Debug(progress.Message);
            },
            cancellationToken).ConfigureAwait(false);

        if (providerResult.ExitCode != 0)
            return new AgentMemoryMaintenanceResult(false, $"Memory update provider failed: {providerResult.StandardError}", "");

        if (!TryParseMaintenanceResponse(providerResult.Text, out var response, out var error))
            return new AgentMemoryMaintenanceResult(false, $"Memory update response was not valid JSON: {error}", "");

        if (!response.Update)
            return new AgentMemoryMaintenanceResult(false, FirstNonEmpty(response.Reason, "No durable memory changes."), response.Reason);

        var saveResult = await RhinoUiDispatcher.InvokeAsync(
            () => AgentMemoryStore.ApplyGeneratedUpdate(
                _doc,
                response.AgentNotes,
                response.Summary,
                FirstNonEmpty(response.Reason, reason))).ConfigureAwait(false);
        return new AgentMemoryMaintenanceResult(saveResult.Changed, saveResult.Message, saveResult.State.LastUpdateReason);
    }

    internal static bool TryParseMaintenanceResponse(string text, out AgentMemoryMaintenanceResponse response, out string error)
    {
        response = new AgentMemoryMaintenanceResponse();
        error = "";
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "No JSON object found.";
            return false;
        }

        try
        {
            response = JsonSerializer.Deserialize<AgentMemoryMaintenanceResponse>(json, JsonOptions.Loose)
                ?? new AgentMemoryMaintenanceResponse();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string BuildTurnMaintenancePrompt(string userMessage, AgentTurnResult result)
    {
        var state = AgentMemoryStore.EnsureCreated(_doc);
        return $$"""
        You are RhinoAgent's private per-file memory updater.
        Update only durable project context for the active Rhino document. Do not call tools. Do not include markdown fences.

        You may rewrite only the generated Agent Notes section. Preserve all user-authored sections by returning just replacement content for Agent Notes.
        Durable context includes user goals, modeling conventions, constraints, decisions, references, current tasks, warnings, and important completed work.
        Do not store live object counts, bounding boxes, transient command output, or generic chat.

        Return exactly one JSON object with this shape:
        {"update":true|false,"agentNotes":"markdown bullet notes for the generated section","summary":"compact prompt summary under 2400 chars","reason":"short reason"}

        Current document summary:
        {{RhinoDocumentSummarizer.Summarize(_doc).Trim()}}

        Current embedded memory:
        {{state.Markdown.Trim()}}

        Latest user message:
        {{userMessage.Trim()}}

        Latest agent visible response:
        {{result.VisibleText.Trim()}}

        Latest turn metadata:
        provider={{result.Provider}}
        tools_called={{result.ToolCallCount}}
        tools_completed={{result.ToolResultCount}}
        stopped_after_tool_limit={{result.StoppedAfterToolLimit}}
        """;
    }

    private string BuildRefreshPrompt()
    {
        var state = AgentMemoryStore.EnsureCreated(_doc);
        return $$"""
        You are RhinoAgent's private per-file memory updater.
        Refresh the generated Agent Notes and compact summary for this Rhino document. Do not call tools. Do not include markdown fences.

        Preserve all user-authored memory sections. Return only replacement content for Agent Notes.
        Return exactly one JSON object:
        {"update":true|false,"agentNotes":"markdown bullet notes for the generated section","summary":"compact prompt summary under 2400 chars","reason":"short reason"}

        Current document summary:
        {{RhinoDocumentSummarizer.Summarize(_doc).Trim()}}

        Current embedded memory:
        {{state.Markdown.Trim()}}
        """;
    }

    private static bool LooksPotentiallyDurable(string userMessage, AgentTurnResult result)
    {
        if (result.ToolResultCount > 0)
            return true;

        var text = userMessage.ToLowerInvariant();
        string[] durableWords =
        [
            "remember", "memory", "context", "constraint", "decision", "task", "todo",
            "prefer", "goal", "intent", "unit", "layer", "material", "reference",
            "deadline", "warning", "important", "project"
        ];
        return durableWords.Any(text.Contains);
    }

    private static string ExtractJsonObject(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                text = text[(firstLine + 1)..lastFence].Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : "";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

public sealed class AgentMemoryMaintenanceResponse
{
    public bool Update { get; set; }
    public string AgentNotes { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Reason { get; set; } = "";
}
