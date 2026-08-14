# PiSharp Implementation Status

Status of the P01–P31 portfolio as of 2026-08-14. Every test count below was verified by running
the suite directly (the "evidence" line is the final `dotnet test` output).

Legend: ✅ done · 🟡 partially done (deviations recorded in the plan/plugin docs) · ⏳ deferred (daemon-gated, scheduled)

## Foundation wave

| Plan | Deliverable | Status | Evidence (dotnet test) |
| --- | --- | --- | --- |
| P01 daemon completion-resilience | Daemon + event-sourced client (`PiSharp.Server`, `PiSharp.Client`, `DaemonMode`, `Coordination.Daemon`) | ✅ (user merge `666f957`) | server suite green |
| P02 settings/state API | `IExtensionApi.Settings`/`State`, `settings_changed`, versioned KV | ✅ | Extensions 183/183 |
| P03 session control | `IExtensionSessionApi` promotion | ✅ | Extensions 183/183 |
| P04 packages/skills | `PiSharp.Packages`, `ISkillProvider`, `ExtensionSkillDefinition`, `ManagedSkillStore`, skill runner + `skill_execution_start/end`, TS bridge actions | ✅ | Extensions 183/183 · Runtime 216/216 · Cli 342/342 · TsBridge 198/198 · Server.DaemonCommands 15/15 |
| P05 TUI/theme | `IExtensionUi` theme + keybindings; C8 server `ThemeRegistry` + `list_themes`/`set_theme` | ✅ | Tui 689/689 · Server 54/54 |
| P06 subagent framework | `SubagentSessionService`, spawn guardrails, plan-mode policy hook | ✅ | Agent 187/187 |
| P07 agent messaging | `PiSharp.AgentMessaging` (roster, send/read tool, outbox, event lane) | ✅ | AgentMessaging 81/81 |

## Wave 2

| Plan | Deliverable | Status | Evidence |
| --- | --- | --- | --- |
| P08 memory system | `PiSharp.Memory` + Abstractions + File/Off backends | ✅ | Memory 91/91 |
| P09 continual harness | `PiSharp.ContinualHarness` (`/refine`) | ✅ | ContinualHarness 42/42 |
| P10 rules engine | `Rule`/`IRuleProvider`/`IExtensionApi.Rules`, `RuleApplyMode`, auto-retry events, rules dir + sticky providers, `--no-rules` | ✅ | Extensions 174/174 · Agent 187/187 |
| P11 foreign compat | `PiSharp.Plugins.ForeignCompat` rule providers | ✅ | ForeignCompat 47/47 |
| P12 IDE protocol clients | `PiSharp.Plugins.Lsp`, `PiSharp.Plugins.Debug`, `PiSharp.Plugins.ProtocolJsonRpc` | ✅ | Lsp 31/31 · ProtocolJsonRpc 14/14 · Debug 2/2 |
| P13 ACP mode | `CliMode.Acp` + `--approval-mode` | ✅ | Cli 342/342 |
| P14 plan mode | `PiSharp.PlanMode` (C1–C4) + C5 daemon RPC | ✅ | PlanMode 79/79 · PlanMode.Rpc 12/12 |
| P15 eval/bench | `PiSharp.Eval` + `PiSharp.Eval.Kernel.CSharp` (Roslyn; Python kernel skipped per plan) | ✅ | Eval 58/58 |
| P16 advisor | `PiSharp.Advisor` + daemon `advisor_note` lane | ✅ | Advisor 28/28 · Advisor.Daemon 11/11 |
| P17 browser automation | Playwright-driven browser tooling | ✅ | Browser 37/37 |
| P18 profiles | `PiAgentPaths.FromCwd` profile param | ✅ | Cli 342/342 |
| P19 provider breadth | `GitHubCopilotProvider`, `EnvApiKeyDetector`, endpoint recipe | ✅ | Ai 212/212 |
| P20 model roles | `PiSharp.ModelRoles`, `ResolveRoleAsync`, TS `modelRolesResolve`/`setModelRole` | ✅ | ModelRoles 35/35 |
| P21 declarative tools | `PiSharp.DeclarativeTools` | ✅ | DeclarativeTools 70/70 |
| P22 SDK | `PiSharp.Sdk` typed daemon client | ✅ | Sdk 16/16 |
| P23 continuity suite | `PiSharp.Continuity` + daemon wire commands | ✅ | Continuity 43/43 · Server.ContinuityCommands 26/26 |
| P24 git integrations | removed `ShareSessionSlashCommand` + `src/PiSharp.Git` | ✅ | Git 99/99 |
| P25 observability | `IExtensionApi.Telemetry`, `PiSharp.Telemetry.Otlp`, C5–C7 `get_metrics`/`get_session_stats` | ✅ | Telemetry.Otlp 24/24 · Server.Observability 18/18 |
| P26 internal URLs | `PiSharp.InternalUrls` (`skill://`, `agent://`, `diff://`) | ✅ | InternalUrls 65/65 |
| P27 MCP client | `PiSharp.Mcp` + HTTP/stdio transports; C2 `mcp_status` | ✅ | Mcp 39/39 · Mcp.Status 6/6 |
| P28 research | `PiSharp.Research` + Serper/GoogleCse/Brave | ✅ | Research 82/82 |
| P29 permissions gating | `PiSharp.Permissions` | ✅ | Permissions 91/91 |
| P30 AST/hashline | AST structural editing + hashline registry | ✅ | Ast 68/68 |
| P31 distribution | self-update dispatch + `VersionInfo` | ✅ | Cli 342/342 |
## Verify gate

Full-union `dotnet build PiSharp.sln` (96 projects): 0 errors. Full-union `dotnet test PiSharp.sln`:
all 42 test projects green except the pre-existing Tui integration timing flakes
(`InlineSelection*` / `DeferredStartup*` — pass in isolation; established pre-existing, not
regressions). Verified 2026-08-14.

## Known deferred (outside this portfolio's scope)

- Python eval kernel (P15 plan notes) — deliberately not built.
- Daemon E2E integration test run of P23 (attach/replay rendering) — covered by unit + server
  command tests; full E2E requires a running daemon session.
