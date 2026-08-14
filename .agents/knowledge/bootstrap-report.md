# PiSharp Bootstrap Report

Repository skill catalogue generated from evidence at commit
`646522ccc6edc48acc39e4545cd120af9f1dafba` (2026-08-14, `main`).

## Repository type

Large, actively developed C# agent platform (~52 src projects + 43 test
projects in `PiSharp.sln`, plus a Vite/TypeScript webapp outside the solution).
Port of the original JavaScript "Pi" coding agent. Monorepo with layered
architecture: dependency-light contract layers (`Abstractions`, `Agent.Core`),
runtime composition (`Runtime`), daemon/client split (`Server`/`Client`/`Cli`/
`Sdk`), extension platform (`Extensions`, `PluginHost`, `TsBridge`), and a
large shipped plugin portfolio. C# on net10.0, xunit, TDD culture, extension
contracts must stay backward compatible with JS Pi.

## Scope

- Analysed: `src/` (52 C# projects + webapp), `tests/` (43 projects),
  `docs/`, `scripts`, `.github/workflows`, `PiSharp.sln`, `Makefile`,
  `Directory.Build.props`, `AGENTS.md`.
- Excluded by design: `javascript/` (reference-only), `bin/`, `obj/`,
  `node_modules/`, `.worktrees/`, `tmp/`.
- Constraint: documentation/maps/catalogues/skills only — no product code,
  tests, or config modified.

## Verification commit

`646522ccc6edc48acc39e4545cd120af9f1dafba` (main, 2026-08-14).

## Units mapped

53 units: 3 application, 2 code-generator, 4 contract, 44 library.

- 52 C# projects in `PiSharp.sln` (51 top-level `src/` dirs + nested
  `src/PiSharp.TsBridge/Tools/ShimExportGenerator/ShimExportGenerator.csproj`).
- 1 non-C# unit: `src/pisharp-session-webapp` (Vite/TypeScript, npm-based, not
  in the solution).
- All 52 C# projects are solution members (a scout claim that the sln omits
  Eval/ModelRoles/Packages/Research was verified false and corrected).

Full detail: `knowledge/repository-map.yaml` (53 units).

## Concerns mapped

14 concerns in `knowledge/concern-map.yaml`:

- Cross-cutting: extension-platform, daemon-protocol, settings-resource-discovery,
  tool-system, sessions-persistence, compatibility-layer.
- Domain: model-providers, agent-harness, plugin-portfolio.
- Application: tui-remote-shell.
- Specialist: tsbridge-parity.
- Workflow: add-native-extension, add-provider, add-daemon-command.

High-risk areas flagged in the map: TsBridge parity wiring (manifest ->
runtime -> Node; no fallback stubs), collectible ALC load/unload, daemon
API-key auth + event-sourced wire protocol, session JSONL compatibility.

Full detail: `knowledge/concern-map.yaml` (14 concerns with invariants, risk,
evidence).

## Documentation audit

`knowledge/documentation-audit.md` (16.9 KB) documents conflicts:

- `docs/specs/SDD-pi-csharp-port.md` and `docs/analysis/*` are historical/stale
  in places (Spectre.Console -> Terminal.Gui 2.0; NativeAOT -> dotnet global
  tool; `[AgentTool]` -> `IAgentTool`; jiti -> manifest shim generator).
- `docs/pisharp-runtime.md` omits parts of the CLI surface (`acp`,
  `--approval-mode`, `--stats`, `--export/--import/--share`, `--attach`,
  `--profile`, `--no-skills/--no-prompt-templates/--no-themes`,
  `--check-updates/--no-check-updates`, `--local`) and the `~/.pi/extensions`
  discovery line.
- `docs/pisharp-implementation-status.md` P24 wording implies
  `src/PiSharp.Git` was removed — it exists; only a CLI slash-command file was
  removed.
- `docs/analysis/pisharp-current-state-catalog.md` §11 (daemon "not in main")
  is stale.
- Only 1 ADR exists (`2026-08-14-daemon-client-architecture.md`).

## Skill candidates

18 candidates scored (max 42) in `knowledge/skill-catalogue.yaml`:

- Dedicated (12): repository-overview (30), local-development (28),
  extension-platform (40), tsbridge-parity (38), plugin-portfolio (30),
  daemon-protocol (39), tui-development (25), model-providers (26),
  sessions-and-persistence (33), settings-and-resources (27), agent-harness
  (39), tools-and-commands (27).
- Merged (6): architecture-boundaries -> repository-overview; testing-strategy
  -> local-development; add-native-extension -> extension-platform;
  add-daemon-command -> daemon-protocol; add-provider -> model-providers;
  compatibility-layer -> sessions-and-persistence + settings-and-resources +
  repository-overview.
- Rejected (6): browser-automation, pisharp-session-webapp, memory-system,
  research-search-providers, tsbridge-shim-generator, ai-model-generator
  (each covered by a broader skill; reasons recorded).

## Generated skills

12 skills + router generated under `skills/`:

| Skill | Type |
|---|---|
| `skills/project/repository-overview/SKILL.md` | orientation |
| `skills/project/local-development/SKILL.md` | orientation |
| `skills/project/extension-platform/SKILL.md` | cross-cutting |
| `skills/project/tsbridge-parity/SKILL.md` | specialist |
| `skills/project/plugin-portfolio/SKILL.md` | domain |
| `skills/project/daemon-protocol/SKILL.md` | cross-cutting |
| `skills/project/tui-development/SKILL.md` | application |
| `skills/project/model-providers/SKILL.md` | cross-cutting |
| `skills/project/sessions-and-persistence/SKILL.md` | cross-cutting |
| `skills/project/settings-and-resources/SKILL.md` | cross-cutting |
| `skills/project/agent-harness/SKILL.md` | domain |
| `skills/project/tools-and-commands/SKILL.md` | cross-cutting |
| `skills/SKILL.md` | project router |

## Validation status

All validation gates pass (per project-skill-toolkit `validation.md`):

- **Structural:** 12 unique skill IDs; router lists all 12; catalogue
  dedicated set == generated skill set (bidirectional); every merge/reject has
  a recorded reason.
- **Reference:** every markdown link resolves to an existing repository path or
  skill (0 broken links after path-depth normalization for
  `skills/project/<id>/`); `related_skills` IDs all resolve; no absolute local
  paths in skills (machine-specific Terminal.Gui paths excluded by decision);
  router back-links verified — all 12 skills link to the router in their
  Important entry points section.
- **Command:** every command cited in skills appears in `AGENTS.md`, CI
  workflows, `Makefile`, or per-project test conventions; CI builds use
  `-p:RunModelCatalogGenerationOnBuild=false` which is reflected in the model
  skill.
- **Evidence:** consequential claims cite primary evidence (implementation
  files, tests, ADR, docs) in each skill's Evidence section; uncertainty is
  surfaced in Known ambiguity sections; stale-doc conflicts are called out
  explicitly.
- **Routing:** 10 representative PiSharp tasks tested against the router
  (add plugin, change daemon event, add provider, fix TUI keybinding, change
  compaction, add slash command, wire TsBridge surface, debug settings,
  build/test, add pipeline stage) — all 10 route to the correct skill; 2
  routing rows allow multi-skill matches (architecture-boundary changes +
  owning concern).
- **Contradiction:** 7 cross-skill claim categories reviewed
  (architecture_boundaries, settings_precedence, tsbridge_contract,
  daemon_purity, hasui_guard, compaction, collectible_alc); 0 contradictions
  found; every duplicated claim has a canonical skill and non-canonical skills
  link to it. Compaction ownership is split correctly: agent-harness owns
  trigger timing, sessions-and-persistence owns the mechanism — they
  cross-link.
- **Freshness:** all 12 skills + router stamped with commit
  `646522ccc6edc48acc39e4545cd120af9f1dafba` (2026-08-14); no incremental
  updates claim broader verification.

Final status: **passed**.

## Output artefacts

| Artefact | Path |
|---|---|
| Analysis charter | `knowledge/analysis-charter.yaml` |
| Repository map | `knowledge/repository-map.yaml` (53 units) |
| Documentation audit | `knowledge/documentation-audit.md` |
| Concern map | `knowledge/concern-map.yaml` (14 concerns) |
| Skill catalogue | `knowledge/skill-catalogue.yaml` (18 candidates, 12 approved) |
| Unresolved questions | `knowledge/unresolved-questions.md` |
| Bootstrap report | `knowledge/bootstrap-report.md` |
| Project router | `skills/SKILL.md` |
| Generated skills | `skills/project/<id>/SKILL.md` (12) |

## Unresolved risks

1. Extension discovery directory list is partly user-settings-dependent
   (`~/.pi/extensions` vs `~/.pisharp/extensions`); not fully verifiable from
   repository data alone.
2. `docs/pisharp-runtime.md` CLI surface is incomplete relative to code;
   skills cite code as authoritative and flag the gap.
3. Session JSONL internals documented only in a stale analysis doc; skills
   point to code as source of truth.
4. Daemon ring-buffer retention constant not located; replay depth semantics
   flagged as tuning-dependent.

See `knowledge/unresolved-questions.md` for resolution steps.
