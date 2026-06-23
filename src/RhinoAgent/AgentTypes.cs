using System.Text.Json.Serialization;

namespace RhinoAgent;

public enum AgentProviderKind
{
    Auto,
    Claude,
    Codex
}

public enum AgentPermissionMode
{
    Ask,
    Auto,
    FullAccess,
    Plan
}

public enum AgentProviderProcessMode
{
    LongRunning,
    Stateless
}

public sealed class AgentConfig
{
    public AgentProviderKind Provider { get; set; } = AgentProviderKind.Auto;
    public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.Ask;
    public AgentProviderProcessMode ProviderProcessMode { get; set; } = AgentProviderProcessMode.LongRunning;
    public string ClaudeModel { get; set; } = "claude-opus-4-8";
    public string CodexModel { get; set; } = "gpt-5.5";
    public string? ClaudePath { get; set; }
    public string? CodexPath { get; set; }
    public string? WorkingDirectory { get; set; }
    public int MaxToolRounds { get; set; } = 4;
}

public sealed record ProviderStatus(
    AgentProviderKind Provider,
    bool ExecutableFound,
    bool LoggedIn,
    string? ExecutablePath,
    string? Account,
    string? Detail);

public sealed record AgentProgress(string Message, bool IsTransient = false);

public sealed record TokenUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CacheCreationInputTokens,
    long? CacheReadInputTokens,
    long? ReasoningOutputTokens,
    decimal? CostUsd);

public sealed record AgentProviderResult(
    string Text,
    string? Model,
    string? SessionId,
    TokenUsage? Usage,
    int ExitCode,
    string StandardError);

public sealed class ToolCallEnvelope
{
    [JsonPropertyName("tool_calls")]
    public List<ToolCallRequest> ToolCalls { get; set; } = [];
}

public sealed class ToolCallRequest
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("arguments")]
    public Dictionary<string, object?> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ToolExecutionResult(
    string Tool,
    bool Success,
    string Output,
    bool WasApproved,
    bool WasSkipped);

public sealed record ParsedAgentText(string VisibleText, IReadOnlyList<ToolCallRequest> ToolCalls);

public sealed record AgentTurnResult(
    bool Success,
    string Provider,
    string? Model,
    string? SessionId,
    TokenUsage? Usage,
    int? ProviderExitCode,
    string StandardError,
    string VisibleText,
    int ToolCallCount,
    int ToolResultCount,
    bool StoppedAfterToolLimit,
    string? Error);
