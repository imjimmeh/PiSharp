// @ts-nocheck
/* eslint-disable */

const SERVICE_KEY = "pisharp.workflows";
const DEFAULT_TIMEOUT_MS = 120_000;
const DEFAULT_MAX_OUTPUT_BYTES = 64_000;
const DEFAULT_MAX_CONCURRENCY = 2;
const ID_PATTERN = /^[A-Za-z0-9._-]{1,128}$/u;
const dynamicImport = new Function("specifier", "return import(specifier)");
let childProcessModule;
let fsModule;
let pathModule;

const childProcess = () => childProcessModule ??= dynamicImport("node:child_process");
const fsPromises = () => fsModule ??= dynamicImport("node:fs/promises");
const nodePath = () => pathModule ??= dynamicImport("node:path");

const PARAMETERS = {
	type: "object",
	properties: {
		prompt: { type: "string", description: "Prompt for a single workflow node." },
		workflowId: { type: "string", description: "Optional workflow id shared by related nodes." },
		nodeId: { type: "string", description: "Optional single-node id." },
		cwd: { type: "string", description: "Working directory for child PiSharp process." },
		sessionDir: { type: "string", description: "Optional child session root." },
		provider: { type: "string", description: "Optional provider override." },
		model: { type: "string", description: "Optional model override." },
		thinking: { type: "string", description: "Optional thinking level override." },
		tools: { type: "array", items: { type: "string" }, description: "Optional active tool names for child process." },
		extensions: { type: "array", items: { type: "string" }, description: "Explicit child extensions to load. Omitted children run with --no-extensions by default." },
		skills: { type: "array", items: { type: "string" }, description: "Optional child skill paths." },
		disableExtensions: { type: "boolean", description: "Disable child extensions. Defaults to true unless extensions are supplied." },
		disableSkills: { type: "boolean", description: "Disable child skills." },
		timeoutMs: { type: "integer", minimum: 1, description: "Child process timeout in milliseconds." },
		maxOutputBytes: { type: "integer", minimum: 1024, description: "Maximum captured stdout/stderr bytes per stream." },
		metadata: { type: "object", description: "Optional workflow metadata." },
		artifactPaths: { type: "array", items: { type: "string" }, description: "Optional artifact paths returned with the result." },
		maxConcurrency: { type: "integer", minimum: 1, description: "Maximum parallel DAG nodes when nodes is supplied." },
		defaults: { type: "object", description: "Default node options for DAG nodes." },
		nodes: {
			type: "array",
			description: "Optional DAG node list. If supplied, prompt is ignored and the DAG is run.",
			items: {
				type: "object",
				required: ["id", "prompt"],
				properties: {
					id: { type: "string" },
					prompt: { type: "string" },
					dependsOn: { type: "array", items: { type: "string" } },
					metadata: { type: "object" },
					artifactPaths: { type: "array", items: { type: "string" } },
				},
			},
		},
	},
	anyOf: [{ required: ["prompt"] }, { required: ["nodes"] }],
};

export default function activate(pi) {
	pi.extensions.declare({ provides: [SERVICE_KEY], activation: "eager" });
	const service = createService(pi);
	pi.extensions.provide(SERVICE_KEY, service);
	pi.registerTool({
		name: "workflow_run",
		label: "Workflow Run",
		description: "Run an isolated child PiSharp workflow node or a bounded-concurrency workflow DAG from this extension.",
		parameters: PARAMETERS,
		promptSnippet: "Use workflow_run for isolated child PiSharp sessions. Child runs default to --no-extensions to avoid recursive workflow loops.",
		promptGuidelines: [
			"Prefer a single node for isolated research or verification tasks.",
			"Use nodes + dependsOn for DAG work; failed dependencies block descendants.",
			"Child processes do not load extensions unless explicit extensions are supplied or disableExtensions is false.",
		],
		async execute(_toolCallId, args) {
			try {
				if (Array.isArray(args.nodes)) return dagToolResult(await service.runDag(args));
				return nodeToolResult(await service.runNode(args));
			} catch (error) {
				const message = error instanceof Error ? error.message : String(error);
				return { content: [{ type: "text", text: message }], details: { error: message }, isError: true };
			}
		},
	});
	return service;
}

function createService(pi) {
	return {
		version: 1,
		runNode: (request) => runNode(pi, request),
		runDag: (request) => runDag(pi, request),
		async validateDag(request) { validateDagRequest(request); return { ok: true }; },
		listRuns: (request = {}) => listRuns(pi, request.workflowId),
		getRun: (workflowId) => listRuns(pi, workflowId),
	};
}

async function runNode(pi, request) {
	const workflowId = optionalId(request.workflowId, "workflowId") ?? newWorkflowId();
	const nodeId = optionalId(request.nodeId, "nodeId") ?? newNodeId();
	const prompt = requiredString(request.prompt, "prompt");
	const startedAt = new Date();
	const path = await nodePath();
	const cwd = path.resolve(optionalString(request.cwd) ?? pi.cwd ?? process.cwd());
	const executable = optionalString(process.env.PISHARP_WORKFLOW_PISHARP_BIN) ?? "pisharp";
	const maxOutputBytes = positiveInteger(request.maxOutputBytes, DEFAULT_MAX_OUTPUT_BYTES);
	const args = buildChildArgs(request, prompt);
	await recordEvent(pi, "created", workflowId, nodeId, request, { command: { executable, args, cwd } });
	await recordEvent(pi, "running", workflowId, nodeId, request, { command: { executable, args, cwd } });
	const outcome = await spawnChild(executable, args, cwd, positiveInteger(request.timeoutMs, DEFAULT_TIMEOUT_MS), maxOutputBytes);
	const completedAt = new Date();
	const status = outcome.timedOut ? "Cancelled" : outcome.exitCode === 0 ? "Completed" : "Failed";
	const finalText = outcome.stdout.trim();
	const result = {
		workflowId,
		nodeId,
		status,
		prompt,
		finalText,
		stdout: outcome.stdout,
		stderr: outcome.stderr,
		exitCode: outcome.exitCode,
		signal: outcome.signal,
		startedAt: startedAt.toISOString(),
		completedAt: completedAt.toISOString(),
		durationMs: completedAt.getTime() - startedAt.getTime(),
		command: { executable, args, cwd },
		metadata: request.metadata,
		artifactPaths: request.artifactPaths ?? [],
		...(status === "Completed" ? {} : { errorMessage: outcome.timedOut ? "Workflow node timed out." : outcome.stderr.trim() || `Child process exited with code ${outcome.exitCode}.` }),
	};
	await recordEvent(pi, status.toLowerCase(), workflowId, nodeId, request, result);
	return result;
}

function buildChildArgs(request, prompt) {
	const args = ["--print"];
	pushPair(args, "--session-dir", optionalString(request.sessionDir));
	pushPair(args, "--provider", optionalString(request.provider));
	pushPair(args, "--model", optionalString(request.model));
	pushPair(args, "--thinking", optionalString(request.thinking));
	if (Array.isArray(request.tools) && request.tools.length > 0) pushPair(args, "--tools", request.tools.join(","));
	if (request.disableSkills === true) args.push("--no-skills");
	for (const skill of stringArray(request.skills)) pushPair(args, "--skill", skill);
	const extensions = stringArray(request.extensions);
	if (extensions.length > 0) for (const extension of extensions) pushPair(args, "--extension", extension);
	else if (request.disableExtensions !== false) args.push("--no-extensions");
	args.push(prompt);
	return args;
}

async function spawnChild(executable, args, cwd, timeoutMs, maxOutputBytes) {
	const { spawn } = await childProcess();
	return await new Promise((resolve) => {
		let stdout = "";
		let stderr = "";
		let timedOut = false;
		const child = process.platform === "win32"
			? spawn([quoteShell(executable), ...args.map(quoteShell)].join(" "), { cwd, shell: true, windowsHide: true })
			: spawn(executable, args, { cwd, shell: false, windowsHide: true });
		const timer = setTimeout(() => {
			timedOut = true;
			child.kill("SIGTERM");
		}, timeoutMs);
		child.stdout?.on("data", (chunk) => { stdout = appendBounded(stdout, String(chunk), maxOutputBytes); });
		child.stderr?.on("data", (chunk) => { stderr = appendBounded(stderr, String(chunk), maxOutputBytes); });
		child.on("error", (error) => {
			clearTimeout(timer);
			resolve({ stdout, stderr: appendBounded(stderr, error.message, maxOutputBytes), exitCode: 1, signal: null, timedOut });
		});
		child.on("close", (exitCode, signal) => {
			clearTimeout(timer);
			resolve({ stdout, stderr, exitCode, signal, timedOut });
		});
	});
}

async function runDag(pi, request) {
	validateDagRequest(request);
	const workflowId = optionalId(request.workflowId, "workflowId") ?? newWorkflowId();
	const startedAt = new Date();
	const nodes = request.nodes ?? [];
	const remaining = new Map(nodes.map((node) => [node.id, node]));
	const results = new Map();
	const maxConcurrency = positiveInteger(request.maxConcurrency, DEFAULT_MAX_CONCURRENCY);
	while (remaining.size > 0) {
		const blocked = [...remaining.values()].filter((node) => (node.dependsOn ?? []).some((dependency) => {
			const result = results.get(dependency);
			return result && result.status !== "Completed";
		}));
		for (const node of blocked) {
			const result = blockedResult(workflowId, node, "Dependency failed or was cancelled.");
			results.set(node.id, result);
			remaining.delete(node.id);
			await recordEvent(pi, "blocked", workflowId, node.id, node, result);
		}
		if (blocked.length > 0) continue;
		const ready = [...remaining.values()].filter((node) => (node.dependsOn ?? []).every((dependency) => results.get(dependency)?.status === "Completed"));
		if (ready.length === 0) throw new Error("Workflow DAG has no ready nodes.");
		for (let index = 0; index < ready.length; index += maxConcurrency) {
			const batch = ready.slice(index, index + maxConcurrency);
			const batchResults = await Promise.all(batch.map((node) => runNode(pi, mergeNodeRequest(workflowId, request.defaults, node))));
			for (let resultIndex = 0; resultIndex < batch.length; resultIndex += 1) {
				results.set(batch[resultIndex].id, batchResults[resultIndex]);
				remaining.delete(batch[resultIndex].id);
			}
		}
	}
	const ordered = nodes.map((node) => results.get(node.id));
	const completedAt = new Date();
	const status = ordered.every((result) => result.status === "Completed") ? "Completed" : "Failed";
	return { workflowId, status, succeeded: status === "Completed", results: ordered, startedAt: startedAt.toISOString(), completedAt: completedAt.toISOString(), durationMs: completedAt.getTime() - startedAt.getTime() };
}

function mergeNodeRequest(workflowId, defaults, node) {
	return { ...(defaults ?? {}), ...node, workflowId, nodeId: node.id, prompt: node.prompt, metadata: { ...(defaults?.metadata ?? {}), ...(node.metadata ?? {}) } };
}

function validateDagRequest(request) {
	const nodes = request.nodes;
	if (!Array.isArray(nodes) || nodes.length === 0) throw new Error("Workflow DAG requires at least one node.");
	optionalId(request.workflowId, "workflowId");
	if (positiveInteger(request.maxConcurrency, DEFAULT_MAX_CONCURRENCY) <= 0) throw new Error("maxConcurrency must be greater than zero.");
	const ids = new Set();
	for (const node of nodes) {
		validateId(node.id, "node.id");
		if (ids.has(node.id)) throw new Error(`Duplicate workflow node id '${node.id}'.`);
		ids.add(node.id);
		requiredString(node.prompt, `node '${node.id}' prompt`);
	}
	for (const node of nodes) {
		const dependencies = new Set();
		for (const dependency of node.dependsOn ?? []) {
			validateId(dependency, "dependsOn");
			if (dependencies.has(dependency)) throw new Error(`Workflow node '${node.id}' declares duplicate dependency '${dependency}'.`);
			dependencies.add(dependency);
			if (!ids.has(dependency)) throw new Error(`Workflow node '${node.id}' depends on unknown node '${dependency}'.`);
		}
	}
	const byId = new Map(nodes.map((node) => [node.id, node]));
	const visiting = new Set();
	const visited = new Set();
	const visit = (nodeId) => {
		if (visited.has(nodeId)) return;
		if (visiting.has(nodeId)) throw new Error(`Workflow DAG contains a cycle involving node '${nodeId}'.`);
		visiting.add(nodeId);
		for (const dependency of byId.get(nodeId)?.dependsOn ?? []) visit(dependency);
		visiting.delete(nodeId);
		visited.add(nodeId);
	};
	for (const node of nodes) visit(node.id);
}

function blockedResult(workflowId, node, reason) {
	const now = new Date().toISOString();
	return { workflowId, nodeId: node.id, status: "Blocked", prompt: node.prompt, finalText: "", stdout: "", stderr: "", exitCode: null, signal: null, startedAt: now, completedAt: now, durationMs: 0, command: { executable: "", args: [], cwd: optionalString(node.cwd) ?? process.cwd() }, metadata: node.metadata, artifactPaths: node.artifactPaths ?? [], errorMessage: reason };
}

async function recordEvent(pi, kind, workflowId, nodeId, request, details) {
	const entry = { kind, workflowId, nodeId, timestamp: new Date().toISOString(), request: summarizeRequest(request), details };
	await appendMetadata(pi, entry);
	try { await pi.appendEntry?.("workflow:node", entry); } catch (error) { console.error(`[workflow-sessions] parent audit append failed: ${error instanceof Error ? error.message : String(error)}`); }
}

async function appendMetadata(pi, entry) {
	const fs = await fsPromises();
	const path = await nodePath();
	const file = await metadataFile(pi);
	await fs.mkdir(path.dirname(file), { recursive: true });
	await fs.appendFile(file, `${JSON.stringify(entry)}\n`, "utf8");
}

async function listRuns(pi, workflowId) {
	try {
		const fs = await fsPromises();
		const content = await fs.readFile(await metadataFile(pi), "utf8");
		return content.split(/\r?\n/u).filter(Boolean).map((line) => JSON.parse(line)).filter((entry) => !workflowId || entry.workflowId === workflowId);
	} catch (error) {
		if (error?.code === "ENOENT") return [];
		throw error;
	}
}

async function metadataFile(pi) {
	const path = await nodePath();
	const configured = optionalString(process.env.PISHARP_WORKFLOW_STATE_DIR);
	const dir = configured ? path.resolve(configured) : path.join(path.resolve(pi.cwd ?? process.cwd()), ".pi", "workflows");
	return path.join(dir, "workflow-runs.jsonl");
}

function nodeToolResult(result) {
	const ok = result.status === "Completed";
	return { content: [{ type: "text", text: ok ? result.finalText || `Workflow node ${result.nodeId} completed.` : `Workflow node ${result.nodeId} ${result.status.toLowerCase()}: ${result.errorMessage ?? "failed"}` }], details: result, isError: !ok };
}

function dagToolResult(result) {
	return { content: [{ type: "text", text: `Workflow ${result.workflowId} ${result.succeeded ? "completed" : "failed"} with ${result.results.length} node(s).` }], details: result, isError: !result.succeeded };
}

function summarizeRequest(request) {
	return { workflowId: request.workflowId, nodeId: request.nodeId, cwd: request.cwd, sessionDir: request.sessionDir, provider: request.provider, model: request.model, thinking: request.thinking, tools: request.tools, extensions: request.extensions, skills: request.skills, disableExtensions: request.disableExtensions, disableSkills: request.disableSkills, timeoutMs: request.timeoutMs, metadata: request.metadata, artifactPaths: request.artifactPaths };
}

function quoteShell(value) {
	return `"${String(value).replaceAll('"', '\\"')}"`;
}

function appendBounded(current, next, maxBytes) {
	const combined = current + next;
	return combined.length <= maxBytes ? combined : combined.slice(combined.length - maxBytes);
}

function pushPair(args, flag, value) {
	if (value !== undefined) args.push(flag, value);
}

function stringArray(value) {
	return Array.isArray(value) ? value.filter((item) => typeof item === "string" && item.trim().length > 0) : [];
}

function requiredString(value, name) {
	if (typeof value !== "string" || value.trim().length === 0) throw new Error(`${name} is required.`);
	return value;
}

function optionalString(value) {
	return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

function positiveInteger(value, fallback) {
	return typeof value === "number" && Number.isInteger(value) && value > 0 ? value : fallback;
}

function validateId(value, name) {
	const text = requiredString(value, name);
	if (!ID_PATTERN.test(text)) throw new Error(`${name} may contain only letters, numbers, '.', '_' and '-' and must be at most 128 characters.`);
	return text;
}

function optionalId(value, name) {
	return value === undefined || value === null || value === "" ? undefined : validateId(value, name);
}

function newWorkflowId() { return `wf_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`; }
function newNodeId() { return `node_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`; }
