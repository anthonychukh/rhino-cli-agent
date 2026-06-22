using System.Text;

namespace RhinoAgent.Runtime;

public static class ToolResultFormatter
{
    public static string Format(IEnumerable<ToolExecutionResult> results)
    {
        var sb = new StringBuilder();
        foreach (var result in results)
        {
            sb.AppendLine($"- tool: {result.Tool}");
            sb.AppendLine($"  success: {result.Success}");
            sb.AppendLine($"  approved: {result.WasApproved}");
            sb.AppendLine($"  skipped: {result.WasSkipped}");
            sb.AppendLine("  output:");
            sb.AppendLine(Indent(result.Output));
        }

        return sb.ToString();
    }

    private static string Indent(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "    (empty)";
        return string.Join(Environment.NewLine, value.Split(["\r\n", "\n"], StringSplitOptions.None).Select(line => "    " + line));
    }
}
