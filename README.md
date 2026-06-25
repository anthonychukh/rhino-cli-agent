# RhinoAgent

RhinoAgent is a Rhino 8 plug-in that starts a Claude Code / Codex-style agent directly in Rhino's command line.

Type `Agent`, chat in the normal Rhino command prompt, and let the agent inspect or modify the active model through RhinoCommon, native Rhino commands, Rhino Python, C# scripts, and local project files.

While `Agent` is running, you can still model manually: type native command forms such as `_Line`, `-Layer`, `.Undo`, `! _Circle`, known command names such as `Line`, or your Rhino aliases. Use `/ask <prompt>` when a normal language prompt intentionally starts with a Rhino command name.

## Status

This is an early V0 meant for hands-on testing.

- Target: Rhino 8, Windows and macOS
- Plug-in format: RhinoCommon `.rhp`
- Providers: Claude Code CLI and Codex CLI
- Auth: delegates to the official provider CLI login flows
- Session persistence: Claude Code and Codex long-running sessions are saved and resumed by working directory; `/clear` starts fresh
- Grasshopper: planned, not implemented yet

## Build

Install the .NET 8 SDK and build:

```powershell
dotnet restore RhinoAgent.sln
dotnet build RhinoAgent.sln --configuration Debug
```

The plug-in is emitted here:

```text
src/RhinoAgent/bin/Debug/net8.0/RhinoAgent.rhp
```

Quick Windows launch for testing:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-RhinoAgent.ps1
```

On macOS, build the same solution and load the emitted `.rhp` from Rhino 8's Plug-in Manager. The debug profile also targets `/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros` for IDE launches.

Run the non-interactive Rhino load smoke test:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-RhinoAgent.ps1 -SelfTest -ExitAfterSelfTest
```

Run the load test plus an installed-provider round trip through the real `Agent` session path:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-RhinoAgent.ps1 -SelfTest -ProviderSelfTest -ExitAfterSelfTest -SelfTestTimeoutSeconds 180
```

To also try starting the installed RhinoMCP package for debugging:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-RhinoAgent.ps1 -WithMcp
```

## Install For Testing

In Rhino 8:

1. Run `PluginManager`.
2. Install or drag-load `src/RhinoAgent/bin/Debug/net8.0/RhinoAgent.rhp`.
3. Restart Rhino if needed.
4. Type `Agent`.

The build output also includes `manifest.yml` for Yak/package-manager staging.

## Authentication

RhinoAgent does not reverse-engineer private account token stores. It uses the official CLIs:

- Claude: `claude auth login`
- Codex: `codex login --device-auth`

On first `Agent` run, if no logged-in provider is detected, RhinoAgent prompts for Claude or Codex and launches the login command in a terminal. You can also run `AgentLogin`.

Useful status command:

```text
AgentStatus
```

## Commands

Rhino commands:

- `Agent` starts the command-line agent session.
- `AgentLogin` starts provider login.
- `AgentStatus` prints provider, auth, model, and config status.
- `AgentConfig` edits provider, provider process mode, and permission mode from Rhino options.
- `AgentSelfTest` writes a JSON smoke-test result to the system temp folder.
- `AgentProviderSelfTest` verifies that the selected logged-in provider can answer through the real `Agent` session path.

Slash commands inside `Agent`:

- `/help`
- `/status`
- `/login`
- `/provider auto|claude|codex`
- `/continue [latest|session-id]`
- `/resume [latest|session-id]`
- `/process long|stateless`
- `/model <model>`
- `/effort low|medium|high|off`
- `/mode ask|auto|full|plan`
- `/debug on|off`
- `/timeout <seconds>|off`
- `/run <rhino command>`
- `! <rhino command or alias>`
- `_Command`, `-Command`, `.Command`, known Rhino command names, or aliases for native passthrough
- `/ask <prompt>` to force chat if a prompt starts like a Rhino command
- `/clear`
- `/usage [on|off]`
- `/compact`
- `/exit`

## Permission Modes

- `ask`: ask before every tool call.
- `auto`: run read/modeling tools, ask before high-impact script/file operations.
- `full`: execute all RhinoAgent tool calls without prompting.
- `plan`: show intended tool calls without executing them.

`/debug off` hides provider progress and tool execution debug messages inside the active `Agent` session. `/usage off` hides the separate usage message line after provider turns. `/effort` controls Codex app-server reasoning effort for new `Agent` sessions; `off` leaves effort unset so Codex uses its provider default.

Exact token and cost usage are only displayed when the provider CLI emits exact usage. RhinoAgent does not estimate usage.

## Provider Process Modes

- `long`: for Codex, start one `codex app-server --stdio` process when the provider is first used, resume the saved Codex thread for the working directory when available, and send each provider round as a `turn/start` on that thread.
- `stateless`: use the previous one-shot CLI behavior, such as `codex exec --json`, for each provider round.

`long` is the default process mode. Claude Code currently keeps the existing print-mode provider path; the long-running app-server implementation is Codex-specific.

Changing `/process`, `/mode`, `/model`, or `/effort` saves config immediately, but restart `Agent` to switch the provider process architecture, provider-level sandbox settings, model, or Codex reasoning effort for the already-created provider object.

## Tool Surface

The model emits hidden `<rhino-agent>{...}</rhino-agent>` tool blocks. RhinoAgent parses those blocks, executes them in-process, returns results to the model, and then asks the model to continue.

For Claude Code, RhinoAgent disables Claude's native tool list for provider turns. This keeps Rhino/file actions inside RhinoAgent's permission modes instead of letting the external CLI act on its own. Claude session persistence stays on, RhinoAgent captures the returned `session_id`, and later turns pass `--resume <session_id>` so the provider conversation remains continuous. When `Agent` starts without a saved RhinoAgent Claude session pointer, Claude uses `--continue` by default to resume the most recent saved Claude conversation in the current working directory. `/continue` and `/resume` can still force a specific resume target.

For Codex long-running mode, RhinoAgent uses the app-server JSONL protocol directly instead of launching `codex exec` for every prompt. After a successful provider turn, Codex thread ids are saved in `%APPDATA%/RhinoAgent/codex-sessions.json` by working directory. When `Agent` starts again, RhinoAgent tries `thread/resume` before starting fresh. `/clear` clears RhinoAgent history, removes the saved provider resume pointer for the working directory, and starts a fresh provider conversation on the next turn.

Current tools:

- `document_summary`
- `list_objects`
- `run_command`
- `run_python`
- `execute_csharp`
- `capture_viewport`
- `read_file`
- `write_file`

This is deliberately close to the tool shape used by Rhino MCP projects, but without requiring a separate MCP bridge between the model and Rhino.

`capture_viewport` writes PNG files and a compact JSON manifest under the system temp folder. Use exact model tools first for dimensions, object IDs, layers, topology, and other CAD facts; use viewport capture when visual feedback matters, such as silhouette, framing, overlap, recognizability, or whether a generated model looks right.

## Configuration

Config is stored in:

```text
%APPDATA%/RhinoAgent/config.json
```

On macOS, this resolves through .NET's application data folder.

Example:

```json
{
  "provider": "Claude",
  "permissionMode": "Ask",
  "providerProcessMode": "LongRunning",
  "claudeModel": "claude-opus-4-8",
  "codexModel": "gpt-5.5",
  "codexReasoningEffort": "low",
  "claudePath": null,
  "codexPath": null,
  "workingDirectory": null,
  "maxToolRounds": 4,
  "providerTurnTimeoutSeconds": 180,
  "showDebugMessages": true,
  "showUsageMessages": true
}
```

Set `claudePath` or `codexPath` if Rhino cannot resolve the CLI from `PATH`.

### Windows Codex Note

If `AgentStatus` shows Codex at a `C:\Program Files\WindowsApps\...` path but reports `Access is denied`, install or expose a normal Codex CLI executable and set `codexPath` in `config.json`. The WindowsApps app package can appear on `PATH` while still refusing direct process launch from other apps.

### macOS CLI Note

Rhino for Mac may launch without your shell's full `PATH`. RhinoAgent checks common user-local and Homebrew folders, including `~/.local/bin`, `~/.npm-global/bin`, `/opt/homebrew/bin`, and `/usr/local/bin`. If your Claude or Codex executable lives elsewhere, set `claudePath` or `codexPath`.

## Security

`run_command`, `run_python`, `execute_csharp`, and `write_file` are powerful. They can change the active model and local files. `capture_viewport` is read-only for the model, but it writes temporary PNG/JSON files and can reveal whatever is visible in the selected viewport. Start in `ask` or `plan` mode when testing on real work.

## Future TODOs

- Grasshopper tool surface and graph-building.
- Side panel for session restore/history.
- Real allowlist/denylist policy.
- Direct API adapters with native tool calling for API-key users.
- Yak package build and release pipeline.
- Broader provider JSONL event parsing across future Claude Code and Codex CLI versions.

## References

- RhinoMCP by Jingcheng Chen: https://github.com/jingcheng-chen/rhinomcp
- Rhino MCP Platform by McNeel: https://github.com/mcneel/RhinoMCP
- Codex CLI docs: https://developers.openai.com/codex/cli/reference
- Claude Code CLI docs: https://code.claude.com/docs/en/cli-reference

## License

MIT. This project is not affiliated with McNeel, OpenAI, Anthropic, Claude Code, or Codex.
