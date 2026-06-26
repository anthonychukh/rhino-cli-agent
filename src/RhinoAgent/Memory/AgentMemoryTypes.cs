namespace RhinoAgent.Memory;

public sealed record AgentMemoryState(
    bool Exists,
    bool Enabled,
    string Markdown,
    string PromptSummary,
    DateTimeOffset? LastUpdatedUtc,
    string LastUpdateReason,
    string CurrentHash,
    IReadOnlyList<AgentMemorySnapshot> History);

public sealed record AgentMemorySnapshot(
    DateTimeOffset TimestampUtc,
    string Markdown,
    string PromptSummary,
    string Reason,
    string Hash);

public sealed record AgentMemorySaveResult(bool Changed, string Message, AgentMemoryState State);

public sealed record AgentMemoryMaintenanceResult(bool Updated, string Message, string Reason);
