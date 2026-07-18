using Rhino;
using Rhino.Input.Custom;
using RhinoAgent.Config;
using RhinoAgent.Skills;

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
            case "/skill":
            case "/skills":
                HandleSkillCommand(arg, config, session, services);
                return SlashCommandResult.Handled;
            case "/clear":
                session.Clear();
                CommandLineUi.Debug("Conversation and saved provider resume state cleared for this working directory.");
                return SlashCommandResult.Handled;
            case "/status":
                {
                    StatusPrinter.Print(config, services);
                    var providerSessionStatus = session.GetProviderSessionStatus();
                    if (!string.IsNullOrWhiteSpace(providerSessionStatus))
                        CommandLineUi.Debug(providerSessionStatus);
                    return SlashCommandResult.Handled;
                }
            case "/continue":
            case "/resume":
                ContinueOrResumeProviderConversation(session, arg);
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
            case "/memory":
                AgentMemorySlashCommands.Handle(arg, config, services, session);
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
                CommandLineUi.Debug("Compaction is not needed yet. RhinoAgent keeps only recent local prompt history; provider-level sessions are resumed separately when supported.");
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
            "  /continue [latest|session-id]",
            "  /resume [latest|session-id]",
            "  /process [long|stateless]",
            "  /model [model]            Set active provider model",
            "  /effort low|medium|high|off",
            "  /mode ask|auto|full|plan",
            "  /debug on|off            Show or hide debug progress/tool messages",
            "  /timeout <seconds>|off   Limit a provider turn so Agent does not wait forever",
            "  /skill ...                List, use, create, export, or manage RhinoAgent skills",
            "  /run <command>            Run a Rhino command manually while in Agent",
            "  /memory <command>         Show, index, edit, refresh, import/export, or undo document memory",
            "  ! <command>               Pass a command or alias directly to Rhino",
            "  _Command / -Command       Native Rhino command passthrough",
            "  Line / user aliases       Direct command and alias passthrough",
            "  /ask <prompt>             Force chat when text starts like a command",
            "  /clear                    Clear local history and saved provider resume state",
            "  /usage [on|off]           Show, hide, or explain exact usage reporting",
            "  /exit                     Leave Agent"
        ]));
    }

    private static void HandleSkillCommand(
        string arg,
        AgentConfig config,
        AgentSession session,
        AgentServices services)
    {
        var parts = arg.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = parts.Length > 0 ? parts[0].Trim().ToLowerInvariant() : "";
        var name = parts.Length > 1 ? parts[1].Trim() : "";
        var rest = parts.Length > 2 ? parts[2].Trim() : "";

        switch (subcommand)
        {
            case "":
            case "help":
                PrintSkillHelp(services.SkillStore);
                return;
            case "list":
                CommandLineUi.Debug(services.SkillStore.BuildSkillListSummary());
                return;
            case "show":
                if (name.Length == 0)
                {
                    CommandLineUi.Debug("Usage: /skill show <name>");
                    return;
                }
                PrintSkillReadResult(services.SkillStore.ReadSkillFile(name, "SKILL.md", 12000));
                return;
            case "use":
                UseSkill(name, rest, config, session, services);
                return;
            case "create":
            case "save":
                var brief = arg.Length > subcommand.Length ? arg[subcommand.Length..].Trim() : "";
                if (brief.Length == 0)
                {
                    CommandLineUi.Debug($"Usage: /skill {subcommand} <brief>");
                    return;
                }
                RunAgentPrompt(
                    config,
                    session,
                    BuildSkillCreationPrompt(subcommand, brief),
                    ["skill-writer"]);
                return;
            case "enable":
            case "disable":
                if (name.Length == 0)
                {
                    CommandLineUi.Debug($"Usage: /skill {subcommand} <name>");
                    return;
                }
                PrintSkillReadResult(services.SkillStore.SetEnabled(name, subcommand == "enable"));
                return;
            case "delete":
                if (name.Length == 0)
                {
                    CommandLineUi.Debug("Usage: /skill delete <name>");
                    return;
                }
                ExecuteSkillToolWithApproval(services, new ToolCallRequest
                {
                    Tool = "delete_skill",
                    Arguments = new Dictionary<string, object?> { ["name"] = name }
                });
                return;
            case "export":
                if (name.Length == 0 || rest.Length == 0)
                {
                    CommandLineUi.Debug("Usage: /skill export <name> <destination-folder>");
                    return;
                }
                ExecuteSkillToolWithApproval(services, new ToolCallRequest
                {
                    Tool = "export_skill",
                    Arguments = new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["destination"] = rest,
                        ["overwrite"] = false
                    }
                });
                return;
            case "demos":
                InstallDemoSkills(services.SkillStore);
                return;
            default:
                CommandLineUi.Debug($"Unknown skill command: {subcommand}. Type /skill help.");
                return;
        }
    }

    private static void PrintSkillHelp(SkillStore store)
    {
        CommandLineUi.Debug(string.Join(Environment.NewLine,
        [
            "RhinoAgent skill commands:",
            $"  Root: {store.RootDirectory}",
            "  /skill list",
            "  /skill show <name>",
            "  /skill use <name> [prompt]",
            "  /skill create <brief>",
            "  /skill save <brief>",
            "  /skill enable|disable <name>",
            "  /skill delete <name>",
            "  /skill export <name> <destination-folder>",
            "  /skill demos"
        ]));
    }

    private static void UseSkill(
        string name,
        string prompt,
        AgentConfig config,
        AgentSession session,
        AgentServices services)
    {
        if (name.Length == 0)
        {
            CommandLineUi.Debug("Usage: /skill use <name> [prompt]");
            return;
        }

        var skill = services.SkillStore.GetSkill(name);
        if (skill is null)
        {
            CommandLineUi.Debug($"Skill not found: {name}");
            return;
        }

        if (!skill.Enabled)
        {
            CommandLineUi.Debug($"Skill is disabled: {skill.Name}");
            return;
        }

        if (prompt.Length == 0)
        {
            session.QueueSkillForNextTurn(skill.Name);
            CommandLineUi.Debug($"Queued skill for next prompt: {skill.Name}");
            return;
        }

        RunAgentPrompt(config, session, prompt, [skill.Name]);
    }

    private static void ExecuteSkillToolWithApproval(AgentServices services, ToolCallRequest call)
    {
        if (!services.Approvals.ShouldExecute(call, services.ToolHost, out var reason))
        {
            CommandLineUi.Debug(reason);
            return;
        }

        if (services.Approvals.RequiresPrompt(call, services.ToolHost)
            && !services.Approvals.PromptForApproval(call, services.ToolHost))
        {
            CommandLineUi.Debug("Skill operation denied.");
            return;
        }

        var result = services.ToolHost.ExecuteAsync(call).GetAwaiter().GetResult();
        CommandLineUi.Debug(result.Output);
    }

    private static void InstallDemoSkills(SkillStore store)
    {
        CommandLineUi.Debug("Install demo skills: rhino-model-review, parametric-form-study, skill-writer");
        if (!PromptYesNo("Install RhinoAgent demo skills?"))
        {
            CommandLineUi.Debug("Demo skill install canceled.");
            return;
        }

        var results = DemoSkillInstaller.Install(store, overwrite: false);
        CommandLineUi.Debug(string.Join(Environment.NewLine, results.Select(result => result.Message)));
    }

    private static bool PromptYesNo(string prompt)
    {
        var getter = new GetOption();
        getter.SetCommandPrompt(prompt);
        getter.AddOption("Yes");
        getter.AddOption("No");
        getter.AcceptNothing(true);

        var result = getter.Get();
        if (result != Rhino.Input.GetResult.Option)
            return false;

        return getter.Option()?.EnglishName == "Yes";
    }

    private static void PrintSkillReadResult(SkillOperationResult result)
    {
        CommandLineUi.Debug(result.Success ? result.Message : result.Message);
    }

    private static string BuildSkillCreationPrompt(string subcommand, string brief) =>
        $"""
        The user asked RhinoAgent to {subcommand} a reusable skill.

        Brief:
        {brief}

        Create a concise Codex-style RhinoAgent skill. Pick a lowercase hyphenated name, write clear frontmatter with name and description, and include only useful files.
        Use references, scripts, or assets only when they make the skill more reusable.
        When ready, call create_skill with name, description, overwrite=false, and the complete files manifest.
        Do not use write_file for this skill; use create_skill so RhinoAgent can validate and approve the manifest.
        """;

    private static void RunAgentPrompt(
        AgentConfig config,
        AgentSession session,
        string prompt,
        IReadOnlyList<string>? forcedSkillNames = null)
    {
        var timeoutSeconds = Math.Max(0, config.ProviderTurnTimeoutSeconds);
        using var timeoutCancellation = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        try
        {
            RhinoTaskPump.Run(
                cancellationToken => session.RunUserTurnAsync(
                    prompt,
                    cancellationToken,
                    forcedSkillNames: forcedSkillNames),
                timeoutCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            CommandLineUi.Debug(timeoutSeconds > 0 && timeoutCancellation.IsCancellationRequested
                ? $"Agent turn timed out after {timeoutSeconds} seconds. Use /timeout <seconds> to adjust or /timeout off to disable."
                : "Agent turn was canceled.");
        }
        catch (Exception ex)
        {
            CommandLineUi.Debug($"Agent error: {ex.Message}");
        }
    }

    private static void ContinueOrResumeProviderConversation(AgentSession session, string arg)
    {
        var resumeLatest = string.IsNullOrWhiteSpace(arg) || IsLatestResumeToken(arg);
        var ok = resumeLatest
            ? session.TryContinueLatestProviderConversation(out var message)
            : session.TryResumeProviderConversation(arg, out message);

        CommandLineUi.Debug(message);
        if (ok)
            CommandLineUi.Debug("Local RhinoAgent history was cleared. Type the next prompt to continue that provider conversation.");
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
            $"Document memory: {(config.EnableDocumentMemory ? "on" : "off")}",
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

    private static bool IsLatestResumeToken(string value)
    {
        value = value.Trim();
        return value.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || value.Equals("last", StringComparison.OrdinalIgnoreCase)
            || value.Equals("--continue", StringComparison.OrdinalIgnoreCase);
    }

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
