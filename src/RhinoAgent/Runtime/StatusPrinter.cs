using Rhino;
using RhinoAgent.Config;

namespace RhinoAgent.Runtime;

public static class StatusPrinter
{
    public static void Print(AgentConfig config, AgentServices services)
    {
        RhinoApp.WriteLine("RhinoAgent status");
        RhinoApp.WriteLine($"  Config: {AgentConfigStore.ConfigPath}");
        RhinoApp.WriteLine($"  Provider preference: {config.Provider}");
        RhinoApp.WriteLine($"  Permission mode: {config.PermissionMode}");
        RhinoApp.WriteLine($"  Claude model: {config.ClaudeModel}");
        RhinoApp.WriteLine($"  Codex model: {config.CodexModel}");

        foreach (var status in services.ProviderFactory.GetProviderStatuses())
        {
            var login = status.LoggedIn ? "logged in" : "not logged in";
            var exe = status.ExecutableFound ? status.ExecutablePath : "not found";
            RhinoApp.WriteLine($"  {status.Provider}: {login}; executable: {exe}");
            if (!string.IsNullOrWhiteSpace(status.Account))
                RhinoApp.WriteLine($"    account: {status.Account}");
            if (!string.IsNullOrWhiteSpace(status.Detail))
                RhinoApp.WriteLine($"    detail: {status.Detail}");
        }
    }
}
