using System.Text;
using System.Text.Json;

namespace RhinoAgent.Providers;

public sealed class ClaudeCliProvider : ExternalProcessProvider, IConversationResumeProvider
{
    private string? _sessionId;
    private string? _resumeSessionId;
    private bool _continueLatestConversation;

    public ClaudeCliProvider(string executablePath, string model, AgentPermissionMode permissionMode, string workingDirectory)
        : base(executablePath, model, permissionMode, workingDirectory)
    {
    }

    public override AgentProviderKind Kind => AgentProviderKind.Claude;
    public override string DisplayName => $"Claude Code ({Model}, {PermissionMode}, persistent)";
    public string? ActiveSessionId => _sessionId;

    public bool TryContinueLatestConversation(out string message)
    {
        _resumeSessionId = null;
        _continueLatestConversation = true;
        message = "Claude will continue the most recent saved conversation in this working directory on the next prompt.";
        return true;
    }

    public bool TryResumeConversation(string sessionId, out string message)
    {
        sessionId = sessionId.Trim();
        if (sessionId.Length == 0)
        {
            message = "Usage: /resume latest|<claude-session-id>";
            return false;
        }

        _resumeSessionId = sessionId;
        _continueLatestConversation = false;
        message = $"Claude will resume session {sessionId} on the next prompt.";
        return true;
    }

    protected override IReadOnlyList<string> BuildArguments(string prompt)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--tools", "",
            "--model", Model,
            "--permission-mode", MapPermissionMode(PermissionMode)
        };

        if (!string.IsNullOrWhiteSpace(_resumeSessionId))
        {
            args.Add("--resume");
            args.Add(_resumeSessionId);
        }
        else if (_continueLatestConversation)
        {
            args.Add("--continue");
        }
        else if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            args.Add("--resume");
            args.Add(_sessionId);
        }

        return args;
    }

    protected override IProviderOutputCollector CreateCollector(Action<AgentProgress> progress) =>
        new ClaudeOutputCollector(progress);

    protected override void OnProviderResult(AgentProviderResult result)
    {
        var requestedSessionId = _resumeSessionId;

        if (!string.IsNullOrWhiteSpace(result.SessionId))
            _sessionId = result.SessionId;
        else if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(requestedSessionId))
            _sessionId = requestedSessionId;

        _resumeSessionId = null;
        _continueLatestConversation = false;
    }

    public override void Reset()
    {
        _sessionId = null;
        _resumeSessionId = null;
        _continueLatestConversation = false;
    }

    private static string MapPermissionMode(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Plan => "plan",
        AgentPermissionMode.Auto => "auto",
        AgentPermissionMode.FullAccess => "bypassPermissions",
        _ => "default"
    };

    private sealed class ClaudeOutputCollector : IProviderOutputCollector
    {
        private readonly Action<AgentProgress> _progress;
        private readonly StringBuilder _text = new();
        private string? _resultText;
        private string? _model;
        private string? _sessionId;
        private TokenUsage? _usage;

        public ClaudeOutputCollector(Action<AgentProgress> progress)
        {
            _progress = progress;
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

                if (type == "system")
                {
                    HandleSystem(root);
                    return;
                }

                if (type == "assistant")
                {
                    HandleAssistant(root);
                    return;
                }

                if (type == "result")
                {
                    HandleResult(root);
                    return;
                }
            }
            catch
            {
                _text.AppendLine(line);
            }
        }

        public AgentProviderResult ToResult(int exitCode, string stderr)
        {
            var text = !string.IsNullOrWhiteSpace(_resultText) ? _resultText! : _text.ToString();
            return new AgentProviderResult(text, _model, _sessionId, _usage, exitCode, stderr);
        }

        private void HandleSystem(JsonElement root)
        {
            var subtype = root.TryGetProperty("subtype", out var subtypeEl) ? subtypeEl.GetString() : null;
            if (subtype == "init")
            {
                _model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : _model;
                _sessionId = root.TryGetProperty("session_id", out var sessionEl) ? sessionEl.GetString() : _sessionId;
                _progress(new AgentProgress($"Claude session started: {_model}"));
            }
            else if (subtype == "status" && root.TryGetProperty("status", out var statusEl))
            {
                _progress(new AgentProgress($"Claude status: {statusEl.GetString()}"));
            }
            else if (subtype == "thinking_tokens" && root.TryGetProperty("estimated_tokens", out var tokensEl))
            {
                _progress(new AgentProgress($"Thinking tokens: {tokensEl.GetInt64()}", true));
            }
        }

        private void HandleAssistant(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message))
                return;

            _model = message.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : _model;
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var typeEl)
                        && typeEl.GetString() == "text"
                        && block.TryGetProperty("text", out var textEl))
                    {
                        builder.Append(textEl.GetString());
                    }
                }

                if (builder.Length > 0)
                {
                    _text.Clear();
                    _text.Append(builder);
                }
            }
        }

        private void HandleResult(JsonElement root)
        {
            _resultText = root.TryGetProperty("result", out var resultEl) ? resultEl.GetString() : _text.ToString();
            _sessionId = root.TryGetProperty("session_id", out var sessionEl) ? sessionEl.GetString() : _sessionId;
            var cost = ReadDecimal(root, "total_cost_usd");

            if (root.TryGetProperty("usage", out var usageEl))
            {
                _usage = new TokenUsage(
                    ReadLong(usageEl, "input_tokens"),
                    ReadLong(usageEl, "output_tokens"),
                    ReadLong(usageEl, "cache_creation_input_tokens"),
                    ReadLong(usageEl, "cache_read_input_tokens"),
                    null,
                    cost);
            }
        }
    }
}
