using System.Runtime.InteropServices;
using System.Text.Json;
using Rhino;
using Rhino.Commands;
using RhinoAgent.Config;
using RhinoAgent.Runtime;

namespace RhinoAgent.Commands;

[Guid("081AC702-8BC1-4D96-AF64-FBE521317480")]
public sealed class AgentProviderSelfTestCommand : Command
{
    public override string EnglishName => "AgentProviderSelfTest";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var outputPath = GetOutputPath();
        var progressPath = GetProgressPath();
        ResetProgress(progressPath);
        WriteProgress(progressPath, "started");
        var config = CreateSmokeConfig(AgentConfigStore.Load());
        WriteProgress(progressPath, "config-loaded");
        var services = AgentServices.Create(config, doc);
        WriteProgress(progressPath, "services-created");
        var provider = services.ProviderFactory.ResolveInteractiveProvider(config);
        WriteProgress(progressPath, provider is null ? "provider-resolution-none" : $"provider-resolved: {provider.DisplayName}");

        if (provider is null)
        {
            WriteResult(outputPath, new
            {
                ok = false,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                error = "No logged-in Claude or Codex CLI provider was detected."
            });
            RhinoApp.WriteLine($"RhinoAgent provider self-test failed; see {outputPath}");
            return Result.Failure;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var session = new AgentSession(doc, config, provider, services.ToolHost, services.Approvals);
            WriteProgress(progressPath, "session-created");
            var result = session.RunUserTurnAsync(
                    "Provider smoke test. Reply exactly RHINO_AGENT_PROVIDER_OK and do not call tools.",
                    cancellation.Token,
                    message => WriteProgress(progressPath, message))
                .GetAwaiter()
                .GetResult();
            WriteProgress(progressPath, $"turn-complete: {result.Success}");

            var ok = result.Success
                && result.VisibleText.Contains("RHINO_AGENT_PROVIDER_OK", StringComparison.OrdinalIgnoreCase);

            WriteResult(outputPath, new
            {
                ok,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                provider = result.Provider,
                model = result.Model,
                sessionId = result.SessionId,
                usage = result.Usage,
                providerExitCode = result.ProviderExitCode,
                standardError = result.StandardError,
                visibleText = result.VisibleText,
                toolCallCount = result.ToolCallCount,
                toolResultCount = result.ToolResultCount,
                stoppedAfterToolLimit = result.StoppedAfterToolLimit,
                error = result.Error
            });

            RhinoApp.WriteLine(ok
                ? $"RhinoAgent provider self-test wrote {outputPath}"
                : $"RhinoAgent provider self-test failed; see {outputPath}");
            return ok ? Result.Success : Result.Failure;
        }
        catch (OperationCanceledException)
        {
            WriteProgress(progressPath, "timeout");
            WriteResult(outputPath, new
            {
                ok = false,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                provider = provider.DisplayName,
                error = "Provider self-test timed out."
            });
            RhinoApp.WriteLine($"RhinoAgent provider self-test timed out; see {outputPath}");
            return Result.Failure;
        }
        catch (Exception ex)
        {
            WriteProgress(progressPath, $"exception: {ex.GetType().Name}: {ex.Message}");
            WriteResult(outputPath, new
            {
                ok = false,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                provider = provider.DisplayName,
                error = ex.Message
            });
            RhinoApp.WriteLine($"RhinoAgent provider self-test failed; see {outputPath}");
            return Result.Failure;
        }
    }

    public static string GetOutputPath() =>
        Path.Combine(Path.GetTempPath(), "RhinoAgent", "provider-self-test.json");

    public static string GetProgressPath() =>
        Path.Combine(Path.GetTempPath(), "RhinoAgent", "provider-self-test.log");

    private static AgentConfig CreateSmokeConfig(AgentConfig source) =>
        new()
        {
            Provider = source.Provider,
            PermissionMode = AgentPermissionMode.Ask,
            ClaudeModel = source.ClaudeModel,
            CodexModel = source.CodexModel,
            ClaudePath = source.ClaudePath,
            CodexPath = source.CodexPath,
            WorkingDirectory = source.WorkingDirectory,
            MaxToolRounds = 1
        };

    private static void WriteResult(string outputPath, object payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
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
