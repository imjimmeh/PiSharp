# Unresolved Questions

Verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba` (2026-08-14).
These items could not be fully resolved during bootstrap with repository-only
evidence.

## Extension discovery paths

- **Question:** What is the exact ordered list of extension discovery
  directories at runtime?
- **What we found:** `docs/pisharp-runtime.md` and the developer guide mention
  plugin/extension directories; `~/.pi/extensions` is referenced as a discovery
  line that the runtime doc omits. Extension loading is configured from
  user-specific `settings.json` (paths under `~/.pisharp/extensions`), which is
  environment data, not repository data.
- **Why unresolved:** The authoritative list lives in user settings + runtime
  behavior, not in a single repo file we could verify without executing the
  application.
- **How to resolve:** Run the CLI with a fresh user profile and observe
  discovery, or add a runtime doc section listing the ordered roots.

## CLI flag surface vs documentation

- **Question:** Which CLI flags/modes exist in `PiSharp.Cli` but are missing
  from `docs/pisharp-runtime.md`?
- **What we found:** The audit identified at least: `acp` mode, `--approval-mode`,
  `--stats`, `--export/--import/--share`, `--attach`, `--profile`,
  `--no-skills/--no-prompt-templates/--no-themes`,
  `--check-updates/--no-check-updates`, `--local`.
- **Why unresolved:** We did not enumerate the full `PiSharp.Cli` argument
  parser; `--help` output is the authoritative flag list and requires running
  the CLI.
- **How to resolve:** Run `dotnet run --project src/PiSharp.Cli -- --help`
  (and per-mode help) and reconcile with `docs/pisharp-runtime.md`.

## Daemon ring-buffer retention constant

- **Question:** What is the exact retained-envelope count for the daemon's
  event ring buffer (summary analysis says ~100k)?
- **Why unresolved:** The constant lives in `src/PiSharp.Server` internals and
  was not located during the map phase.
- **How to resolve:** Grep `src/PiSharp.Server` for the ring-buffer capacity
  constant and document it in `docs/adr/2026-08-14-daemon-client-architecture.md`.

## JSONL session format internals

- **Question:** Are the JSONL header/leaf-entry details in
  `docs/analysis/pisharp-current-state-catalog.md` §2.6 current?
- **What we found:** The catalog is the only reference for JSONL internals, but
  the doc is stale elsewhere (e.g. §11 daemon claim). We did not re-verify §2.6
  against `JsonlSessionRepo` line by line.
- **How to resolve:** Compare §2.6 against `src/PiSharp.Agent/Sessions` reader
  and update the developer guide with a current JSONL section.

## Provider count drift

- **Question:** What is the exact current built-in provider count?
- **What we found:** ~11 built-in providers registered in
  `BuiltInProviders.RegisterAll` (per analysis); `docs/pisharp-providers.md`
  lists providers.
- **Why unresolved:** The count drifts as providers are added; no single doc is
  kept in lockstep.
- **How to resolve:** Treat `src/PiSharp.Ai/BuiltInProviders.cs` as the source
  of truth; re-audit when the count matters.

## Terminal.Gui local reference paths

- **Question:** The local toolkit copies at `G:\tmp\tgui-nuget` (v2.0.0) and
  `G:\tmp\tgui` (latest) are machine-specific. Should they be referenced from
  skills?
- **Decision recorded:** No — skills avoid absolute local paths; they reference
  the in-repo `docs/terminal-gui-*.md` references instead.

## Pipeline stage drift

- **Question:** Do the pipeline stage names/order in
  `docs/pisharp-developer-guide.md` match `src/PiSharp.Agent/Loops/` exactly?
- **What we found:** The guide describes durability-first ordering
  (PersistenceStage -> PhaseTransitionStage -> ToolMiddlewareStage ->
  ExtensionDispatchStage -> ListenerNotificationStage); the harness implements a
  stage pipeline.
- **Why unresolved:** We verified the harness file and the guide's description
  independently but did not exhaustively diff every stage class against the doc
  wording.
- **How to resolve:** Re-read `src/PiSharp.Agent/Loops/` and update the guide
  section if stage names drifted.

## Compatibility wording in implementation status

- **Question:** `docs/pisharp-implementation-status.md` P24 wording implies
  `src/PiSharp.Git` was removed; the project exists. What exactly changed?
- **What we found:** Only the CLI slash-command file was removed
  (`ShareSessionSlashCommand.cs`-style path); the plugin project remains.
- **Why unresolved:** The status doc wording is ambiguous about which artifact
  was removed.
- **How to resolve:** Clarify the status row to say the CLI slash command was
  removed while the plugin remains.
