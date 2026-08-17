# ADR: Daemon-Pushed State and Zero-Polling TUI/CLI Architecture

Date: 2026-08-17

Status: Accepted

## Context

Prior to this change, the remote TUI client had several latent polling, process-spawning, and RPC query loops running continuously:
1. **Footer Provider / Git Branch**: Client-side execution of `git` or fallback git inspection on the TUI thread.
2. **Modified Files in Left Sidebar**: `TuiRenderCoordinator` running a background `git status --porcelain` process every 2 seconds via `Task.Run`.
3. **Extension Load Status**: A 200ms `AddTimeout` polling loop in `TuiRenderCoordinator` and an independent 2-second background refresh latch in `InteractiveMode.cs` querying the daemon via RPC.
4. **Extension Shortcuts**: Background RPC latches repeatedly polling the server for shortcut lists.
5. **Slash Command Auto-Completions**: Autocompletion querying the daemon asynchronously over WebSocket rather than using pre-hydrated in-memory state.

These polling loops and out-of-band subprocess executions caused CPU overhead, lock contention, and input latency spikes on the UI thread and render workers.

## Decision

Shift from client-driven polling to an **asynchronous daemon-pushed state architecture**:

1. **Server-Pushed WebSocket Events**:
   - `session_metrics`: Pushed by `LiveServerSession` upon turn completion, agent end, session tree compaction, and session switch. Emits token counts, cost, context percentage, context window, auto-compact state, and cached git branch info.
   - `extension_load_status`: Pushed whenever extension discovery and loading states transition (total, active, blocking active, ready, failed, and failure diagnostics).
   - `modified_files`: Pushed whenever modified files change or session turns modify working directory files.

2. **Hydrated Server Session Snapshot**:
   - `ServerSessionSnapshot` enriched with `Footer`, `ModifiedFiles`, `ExtensionLoadStatus`, `Shortcuts`, and `Commands`.
   - On initial attach or session switch, the client immediately receives complete metadata and populates in-memory caches.

3. **Zero-Polling TUI Client**:
   - Client `RemoteTuiBackend` maintains in-memory caches hydrated purely from snapshot and push events.
   - `TuiRenderCoordinator` eliminates background `git status` process spawning and 200ms `AddTimeout` polling loops.
   - `InteractiveMode.cs` removes all `shortcutCacheGate`, `loadStatusCacheGate`, `completionCacheGate`, and `Interlocked` background refresh workers.
   - Slash command completions resolve in 0ms from in-memory cached command lists.
   - Footer and sidebar render directly from immutable `TuiRenderState` in 0µs.

4. **Shared Session Metrics & Git Tracking**:
   - Shared `SessionMetricsCalculator` and `ModifiedFilesTracker` provide centralized, asynchronous, non-blocking calculations used by both daemon and local modes.

## Consequences

### Positive
- **Instantaneous UI Rendering**: All metadata (footer, sidebar modified files, extension load status, shortcuts, command completions) is read synchronously from immutable in-memory state in 0µs without waiting for RPC or spawning child processes.
- **Zero Background Polling**: No periodic timers, no background `git status` child processes, and no RPC latches contending for locks.
- **Resilience**: The daemon acts as the single source of truth for session and environment state, ensuring multiple attached clients stay consistently synchronized.

### Negative / Trade-offs
- The server session must listen to harness lifecycle events and dispatch wire events to connected clients, adding minimal WebSocket frame traffic when state actually changes.
