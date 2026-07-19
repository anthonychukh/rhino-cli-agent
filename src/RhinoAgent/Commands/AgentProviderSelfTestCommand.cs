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
        using var services = AgentServices.Create(config, doc);
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
            using var providerScope = provider;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var session = new AgentSession(doc, config, provider, services.ToolHost, services.Approvals, services.SkillStore);
            WriteProgress(progressPath, "session-created");
            var firstResult = session.RunUserTurnAsync(
                    "Provider smoke test. Reply exactly RHINO_AGENT_PROVIDER_OK and do not call tools.",
                    cancellation.Token,
                    message => WriteProgress(progressPath, message))
                .GetAwaiter()
                .GetResult();
            WriteProgress(progressPath, $"first-turn-complete: {firstResult.Success}");

            var secondResult = session.RunUserTurnAsync(
                    "Provider smoke test continuation. Reply exactly RHINO_AGENT_PROVIDER_OK_2 and do not call tools.",
                    cancellation.Token,
                    message => WriteProgress(progressPath, message))
                .GetAwaiter()
                .GetResult();
            WriteProgress(progressPath, $"second-turn-complete: {secondResult.Success}");

            var sameProviderSession = string.Equals(
                firstResult.SessionId,
                secondResult.SessionId,
                StringComparison.Ordinal);
            var requiresSameSession = provider.ProcessMode == AgentProviderProcessMode.LongRunning;
            var ok = firstResult.Success
                && secondResult.Success
                && firstResult.VisibleText.Contains("RHINO_AGENT_PROVIDER_OK", StringComparison.OrdinalIgnoreCase)
                && secondResult.VisibleText.Contains("RHINO_AGENT_PROVIDER_OK_2", StringComparison.OrdinalIgnoreCase)
                && (!requiresSameSession || sameProviderSession);

            WriteResult(outputPath, new
            {
                ok,
                command = EnglishName,
                timestampUtc = DateTimeOffset.UtcNow,
                provider = secondResult.Provider,
                processMode = provider.ProcessMode,
                model = secondResult.Model,
                sessionId = secondResult.SessionId,
                firstSessionId = firstResult.SessionId,
                secondSessionId = secondResult.SessionId,
                sameProviderSession,
                usage = secondResult.Usage,
                providerExitCode = secondResult.ProviderExitCode,
                standardError = secondResult.StandardError,
                firstVisibleText = firstResult.VisibleText,
                secondVisibleText = secondResult.VisibleText,
                toolCallCount = firstResult.ToolCallCount + secondResult.ToolCallCount,
                toolResultCount = firstResult.ToolResultCount + secondResult.ToolResultCount,
                stoppedAfterToolLimit = firstResult.StoppedAfterToolLimit || secondResult.StoppedAfterToolLimit,
                error = FirstNonEmpty(firstResult.Error, secondResult.Error)
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
            ProviderProcessMode = source.ProviderProcessMode,
            ClaudeModel = source.ClaudeModel,
            CodexModel = source.CodexModel,
            CodexReasoningEffort = source.CodexReasoningEffort,
            ClaudePath = source.ClaudePath,
            CodexPath = source.CodexPath,
            WorkingDirectory = source.WorkingDirectory,
            MaxToolRounds = 1,
            ShowDebugMessages = source.ShowDebugMessages,
            ShowUsageMessages = source.ShowUsageMessages,
            EnableDocumentMemory = false
        };

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

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
