namespace RhinoAgent.Providers;

public interface IAgentProvider : IDisposable
{
    AgentProviderKind Kind { get; }
    string DisplayName { get; }
    AgentProviderProcessMode ProcessMode { get; }
    Task<AgentProviderResult> RunPromptAsync(
        string prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken);
    void Reset();
}
