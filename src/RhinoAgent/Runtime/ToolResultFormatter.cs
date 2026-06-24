using System.Text;

namespace RhinoAgent.Runtime;

public static class ToolResultFormatter
{
    private const int MaxOutputChars = 3000;

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
            sb.AppendLine(Indent(TrimOutput(result.Output)));
        }

        return sb.ToString();
    }

    private static string Indent(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "    (empty)";
        return string.Join(Environment.NewLine, value.Split(["\r\n", "\n"], StringSplitOptions.None).Select(line => "    " + line));
    }

    private static string TrimOutput(string value)
    {
        if (value.Length <= MaxOutputChars)
            return value;

        return value[..MaxOutputChars] + Environment.NewLine + $"... truncated {value.Length - MaxOutputChars} characters";
    }
}
