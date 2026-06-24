using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Rhino;
using RhinoAgent.Runtime;

namespace RhinoAgent.Tools;

public sealed class RhinoToolHost
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

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
            ["fetch_url"] = FetchUrl,
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
        - run_command {"command":"_Sphere 0,0,0 5","echo":false}: run a complete native Rhino command macro with every prompt answered inline.
        - run_python {"script":"..."}: run Rhino Python through RunPythonScript when Rhino exposes that command. If Rhino reports an unknown command, use execute_csharp.
        - execute_csharp {"code":"..."}: run a RhinoCommon C# script with globals doc and output. Prefer this for geometry creation; output supports AppendLine(...) and WriteLine(...).
        - fetch_url {"url":"https://example.com/product","max_chars":2500}: fetch a web page and return compact title, metadata, JSON-LD snippets, and readable text. Use this first for product-page modeling prompts.
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

    private Task<ToolExecutionResult> FetchUrl(ToolCallRequest call)
    {
        var url = GetString(call, "url");
        if (string.IsNullOrWhiteSpace(url))
            return Failure(call.Tool, "Missing url.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Failure(call.Tool, "Only absolute http/https URLs are supported.");

        var maxChars = Math.Clamp(GetInt(call, "max_chars") ?? 2500, 1000, 4000);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = HttpClient.Send(request, HttpCompletionOption.ResponseContentRead, cancellation.Token);
            var content = response.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult();
            var output = SummarizeFetchedPage(uri, response, content, maxChars);
            return Task.FromResult(new ToolExecutionResult(call.Tool, response.IsSuccessStatusCode, output, true, false));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolExecutionResult(call.Tool, false, $"Fetch failed: {ex.Message}", true, false));
        }
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RhinoAgent", "0.1"));
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8");
        return client;
    }

    private static string SummarizeFetchedPage(Uri uri, HttpResponseMessage response, string html, int maxChars)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"url: {uri}");
        builder.AppendLine($"status: {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentType is not null)
            builder.AppendLine($"content_type: {response.Content.Headers.ContentType}");

        AppendIfPresent(builder, "title", ExtractTitle(html));
        AppendIfPresent(builder, "description", ExtractMeta(html, "description"));
        AppendIfPresent(builder, "og_title", ExtractMeta(html, "og:title"));
        AppendIfPresent(builder, "og_description", ExtractMeta(html, "og:description"));

        var jsonLd = ExtractJsonLd(html, 3000);
        if (!string.IsNullOrWhiteSpace(jsonLd))
        {
            builder.AppendLine("json_ld:");
            builder.AppendLine(jsonLd);
        }

        var text = ExtractReadableText(html, maxChars);
        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine("text:");
            builder.AppendLine(text);
        }

        return TrimToMax(builder.ToString().Trim(), maxChars);
    }

    private static void AppendIfPresent(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.AppendLine($"{label}: {value}");
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanText(match.Groups[1].Value, 500) : "";
    }

    private static string ExtractMeta(string html, string name)
    {
        foreach (Match match in Regex.Matches(html, "<meta\\s+[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var tag = match.Value;
            var key = ExtractAttribute(tag, "name");
            if (string.IsNullOrWhiteSpace(key))
                key = ExtractAttribute(tag, "property");
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                continue;

            return CleanText(ExtractAttribute(tag, "content"), 1000);
        }

        return "";
    }

    private static string ExtractAttribute(string tag, string attribute)
    {
        var match = Regex.Match(
            tag,
            $"{Regex.Escape(attribute)}\\s*=\\s*([\"'])(.*?)\\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[2].Value) : "";
    }

    private static string ExtractJsonLd(string html, int maxChars)
    {
        var snippets = new List<string>();
        foreach (Match match in Regex.Matches(
                     html,
                     "<script[^>]+type\\s*=\\s*([\"'])application/ld\\+json\\1[^>]*>(.*?)</script>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var value = CleanText(match.Groups[2].Value, 1500);
            if (!string.IsNullOrWhiteSpace(value))
                snippets.Add(value);
            if (string.Join(Environment.NewLine, snippets).Length >= maxChars)
                break;
        }

        return TrimToMax(string.Join(Environment.NewLine, snippets), maxChars);
    }

    private static string ExtractReadableText(string html, int maxChars)
    {
        var text = Regex.Replace(html, "<script[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<style[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ", RegexOptions.Singleline);
        return CleanText(text, maxChars);
    }

    private static string CleanText(string value, int maxChars)
    {
        var decoded = WebUtility.HtmlDecode(value);
        var compact = Regex.Replace(decoded, "\\s+", " ").Trim();
        return TrimToMax(compact, maxChars);
    }

    private static string TrimToMax(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + Environment.NewLine + $"... truncated {value.Length - maxChars} characters";

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
