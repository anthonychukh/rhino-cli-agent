using Rhino;

namespace RhinoAgent.Memory;

public static class AgentMemoryPromptFormatter
{
    public static string FormatForPrompt(RhinoDoc doc)
    {
        var state = AgentMemoryStore.EnsureCreated(doc);
        if (!state.Enabled)
            return "RhinoAgent document memory is disabled for this file.";

        if (state.Markdown.Length <= AgentMemoryMarkdown.PromptMemoryCharacterLimit)
        {
            return string.Join(Environment.NewLine,
            [
                "Embedded RhinoAgent memory (AGENTS.md-style, stored in this .3dm):",
                "```markdown",
                state.Markdown.Trim(),
                "```"
            ]);
        }

        return string.Join(Environment.NewLine,
        [
            "Embedded RhinoAgent memory is larger than the prompt limit.",
            "Use this compact memory summary. The full memory is available through /memory show or the AgentMemory panel.",
            "```text",
            state.PromptSummary.Trim(),
            "```"
        ]);
    }
}
