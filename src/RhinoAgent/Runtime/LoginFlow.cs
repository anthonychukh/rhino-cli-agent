using Rhino;
using Rhino.Input.Custom;
using RhinoAgent.Config;
using RhinoAgent.Providers;

namespace RhinoAgent.Runtime;

public static class LoginFlow
{
    public static void Run(AgentConfig config, AgentServices services, AgentProviderKind? requestedProvider = null)
    {
        var provider = requestedProvider ?? ChooseProvider();
        if (provider == AgentProviderKind.Auto)
            return;

        config.Provider = provider;
        AgentConfigStore.Save(config);

        var command = provider == AgentProviderKind.Claude
            ? services.ProviderFactory.BuildClaudeLoginCommand()
            : services.ProviderFactory.BuildCodexLoginCommand();

        if (command is null)
        {
            RhinoApp.WriteLine($"{provider} CLI was not found on PATH. Install it, or set the executable path in config.json.");
            return;
        }

        RhinoApp.WriteLine($"Starting {provider} login in a terminal:");
        RhinoApp.WriteLine(command.DisplayCommand);
        if (TerminalLauncher.Launch(command))
            RhinoApp.WriteLine("Finish login in the terminal/browser, then run Agent again.");
        else
            RhinoApp.WriteLine("Could not launch a terminal. Run the command above manually.");
    }

    public static bool TryParseProvider(string value, out AgentProviderKind provider)
    {
        provider = value.Trim().ToLowerInvariant() switch
        {
            "claude" => AgentProviderKind.Claude,
            "codex" or "openai" => AgentProviderKind.Codex,
            _ => AgentProviderKind.Auto
        };

        return provider != AgentProviderKind.Auto;
    }

    private static AgentProviderKind ChooseProvider()
    {
        var getter = new GetOption();
        getter.SetCommandPrompt("Choose provider login");
        getter.AddOption("Claude");
        getter.AddOption("Codex");
        getter.AddOption("Cancel");
        getter.AcceptNothing(true);

        var result = getter.Get();
        if (result != Rhino.Input.GetResult.Option)
            return AgentProviderKind.Auto;

        return getter.Option()?.EnglishName switch
        {
            "Claude" => AgentProviderKind.Claude,
            "Codex" => AgentProviderKind.Codex,
            _ => AgentProviderKind.Auto
        };
    }
}
