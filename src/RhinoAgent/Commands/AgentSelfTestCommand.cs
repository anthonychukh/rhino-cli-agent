using System.Runtime.InteropServices;
using System.Text.Json;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using RhinoAgent.Config;
using RhinoAgent.Runtime;
using RhinoAgent.Tools;

namespace RhinoAgent.Commands;

[Guid("CC864274-DB30-412A-949B-3EEFE9C57141")]
public sealed class AgentSelfTestCommand : Command
{
    public override string EnglishName => "AgentSelfTest";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var outputPath = GetOutputPath();
        var registeredCommands = Command.GetCommandNames(true, true)
            .Where(name => name.StartsWith("Agent", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name)
            .ToArray();
        var parsed = AgentResponseParser.Parse(
            """
            visible response
            <rhino-agent>{"tool_calls":[{"tool":"document_summary","arguments":{}}]}</rhino-agent>
            """);

        var success = parsed.VisibleText == "visible response"
            && parsed.ToolCalls.Count == 1
            && parsed.ToolCalls[0].Tool == "document_summary";

        var lineRecognized = AgentCommand.TryResolveManualRhinoCommand("Line", out var lineScript, out var lineMatchedBy)
            && lineMatchedBy == "command"
            && lineScript == "Line";
        var prefixedRecognized = AgentCommand.TryResolveManualRhinoCommand("_Circle 0,0,0 5", out var prefixedScript, out var prefixedMatchedBy)
            && prefixedMatchedBy == "prefix"
            && prefixedScript == "_Circle 0,0,0 5";
        var aliasNames = GetAliasNames();
        var aliasProbe = aliasNames.FirstOrDefault();
        var aliasRecognized = aliasProbe is null
            || AgentCommand.TryResolveManualRhinoCommand(aliasProbe, out _, out var aliasMatchedBy) && aliasMatchedBy == "alias";
        success = success && lineRecognized && prefixedRecognized && aliasRecognized;

        var payload = new
        {
            ok = success,
            command = EnglishName,
            timestampUtc = DateTimeOffset.UtcNow,
            rhinoVersion = RhinoApp.Version?.ToString(),
            pluginAssembly = typeof(RhinoAgentPlugin).Assembly.GetName().Version?.ToString(),
            configPath = AgentConfigStore.ConfigPath,
            document = new
            {
                name = doc.Name ?? "(unsaved)",
                path = doc.Path ?? "(unsaved)",
                units = doc.ModelUnitSystem.ToString(),
                objectCount = doc.Objects.Count,
                layerCount = doc.Layers.Count,
                summary = RhinoDocumentSummarizer.Summarize(doc)
            },
            parser = new
            {
                visibleText = parsed.VisibleText,
                toolCallCount = parsed.ToolCalls.Count,
                firstTool = parsed.ToolCalls.FirstOrDefault()?.Tool
            },
            manualCommandRouting = new
            {
                lineRecognized,
                lineMatchedBy,
                lineScript,
                prefixedRecognized,
                prefixedMatchedBy,
                prefixedScript,
                aliasCount = aliasNames.Length,
                aliasProbe,
                aliasRecognized
            },
            commands = new[]
            {
                "Agent",
                "AgentLogin",
                "AgentStatus",
                "AgentConfig",
                "AgentSelfTest",
                "AgentProviderSelfTest"
            },
            registeredCommands
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        RhinoApp.WriteLine(success
            ? $"RhinoAgent self-test wrote {outputPath}"
            : $"RhinoAgent self-test failed; see {outputPath}");

        return success ? Result.Success : Result.Failure;
    }

    public static string GetOutputPath() =>
        Path.Combine(Path.GetTempPath(), "RhinoAgent", "self-test.json");

    private static string[] GetAliasNames()
    {
        try
        {
            return CommandAliasList.GetNames() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
