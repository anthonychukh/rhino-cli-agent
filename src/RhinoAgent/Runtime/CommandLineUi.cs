using Rhino;

namespace RhinoAgent.Runtime;

public static class CommandLineUi
{
    private const string UserIcon = "▶";
    private const string DebugIcon = "•";
    private const string AgentIcon = "◀";
    private const string UserLabel = "You";
    private const string DebugLabel = "Debug";
    private const string AgentLabel = "Agent";
    private const int ThinkingFrameMs = 350;

    private static readonly object WriteLock = new();
    private static EntryKind? LastEntryKind;

    public const string UserPrompt = $"{UserIcon} {UserLabel}";

    public static void Separator()
    {
        lock (WriteLock)
        {
            RhinoApp.WriteLine();
            LastEntryKind = null;
        }
    }

    public static void Debug(string message) =>
        WriteEntry(EntryKind.Debug, DebugIcon, DebugLabel, message);

    public static void AgentResponse(string message) =>
        WriteEntry(EntryKind.Agent, AgentIcon, AgentLabel, message);

    public static IDisposable Thinking(string message)
    {
        Debug($"{message}...");
        return new ThinkingIndicator(message);
    }

    private static void WriteEntry(EntryKind kind, string icon, string label, string message)
    {
        var prefix = $"{icon} {label}: ";
        var continuationPrefix = new string(' ', prefix.Length);
        var lines = SplitLines(message);

        lock (WriteLock)
        {
            if (ShouldSeparate(kind))
                RhinoApp.WriteLine();
            for (var i = 0; i < lines.Length; i++)
                RhinoApp.WriteLine($"{(i == 0 ? prefix : continuationPrefix)}{lines[i]}");
            LastEntryKind = kind;
        }
    }

    private static bool ShouldSeparate(EntryKind kind) =>
        kind != EntryKind.Debug || LastEntryKind != EntryKind.Debug;

    private static string[] SplitLines(string message)
    {
        if (string.IsNullOrEmpty(message))
            return [""];

        return message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private sealed class ThinkingIndicator : IDisposable
    {
        private readonly CancellationTokenSource? _cancellation;
        private readonly Task? _animationTask;

        public ThinkingIndicator(string message)
        {
            if (RhinoApp.IsRunningHeadless || RhinoApp.IsRunningAutomated)
                return;

            _cancellation = new CancellationTokenSource();
            _animationTask = Task.Run(() => AnimateAsync(message, _cancellation.Token));
        }

        public void Dispose()
        {
            if (_cancellation is null)
                return;

            try
            {
                _cancellation.Cancel();
                _animationTask?.Wait(TimeSpan.FromMilliseconds(ThinkingFrameMs));
            }
            catch
            {
                // Prompt animation is best-effort; command history already has the durable state.
            }
            finally
            {
                SetPromptMessage($"{UserPrompt}>");
                _cancellation.Dispose();
            }
        }

        private static async Task AnimateAsync(string message, CancellationToken cancellationToken)
        {
            var frame = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var dots = new string('.', frame % 4).PadRight(3);
                SetPromptMessage($"{DebugIcon} {message}{dots}");
                frame++;
                await Task.Delay(ThinkingFrameMs, cancellationToken).ConfigureAwait(false);
            }
        }

        private static void SetPromptMessage(string message)
        {
            try
            {
                RhinoApp.SetCommandPromptMessage(message);
            }
            catch
            {
                // Some automated or non-interactive Rhino sessions do not expose a prompt.
            }
        }
    }

    private enum EntryKind
    {
        Debug,
        Agent
    }
}
