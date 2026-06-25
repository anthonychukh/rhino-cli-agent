using System.Text.Json;

namespace RhinoAgent.Config;

public static class ClaudeSessionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string StorePath => Path.Combine(AgentConfigStore.ConfigDirectory, "claude-sessions.json");

    public static ClaudeSessionRecord? LoadForWorkingDirectory(string workingDirectory)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0)
            return null;

        try
        {
            var state = Load();
            return state.SessionsByWorkingDirectory.TryGetValue(key, out var record)
                && !string.IsNullOrWhiteSpace(record.SessionId)
                ? record
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool ShouldStartFresh(string workingDirectory)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0)
            return false;

        try
        {
            var state = Load();
            return state.SessionsByWorkingDirectory.TryGetValue(key, out var record)
                && record.StartFresh;
        }
        catch
        {
            return false;
        }
    }

    public static void SaveWorkingDirectorySession(string workingDirectory, string sessionId, string? model)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(sessionId))
            return;

        try
        {
            var state = Load();
            state.SessionsByWorkingDirectory[key] = new ClaudeSessionRecord
            {
                WorkingDirectory = workingDirectory,
                SessionId = sessionId,
                Model = model,
                StartFresh = false,
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
            state.SessionsByWorkingDirectory[key] = new ClaudeSessionRecord
            {
                WorkingDirectory = workingDirectory,
                SessionId = "",
                StartFresh = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            Directory.CreateDirectory(AgentConfigStore.ConfigDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(state, Options));
        }
        catch
        {
            // Clearing persisted resume state is best-effort.
        }
    }

    public static void AllowDefaultContinue(string workingDirectory)
    {
        var key = NormalizeWorkingDirectory(workingDirectory);
        if (key.Length == 0)
            return;

        try
        {
            var state = Load();
            if (state.SessionsByWorkingDirectory.TryGetValue(key, out var record))
            {
                record.StartFresh = false;
                state.SessionsByWorkingDirectory[key] = record;
                Directory.CreateDirectory(AgentConfigStore.ConfigDirectory);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(state, Options));
            }
        }
        catch
        {
            // Best-effort state update.
        }
    }

    private static ClaudeSessionState Load()
    {
        if (!File.Exists(StorePath))
            return new ClaudeSessionState();

        var json = File.ReadAllText(StorePath);
        var state = JsonSerializer.Deserialize<ClaudeSessionState>(json, Options) ?? new ClaudeSessionState();
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

public sealed class ClaudeSessionState
{
    public Dictionary<string, ClaudeSessionRecord> SessionsByWorkingDirectory { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeSessionState NormalizeComparer()
    {
        SessionsByWorkingDirectory = new Dictionary<string, ClaudeSessionRecord>(
            SessionsByWorkingDirectory,
            StringComparer.OrdinalIgnoreCase);
        return this;
    }
}

public sealed class ClaudeSessionRecord
{
    public string WorkingDirectory { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string? Model { get; set; }
    public bool StartFresh { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
