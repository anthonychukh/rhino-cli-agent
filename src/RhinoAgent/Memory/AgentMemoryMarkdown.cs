using System.Security.Cryptography;
using System.Text;

namespace RhinoAgent.Memory;

public static class AgentMemoryMarkdown
{
    public const string AgentNotesBegin = "<!-- RHINOAGENT:AGENT_NOTES_BEGIN -->";
    public const string AgentNotesEnd = "<!-- RHINOAGENT:AGENT_NOTES_END -->";
    public const int PromptMemoryCharacterLimit = 8000;

    public static string CreateTemplate(string documentName)
    {
        var title = string.IsNullOrWhiteSpace(documentName) ? "Rhino Document" : documentName.Trim();
        return $$"""
        # RhinoAgent Memory: {{title}}

        ## Project Intent
        - 

        ## Modeling Conventions
        - Use the Rhino document units unless the user says otherwise.
        - Keep generated geometry organized by layer and name when practical.

        ## Constraints
        - 

        ## Decisions
        - 

        ## Current Tasks
        - 

        ## Agent Notes
        {{AgentNotesBegin}}
        - No generated notes yet.
        {{AgentNotesEnd}}
        """;
    }

    public static string ReplaceAgentNotes(string markdown, string agentNotes)
    {
        markdown = EnsureAgentNotesSection(markdown);
        var begin = markdown.IndexOf(AgentNotesBegin, StringComparison.Ordinal);
        var end = markdown.IndexOf(AgentNotesEnd, StringComparison.Ordinal);
        if (begin < 0 || end < begin)
            return markdown;

        var replacement = NormalizeAgentNotes(agentNotes);
        var before = markdown[..(begin + AgentNotesBegin.Length)].TrimEnd();
        var after = markdown[end..].TrimStart();
        return $"{before}{Environment.NewLine}{replacement}{Environment.NewLine}{after}".Trim() + Environment.NewLine;
    }

    public static string EnsureAgentNotesSection(string markdown)
    {
        markdown = string.IsNullOrWhiteSpace(markdown) ? CreateTemplate("Rhino Document") : markdown.TrimEnd();
        if (markdown.Contains(AgentNotesBegin, StringComparison.Ordinal)
            && markdown.Contains(AgentNotesEnd, StringComparison.Ordinal))
            return markdown + Environment.NewLine;

        return $$"""
        {{markdown}}

        ## Agent Notes
        {{AgentNotesBegin}}
        - No generated notes yet.
        {{AgentNotesEnd}}
        """;
    }

    public static string BuildPromptSummary(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "No RhinoAgent memory has been written yet.";

        var lines = markdown
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0
                && !line.StartsWith("<!-- RHINOAGENT:", StringComparison.OrdinalIgnoreCase))
            .Take(80);
        var compact = string.Join(Environment.NewLine, lines);
        return Truncate(compact, 2400);
    }

    public static string Hash(string markdown)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(markdown ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;
        return value[..maxChars].TrimEnd() + Environment.NewLine + $"... truncated {value.Length - maxChars} characters";
    }

    private static string NormalizeAgentNotes(string agentNotes)
    {
        if (string.IsNullOrWhiteSpace(agentNotes))
            return "- No generated notes yet.";

        var normalized = agentNotes.Trim();
        if (normalized.StartsWith(AgentNotesBegin, StringComparison.Ordinal)
            || normalized.StartsWith(AgentNotesEnd, StringComparison.Ordinal)
            || normalized.Contains("\n## ", StringComparison.Ordinal))
        {
            normalized = normalized
                .Replace(AgentNotesBegin, "", StringComparison.Ordinal)
                .Replace(AgentNotesEnd, "", StringComparison.Ordinal)
                .Trim();
        }

        return string.IsNullOrWhiteSpace(normalized) ? "- No generated notes yet." : normalized;
    }
}
