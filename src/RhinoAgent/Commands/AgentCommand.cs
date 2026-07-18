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
    private static int _reportedImageHookFailure;

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
                    using var imageComposer = new AgentImageComposer();
                    var input = ReadLiteralLine(CommandLineUi.UserPrompt, imageComposer);
                    if (input is null)
                        return Result.Cancel;

                    var message = imageComposer.Compose(input);
                    if (message.Text.Length == 0 && message.Images.Count == 0)
                        continue;

                    if (message.Images.Count > 0)
                    {
                        CommandLineUi.Debug("Attached " + string.Join(
                            ", ",
                            message.Images.Select(image => $"{image.Placeholder} {image.FileName}")));
                    }

                    if (TryHandleForcedPrompt(message, session))
                        continue;

                    if (message.Images.Count == 0 && TryRunManualRhinoCommand(message.Text))
                        continue;

                    var slashResult = SlashCommands.TryHandle(message.Text, config, session, services);
                    if (slashResult == SlashCommandResult.Exit)
                        return Result.Success;
                    if (slashResult == SlashCommandResult.Handled)
                        continue;

                    RunAgentTurn(session, message);
                }
            }
            finally
            {
                FlushConversationIndex(session, config);
            }
        }
    }

    private static string? ReadLiteralLine(string prompt, AgentImageComposer imageComposer)
    {
        var pendingImages = "";
        var awaitingNativeImagePath = false;
        while (true)
        {
            var getter = new GetString();
            getter.SetCommandPrompt(pendingImages.Length == 0
                ? $"{prompt}>"
                : $"{prompt}> {pendingImages}");
            getter.AcceptNothing(true);

            // GetLiteralString keeps spaces in the command line, which makes the
            // Rhino prompt viable as a chat composer instead of a single-word input.
            using var pasteHook = AgentCommandLineImagePasteHook.TryInstall(imageComposer);
            if (OperatingSystem.IsWindows()
                && pasteHook is null
                && Interlocked.Exchange(ref _reportedImageHookFailure, 1) == 0)
            {
                CommandLineUi.Debug(
                    $"Image clipboard hook could not start (Windows error {AgentCommandLineImagePasteHook.LastInstallError}). " +
                    "Rhino's native paste fallback and pasted/dropped image paths still work.");
            }

            var result = getter.GetLiteralString();
            if (result == Rhino.Input.GetResult.Cancel)
                return null;

            var input = result == Rhino.Input.GetResult.String ? getter.StringResult() : "";
            if (IsNativeImagePasteCommand(input)
                && imageComposer.TryCaptureClipboard(out var insertion))
            {
                pendingImages = JoinPromptParts(pendingImages, insertion);
                awaitingNativeImagePath = true;
                continue;
            }

            // Rhino may queue its own PastedImages file path immediately after
            // the _Paste sentinel. The clipboard capture above already owns the
            // attachment, so consume this duplicate and keep the composer open.
            if (awaitingNativeImagePath && IsExistingImagePathOnly(input))
            {
                awaitingNativeImagePath = false;
                continue;
            }

            // Explorer drag/drop can arrive as a submitted image path rather
            // than a keyboard paste event. Convert a path-only input into the
            // same visible attachment state and keep the composer open.
            if (IsExistingImagePathOnly(input))
            {
                var droppedImage = imageComposer.Compose(input);
                if (droppedImage.Images.Count > 0)
                {
                    pendingImages = JoinPromptParts(pendingImages, droppedImage.Text);
                    continue;
                }
            }

            return JoinPromptParts(pendingImages, input);
        }
    }

    private static bool IsNativeImagePasteCommand(string input)
    {
        var command = input.TrimStart('_', '-', ' ');
        return command.Equals("Paste", StringComparison.OrdinalIgnoreCase)
            || command.Equals("Picture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExistingImagePathOnly(string input)
    {
        var path = input.Trim().Trim('"', '\'');
        if (!File.Exists(path))
            return false;

        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
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

    private static void FlushConversationIndex(AgentSession session, AgentConfig config)
    {
        if (session.PendingConversationIndexTurnCount == 0)
            return;

        var timeoutSeconds = Math.Max(30, config.ProviderTurnTimeoutSeconds);
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = RhinoTaskPump.Run(
                session.IndexConversationAsync,
                timeoutCancellation.Token);
            if (result.Updated)
                CommandLineUi.Debug($"Session conversation indexed into memory: {result.Reason}. Use /memory undo to revert.");
            else if (!result.Completed)
                CommandLineUi.Debug($"Session conversation index remains pending: {result.Message}");
            else if (config.ShowDebugMessages)
                CommandLineUi.Debug($"Session conversation indexed; memory unchanged: {result.Message}");
        }
        catch (OperationCanceledException)
        {
            CommandLineUi.Debug(timeoutCancellation.IsCancellationRequested
                ? $"Session conversation indexing timed out after {timeoutSeconds} seconds."
                : "Session conversation indexing was canceled.");
        }
        catch (Exception ex)
        {
            CommandLineUi.Debug($"Session conversation indexing failed: {ex.Message}");
        }
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
