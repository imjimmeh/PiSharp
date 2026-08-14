---
name: tsbridge-parity
description: >
  Use when changing the PiSharp <-> JavaScript Pi TypeScript bridge parity
  contract: TsBridgeManifestFactory.CreateApiSurfaceManifest(), Runtime(...)
  manifest entries, runtime actions, snapshot fields, RuntimeSnapshotFields,
  RuntimeExtensionBinder.BuildSessionSnapshotAsync, or the Node API wrappers
  (TsBridgeRunner.mjs, piApi.mjs, uiApi.mjs). The manifest is a contract, not a
  roadmap — do not add planned/unsupported stubs.
type: specialist
scope:
  - src/PiSharp.TsBridge/**
  - tests/PiSharp.TsBridge.Tests/**
  - AGENTS.md
related_skills:
  - extension-platform
  - tools-and-commands
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# TypeScript Bridge Parity Contract

## When to use this skill

Use this skill when:

- adding or changing a TsBridge manifest entry;
- wiring a runtime action end-to-end;
- adding a snapshot field;
- fixing parity test failures;
- exposing a Pi API member to TypeScript extensions.

Typical tasks include:

- adding `Runtime(...)` manifest entries with full layer wiring;
- adding `Snapshot(...)` manifest entries populated with live data;
- updating `TsBridgeRunner.mjs`, `runner/piApi.mjs`, or `runner/uiApi.mjs`;
- running the TsBridge test project after parity changes.

Do not use this skill for:

- extension authoring in general — use [extension-platform](../extension-platform/SKILL.md);
- tool registration — use [tools-and-commands](../tools-and-commands/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the parity manifest (`TsBridgeManifestFactory.CreateApiSurfaceManifest()`);
- the runtime-action wiring stack;
- snapshot population (`RuntimeExtensionBinder.BuildSessionSnapshotAsync`);
- the Node API wrappers.

This area does not own:

- the extension loading mechanism itself (extension-platform);
- product-specific plugin logic (plugin-portfolio).

## Architecture

`TsBridgeManifestFactory.CreateApiSurfaceManifest()` is the source of truth for
PiSharp <-> JavaScript Pi TypeScript bridge parity. Every manifest entry must be
implemented end-to-end before being declared — the manifest is a contract, not a
roadmap.

Two entry kinds exist:

- **Runtime(...)**: imperative operations. Each must be wired through all layers:
  manifest -> runtime action (`TsExtensionHost.RuntimeActionAsync`) ->
  `ExtensionRuntimeBinding`/`SessionRuntime` -> Node wrapper.
- **Snapshot(...)**: state fields. Each must be added to `RuntimeSnapshotFields`,
  populated with live data in `RuntimeExtensionBinder.BuildSessionSnapshotAsync`,
  and exposed from Node context without hard-coded fallbacks
  (`() => true`, `() => false`, `() => undefined`, `() => {}` are forbidden).

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Manifest factory | `src/PiSharp.TsBridge/TsBridgeManifestFactory.cs` | Parity contract source of truth |
| Runtime actions | `src/PiSharp.TsBridge/` (TsBridgeRuntimeActions/protocol manifest) | Declared runtime operations |
| Extension host | `src/PiSharp.TsBridge/TsExtensionHost.cs` | `RuntimeActionAsync` dispatch |
| Runtime binding | `src/PiSharp.Runtime/RuntimeExtensionBinder.cs` | Binds runtime actions; builds session snapshot |
| Snapshot fields | `src/PiSharp.TsBridge/RuntimeSnapshotFields.cs` | Declared snapshot fields |
| Node wrappers | `src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs`, `runner/piApi.mjs`, `runner/uiApi.mjs` | Exposed Pi API to TS extensions |
| Parity tests | `tests/PiSharp.TsBridge.Tests/` | Enforce manifest contract |

### Main flow

1. `CreateApiSurfaceManifest()` declares the surface.
2. For `Runtime(...)` entries: `TsExtensionHost.RuntimeActionAsync` receives the
   action; `RuntimeExtensionBinder.BindRuntimeActions` binds it to the runtime;
   the Node wrapper invokes it.
3. For `Snapshot(...)` entries: `BuildSessionSnapshotAsync` collects live values
   into the snapshot; the Node context exposes them.
4. Tests in `tests/PiSharp.TsBridge.Tests/` enforce that declared entries are
   implemented (no planned/unsupported statuses).

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Manifest | `CreateApiSurfaceManifest()` output; parity contract |
| Runtime entry | Imperative operation declared in the manifest |
| Snapshot entry | State field declared in the manifest, populated live |
| Node wrapper | The `piApi.mjs`/`uiApi.mjs`/`TsBridgeRunner.mjs` surface TS extensions see |
| Fallback stub | Forbidden hard-coded snapshot value (`() => true` etc.) |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.TsBridge/TsBridgeManifestFactory.cs`](../../../src/PiSharp.TsBridge/TsBridgeManifestFactory.cs)
- [`src/PiSharp.Runtime/RuntimeExtensionBinder.cs`](../../../src/PiSharp.Runtime/Runtime/RuntimeExtensionBinder.cs)
- [`src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs`](../../../src/PiSharp.TsBridge/Node/TsBridgeRunner.mjs)
- [`tests/PiSharp.TsBridge.Tests/`](../../../tests/PiSharp.TsBridge.Tests/)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Runtime` (runtime binding), `src/PiSharp.Extensions`
  (extension surface).

### Consumed by

- TypeScript extensions (`extensions/*`).
- The daemon for extension loading (see [daemon-protocol](../daemon-protocol/SKILL.md)).

### External systems

- Node.js runtime for the sidecar bridge.

## Invariants

The following must remain true:

1. `CreateApiSurfaceManifest()` is a contract: every declared entry is fully
   implemented.
2. No `Planned(...)`, `not-yet-supported`, stale phase labels, or unsupported
   reasons appear in the manifest; no broad compatibility stubs or fake helpers.
3. Every `Runtime(...)` entry is wired through all layers (manifest -> runtime
   action -> binding -> Node wrapper).
4. Every `Snapshot(...)` entry is populated with live data — no hard-coded
   fallbacks.
5. PiSharp may add functionality beyond the JavaScript version, but must not
   break existing JavaScript-compatible extension contracts.

## Common change workflows

### Add a Runtime(...) manifest entry

1. Add the entry to `CreateApiSurfaceManifest()`.
2. Add the runtime action declaration (TsBridgeRuntimeActions/protocol manifest).
3. Handle it in `TsExtensionHost.RuntimeActionAsync`.
4. Bind it through `RuntimeExtensionBinder` (or `SessionRuntime` for session
   operations).
5. Expose it in the Node wrapper (`TsBridgeRunner.mjs` / `piApi.mjs` /
   `uiApi.mjs`).

Files commonly changed together:

- `src/PiSharp.TsBridge/TsBridgeManifestFactory.cs`
- `src/PiSharp.Runtime/RuntimeExtensionBinder.cs`
- `src/PiSharp.TsBridge/Node/runner/piApi.mjs` (or `uiApi.mjs`/`TsBridgeRunner.mjs`)

Validation:

```bash
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
```

### Add a Snapshot(...) manifest entry

1. Add the entry to `CreateApiSurfaceManifest()`.
2. Add the field to `RuntimeSnapshotFields`.
3. Populate it with live data in `RuntimeExtensionBinder.BuildSessionSnapshotAsync`.
4. Expose it from Node context without fallback stubs.

Files commonly changed together:

- `src/PiSharp.TsBridge/TsBridgeManifestFactory.cs`
- `src/PiSharp.TsBridge/RuntimeSnapshotFields.cs`
- `src/PiSharp.Runtime/RuntimeExtensionBinder.cs`

Validation:

```bash
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet test tests/PiSharp.TsBridge.Tests/PiSharp.TsBridge.Tests.csproj
```

Run conditionally (cross-project runtime changes):

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- Parity tests are the enforcement mechanism — do not weaken them to ship an
  unimplemented entry.
- Node.js must be available for the bridge to run; parity tests may require it.

## Common mistakes

- Do not declare a manifest entry before the full wiring stack exists.
- Do not populate snapshots with `() => true`/`() => false`/`() => undefined`/
  `() => {}` placeholders.
- Do not remove or relabel existing JavaScript-compatible entries — backward
  compatibility is a hard requirement.

## Legacy and deprecated patterns

- The manifest previously tolerated phase labels and "planned" entries; that
  practice is banned — implement end-to-end or do not declare.

## Existing authoritative documentation

- [`AGENTS.md`](../../../AGENTS.md), "TypeScript bridge parity contract" section

  * Covers the manifest-as-contract rule, layer wiring, snapshot rules, and test
    commands.
  * Treat as authoritative.
  * Does not cover per-entry implementation detail.

## Known ambiguity and technical debt

- The full list of current snapshot/runtime parity entries changes with the
  manifest; re-read `CreateApiSurfaceManifest()` before assuming coverage.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.TsBridge/TsBridgeManifestFactory.cs`](../../../src/PiSharp.TsBridge/TsBridgeManifestFactory.cs)
- [`src/PiSharp.Runtime/RuntimeExtensionBinder.cs`](../../../src/PiSharp.Runtime/Runtime/RuntimeExtensionBinder.cs)
- [`tests/PiSharp.TsBridge.Tests/`](../../../tests/PiSharp.TsBridge.Tests/)
- [`AGENTS.md`](../../../AGENTS.md)
