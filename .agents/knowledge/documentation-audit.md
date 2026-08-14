# PiSharp Documentation Audit

Verified at commit `646522ccc6edc48acc39e4545cd120af9f1dafba` (main), 2026-08-14.

Read-only audit of `docs/**/*.md` + `AGENTS.md`/`README.md`/`CHANGELOG.md`/`TODO.md` against
current repository state. Every path claim below was verified by read/glob/grep; test-count claims
were NOT re-run (read-only mandate) and are marked accordingly.

## A. Inventory (all files classified)

**Root (5)**: `AGENTS.md` (canonical agent rules, current), `README.md` (authoritative overview,
current), `CHANGELOG.md` (historical; v1.0.0 2026-06-08 only, Unreleased empty), `TODO.md`
(historical scratch list: 2 open bugs/improvements — provider-error visibility, `/resume` parity,
UI sidebars; items overlap already-shipped sidebars feature), LICENSE/install scripts (out of doc
scope).

**docs/ root guides (15, all current/authoritative)**: pisharp-developer-guide, -runtime, -tools,
-providers, -native-extensions, -typescript-extensions, -agent-coordination, -vs-pi,
-adding-a-provider, -plugins, -implementation-status, -slash-command-development,
-tui-shortcut-development, -tui-tracing, + terminal-gui-architecture/input/examples-reference
(3, supplementary TGUI pattern references).

**docs/adr (1)**: `2026-08-14-daemon-client-architecture.md` — the ONLY ADR; canonical for
daemon/client split.

**docs/specs (4)**: SDD-pi-csharp-port.md + PRD-pi-csharp-port.md (historical port intent,
Draft), SDD-subagent-jsonl-mode.md (implemented spec, current), TUI-visual-parity-contract.md
(current contract; all cited source paths verified).

**docs/epics (12)**: EPIC-01..12 + EPIC-subagent-jsonl-mode. Frontmatter statuses: EPIC-07
proposed, EPIC-08 proposed, EPIC-09 in-progress, EPIC-10/11 completed, EPIC-12 implemented,
others undated/older — all historical planning docs for already-landed work; EPIC-12+subagent-jsonl
remain useful as traceability.

**docs/analysis (9)**: current-state-catalog (2026-08-14, evidence-driven snapshot; §11
daemon-in-worktree now stale — main HAS PiSharp.Client), feature-gaps (2026-08-14, basis for
P01–P31; superseded by implementation-status), pisharp-js-pi-extension-parity-gaps +
pisharp-extension-api-inventory + js-pi-extension-api-inventory + ANALYSIS-epic-12-js-extension-parity
(June parity audits; gaps since closed — superseded by pisharp-typescript-extensions.md),
pisharp-non-web-architecture-review (May 2026 review snapshot), terminal-gui-usage-review-2026-05-31
(historical review; references old line numbers), ANALYSIS-tui-thinking-cycle-write-hang (June
investigation, resolved per later plans).

**docs/plans (~99)**: 34 plugin-portfolio files (2026-08-14-plugin-index + 31 P-plans +
2026-08-14-daemon-completion-resilience + 3 daemon-client files) — 31 plans map 1:1 to P01–P31,
ALL marked done in implementation-status; ~60 historical implementation/design plan pairs
(2026-05-26..06-11, e.g. slash-command-refactor, tui-shortcut-refactor, tsbridge-manifest,
skills-extensions-loading, release-pipeline, install-native-extension-dll, oauth-login-logout,
native-agent-coordination, cli-rpc-parity, tui-integration-tests, tui-real-host-profiling,
session-scoped-logs, file-logging-settings, structured-logging, subagent-performance,
epics-11/12 implementation plans) — all for merged work, plus misc (tui-menubar-and-sidebars,
chat-row-extension-customization, EPIC-11-worker-pool-decision, system-prompt-parity/composer-redesign).

**docs/superpowers (9)**: 4 design/implementation pairs (tuihosthandlers-decomposition,
extension-testing-helper, outdated-extension-notifications, shimgenerator-refactor) +
tui-menubar-and-sidebars plan — duplicates the pattern of docs/plans; historical.

**docs/work (1)**: analyse-session-file-creation/ — historical investigation.

## B. Named key docs — classifications

1. **AGENTS.md** — agents; canonical; current. ALL referenced paths verified: PiSharp.sln;
   src/{Abstractions,Agent.Core,Runtime,TsBridge,PluginHost};
   tests/{Agent.Tests,Tui.Tests,TsBridge.Tests} incl. TuiRenderingTests.cs;
   TsBridgeManifestFactory.cs; 3 terminal-gui-*.md; build/test commands match repo (TFM net10.0).
   Parity contract matches TsBridgeManifestTests enforcement
   (`BridgeManifestDoesNotContainRoadmapOrFalseUnsupportedStatuses`). Pitfalls verified against
   code. Omissions: no `acp` mode / `--approval-mode`; no `pisharp daemon`/`stats` commands;
   machine-specific absolute paths (C:\Users\jimme\...) are single-user. Typos "creaed"/"Locatjons".
   should_link: YES — canonical for agent guardrails + parity contract.

2. **pisharp-developer-guide.md** — developers+agents; authoritative; current. Daemon section
   verified line-by-line against `src/PiSharp.Cli/Modes/DaemonMode.cs` (start/stop/status/foreground,
   lease ~/.pi/PiSharp/daemon.json, lock, auto-start) and `src/PiSharp.Server/Hosting/PiServerHost.cs`
   (/health, /ws). Repo layout table omits ~25 plugin projects (Git, Browser, Ast, Subagents, Eval,
   Mcp, Research, Memory, PlanMode, Permissions, AgentMessaging, Continuity, Advisor, Sdk,
   Telemetry.Otlp, InternalUrls, DeclarativeTools, ModelRoles, Packages, Coordination.Daemon,
   transports/backends/kernels — covered instead by pisharp-plugins.md). "Detailed guides" table
   omits pisharp-adding-a-provider.md and the terminal-gui refs. should_link: YES — canonical
   project map.

3. **pisharp-runtime.md** — developers/agents; authoritative; mostly current. Startup sequence,
   settings layers (PiSettingsStore), PiAgentPaths.FromCwd paths, pisharp.append, session
   precedence, resource discovery, --no-resources expansion, mode selection (incl. subagent-json +
   `--mode json -p --no-session` routing — verified in CliParser.cs) all verified.
   **Conflicts/omissions**: (a) CLI mode list omits `acp` — CliArgs.cs help shows
   `--mode <text|json|rpc|subagent-json|acp>`; (b) flag list omits `--approval-mode`, `--stats`,
   `--export/--import/--share`, `--attach`, `--profile`, `--no-skills/--no-prompt-templates/
   --no-themes`, `--check-updates/--no-check-updates`, `--local` (all exist in
   CliArgs.cs/CliParser.cs); (c) native .dll discovery list omits global `~/.pi/extensions`
   (native-extensions doc + PluginHostOptions.FromCwd include it — doc-vs-doc conflict);
   (d) settings list omits `logging` key (file-logging settings exist). should_link: YES.

4. **pisharp-tools.md** — developers/agents; authoritative; current. Verified: BuiltInTools.CreateAll
   = read/bash/edit/write/grep/find/ls; CreateReadOnly = read/grep/find/ls
   (src/PiSharp.Tools/BuiltInTools.cs); IAgentTool contract matches AgentToolContracts.cs; flags
   -t/-nt/-nbt; extension tool registration + middleware; package CLI forms match PiSharp.Packages
   + EPIC-12 (object-form filters deferred). should_link: YES — canonical for tool contracts.

5. **pisharp-native-extensions.md** — developers/agents (extension authors); authoritative; current.
   Verified: NativePluginHost discovery dirs (`<cwd>/plugins`, `<cwd>/.pi/extensions`,
   `~/.pi/extensions`), collectible PluginLoadContext, IExtensionApi surface (matches
   src/PiSharp.Extensions), events list, override policy, UI API, install flow
   (`pisharp install ...dll`). should_link: YES — canonical native-extension guide.

6. **pisharp-typescript-extensions.md** — developers/agents; authoritative; current. Verified:
   TsBridgeRunner.mjs + Node/src/runner/{piApi,uiApi}.ts layout, descriptor cache
   `~/.pi/PiSharp/cache/ts-bridge`, services, createAgentSession, 3 shipped extensions
   (extensions/{workflow-sessions with README, pisharp-embeddings, relevance-filtered-skills} all
   exist), manifest parity contract (TsBridgeManifestFactory.cs, TsBridgeManifestTests.cs,
   RuntimeExtensionBinder.cs, ExtensionRuntimeBinding.cs). should_link: YES — canonical TS-extension
   guide.

7. **pisharp-providers.md** — developers; authoritative; current. Verified: IModelProvider
   (src/PiSharp.Ai/Providers/IModelProvider.cs), built-in API names, EnvApiKeyDetector,
   models.json/auth.json, GitHub Copilot section, catalog recipe +
   ModelsJsonCatalogLoaderTests.cs (exists, tests/PiSharp.Ai.Tests/Models/). should_link: YES.

8. **pisharp-plugins.md** — developers; authoritative; current. All 20+ plugin projects verified to
   exist in src/; test stack claim verified (tests/PiSharp.Memory.Tests.csproj: xunit 2.9.2,
   SDK 17.12.0, runner 2.8.2, IsPackable=false, net10.0). should_link: YES — canonical plugin map.

9. **pisharp-implementation-status.md** — developers/ops; authoritative; current (same-day as verify
   commit). All cited test projects exist. **One misleading row**: P24 reads "removed
   ShareSessionSlashCommand + src/PiSharp.Git" but src/PiSharp.Git EXISTS as the plugin
   (CommitTool.cs, ShareSlashCommand.cs, GitHubGistUploader.cs) and only the CLI built-in
   ShareSessionSlashCommand.cs was removed (verified: src/PiSharp.Cli/Commands/BuiltIn/ has 18
   files, no Share). Test counts are claims from a prior run — structurally plausible, not
   re-verified (read-only). should_link: YES.

10. **pisharp-agent-coordination.md** — agents/developers; authoritative; current. Verified:
    PiSharp.Coordination project + CoordinationDaemon/Client/JsonlStore, repo `.pi/coordination/`
    contains daemon.json + events.jsonl (live evidence), named-pipe design, tools/roster/brief/
    middleware descriptions. Omission: standalone `src/PiSharp.Coordination.Daemon/Program.cs`
    (--repo-root/--pipe-name) exists but is unmentioned; doc says daemon is in-process-only.
    Note the extension's repo-local daemon is distinct from the per-user session daemon
    (PiSharp.Server) — no doc conflates them, but skills should. should_link: YES.

11. **pisharp-vs-pi.md** — developers; authoritative; current (compat-focused). Omits the
    daemon/client split and the P01–P31 portfolio; otherwise verified. should_link: YES.

12. **pisharp-adding-a-provider.md** — developers; authoritative; current. All touchpoint files
    verified (BuiltInProviders.cs, ModelCatalogGenerator.cs + Ai.ModelGenerator command,
    EnvApiKeyDetector.cs, BuiltInProvidersTests.cs). should_link: YES — canonical provider-addition
    recipe.

13. **pisharp-slash-command-development.md** — developers; authoritative; current. Verified:
    src/PiSharp.Cli/Commands/{BuiltInSlashCommandCatalog.cs,BuiltIn/*.cs,SlashCommandRegistryFactory.cs},
    tests/PiSharp.Cli.Tests/Commands/SlashCommandRegistryTests.cs exists. should_link: YES.

14. **pisharp-tui-shortcut-development.md** — developers; authoritative; current. Verified:
    src/PiSharp.Tui/Interactive/{TuiBuiltInShortcutCatalog.cs,BuiltInShortcuts/,TuiShortcutDispatcher.cs,
    TuiShortcutRegistrar.cs,Components/HeaderView.cs}, tests/PiSharp.Tui.Tests/TuiShortcutTests.cs
    exists. should_link: YES.

15. **pisharp-tui-tracing.md** — developers; authoritative; current. Real-host test filters
    referenced in doc — spot-checked suite names exist in tests/PiSharp.Tui.Tests
    (TuiHostIntegrationTests.cs, TuiPerformanceTests.cs); dotnet-trace workflow valid.
    should_link: YES (specialist).

16. **terminal-gui-*.md (3)** — developers (TUI work); supplementary; current. Pattern references
    to G:\tmp\tgui (external, unverifiable from repo; consistent with PiSharp.Tui.csproj
    Terminal.Gui 2.0.0 and AGENTS.md). Not PiSharp-behavior docs — skills should reference, not
    duplicate. should_link: YES (TUI tasks only).

17. **SDD-pi-csharp-port.md / PRD-pi-csharp-port.md** — all; historical (original intent;
    developer-guide explicitly declares source tree authority). **Confirmed divergences from
    implementation**: TUI = "Spectre.Console" (§2/§12) vs actual Terminal.Gui 2.0.0; NativeAOT
    single-binary + bundled Node (§1/§11) vs actual dotnet global tool (no PublishAot anywhere in
    src/); `[AgentTool]` attribute-based tool discovery (§3.2/§5.2/§9.1, PRD G3/US1/FR6.2) — no
    AgentToolAttribute exists in src/ (tools are IAgentTool classes); jiti-based TS loading (§6.3)
    — no jiti in src/PiSharp.TsBridge/Node (manifest shim generator instead); `ILLMProvider`/
    `CliMode.Pipe` naming (§7.1/§10.1) vs IModelProvider/AppMode. PRD FR3.2 lists 25+ providers,
    most shipped only as catalog entries or not at all (recipe path covers). PRD C5 .NET 8.0+
    satisfied by net10.0. should_link: NO (historical; link only from developer-guide already).

18. **docs/adr/2026-08-14-daemon-client-architecture.md** — all; canonical; current. Matches
    implementation (PiServerHost, lease, 100k retained envelopes, gap recovery, command set vs
    ServerCommandTypes). NOTE: only ADR in repo; SDD §9 promised ADRs for TUI library, DI,
    serialization, logging, ALC — never written. should_link: YES — canonical daemon-client
    decision (prefer over the two docs/plans daemon files:
    2026-08-14-daemon-client-architecture.md 43.6KB implementation plan + -design.md 14.1KB
    approved design — both historical execution docs).

## C. Conflicts & stale items (verified)

1. SDD/PRD vs code: Spectre.Console→Terminal.Gui; NativeAOT→global tool; [AgentTool]→IAgentTool
   classes; jiti→shim generator (see B17).
2. pisharp-runtime.md CLI surface omits: `acp` mode, `--approval-mode`, `stats`,
   `--export/--import/--share`, `--attach`, `--profile`, `--no-skills/--no-prompt-templates/
   --no-themes`, `--check-updates/--no-check-updates` (all in CliArgs.cs:7-8,138-141,158-161;
   Program.cs:69-71,156-157; StatsMode.cs).
3. pisharp-runtime.md native-discovery list omits `~/.pi/extensions` vs pisharp-native-extensions.md
   + PluginHostOptions.FromCwd.
4. pisharp-implementation-status.md P24 wording implies src/PiSharp.Git removed — it wasn't (see B9).
5. analysis/current-state-catalog §11 says daemon-client "NOT in main" — now stale; PiSharp.Client/
   DaemonMode are on main.
6. analysis parity-gap docs (May–June) describe missing getCommands/waitForIdle/snapshot stubs —
   all closed per AGENTS.md parity contract + TsBridgeParityTests; treat as historical.
7. pisharp-runtime.md settings list omits `logging`; plugin event-count trivia: feature-gaps says
   42 event names, native-extensions lists a subset — cosmetic.
8. TODO.md items (sidebars, `/resume` parity, provider errors) — sidebars/registerMenuItem shipped
   (plans/tui-menubar-and-sidebars.md + EPIC-10); TODO not updated; stale-ish scratch file.
9. pisharp-plugins.md says daemon surfaces all in ServerContracts.cs — verified file exists with
   matching delegates (PlanModeDaemonContracts.cs, ContinuityHandlers observed).

## D. Behaviour documented only in code (gaps — no doc covers)

- `pisharp stats` command (StatsMode: metrics.jsonl journal, `--live` over daemon WS) — no doc.
- `--check-updates/--no-check-updates` update check; `--attach` flag.
- ACP mode details (AcpMode.cs, AcpApprovalMode yolo|ask|read-only) beyond one status row.
- `PiSharp.Coordination.Daemon` standalone executable.
- Session JSONL format internals (header version 3, leaf entries) — only in
  analysis/current-state-catalog §2.6, not in any current guide.
- `--no-compatibility` leaf-entry semantics details; `pisharp.append` de-dup rules (documented in
  runtime doc — ok).
- Telemetry metrics.jsonl/telemetry.json paths (only in code: InstallIdResolver.cs, StatsMode.cs).

## E. should_link matrix for generated skills

LINK (canonical, current): AGENTS.md; developer-guide; runtime; tools; native-extensions;
typescript-extensions; providers; adding-a-provider; plugins; implementation-status;
agent-coordination; vs-pi; slash-command-dev; tui-shortcut-dev; tui-tracing;
adr/daemon-client; specs/SDD-subagent-jsonl-mode (subagent JSONL work);
specs/TUI-visual-parity-contract (TUI parity work); terminal-gui-* (TUI tasks).

REFERENCE-ONLY (historical/design traceability — link, never treat as current):
specs/SDD+PRD-pi-csharp-port; docs/plans/2026-08-14-plugin-index (portfolio index);
docs/analysis/current-state-catalog (evidence snapshot); docs/analysis/feature-gaps;
docs/plans/2026-08-14-{daemon-client-architecture,daemon-client-architecture-design};
EPIC-12/subagent-jsonl epics; superpowers pairs.

DO NOT LINK: docs/plans 2026-05-26..06-11 implementation plans (execution artifacts, 'For Claude'
TDD framing, superseded), docs/work, TODO.md, CHANGELOG.md, ANALYSIS-* reviews.

## F. Recommendations for the skill catalogue

1. Skills should cite the 15 current docs + ADR as authority and never quote SDD/PRD as current
   behavior (developer-guide already says source tree is authority — preserve that stance).
2. One "plugin portfolio" skill should link plugins.md + implementation-status.md + plugin-index
   plan, not individual P-plans.
3. TUI skills: link terminal-gui refs + tui-shortcut-dev + TUI-visual-parity-contract + tui-tracing;
   do NOT duplicate their content.
4. Gap to fix in docs (out of scope here): runtime doc CLI table + `~/.pi/extensions` discovery
   line + P24 wording + TODO.md refresh.
