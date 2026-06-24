using System.Text;
using Rhino;
using RhinoAgent.Tools;

namespace RhinoAgent.Runtime;

public static class AgentPromptBuilder
{
    public static string Build(
        RhinoDoc doc,
        AgentConfig config,
        IReadOnlyList<(string Role, string Text)> history,
        IReadOnlyList<ToolExecutionResult> toolResults,
        string toolDescriptions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are RhinoAgent, an agent running inside Rhino's command line.");
        sb.AppendLine("Help the user model, inspect, script, and manage the active Rhino document.");
        sb.AppendLine("Be concise in normal prose. Do not mention implementation details unless useful.");
        sb.AppendLine($"Permission mode: {config.PermissionMode}.");
        sb.AppendLine();
        sb.AppendLine("Important command protocol:");
        sb.AppendLine("When you need Rhino or file-system action, emit only this hidden XML-like block after a short user-facing sentence:");
        sb.AppendLine("<rhino-agent>");
        sb.AppendLine("{\"tool_calls\":[{\"tool\":\"run_command\",\"arguments\":{\"command\":\"_Sphere 0,0,0 5\"}}]}");
        sb.AppendLine("</rhino-agent>");
        sb.AppendLine("You may include multiple tool_calls. Do not put tool JSON in markdown fences.");
        sb.AppendLine("After tool results are returned, continue naturally and say what changed.");
        sb.AppendLine("Do not use native Codex app-server tools, shell commands, or web search. Use only the RhinoAgent hidden tool blocks listed below.");
        sb.AppendLine("For prompts containing http or https product URLs, call fetch_url first, then create a practical model from the returned page metadata.");
        sb.AppendLine("After a fetch_url result, do not fetch again and do not write a plan. Immediately emit one execute_csharp tool call that creates a stylized but recognizable model.");
        sb.AppendLine("Keep generated C# scripts simple and bounded. Approximate product proportions when exact dimensions are unavailable.");
        sb.AppendLine("For complex modeling prompts, make the main silhouette first and add distinctive details in the same or next tool round.");
        sb.AppendLine("For geometry creation, prefer execute_csharp with RhinoCommon when a Rhino command would require interactive prompts.");
        sb.AppendLine("In C# scripts, write messages with output.AppendLine(...) or output.WriteLine(...).");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        sb.AppendLine(toolDescriptions);
        sb.AppendLine();
        sb.AppendLine("Current Rhino document:");
        sb.AppendLine(RhinoDocumentSummarizer.Summarize(doc));
        sb.AppendLine();

        if (toolResults.Count > 0)
        {
            sb.AppendLine("Latest tool results:");
            sb.AppendLine(ToolResultFormatter.Format(toolResults));
            sb.AppendLine();
        }

        sb.AppendLine("Conversation:");
        foreach (var (role, text) in history.TakeLast(8))
        {
            sb.AppendLine($"[{role}]");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        sb.AppendLine("[assistant]");
        return sb.ToString();
    }
}
