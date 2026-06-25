using System.Runtime.InteropServices;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.Input.Custom;
using RhinoAgent.Config;
using RhinoAgent.Runtime;

namespace RhinoAgent.Commands;

[Guid("6DC03904-98A7-465B-90A4-6832EE10063E")]
public sealed class AgentCommand : Command
{
    public override string EnglishName => "Agent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var config = AgentConfigStore.Load();
        var services = AgentServices.Create(config, doc);

        CommandLineUi.Debug(
            $"RhinoAgent{Environment.NewLine}" +
            $"  Config: {AgentConfigStore.ConfigPath}{Environment.NewLine}" +
            "  Type /help for commands, /exit to leave, /run or ! for manual Rhino command passthrough.");

        var provider = services.ProviderFactory.ResolveInteractiveProvider(config);
        if (provider is null)
        {
            CommandLineUi.Debug(
                "No logged-in Claude or Codex CLI was detected." + Environment.NewLine +
                "Starting first-run login. Choose a provider in the prompt below.");
            LoginFlow.Run(config, services);
            return Result.Success;
        }

        CommandLineUi.Debug(
            $"Provider: {provider.DisplayName}{Environment.NewLine}" +
            $"Process: {provider.ProcessMode}{Environment.NewLine}" +
            $"Mode: {config.PermissionMode}");

        using (provider)
        {
            var session = new AgentSession(doc, config, provider, services.ToolHost, services.Approvals, services.SkillStore);
            while (true)
            {
                CommandLineUi.Separator();
                var input = ReadLiteralLine(CommandLineUi.UserPrompt);
                if (input is null)
                    return Result.Cancel;

                input = input.Trim();
                if (input.Length == 0)
                    continue;

                if (TryHandleForcedPrompt(input, session))
                    continue;

                if (TryRunManualRhinoCommand(input))
                    continue;

                var slashResult = SlashCommands.TryHandle(input, config, session, services);
                if (slashResult == SlashCommandResult.Exit)
                    return Result.Success;
                if (slashResult == SlashCommandResult.Handled)
                    continue;

                RunAgentTurn(session, input);
            }
        }
    }

    private static string? ReadLiteralLine(string prompt)
    {
        var getter = new GetString();
        getter.SetCommandPrompt($"{prompt}>");
        getter.AcceptNothing(true);

        // GetLiteralString keeps spaces in the command line, which makes the
        // Rhino prompt viable as a chat composer instead of a single-word input.
        var result = getter.GetLiteralString();
        if (result == Rhino.Input.GetResult.Cancel)
            return null;
        if (result == Rhino.Input.GetResult.Nothing)
            return "";
        return result == Rhino.Input.GetResult.String ? getter.StringResult() : "";
    }

    private static void RunAgentTurn(AgentSession session, string input)
    {
        try
        {
            var timeoutSeconds = Math.Max(0, AgentConfigStore.Load().ProviderTurnTimeoutSeconds);
            using var cancellation = timeoutSeconds > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
                : new CancellationTokenSource();

            session.RunUserTurnAsync(input, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            var timeoutSeconds = Math.Max(0, AgentConfigStore.Load().ProviderTurnTimeoutSeconds);
            CommandLineUi.Debug(timeoutSeconds > 0
                ? $"Agent turn timed out after {timeoutSeconds} seconds. Use /timeout <seconds> to adjust or /timeout off to disable."
                : "Agent turn was canceled.");
        }
        catch (Exception ex)
        {
            CommandLineUi.Debug($"Agent error: {ex.Message}");
        }
    }

    private static bool TryHandleForcedPrompt(string input, AgentSession session)
    {
        const string askCommand = "/ask";
        if (!input.Equals(askCommand, StringComparison.OrdinalIgnoreCase)
            && !input.StartsWith(askCommand + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        var prompt = input.Length == askCommand.Length
            ? ""
            : input[askCommand.Length..].TrimStart();

        if (prompt.Length == 0)
            CommandLineUi.Debug("Usage: /ask <prompt>");
        else
            RunAgentTurn(session, prompt);

        return true;
    }

    private static bool TryRunManualRhinoCommand(string input)
    {
        if (!TryResolveManualRhinoCommand(input, out var command, out var matchedBy))
            return false;

        if (command.Length == 0)
        {
            CommandLineUi.Debug("Usage: ! _RhinoCommand arguments");
            return true;
        }

        if (matchedBy is "alias")
            CommandLineUi.Debug($"Alias -> {command}");
        RhinoApp.RunScript(command, true);
        return true;
    }

    internal static bool TryResolveManualRhinoCommand(string input, out string script, out string matchedBy)
    {
        script = "";
        matchedBy = "";
        var command = input.Trim();
        if (command.Length == 0)
            return false;

        if (command.StartsWith("!", StringComparison.Ordinal))
        {
            script = command[1..].TrimStart();
            matchedBy = "bang";
            return true;
        }

        // These prefixes are native Rhino command-line idioms. Passing them
        // through keeps Agent usable while the user manually models.
        if (StartsWithNativeCommandPrefix(command))
        {
            script = command;
            matchedBy = "prefix";
            return true;
        }

        var firstToken = SplitFirstToken(command, out var remainder);
        if (IsRhinoAgentCommand(firstToken))
            return false;

        if (TryGetAliasMacro(firstToken, out var macro) && LooksLikeCommandArguments(remainder))
        {
            script = JoinMacroAndRemainder(macro, remainder);
            matchedBy = "alias";
            return true;
        }

        if (IsKnownRhinoCommand(firstToken) && LooksLikeCommandArguments(remainder))
        {
            script = command;
            matchedBy = "command";
            return true;
        }

        return false;
    }

    private static bool StartsWithNativeCommandPrefix(string command) =>
        command.StartsWith("_", StringComparison.Ordinal)
        || command.StartsWith("-", StringComparison.Ordinal)
        || command.StartsWith(".", StringComparison.Ordinal)
        || command.StartsWith("'", StringComparison.Ordinal);

    private static string SplitFirstToken(string command, out string remainder)
    {
        var index = command.IndexOfAny([' ', '\t']);
        if (index < 0)
        {
            remainder = "";
            return command;
        }

        remainder = command[(index + 1)..].TrimStart();
        return command[..index];
    }

    private static bool IsRhinoAgentCommand(string token) =>
        token.StartsWith("Agent", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetAliasMacro(string token, out string macro)
    {
        macro = "";
        try
        {
            if (!CommandAliasList.IsAlias(token))
                return false;

            macro = CommandAliasList.GetMacro(token) ?? "";
            return macro.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKnownRhinoCommand(string token)
    {
        try
        {
            return Command.IsCommand(token);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeCommandArguments(string remainder)
    {
        if (string.IsNullOrWhiteSpace(remainder))
            return true;

        var value = remainder.Trim();
        if (value.Contains("_Enter", StringComparison.OrdinalIgnoreCase)
            || value.Contains("_Pause", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" pause", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" enter", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Any(c => char.IsDigit(c) || ",=;\"'()[]{}@#".Contains(c));
    }

    private static string JoinMacroAndRemainder(string macro, string remainder) =>
        string.IsNullOrWhiteSpace(remainder) ? macro : $"{macro} {remainder}";
}
