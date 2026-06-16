# PiSharp

PiSharp is the C#/.NET port of the Pi coding agent. It is an agent runtime, CLI, and Terminal UI (TUI) for interactive and non-interactive coding-agent workflows.

---

## 🚀 Key Features

- **Command-Line & TUI Application**: Run `pisharp` in an interactive Terminal UI, print-based output, JSON, or RPC mode.
- **Streaming Agent Harness**: Manages conversation turns, streams assistant outputs, handles tool execution, and persists session history.
- **Built-in Coding Tools**: Integrated tools for filesystem and terminal shell actions: `read`, `bash`, `edit`, `write`, `grep`, `find`, and `ls`.
- **Multi-Provider LLM Integration**: Shared `IModelProvider` abstraction with built-in support for Anthropic, OpenAI, Google Gemini/Vertex, AWS Bedrock, Mistral, and mock testing.
- **Durable Session Management**: JSONL-backed session logs supporting session continuation, branching, automatic compaction, and on-the-fly model or thinking-level changes.
- **Flexible Extension Architecture**:
  - **Native .NET Extensions**: Direct DLL-based plugins running in-process via collectible assembly loading.
  - **TypeScript Extension Bridge**: Out-of-process Node.js bridge maintaining backward compatibility with the original JavaScript version of Pi.
- **Live Sessions & Server Host**: A lightweight ASP.NET Core web server providing health check and WebSocket connection endpoints.

---

## ⚡ Installation

### Recommended: Install via script

**macOS / Linux:**
```bash
curl -fsSL https://raw.githubusercontent.com/imjimmeh/PiSharpio/master/install.sh | bash
```

**Windows (PowerShell):**
```powershell
irm https://raw.githubusercontent.com/imjimmeh/PiSharpio/master/install.ps1 | iex
```

### Manual install

Requires [.NET SDK 10+](https://dot.net/download):

```bash
dotnet tool install --global PiSharp.Cli
```

> **PATH note:** After installing, ensure `~/.dotnet/tools` (macOS/Linux) or `%USERPROFILE%\.dotnet\tools` (Windows) is on your `PATH`.

### Update

```bash
dotnet tool update --global PiSharp.Cli
```

---

## ⚡ Quick Start

PiSharp requires an API key for your chosen LLM provider. Set `ANTHROPIC_API_KEY` (or the relevant key) as an environment variable before running.

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

**Launch the interactive TUI (default):**

```bash
pisharp
```

**Send a one-shot prompt (print mode):**

```bash
pisharp --print "Explain the purpose of this project"
```

**Continue a previous session:**

```bash
pisharp --resume <session-id>
```

**List available options:**

```bash
pisharp --help
```

---

## 🏛️ Project Structure

The codebase is organized into clean, decoupled components located under `src/`:

| Project                                                | Description                                                                              |
| :----------------------------------------------------- | :--------------------------------------------------------------------------------------- |
| **[PiSharp.Abstractions](src/PiSharp.Abstractions)**   | Essential interfaces for the filesystem, execution environment, shell, and result types. |
| **[PiSharp.Agent.Core](src/PiSharp.Agent.Core)**       | Base contracts for the agent loop, assistants, event streams, and tools.                 |
| **[PiSharp.Agent](src/PiSharp.Agent)**                 | Agent loop, compaction logic, session persistence, and template catalog.                 |
| **[PiSharp.Ai](src/PiSharp.Ai)**                       | Model provider registry, credential resolution, OAuth storage, and client wrappers.       |
| **[PiSharp.Ai.ModelGenerator](src/PiSharp.Ai.ModelGenerator)** | Build-time model catalog generator.                                               |
| **[PiSharp.Tools](src/PiSharp.Tools)**                 | Implementations of core tools (`read`, `write`, `edit`, `bash`, etc.).                   |
| **[PiSharp.Extensions](src/PiSharp.Extensions)**       | Shared contracts and APIs for native and bridged extensions.                             |
| **[PiSharp.PluginHost](src/PiSharp.PluginHost)**       | Native DLL plugin loader and lifecycle manager.                                          |
| **[PiSharp.TsBridge](src/PiSharp.TsBridge)**           | TypeScript/JavaScript sidecar bridge runner and compatibility shims.                     |
| **[PiSharp.Compatibility](src/PiSharp.Compatibility)** | Parsing and loading of legacy Pi-compatible settings and layouts.                        |
| **[PiSharp.Coordination](src/PiSharp.Coordination)**   | Native extension for multi-agent coordination, daemon setup, and conflict warnings.      |
| **[PiSharp.Coordination.Daemon](src/PiSharp.Coordination.Daemon)** | Coordination daemon executable/library used by the extension.                    |
| **[PiSharp.Runtime](src/PiSharp.Runtime)**             | Main bootstrap wiring settings, providers, and session contexts.                         |
| **[PiSharp.Cli](src/PiSharp.Cli)**                     | CLI argument parser, print formatting, and execution controller.                         |
| **[PiSharp.Tui](src/PiSharp.Tui)**                     | Terminal User Interface views, keyboard shortcuts, and rendering.                        |
| **[PiSharp.Server](src/PiSharp.Server)**               | ASP.NET Core server containing `/health` and `/ws` endpoints.                            |

Tests are located in corresponding projects under `tests/`.

---

## 🛠️ Getting Started & Setup

### Prerequisites

- **.NET SDK 10.0 or higher** — [Download from dot.net](https://dot.net/download)
- **Node.js** (required if using TypeScript/JavaScript extensions via the bridge)

### Building the Project

Clone the repository and build the solution using the .NET CLI:

```powershell
dotnet build PiSharp.sln
```

### Running the Application

To run the CLI application:

```powershell
dotnet run --project src/PiSharp.Cli/PiSharp.Cli.csproj
```

---

## 🧪 Running Tests

A comprehensive test suite is provided to verify the runtime, TUI, and TypeScript bridge behavior.

Run all tests in the solution:

```powershell
dotnet test PiSharp.sln
```

Run tests for a specific project:

```powershell
dotnet test tests/PiSharp.Agent.Tests/PiSharp.Agent.Tests.csproj
```

Run targeted TUI tests (e.g., verifying rendering or shortcut flows):

```powershell
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter "NamePattern"
```

---

## 🔌 Extension Architecture

PiSharp is designed with an **Extension-First Policy**. Most custom features or domain-specific integrations should be built as extensions rather than directly modifying the core runtime.

### 1. Native .NET Extensions

Built as class libraries that implement `IExtension` and compile into `.dll` plugins. They run directly inside the main host process.

- See [Native .NET Extensions Guide](docs/pisharp-native-extensions.md) for details.

### 2. TypeScript Extensions

Located under the `extensions/` directory and executed in an out-of-process Node.js sidecar via JSON-RPC. They are fully compatible with existing JavaScript Pi packages.

- See [TypeScript Extension Compatibility Guide](docs/pisharp-typescript-extensions.md) for details.

---

## 📖 Additional Documentation

For detailed architectural specifications, reference guides, and API contracts, explore the `docs/` folder:

- [Developer Guide Overview](docs/pisharp-developer-guide.md)
- [Runtime architecture & settings precedence](docs/pisharp-runtime.md)
- [Built-in tools & contracts](docs/pisharp-tools.md)
- [Slash command development guide](docs/pisharp-slash-command-development.md)
- [TUI shortcut development guide](docs/pisharp-tui-shortcut-development.md)
- [Terminal.GUI Architecture Reference](docs/terminal-gui-architecture-reference.md)
- [Multi-agent Coordination Guide](docs/pisharp-agent-coordination.md)
- [PiSharp vs TypeScript Pi comparison](docs/pisharp-vs-pi.md)
