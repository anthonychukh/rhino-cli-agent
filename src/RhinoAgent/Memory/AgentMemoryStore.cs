using System.Text.Json;
using Rhino;
using RhinoAgent.Runtime;

namespace RhinoAgent.Memory;

public static class AgentMemoryStore
{
    private const string Section = "RhinoAgent.Memory";
    private const string MarkdownKey = "markdown";
    private const string MetadataKey = "metadata";
    private const int MaxHistory = 10;

    public static event EventHandler? MemoryChanged;

    public static AgentMemoryState Load(RhinoDoc doc)
    {
        var markdown = doc.Strings.GetValue(Section, MarkdownKey) ?? "";
        var metadata = LoadMetadata(doc);
        var exists = !string.IsNullOrWhiteSpace(markdown);
        var normalizedMarkdown = exists ? AgentMemoryMarkdown.EnsureAgentNotesSection(markdown) : "";
        var hash = exists ? AgentMemoryMarkdown.Hash(normalizedMarkdown) : "";

        if (exists && !string.Equals(markdown, normalizedMarkdown, StringComparison.Ordinal))
        {
            doc.Strings.SetString(Section, MarkdownKey, normalizedMarkdown);
            markdown = normalizedMarkdown;
        }

        return new AgentMemoryState(
            exists,
            metadata.Enabled,
            markdown,
            FirstNonEmpty(metadata.PromptSummary, exists ? AgentMemoryMarkdown.BuildPromptSummary(markdown) : ""),
            metadata.LastUpdatedUtc,
            metadata.LastUpdateReason ?? "",
            FirstNonEmpty(metadata.CurrentHash, hash),
            metadata.History);
    }

    public static AgentMemoryState EnsureCreated(RhinoDoc doc)
    {
        var state = Load(doc);
        if (state.Exists)
            return state;

        var markdown = AgentMemoryMarkdown.CreateTemplate(doc.Name ?? "Rhino Document");
        var metadata = new AgentMemoryMetadata
        {
            Enabled = true,
            PromptSummary = AgentMemoryMarkdown.BuildPromptSummary(markdown),
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            LastUpdateReason = "Created RhinoAgent document memory.",
            CurrentHash = AgentMemoryMarkdown.Hash(markdown)
        };

        SaveRaw(doc, markdown, metadata);
        NotifyChanged();
        return Load(doc);
    }

    public static AgentMemorySaveResult SaveUserMarkdown(RhinoDoc doc, string markdown, string reason)
    {
        markdown = AgentMemoryMarkdown.EnsureAgentNotesSection(markdown);
        var state = EnsureCreated(doc);
        if (string.Equals(state.Markdown, markdown, StringComparison.Ordinal))
            return new AgentMemorySaveResult(false, "Memory is already up to date.", state);

        var metadata = LoadMetadata(doc);
        AddSnapshot(metadata, state.Markdown, state.PromptSummary, reason);
        metadata.PromptSummary = AgentMemoryMarkdown.BuildPromptSummary(markdown);
        metadata.LastUpdatedUtc = DateTimeOffset.UtcNow;
        metadata.LastUpdateReason = reason;
        metadata.CurrentHash = AgentMemoryMarkdown.Hash(markdown);
        SaveRaw(doc, markdown, metadata);
        NotifyChanged();
        return new AgentMemorySaveResult(true, "Memory saved.", Load(doc));
    }

    public static AgentMemorySaveResult ApplyGeneratedUpdate(
        RhinoDoc doc,
        string generatedAgentNotes,
        string promptSummary,
        string reason)
    {
        var state = EnsureCreated(doc);
        var updatedMarkdown = AgentMemoryMarkdown.ReplaceAgentNotes(state.Markdown, generatedAgentNotes);
        var summary = FirstNonEmpty(promptSummary, AgentMemoryMarkdown.BuildPromptSummary(updatedMarkdown));

        if (string.Equals(state.Markdown, updatedMarkdown, StringComparison.Ordinal)
            && string.Equals(state.PromptSummary, summary, StringComparison.Ordinal))
            return new AgentMemorySaveResult(false, "Memory update produced no changes.", state);

        var metadata = LoadMetadata(doc);
        AddSnapshot(metadata, state.Markdown, state.PromptSummary, reason);
        metadata.PromptSummary = summary;
        metadata.LastUpdatedUtc = DateTimeOffset.UtcNow;
        metadata.LastUpdateReason = reason;
        metadata.CurrentHash = AgentMemoryMarkdown.Hash(updatedMarkdown);
        SaveRaw(doc, updatedMarkdown, metadata);
        NotifyChanged();
        return new AgentMemorySaveResult(true, "Memory updated.", Load(doc));
    }

    public static AgentMemorySaveResult Reset(RhinoDoc doc)
    {
        var state = Load(doc);
        var metadata = LoadMetadata(doc);
        if (state.Exists)
            AddSnapshot(metadata, state.Markdown, state.PromptSummary, "Reset memory.");

        var markdown = AgentMemoryMarkdown.CreateTemplate(doc.Name ?? "Rhino Document");
        metadata.Enabled = true;
        metadata.PromptSummary = AgentMemoryMarkdown.BuildPromptSummary(markdown);
        metadata.LastUpdatedUtc = DateTimeOffset.UtcNow;
        metadata.LastUpdateReason = "Reset memory.";
        metadata.CurrentHash = AgentMemoryMarkdown.Hash(markdown);
        SaveRaw(doc, markdown, metadata);
        NotifyChanged();
        return new AgentMemorySaveResult(true, "Memory reset.", Load(doc));
    }

    public static AgentMemorySaveResult SetEnabled(RhinoDoc doc, bool enabled)
    {
        EnsureCreated(doc);
        var metadata = LoadMetadata(doc);
        if (metadata.Enabled == enabled)
            return new AgentMemorySaveResult(false, $"Memory is already {(enabled ? "on" : "off")}.", Load(doc));

        metadata.Enabled = enabled;
        metadata.LastUpdatedUtc = DateTimeOffset.UtcNow;
        metadata.LastUpdateReason = enabled ? "Enabled memory." : "Disabled memory.";
        SaveMetadata(doc, metadata);
        NotifyChanged();
        return new AgentMemorySaveResult(true, $"Memory {(enabled ? "enabled" : "disabled")}.", Load(doc));
    }

    public static AgentMemorySaveResult Undo(RhinoDoc doc, int steps)
    {
        steps = Math.Max(1, steps);
        var state = Load(doc);
        if (!state.Exists)
            return new AgentMemorySaveResult(false, "No memory exists yet.", state);

        var metadata = LoadMetadata(doc);
        if (metadata.History.Count == 0)
            return new AgentMemorySaveResult(false, "No memory history is available.", state);
        if (steps > metadata.History.Count)
            return new AgentMemorySaveResult(false, $"Only {metadata.History.Count} memory snapshot(s) are available.", state);

        var index = metadata.History.Count - steps;
        var snapshot = metadata.History[index];
        metadata.History.RemoveRange(index, metadata.History.Count - index);
        metadata.PromptSummary = snapshot.PromptSummary;
        metadata.LastUpdatedUtc = DateTimeOffset.UtcNow;
        metadata.LastUpdateReason = $"Undo memory by {steps} step(s).";
        metadata.CurrentHash = AgentMemoryMarkdown.Hash(snapshot.Markdown);
        SaveRaw(doc, snapshot.Markdown, metadata);
        NotifyChanged();
        return new AgentMemorySaveResult(true, $"Memory restored from {snapshot.TimestampUtc:u}.", Load(doc));
    }

    public static AgentMemorySaveResult ImportMarkdown(RhinoDoc doc, string path)
    {
        if (!File.Exists(path))
            return new AgentMemorySaveResult(false, $"Import file not found: {path}", Load(doc));

        var markdown = File.ReadAllText(path);
        return SaveUserMarkdown(doc, markdown, $"Imported memory from {path}.");
    }

    public static string ExportMarkdown(RhinoDoc doc, string? path)
    {
        var state = EnsureCreated(doc);
        path = ResolveExportPath(doc, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, state.Markdown);
        return path;
    }

    public static string DescribeStatus(RhinoDoc doc)
    {
        var state = Load(doc);
        if (!state.Exists)
            return "RhinoAgent memory has not been created for this document yet.";

        return string.Join(Environment.NewLine,
        [
            "RhinoAgent memory",
            $"  Enabled: {(state.Enabled ? "on" : "off")}",
            $"  Characters: {state.Markdown.Length}",
            $"  Hash: {ShortHash(state.CurrentHash)}",
            $"  Last updated: {FormatDate(state.LastUpdatedUtc)}",
            $"  Last reason: {FirstNonEmpty(state.LastUpdateReason, "(none)")}",
            $"  History snapshots: {state.History.Count}"
        ]);
    }

    public static string DescribeHistory(RhinoDoc doc)
    {
        var state = Load(doc);
        if (!state.Exists || state.History.Count == 0)
            return "No memory history is available.";

        return string.Join(Environment.NewLine, state.History
            .Select((snapshot, index) => $"{index + 1}. {snapshot.TimestampUtc:u} {ShortHash(snapshot.Hash)} {snapshot.Reason}"));
    }

    public static string DescribeDebug(RhinoDoc doc)
    {
        var state = Load(doc);
        return JsonSerializer.Serialize(new
        {
            section = Section,
            markdownKey = MarkdownKey,
            metadataKey = MetadataKey,
            state.Exists,
            state.Enabled,
            state.CurrentHash,
            state.LastUpdatedUtc,
            state.LastUpdateReason,
            state.PromptSummary,
            historyCount = state.History.Count
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Clear(RhinoDoc doc)
    {
        doc.Strings.Delete(Section, MarkdownKey);
        doc.Strings.Delete(Section, MetadataKey);
        NotifyChanged();
    }

    public static void Restore(RhinoDoc doc, AgentMemoryState state)
    {
        if (!state.Exists)
        {
            Clear(doc);
            return;
        }

        var metadata = new AgentMemoryMetadata
        {
            Enabled = state.Enabled,
            PromptSummary = state.PromptSummary,
            LastUpdatedUtc = state.LastUpdatedUtc,
            LastUpdateReason = state.LastUpdateReason,
            CurrentHash = state.CurrentHash,
            History = state.History.ToList()
        };
        SaveRaw(doc, state.Markdown, metadata);
        NotifyChanged();
    }

    private static AgentMemoryMetadata LoadMetadata(RhinoDoc doc)
    {
        var json = doc.Strings.GetValue(Section, MetadataKey);
        if (string.IsNullOrWhiteSpace(json))
            return new AgentMemoryMetadata();

        try
        {
            return JsonSerializer.Deserialize<AgentMemoryMetadata>(json, JsonOptions.Loose) ?? new AgentMemoryMetadata();
        }
        catch
        {
            return new AgentMemoryMetadata();
        }
    }

    private static void SaveRaw(RhinoDoc doc, string markdown, AgentMemoryMetadata metadata)
    {
        doc.Strings.SetString(Section, MarkdownKey, markdown);
        SaveMetadata(doc, metadata);
    }

    private static void SaveMetadata(RhinoDoc doc, AgentMemoryMetadata metadata)
    {
        doc.Strings.SetString(Section, MetadataKey, JsonSerializer.Serialize(metadata, JsonOptions.Loose));
    }

    private static void AddSnapshot(AgentMemoryMetadata metadata, string markdown, string promptSummary, string reason)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        metadata.History.Add(new AgentMemorySnapshot(
            DateTimeOffset.UtcNow,
            markdown,
            promptSummary,
            reason,
            AgentMemoryMarkdown.Hash(markdown)));

        if (metadata.History.Count > MaxHistory)
            metadata.History.RemoveRange(0, metadata.History.Count - MaxHistory);
    }

    private static string ResolveExportPath(RhinoDoc doc, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(path);

        var folder = !string.IsNullOrWhiteSpace(doc.Path)
            ? Path.GetDirectoryName(doc.Path)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(folder, "RhinoAgentMemory.md");
    }

    private static void NotifyChanged() => MemoryChanged?.Invoke(null, EventArgs.Empty);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string ShortHash(string hash) =>
        string.IsNullOrWhiteSpace(hash) ? "(none)" : hash[..Math.Min(12, hash.Length)];

    private static string FormatDate(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToString("u") : "(never)";

    private sealed class AgentMemoryMetadata
    {
        public bool Enabled { get; set; } = true;
        public string PromptSummary { get; set; } = "";
        public DateTimeOffset? LastUpdatedUtc { get; set; }
        public string LastUpdateReason { get; set; } = "";
        public string CurrentHash { get; set; } = "";
        public List<AgentMemorySnapshot> History { get; set; } = [];
    }
}
