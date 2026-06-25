using System.Text.Json;

namespace RhinoAgent.Config;

public static class CodexSessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string StorePath => Path.Combine(AgentConfigStore.ConfigDirectory, "codex-sessions.json");

    public static CodexSessionRecord? LoadForWorkingDirectory(string workingDirectory)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0)
            return null;

        try
        {
            var state = Load();
            return state.ThreadsByWorkingDirectory.TryGetValue(key, out var record)
                && !string.IsNullOrWhiteSpace(record.ThreadId)
                ? record
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveWorkingDirectoryThread(string workingDirectory, string threadId, string? model)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(threadId))
            return;

        try
        {
            var state = Load();
            state.ThreadsByWorkingDirectory[key] = new CodexSessionRecord
            {
                WorkingDirectory = workingDirectory,
                ThreadId = threadId,
                Model = model,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            Directory.CreateDirectory(AgentConfigStore.ConfigDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // Session resume is best-effort; provider turns should not fail because state could not be saved.
        }
    }

    public static void ClearWorkingDirectory(string workingDirectory)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0)
            return;

        try
        {
            var state = Load();
            if (!state.ThreadsByWorkingDirectory.Remove(key))
                return;

            Directory.CreateDirectory(AgentConfigStore.ConfigDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // Clearing persisted resume state is best-effort.
        }
    }

    private static CodexSessionState Load()
    {
        if (!File.Exists(StorePath))
            return new CodexSessionState();

        var json = File.ReadAllText(StorePath);
        var state = JsonSerializer.Deserialize<CodexSessionState>(json, Options) ?? new CodexSessionState();
        return state.NormalizeComparer();
    }

    private static string NormalizeWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return "";

        try
        {
            return Path.GetFullPath(workingDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return workingDirectory.Trim().ToUpperInvariant();
        }
    }
}

public sealed class CodexSessionState
{
    public Dictionary<string, CodexSessionRecord> ThreadsByWorkingDirectory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CodexSessionState NormalizeComparer()
    {
        ThreadsByWorkingDirectory = new Dictionary<string, CodexSessionRecord>(
            ThreadsByWorkingDirectory,
            StringComparer.OrdinalIgnoreCase);
        return this;
    }
}

public sealed class CodexSessionRecord
{
    public string WorkingDirectory { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string? Model { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
