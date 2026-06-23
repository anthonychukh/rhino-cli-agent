using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RhinoAgent.Providers;

public sealed class CodexAppServerProvider : IAgentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _executablePath;
    private readonly string _model;
    private readonly AgentPermissionMode _permissionMode;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private string? _threadId;
    private bool _initialized;
    private bool _disposed;

    public CodexAppServerProvider(string executablePath, string model, AgentPermissionMode permissionMode, string workingDirectory)
    {
        _executablePath = executablePath;
        _model = model;
        _permissionMode = permissionMode;
        _workingDirectory = Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public AgentProviderKind Kind => AgentProviderKind.Codex;
    public string DisplayName => $"Codex app-server ({_model}, {_permissionMode}, long-running)";
    public AgentProviderProcessMode ProcessMode => AgentProviderProcessMode.LongRunning;

    public async Task<AgentProviderResult> RunPromptAsync(
        string prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyAsync(progress, cancellationToken).ConfigureAwait(false);
            return await RunTurnAsync(prompt, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            throw;
        }
        catch (Exception ex)
        {
            StopProcess();
            return new AgentProviderResult("", _model, _threadId, null, 1, FirstNonEmpty(GetStderr(), ex.Message));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset() => _threadId = null;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopProcess();
        _gate.Dispose();
    }

    private async Task EnsureReadyAsync(Action<AgentProgress> progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_process is null || _process.HasExited)
            StartProcess(progress);

        if (!_initialized)
        {
            var initializeId = Guid.NewGuid().ToString();
            await SendAsync(new
            {
                id = initializeId,
                method = "initialize",
                @params = new
                {
                    clientInfo = new
                    {
                        name = "rhino-agent",
                        title = "RhinoAgent",
                        version = "0.1.0"
                    },
                    capabilities = new
                    {
                        experimentalApi = true,
                        requestAttestation = false,
                        optOutNotificationMethods = new[]
                        {
                            "command/exec/outputDelta",
                            "item/agentMessage/delta",
                            "item/plan/delta",
                            "item/fileChange/outputDelta",
                            "item/reasoning/summaryTextDelta",
                            "item/reasoning/textDelta"
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            await WaitForResponseAsync(initializeId, progress, cancellationToken).ConfigureAwait(false);
            await SendAsync(new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            progress(new AgentProgress("Codex app-server initialized."));
        }

        if (string.IsNullOrWhiteSpace(_threadId))
            await StartThreadAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    private void StartProcess(Action<AgentProgress> progress)
    {
        _stderr.Clear();
        _initialized = false;
        _threadId = null;

        var process = new Process();
        process.StartInfo.FileName = _executablePath;
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--stdio");
        process.StartInfo.WorkingDirectory = _workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        progress(new AgentProgress($"Starting {DisplayName}: {_executablePath} app-server --stdio"));
        process.Start();
        _process = process;
        _ = Task.Run(() => DrainStderrAsync(process));
        progress(new AgentProgress($"{DisplayName} process started: pid {process.Id}."));
    }

    private async Task StartThreadAsync(Action<AgentProgress> progress, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString();
        await SendAsync(new
        {
            id = requestId,
            method = "thread/start",
            @params = new
            {
                model = _model,
                modelProvider = (string?)null,
                cwd = _workingDirectory,
                runtimeWorkspaceRoots = new[] { _workingDirectory },
                approvalPolicy = MapApprovalPolicy(_permissionMode),
                approvalsReviewer = "user",
                sandbox = MapSandbox(_permissionMode),
                config = (object?)null,
                serviceName = (string?)null,
                baseInstructions = "You are RhinoAgent's long-running Codex app-server provider. Follow each user message exactly.",
                developerInstructions = (string?)null,
                personality = (object?)null,
                ephemeral = true,
                sessionStartSource = "startup",
                threadSource = (string?)null
            }
        }, cancellationToken).ConfigureAwait(false);

        using var response = await WaitForResponseAsync(requestId, progress, cancellationToken).ConfigureAwait(false);
        if (response.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("thread", out var thread)
            && thread.TryGetProperty("id", out var threadId))
        {
            _threadId = threadId.GetString();
            progress(new AgentProgress($"Codex app-server thread started: {_threadId}"));
            return;
        }

        throw new InvalidOperationException("Codex app-server thread/start response did not include a thread id.");
    }

    private async Task<AgentProviderResult> RunTurnAsync(
        string prompt,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_threadId))
            throw new InvalidOperationException("Codex app-server thread was not started.");

        var requestId = Guid.NewGuid().ToString();
        var turnId = "";
        var text = new StringBuilder();
        TokenUsage? usage = null;

        await SendAsync(new
        {
            id = requestId,
            method = "turn/start",
            @params = new
            {
                threadId = _threadId,
                clientUserMessageId = (string?)null,
                input = new[]
                {
                    new
                    {
                        type = "text",
                        text = prompt,
                        text_elements = Array.Empty<object>()
                    }
                },
                cwd = _workingDirectory,
                approvalPolicy = MapApprovalPolicy(_permissionMode),
                approvalsReviewer = "user",
                sandboxPolicy = BuildTurnSandboxPolicy(_permissionMode),
                model = _model,
                effort = (string?)null,
                summary = (object?)null,
                personality = (object?)null,
                outputSchema = (object?)null
            }
        }, cancellationToken).ConfigureAwait(false);

        var completed = false;
        while (!completed)
        {
            using var doc = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            if (IsResponse(root, requestId))
            {
                ThrowIfErrorResponse(root);
                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("turn", out var turn)
                    && turn.TryGetProperty("id", out var turnIdEl))
                {
                    turnId = turnIdEl.GetString() ?? "";
                    progress(new AgentProgress($"Codex app-server turn started: {turnId}"));
                }

                continue;
            }

            var method = ReadString(root, "method");
            if (string.IsNullOrWhiteSpace(method))
                continue;

            if (method == "item/completed"
                && TryReadParams(root, out var itemParams)
                && BelongsToTurn(itemParams, turnId)
                && itemParams.TryGetProperty("item", out var item)
                && ReadString(item, "type") == "agentMessage")
            {
                var message = ReadString(item, "text");
                if (!string.IsNullOrWhiteSpace(message))
                    text.AppendLine(message);
            }
            else if (method == "thread/tokenUsage/updated"
                && TryReadParams(root, out var usageParams)
                && BelongsToTurn(usageParams, turnId))
            {
                usage = ReadUsage(usageParams);
            }
            else if (method == "turn/completed"
                && TryReadParams(root, out var turnParams)
                && turnParams.TryGetProperty("turn", out var turn)
                && BelongsToTurn(turn, turnId))
            {
                completed = true;
                if (turn.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    return new AgentProviderResult(text.ToString().Trim(), _model, _threadId, usage, 1, error.ToString());
            }
            else
            {
                ReportProgress(method, root, progress);
            }
        }

        return new AgentProviderResult(text.ToString().Trim(), _model, _threadId, usage, 0, "");
    }

    private async Task<JsonDocument> WaitForResponseAsync(
        string requestId,
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var doc = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (IsResponse(root, requestId))
            {
                ThrowIfErrorResponse(root);
                return doc;
            }

            var method = ReadString(root, "method");
            if (!string.IsNullOrWhiteSpace(method))
                ReportProgress(method!, root, progress);

            doc.Dispose();
        }
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var process = GetLiveProcess();
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var process = GetLiveProcess();
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new InvalidOperationException($"Codex app-server stdout closed. {GetStderr()}");
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                return JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                AppendStderr(line);
            }
        }
    }

    private Process GetLiveProcess()
    {
        ThrowIfDisposed();
        if (_process is null)
            throw new InvalidOperationException("Codex app-server process was not started.");
        if (_process.HasExited)
            throw new InvalidOperationException($"Codex app-server exited with code {_process.ExitCode}. {GetStderr()}");
        return _process;
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                    AppendStderr(line);
            }
        }
        catch
        {
            // Stderr is best-effort diagnostics; provider turns fail through stdout/process state.
        }
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _initialized = false;
        _threadId = null;

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                    // Best-effort graceful shutdown.
                }

                if (!process.WaitForExit(1_000))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process cleanup is best-effort.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool IsResponse(JsonElement root, string requestId) =>
        root.TryGetProperty("id", out var id) && id.GetString() == requestId;

    private static void ThrowIfErrorResponse(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var messageEl)
                ? messageEl.GetString()
                : error.ToString();
            throw new InvalidOperationException(message ?? "Codex app-server returned an error response.");
        }
    }

    private static bool TryReadParams(JsonElement root, out JsonElement value) =>
        root.TryGetProperty("params", out value);

    private static bool BelongsToTurn(JsonElement value, string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return true;
        if (value.TryGetProperty("turnId", out var directTurnId))
            return directTurnId.GetString() == turnId;
        if (value.TryGetProperty("id", out var id))
            return id.GetString() == turnId;
        return false;
    }

    private static TokenUsage? ReadUsage(JsonElement usageParams)
    {
        if (!usageParams.TryGetProperty("tokenUsage", out var tokenUsage)
            || !tokenUsage.TryGetProperty("last", out var last))
            return null;

        return new TokenUsage(
            ReadLong(last, "inputTokens"),
            ReadLong(last, "outputTokens"),
            null,
            ReadLong(last, "cachedInputTokens"),
            ReadLong(last, "reasoningOutputTokens"),
            null);
    }

    private static string MapSandbox(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => "read-only",
        AgentPermissionMode.FullAccess => "danger-full-access",
        _ => "workspace-write"
    };

    private static string MapApprovalPolicy(AgentPermissionMode mode)
    {
        _ = mode;
        // RhinoAgent owns the user-facing approval flow for its in-process tools.
        return "never";
    }

    private static object BuildTurnSandboxPolicy(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => new
        {
            type = "readOnly",
            networkAccess = false
        },
        AgentPermissionMode.FullAccess => new
        {
            type = "dangerFullAccess"
        },
        _ => new
        {
            type = "workspaceWrite",
            writableRoots = Array.Empty<string>(),
            networkAccess = true,
            excludeTmpdirEnvVar = false,
            excludeSlashTmp = false
        }
    };

    private static void ReportProgress(string method, JsonElement root, Action<AgentProgress> progress)
    {
        if (method == "turn/started")
        {
            progress(new AgentProgress("Codex app-server turn is active."));
            return;
        }

        if (method == "thread/status/changed"
            && TryReadParams(root, out var statusParams)
            && statusParams.TryGetProperty("status", out var status)
            && status.TryGetProperty("type", out var type))
        {
            progress(new AgentProgress($"Codex thread status: {type.GetString()}", true));
        }
    }

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var longValue)
            ? longValue
            : null;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void AppendStderr(string value)
    {
        lock (_stderr)
        {
            _stderr.AppendLine(value);
        }
    }

    private string GetStderr()
    {
        lock (_stderr)
        {
            return _stderr.ToString().Trim();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CodexAppServerProvider));
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
