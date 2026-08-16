# Remote TUI: model-switch failure, dropped sends, and the session-snapshot KeyNotFoundException

Status: **Resolved** (2026-08-15). Root-cause write-up for future maintainers and agents.

## Symptom

Remote TUI client (`PiSharp.Tui` over `PiSharp.Client` to a daemon `PiSharp.Server`):

1. Switch model with `/model` → a `[error] Error: The given key was not present in the Dictionary.`
   appears (a .NET `KeyNotFoundException`, parameterless message).
2. Afterwards, sending a message does nothing: the prompt text clears from the input box but no
   agent turn starts.

There are **two independent root causes**. Each had to be fixed; the second is what actually
dropped sends.

## Root cause #1 — `get_session_snapshot` wire-contract mismatch (`KeyNotFoundException`)

### Flow

`/model` → `TuiCommandController` → `run_command` RPC → daemon `DaemonCommandHost` → `UiSelectAsync`
(`src/PiSharp.Cli/Modes/DaemonCommandHost.cs`) → `context.Session.UiBridge.RequestUiAsync` →
`ui_request` flat event → TUI inline selection → user picks → `ui_response` → model switch applies.
After every command the client dispatches a **post-dispatch `get_session_snapshot`** refresh to
re-hydrate the transcript/branch. That snapshot refresh is where the `KeyNotFoundException` is
thrown (surface: `TuiCommandController.cs`, `AppendSystem($"Error: {ex.Message}")`).

### The contract break

- Daemon side, `PiServerWebSocketHandler.GetSessionSnapshotAsync`
  (`src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs`) filled
  `ServerSessionSnapshot.BranchEntries` (`IReadOnlyList<object>`)
  from `runtime.GetForkableEntriesAsync(...)` (`src/PiSharp.Runtime/Runtime/SessionRuntime.cs:539`),
  which returns **anonymous projections** `{ Id, ParentId, Role }` — no `type`, no `timestamp`.
- Client side, `ClientToTuiAdapter.ToSessionSnapshot` deserializes each entry through
  `SessionTreeEntryJsonConverter.Read` (`src/PiSharp.Agent/Serialization/SessionTreeEntryJsonConverter.cs`),
  which unconditionally reads `type` / `timestamp` → `KeyNotFoundException` on **any** snapshot
  that contains at least one entry.
- Because `Session.AppendEntriesAsync` updates in-memory storage before persisting, a model change
  (`ModelChangeEntry`) is visible to the snapshot **immediately** — so the error fired after the
  first switch that persisted (`/model #1` and `#2` both applied daemon-side then showed the error).
- The local (non-remote) TUI did not hit this: `InteractiveMode.cs:125` hydrates from
  `runtime.Session.GetBranchAsync(...)` — full `SessionTreeEntry`s.

### Fix

`GetSessionSnapshotAsync` now emits **full records**, matching the local-client parity:

```csharp
await runtime.Session.GetBranchAsync(cancellationToken: token)   // IReadOnlyList<SessionTreeEntry>
```

`Session.GetBranchAsync` = `Storage.GetPathToRootAsync(leafId)` — the branch path to root as real
`MessageEntry` / `ModelChangeEntry` / … records. They serialize through `ServerJsonSerializer.Options`
(camelCase + the agent converters) into the exact `type`/`id`/`timestamp`… shape the client converter
reads back.

**Do not** homogenize the other consumer: `get_fork_messages` /
`runtime.GetForkableEntriesAsync` intentionally keeps the lightweight `{ Id, ParentId, Role }`
projection (`src/PiSharp.Cli/Modes/RpcMode.cs:159-161`; `RpcModeTests` pins that shape).

## Root cause #2 — daemon auto-cancel never notifies the client ⇒ inline selection leaks ⇒ submits swallowed

This is what made "send does nothing" happen (prompt clears, no turn).

### Flow

1. `/model`'s `ui_request` is a **`select`** kind → the daemon bridge gives it the long
   `InteractiveKindTimeout` (5 min, `ServerUiBridge.InteractiveKindTimeout`).
2. The TUI answers via `TuiInlineSelectionCoordinator.SelectInlineAsync(...)` —
   `TuiHost.cs` → `ExtensionUiBridgeHost.Select` → inline picker. A `ui_request` handler blocks the
   inbox pump (`RemoteTuiBackend.ProcessInboxAsync` awaited `HandleUiRequestAsync` inline).
3. If the user does not answer (or the 5-min window elapses / the parent token cancels),
   `ServerUiBridge.RequestUiAsyncCore` resolves its `TaskCompletionSource` as
   `Cancelled: true` **silently** — it emits **no event** to the client.
4. Client-side the per-request handler token is the backend-lifetime `_cts.Token`, never cancelled →
   `SelectInlineAsync` never returns → `_selectionSession` stays non-null **forever**.
5. `TuiPromptSubmissionCoordinator.HandleSubmitAsync` has
   `if (_inlineSelection.CompleteInlineSelection(text)) return;` — with a stale selection session,
   every subsequent Enter is swallowed. `PromptEditorController.SubmitAsync` clears the box *before*
   submit, so the swallowed submit leaves the box cleared with no turn. Only Ctrl+C / restart
   escapes.

### Fix

- **Server** (`src/PiSharp.Server/UiBridge/ServerUiBridge.cs`): when `RequestUiAsyncCore` auto-cancels
  (timeout registration **or** parent-token cancellation) it now emits a flat **`ui_cancelled`**
  event carrying `requestId` onto the **same** session lane as the original `ui_request`
  (`EmitUiCancelled`). If the client answers early, the timeout registration is disposed and nothing
  is emitted.
- **Client** (`src/PiSharp.Client/RemoteTuiBackend.cs`):
  - `HandleUiRequestAsync` now runs the handler **off the inbox pump** with a **per-request linked
    `CancellationTokenSource`** kept in `_pendingUiRequests[requestId]`. This is required: a blocking
    select handler would otherwise starve the queued `ui_cancelled` envelope behind it (the inbox
    awaits each `ApplyEnvelopeAsync`).
  - `ApplyEnvelopeAsync` handles `ui_cancelled` by cancelling that request's linked CTS, which fires
    `SelectInlineAsync`'s `token.Register(() => _dispatch(CancelInlineSelection))` → the inline
    session ends → subsequent submits dispatch normally.
  - The handler still sends a `ui_response(cancelled)` — the daemon already removed the pending
    request, so a late `ui_response` is dropped harmlessly.

## Wire-contract invariants (keep these)

- `get_session_snapshot` → `ServerSessionSnapshot.BranchEntries` = **full `SessionTreeEntry`
  records** (root-to-leaf branch) — the client converter requires `type`/`timestamp`.
- `get_fork_messages` → `{ entries: [{ Id, ParentId, Role }] }` — lightweight projection, unchanged.
- `ui_request` / `ui_cancelled` are flat session-lane events (`AgentSessionEvent.FromServer`);
  `ui_cancelled` carries `{ requestId }`. Unknown flat types are a no-op on the
  client (`ClientToTuiAdapter.ToHarnessEvent` returns null) and benign for the SDK consumer.
- A pending `ui_request`'s auto-cancel must **always** notify the attached client — never resolve
  the server TCS silently while the client still holds an interactive session.

## Key files

| Concern | Path |
|---|---|
| Snapshot emit (Fix #1) | `src/PiSharp.Server/WebSockets/PiServerWebSocketHandler.cs` (`GetSessionSnapshotAsync`) |
| Full-branch source | `src/PiSharp.Agent/Sessions/Session.cs` (`GetBranchAsync` → `GetPathToRootAsync`) |
| UI bridge auto-cancel (Fix #2) | `src/PiSharp.Server/UiBridge/ServerUiBridge.cs` (`RequestUiAsyncCore`, `EmitUiCancelled`) |
| Client inbox + ui handler (Fix #2) | `src/PiSharp.Client/RemoteTuiBackend.cs` (`ApplyEnvelopeAsync`, `HandleUiRequestAsync`, `CancelPendingUiRequest`) |
| Snapshot converter (client) | `src/PiSharp.Client/ClientToTuiAdapter.cs` (`ToSessionSnapshot`) |
| Converter that needs `type`/`timestamp` | `src/PiSharp.Agent/Serialization/SessionTreeEntryJsonConverter.cs` |
| Inline select + swallow | `src/PiSharp.Tui/Interactive/Sessions/TuiInlineSelectionCoordinator.cs`, `src/PiSharp.Tui/Interactive/Prompt/TuiPromptSubmissionCoordinator.cs` |
| Wire-invariant projections | `src/PiSharp.Runtime/Runtime/SessionRuntime.cs` (`GetForkableEntriesAsync`), `src/PiSharp.Cli/Modes/RpcMode.cs` |
| Local TUI parity | `src/PiSharp.Cli/Modes/InteractiveMode.cs` (`GetBranchAsync` hydration, `backend.UiRequestHandler` wiring) |

## Logging / debugging notes

- **The remote TUI client DOES have its own file logging**: `CliFileLogging.cs` (in
  `src/PiSharp.Cli/Logging/`) → default `~/.pi/PiSharp/logs/pi.log`, per-session retargeting to
  `logs/<encodedCwd>/<session>.log`. Daemon and client share the file when the daemon is hosted by
  the CLI. (See also `docs/plan-client-logging.md` §1, which corrected the original "no client
  logging" assumption.)
- Not every client logger category appears in `pi.log`: in the bug window,
  `TuiHost` / `TuiFooterSnapshotProvider` logged, but `TuiCommandController`,
  `TuiPromptSubmissionCoordinator`, and `TuiInlineSelectionCoordinator` had no lines. When verifying
  remote-TUI behavior end-to-end, rely on daemon-side evidence
  (`process_input` / `prompt` / `get_session_snapshot` lines) rather than expecting per-component
  client log lines.
- Timeline tells the story: a `run_command` for `/model` that ends `isError=False` with
  `hasMessage=True` after the 5-min window is a **normal auto-cancel**, not an error — the
  `KeyNotFoundException` came from the *post-dispatch snapshot refresh*, not the `run_command`.
- After the fix, a failed/abandoned select must end with a `ui_cancelled` emission (daemon) and the
  next real send must produce a `process_input` / `prompt` line in the daemon log.

## Verification

Regression coverage (TDD, red → green):

- `PiServerWebSocketHandlerTests.GetSessionSnapshot_ReturnsFullBranchEntries` — daemon snapshot
  must carry a full `ModelChangeEntry` (previously anonymous projection → failed).
- `PiServerUiBridgeTests.Request_ShortResponseTimeout_EmitsUiCancelledForTheRequest` — a
  short-timeout auto-cancel must emit `ui_cancelled` with the request id on the session lane.
- `RemoteTuiBackendTests.UiCancelled_EndsPendingUiRequestAsCancelled` — a `ui_cancelled` envelope
  must end a pending `ui_request` handler as cancelled (client per-request CTS path).
