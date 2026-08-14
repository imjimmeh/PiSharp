# PiSharp Feature-Gap Declaration & Plugin Portfolio

**Authoritative gap analysis: PiSharp vs Oh My Pi (omp) and Prime Agent (prime).**
Inputs: `master-harness-analysis.md` (canonical normalized catalog, 80 features in 31 categories), `PiSharp\docs\analysis\pisharp-current-state-catalog.md` (PiSharp main-branch surface), `PiSharp\docs\plans\2026-08-14-daemon-client-architecture-design.md` (approved, in-flight daemon + event-sourced client). Everything in the design doc is treated as **in-flight/planned**, not existing.

This document feeds phase 3 of the plugin-planning program: **one planning agent per portfolio entry (§5)**.

---

## 1. Method

Classification rules:

- **covered** — the PiSharp catalog (main branch) shows the capability working end-to-end, or the capability is a strict subset with the missing slice listed (subset gaps are still cataloged in §3).
- **partial** — the capability exists but a named sub-capability is absent; the gap entry names exactly what is missing.
- **missing** — absent from the PiSharp catalog *and* confirmed absent by spot-checks.
- **not-to-build** — present in the master catalog but an anti-goal or irrelevant for PiSharp (§6).

Evidence base:

1. `master-harness-analysis.md` — the exhaustive omp/prime checklist; every gap cites its category and feature name.
2. `pisharp-current-state-catalog.md` — PiSharp's verified surface; every covered claim cites a catalog section.
3. `2026-08-14-daemon-client-architecture-design.md` — cited for every daemon-awareness note.
4. Spot-checks on the PiSharp repo (only where the catalog needed disambiguation): read `ShareSessionSlashCommand.cs` (confirm `/share` = local `File.Copy`, not gist); grep `src\` for `web_search|WebSearch|SearchWeb` (no match → web search absent), `skill://` (no match → URL scheme absent), `(?i)\bmcp\b` (no match → MCP absent), and `planmode|commit|goal|bench|trace|heartbeat|schedule|cron` (no plan mode, commit, goal, bench, or trace surfaces; the only "heartbeat" is `PiSharp.Coordination`'s agent-liveness record, not prime's periodic re-entry). Everything else rests on the catalog; genuinely unverified judgments are marked `[INFERENCE]`.

### Coverage pass (acceptance data)

| Bucket | Count | Notes |
| --- | --- | --- |
| Master-catalog features | **80** | 31 categories, `master-harness-analysis.md` §3 |
| Already covered | **21** | §2 table |
| Gap catalog | **51** | master-feature gaps, GAP-01…GAP-51 |
| Not-to-build | **8** | §6 |
| Extension-surface core-changes (from PiSharp catalog §7.5) | **5** | GAP-52…GAP-56 — not master features; required extension surfaces that feature plugins depend on |
| Total gap entries | **56** | §3 |

`21 + 51 + 8 = 80` master features; every one is in exactly one bucket. Every one of the 56 gap entries maps to exactly one portfolio entry (§5).

---

## 2. Already covered (do not re-plan)

| Master feature | Master § | PiSharp evidence (current-state catalog) |
| --- | --- | --- |
| Session persistence format | §3.1 | JSONL with header line + tree entries (version 3), lazy write, `LeafEntry` compat mode (§2.6) |
| Subagent spawning | §3.2 | `SubagentSessionService` in-process child sessions, TS `createAgentSession()`, `--mode subagent-json` (§10) |
| Skill format & discovery | §3.4 | `SkillManager`: `SKILL.md` + YAML frontmatter, discovery dirs, `skill:<name>` selection, prompt-section injection (§9) |
| Context files (AGENTS.md / CLAUDE.md) | §3.5 | Ancestor walk from global agent dir + cwd, `--no-context-files`, `SYSTEM.md`/`APPEND_SYSTEM.md` (§9) |
| Image attachment handling | §3.11 | `PromptAsync(text, images?)`, `ReadTool` reads images, `ImageUtilities` auto-resize (§2.2, §3.2). TUI clipboard-paste flow unverified — trivial client addition, not a gap |
| Config files & layering | §3.12 | `PiSettingsStore`: 4 layers (global legacy → global PiSharp → project legacy → project PiSharp) + `pisharp.append` arrays (§9) |
| Auth storage | §3.12 | `~/.pi/agent/auth.json` (0600), `FileOAuthStorage`, env-var/literal key forms, precedence (§4.4–4.5). Missing prime's `!cmd` shell-command key form — trivial, not cataloged |
| Environment variables | §3.12 | Per-provider env map (`ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, …) + `PISHARP_*` (§4.4, §8) |
| Extension model | §3.14 | Native `IExtensionApi` + TS bridge (`registerTool/Command/Shortcut/Provider/Skill`, renderers, decorators, prompt contributors, services), collectible plugin host, `reload_extensions` (§7) |
| Hooks / event interception | §3.14 | 42 extension event names + tool-call middleware (`Blocked`/`ModifyToolResult`), mutating hooks for input/bash/session-switch/fork (§7.1, §8) |
| Slash commands | §3.15 | 22 built-in names + extension/skill/prompt commands in one registry shared by TUI/RPC/server (§5.4) |
| Terminal UI | §3.16 | `TuiHost` (Terminal.Gui), event-batched rendering; kept untouched by the daemon client (§5.2, design doc §Client state model) |
| Print mode (one-shot) | §3.17 | `--print` / `--mode text` (§5.2) |
| JSON mode (event stream) | §3.17 | `--mode json` emits `AgentHarnessEvent` JSONL (§5.2) |
| RPC mode | §3.17 | JSONL stdio request/response, ~30 commands incl. `run_command`, `get_commands`, `get_fork_messages`, `export_html` (§5.2) |
| Context compaction | §3.20 | `CompactionService` — defaults `reserveTokens 16384` / `keepRecentTokens 20000` (identical to prime's), mid-turn cut points, branch summaries (§2.5) |
| Context tree / branching | §3.20 | Parent-id session tree, `/tree`, `navigateTree`, `BranchSummaryEntry` summaries (§2.6) |
| Fork / resume / continue | §3.20 | `--continue/--resume/--session/--fork/--no-session/--session-dir`, `/new /session /fork /clone /resume`, RPC + server commands (§2.6, §5.5) |
| Export to HTML | §3.21 | `--export`, `/export`, `export_html` RPC command. Standalone-renderer parity with prime unverified [INFERENCE] |
| Code/repo search | §3.28 | `grep` / `find` / `ls` built-ins with truncation and temp spill (§3.2) |
| Windows support | §3.31 | .NET `net10.0`, native Windows runtime (workstation win32); no WSL bridge needed |
| Tool infrastructure (no single master entry) | §3.2/§3.14/§3.28 | `read`/`write`/`edit`/`bash` + typed JSON schemas, truncation, `FileMutationQueue` serialization, `BashSpawnHook` (§3) |

---

## 3. Gap catalog

### §3.1 Session & daemon model

#### GAP-01 — Daemon / background execution
- **Master:** `master-harness-analysis.md` §3.1 "Daemon / background execution".
- **Status:** missing (in-flight — the approved design is not in main).
- **Why:** The single biggest continuity feature both forks' users rely on: close the terminal, the session keeps working; re-attach anywhere.
- **omp/prime:** omp is a single foreground process with modes — durability is file-based, no resident workers (omp §5). Prime is a detached supervisor with one resident worker per active root session tree; `prime-agent attach <agent>` reattaches; sessions keep running after disconnect (prime §4, §5.6).
- **Daemon-awareness:** Fully absorbed by the design doc: per-user daemon, lease at `~/.pi/PiSharp/daemon.json`, auto-start from the CLI client, warm `SessionRuntime`s, `attach { sessionId, sinceSequence }` with replay, multi-terminal attach, idle-timeout disposal, `daemon start/stop/status` (§Architecture, §Daemon lifecycle, §Wire protocol, phases 0–4). The gap is "missing" only because the design is in-flight.
- **Shape:** core-change (the daemon mode itself; the extension surface it creates is GAP-33/GAP-52 territory).
- **Deps:** none. Portfolio: P01.

#### GAP-02 — Crash recovery / process containment
- **Master:** `master-harness-analysis.md` §3.1 "Crash recovery / process containment".
- **Status:** partial — missing worker crash-retry and supervisor adoption.
- **Why:** A daemon that dies mid-run or a worker that crashes should not lose live sessions; containment keeps one bad turn from taking down everything.
- **omp/prime:** omp has only hidden in-process argv workers for background tasks (omp §5). Prime runs workers/kernels as separate processes, retries worker crashes at 250ms/1s/5s, and on supervisor death a worker acquires an atomic launch lease and spawns a replacement supervisor that adopts live workers (prime §4, §5.6).
- **Daemon-awareness:** The design gives process separation (client detach survives; live session disposed only after idle timeout; in-progress turns run to completion — §Daemon lifecycle) but has **no** worker crash-retry or supervisor adoption; multi-client conflicts are explicitly deferred (§Open questions). This gap extends the design with a lease/adoption layer.
- **Shape:** core-change (daemon resilience machinery; no extension surface needed).
- **Deps:** on P01 (daemon). Portfolio: P01.

#### GAP-03 — Idle eviction / retention
- **Master:** `master-harness-analysis.md` §3.1 "Idle eviction / retention".
- **Status:** partial — missing tree-level eviction/passivation.
- **Why:** Bounded memory/process footprint when many sessions pile up; predictable retention semantics.
- **omp/prime:** omp none. Prime evicts whole idle session trees (`idleEvictionMinutes`, default 90) and passivates idle children (prime §5.6, §8).
- **Daemon-awareness:** The design has per-`LiveServerSession` idle-timeout disposal (default 5 min, configurable, only with zero attached clients — §Daemon lifecycle). Missing: eviction of whole session *trees* and passivation of idle subagent children.
- **Shape:** core-change (daemon session registry policy).
- **Deps:** on P01 (daemon). Portfolio: P01.

### §3.2 Subagents / task agents

#### GAP-04 — Structured output from subagents
- **Master:** `master-harness-analysis.md` §3.2 "Structured output from subagents".
- **Status:** missing.
- **Why:** Schema-validated child results ("typed yield") eliminate prose-parsing and sibling merge conflicts — the difference between fan-out that works and fan-out that flails.
- **omp/prime:** omp: child calls `yield` returning a schema-validated object (frontmatter `output` / task `outputSchema` precedence) — "no prose to parse" (omp §6). Prime: message-based replies with parent follow-up via registry, no typed contract (prime §5.2).
- **Daemon-awareness:** Subagents run in-process in the daemon; the event stream must carry a typed completion event so clients can dispatch on results. No typed-output contract exists today (PiSharp catalog §10).
- **Shape:** feature-plugin (subagent framework).
- **Deps:** on P06. Portfolio: P06.

#### GAP-05 — Agent definitions
- **Master:** `master-harness-analysis.md` §3.2 "Agent definitions".
- **Status:** missing.
- **Why:** Declarative, file-backed agent personas make subagents reusable, shareable, and prompt-tunable without code changes.
- **omp/prime:** omp: markdown agent files with YAML frontmatter (`name`, `systemPrompt`, `tools`, `spawns`, `model`, `thinkingLevel`, `output`, `autoloadSkills`, …) (omp §6). Prime: none — subagents are programmatic `rlm()` args, refined into specs via the harness (prime §5.2, §5.4).
- **Daemon-awareness:** Agent definitions are discovered and resolved server-side; the client needs only a `get_agents`-style query. Discovery happens per daemon start (resources are daemon-resident per the design's component diagram).
- **Shape:** feature-plugin (subagent framework).
- **Deps:** on P06. Portfolio: P06.

#### GAP-06 — Agent discovery & precedence
- **Master:** `master-harness-analysis.md` §3.2 "Agent discovery & precedence".
- **Status:** missing.
- **Why:** A first-wins discovery ladder (project → user → extension → bundled) is what makes agent definitions composable across repos and machines.
- **omp/prime:** omp: five-tier discovery with first-wins dedup and a bundled set (`task`, `scout`, `designer`, `reviewer`, …) (omp §6). Prime: parent-scoped bookkeeping only (prime §5.2).
- **Daemon-awareness:** Server-side concern (mirrors the design's daemon-resident theme/prompts/resources); no new wire surface beyond a listing query.
- **Shape:** feature-plugin (subagent framework).
- **Deps:** on GAP-05, on P06. Portfolio: P06.

#### GAP-07 — Recursion depth & spawn guardrails
- **Master:** `master-harness-analysis.md` §3.2 "Recursion depth & spawn guardrails".
- **Status:** missing.
- **Why:** Without depth caps and self-recursion guards, agent-spawning agents can recurse into unbounded cost loops.
- **omp/prime:** omp: `resolveEffectiveSubagentPolicy()` shared with eval — disabled agents, spawns policy, `PI_BLOCKED_AGENT` self-recursion guard, `maxRecursionDepth` default 2 (at cap the child loses `task`) (omp §6). Prime: depth cap + parent-attributed usage (prime §5.2).
- **Daemon-awareness:** Enforcement is server-side inside the subagent service; the design's event stream already carries `agent_*` events so policy violations can surface to clients. `SubagentSessionService` has no depth/policy machinery today (PiSharp catalog §10).
- **Shape:** feature-plugin (subagent framework).
- **Deps:** on P06. Portfolio: P06.

#### GAP-08 — Agent-to-agent messaging
- **Master:** `master-harness-analysis.md` §3.2 "Agent-to-agent messaging".
- **Status:** partial — missing daemon-level routing guarantees, a model-facing `hub` tool, and a watch/steer UI.
- **Why:** Agents that can message each other (not just the parent) enable self-organizing multi-agent workflows; without delivery guarantees the pattern is unreliable.
- **omp/prime:** omp: `hub` tool + Agent Hub UI to watch/steer/kill live subagents — same-process supervision (omp §6). Prime: first-class daemon-level delivery — `agent_message` skill, family roster/catalog, direct-agent target validation (prime §5.3, §5.5).
- **Daemon-awareness:** `PiSharp.Coordination` already has a named-pipe daemon with `send_message`/`get_inbox`/roster and heartbeat records, but it is in-process (lost when the owning agent exits), single-repo, best-effort JSONL replay, and invisible to the daemon design (PiSharp catalog §7.5 #12, §12 #11). Natural evolution: fold coordination routing into the daemon; add roster + message events to the protocol.
- **Shape:** feature-plugin (agent messaging & coordination).
- **Deps:** on P01 (daemon), on GAP-06 (roster/discovery). Portfolio: P07.

#### GAP-09 — Skills inside subagents
- **Master:** `master-harness-analysis.md` §3.2 "Skills inside subagents".
- **Status:** partial — no per-subagent skill policy or pinning.
- **Why:** Task-appropriate skill surfaces (and their absence) materially change child quality; both references inherit-only, so this is a parity-plus opportunity.
- **omp/prime:** Both inherit-only: omp adds only additive `autoloadSkills` frontmatter (unknown names silently ignored) (omp §6, §13); prime children inherit skills/config/tools from the parent (prime §5.2).
- **Daemon-awareness:** Server-side per-harness selection (`SetSelectedSkills` exists per harness — PiSharp catalog §2.2); a policy surface rides the agent-definition format (GAP-05).
- **Shape:** feature-plugin (subagent framework).
- **Deps:** on GAP-05, on P06. Portfolio: P06.

#### GAP-10 — Subagent isolation / worktrees
- **Master:** `master-harness-analysis.md` §3.2 "Subagent isolation / worktrees".
- **Status:** missing.
- **Why:** Filesystem-level isolation (CoW/worktree) lets parallel subagents edit without colliding and makes experimental agents safe.
- **omp/prime:** omp: `pi-iso` isolation backends (CoW, overlayfs/ProjFS, git worktree, recursive copy) per subagent (omp §2, §6, §15). Prime: context isolation only (own session dirs, same filesystem) (prime §5.2).
- **Daemon-awareness:** Worktree creation is a server-side operation (git in the daemon); a subagent's `cwd`/env is part of its session metadata, so isolation must be designed into the daemon's session model, not bolted on the client.
- **Shape:** feature-plugin (subagent framework).
- **Deps:** on P06. Portfolio: P06.

### §3.3 Memory & self-improvement

#### GAP-11 — Memory tools
- **Master:** `master-harness-analysis.md` §3.3 "Memory tools".
- **Status:** missing.
- **Why:** Model-facing memory verbs (`retain`/`recall`/`reflect`/`learn`) turn project knowledge into a queryable asset instead of a prompt blob.
- **omp/prime:** omp: `retain`/`recall`/`reflect`/`memory_edit`/`learn` tools, project-scoped, gated by `memory.backend` (omp §10). Prime: memory as harness state edited via `/refine`, no model-facing verbs (prime §5.4).
- **Daemon-awareness:** Memory tools execute server-side; state must be daemon-resident (surviving client attach/detach). The `pisharp-embeddings` extension (PiSharp catalog §7.4) already ships an embeddings service the vector half can build on.
- **Shape:** feature-plugin (memory system).
- **Deps:** on GAP-52 (settings API) and GAP-53 (extension state store) for backend config/state. Portfolio: P08.

#### GAP-12 — Memory backends
- **Master:** `master-harness-analysis.md` §3.3 "Memory backends".
- **Status:** missing.
- **Why:** Off/file/vector backends make memory a pluggable capability rather than a hard-coded store.
- **omp/prime:** omp: `"off" | "local" | "hindsight" | "mnemopi"` (local summarization, remote vectors, local SQLite+embeddings) (omp §10). Prime: `harness_state.json` CRUD only, no search layer (prime §5.4).
- **Daemon-awareness:** Backend processes (vector service, SQLite) live daemon-side; the client only sees tool events. The embeddings extension provides the `embed`/`embedMany` service to reuse.
- **Shape:** feature-plugin (memory system).
- **Deps:** on GAP-11, on GAP-53 (state store), on P08. Portfolio: P08.

#### GAP-13 — Auto-learn (post-stop capture)
- **Master:** `master-harness-analysis.md` §3.3 "Auto-learn (post-stop capture)".
- **Status:** missing.
- **Why:** Off-by-default post-turn capture is the lowest-friction route to a personal skill/memory library that compounds.
- **omp/prime:** omp: `autolearn.enabled` (default off), private capture turn, `minToolCalls` threshold, managed skills store + `manage_skill` tool (omp §14, §13). Prime: no post-stop capture; `/refine` is explicit and auditable (prime §5.4).
- **Daemon-awareness:** Capture turns run as private daemon-side turns (`turn_start`/`agent_*` events already stream); the "zero footprint" default keeps clients unaffected.
- **Shape:** feature-plugin (memory system).
- **Deps:** on GAP-11 (memory verbs) + GAP-12 (backend), on GAP-56 (structured skill pipeline) for managed skills, on P08. Portfolio: P08.

#### GAP-14 — Self-improvement target & safety (/refine)
- **Master:** `master-harness-analysis.md` §3.3 "Self-improvement target & safety".
- **Status:** missing.
- **Why:** Versioned, rollback-able edits to prompt/memory/skill/subagent state with an immutable base prompt is the auditable self-improvement loop.
- **omp/prime:** omp: writes only to managed-skills store and memory (omp §14). Prime: `/refine` computes create/update/delete edits to harness entries (`refinements.jsonl`, rollback snapshots), never rewrites the base system prompt (prime §1, §5.4).
- **Daemon-awareness:** Refinement edits daemon-resident state; the immutable base prompt stays in the daemon's prompt pipeline. mtime-style clobber protection matters once multiple clients can trigger refinement.
- **Shape:** feature-plugin (continual harness).
- **Deps:** on GAP-53 (state store), on GAP-56 (structured skills), on GAP-05 (subagent specs). Portfolio: P09.

### §3.4 Skills system

#### GAP-15 — Skill providers & foreign-format compatibility
- **Master:** `master-harness-analysis.md` §3.4 "Skill providers & foreign-format compatibility".
- **Status:** missing.
- **Why:** "Inherits what your other tools already wrote" — importing Claude/Codex/opencode/GitHub skills makes PiSharp immediately useful in existing setups.
- **omp/prime:** omp: seven-provider pipeline (`.omp` 100 → plugins 90 → claude 80 → … → GitHub 30 → managed 5), first-wins dedup, source toggles and include/ignore globs (omp §13, §5). Prime: own dirs only (prime §5.5).
- **Daemon-awareness:** Skill discovery is daemon-resident (resources load at daemon start per the design's component diagram); source toggles belong in settings (GAP-52).
- **Shape:** feature-plugin (foreign rules & skills compatibility).
- **Deps:** on GAP-52 (settings API for source toggles). Portfolio: P11.

#### GAP-16 — `skill://` URLs & `/skill:` commands
- **Master:** `master-harness-analysis.md` §3.4 "`skill://` URLs & `/skill:` commands".
- **Status:** partial — `/skill:<name>` slash invocation exists (PiSharp catalog §5.4, §9); the `skill://` URL half is absent (no `skill://` anywhere in `src\`).
- **Why:** URL-style skill addressing lets the model pull a skill's body on demand with traversal guards, independent of slash commands.
- **omp/prime:** omp: `skill://<name>` with traversal guards + `/skill:<name>` (Enter → steer, Ctrl+Enter → followUp) (omp §13). Prime: `/skill:name` commands only (prime §5.5).
- **Daemon-awareness:** URL resolution happens server-side inside the read tool; the missing half rides the internal-URL scheme work (GAP-43).
- **Shape:** core-plugin (internal URL resolution).
- **Deps:** on GAP-43 (internal URL schemes). Portfolio: P26.

### §3.5 Rules & context files

#### GAP-17 — Rules engine (time-traveling stream rules)
- **Master:** `master-harness-analysis.md` §3.5 "Rules engine".
- **Status:** missing.
- **Why:** Rules that fire only when the model goes off-script (mid-token abort + rule injection + retry) keep a repo's guardrails active without bloating every prompt.
- **omp/prime:** omp-only: regex match aborts the stream mid-token, injects the rule as a system reminder, retries from the same point; exposed as `ttsr`; `--no-rules` disables (omp §11). Prime: none (context files only).
- **Daemon-awareness:** The stream runs in the daemon; the harness already exposes `before_provider_payload` / `after_provider_response` extension events and an `auto_retry` loop (PiSharp catalog §7.1, §8), which are the natural interception points. Mid-token abort-and-retry may need a small core stream hook [INFERENCE].
- **Shape:** feature-plugin (rules engine).
- **Deps:** none core; optional small core stream hook [INFERENCE]. Portfolio: P10.

#### GAP-18 — Sticky always-apply RULES.md
- **Master:** `master-harness-analysis.md` §3.5 "Sticky always-apply RULES.md".
- **Status:** missing.
- **Why:** The cheapest, most portable guardrail surface: a `RULES.md` that is force-applied every turn and survives compaction.
- **omp/prime:** omp-only: `~/.omp/agent/RULES.md` + nearest `.omp/RULES.md`, force-applied, survive compaction (omp §11). Prime: none.
- **Daemon-awareness:** Prompt composition is daemon-side; rules ride the existing prompt pipeline (compaction already re-injects from session state). Needs compaction-survival guarantees tested in the daemon's `compact` path.
- **Shape:** feature-plugin (rules engine).
- **Deps:** on GAP-17. Portfolio: P10.

#### GAP-19 — Foreign rules/context ingestion (Cursor/Cline/Codex/Copilot/Gemini)
- **Master:** `master-harness-analysis.md` §3.5 "Foreign rules/context ingestion".
- **Status:** missing.
- **Why:** Repos already carry `.mdc`, `.clinerules`, and `applyTo` rules; honoring them makes PiSharp drop into existing projects.
- **omp/prime:** omp-only: eight-format compatibility layer (Cursor MDC, Cline, Codex AGENTS.md, Copilot applyTo, Claude/Gemini roots, GitHub rules) (omp §2, §11, §13, §5). Prime: none.
- **Daemon-awareness:** Same mechanism as GAP-15 — daemon-side discovery/merge; toggles in settings (GAP-52). Pair with the existing AGENTS.md/CLAUDE.md walk (covered).
- **Shape:** feature-plugin (foreign rules & skills compatibility).
- **Deps:** on GAP-52 (settings API), on GAP-17 (rules pipeline). Portfolio: P11.

### §3.6–3.7 IDE protocols

#### GAP-20 — LSP integration
- **Master:** `master-harness-analysis.md` §3.6 "LSP integration".
- **Status:** missing.
- **Why:** Real language intelligence (hover, definition, rename-with-references, diagnostics) is the difference between text-pattern edits and correct ones.
- **omp/prime:** omp: full LSP client — per-language server factories, request muxer, 14 ops, `workspace/willRenameFiles` for re-export-aware renames, post-write diagnostics, `lsp.enabled` gate (omp §8). Prime: none (ACP only).
- **Daemon-awareness:** Language servers are long-lived daemon-side processes (warm runtimes fit the design's resident-process model); tool calls surface via existing `tool_execution_*` events. Client needs no new protocol.
- **Shape:** feature-plugin (IDE protocol clients).
- **Deps:** on GAP-52 (settings for `lsp.enabled` gating). Portfolio: P12.

#### GAP-21 — Debug tool (DAP client)
- **Master:** `master-harness-analysis.md` §3.7 "Debug tool (DAP client)".
- **Status:** missing.
- **Why:** In-conversation debugging (set breakpoints, inspect variables, step) closes the loop a coding agent otherwise leaves to the human.
- **omp/prime:** omp: DAP client mirroring the LSP architecture — spawn `lldb-dap`/`dlv-dap`/`debugpy`, 28 ops, setting-gated `debug.enabled` off by default (omp §9). Prime: none.
- **Daemon-awareness:** Adapters are daemon-side subprocesses; long-lived debug sessions fit the daemon model (they must survive client attach/detach like turns do). Setting-gating needs GAP-52.
- **Shape:** feature-plugin (IDE protocol clients).
- **Deps:** on GAP-52 (settings). Portfolio: P12.

### §3.8 Plan mode

#### GAP-22 — Plan mode (read-only planning phase)
- **Master:** `master-harness-analysis.md` §3.8 "Plan mode (read-only planning phase)".
- **Status:** missing.
- **Why:** A read-only exploration phase with persisted, approved plans keeps the agent from touching files before a plan is agreed.
- **omp/prime:** omp: `--plan`/`--plan-model`, restricted tool set, plans persisted as files, model transition planning→execution, policy propagates to subagents (omp §7). Prime: none (`--thinking` levels are the closest analogue).
- **Daemon-awareness:** Plan mode is a session mode — server-side state (restricted active tools + plan-file persistence), surfaced to the client via a mode event; subagent policy propagation is server-side (ties into GAP-07).
- **Shape:** feature-plugin (plan mode).
- **Deps:** on GAP-52 (settings for `planModeEnabled`). Portfolio: P14.

### §3.9 Eval kernels & kernel runtime

#### GAP-23 — Eval kernels with tool re-entry
- **Master:** `master-harness-analysis.md` §3.9 "Eval kernels with tool re-entry".
- **Status:** missing.
- **Why:** Persistent Python/JS kernels with a loopback to the agent's own tools are the standard substrate for reproducible evaluation and long-lived compute.
- **omp/prime:** omp: persistent Python and JS kernels with loopback bridge into agent tools (omp §17). Prime: the IPython kernel *is* the control plane (role-inverted) with snapshot/restore (prime §4, §5.1, §5.8).
- **Daemon-awareness:** Kernels are daemon-side long-lived processes; the design's warm-runtime model is the natural host. Kernel state must survive client detach; snapshot/restore (prime-style) is the compaction-survival answer.
- **Shape:** feature-plugin (eval & bench).
- **Deps:** on P15. Portfolio: P15.

### §3.10 Advisor model

#### GAP-24 — Second-model watchdog
- **Master:** `master-harness-analysis.md` §3.10 "Second-model watchdog".
- **Status:** missing.
- **Why:** A cheap second model watching every turn and injecting notes/blockers catches mistakes the main model cannot see in its own output.
- **omp/prime:** omp-only: advisor model watches every turn, `modelRoles.advisor` (omp §17). Prime: none.
- **Daemon-awareness:** The advisor runs as a second server-side stream; its notes must reach the client as an event (new `advisor_note` event or message annotations) so they render distinctly.
- **Shape:** feature-plugin (advisor model).
- **Deps:** none core (uses existing providers + `Steer`/events). Portfolio: P16.

### §3.11 Browser / computer / collab

#### GAP-25 — Browser automation
- **Master:** `master-harness-analysis.md` §3.11 "Browser automation".
- **Status:** missing.
- **Why:** Verifying web UIs and scraping dynamic pages is a recurring coding-agent task (E2E smoke checks, docs, repro steps).
- **omp/prime:** omp: Puppeteer over headless Chromium, CDP-attached apps, or the user's Chrome via relay (omp §17). Prime: explicitly none — images pasted into terminal (prime §6).
- **Daemon-awareness:** Headless Chromium is a daemon-side subprocess; `tool_execution_*` events already stream progress. Remote tab-driving of the user's real browser is a client-side relay concern.
- **Shape:** feature-plugin (browser automation).
- **Deps:** none. Portfolio: P17.

### §3.12 Config & profiles

#### GAP-26 — Profiles
- **Master:** `master-harness-analysis.md` §3.12 "Profiles".
- **Status:** missing.
- **Why:** Whole-base relocation (sessions, config, skills, auth) enables role separation and safe experimentation.
- **omp/prime:** omp-only: `--profile`/`OMP_PROFILE` relocates the user base to `~/.omp/profiles/<name>/agent` (omp §15). Prime: none.
- **Daemon-awareness:** Must be designed daemon-aware: the lease must be keyed by profile (one daemon per profile, or profile-scoped paths inside one daemon); `pisharp daemon start --profile <name>`.
- **Shape:** core-change — requires `PiAgentPaths`/`PiSettingsStore` (PiSharp catalog §9) to accept a profile root; CLI flag; daemon lease keying.
- **Deps:** none. Portfolio: P18.

#### GAP-27 — Keybindings / themes / prompt templates
- **Master:** `master-harness-analysis.md` §3.12 "Keybindings / themes / prompt templates".
- **Status:** partial — prompt templates and theme *documents* exist (PiSharp catalog §9); missing: `keybindings.json` is defined in `PiAgentPaths` but **unread** by the TUI (verified absence), and there is no theme API for extensions (§7.5 #5).
- **Why:** Remappable keys and a programmatic theme surface are table stakes for a TUI users live in all day; the keybindings file is already promised by the path layout.
- **omp/prime:** omp: `theme.dark/light` setting, `--no-themes`-class surface implied (omp §15). Prime: fully remappable keybindings, themes, `/templatename` expansion, `--no-themes`/`--no-prompt-templates` (prime §7–8).
- **Daemon-awareness:** The daemon design already adds `get_theme` (design §Wire protocol) — themes are daemon-resident; rendering and keybinding dispatch are client/TUI concerns. Theme *API* for extensions is the core-change half (ties to GAP-31).
- **Shape:** core-change (theme API on `IExtensionApi`/`IExtensionUi`) + feature (client-side keybindings.json loader).
- **Deps:** none. Portfolio: P05.

### §3.13 Providers & model catalog

#### GAP-28 — Provider breadth & subscription OAuth
- **Master:** `master-harness-analysis.md` §3.13 "Provider breadth & subscription OAuth".
- **Status:** partial — the subscription-OAuth half is covered (Anthropic OAuth, GitHub Copilot device flow, OpenAI Codex OAuth — PiSharp catalog §4.5); missing: provider breadth (11 built-in provider classes vs omp's 60+/prime's 30+).
- **Why:** Users bring whichever key they own; subscription providers (Codex/Copilot/Claude Pro) are already first-class here, so the gap is breadth, not mechanics.
- **omp/prime:** omp: 60+ providers via `packages/catalog` + descriptors (omp §1, §15). Prime: 30+ endpoints through one registry incl. OAuth subscriptions for Codex/Copilot/Claude (prime §6).
- **Daemon-awareness:** Registration is daemon-startup work; `models.json` already lets users add OpenAI-compatible providers without code (PiSharp catalog §4.3). Non-OpenAI-compatible APIs (e.g. Groq-style endpoints, Cerebras) are the only true gaps.
- **Shape:** feature-plugin (provider pack).
- **Deps:** none (uses existing `RegisterProvider(IModelProvider)` surface). Portfolio: P19.

#### GAP-29 — Model catalog & role system
- **Master:** `master-harness-analysis.md` §3.13 "Model catalog & role system".
- **Status:** partial — generated catalog, thinking budgets, `--models` filtering, `cycle_model` exist (PiSharp catalog §4.3, §2.7); missing: named model *roles* (`@review`, `@smol`, …) and effort levels.
- **Why:** Role-named models let users/prompts say "use the cheap fast one for this" without hard-coding provider ids.
- **omp/prime:** omp: `modelRoles` (`@review`, `@fast_worker`, `@smol`) + effort levels (omp §15, §6). Prime: `enabledModels` cycling, `thinkingBudgets` (prime §7–8).
- **Daemon-awareness:** Role resolution is server-side (`RuntimeModelSelector` is the hook, PiSharp catalog §2.7); `set_model` already exists on the wire.
- **Shape:** feature-plugin (model roles & effort).
- **Deps:** on GAP-52 (settings for role definitions). Portfolio: P20.

### §3.14 Extensions / plugins / marketplace

#### GAP-30 — Custom tools (declarative / file-based)
- **Master:** `master-harness-analysis.md` §3.14 "Custom tools".
- **Status:** partial — extension tools via `RegisterTool`/`pi.registerTool` exist (PiSharp catalog §7.1, §7.6); missing: declarative `.md`/`.json` tool files and executable script tools (`tools/*.{sh,bash,py}`) with no code required.
- **Why:** A non-programmer can add a tool by dropping a file; script tools are the cheapest integration surface.
- **omp/prime:** omp: declarative files + executable code modules in TS/JS/sh/bash/py (omp §16). Prime: `pi.registerTool()` TS only (prime §5.10).
- **Daemon-awareness:** Tool definitions are discovered daemon-side; registration feeds the existing `ExtensionRegistry` (`tool:{name}`). No protocol change.
- **Shape:** feature-plugin (declarative tools).
- **Deps:** on GAP-52 (settings for discovery config). Portfolio: P21.

#### GAP-31 — Custom TUI / persistent storage
- **Master:** `master-harness-analysis.md` §3.14 "Custom TUI / persistent storage".
- **Status:** partial — persistent entries (`AppendEntryAsync`) and a rich `IExtensionUi` (widgets, custom overlays, editor text ops, menu items) exist (PiSharp catalog §7.1); missing (all §7.5-verified): theme API (#5), `getToolsExpanded`/`setToolsExpanded` (#6), full editor-component API (`SetEditorComponent`/`GetEditorComponent`, #7).
- **Why:** Extensions that can restyle and drive the TUI are what make a harness feel like an IDE.
- **omp/prime:** omp: "UI extensions" (mechanics not detailed) (omp §16). Prime: custom TUI UI + custom rendering + `pi.appendEntry()` (prime §5.10).
- **Daemon-awareness:** Extension UI rides the design's `ui_request`/`ui_response` bidirectional lane (design §UI bridge round-trips); the *surface* gaps are core extension-API gaps, and the TUI-side rendering is a client concern.
- **Shape:** core-change — theme API, tools-expanded, editor-component on `IExtensionApi`/`IExtensionUi` (PiSharp catalog §7.5 #5–7).
- **Deps:** none. Portfolio: P05.

#### GAP-32 — ACP mode (Agent Client Protocol)
- **Master:** `master-harness-analysis.md` §3.17 "ACP mode (Agent Client Protocol)".
- **Status:** missing.
- **Why:** ACP is the editor-interop standard both forks converged on; without it, editors (Zed et al.) cannot drive PiSharp.
- **omp/prime:** omp: `omp acp` serves ACP + approval modes (omp §17, §4). Prime: `--mode acp` via the ACP SDK with session-event mapping (prime §5.11, §3).
- **Daemon-awareness:** The design's non-goals keep non-interactive modes in-process/stdio — an ACP mode can be an in-process listener (like `rpc`) or a daemon-side listener; design decision to make, mirroring the design's `rpc` precedent.
- **Shape:** feature-plugin (ACP mode).
- **Deps:** none. Portfolio: P13.

### §3.18 SDK & embedding

#### GAP-33 — Programmatic SDK
- **Master:** `master-harness-analysis.md` §3.18 "Programmatic SDK".
- **Status:** missing.
- **Why:** An embedding/client API lets other tools drive PiSharp programmatically — the daemon protocol is already a wire SDK waiting for a public surface.
- **omp/prime:** omp: npm package with deep feature-domain subpath exports (omp §3). Prime: `AgentSessionRuntime` in-process embedding + `AgentConnection`-style client with snapshots/replay/reconnect (prime §5.11, §7).
- **Daemon-awareness:** The design's `PiSharp.Client` project is TUI-only; exposing its `ClientSessionState`/protocol as a public API (attach, prompt, events) *is* the daemon-side SDK, and in-process embedding mirrors prime's runtime export. Documented as NuGet surface.
- **Shape:** core-plugin (SDK layer on the daemon protocol + embedding).
- **Deps:** on P01 (daemon). Portfolio: P22.

### §3.19 Goals / heartbeats / cron / autonomous

#### GAP-34 — Goals
- **Master:** `master-harness-analysis.md` §3.19 "Goals".
- **Status:** missing.
- **Why:** A persisted objective with status and token-budget accounting keeps long-running work coherent across turns and restarts.
- **omp/prime:** omp: none. Prime: `/goal` persists `GoalState` (idle|active|paused|budget_limited|complete|error) with budget accounting (prime §5.7).
- **Daemon-awareness:** Goal state is daemon-resident session state; needs `set_goal`/`get_goal` commands and a `goal_updated` event so clients render it.
- **Shape:** feature-plugin (continuity suite).
- **Deps:** on P01, on GAP-52 (budget config). Portfolio: P23.

#### GAP-35 — Heartbeats
- **Master:** `master-harness-analysis.md` §3.19 "Heartbeats".
- **Status:** missing.
- **Why:** Periodic re-entry keeps an agent on a long task without user babysitting (checking on builds, following up on blocked steps).
- **omp/prime:** omp: none. Prime: `/heartbeat` + `rlm_heartbeat` skill re-enter a session periodically (prime §5.7, §5.5).
- **Daemon-awareness:** Heartbeat timers are daemon-side (the daemon owns session liveness); the design's idle-timeout already distinguishes "no attached client" from "no pending turn" so a heartbeat can keep a session alive deliberately.
- **Shape:** feature-plugin (continuity suite).
- **Deps:** on P01. Portfolio: P23.

#### GAP-36 — Cron / scheduled prompts
- **Master:** `master-harness-analysis.md` §3.19 "Cron / scheduled prompts".
- **Status:** missing.
- **Why:** Scheduled prompts (daily standup, nightly maintenance) make the daemon a true background worker.
- **omp/prime:** omp: none. Prime: `prime-agent schedule`, per-session persisted jobs, claimed-and-advanced ticks so a crash never replays a prompt (prime §5.7).
- **Daemon-awareness:** Scheduler lives in the daemon (it must run with zero clients attached); needs `schedule_job`/`list_jobs`/`cancel_job` commands + a `scheduled_prompt` event; adopt prime's crash-safe tick design.
- **Shape:** feature-plugin (continuity suite).
- **Deps:** on P01, on GAP-34. Portfolio: P23.

#### GAP-37 — Autonomous mode
- **Master:** `master-harness-analysis.md` §3.19 "Autonomous mode".
- **Status:** missing.
- **Why:** Budgeted continuation (`maxContinuations`/`maxTurns`/`maxTokens`) with soft limits is what makes "go finish this" safe to say.
- **omp/prime:** omp: `--autonomous`-class continuation not surfaced in the analysis (omp §6). Prime: `/autonomous` with budgets, optional user-defined quality gates with retries, soft-bounded limits (prime §5.7).
- **Daemon-awareness:** Continuation turns run daemon-side; needs a budget envelope on `prompt` (or an `autonomous` command) and `budget_updated`/`autonomous_ended` events. The harness's existing auto-retry loop is a building block.
- **Shape:** feature-plugin (continuity suite).
- **Deps:** on P01, on GAP-34. Portfolio: P23.

### §3.21 Export / share / traces

#### GAP-38 — Share (gist)
- **Master:** `master-harness-analysis.md` §3.21 "Share (gist)".
- **Status:** partial — `/share` exists but is a **local file copy** (verified: `ShareSessionSlashCommand.cs` → `File.Copy`); missing: private GitHub gist upload.
- **Why:** One-command share-to-URL is how users hand sessions to collaborators or issue trackers.
- **omp/prime:** omp: none surfaced. Prime: `/share` uploads a private GitHub gist (prime §5.12).
- **Daemon-awareness:** Client-side network op (or daemon-side with a `share_session` command); either works — recommend client-side to keep the daemon network-free.
- **Shape:** feature-plugin (git integrations).
- **Deps:** none. Portfolio: P24.

### §3.22 Telemetry / stats / cleanse

#### GAP-39 — Telemetry
- **Master:** `master-harness-analysis.md` §3.22 "Telemetry".
- **Status:** missing.
- **Why:** Observability (latency, token counts, tool success, retries) is how a harness improves itself; today PiSharp has only `--benchmark-startup` (PiSharp catalog §8) and a plain-text logging plan.
- **omp/prime:** omp: OpenTelemetry-based telemetry (omp §17). Prime: pseudonymous aggregate events, opt-out via settings/env/`--offline`, overridable endpoint (prime §5.13, §8).
- **Daemon-awareness:** The daemon is the natural metrics collector (it owns all sessions); the design's logging split (`~/.pi/PiSharp/logs`) is the base. Core-change: tracing/metrics API on `IExtensionApi` (PiSharp catalog §7.5 #10) + structured logging.
- **Shape:** core-change (tracing/metrics extension API + structured logging) with a feature surface (aggregate reporting).
- **Deps:** on P01 (daemon logging). Portfolio: P25.

#### GAP-40 — Local stats dashboard
- **Master:** `master-harness-analysis.md` §3.22 "Local stats dashboard".
- **Status:** missing.
- **Why:** `omp stats`-style local observability gives users insight without shipping data anywhere.
- **omp/prime:** omp: `omp stats` local dashboard (omp §17, §5). Prime: none.
- **Daemon-awareness:** The daemon's retained event logs + `get_session_stats`-style queries (RPC already has `get_session_stats`, PiSharp catalog §5.2) are the data source; rendering is client-side.
- **Shape:** feature (dashboard) inside the observability entry.
- **Deps:** on GAP-39, on P01. Portfolio: P25.

#### GAP-41 — Cleanse / data removal
- **Master:** `master-harness-analysis.md` §3.22 "Cleanse / data removal".
- **Status:** missing.
- **Why:** A one-command removal of agent-owned data (sessions, auth, logs) is a privacy and hygiene guarantee users ask for.
- **omp/prime:** omp: `omp cleanse` + install-identity docs (omp §17). Prime: none.
- **Daemon-awareness:** Must stop the daemon first, then delete `~/.pi` state; CLI-level concern interacting with the daemon lifecycle.
- **Shape:** feature (cleanse command) inside the observability/data-hygiene entry.
- **Deps:** on P01 (daemon stop semantics). Portfolio: P25.

### §3.23 Commit

#### GAP-42 — Commit tool
- **Master:** `master-harness-analysis.md` §3.23 "Commit tool".
- **Status:** missing.
- **Why:** Dependency-ordered, atomic, message-driven commits are the last mile of an agentic edit cycle; hand-rolled commit commands are where agents misbehave.
- **omp/prime:** omp-only: `/commit` splits unrelated changes into dependency-ordered atomic commits, rejects cycles, scores source over tests/docs/configs, excludes lockfiles (omp §17). Prime: none.
- **Daemon-awareness:** Server-side git operations via the existing `bash` tool surface; no protocol change.
- **Shape:** feature-plugin (git integrations).
- **Deps:** none. Portfolio: P24.

### §3.24 Internal URL schemes

#### GAP-43 — Internal URL schemes (`://`)
- **Master:** `master-harness-analysis.md` §3.24 "Internal URL schemes (`://`)".
- **Status:** missing.
- **Why:** URL-addressable repo/harness resources (`pr://`, `issue://`, `agent://`, `diff://`, …) inside FS-shaped tools are a genuinely distinctive integration surface that compounds with everything else.
- **omp/prime:** omp-only: `pr://`, `issue://`, `agent://`, `skill://`, `conflict://`, `xd://`, `ssh://`, `diff://` resolve transparently in `read`-shaped tools; `agent://<id>/field.path` pulls subagent fields (omp §17, §2). Prime: none.
- **Daemon-awareness:** Resolution is server-side inside the read tool; also absorbs the missing half of GAP-16 (`skill://`).
- **Shape:** core-plugin (URL resolver in the read-tool layer, provider-registrable).
- **Deps:** none. Portfolio: P26.

### §3.25 Hashline edits

#### GAP-44 — Content-hash-anchored edits
- **Master:** `master-harness-analysis.md` §3.25 "Content-hash-anchored edits".
- **Status:** missing.
- **Why:** Hash-anchored edit addressing rejects stale patches before corruption and cuts edit tokens substantially (omp claims −61%).
- **omp/prime:** omp-only: `packages/hashline` content-hash anchors; stale anchors reject the patch (omp §17, §2). Prime: edits go through an in-kernel `edit` skill (prime §5.5).
- **Daemon-awareness:** Server-side edit-tool behavior; rides `tool_execution_*` events like any tool. PiSharp's `EditTool` is exact-text replacement with diff preview (PiSharp catalog §3.2) — compatible upgrade path.
- **Shape:** feature-plugin (structural & edit tooling).
- **Deps:** none. Portfolio: P30.

### §3.27 MCP integrations

#### GAP-45 — MCP client
- **Master:** `master-harness-analysis.md` §3.27 "MCP client".
- **Status:** missing (no `mcp` anywhere in `src\`).
- **Why:** MCP is the fastest-growing external-tool protocol; a client turns any MCP server into agent tools.
- **omp/prime:** omp: MCP in the tool layer; extensions can contribute MCP servers (omp §5, §16). Prime: in-kernel Python MCP client with host-managed OAuth (`/mcp login`), built-in Linear/Notion (prime §5.9, §6).
- **Daemon-awareness:** MCP servers are daemon-side subprocesses; their tools register in `ExtensionRegistry` and stream via `tool_execution_*`. Recommend the omp shape (agent-facing tools), not the kernel shape (PiSharp has no kernel control plane).
- **Shape:** feature-plugin (MCP client).
- **Deps:** on GAP-52 (settings for `mcpServers` config). Portfolio: P27.

### §3.28 Search

#### GAP-46 — Web search
- **Master:** `master-harness-analysis.md` §3.28 "Web search".
- **Status:** missing (no `web_search`/`WebSearch` in `src\`).
- **Why:** Current-API lookups, repro research, and docs retrieval are core agent work; without it the agent is blind to anything newer than the training cut.
- **omp/prime:** omp: `web_search` tool (allowed in plan mode alongside read/grep/glob) (omp §7). Prime: built-in `websearch` skill (Serper) with configurable env vars (prime §6, §5.5).
- **Daemon-awareness:** Server-side tool with key resolution via the existing credential resolver (PiSharp catalog §4.4); works in plan mode if plan mode (GAP-22) includes it.
- **Shape:** feature-plugin (research & retrieval).
- **Deps:** on GAP-52 (provider/key config). Portfolio: P28.

### §3.29 Security model

#### GAP-47 — Permissions & gating
- **Master:** `master-harness-analysis.md` §3.29 "Permissions & gating".
- **Status:** partial — tool selection flags (`--tools`, `--no-tools`, `--no-builtin-tools`) and middleware `Block` exist (PiSharp catalog §3.3, §7.1); missing: approval prompts, setting-gated dangerous tools (off by default), and a coherent permission model.
- **Why:** Neither reference sandboxes; the practical middle ground is gating — approval for destructive ops, defaults off for dangerous tools.
- **omp/prime:** omp: setting-gated tools off by default (`debug.enabled`), subagent guardrails (omp §9, §13, §6). Prime: same-OS permissions with a loud no-sandbox warning; leases prevent concurrent writers (correctness, not security) (prime §4, §5.7, §9).
- **Daemon-awareness:** Must be designed daemon-aware: approval prompts are asynchronous UI interactions — the design's `ui_request`/`ui_response` lane (design §UI bridge round-trips) is the natural carrier (typed `permission_request`); enforcement is server-side middleware.
- **Shape:** feature-plugin (permission gate on the middleware surface).
- **Deps:** on P01 (ui_request lane), on GAP-52 (settings). Portfolio: P29.

### §3.30 Native tooling & shell

#### GAP-48 — AST tools (ast_edit / ast_grep)
- **Master:** `master-harness-analysis.md` §3.30 "AST tools (ast_edit / ast_grep)".
- **Status:** missing.
- **Why:** Structural query/edit is dramatically safer than text patches for renames, API migrations, and codemods.
- **omp/prime:** omp: tree-sitter `pi-ast`, `ast_edit`/`ast_grep` tools (omp §2, §7). Prime: none.
- **Daemon-awareness:** Server-side tool; for a .NET agent, Roslyn gives first-class C# structural editing and tree-sitter (or Roslyn's parser) covers other languages.
- **Shape:** feature-plugin (structural & edit tooling).
- **Deps:** none. Portfolio: P30.

#### GAP-49 — PDF / arxiv reading
- **Master:** `master-harness-analysis.md` §3.30 "PDF / arxiv reading".
- **Status:** missing.
- **Why:** Repos and research workflows contain PDFs (specs, papers); a read tool that refuses them forces shell gymnastics.
- **omp/prime:** omp: PDF via `mupdf` (omp §4, §2). Prime: none.
- **Daemon-awareness:** Server-side read-tool extension; images already handled by `ReadTool` + `ImageUtilities` (PiSharp catalog §3.2).
- **Shape:** feature-plugin (research & retrieval).
- **Deps:** none. Portfolio: P28.

#### GAP-50 — Eval / bench tooling
- **Master:** `master-harness-analysis.md` §3.30 "Eval / bench tooling".
- **Status:** missing (only `--benchmark-startup` exists, PiSharp catalog §8).
- **Why:** A repeatable bench harness is how a coding agent gets measurably better; without it, improvements are vibes.
- **omp/prime:** omp: `omp bench`, `omp ttsr`, eval kernels, `typescript-edit-benchmark` (omp §5, §17, §3). Prime: partial — eval is the target use case, not a bench command (prime §1).
- **Daemon-awareness:** Bench runs are daemon-side batch workloads; eval kernels (GAP-23) are the runtime substrate. Pair with structured-output subagents (GAP-04) for scored runs.
- **Shape:** feature-plugin (eval & bench).
- **Deps:** on GAP-23 (eval kernels). Portfolio: P15.

### §3.31 Distribution

#### GAP-51 — Install methods & self-update
- **Master:** `master-harness-analysis.md` §3.31 "Install methods & self-update".
- **Status:** partial — package install/remove/update for extensions exists (PiSharp catalog §5.2); self-update is parsed but explicitly **"not implemented"**.
- **Why:** `update self` is expected hygiene; the daemon's version-compat check already makes mismatched binaries fail loudly.
- **omp/prime:** omp: curl/Homebrew/npm/Nix/mise/PowerShell installs, prebuilt binaries (omp §1, §3). Prime: versioned SHA-verified installer + install-method-detecting self-update (prime §2, §7).
- **Daemon-awareness:** Client-side concern; the design's version-compat validation (daemon.json version check, client starts its own daemon on mismatch — §Daemon lifecycle) is the seam a self-update must coordinate with.
- **Shape:** feature-plugin (distribution polish).
- **Deps:** none. Portfolio: P31.

### Extension-surface core-changes (PiSharp catalog §7.5 verified gaps — not master features, required by the features above)

#### GAP-52 — Settings API for extensions
- **Master:** n/a — extension-surface gap, PiSharp catalog §7.5 #1.
- **Status:** missing — `IExtensionApi` has no settings read/write; nothing in `src\PiSharp.Extensions`.
- **Why:** Nearly every feature plugin (permissions, roles, memory, MCP, web search, rules toggles) needs typed, layered, extension-scoped configuration; today extensions can only read `GetFlag`.
- **omp/prime:** omp extensions read/write through the layered config schema (omp §15); prime settings are global/project JSON (prime §8). Neither is the model to copy — PiSharp's four-layer `PiSettingsStore` (catalog §9) should be exposed.
- **Daemon-awareness:** Settings are daemon-resident and must be readable/writable by daemon-side extensions; changes flow to clients via events (`session_info_changed`-style).
- **Shape:** core-change — settings read/write + change events on `IExtensionApi`, backed by `PiSettingsStore`.
- **Deps:** none. Portfolio: P02.

#### GAP-53 — Extension-owned persistent state store
- **Master:** n/a — extension-surface gap, PiSharp catalog §7.5 #2.
- **Status:** missing — no `State`/`Storage`/`KV` surface; only session-entry appends.
- **Why:** Extensions (memory, goals, workflows) need durable key-value state that survives restarts without abusing the session transcript.
- **omp/prime:** prime's `pi.appendEntry()` is the closest analogue (prime §5.10) but is transcript-adjacent; omp's `agent.db` shows a proper store.
- **Daemon-awareness:** Store is daemon-side (survives client churn); scoped per extension id; exposed to the TS bridge as a runtime action.
- **Shape:** core-change — `IExtensionApi.State` KV store (namespaced, versioned, JSON-typed).
- **Deps:** none. Portfolio: P02.

#### GAP-54 — Session control on `IExtensionSessionApi`
- **Master:** n/a — extension-surface gap, PiSharp catalog §7.5 #4 (+ native `GetCommands` #3).
- **Status:** missing — `NewSessionAsync`/`ForkAsync`/`SwitchSessionAsync`/`NavigateTreeAsync`/`WaitForIdleAsync` exist only as `ExtensionRuntimeBinding` delegates; not on `IExtensionSessionApi`; native `GetCommandsAsync` is not surfaced on `IExtensionApi`.
- **Why:** Extensions that fork/navigate/wait are a precondition for workflow and multi-agent plugins; the TS bridge already has the runtime actions (§7.3) — the native API lags it.
- **Daemon-awareness:** Session control becomes daemon commands behind the scenes; the daemon design already adds `get_session_snapshot`/`get_fork_messages`, so the API surface can map 1:1 onto the wire.
- **Shape:** core-change — promote the `ExtensionRuntimeBinding` delegates onto `IExtensionSessionApi` + `GetCommands` on `IExtensionApi`.
- **Deps:** none. Portfolio: P03.

#### GAP-55 — Runtime package / install API
- **Master:** n/a — extension-surface gap, PiSharp catalog §7.5 #9.
- **Status:** missing — extension install is CLI-only (`PiSharp.Cli\Packages\`).
- **Why:** Extensions that install/update sibling extensions at runtime (marketplace-like behavior without a marketplace) need an API; also the daemon needs install state to be daemon-side.
- **omp/prime:** omp's `omp plugin` install flow is CLI+package manager (omp §16); prime has no install flow at all.
- **Daemon-awareness:** Installs mutate daemon-resident extension state → must be a daemon command (`install_extension`/`uninstall_extension`) so the daemon can hot-reload without restart.
- **Shape:** core-change — install/update/remove/list on `IExtensionApi` (reusing `PiPackageCommandRunner` logic).
- **Deps:** none. Portfolio: P04.

#### GAP-56 — Structured skill pipeline
- **Master:** n/a — extension-surface gap, PiSharp catalog §7.5 #8.
- **Status:** missing — `RegisterSkill` takes only `(name, description, content, file, disableModelInvocation)`; no structured pipeline, no per-skill runner hooks.
- **Why:** Managed skills (auto-learn), python-ish executable skills, and skill-level hooks need a definition richer than markdown payloads.
- **omp/prime:** omp: skills are file packs (omp §13); prime: Python-backed importable skill packages (prime §5.5) — PiSharp should adopt a middle path: structured metadata + optional runner/execute hook.
- **Daemon-awareness:** Server-side skill pipeline (load, select, invoke, rank — `relevance-filtered-skills` already patches the `skills.available` section, PiSharp catalog §7.4).
- **Shape:** core-change — skill pipeline interfaces (metadata schema, per-skill runner hook, managed-skill store) on `IExtensionSkillApi`.
- **Deps:** none. Portfolio: P04.

---

## 4. Daemon-client interplay

The design doc (`2026-08-14-daemon-client-architecture-design.md`) already plans a protocol that absorbs or shapes a large part of the gap catalog. Three buckets:

### 4.1 Gaps the daemon already handles (no new protocol work)

| Gap | How the design absorbs it | Design doc cite |
| --- | --- | --- |
| GAP-01 daemon/background execution | per-user daemon, lease, auto-start, warm runtimes, attach/re-attach, multi-terminal | §Goals, §Architecture, §Daemon lifecycle, phases 0–4 |
| GAP-02 crash recovery (process half) | client detach survives; in-progress turns run to completion; idle-timeout disposal | §Daemon lifecycle |
| GAP-03 idle eviction (session half) | per-session idle timeout (5 min, configurable), zero attached clients | §Daemon lifecycle |
| GAP-08 messaging (substrate) | event-sourced per-session streams are the delivery substrate for agent-to-agent messages | §Wire protocol |
| GAP-31/27 extension UI (transport half) | `ui_request`/`ui_response` bidirectional lane, multi-client responder policy, headless auto-decline | §UI bridge round-trips, §Multi-client policy |
| GAP-47 permission prompts (transport half) | the `ui_request` lane generalizes to typed `permission_request` | §UI bridge round-trips |
| Server-side execution of slash commands/input hooks | `run_command`/`complete_command`/`process_input` execute in `RunExclusiveAsync` | §Wire protocol |
| Theme/extension/registry queries | `get_theme`, `get_session_snapshot`, `get_extension_load_status/shortcuts/registry`, `resolve_tool`, `get_available_models`, `get_commands` | §Wire protocol (additions) |

### 4.2 Gaps needing new daemon commands/events

| Gap | New command(s) | New event(s) |
| --- | --- | --- |
| GAP-02 crash adoption | daemon lifecycle machinery (lease + adoption), not a WS command | `daemon_adopted` (daemon-local) |
| GAP-04 structured subagent output | `get_subagent_result` (typed, schema-validated) | `subagent_completed` (typed payload) |
| GAP-06/05 agent discovery | `get_agents` | — |
| GAP-08 daemon-level messaging | `get_roster`, `send_agent_message` | `agent_message_received` (with sender validation) |
| GAP-22 plan mode | `set_plan_mode` | `plan_mode_changed` / `plan_updated` |
| GAP-24 advisor notes | — | `advisor_note` (rendered distinctly by client) |
| GAP-34 goals | `set_goal`, `get_goal` | `goal_updated` |
| GAP-36 cron | `schedule_job`, `list_jobs`, `cancel_job` | `scheduled_prompt` |
| GAP-37 autonomous | `autonomous` (or budget envelope on `prompt`) | `budget_updated`, `autonomous_ended` |
| GAP-26 profiles | daemon start arg `--profile`; lease keyed by profile | — |
| GAP-39/40 telemetry & stats | `get_metrics` / port `get_session_stats` (already in RPC) | daemon-side metrics export (file or event) |
| GAP-55 runtime package API | `install_extension`, `uninstall_extension`, `list_installed_extensions` | `extensions_changed` |
| GAP-33 SDK | no new protocol — expose `PiSharp.Client` state model + protocol as a public API | — |

### 4.3 Client-only / TUI concerns

- **GAP-27** keybindings.json loading (client); theme rendering is client-side over daemon `get_theme`.
- **GAP-31** tools-expanded + editor-component surfaces are client TUI API additions behind the `ui_request` lane.
- **GAP-32** ACP server placement: design non-goals keep non-interactive modes in-process — host ACP in-process (like `rpc`) or as a daemon-side listener (decision for P13).
- **GAP-40** stats dashboard rendering from daemon events/queries.
- **GAP-38** gist share (recommended client-side, keeps the daemon network-free).
- **GAP-41** cleanse (client CLI: `daemon stop` then delete `~/.pi`).
- **GAP-51** self-update (client-side; coordinate with the design's daemon version-compat check).
- **GAP-25** browser relay to the user's real Chrome (client-side; headless is daemon-side).

Everything else in §3 is server-side tooling/prompt work that rides the existing protocol (commands in §4.1 + `tool_execution_*`/`message_*`/`turn_*` events) and needs no new wire surface.

---

## 5. Prioritized plugin portfolio

Dispatch order: **one planning agent per entry, in this order.** An entry's `Deps` always appear earlier. Coverage: all 56 gaps exactly once.

### Foundation wave (core-changes and core-plugins first)

- **P01 — Daemon completion & resilience** — *core-change*
  - Gaps: GAP-01, GAP-02, GAP-03. Deps: none.
  - Scope: execute the approved daemon design phases 0–4 (lease, attach/replay/resync, RemoteTuiBackend, lifecycle); add phase-5 resilience: supervisor/worker crash adoption with launch leases and tree-level idle eviction. Planning agent designs the extension of the approved doc, not a new plugin.

- **P02 — Extension settings & persistent state** — *core-change*
  - Gaps: GAP-52 (settings API), GAP-53 (state store). Deps: none.
  - Scope: `IExtensionApi.Settings` (layered read/write over `PiSettingsStore` + change events) and `IExtensionApi.State` (namespaced versioned KV), plus TS-bridge runtime actions. Everything downstream that needs configuration depends on this.

- **P03 — Extension session & command control** — *core-change*
  - Gaps: GAP-54. Deps: none.
  - Scope: promote `NewSessionAsync`/`ForkAsync`/`SwitchSessionAsync`/`NavigateTreeAsync`/`WaitForIdleAsync` onto `IExtensionSessionApi` and `GetCommands` onto `IExtensionApi`; map onto the daemon's session commands.

- **P04 — Extension runtime packages & structured skills** — *core-change*
  - Gaps: GAP-55 (package API), GAP-56 (skill pipeline). Deps: none.
  - Scope: install/update/remove extension packages from `IExtensionApi` (reusing `PiPackageCommandRunner`) with daemon hot-reload; structured skill pipeline (metadata schema, per-skill runner hook, managed-skill store) on `IExtensionSkillApi`.

- **P05 — TUI & theme extension surface** — *core-change + client feature*
  - Gaps: GAP-27 (keybindings/themes), GAP-31 (custom TUI). Deps: none.
  - Scope: theme API on `IExtensionApi`/`IExtensionUi` (daemon `get_theme` + client render), `getToolsExpanded`/`setToolsExpanded`, editor-component API, and a client-side `keybindings.json` loader replacing the unread path.

- **P06 — Subagent framework** — *core-plugin*
  - Gaps: GAP-04 (structured output), GAP-05 (agent definitions), GAP-06 (discovery & precedence), GAP-07 (guardrails), GAP-09 (skills in subagents), GAP-10 (isolation/worktrees). Deps: none (builds on the existing `SubagentSessionService`).
  - Scope: markdown agent definitions with discovery precedence, schema-validated `yield`-style results, recursion/spawn policy, per-agent skill policy, optional git-worktree isolation. The generic foundation for every multi-agent feature.

- **P07 — Agent messaging & coordination** — *feature-plugin*
  - Gaps: GAP-08. Deps: P01.
  - Scope: evolve `PiSharp.Coordination` onto daemon-level routing: roster, validated direct messages, delivery guarantees, model-facing `hub` tool, watch/steer UI in the client.

### Wave 2 (config-dependent features — after P02)

- **P08 — Memory system** — *feature-plugin*
  - Gaps: GAP-11 (memory tools), GAP-12 (backends), GAP-13 (auto-learn). Deps: P02 (settings/state), P04 (managed skills).
  - Scope: `retain`/`recall`/`reflect`/`learn` verbs, pluggable backends (off/file/vector via the existing embeddings extension), off-by-default post-stop capture.

- **P09 — Continual harness (/refine)** — *feature-plugin*
  - Gaps: GAP-14. Deps: P02, P04 (structured skills), P06 (subagent specs).
  - Scope: `/refine`-style versioned, rollback-able edits to prompt/memory/skill/subagent harness state with an immutable base prompt and clobber protection.

- **P10 — Rules engine & RULES.md** — *feature-plugin*
  - Gaps: GAP-17 (TTSR), GAP-18 (sticky RULES.md). Deps: none (uses existing `before_provider_payload`/auto-retry; small core stream hook if mid-token retry needs it [INFERENCE]).
  - Scope: regex stream rules that abort/inject/retry mid-token, plus always-apply `RULES.md` that survives compaction.

- **P11 — Foreign rules & skills compatibility** — *feature-plugin*
  - Gaps: GAP-15 (skill providers), GAP-19 (foreign rules ingestion). Deps: P02, P10.
  - Scope: import Claude/Codex/opencode/GitHub skills and Cursor/Cline/Copilot rules with priority, first-wins dedup, and source toggles — "inherits what your other tools already wrote."

- **P12 — IDE protocol clients (LSP + DAP)** — *feature-plugin*
  - Gaps: GAP-20 (LSP), GAP-21 (DAP). Deps: P02 (setting gating).
  - Scope: LSP client (hover/definition/rename/diagnostics/format) with a request muxer and post-write diagnostics; DAP client (breakpoints/step/stack/variables/evaluate), both setting-gated off by default, adapters as daemon-side processes.

- **P13 — ACP mode** — *feature-plugin*
  - Gaps: GAP-32. Deps: none.
  - Scope: Agent Client Protocol server (in-process stdio or daemon-side listener — design decision) mapping session events to ACP events so editors can drive PiSharp.

- **P14 — Plan mode** — *feature-plugin*
  - Gaps: GAP-22. Deps: P02.
  - Scope: read-only planning phase with restricted tools, persisted approved plans, planning→execution model transition, and policy propagation to subagents.

- **P15 — Eval & bench** — *feature-plugin*
  - Gaps: GAP-23 (eval kernels), GAP-50 (bench tooling). Deps: none core.
  - Scope: persistent Python/JS kernels with tool-re-entry loopback and kernel snapshot/restore; `bench` command for repeatable scored runs.

- **P16 — Advisor model** — *feature-plugin*
  - Gaps: GAP-24. Deps: none core.
  - Scope: a second model watching every turn, emitting `advisor_note` events rendered distinctly by the client; configured via a model-role name.

- **P17 — Browser automation** — *feature-plugin*
  - Gaps: GAP-25. Deps: none.
  - Scope: headless-Chromium `browser` tool (daemon-side) with optional relay to the user's own browser (client-side); screenshots feed the existing image path.

- **P18 — Profiles** — *core-change*
  - Gaps: GAP-26. Deps: none.
  - Scope: `--profile`/`PISHARP_PROFILE` relocating `PiAgentPaths`/`PiSettingsStore` roots; daemon lease keyed by profile.

- **P19 — Provider breadth pack** — *feature-plugin*
  - Gaps: GAP-28. Deps: none.
  - Scope: first-class provider classes for the popular non-OpenAI-compatible endpoints; OpenAI-compatible ones stay models.json-configured (document the recipe).

- **P20 — Model roles & effort** — *feature-plugin*
  - Gaps: GAP-29. Deps: P02.
  - Scope: named `@role` model resolution in `RuntimeModelSelector` and effort levels, configurable via settings.

- **P21 — Declarative custom tools** — *feature-plugin*
  - Gaps: GAP-30. Deps: P02.
  - Scope: `.md`/`.json` tool files and executable script tools (`sh`/`bash`/`py`/`ts`) discovered into `ExtensionRegistry` without writing an extension.

- **P22 — Programmatic SDK** — *core-plugin*
  - Gaps: GAP-33. Deps: P01.
  - Scope: NuGet surface exposing daemon attach/prompt/events (from `PiSharp.Client`) plus in-process `SessionRuntime` embedding; documented SDK + sample.

- **P23 — Continuity suite** — *feature-plugin*
  - Gaps: GAP-34 (goals), GAP-35 (heartbeats), GAP-36 (cron), GAP-37 (autonomous). Deps: P01, P02.
  - Scope: persisted goals with budget accounting; heartbeat re-entry; per-session scheduled prompts with crash-safe ticks; budgeted autonomous continuation with quality gates. The daemon is the only place these can live.

- **P24 — Git integrations** — *feature-plugin*
  - Gaps: GAP-38 (gist share), GAP-42 (commit tool). Deps: none.
  - Scope: dependency-ordered atomic `/commit` with cycle rejection and file scoring; `/share` upgraded from local copy to private gist upload.

- **P25 — Observability & data hygiene** — *core-change + feature*
  - Gaps: GAP-39 (telemetry), GAP-40 (stats dashboard), GAP-41 (cleanse). Deps: P01.
  - Scope: tracing/metrics API on `IExtensionApi` + structured logging; local `stats` dashboard over daemon events; `cleanse` command (stop daemon → delete `~/.pi` state).

- **P26 — Internal URL schemes** — *core-plugin*
  - Gaps: GAP-43 (internal URLs), GAP-16 (`skill://` half). Deps: none.
  - Scope: provider-registrable URL resolver in the read-tool layer (`pr://`, `issue://`, `agent://`, `skill://`, `diff://`, …) with traversal guards.

- **P27 — MCP client** — *feature-plugin*
  - Gaps: GAP-45. Deps: P02.
  - Scope: MCP client in the tool layer (omp shape), extension-contributed servers, `mcpServers` config via settings, OAuth credentials via the existing auth store.

- **P28 — Research & retrieval** — *feature-plugin*
  - Gaps: GAP-46 (web search), GAP-49 (PDF reading). Deps: P02.
  - Scope: `web_search` tool with key resolution through the credential resolver; PDF/arxiv text extraction in `read`.

- **P29 — Permissions & gating** — *feature-plugin*
  - Gaps: GAP-47. Deps: P01 (ui_request lane), P02.
  - Scope: approval prompts for destructive operations over the `ui_request` lane, setting-gated dangerous tools, defaults-off posture, middleware enforcement.

- **P30 — AST & structural editing** — *feature-plugin*
  - Gaps: GAP-48 (AST tools), GAP-44 (hashline edits). Deps: none.
  - Scope: Roslyn-based `ast_edit`/`ast_grep` for C# (+ tree-sitter for other languages); content-hash-anchored edit addressing in `EditTool`.

- **P31 — Distribution polish (self-update)** — *feature-plugin*
  - Gaps: GAP-51. Deps: none.
  - Scope: implement `update self` (dotnet tool/NuGet) coordinated with the daemon's version-compat check.

---

## 6. What NOT to build

| Master feature | Reason |
| --- | --- |
| Python-backed skills (§3.4) | Tied to prime's RLM/IPython control plane (prime §5.5, §5.1), which PiSharp is not adopting; PiSharp's analogue is extension tools + the structured skill pipeline (GAP-56). |
| Computer-use (§3.11) | omp-only desktop control (omp §17); beyond a terminal coding harness and a large attack surface. |
| Collab / join (§3.11) | omp-only remote relay (omp §17); the daemon's multi-terminal attach already covers local shared sessions, and remote connections are an explicit daemon non-goal (design §Non-goals). |
| Marketplace (§3.14) | TS-ecosystem npm marketplace (omp §16); PiSharp's package manager already installs from npm/git/local (catalog §5.2) — a marketplace front-end adds distribution surface, not capability. |
| Trace sharing / RL flywheel (§3.21) | Prime's trajectory upload serves its publisher's training mission (prime §5.12); irrelevant for PiSharp. |
| Execution sandbox (§3.26) | Both references are explicitly no-sandbox-by-default (omp §6, prime §6); the practical answer is gating (P29) and, when needed, a wrapper extension around `bash` via `BashSpawnHook`/middleware — a recipe, not a catalog feature. |
| In-process native tools / Rust core (§3.30) | omp's defining implementation bet (omp §2, §4); PiSharp's managed tools already avoid fork/exec for file/search ops, and a native rewrite is an anti-goal, not a capability gap. |
| Voice / TTS (§3.30) | omp-only, peripheral for a coding harness, needs native audio deps (omp §4). |

---

*End of authoritative gap declaration. Coverage: 80 master features = 21 covered (§2) + 51 gaps (§3) + 8 not-to-build (§6); plus 5 extension-surface core-changes (GAP-52…GAP-56). 56 gap entries → 31 portfolio entries (P01…P31), each dispatchable to one planning agent.*
