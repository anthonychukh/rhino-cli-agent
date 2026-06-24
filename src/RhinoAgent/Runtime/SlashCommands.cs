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
                CommandLineUi.Debug("Conversation cleared for this Rhino Agent session.");
                return SlashCommandResult.Handled;
            case "/status":
                StatusPrinter.Print(config, services);
                return SlashCommandResult.Handled;
            case "/debug":
                SetDebugMessages(config, arg);
                return SlashCommandResult.Handled;
            case "/login":
                if (arg.Length > 0 && !LoginFlow.TryParseProvider(arg, out _))
                {
                    CommandLineUi.Debug("Usage: /login claude|codex");
                    return SlashCommandResult.Handled;
                }

                LoginFlow.Run(config, services, LoginFlow.TryParseProvider(arg, out var provider) ? provider : null);
                return SlashCommandResult.Handled;
            case "/model":
                SetModel(config, arg);
                return SlashCommandResult.Handled;
            case "/effort":
            case "/reasoning":
                SetReasoningEffort(config, arg);
                return SlashCommandResult.Handled;
            case "/provider":
                SetProvider(config, arg);
                return SlashCommandResult.Handled;
            case "/process":
            case "/processmode":
            case "/providerprocess":
                SetProviderProcessMode(config, arg);
                return SlashCommandResult.Handled;
            case "/permissions":
            case "/permission":
            case "/mode":
                SetPermission(config, arg);
                return SlashCommandResult.Handled;
            case "/run":
                if (arg.Length == 0)
                    CommandLineUi.Debug("Usage: /run _RhinoCommand arguments");
                else
                    RhinoApp.RunScript(arg, true);
                return SlashCommandResult.Handled;
            case "/tokens":
            case "/usage":
                SetUsageMessages(config, arg);
                return SlashCommandResult.Handled;
            case "/timeout":
                SetProviderTimeout(config, arg);
                return SlashCommandResult.Handled;
            case "/compact":
                CommandLineUi.Debug("Compaction is not needed yet. This V0 keeps only the recent in-memory turns and does not persist sessions.");
                return SlashCommandResult.Handled;
            case "/config":
                PrintConfig(config);
                return SlashCommandResult.Handled;
            default:
                CommandLineUi.Debug($"Unknown slash command: {command}. Type /help.");
                return SlashCommandResult.Handled;
        }
    }

    private static void PrintHelp()
    {
        CommandLineUi.Debug(string.Join(Environment.NewLine,
        [
            "RhinoAgent slash commands:",
            "  /help                     Show this help",
            "  /status                   Show provider/auth/config status",
            "  /login [claude|codex]     Start provider login in a terminal",
            "  /provider [auto|claude|codex]",
            "  /process [long|stateless]",
            "  /model [model]            Set active provider model",
            "  /effort low|medium|high|off",
            "  /mode ask|auto|full|plan",
            "  /debug on|off            Show or hide debug progress/tool messages",
            "  /timeout <seconds>|off   Limit a provider turn so Agent does not wait forever",
            "  /run <command>            Run a Rhino command manually while in Agent",
            "  ! <command>               Pass a command or alias directly to Rhino",
            "  _Command / -Command       Native Rhino command passthrough",
            "  Line / user aliases       Direct command and alias passthrough",
            "  /ask <prompt>             Force chat when text starts like a command",
            "  /clear                    Clear this in-memory conversation",
            "  /usage [on|off]           Show, hide, or explain exact usage reporting",
            "  /exit                     Leave Agent"
        ]));
    }

    private static void PrintConfig(AgentConfig config)
    {
        CommandLineUi.Debug(string.Join(Environment.NewLine,
        [
            $"Config path: {AgentConfigStore.ConfigPath}",
            $"Provider: {config.Provider}",
            $"Provider process: {config.ProviderProcessMode}",
            $"PermissionMode: {config.PermissionMode}",
            $"Provider timeout: {FormatTimeout(config.ProviderTurnTimeoutSeconds)}",
            $"Debug messages: {(config.ShowDebugMessages ? "on" : "off")}",
            $"Usage messages: {(config.ShowUsageMessages ? "on" : "off")}",
            $"Claude model: {config.ClaudeModel}",
            $"Codex model: {config.CodexModel}",
            $"Codex effort: {FormatReasoningEffort(config.CodexReasoningEffort)}",
            $"Working directory: {config.WorkingDirectory ?? "(document folder or home)"}"
        ]));
    }

    private static void SetProvider(AgentConfig config, string arg)
    {
        if (!Enum.TryParse<AgentProviderKind>(NormalizeProvider(arg), true, out var provider))
        {
            CommandLineUi.Debug("Usage: /provider auto|claude|codex");
            return;
        }

        config.Provider = provider;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Provider set to {config.Provider}. Restart Agent to switch provider.");
    }

    private static void SetProviderProcessMode(AgentConfig config, string arg)
    {
        var normalized = arg.Trim().ToLowerInvariant() switch
        {
            "long" or "longrunning" or "persistent" or "appserver" => nameof(AgentProviderProcessMode.LongRunning),
            "stateless" or "perturn" or "exec" => nameof(AgentProviderProcessMode.Stateless),
            _ => ""
        };

        if (!Enum.TryParse<AgentProviderProcessMode>(normalized, out var mode))
        {
            CommandLineUi.Debug("Usage: /process long|stateless");
            return;
        }

        config.ProviderProcessMode = mode;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Provider process mode set to {mode}. Restart Agent to switch provider process architecture.");
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
            CommandLineUi.Debug("Usage: /mode ask|auto|full|plan");
            return;
        }

        config.PermissionMode = mode;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Permission mode set to {mode}. Restart Agent to switch provider sandbox/permission arguments.");
    }

    private static void SetDebugMessages(AgentConfig config, string arg)
    {
        if (!TryParseOnOff(arg, out var enabled))
        {
            CommandLineUi.Debug($"Debug messages are {(config.ShowDebugMessages ? "on" : "off")}. Usage: /debug on|off");
            return;
        }

        config.ShowDebugMessages = enabled;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Debug messages {(enabled ? "on" : "off")}.");
    }

    private static void SetUsageMessages(AgentConfig config, string arg)
    {
        if (!TryParseOnOff(arg, out var enabled))
        {
            CommandLineUi.Debug(
                $"Usage messages are {(config.ShowUsageMessages ? "on" : "off")}. " +
                "Exact token and cost usage are shown after provider turns only when the provider emits exact values. " +
                "Usage: /usage on|off");
            return;
        }

        config.ShowUsageMessages = enabled;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Usage messages {(enabled ? "on" : "off")}.");
    }

    private static void SetModel(AgentConfig config, string arg)
    {
        if (arg.Length == 0)
        {
            CommandLineUi.Debug(
                $"Claude model: {config.ClaudeModel}{Environment.NewLine}" +
                $"Codex model: {config.CodexModel}");
            return;
        }

        if (config.Provider == AgentProviderKind.Codex)
            config.CodexModel = arg;
        else
            config.ClaudeModel = arg;

        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Model saved: {arg}. Restart Agent to ensure the provider process uses it.");
    }

    private static void SetProviderTimeout(AgentConfig config, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            CommandLineUi.Debug($"Provider timeout: {FormatTimeout(config.ProviderTurnTimeoutSeconds)}. Usage: /timeout <seconds>|off");
            return;
        }

        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("disable", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || arg.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            config.ProviderTurnTimeoutSeconds = 0;
            AgentConfigStore.Save(config);
            CommandLineUi.Debug("Provider timeout disabled.");
            return;
        }

        if (!int.TryParse(arg, out var seconds) || seconds < 15)
        {
            CommandLineUi.Debug("Usage: /timeout <seconds>|off. Minimum timeout is 15 seconds.");
            return;
        }

        config.ProviderTurnTimeoutSeconds = seconds;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug($"Provider timeout set to {seconds} seconds.");
    }

    private static void SetReasoningEffort(AgentConfig config, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            CommandLineUi.Debug($"Codex reasoning effort: {FormatReasoningEffort(config.CodexReasoningEffort)}. Usage: /effort low|medium|high|off");
            return;
        }

        var normalized = NormalizeReasoningEffort(arg);
        if (normalized is null)
        {
            CommandLineUi.Debug("Usage: /effort low|medium|high|off");
            return;
        }

        config.CodexReasoningEffort = normalized;
        AgentConfigStore.Save(config);
        CommandLineUi.Debug(
            $"Codex reasoning effort set to {FormatReasoningEffort(normalized)}. " +
            "Restart Agent to ensure the active Codex provider process uses it.");
    }

    private static string NormalizeProvider(string arg) => arg.Trim().ToLowerInvariant() switch
    {
        "" => "",
        "openai" => nameof(AgentProviderKind.Codex),
        _ => arg
    };

    private static bool TryParseOnOff(string arg, out bool enabled)
    {
        enabled = false;
        switch (arg.Trim().ToLowerInvariant())
        {
            case "on":
            case "true":
            case "yes":
            case "enable":
            case "enabled":
                enabled = true;
                return true;
            case "off":
            case "false":
            case "no":
            case "disable":
            case "disabled":
                return true;
            default:
                return false;
        }
    }

    private static string FormatTimeout(int seconds) =>
        seconds > 0 ? $"{seconds} seconds" : "off";

    private static string? NormalizeReasoningEffort(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "low" or "minimal" or "min" => "low",
            "medium" or "med" => "medium",
            "high" or "max" => "high",
            "off" or "default" or "none" => "",
            _ => null
        };
    }

    private static string FormatReasoningEffort(string value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value;
}
