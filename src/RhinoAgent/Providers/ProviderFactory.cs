using Rhino;

namespace RhinoAgent.Providers;

public sealed class ProviderFactory
{
    private readonly AgentConfig _config;
    private readonly CommandResolver _resolver;
    private readonly AuthService _auth;
    private readonly RhinoDoc _doc;

    public ProviderFactory(AgentConfig config, CommandResolver resolver, AuthService auth, RhinoDoc doc)
    {
        _config = config;
        _resolver = resolver;
        _auth = auth;
        _doc = doc;
    }

    public IAgentProvider? ResolveInteractiveProvider(AgentConfig config)
    {
        AgentProviderKind[] choices = config.Provider switch
        {
            AgentProviderKind.Claude => [AgentProviderKind.Claude],
            AgentProviderKind.Codex => [AgentProviderKind.Codex],
            _ => [AgentProviderKind.Codex, AgentProviderKind.Claude]
        };

        foreach (var provider in choices)
        {
            var status = _auth.GetStatus(provider, GetConfiguredPath(provider));
            if (status is { ExecutableFound: true, LoggedIn: true })
                return Create(provider, status.ExecutablePath!);
        }

        return null;
    }

    public IEnumerable<ProviderStatus> GetProviderStatuses()
    {
        yield return _auth.GetStatus(AgentProviderKind.Claude, _config.ClaudePath);
        yield return _auth.GetStatus(AgentProviderKind.Codex, _config.CodexPath);
    }

    public TerminalCommand? BuildClaudeLoginCommand()
    {
        var executable = _resolver.Resolve("claude", _config.ClaudePath);
        return executable is null
            ? null
            : new TerminalCommand(executable, ["auth", "login"], "claude auth login");
    }

    public TerminalCommand? BuildCodexLoginCommand()
    {
        var executable = _resolver.Resolve("codex", _config.CodexPath);
        return executable is null
            ? null
            : new TerminalCommand(executable, ["login", "--device-auth"], "codex login --device-auth");
    }

    private IAgentProvider Create(AgentProviderKind provider, string executablePath)
    {
        var cwd = WorkingDirectoryResolver.Resolve(_doc, _config.WorkingDirectory);
        return provider switch
        {
            AgentProviderKind.Codex when _config.ProviderProcessMode == AgentProviderProcessMode.LongRunning =>
                new CodexAppServerProvider(executablePath, _config.CodexModel, _config.CodexReasoningEffort, _config.PermissionMode, cwd),
            AgentProviderKind.Codex => new CodexCliProvider(executablePath, _config.CodexModel, _config.PermissionMode, cwd),
            _ => new ClaudeCliProvider(executablePath, _config.ClaudeModel, _config.PermissionMode, cwd)
        };
    }

    private string? GetConfiguredPath(AgentProviderKind provider) =>
        provider == AgentProviderKind.Codex ? _config.CodexPath : _config.ClaudePath;
}
