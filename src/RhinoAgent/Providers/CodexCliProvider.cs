using System.Text;
using System.Text.Json;

namespace RhinoAgent.Providers;

public sealed class CodexCliProvider : ExternalProcessProvider, IModelCatalogProvider
{
    public CodexCliProvider(string executablePath, string model, AgentPermissionMode permissionMode, string workingDirectory)
        : base(executablePath, model, permissionMode, workingDirectory)
    {
    }

    public override AgentProviderKind Kind => AgentProviderKind.Codex;
    public override string DisplayName => $"Codex CLI ({Model}, {PermissionMode})";

    protected override IReadOnlyList<string> BuildArguments(AgentProviderPrompt prompt)
    {
        var args = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--model",
            Model,
            "--sandbox",
            MapSandbox(PermissionMode)
        };

        foreach (var image in prompt.Images)
        {
            args.Add("--image");
            args.Add(image.LocalPath);
        }

        if (PermissionMode == AgentPermissionMode.FullAccess)
            args.Add("--dangerously-bypass-approvals-and-sandbox");

        return args;
    }

    protected override IProviderOutputCollector CreateCollector(Action<AgentProgress> progress) =>
        new CodexOutputCollector(progress, Model);

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        Action<AgentProgress> progress,
        CancellationToken cancellationToken)
    {
        using var catalogProvider = new CodexAppServerProvider(
            ExecutablePath,
            Model,
            "",
            PermissionMode,
            WorkingDirectory);
        return await catalogProvider
            .GetAvailableModelsAsync(progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string MapSandbox(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => "read-only",
        AgentPermissionMode.FullAccess => "danger-full-access",
        _ => "workspace-write"
    };

    private sealed class CodexOutputCollector : IProviderOutputCollector
    {
        private readonly Action<AgentProgress> _progress;
        private readonly string _model;
        private readonly StringBuilder _text = new();
        private string? _sessionId;
        private TokenUsage? _usage;

        public CodexOutputCollector(Action<AgentProgress> progress, string model)
        {
            _progress = progress;
            _model = model;
        }

        public void AcceptLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(type))
                    _progress(new AgentProgress($"Codex event: {type}"));

                TryCaptureSession(root);
                TryCaptureUsage(root);
                TryCaptureText(root);
            }
            catch
            {
                _progress(new AgentProgress(line));
            }
        }

        public AgentProviderResult ToResult(int exitCode, string stderr) =>
            new(_text.ToString().Trim(), _model, _sessionId, _usage, exitCode, stderr);

        private void TryCaptureSession(JsonElement root)
        {
            if (root.TryGetProperty("session_id", out var sessionEl))
                _sessionId = sessionEl.GetString();
            else if (root.TryGetProperty("thread_id", out var threadEl))
                _sessionId = threadEl.GetString();
        }

        private void TryCaptureUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usageEl))
                return;

            _usage = new TokenUsage(
                ReadLong(usageEl, "input_tokens") ?? ReadLong(usageEl, "inputTokens"),
                ReadLong(usageEl, "output_tokens") ?? ReadLong(usageEl, "outputTokens"),
                ReadLong(usageEl, "cache_creation_input_tokens"),
                ReadLong(usageEl, "cache_read_input_tokens") ?? ReadLong(usageEl, "cached_input_tokens"),
                ReadLong(usageEl, "reasoning_output_tokens"),
                ReadDecimal(usageEl, "total_cost_usd") ?? ReadDecimal(root, "total_cost_usd"));
        }

        private void TryCaptureText(JsonElement root)
        {
            foreach (var property in new[] { "text", "message", "content", "result", "last_message" })
            {
                if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        _text.AppendLine(text);
                }
            }

            if (root.TryGetProperty("item", out var item))
                TryCaptureText(item);
        }
    }
}
