# RhinoAgent

RhinoAgent is a Rhino 8 plug-in that starts a Claude Code / Codex-style agent directly in Rhino's command line.

Type `Agent`, chat in the normal Rhino command prompt, and let the agent inspect or modify the active model through RhinoCommon, native Rhino commands, Rhino Python, C# scripts, and local project files.

While the `Agent` prompt is active on Windows, copy or drag any regular local file into Rhino's command line. RhinoAgent accepts one or many Explorer files, standalone paths, and quoted paths inside a prompt. Placeholders use a lowercase extension plus a session-stable counter, such as `[.stl 1]`, `[.stp 1]`, and `[.stp 2]`; extensionless files use `[file 1]`. Multiple pasted or dropped files accumulate without submitting the prompt so you can type a request such as `Compare [.stp 1] with [.stp 2]`.

PNG, JPEG, GIF, and WebP files up to 20 MB continue through each provider's native image channel. Every other file stays local: the prompt receives a structured attachment manifest and the model calls RhinoAgent's read-only attachment tools to choose an installed interpreter. Text gets a bounded preview, ZIP files get a non-extracting listing, recognized 3D formats are inspected in a disposable headless Rhino document, and unknown binaries get an honest bounded signature probe. User-owned files are never deleted or modified. Raw clipboard captures live only under `%TEMP%/RhinoAgent/attachments` and are released after the turn; a guarded 24-hour startup sweep handles crash leftovers.

After you submit a prompt, the command line remains in Agent mode and shows the animated thinking state until the turn finishes. Provider work runs on a worker task while Rhino continues pumping its UI, so you can orbit the viewport and inspect panels without allowing a conflicting Rhino command to start. When the response is complete, the Agent prompt returns for the next message.

Progress-style responses remain visible during multi-step work. If a provider says it will start or continue an action but accidentally omits the required RhinoAgent tool block, RhinoAgent keeps ownership of the turn and asks the provider to continue automatically. This recovery is bounded so a repeatedly non-actionable provider response fails clearly instead of looping forever or silently returning to `You>`.

While `Agent` is running, you can still model manually: type native command forms such as `_Line`, `-Layer`, `.Undo`, `! _Circle`, known command names such as `Line`, or your Rhino aliases. Use `/ask <prompt>` when a normal language prompt intentionally starts with a Rhino command name.

## Status

This is an early V0 meant for hands-on testing.

- Target: Rhino 8, Windows and macOS
- Plug-in format: RhinoCommon `.rhp`
- Providers: Claude Code CLI and Codex CLI
- File prompts: arbitrary regular files, multi-file paste/drop, extension-number placeholders, local interpreter tools, and native multimodal image transport
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
- `/model <model>` validates and sets the active provider model
- `/effort low|medium|high|off`
- `/mode ask|auto|full|plan`
- `/debug on|off`
- `/timeout <seconds>|off`
- `/skill list|show|use|create|save|enable|disable|delete|export|demos`
- `/memory status|show|open|index|refresh|undo|history|import|export|on|off`
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
- `full`: execute RhinoAgent tool calls without prompting, except operations such as importing an attachment or changing saved skills that are always confirmation-gated.
- `plan`: show intended tool calls without executing them.

`/debug off` hides provider progress and tool execution debug messages inside the active `Agent` session. `/usage off` hides the separate usage message line after provider turns. `/effort` controls Codex app-server reasoning effort for new `Agent` sessions; `off` leaves effort unset so Codex uses its provider default.

Exact token and cost usage are only displayed when the provider CLI emits exact usage. RhinoAgent does not estimate usage.

## Conversation Memory Index

Completed turns are added to a bounded in-session index using only the user's message, the visible assistant response, and compact tool-count metadata. Hidden provider prompts, hidden tool blocks, and raw tool output are excluded. Identical turns are deduplicated, individual messages are length-bounded, and casual turns are batched so memory maintenance does not require an extra provider call after every message.

After four pending turns, a successful tool action, an explicit durable-memory request, `/memory index`, or session exit, RhinoAgent schedules a separate background worker and returns to the command prompt without waiting for the maintenance provider. Rhino document reads and writes are marshaled onto Rhino's UI thread, while maintenance for the same document is serialized to prevent competing sessions from overwriting each other. `/memory status` reports queued and in-flight turns.

The private maintenance pass merges durable goals, decisions, constraints, conventions, tasks, and completed work into the generated Agent Notes section of the active `.3dm`; it does not append a raw conversation transcript or overwrite user-authored memory sections. Failed or timed-out batches are restored to the bounded queue so `/memory index` can retry them.

## Provider Process Modes

- `long`: for Codex, start one `codex app-server --stdio` process when the provider is first used, resume the saved Codex thread for the working directory when available, and send each provider round as a `turn/start` on that thread.
- `stateless`: use the previous one-shot CLI behavior, such as `codex exec --json`, for each provider round.

`long` is the default process mode. Claude Code currently keeps the existing print-mode provider path; the long-running app-server implementation is Codex-specific.

Changing `/process`, `/mode`, `/model`, or `/effort` saves config immediately, but restart `Agent` to switch the provider process architecture, provider-level sandbox settings, model, or Codex reasoning effort for the already-created provider object.

`/model` validates before saving. Codex names are checked against the live `model/list` catalog returned for the logged-in account; Claude names are checked against Claude Code's stable model aliases. Invalid names leave the config unchanged and show the closest available name, so `/model gpt5.5` suggests `/model gpt-5.5` without asking for a restart.

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
- `fetch_url`
- `read_file`
- `write_file`
- `attachment_info`
- `inspect_attachment`
- `compare_attachments`
- `list_attachment_interpreters`
- `import_attachment`
- `list_skills`
- `read_skill_file`
- `create_skill`
- `update_skill`
- `delete_skill`
- `export_skill`

This is deliberately close to the tool shape used by Rhino MCP projects, but without requiring a separate MCP bridge between the model and Rhino.

`attachment_info`, `inspect_attachment`, and `compare_attachments` are read-only. `inspect_attachment` dispatches through an ordered interpreter registry and never executes a file. For `.3dm`, `.stp`, `.step`, `.stl`, and other recognized Rhino-importable formats it uses a disposable headless document, leaving the active document unchanged. `import_attachment` is deliberately separate because it changes the active document and always asks for confirmation, including in `full` mode.

`capture_viewport` writes PNG files and a compact JSON manifest under the system temp folder. Use exact model tools first for dimensions, object IDs, layers, topology, and other CAD facts; use viewport capture when visual feedback matters, such as silhouette, framing, overlap, recognizability, or whether a generated model looks right.

## Skills

RhinoAgent can create and use Codex-style skill folders without enabling native provider tools. Skills are stored per user under:

```text
%APPDATA%/RhinoAgent/skills
```

Each skill uses a required `SKILL.md` with `name` and `description` frontmatter and may include `references/`, `scripts/`, `assets/`, and `agents/openai.yaml`. Before each provider turn, RhinoAgent matches enabled skills by explicit name or description keywords, loads up to three matching `SKILL.md` files, and prints a compact `Loaded skill: ...` message.

Skill creation and export stay inside RhinoAgent's approval model. `create_skill`, `update_skill`, `delete_skill`, and `export_skill` show a manifest summary and ask for approval even in `full` mode.

Useful commands:

```text
/skill demos
/skill list
/skill show rhino-model-review
/skill use parametric-form-study create a small facade panel study
/skill create a reusable workflow for checking imported STEP files
/skill export skill-writer C:\Users\you\Desktop\skills
```

`/skill demos` installs three testable demo skills: `rhino-model-review`, `parametric-form-study`, and `skill-writer`.

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

`run_command`, `run_python`, `execute_csharp`, `write_file`, and `import_attachment` are powerful. They can change the active model and local files. Attached files are treated as untrusted: RhinoAgent does not execute them, archive inspection does not extract them, and user-owned paths are never cleanup targets. `capture_viewport` is read-only for the model, but it writes temporary PNG/JSON files and can reveal whatever is visible in the selected viewport. Start in `ask` or `plan` mode when testing on real work.

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
