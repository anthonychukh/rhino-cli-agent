using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhinoAgent.Runtime;

public static partial class AgentResponseParser
{
    public static ParsedAgentText Parse(string text)
    {
        var calls = new List<ToolCallRequest>();
        var visible = ToolBlockRegex().Replace(text, match =>
        {
            var json = match.Groups["json"].Value.Trim();
            calls.AddRange(ParseCalls(json));
            return "";
        });

        return new ParsedAgentText(visible.Trim(), calls);
    }

    private static IEnumerable<ToolCallRequest> ParseCalls(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ToolCallEnvelope>(json, JsonOptions.Loose);
            if (envelope?.ToolCalls is { Count: > 0 })
                return envelope.ToolCalls.Where(c => !string.IsNullOrWhiteSpace(c.Tool)).ToArray();

            var single = JsonSerializer.Deserialize<ToolCallRequest>(json, JsonOptions.Loose);
            if (single is not null && !string.IsNullOrWhiteSpace(single.Tool))
                return [single];
        }
        catch
        {
            return [];
        }

        return [];
    }

    [GeneratedRegex("<rhino-agent>(?<json>.*?)</rhino-agent>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ToolBlockRegex();
}
