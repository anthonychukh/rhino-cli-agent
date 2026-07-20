using System.Runtime.InteropServices;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.Input.Custom;
using RhinoAgent.Attachments;
using RhinoAgent.Config;
using RhinoAgent.Runtime;

namespace RhinoAgent.Commands;

[Guid("6DC03904-98A7-465B-90A4-6832EE10063E")]
public sealed class AgentCommand : Command
{
    private static int _reportedAttachmentHookFailure;

    public override string EnglishName => "Agent";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var config = AgentConfigStore.Load();
        using var services = AgentServices.Create(config, doc);

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
            var session = new AgentSession(
                doc,
                config,
                provider,
                services.ToolHost,
                services.Approvals,
                services.SkillStore,
                services.MemoryUpdater);
            try
            {
                while (true)
                {
                    CommandLineUi.Separator();
                    var attachmentComposer = new AgentAttachmentComposer(services.AttachmentStore);
                    var input = ReadLiteralLine(CommandLineUi.UserPrompt, attachmentComposer);
                    if (input is null)
                        return Result.Cancel;

                    var message = attachmentComposer.Compose(input);
                    try
                    {
                        if (message.Text.Length == 0 && message.Attachments.Count == 0)
                            continue;

                        if (message.Attachments.Count > 0)
                        {
                            CommandLineUi.Debug("Attached " + string.Join(
                                ", ",
                                message.Attachments.Select(attachment => $"{attachment.Placeholder} {attachment.FileName}")));
                        }

                        if (TryHandleForcedPrompt(message, session))
                            continue;

                        if (message.Attachments.Count == 0 && TryRunManualRhinoCommand(message.Text))
                            continue;

                        var slashResult = SlashCommands.TryHandle(message.Text, config, session, services);
                        if (slashResult == SlashCommandResult.Exit)
                            return Result.Success;
                        if (slashResult == SlashCommandResult.Handled)
                            continue;

                        RunAgentTurn(session, message);
                    }
                    finally
                    {
                        services.AttachmentStore.ReleaseTemporary(message.Attachments);
                    }
                }
            }
            finally
            {
                StartConversationIndexInBackground(session, config);
            }
        }
    }

    private static string? ReadLiteralLine(string prompt, AgentAttachmentComposer attachmentComposer)
    {
        var pendingAttachments = "";
        var awaitingNativePastePath = false;
        while (true)
        {
            var getter = new GetString();
            getter.SetCommandPrompt(pendingAttachments.Length == 0
                ? $"{prompt}>"
                : $"{prompt}> {pendingAttachments}");
            getter.AcceptNothing(true);

            // GetLiteralString keeps spaces in the command line, which makes the
            // Rhino prompt viable as a chat composer instead of a single-word input.
            using var pasteHook = AgentCommandLineAttachmentPasteHook.TryInstall(attachmentComposer);
            if (OperatingSystem.IsWindows()
                && pasteHook is null
                && Interlocked.Exchange(ref _reportedAttachmentHookFailure, 1) == 0)
            {
                CommandLineUi.Debug(
                    $"Attachment clipboard hook could not start (Windows error {AgentCommandLineAttachmentPasteHook.LastInstallError}). " +
                    "Rhino's native paste fallback and pasted/dropped file paths still work.");
            }

            var result = getter.GetLiteralString();
            if (result == Rhino.Input.GetResult.Cancel)
                return null;

            var input = result == Rhino.Input.GetResult.String ? getter.StringResult() : "";
            if (IsNativeImagePasteCommand(input)
                && attachmentComposer.TryCaptureClipboard(out var insertion))
            {
                pendingAttachments = JoinPromptParts(pendingAttachments, insertion);
                awaitingNativePastePath = true;
                continue;
            }

            // Rhino may queue its own PastedImages file path immediately after
            // the _Paste sentinel. The clipboard capture above already owns the
            // attachment, so consume this duplicate and keep the composer open.
            if (awaitingNativePastePath && IsExistingFilePathOnly(input))
            {
                awaitingNativePastePath = false;
                continue;
            }

            // Explorer drag/drop can arrive as one or more submitted file paths
            // rather than a keyboard paste event. Convert an attachment-only
            // input into the same visible state and keep the composer open.
            var droppedFiles = attachmentComposer.Compose(input);
            if (droppedFiles.Attachments.Count > 0 && ContainsOnlyAttachments(droppedFiles))
            {
                pendingAttachments = JoinPromptParts(pendingAttachments, droppedFiles.Text);
                continue;
            }

            return JoinPromptParts(pendingAttachments, input);
        }
    }

    private static bool IsNativeImagePasteCommand(string input)
    {
        var command = input.TrimStart('_', '-', ' ');
        return command.Equals("Paste", StringComparison.OrdinalIgnoreCase)
            || command.Equals("Picture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExistingFilePathOnly(string input)
    {
        var path = input.Trim().Trim('"', '\'');
        return File.Exists(path);
    }

    private static bool ContainsOnlyAttachments(AgentUserMessage message)
    {
        var remaining = message.Text;
        foreach (var attachment in message.Attachments)
            remaining = remaining.Replace(attachment.Placeholder, "", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(remaining);
    }

    private static string JoinPromptParts(string left, string right) =>
        string.IsNullOrWhiteSpace(left)
            ? right.Trim()
            : string.IsNullOrWhiteSpace(right)
                ? left.Trim()
                : $"{left.Trim()} {right.Trim()}";

    private static void RunAgentTurn(
        AgentSession session,
        AgentUserMessage input,
        IReadOnlyList<string>? forcedSkillNames = null)
    {
        var timeoutSeconds = Math.Max(0, AgentConfigStore.Load().ProviderTurnTimeoutSeconds);
        using var timeoutCancellation = timeoutSeconds > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))
            : new CancellationTokenSource();

        try
        {
            RhinoTaskPump.Run(
                cancellationToken => session.RunUserTurnAsync(
                    input,
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

    private static void StartConversationIndexInBackground(AgentSession session, AgentConfig config)
    {
        if (session.PendingConversationIndexTurnCount == 0)
            return;

        var result = session.StartConversationIndexing();
        if (result.Started || config.ShowDebugMessages && result.Running)
            CommandLineUi.Debug(result.Message);
    }

    private static bool TryHandleForcedPrompt(AgentUserMessage message, AgentSession session)
    {
        const string askCommand = "/ask";
        var input = message.Text;
        if (!input.Equals(askCommand, StringComparison.OrdinalIgnoreCase)
            && !input.StartsWith(askCommand + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        var prompt = input.Length == askCommand.Length
            ? ""
            : input[askCommand.Length..].TrimStart();

        if (prompt.Length == 0)
            CommandLineUi.Debug("Usage: /ask <prompt>");
        else
            RunAgentTurn(session, message with { Text = prompt });

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
