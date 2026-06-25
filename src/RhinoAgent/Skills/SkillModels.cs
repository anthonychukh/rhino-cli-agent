using System.Text.Json.Serialization;

namespace RhinoAgent.Skills;

public sealed record SkillInfo(
    string Name,
    string Description,
    string Directory,
    bool Enabled,
    IReadOnlyList<string> ResourceDirectories);

public sealed record SkillContext(
    string Name,
    string Description,
    string Directory,
    string SkillMarkdown);

public sealed record SkillOperationResult(
    bool Success,
    string Message,
    SkillInfo? Skill = null,
    IReadOnlyList<string>? Files = null);

public sealed class SkillWriteRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; set; }

    [JsonPropertyName("files")]
    public List<SkillFileSpec> Files { get; set; } = [];
}

public sealed class SkillFileSpec
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("source_path")]
    public string? SourcePath { get; set; }
}

