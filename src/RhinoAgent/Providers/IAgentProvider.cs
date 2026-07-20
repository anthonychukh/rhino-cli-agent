namespace RhinoAgent.Providers;

public interface IAgentProvider : IDisposable
{
    AgentProviderKind Kind { get; }
    string DisplayName { get; }
    AgentProviderProcessMode ProcessMode { get; }
    Task<AgentProviderResult> RunPromptAsync(
        AgentProviderPrompt prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken);
    void Reset();
}

public interface IConversationResumeProvider
{
    string? ActiveSessionId { get; }
    bool TryContinueLatestConversation(out string message);
    bool TryResumeConversation(string sessionId, out string message);
}

public interface IModelCatalogProvider
{
    Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        Action<AgentProgress> progress,
        CancellationToken cancellationToken);
}
