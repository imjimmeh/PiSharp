# PiSharp Workflow Sessions Extension

`extensions/workflow-sessions` is a bundled TypeScript extension that provides workflow/DAG orchestration without adding a workflow runtime to PiSharp core.

Load it with:

```bash
pisharp --extension ./extensions/workflow-sessions
```

The extension registers:

- `workflow_run` — a model-callable tool.
- `pisharp.workflows` — an eager extension service for other TypeScript extensions.

## How it works

Each workflow node is a separate child `pisharp --print` process. That child creates or uses normal PiSharp JSONL sessions through existing CLI/session behavior. The scheduler, DAG validation, metadata store, and tool/service API live in this extension.

By default, child nodes run with `--no-extensions` to avoid recursive workflow loops. Pass explicit `extensions` in the node request, or set `disableExtensions: false`, only when a child really needs extensions.

## Single node

```json
{
  "workflowId": "research",
  "nodeId": "runtime-boundary",
  "prompt": "Research the runtime/session boundary and summarize it.",
  "timeoutMs": 120000
}
```

## DAG

```json
{
  "workflowId": "feature-research",
  "maxConcurrency": 2,
  "defaults": {
    "sessionDir": ".pi/workflow-sessions"
  },
  "nodes": [
    { "id": "contracts", "prompt": "Research extension contracts." },
    { "id": "bridge", "prompt": "Research TypeScript bridge behavior." },
    { "id": "synthesis", "prompt": "Synthesize findings.", "dependsOn": ["contracts", "bridge"] }
  ]
}
```

Dependencies must complete successfully before descendants run. Failed or cancelled dependencies mark descendants as blocked.

## Runtime options

Node requests support:

- `cwd`
- `sessionDir`
- `provider`
- `model`
- `thinking`
- `tools`
- `skills`
- `extensions`
- `disableExtensions`
- `disableSkills`
- `timeoutMs`
- `maxOutputBytes`
- `metadata`
- `artifactPaths`

## Environment variables

| Variable | Purpose |
| --- | --- |
| `PISHARP_WORKFLOW_PISHARP_BIN` | Child executable. Defaults to `pisharp`. |
| `PISHARP_WORKFLOW_STATE_DIR` | Metadata directory. Defaults to `<cwd>/.pi/workflows`. |

Default timeout is 120 seconds per child node. Default captured stdout/stderr limit is 64 KB per stream.

## Metadata

The extension appends workflow events to:

```text
<cwd>/.pi/workflows/workflow-runs.jsonl
```

or to `PISHARP_WORKFLOW_STATE_DIR/workflow-runs.jsonl` when configured. Events include `created`, `running`, `completed`, `failed`, `cancelled`, and `blocked` with workflow/node ids, timestamps, command metadata, exit code, and bounded output summaries.

The extension also appends concise `workflow:node` audit entries to the parent session through existing extension APIs. Full child transcripts remain in the child PiSharp sessions.

## Extension service

Other TypeScript extensions can consume the service:

```javascript
export default async function activate(pi) {
  const workflows = await pi.extensions.waitFor("pisharp.workflows", { timeoutMs: 1000 });
  const result = await workflows.runNode({ workflowId: "demo", nodeId: "one", prompt: "Say ok." });
  pi.registerCommand(`workflow-${result.status}`, { description: "Workflow status", handler: () => result.finalText ?? "" });
}
```

Service methods:

- `runNode(request)`
- `runDag(request)`
- `validateDag(request)`
- `listRuns({ workflowId? })`
- `getRun(workflowId)`

## Maintainer guardrails

This extension intentionally owns workflow orchestration. Do not add `SessionRuntime.Workflows`, workflow DTOs in `PiSharp.Extensions`, or TypeScript bridge `workflow_*` runtime actions unless a separate SDK need is proven and approved. Prefer CLI/process composition for independent workflow node sessions.
