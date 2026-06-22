using System.Text.Json;
using Rhino;
using RhinoAgent.Runtime;

namespace RhinoAgent.Tools;

public sealed class RhinoToolHost
{
    private readonly RhinoDoc _doc;
    private readonly AgentConfig _config;
    private readonly Dictionary<string, Func<ToolCallRequest, Task<ToolExecutionResult>>> _tools;
    private readonly HashSet<string> _highImpactTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_command",
        "run_python",
        "execute_csharp",
        "write_file"
    };

    public RhinoToolHost(RhinoDoc doc, AgentConfig config)
    {
        _doc = doc;
        _config = config;
        _tools = new(StringComparer.OrdinalIgnoreCase)
        {
            ["document_summary"] = DocumentSummary,
            ["list_objects"] = ListObjects,
            ["run_command"] = RunCommand,
            ["run_python"] = RunPython,
            ["execute_csharp"] = ExecuteCSharp,
            ["read_file"] = ReadFile,
            ["write_file"] = WriteFile
        };
    }

    public bool HasTool(string name) => _tools.ContainsKey(name);
    public bool IsHighImpact(string name) => _highImpactTools.Contains(name);

    public string DescribeTools() =>
        """
        - document_summary {}: read document name, units, layers, object counts, and bounding box.
        - list_objects {"limit":100}: list active objects with id, name, layer, type, and bounding box.
        - run_command {"command":"_Box 0,0,0 10,10,10","echo":false}: run a native Rhino command string.
        - run_python {"script":"..."}: run Rhino Python through ScriptEditor. Use __rhino_doc__ if available.
        - execute_csharp {"code":"..."}: run a RhinoCommon C# script with globals doc and output.
        - read_file {"path":"relative/or/absolute"}: read a local text file.
        - write_file {"path":"relative/or/absolute","content":"..."}: write a local text file.
        """;

    public Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest call)
    {
        if (!_tools.TryGetValue(call.Tool, out var tool))
            return Task.FromResult(new ToolExecutionResult(call.Tool, false, $"Unknown tool: {call.Tool}", false, true));

        return tool(call);
    }

    private Task<ToolExecutionResult> DocumentSummary(ToolCallRequest call) =>
        Success(call.Tool, RhinoDocumentSummarizer.Summarize(_doc));

    private Task<ToolExecutionResult> ListObjects(ToolCallRequest call)
    {
        var limit = GetInt(call, "limit") ?? 100;
        var objects = _doc.Objects
            .Where(o => !o.IsDeleted)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(o => new
            {
                id = o.Id,
                name = o.Name ?? "",
                layer = _doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "",
                type = o.Geometry?.GetType().Name ?? o.ObjectType.ToString(),
                bbox = o.Geometry?.GetBoundingBox(true).ToString()
            });

        return Success(call.Tool, JsonSerializer.Serialize(objects, JsonOptions.Loose));
    }

    private Task<ToolExecutionResult> RunCommand(ToolCallRequest call)
    {
        var command = GetString(call, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Failure(call.Tool, "Missing command.");

        var echo = GetBool(call, "echo") ?? false;
        var previousCapture = RhinoApp.CommandWindowCaptureEnabled;
        RhinoApp.CommandWindowCaptureEnabled = true;
        var ran = false;
        string[] lines = [];
        Exception? error = null;

        try
        {
            try
            {
                ran = RhinoApp.RunScript(_doc.RuntimeSerialNumber, command, echo);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            lines = RhinoApp.CapturedCommandWindowStrings(true) ?? [];
        }
        finally
        {
            RhinoApp.CommandWindowCaptureEnabled = previousCapture;
        }

        var output = string.Join(Environment.NewLine, lines);
        if (error is not null)
            output += $"{Environment.NewLine}Exception: {error.Message}";

        return Task.FromResult(new ToolExecutionResult(call.Tool, ran && error is null, output.Trim(), true, false));
    }

    private Task<ToolExecutionResult> RunPython(ToolCallRequest call)
    {
        var script = GetString(call, "script");
        if (string.IsNullOrWhiteSpace(script))
            return Failure(call.Tool, "Missing script.");

        var tmp = Path.Combine(Path.GetTempPath(), $"rhino_agent_{Guid.NewGuid():N}.py");
        File.WriteAllText(tmp, script);
        var wrapped = $"_-RunPythonScript \"{tmp}\"";
        var toolCall = new ToolCallRequest
        {
            Tool = call.Tool,
            Arguments = new Dictionary<string, object?> { ["command"] = wrapped, ["echo"] = false }
        };

        try
        {
            return RunCommand(toolCall);
        }
        finally
        {
            _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => TryDelete(tmp), TaskScheduler.Default);
        }
    }

    private async Task<ToolExecutionResult> ExecuteCSharp(ToolCallRequest call)
    {
        var code = GetString(call, "code");
        if (string.IsNullOrWhiteSpace(code))
            return new ToolExecutionResult(call.Tool, false, "Missing code.", true, false);

        return await CSharpScriptRunner.ExecuteAsync(call.Tool, _doc, code);
    }

    private Task<ToolExecutionResult> ReadFile(ToolCallRequest call)
    {
        var path = ResolvePath(GetString(call, "path"));
        if (path is null || !File.Exists(path))
            return Failure(call.Tool, "File not found.");

        return Success(call.Tool, File.ReadAllText(path));
    }

    private Task<ToolExecutionResult> WriteFile(ToolCallRequest call)
    {
        var path = ResolvePath(GetString(call, "path"));
        var content = GetString(call, "content");
        if (path is null)
            return Failure(call.Tool, "Missing path.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content ?? "");
        return Success(call.Tool, $"Wrote {path}");
    }

    private string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var baseDir = Providers.WorkingDirectoryResolver.Resolve(_doc, _config.WorkingDirectory);
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    private static Task<ToolExecutionResult> Success(string tool, string output) =>
        Task.FromResult(new ToolExecutionResult(tool, true, output, true, false));

    private static Task<ToolExecutionResult> Failure(string tool, string output) =>
        Task.FromResult(new ToolExecutionResult(tool, false, output, true, false));

    private static string? GetString(ToolCallRequest call, string key) =>
        call.Arguments.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static int? GetInt(ToolCallRequest call, string key)
    {
        if (!call.Arguments.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
            return i;
        return int.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
    }

    private static bool? GetBool(ToolCallRequest call, string key)
    {
        if (!call.Arguments.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is JsonElement el && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            return el.GetBoolean();
        return bool.TryParse(Convert.ToString(value), out var parsed) ? parsed : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Rhino may still be reading the script; temporary cleanup is best-effort.
        }
    }
}
