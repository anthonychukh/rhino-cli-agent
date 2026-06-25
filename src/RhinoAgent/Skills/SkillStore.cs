using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RhinoAgent.Config;

namespace RhinoAgent.Skills;

public sealed class SkillStore
{
    private static readonly Regex SkillNameRegex = new("^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _rootDirectory;
    private readonly string _statePath;

    public SkillStore(string? rootDirectory = null)
    {
        _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AgentConfigStore.ConfigDirectory, "skills")
            : Path.GetFullPath(rootDirectory);
        _statePath = Path.Combine(Path.GetDirectoryName(_rootDirectory) ?? _rootDirectory, "skills-state.json");
    }

    public string RootDirectory => _rootDirectory;

    public IReadOnlyList<SkillInfo> ListSkills()
    {
        Directory.CreateDirectory(_rootDirectory);
        var disabled = LoadState().DisabledSkills;
        return Directory.EnumerateDirectories(_rootDirectory)
            .Select(TryReadSkillInfo)
            .Where(info => info is not null)
            .Select(info => info!)
            .Select(info => info with
            {
                Enabled = !disabled.Contains(info.Name, StringComparer.OrdinalIgnoreCase)
            })
            .OrderByDescending(info => info.Enabled)
            .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SkillInfo? GetSkill(string name)
    {
        name = NormalizeName(name);
        if (!IsValidName(name))
            return null;

        var directory = Path.Combine(_rootDirectory, name);
        var info = TryReadSkillInfo(directory);
        if (info is null)
            return null;

        var disabled = LoadState().DisabledSkills;
        return info with
        {
            Enabled = !disabled.Contains(info.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    public IReadOnlyList<SkillContext> SelectRelevantSkills(
        string prompt,
        int limit,
        IEnumerable<string>? forcedSkillNames = null)
    {
        var skills = ListSkills()
            .Where(skill => skill.Enabled)
            .ToArray();
        var forced = (forcedSkillNames ?? [])
            .Select(NormalizeName)
            .Where(IsValidName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var scored = new List<(SkillInfo Skill, int Score)>();
        foreach (var skill in skills)
        {
            var forceScore = forced.Contains(skill.Name, StringComparer.OrdinalIgnoreCase) ? 10_000 : 0;
            var score = forceScore + ScoreSkill(prompt, skill);
            if (score > 0)
                scored.Add((skill, score));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 10))
            .Select(item => LoadContext(item.Skill))
            .Where(context => context is not null)
            .Select(context => context!)
            .ToArray();
    }

    public SkillOperationResult SaveSkill(SkillWriteRequest request, bool update)
    {
        request.Name = NormalizeName(request.Name);
        if (!IsValidName(request.Name))
            return Fail("Skill name must use lowercase letters, digits, and hyphens only, and be 2-64 characters.");

        var target = Path.Combine(_rootDirectory, request.Name);
        var exists = Directory.Exists(target);
        if (!update && exists && !request.Overwrite)
            return Fail($"Skill already exists: {request.Name}. Set overwrite=true to replace it.");
        if (update && !exists)
            return Fail($"Skill does not exist: {request.Name}.");
        if (request.Files.Count == 0)
            return Fail("Skill write request must include at least one file.");

        Directory.CreateDirectory(_rootDirectory);
        var temp = Path.Combine(_rootDirectory, $".pending-{request.Name}-{Guid.NewGuid():N}");
        var backup = Path.Combine(_rootDirectory, $".backup-{request.Name}-{Guid.NewGuid():N}");
        var written = new List<string>();

        try
        {
            if (update)
                CopyDirectory(target, temp);
            else
                Directory.CreateDirectory(temp);

            foreach (var file in request.Files)
            {
                var safePath = ResolveSkillFilePath(temp, file.Path);
                if (safePath is null)
                    return Fail($"Unsafe skill file path: {file.Path}");

                Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
                if (!string.IsNullOrWhiteSpace(file.SourcePath))
                {
                    var source = Path.GetFullPath(file.SourcePath);
                    if (File.Exists(source))
                    {
                        File.Copy(source, safePath, overwrite: true);
                    }
                    else if (Directory.Exists(source))
                    {
                        CopyDirectory(source, safePath);
                    }
                    else
                    {
                        return Fail($"Source path was not found: {file.SourcePath}");
                    }
                }
                else
                {
                    File.WriteAllText(safePath, file.Content ?? "", Encoding.UTF8);
                }

                written.Add(NormalizeRelativePath(file.Path));
            }

            var validation = ValidateSkillDirectory(temp, request.Name, request.Description);
            if (!validation.Success)
                return validation;

            if (exists)
                Directory.Move(target, backup);
            Directory.Move(temp, target);
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);

            var info = GetSkill(request.Name);
            return new SkillOperationResult(true, $"Saved skill {request.Name}", info, written);
        }
        catch (Exception ex)
        {
            if (Directory.Exists(backup) && !Directory.Exists(target))
                Directory.Move(backup, target);
            return Fail($"Skill write failed: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(temp);
            TryDeleteDirectory(backup);
        }
    }

    public SkillOperationResult DeleteSkill(string name)
    {
        name = NormalizeName(name);
        if (!IsValidName(name))
            return Fail("Invalid skill name.");

        var directory = Path.Combine(_rootDirectory, name);
        if (!Directory.Exists(directory))
            return Fail($"Skill not found: {name}");

        Directory.Delete(directory, recursive: true);
        var state = LoadState();
        state.DisabledSkills.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        SaveState(state);
        return new SkillOperationResult(true, $"Deleted skill {name}");
    }

    public SkillOperationResult SetEnabled(string name, bool enabled)
    {
        name = NormalizeName(name);
        var info = GetSkill(name);
        if (info is null)
            return Fail($"Skill not found: {name}");

        var state = LoadState();
        state.DisabledSkills.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
            state.DisabledSkills.Add(name);
        SaveState(state);

        return new SkillOperationResult(true, $"{(enabled ? "Enabled" : "Disabled")} skill {name}", GetSkill(name));
    }

    public SkillOperationResult ExportSkill(string name, string destinationFolder, bool overwrite)
    {
        name = NormalizeName(name);
        var info = GetSkill(name);
        if (info is null)
            return Fail($"Skill not found: {name}");

        var destinationRoot = Path.GetFullPath(destinationFolder);
        var destination = Path.Combine(destinationRoot, name);
        if (Directory.Exists(destination))
        {
            if (!overwrite)
                return Fail($"Export destination already exists: {destination}");
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destinationRoot);
        CopyDirectory(info.Directory, destination);
        return new SkillOperationResult(true, $"Exported skill {name} to {destination}", info);
    }

    public SkillOperationResult ReadSkillFile(string name, string relativePath, int maxChars)
    {
        name = NormalizeName(name);
        var info = GetSkill(name);
        if (info is null)
            return Fail($"Skill not found: {name}");

        relativePath = string.IsNullOrWhiteSpace(relativePath) ? "SKILL.md" : relativePath;
        var path = ResolveSkillFilePath(info.Directory, relativePath);
        if (path is null || !File.Exists(path))
            return Fail($"Skill file not found: {relativePath}");

        var content = File.ReadAllText(path);
        if (content.Length > maxChars)
            content = content[..maxChars] + Environment.NewLine + $"... truncated {content.Length - maxChars} characters";
        return new SkillOperationResult(true, content, info, [NormalizeRelativePath(relativePath)]);
    }

    public string BuildSkillListSummary()
    {
        var skills = ListSkills();
        if (skills.Count == 0)
            return $"No RhinoAgent skills found. Root: {_rootDirectory}";

        var lines = new List<string> { $"RhinoAgent skills ({skills.Count})", $"  Root: {_rootDirectory}" };
        foreach (var skill in skills)
        {
            var status = skill.Enabled ? "enabled" : "disabled";
            lines.Add($"  - {skill.Name} ({status}): {skill.Description}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string NormalizeName(string value)
    {
        value = value.Trim().ToLowerInvariant();
        value = Regex.Replace(value, "[^a-z0-9-]+", "-");
        value = Regex.Replace(value, "-+", "-").Trim('-');
        return value;
    }

    private static bool IsValidName(string value) =>
        SkillNameRegex.IsMatch(value);

    private static int ScoreSkill(string prompt, SkillInfo skill)
    {
        var haystack = prompt.ToLowerInvariant();
        var score = 0;
        if (haystack.Contains(skill.Name, StringComparison.OrdinalIgnoreCase)
            || haystack.Contains("$" + skill.Name, StringComparison.OrdinalIgnoreCase))
            score += 100;

        foreach (var token in Tokenize(skill.Name + " " + skill.Description).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += token.Length >= 8 ? 4 : 2;
        }

        return score;
    }

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]{3,}")
            .Select(match => match.Value)
            .Where(token => !CommonWords.Contains(token));

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "when", "use", "uses",
        "using", "skill", "skills", "agent", "rhinoagent", "create", "work", "task"
    };

    private SkillContext? LoadContext(SkillInfo info)
    {
        var path = Path.Combine(info.Directory, "SKILL.md");
        if (!File.Exists(path))
            return null;

        var markdown = File.ReadAllText(path);
        return new SkillContext(info.Name, info.Description, info.Directory, markdown);
    }

    private SkillInfo? TryReadSkillInfo(string directory)
    {
        var skillPath = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(skillPath))
            return null;

        var frontmatter = ParseFrontmatter(File.ReadAllText(skillPath));
        if (!frontmatter.TryGetValue("name", out var name) || !IsValidName(name))
            return null;
        if (!frontmatter.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            return null;

        var resourceDirs = new[] { "references", "scripts", "assets", "agents" }
            .Where(dir => Directory.Exists(Path.Combine(directory, dir)))
            .ToArray();
        return new SkillInfo(name, description, Path.GetFullPath(directory), true, resourceDirs);
    }

    private static SkillOperationResult ValidateSkillDirectory(string directory, string expectedName, string requestDescription)
    {
        var skillPath = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(skillPath))
            return Fail("Skill must include SKILL.md.");

        var markdown = File.ReadAllText(skillPath);
        var frontmatter = ParseFrontmatter(markdown);
        if (!frontmatter.TryGetValue("name", out var name) || !IsValidName(name))
            return Fail("SKILL.md frontmatter must include a valid name.");
        if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
            return Fail($"SKILL.md name '{name}' must match folder name '{expectedName}'.");
        if (!frontmatter.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            return Fail("SKILL.md frontmatter must include a non-empty description.");
        if (markdown.Length < 80)
            return Fail("SKILL.md is too small to be useful.");

        _ = requestDescription;
        return new SkillOperationResult(true, "Skill validation passed");
    }

    private static Dictionary<string, string> ParseFrontmatter(string markdown)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(markdown);
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
            return result;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line == "---")
                break;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            result[key] = value;
        }

        return result;
    }

    private static string? ResolveSkillFilePath(string skillDirectory, string relativePath)
    {
        relativePath = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/').Any(part => part == ".." || part.Length == 0))
            return null;

        var root = Path.GetFullPath(skillDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Trim().Replace('\\', '/').TrimStart('/');

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private SkillState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
                return new SkillState();
            return JsonSerializer.Deserialize<SkillState>(File.ReadAllText(_statePath), JsonOptions) ?? new SkillState();
        }
        catch
        {
            return new SkillState();
        }
    }

    private void SaveState(SkillState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
    }

    private static SkillOperationResult Fail(string message) =>
        new(false, message);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temporary skill writes.
        }
    }

    private sealed class SkillState
    {
        public List<string> DisabledSkills { get; set; } = [];
    }
}

