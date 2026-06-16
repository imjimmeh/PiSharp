# Epic 5: CLI, Modes, and Application Shell

**Status:** Draft

**Dependencies:** Epics 2, 3, 4 (Agent Loop, Providers, Tools)

**Depended On By:** Epic 6 (Extensions depend on full CLI)

---

## Goal

Build the C# CLI application with all three operating modes (interactive TUI, RPC, print), argument parsing, configuration management, and the application bootstrap.

## Target Projects

- `PiSharp.Cli` — entry point, argument parsing, mode selection, configuration
- `PiSharp.Tui` — interactive terminal UI

---

## Key Deliverables

### 1. CLI Entry Point (`Program.cs`)

Main entry point with command-line argument parsing, mode selection, configuration loading, service bootstrapping, extension loading, and process lifecycle management (graceful shutdown, signal handling).

### 2. Argument Parsing (`CliArgs.cs`)

Map all existing CLI flags:

| Flag | Alias | Description |
|------|-------|-------------|
| `--mode` | `-m` | Operating mode: interactive, rpc, print |
| `--model` | `-m` | Model selection |
| `--provider` | `-p` | Provider selection |
| `--config` | | Config file path |
| `--cwd` | | Working directory |
| `--prompt` | `-p` | Initial prompt |
| `--version` | `-v` | Version info |
| `--help` | `-h` | Help text |
| (extension-registered) | | Additional flags from extensions |

Support environment variable overrides and config file merging with CLI flag precedence.

### 3. RPC Mode (`RpcMode.cs`)

JSON-line protocol on stdin/stdout, wire-compatible with the existing TypeScript RPC specification.

**Supported commands:**

- `prompt`, `steer`, `follow_up`, `abort`
- `new_session`, `get_state`
- `set_model`, `cycle_model`, `get_available_models`
- `set_thinking_level`, `cycle_thinking_level`
- `set_steering_mode`, `set_follow_up_mode`
- `compact`, `set_auto_compaction`, `set_auto_retry`, `abort_retry`
- `bash`, `abort_bash`
- `get_session_stats`, `export_html`, `switch_session`, `fork`, `clone`
- `get_fork_messages`, `get_last_assistant_text`, `set_session_name`
- `get_messages`, `get_commands`

Response and event emission as JSON lines on stdout. Streaming event forwarding during prompt processing.

Also provide `RpcClient.cs` for external tools to communicate via RPC (matching `rpc-client.ts`).

### 4. Interactive Mode (TUI)

Terminal UI built with a .NET TUI library (Spectre.Console or similar).

- Chat interface with message display
- Editor component (single-line and multi-line input)
- Theme system (JSON theme files matching existing format)
- Autocomplete for commands
- Widget system (status bar, custom widgets above/below editor)
- Terminal image display
- Keybindings system
- Footer with session info, model, token usage

### 5. Print Mode (`PrintMode.cs`)

Simple prompt-response mode (non-interactive). Reads prompt from args or stdin, prints assistant response to stdout. Optional output formatting.

### 6. Configuration Management (`ConfigurationManager.cs`)

Read/write `.pi/config.json`. Model configuration persistence, extension configuration, settings management (auto-compaction, auto-retry, etc.). Config file discovery (cwd → home directory fallback).

### 7. Session Selector (`SessionPicker.cs`)

List available sessions, select by criteria (recent, name, id). Session file naming and discovery under `.pi/sessions/`.

### 8. Model Selector (`ModelSelector.cs`)

Provider/model selection UI, model filtering by capabilities, thinking level configuration.

---

## RPC Protocol Compatibility

RPC mode must be wire-compatible with the existing TS RPC protocol:

- Same JSON command format on stdin
- Same JSON response/event format on stdout
- Same streaming behavior
- Same error codes

This is critical for tools and editors that communicate with pi via RPC.

---

## Implementation Notes

- Use `System.CommandLine` for argument parsing (or equivalent)
- TUI: consider Spectre.Console for basic UI, or build a minimal TUI matching the TS `tui` package
- Configuration: `System.Text.Json` with custom converters
- Session picker: file system scan for `.pi/sessions/` directory
- RPC mode must be first-class — it is the primary integration point for editors and CI/CD
- Support graceful shutdown on Ctrl+C / SIGTERM
- Logging via `Microsoft.Extensions.Logging`
