using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rhino;
using Rhino.Commands;
using RhinoAgent.Config;
using RhinoAgent.Runtime;

namespace RhinoAgent.Commands;

[Guid("BFC9488D-D296-4B1B-8783-822701574A5A")]
public sealed class AgentPromptSelfTestCommand : Command
{
    private const string YetiPrompt =
        "Model this yeti cup https://www.yeti.com/drinkware/hydration/21071508616.html?utm_content=PMAX_Bottles&gad_source=1&gad_campaignid=17326787701&gbraid=0AAAAADI2_4_2v18ZUSZ_Nth2CLr_9OvVs&gclid=Cj0KCQjwoMXQBhDcARIsAH-eEtuSTy5aenJL9RaKXyoI8WKKzlkHAIYRB-UNerk0bEdtFeX6jARPdhUaAsTcEALw_wcB";

    public override string EnglishName => "AgentPromptSelfTest";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var outputPath = GetOutputPath();
        var progressPath = GetProgressPath();
        ResetProgress(progressPath);
        WriteProgress(progressPath, "started");

        var config = AgentConfigStore.Load();
        var timeoutSeconds = Math.Max(15, config.ProviderTurnTimeoutSeconds > 0 ? config.ProviderTurnTimeoutSeconds : 180);
        var services = AgentServices.Create(config, doc);
        var provider = services.ProviderFactory.ResolveInteractiveProvider(config);
        WriteProgress(progressPath, provider is null ? "provider-resolution-none" : $"provider-resolved: {provider.DisplayName}");

        if (provider is null)
        {
            WriteResult(outputPath, new
            {
                ok = false,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                prompt = YetiPrompt,
                progressPath,
                error = "No logged-in Claude or Codex CLI provider was detected."
            });
            RhinoApp.WriteLine($"RhinoAgent prompt self-test failed; see {outputPath}");
            return Result.Failure;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var providerScope = provider;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var session = new AgentSession(doc, config, provider, services.ToolHost, services.Approvals);
            WriteProgress(progressPath, $"session-created; timeout={timeoutSeconds}s");
            var result = session.RunUserTurnAsync(
                    YetiPrompt,
                    cancellation.Token,
                    message => WriteProgress(progressPath, message))
                .GetAwaiter()
                .GetResult();
            stopwatch.Stop();
            WriteProgress(progressPath, $"turn-complete: success={result.Success}; elapsedMs={stopwatch.ElapsedMilliseconds}");

            var ok = result.Success
                && result.ProviderExitCode == 0
                && result.ToolCallCount > 0
                && result.ToolResultCount > 0
                && !result.StoppedAfterToolLimit;

            WriteResult(outputPath, new
            {
                ok,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                prompt = YetiPrompt,
                timeoutSeconds,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                provider = result.Provider,
                processMode = provider.ProcessMode,
                model = result.Model,
                sessionId = result.SessionId,
                usage = result.Usage,
                providerExitCode = result.ProviderExitCode,
                standardError = result.StandardError,
                visibleText = result.VisibleText,
                toolCallCount = result.ToolCallCount,
                toolResultCount = result.ToolResultCount,
                stoppedAfterToolLimit = result.StoppedAfterToolLimit,
                error = result.Error,
                progressPath
            });

            RhinoApp.WriteLine(ok
                ? $"RhinoAgent prompt self-test wrote {outputPath}"
                : $"RhinoAgent prompt self-test failed; see {outputPath}");
            return ok ? Result.Success : Result.Failure;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            WriteProgress(progressPath, $"timeout; elapsedMs={stopwatch.ElapsedMilliseconds}");
            WriteResult(outputPath, new
            {
                ok = false,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                prompt = YetiPrompt,
                timeoutSeconds,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                provider = provider.DisplayName,
                processMode = provider.ProcessMode,
                error = "Provider turn timed out.",
                progressPath
            });
            RhinoApp.WriteLine($"RhinoAgent prompt self-test timed out; see {outputPath}");
            return Result.Failure;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            WriteProgress(progressPath, $"exception: {ex.GetType().Name}: {ex.Message}");
            WriteResult(outputPath, new
            {
                ok = false,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                prompt = YetiPrompt,
                timeoutSeconds,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                provider = provider.DisplayName,
                processMode = provider.ProcessMode,
                error = ex.Message,
                progressPath
            });
            RhinoApp.WriteLine($"RhinoAgent prompt self-test failed; see {outputPath}");
            return Result.Failure;
        }
    }

    public static string GetOutputPath() =>
        Path.Combine(Path.GetTempPath(), "RhinoAgent", "prompt-self-test.json");

    public static string GetProgressPath() =>
        Path.Combine(Path.GetTempPath(), "RhinoAgent", "prompt-self-test.log");

    private static void WriteResult(string outputPath, object payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        }));
    }

    private static void ResetProgress(string progressPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
        File.WriteAllText(progressPath, "");
    }

    private static void WriteProgress(string progressPath, string message)
    {
        File.AppendAllText(progressPath, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
    }
}
