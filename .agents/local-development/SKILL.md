---
name: local-development
description: >
  Use when building, testing, running, or installing PiSharp locally, working
  with CI, debugging a failing test, or following TDD conventions. Covers the
  solution layout (52 src + 43 test projects), verified dotnet commands, CI
  gates, Makefile/install flows, and per-project test shortcuts.
type: orientation
scope:
  - PiSharp.sln
  - Directory.Build.props
  - Makefile
  - install.sh
  - install.ps1
  - .github/workflows/**
  - tests/**
related_skills:
  - repository-overview
  - tools-and-commands
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Local Development, Build, and Test

## When to use this skill

Use this skill when:

- building or testing the solution;
- running a single test project while iterating;
- running the CLI locally;
- installing PiSharp as a global tool;
- debugging a failing test or build;
- checking what CI runs.

Typical tasks include:

- `dotnet build PiSharp.sln`;
- `dotnet test tests/<Project>.Tests/<Project>.Tests.csproj`;
- running `PiSharp.Cli` locally;
- reproducing a CI failure locally.

Do not use this skill for:

- repository orientation/architecture — use [repository-overview](../repository-overview/SKILL.md);
- TUI-specific behavior — use [tui-development](../tui-development/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the verified build/test command set;
- solution membership facts;
- CI workflow contents;
- install/packaging flows.

This area does not own:

- what individual tests assert (owned by each concern's skill);
- test authoring conventions beyond command-level guidance.

## Architecture

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Solution | `PiSharp.sln` | 95 entries: 52 src + 43 test C# projects + 4 solution folders |
| Shared build props | `Directory.Build.props` | TFM (net10.0), common settings |
| CLI project | `src/PiSharp.Cli` | Executable; packable as global tool `PiSharp.Cli` |
| CI | `.github/workflows/ci.yml` | Build + test on `master` push/PR |
| Release | `.github/workflows/release.yml` | Pack + NuGet push on `v*` tags |
| Makefile | `Makefile` | `build`, `install`, `build-and-install` |

### Main flow

1. Restore: `dotnet restore PiSharp.sln`.
2. Build: `dotnet build PiSharp.sln`.
3. Test: `dotnet test PiSharp.sln` (or a single test project for iteration).
4. Install as global tool: `dotnet pack src/PiSharp.Cli/PiSharp.Cli.csproj -c Release`
   then `dotnet tool install --global --add-source <package dir> PiSharp.Cli`
   (Makefile `install` target does this).

## Project terminology

| Term | Meaning in this repository |
|---|---|
| TFM | `net10.0` (set in `Directory.Build.props`) |
| Test project | `tests/<Project>.Tests/<Project>.Tests.csproj`, xunit |
| Model catalog generation | Build-time regeneration of `BuiltInModels.g.cs`; CI disables it with `-p:RunModelCatalogGenerationOnBuild=false` |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`PiSharp.sln`](../../../PiSharp.sln): solution membership.
- [`Makefile`](../../../Makefile): `build`, `install`, `build-and-install` targets.
- [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml): CI gate.
- [`.github/workflows/release.yml`](../../../.github/workflows/release.yml): release gate.

## Dependencies and consumers

### Depends on

- .NET 10 SDK (CI uses `dotnet-version: '10.0.x'`).
- NuGet packages (xunit, etc.).

### Consumed by

- All other skills (they cite these commands in their validation sections).

### External systems

- NuGet.org (release publishing).
- GitHub Actions (CI/release workflows).

## Invariants

The following must remain true:

1. Every C# `src` project has a matching `tests/<Project>.Tests` project.
2. `PiSharp.sln` builds and tests green before merging.
3. CI builds with `-p:RunModelCatalogGenerationOnBuild=false` (generation runs
   locally via the model generator); do not rely on CI to regenerate the catalog.
4. Release tags are `v*`; version is the tag minus `v`.

## Common change workflows

### Build and test the whole solution

```bash
dotnet build PiSharp.sln
dotnet test PiSharp.sln
```

### Run one test project while iterating

```bash
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter <NamePattern>
```

Replace `PiSharp.Tui.Tests` with the project under test; every project follows
`tests/<Project>.Tests`.

### Reproduce CI locally

CI runs (on `master` push/PR):

```bash
dotnet restore PiSharp.sln
dotnet build PiSharp.sln --configuration Release --no-restore -p:RunModelCatalogGenerationOnBuild=false
dotnet test PiSharp.sln --configuration Release --no-build
```

### Run the CLI locally

```bash
dotnet run --project src/PiSharp.Cli
```

### Install as a global tool

```bash
dotnet pack src/PiSharp.Cli/PiSharp.Cli.csproj -c Release --version <version> -o ./nupkg
dotnet tool install --global --add-source ./nupkg PiSharp.Cli
```

(`make install` / `make build-and-install` wrap these; see `Makefile`.)

Files commonly changed together:

- `PiSharp.sln` — when adding/removing a project;
- `Directory.Build.props` — when changing common build settings.

Validation:

```bash
dotnet build PiSharp.sln
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
```

Run conditionally:

```bash
dotnet test tests/<Project>.Tests/<Project>.Tests.csproj
dotnet test PiSharp.sln
```

## Operational considerations

- PiSharp logs: `~/.pi/PiSharp/logs`.
- PiSharp settings: `~/.pi/PiSharp/settings.json`.
- Session files: `~/.pi/agent/sessions`.
- Agent npm extensions: `~/.pi/agent/npm`.

## Common mistakes

- Do not run the full solution test suite when a single project changed — use
  the per-project test command.
- Do not hand-edit generated files like `BuiltInModels.g.cs`; regenerate via the
  model generator (see [model-providers](../model-providers/SKILL.md)).
- Do not add projects to the solution that are intentionally external
  (`src/pisharp-session-webapp`, `extensions/` TypeScript packages).

## Legacy and deprecated patterns

- NativeAOT publish: superseded by `dotnet tool install --global` packaging.
- Per-project `dotnet test` with `--filter`: still the recommended iteration loop.

## Existing authoritative documentation

- [`AGENTS.md`](../../../AGENTS.md): "Build and test commands" section lists the same
  commands verified here.

## Known ambiguity and technical debt

- The Makefile hardcodes a user home (`C:/Users/jimme`); it is a local-machine
  convenience, not portable CI.
- `install.sh`/`install.ps1` exist at repo root but are not exercised by CI.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`PiSharp.sln`](../../../PiSharp.sln) (95 entries)
- [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml)
- [`.github/workflows/release.yml`](../../../.github/workflows/release.yml)
- [`Makefile`](../../../Makefile)
- [`AGENTS.md`](../../../AGENTS.md)
