---
name: settings-and-resources
description: >
  Use when working with settings precedence and resource discovery: the
  PiSettingsStore layers (global/project x legacy/PiSharp), PiResourceLoader
  discovery (extensions, skills, prompts, prompt templates, themes, context
  files, packages, system prompts, append prompts), PiAgentPaths path
  resolution, CLI flag overrides, and extension scoped settings/state
  (extensions.<ns> namespaces).
type: cross-cutting
scope:
  - src/PiSharp.Compatibility/**
  - src/PiSharp.Extensions/**
  - src/PiSharp.Runtime/**
  - tests/PiSharp.Compatibility.Tests/**
related_skills:
  - sessions-and-persistence
  - extension-platform
  - model-providers
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Settings Layers and Resource Discovery

## When to use this skill

Use this skill when:

- adding or changing a settings key;
- changing settings precedence or path resolution;
- changing resource discovery (extensions, skills, prompts, themes, ...);
- debugging why a setting is ignored;
- working with extension scoped settings/state;
- aligning with legacy Pi settings paths.

Typical tasks include:

- modifying `PiSettingsStore` layer merge order;
- changing `PiResourceLoader` discovery roots;
- adding a new CLI flag that overrides a setting;
- adding a settings key with legacy-path fallback.

Do not use this skill for:

- session file format — use [sessions-and-persistence](../sessions-and-persistence/SKILL.md);
- provider credentials — use [model-providers](../model-providers/SKILL.md);
- the extension API — use [extension-platform](../extension-platform/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the settings store and layer precedence;
- path resolution (`PiAgentPaths`);
- resource discovery (extensions, skills, prompts, prompt templates, themes,
  context files, packages, system prompts, append prompts);
- extension scoped settings/state namespacing.

This area does not own:

- session persistence format (sessions-and-persistence);
- credential storage specifics (model-providers).

## Architecture

`PiSettingsStore` merges four layers: **global/project x legacy/PiSharp**:

- `~/.pi/agent/settings.json` (legacy global)
- `~/.pi/PiSharp/settings.json` (PiSharp global)
- `<cwd>/.pi/settings.json` (legacy project)
- `<cwd>/.pi/PiSharp/settings.json` (PiSharp project)

`PiResourceLoader` discovers extensions, skills, prompts, prompt templates,
themes, context files, packages, system prompts, and append prompts from the Pi
home and project directories. CLI flags override settings values. Extension
scoped settings/state use `extensions.<ns>.` prefixes with first-writer-wins
claim semantics.

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Settings store | `src/PiSharp.Compatibility` (`PiSettingsStore`) | Four-layer settings merge |
| Path resolution | `src/PiSharp.Compatibility` (`PiAgentPaths`) | Pi home/project path resolution |
| Resource loader | `src/PiSharp.Compatibility` (`PiResourceLoader`) | Discovers extensions/skills/prompts/themes/... |
| Scoped settings | `src/PiSharp.Extensions` (`ExtensionScopedSettings`) | Namespace-prefixed extension settings/state |
| Settings tests | `tests/PiSharp.Compatibility.Tests/` | Layer precedence guard |

### Main flow

1. Store loads the four layer files (those that exist).
2. Layers merge with precedence (project over global; PiSharp over legacy).
3. CLI flags override merged values.
4. Resource loader discovers resources from Pi home + project dirs.
5. Extensions read/write scoped settings under `extensions.<ns>.` keys.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| PiSettingsStore | Four-layer settings store |
| PiAgentPaths | Path resolution for Pi home/project dirs |
| PiResourceLoader | Resource discovery |
| Legacy layer | JS Pi-style settings paths (`~/.pi/agent/settings.json`, `<cwd>/.pi/settings.json`) |
| Scoped settings | `extensions.<ns>.`-prefixed keys, first-writer-wins |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Compatibility`](../../../src/PiSharp.Compatibility)
- [`tests/PiSharp.Compatibility.Tests/`](../../../tests/PiSharp.Compatibility.Tests/)
- [`src/PiSharp.Extensions/ExtensionScopedSettings.cs`](../../../src/PiSharp.Extensions/ExtensionScopedSettings.cs)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Compatibility` internals; nothing external.

### Consumed by

- The runtime, CLI (flags), extensions (scoped settings), the daemon.

### External systems

- The filesystem (settings/resources under Pi home and cwd).

## Invariants

The following must remain true:

1. Layer precedence: project over global; PiSharp-specific over legacy —
   `<cwd>/.pi/PiSharp/settings.json` wins over `<cwd>/.pi/settings.json` wins
   over `~/.pi/PiSharp/settings.json` wins over `~/.pi/agent/settings.json`.
2. CLI flags override settings values.
3. Legacy Pi settings paths keep working (compatibility requirement).
4. Extension scoped settings/state are namespace-prefixed
   (`extensions.<ns>.`) and first-writer-wins.
5. Resource discovery honors both Pi home and project roots.

## Common change workflows

### Add a settings key

1. Add the key to the store's typed surface (and its legacy alias if JS Pi had
   one).
2. If a CLI flag controls it, register the flag before applying values
   (AGENTS.md pitfall: register extension flags/options before applying CLI
   flag values).
3. Add a precedence test in `tests/PiSharp.Compatibility.Tests`.

Files commonly changed together:

- `src/PiSharp.Compatibility/**`
- `src/PiSharp.Cli/**` (flag)
- `tests/PiSharp.Compatibility.Tests/**`

Validation:

```bash
dotnet test tests/PiSharp.Compatibility.Tests/PiSharp.Compatibility.Tests.csproj
```

### Change resource discovery

1. Change `PiResourceLoader` roots/order.
2. Update tests covering discovery precedence.
3. If extension discovery paths change, update
   [extension-platform](../extension-platform/SKILL.md) expectations too.

Files commonly changed together:

- `src/PiSharp.Compatibility/Resources/**`
- `tests/PiSharp.Compatibility.Tests/**`

Validation:

```bash
dotnet test tests/PiSharp.Compatibility.Tests/PiSharp.Compatibility.Tests.csproj
dotnet test PiSharp.sln
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Compatibility.Tests/PiSharp.Compatibility.Tests.csproj
```

Run conditionally:

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- Never copy secrets (API keys, OAuth tokens) into settings docs or tests —
  document mechanisms and paths only (see model-providers).
- Settings paths are user-specific; tests should use isolated temp homes, not
  the real `~/.pi`.

## Common mistakes

- Do not break legacy settings paths — JS Pi compatibility is a hard
  requirement.
- Do not apply CLI flag values before registering the flag/option.
- Do not let extension scoped keys collide across namespaces — always use the
  `extensions.<ns>.` prefix.
- Do not hardcode user-specific home paths in production code.

## Legacy and deprecated patterns

- Legacy Pi paths (`~/.pi/agent/settings.json`, `<cwd>/.pi/settings.json`) are
  intentional compatibility surfaces — keep them working.
- The developer guide's CLI surface is incomplete: flags like `acp`,
  `--approval-mode`, `--stats`, `--export/--import/--share`, `--attach`,
  `--profile`, `--no-skills/--no-prompt-templates/--no-themes`,
  `--check-updates/--no-check-updates`, and `--local` exist in code but are not
  all documented in `docs/pisharp-runtime.md`.

## Existing authoritative documentation

- [`docs/pisharp-runtime.md`](../../../docs/pisharp-runtime.md)

  * Covers settings/session basics.
  * Gaps: omits several CLI flags/modes and the `~/.pi/extensions` discovery
    line; treat code as authoritative where they disagree.

## Known ambiguity and technical debt

- The full CLI flag surface is under-documented; `--help` on `PiSharp.Cli` is
  the authoritative flag list.
- Extension discovery paths depend partly on user home layout
  (`~/.pi/extensions`), which is environment-specific.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.Compatibility`](../../../src/PiSharp.Compatibility)
- [`tests/PiSharp.Compatibility.Tests/`](../../../tests/PiSharp.Compatibility.Tests/)
- [`src/PiSharp.Extensions/ExtensionScopedSettings.cs`](../../../src/PiSharp.Extensions/ExtensionScopedSettings.cs)
- [`AGENTS.md`](../../../AGENTS.md) (register-flags-before-applying pitfall)
