namespace RhinoAgent.Providers;

public interface IAgentProvider
{
    AgentProviderKind Kind { get; }
    string DisplayName { get; }
    Task<AgentProviderResult> RunPromptAsync(
        string prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken);
}
