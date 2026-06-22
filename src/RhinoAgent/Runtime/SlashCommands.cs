using Rhino;
using RhinoAgent.Config;

namespace RhinoAgent.Runtime;

public enum SlashCommandResult
{
    NotHandled,
    Handled,
    Exit
}

public static class SlashCommands
{
    public static SlashCommandResult TryHandle(
        string input,
        AgentConfig config,
        AgentSession session,
        AgentServices services)
    {
        if (!input.StartsWith("/", StringComparison.Ordinal))
            return SlashCommandResult.NotHandled;

        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        switch (command)
        {
            case "/exit":
            case "/quit":
                return SlashCommandResult.Exit;
            case "/help":
                PrintHelp();
                return SlashCommandResult.Handled;
            case "/clear":
                session.Clear();
                RhinoApp.WriteLine("Conversation cleared for this Rhino Agent session.");
                return SlashCommandResult.Handled;
            case "/status":
                StatusPrinter.Print(config, services);
                return SlashCommandResult.Handled;
            case "/login":
                if (arg.Length > 0 && !LoginFlow.TryParseProvider(arg, out _))
                {
                    RhinoApp.WriteLine("Usage: /login claude|codex");
                    return SlashCommandResult.Handled;
                }

                LoginFlow.Run(config, services, LoginFlow.TryParseProvider(arg, out var provider) ? provider : null);
                return SlashCommandResult.Handled;
            case "/model":
                SetModel(config, arg);
                return SlashCommandResult.Handled;
            case "/provider":
                SetProvider(config, arg);
                return SlashCommandResult.Handled;
            case "/permissions":
            case "/permission":
            case "/mode":
                SetPermission(config, arg);
                return SlashCommandResult.Handled;
            case "/run":
                if (arg.Length == 0)
                    RhinoApp.WriteLine("Usage: /run _RhinoCommand arguments");
                else
                    RhinoApp.RunScript(arg, true);
                return SlashCommandResult.Handled;
            case "/tokens":
            case "/usage":
                RhinoApp.WriteLine("Token usage is shown after provider turns when the provider emits exact usage.");
                return SlashCommandResult.Handled;
            case "/compact":
                RhinoApp.WriteLine("Compaction is not needed yet. This V0 keeps only the recent in-memory turns and does not persist sessions.");
                return SlashCommandResult.Handled;
            case "/config":
                PrintConfig(config);
                return SlashCommandResult.Handled;
            default:
                RhinoApp.WriteLine($"Unknown slash command: {command}. Type /help.");
                return SlashCommandResult.Handled;
        }
    }

    private static void PrintHelp()
    {
        RhinoApp.WriteLine("RhinoAgent slash commands:");
        RhinoApp.WriteLine("  /help                     Show this help");
        RhinoApp.WriteLine("  /status                   Show provider/auth/config status");
        RhinoApp.WriteLine("  /login [claude|codex]     Start provider login in a terminal");
        RhinoApp.WriteLine("  /provider [auto|claude|codex]");
        RhinoApp.WriteLine("  /model [model]            Set active provider model");
        RhinoApp.WriteLine("  /mode [ask|auto|full|plan]");
        RhinoApp.WriteLine("  /run <command>            Run a Rhino command manually while in Agent");
        RhinoApp.WriteLine("  ! <command>               Pass a command or alias directly to Rhino");
        RhinoApp.WriteLine("  _Command / -Command       Native Rhino command passthrough");
        RhinoApp.WriteLine("  Line / user aliases       Direct command and alias passthrough");
        RhinoApp.WriteLine("  /ask <prompt>             Force chat when text starts like a command");
        RhinoApp.WriteLine("  /clear                    Clear this in-memory conversation");
        RhinoApp.WriteLine("  /usage                    Explain exact usage reporting");
        RhinoApp.WriteLine("  /exit                     Leave Agent");
    }

    private static void PrintConfig(AgentConfig config)
    {
        RhinoApp.WriteLine($"Config path: {AgentConfigStore.ConfigPath}");
        RhinoApp.WriteLine($"Provider: {config.Provider}");
        RhinoApp.WriteLine($"PermissionMode: {config.PermissionMode}");
        RhinoApp.WriteLine($"Claude model: {config.ClaudeModel}");
        RhinoApp.WriteLine($"Codex model: {config.CodexModel}");
        RhinoApp.WriteLine($"Working directory: {config.WorkingDirectory ?? "(document folder or home)"}");
    }

    private static void SetProvider(AgentConfig config, string arg)
    {
        if (!Enum.TryParse<AgentProviderKind>(NormalizeProvider(arg), true, out var provider))
        {
            RhinoApp.WriteLine("Usage: /provider auto|claude|codex");
            return;
        }

        config.Provider = provider;
        AgentConfigStore.Save(config);
        RhinoApp.WriteLine($"Provider set to {config.Provider}. Restart Agent to switch provider.");
    }

    private static void SetPermission(AgentConfig config, string arg)
    {
        var normalized = arg.Trim().ToLowerInvariant() switch
        {
            "ask" or "default" => nameof(AgentPermissionMode.Ask),
            "auto" => nameof(AgentPermissionMode.Auto),
            "full" or "fullaccess" or "bypass" or "yolo" => nameof(AgentPermissionMode.FullAccess),
            "plan" => nameof(AgentPermissionMode.Plan),
            _ => ""
        };

        if (!Enum.TryParse<AgentPermissionMode>(normalized, out var mode))
        {
            RhinoApp.WriteLine("Usage: /mode ask|auto|full|plan");
            return;
        }

        config.PermissionMode = mode;
        AgentConfigStore.Save(config);
        RhinoApp.WriteLine($"Permission mode set to {mode}.");
    }

    private static void SetModel(AgentConfig config, string arg)
    {
        if (arg.Length == 0)
        {
            RhinoApp.WriteLine($"Claude model: {config.ClaudeModel}");
            RhinoApp.WriteLine($"Codex model: {config.CodexModel}");
            return;
        }

        if (config.Provider == AgentProviderKind.Codex)
            config.CodexModel = arg;
        else
            config.ClaudeModel = arg;

        AgentConfigStore.Save(config);
        RhinoApp.WriteLine($"Model saved: {arg}. Restart Agent to ensure the provider process uses it.");
    }

    private static string NormalizeProvider(string arg) => arg.Trim().ToLowerInvariant() switch
    {
        "" => "",
        "openai" => nameof(AgentProviderKind.Codex),
        _ => arg
    };
}
