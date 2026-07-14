using RhinoAgent.Memory;
using RhinoAgent.UI;

namespace RhinoAgent.Runtime;

public static class AgentMemorySlashCommands
{
    public static void Handle(string arg, AgentConfig config, AgentServices services)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        var rest = parts.Length > 1 ? parts[1].Trim() : "";
        var doc = services.Document;

        switch (command)
        {
            case "status":
                CommandLineUi.Debug(AgentMemoryStore.DescribeStatus(doc));
                return;
            case "open":
                AgentMemoryStore.EnsureCreated(doc);
                CommandLineUi.Debug(AgentMemoryPanel.OpenPanel()
                    ? "Agent memory panel opened."
                    : "Agent memory panel could not be opened.");
                return;
            case "show":
                CommandLineUi.Debug(AgentMemoryMarkdown.Truncate(AgentMemoryStore.EnsureCreated(doc).Markdown, 12000));
                return;
            case "create":
                AgentMemoryStore.EnsureCreated(doc);
                CommandLineUi.Debug("Agent memory exists for this document.");
                return;
            case "refresh":
                Refresh(config, services);
                return;
            case "undo":
                Undo(rest, services);
                return;
            case "history":
                CommandLineUi.Debug(AgentMemoryStore.DescribeHistory(doc));
                return;
            case "reset":
                CommandLineUi.Debug(AgentMemoryStore.Reset(doc).Message);
                return;
            case "import":
                Import(rest, services);
                return;
            case "export":
                Export(rest, services);
                return;
            case "on":
            case "enable":
                CommandLineUi.Debug(AgentMemoryStore.SetEnabled(doc, true).Message);
                return;
            case "off":
            case "disable":
                CommandLineUi.Debug(AgentMemoryStore.SetEnabled(doc, false).Message);
                return;
            case "debug":
                CommandLineUi.Debug(AgentMemoryStore.DescribeDebug(doc));
                return;
            default:
                CommandLineUi.Debug("Usage: /memory status|open|show|create|refresh|undo [steps]|history|reset|import <path>|export [path]|on|off|debug");
                return;
        }
    }

    private static void Refresh(AgentConfig config, AgentServices services)
    {
        var timeoutSeconds = Math.Max(30, config.ProviderTurnTimeoutSeconds);
        using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = RhinoTaskPump.Run(
                services.MemoryUpdater.RefreshAsync,
                timeoutCancellation.Token);
            CommandLineUi.Debug(result.Updated
                ? $"Memory refreshed: {result.Reason}. Use /memory undo to revert."
                : $"Memory unchanged: {result.Message}");
        }
        catch (OperationCanceledException)
        {
            CommandLineUi.Debug(timeoutCancellation.IsCancellationRequested
                ? $"Memory refresh timed out after {timeoutSeconds} seconds."
                : "Memory refresh was canceled.");
        }
        catch (Exception ex)
        {
            CommandLineUi.Debug($"Memory refresh failed: {ex.Message}");
        }
    }

    private static void Undo(string arg, AgentServices services)
    {
        var steps = 1;
        if (!string.IsNullOrWhiteSpace(arg) && (!int.TryParse(arg, out steps) || steps < 1))
        {
            CommandLineUi.Debug("Usage: /memory undo [steps]");
            return;
        }

        CommandLineUi.Debug(AgentMemoryStore.Undo(services.Document, steps).Message);
    }

    private static void Import(string path, AgentServices services)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            CommandLineUi.Debug("Usage: /memory import <path>");
            return;
        }

        CommandLineUi.Debug(AgentMemoryStore.ImportMarkdown(services.Document, Unquote(path)).Message);
    }

    private static void Export(string path, AgentServices services)
    {
        try
        {
            var written = AgentMemoryStore.ExportMarkdown(
                services.Document,
                string.IsNullOrWhiteSpace(path) ? null : Unquote(path));
            CommandLineUi.Debug($"Memory exported: {written}");
        }
        catch (Exception ex)
        {
            CommandLineUi.Debug($"Memory export failed: {ex.Message}");
        }
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }
}
