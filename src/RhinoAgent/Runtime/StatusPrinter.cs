using RhinoAgent.Config;

namespace RhinoAgent.Runtime;

public static class StatusPrinter
{
    public static void Print(AgentConfig config, AgentServices services)
    {
        var lines = new List<string>
        {
            "RhinoAgent status",
            $"  Config: {AgentConfigStore.ConfigPath}",
            $"  Provider preference: {config.Provider}",
            $"  Provider process: {config.ProviderProcessMode}",
            $"  Permission mode: {config.PermissionMode}",
            $"  Debug messages: {(config.ShowDebugMessages ? "on" : "off")}",
            $"  Usage messages: {(config.ShowUsageMessages ? "on" : "off")}",
            $"  Claude model: {config.ClaudeModel}",
            $"  Codex model: {config.CodexModel}",
            $"  Codex effort: {FormatReasoningEffort(config.CodexReasoningEffort)}"
        };

        foreach (var status in services.ProviderFactory.GetProviderStatuses())
        {
            var login = status.LoggedIn ? "logged in" : "not logged in";
            var exe = status.ExecutableFound ? status.ExecutablePath : "not found";
            lines.Add($"  {status.Provider}: {login}; executable: {exe}");
            if (!string.IsNullOrWhiteSpace(status.Account))
                lines.Add($"    account: {status.Account}");
            if (!string.IsNullOrWhiteSpace(status.Detail))
                lines.Add($"    detail: {status.Detail}");
        }

        CommandLineUi.Debug(string.Join(Environment.NewLine, lines));
    }

    private static string FormatReasoningEffort(string value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value;
}
