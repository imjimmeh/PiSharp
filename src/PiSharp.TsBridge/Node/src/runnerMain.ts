import fs from "node:fs/promises";
import { Console } from "node:console";
import path from "node:path";
import readline from "node:readline";
import { createJsonRpcTransport } from "./runner/jsonRpcTransport.js";

process.on("unhandledRejection", (reason) => {
	console.error("[pisharp-ts-bridge] unhandled rejection:", reason);
});
process.on("uncaughtException", (error) => {
	console.error("[pisharp-ts-bridge] uncaught exception:", error);
});

// Ignore SIGINT (Ctrl+C) and SIGBREAK (Ctrl+Break on Windows).
// The C# host sets e.Cancel=true on CancelKeyPress so the parent survives,
// but CTRL_C_EVENT is still delivered to the child by the OS console.
// The parent controls our lifecycle and kills the tree on dispose;
// we must NOT exit here or the JSON-RPC pipe breaks.
process.on("SIGINT", () => { /* ignore: parent owns lifecycle */ });
process.on("SIGBREAK", () => { /* ignore: parent owns lifecycle */ });
import { createRuntimeState } from "./runner/runtimeState.js";
import {
	createBridgeTheme,
	createToolRenderContext,
	handleCustomUiInput,
	createUiApi,
	renderComponentLines,
} from "./runner/uiApi.js";
import { createPiApi as buildPiApi } from "./runner/piApi.js";
import { dispatchEventToExtensions as dispatchEvents } from "./runner/eventDispatch.js";
import { ShimRegistry } from "./shims/shimRegistry.js";
import { createBridgeProtocol, type BridgeManifest } from "./protocol.js";
import { createTranspiler, createLoadTimings, elapsedMilliseconds, measurePhase, withTimeout } from "./runner/transpiler.js";
import { createExtensionContextModule, abortContextSignal, resetContextSignal } from "./runner/extensionContext.js";
import type {
	BridgeProtocol,
	BridgeTheme,
	CommandInfo,
	CommandResult,
	CustomMessageData,
	ExtensionDescriptor,
	ExtensionLoadResult,
	ExtensionState,
	JsonRpcRequest,
	LoadTimings,
	RenderableComponent,
	RuntimeSessionSnapshot,
	ToolResult,
	ToolRenderContext,
	ToolRenderOptions,
	ShimRegistryLike,
	ExtensionContextModule,
} from "./types.js";

if (process.argv.includes("--self-test")) {
	process.stdout.write(
		`${JSON.stringify({
			ok: true,
			registrations: ["command", "provider", "ui", "tool", "flag"],
		})}\n`,
	);
	process.exit(0);
}

const protocolStdoutWrite = process.stdout.write.bind(process.stdout);

function reserveStdoutForProtocol(): void {
	const stderrConsole = new Console({ stdout: process.stderr, stderr: process.stderr });
	console.debug = stderrConsole.debug.bind(stderrConsole);
	console.info = stderrConsole.info.bind(stderrConsole);
	console.log = stderrConsole.log.bind(stderrConsole);
	console.warn = stderrConsole.warn.bind(stderrConsole);
	(process.stdout.write as any) = process.stderr.write.bind(process.stderr);
}

reserveStdoutForProtocol();

const EXTENSION_LOAD_TIMEOUT_MS = 60_000;
const REQUEST_TIMEOUT_MS = 60_000;
const DEFAULT_BACKGROUND_LOAD_CONCURRENCY = 4;
let runtimeHasUI = false;
let runtimeSessionId: string | undefined;
const runtimeSessionSnapshot: RuntimeSessionSnapshot = {};
const runtimeCommands: CommandInfo[] = [];
const agentSessionListeners = new Map<string, Set<(event: unknown) => void>>();
let bridgeManifest: unknown;
let bridgeProtocol: BridgeProtocol = createBridgeProtocol();
let bridgeCacheDirectory = "";
const shimRegistry: ShimRegistryLike = new ShimRegistry(() => bridgeCacheDirectory) as unknown as ShimRegistryLike;
const transpiler = createTranspiler(shimRegistry, () => bridgeManifest);
transpiler.registerExitCleanup();

type BackgroundLoadEntry = {
	result?: ExtensionLoadResult;
	promise: Promise<ExtensionLoadResult>;
	resolve?: (result: ExtensionLoadResult) => void;
};

const backgroundLoads = new Map<string, BackgroundLoadEntry>();

function backgroundLog(message: string): void {
	console.error(`[pisharp] bg: ${message}`);
}

const rl = readline.createInterface({
	input: process.stdin,
	output: process.stdout,
	terminal: false,
});
rl.on("close", () => {
	// stdin was closed — parent process is gone; exit cleanly.
	process.exit(0);
});
const write = (msg: object): void => {
	protocolStdoutWrite(
		`${JSON.stringify(msg).replace(
			/[\u007f-\uffff]/g,
			(char) => `\\u${char.charCodeAt(0).toString(16).padStart(4, "0")}`,
		)}\n`,
	);
};

function createDescriptor(extensionId: string): ExtensionDescriptor {
	return {
		schemaVersion: 1,
			extensionPath: extensionId,
			sourceHash: null,
			dependencyHashes: [],
			packageName: null,
			packageVersion: null,
			tools: [],
		commands: [],
		shortcuts: [],
		flags: [],
		promptSections: [],
		promptTransforms: [],
		providers: [],
		skills: [],
		providesServices: [],
		consumesServices: [],
		activation: "auto",
	};
}

function resetDescriptor(extensionId: string, state: ExtensionState): void {
	state.descriptor = createDescriptor(extensionId);
}

function normalizeServiceKey(key: string): string {
	if (typeof key !== "string" || !key.trim())
		throw new Error("Extension service key must be a non-empty string");
	return key.trim();
}

function rememberDescriptorService(state: ExtensionState, field: "providesServices" | "consumesServices", key: string): void {
	if (!state.descriptor[field].includes(key)) state.descriptor[field].push(key);
	state.descriptor.activation = "eager";
}

const runtimeState = createRuntimeState({
	createDescriptor,
	onDescriptorServiceUse: rememberDescriptorService,
});
const {
	extensionStates,
	stateFor,
	addEventHandler,
	provideExtensionService,
	getExtensionService,
	waitForExtensionService,
	resetRuntimeState,
} = runtimeState;
const transport = createJsonRpcTransport({
	write,
	requestTimeoutMs: REQUEST_TIMEOUT_MS,
});
const {
	sendRequest,
	trackRegistrationRequest,
	queueRegistrationRequest,
	drainQueuedRegistrationRequests,
	flushRegistrationRequests,
	handleResponse,
	rejectAllPending,
} = transport;

function sendRuntimeAction(extensionId: string, action: string, payload: Record<string, unknown> = {}): Promise<unknown> {
	return sendRequest(bridgeProtocol.methods.runtimeAction, {
		extensionId,
		action,
		payload,
	}, { timeoutMs: null });
}

function setRuntimeSessionSnapshot(session: RuntimeSessionSnapshot): void {
	for (const key of Object.keys(runtimeSessionSnapshot))
		delete runtimeSessionSnapshot[key];
	if (session && typeof session === "object")
		Object.assign(runtimeSessionSnapshot, session);
	if (typeof runtimeSessionSnapshot.sessionId === "string")
		runtimeSessionId = runtimeSessionSnapshot.sessionId;
}

function setRuntimeCommands(commands: CommandInfo[]): void {
	runtimeCommands.length = 0;
	if (Array.isArray(commands)) runtimeCommands.push(...commands);
}

function setShimRuntimeSnapshot(session: RuntimeSessionSnapshot): void {
	setRuntimeSessionSnapshot(session);
}

function installShimRuntime(extensionId: string): void {
	globalThis.__pisharpTsBridge = {
		actions: bridgeProtocol.runtimeActions,
		get snapshot() { return runtimeSessionSnapshot; },
		setSnapshot: setShimRuntimeSnapshot,
		runtime: (action: string, payload = {}) => sendRuntimeAction(extensionId, action, payload),
		_agentSessionListeners: agentSessionListeners,
	};
}

function addRuntimeCommand(command: CommandInfo): void {
	if (!command?.name) return;
	const exists = runtimeCommands.some(
		(existing) =>
			existing.name === command.name &&
			existing.source === command.source &&
			existing.sourceInfo?.source === command.sourceInfo?.source,
	);
	if (!exists) runtimeCommands.push(command);
}

async function dispatchEmitToExtensions(channel: string, payload: unknown): Promise<void> {
	for (const [extensionId, state] of extensionStates.entries()) {
		const handlers = state.events.get(channel) ?? [];
		for (const handler of handlers) {
			try {
				await handler(payload, contextModule.createExtensionContext(extensionId));
			} catch (error) {
				console.error(
					`[pisharp-ts-bridge] emit ${channel} handler for ${extensionId} failed: ${(error as { message?: string })?.message ?? String(error)}`,
				);
			}
		}
	}
}

async function dispatchSubagentSessionEvent(sessionId: string, event: unknown): Promise<void> {
	const listeners = agentSessionListeners.get(sessionId);
	if (!listeners) return;

	for (const listener of listeners) {
		try { await Promise.resolve(listener(event)); } catch (error) {
			console.error(
				`[pisharp-ts-bridge] subagent event listener for session ${sessionId} failed: ${(error as { message?: string })?.message ?? String(error)}`,
			);
		}
	}
}

const contextModule: ExtensionContextModule = createExtensionContextModule(
	bridgeProtocol,
	runtimeSessionSnapshot,
	() => runtimeHasUI,
	sendRuntimeAction,
	sendRequest,
	createUiApi,
	stateFor,
	() => runtimeSessionId,
);

function createPiApi(extensionId: string, state: ExtensionState, registrationSink: (method: string, params?: Record<string, unknown>) => Promise<unknown> | void = trackRegistrationRequest) {
	return buildPiApi({
		extensionId,
		state,
		deps: {
			sendRequest,
			trackRegistrationRequest: registrationSink,
			provideExtensionService,
			getExtensionService,
			waitForExtensionService,
			rememberDescriptorService,
			normalizeServiceKey,
			addEventHandler,
			createUiApi,
			emitEvent: dispatchEmitToExtensions,
			cwd: process.cwd(),
			getCommandsSnapshot: () => runtimeCommands.slice(),
			addCommandToSnapshot: addRuntimeCommand,
			getSessionNameSnapshot: () => runtimeSessionSnapshot.sessionName as string | undefined,
			getSnapshot: () => runtimeSessionSnapshot,
			protocol: bridgeProtocol,
		},
	});
}

async function flushQueuedRegistrationRequests(timings?: LoadTimings): Promise<Map<string, string>> {
	const failedExtensions = new Map<string, string>();
	const requests = drainQueuedRegistrationRequests();
	if (requests.length === 0) return failedExtensions;
	await measurePhase(timings ?? createLoadTimings(), "registrationApply", async () => {
		for (const request of requests) {
			try {
				await trackRegistrationRequest(request.method, request.params);
			} catch (error) {
				const message = (error as { message?: string })?.message ?? String(error);
				const extensionPath = typeof request.params?.extensionId === "string" ? request.params.extensionId : undefined;
				if (extensionPath) failedExtensions.set(extensionPath, message);
				if (extensionPath) {
					const state = stateFor(extensionPath);
					if (request.method === bridgeProtocol.methods.registerTool && typeof request.params?.name === "string")
						state.descriptor.tools = state.descriptor.tools.filter((item) => item.name !== request.params?.name);
					else if (request.method === bridgeProtocol.methods.registerCommand && typeof request.params?.name === "string")
						state.descriptor.commands = state.descriptor.commands.filter((item) => item.name !== request.params?.name);
					else if (request.method === bridgeProtocol.methods.registerShortcut && typeof request.params?.keys === "string")
						state.descriptor.shortcuts = state.descriptor.shortcuts.filter((item) => item.keys !== request.params?.keys);
					else if (request.method === bridgeProtocol.methods.registerFlag && typeof request.params?.name === "string")
						state.descriptor.flags = state.descriptor.flags.filter((item) => item.name !== request.params?.name);
					else if (request.method === bridgeProtocol.methods.registerPromptSection && typeof request.params?.id === "string")
						state.descriptor.promptSections = state.descriptor.promptSections.filter((item) => item.id !== request.params?.id);
					else if (request.method === bridgeProtocol.methods.registerPromptTransform && typeof request.params?.name === "string")
						state.descriptor.promptTransforms = state.descriptor.promptTransforms.filter((item) => item.name !== request.params?.name);
				}
				console.error(`[pisharp-ts-bridge] queued registration for ${extensionPath ?? "extension"} failed: ${message}`);
			}
		}
	});
	return failedExtensions;
}

async function loadExtension(extensionPath: string, timings: LoadTimings, registrationSink: (method: string, params?: Record<string, unknown>) => Promise<unknown> | void = trackRegistrationRequest): Promise<ExtensionLoadResult> {
	const extensionId = extensionPath;
	const state = stateFor(extensionId);
	installShimRuntime(extensionId);
	resetDescriptor(extensionId, state);
	console.error(`[pisharp] ext: transpile ${extensionPath}`);
	const importResult = await transpiler.importExtensionModule(extensionPath, timings);
	console.error(`[pisharp] ext: import ${extensionPath}`);
	const metadata = importResult.descriptorMetadata;
	state.descriptor.sourceHash = metadata.sourceHash;
	state.descriptor.dependencyHashes = metadata.dependencyHashes;
	state.descriptor.packageName = metadata.packageName ?? null;
	state.descriptor.packageVersion = metadata.packageVersion ?? null;
	const mod = importResult.module;
	const activate = mod.default ?? mod.activate;
	if (typeof activate !== "function")
		return {
			ok: true,
			extensionPath,
			skipped: true,
			descriptor: state.descriptor,
		};
	console.error(`[pisharp] ext: activate ${extensionPath}`);
	const registrationMode = { sink: registrationSink };
	await measurePhase(timings, "activation", () =>
		(activate as Function)(createPiApi(extensionId, state, (method, params) => registrationMode.sink(method, params))),
	);
	registrationMode.sink = trackRegistrationRequest;
	console.error(`[pisharp] ext: flush ${extensionPath}`);
	if (registrationSink === trackRegistrationRequest)
		await measurePhase(timings, "registrationFlush", () =>
			flushRegistrationRequests(),
		);
	console.error(`[pisharp] ext: done ${extensionPath}`);
	return { ok: true, extensionPath, descriptor: state.descriptor };
}

async function loadExtensionSafely(extensionPath: string, registrationSink: (method: string, params?: Record<string, unknown>) => Promise<unknown> | void = trackRegistrationRequest): Promise<ExtensionLoadResult> {
	const timings = createLoadTimings();
	const startedAt = process.hrtime.bigint();
	try {
		const result = await withTimeout(
			loadExtension(extensionPath, timings, registrationSink),
			EXTENSION_LOAD_TIMEOUT_MS,
			`Extension '${extensionPath}' timed out during load.`,
		);
		if (runtimeHasUI && result?.ok && !result.skipped) {
			await dispatchSessionStartToExtension(extensionPath, "ui_ready");
		}
		timings.total = elapsedMilliseconds(startedAt);
		return { ...result, timings };
	} catch (error) {
		timings.total = elapsedMilliseconds(startedAt);
		return {
			ok: false,
			extensionPath,
			error: (error as { message?: string })?.message ?? String(error),
			timings,
		};
	}
}

function ensureBackgroundEntry(extensionPath: string): BackgroundLoadEntry {
	const existing = backgroundLoads.get(extensionPath);
	if (existing && !existing.result) return existing;
	let resolveEntry: (result: ExtensionLoadResult) => void = () => {};
	const entry: BackgroundLoadEntry = {
		promise: new Promise<ExtensionLoadResult>((resolve) => { resolveEntry = resolve; }),
		resolve: resolveEntry,
	};
	backgroundLoads.set(extensionPath, entry);
	return entry;
}

function completeBackgroundEntry(extensionPath: string, result: ExtensionLoadResult): void {
	const entry = backgroundLoads.get(extensionPath) ?? ensureBackgroundEntry(extensionPath);
	entry.result = result;
	entry.resolve?.(result);
}

function normalizeConcurrency(value: unknown, fallback: number): number {
	const parsed = Number(value ?? fallback);
	if (!Number.isFinite(parsed) || parsed < 1) return fallback;
	return Math.max(1, Math.floor(parsed));
}

function startBackgroundLoadExtensions(extensionPaths: string[], concurrency = DEFAULT_BACKGROUND_LOAD_CONCURRENCY): void {
	const scheduled = extensionPaths.filter((extensionPath) => {
		const existing = backgroundLoads.get(extensionPath);
		if (existing && !existing.result) return false;
		ensureBackgroundEntry(extensionPath);
		return true;
	});
	backgroundLog(`schedule requested=${extensionPaths.length} scheduled=${scheduled.length} concurrency=${concurrency}`);
	if (scheduled.length === 0) return;

	setTimeout(() => {
		backgroundLog(`batch start count=${scheduled.length} concurrency=${concurrency}`);
		loadExtensionsSafely(scheduled, concurrency, (result) => completeBackgroundEntry(result.extensionPath ?? "", result), false)
			.then((batch) => {
				backgroundLog(`batch done count=${scheduled.length} results=${batch.results?.length ?? 0}`);
				const byPath = new Map((batch.results ?? []).map((result) => [result.extensionPath, result]));
				for (const extensionPath of scheduled) {
					const existing = backgroundLoads.get(extensionPath);
					if (existing?.result) continue;
					completeBackgroundEntry(
						extensionPath,
						byPath.get(extensionPath) ?? { ok: false, extensionPath, error: "Background batch did not return a result." },
					);
				}
			})
			.catch((error) => {
				backgroundLog(`batch fail count=${scheduled.length} error=${(error as { message?: string })?.message ?? String(error)}`);
				for (const extensionPath of scheduled) {
					completeBackgroundEntry(extensionPath, {
						ok: false,
						extensionPath,
						error: (error as { message?: string })?.message ?? String(error),
					});
				}
			});
	}, 0);
}

async function dispatchSessionStartToExtension(extensionId: string, reason: string): Promise<void> {
	const state = stateFor(extensionId);
	const handlers = state.events.get("session_start") ?? [];
	for (const handler of handlers) {
		try {
			await handler({ reason }, contextModule.createExtensionContext(extensionId));
		} catch (error) {
			console.error(
				`[pisharp-ts-bridge] session_start for ${extensionId} failed: ${(error as { message?: string })?.message ?? String(error)}`,
			);
		}
	}
}

async function usesOrderedRuntimeState(extensionPath: string): Promise<boolean> {
	try {
		const source = await fs.readFile(extensionPath, "utf8");
		return /\bpi\.(?:getAllTools|getActiveTools|getCommands|getAllSkills|getSelectedSkills|getFlags|getFlag|on)\s*\(/.test(source)
			|| /\bpi\.events\.(?:on|emit)\s*\(/.test(source);
	} catch {
		return true;
	}
}

async function mapWithConcurrency<T, R>(items: T[], concurrency: number, mapper: (item: T) => Promise<R>): Promise<R[]> {
	const limit = Math.max(1, Math.min(concurrency, items.length || 1));
	const results = new Array<R>(items.length);
	let nextIndex = 0;
	async function worker(): Promise<void> {
		while (true) {
			const index = nextIndex++;
			if (index >= items.length) return;
			results[index] = await mapper(items[index]!);
		}
	}
	await Promise.all(Array.from({ length: limit }, () => worker()));
	return results;
}

async function loadExtensionsSafely(extensionPaths: string[], concurrency = Number.POSITIVE_INFINITY, onResult?: (result: ExtensionLoadResult) => void, queueParallelRegistrations = true): Promise<{ ok: boolean; results: ExtensionLoadResult[] }> {
	const startedAt = process.hrtime.bigint();
	const results: ExtensionLoadResult[] = [];
	if (!queueParallelRegistrations) {
		results.push(...await mapWithConcurrency(extensionPaths, concurrency, async (extensionPath) => {
			backgroundLog(`load start ${extensionPath}`);
			const result = await loadExtensionSafely(extensionPath);
			backgroundLog(`load ${result.ok ? "ok" : "fail"} ${extensionPath}${result.error ? ` error=${result.error}` : ""}`);
			onResult?.(result);
			return result;
		}));
	} else {
	let pendingParallel: string[] = [];
	async function flushParallel(): Promise<void> {
		if (pendingParallel.length === 0) return;
		const batch = pendingParallel;
		pendingParallel = [];
		const batchResults = await mapWithConcurrency(batch, concurrency, (extensionPath) =>
			loadExtensionSafely(extensionPath, queueRegistrationRequest),
		);
		results.push(...batchResults);
		const timing = results.find((result) => result.ok && result.timings)?.timings;
		const failedExtensions = await flushQueuedRegistrationRequests(timing);
		for (const result of results) {
			const error = failedExtensions.get(result.extensionPath);
			if (error) {
				result.ok = false;
				result.error = error;
			}
		}
		for (const result of batchResults) onResult?.(result);
	}
	for (const extensionPath of extensionPaths) {
		if (await usesOrderedRuntimeState(extensionPath)) {
			await flushParallel();
			const result = await loadExtensionSafely(extensionPath);
			results.push(result);
			onResult?.(result);
		} else {
			pendingParallel.push(extensionPath);
		}
	}
	await flushParallel();
	}
	const successfulTimings = results
		.filter((result) => result.ok)
		.map((result) => result.timings)
		.filter((timings): timings is LoadTimings => Boolean(timings));
	const byPath = new Map(results.map((result) => [result.extensionPath, result]));
	for (const extensionPath of extensionPaths) {
		const result = byPath.get(extensionPath);
		if (!result?.ok) continue;
		const state = stateFor(extensionPath);
		const missingTool = state.descriptor.tools.find((tool) => !tool.name || !state.tools.has(tool.name));
		const missingCommand = state.descriptor.commands.find((command) => !command.name || !state.commands.has(command.name));
		const missingShortcut = state.descriptor.shortcuts.find((shortcut) => !shortcut.keys || !state.commands.has(`shortcut:${shortcut.keys}`));
		const failed = missingTool ?? missingCommand ?? missingShortcut;
		if (failed) {
			result.ok = false;
			result.error = `Registration '${"name" in failed ? failed.name : failed.keys}' was not applied.`;
		}
	}
	const registrationFlush = elapsedMilliseconds(startedAt) - Math.max(0, ...successfulTimings.map((timings) => timings.total));
	for (const timings of successfulTimings) {
		timings.registrationFlush += Math.max(0, registrationFlush);
		timings.total += Math.max(0, registrationFlush);
	}
	return { ok: true, results };
}

async function dispatchEventToExtensions(name: string, payload: Record<string, unknown>) {
	return await dispatchEvents({
		name,
		payload,
		extensionStates,
		createExtensionContext: contextModule.createExtensionContext,
		flushRegistrationRequests,
	});
}

async function invokeCommand(extensionId: string, name: string, args: string): Promise<CommandResult> {
	const handler = stateFor(extensionId).commands.get(name);
	if (!handler)
		return {
			handled: false,
			isError: true,
			message: `Command ${extensionId}:${name} is not registered`,
		};
	const result = await handler(args ?? "", contextModule.createCommandContext(extensionId));
	return result && typeof result === "object" && "handled" in result
		? (result as CommandResult)
		: {
				handled: true,
				message: typeof result === "string" ? result : undefined,
			};
}

async function invokeTool(extensionId: string, name: string, args: Record<string, unknown>, toolCallId: string): Promise<ToolResult> {
	const tool = stateFor(extensionId).tools.get(name);
	if (!tool?.execute)
		return {
			content: [
				{ type: "text", text: `Tool ${extensionId}:${name} is not registered` },
			],
			isError: true,
		};
	const signal = new AbortController().signal;
	const result = await tool.execute(
		toolCallId ?? "",
		args ?? {},
		signal,
		() => {},
		contextModule.createExtensionContext(extensionId),
	);
	if (Array.isArray(result?.content)) return result;
	if (typeof result === "string")
		return { content: [{ type: "text", text: result }], isError: false };
	return {
		content: [{ type: "text", text: JSON.stringify(result ?? null) }],
		isError: false,
	};
}

async function renderToolSlot(extensionId: string, name: string, request: Record<string, unknown>, slot: "call" | "result"): Promise<{ lines: string[] }> {
	const tool = stateFor(extensionId).tools.get(name);
	const renderer = slot === "call" ? tool?.renderCall : tool?.renderResult;
	if (typeof renderer !== "function") return { lines: [] as string[] };
	const args = (request.arguments ?? {}) as Record<string, unknown>;
	const theme = createBridgeTheme();
	const context = createToolRenderContext(request as { toolCallId?: string; isPartial?: boolean; expanded?: boolean; isError?: boolean }, args, process.cwd());
	const width = (request.width as number) ?? 120;
	let component: RenderableComponent;
	if (slot === "call") {
		component = (renderer as (a: unknown, t: BridgeTheme, c: ToolRenderContext) => RenderableComponent)(args, theme, context);
	} else {
		component = (renderer as (r: unknown, o: ToolRenderOptions, t: BridgeTheme, c: ToolRenderContext) => RenderableComponent)(
			(request.result as Record<string, unknown>) ?? { content: [], details: undefined },
			{
				expanded: (request.expanded as boolean) ?? false,
				isPartial: (request.isPartial as boolean) ?? false,
			},
			theme,
			context,
		);
	}
	return { lines: renderComponentLines(component, width) };
}

function ok(id: string | number | null | undefined, result: unknown = { ok: true }): void {
	if (id == null) return;
	write({ jsonrpc: "2.0", id, result });
}
function fail(id: string | number | null | undefined, error: unknown): void {
	if (id == null) return;
	write({
		jsonrpc: "2.0",
		id,
		error: { code: -32000, message: (error as { message?: string })?.message ?? String(error) },
	});
}

function resetExtensionRuntime(): void {
	resetRuntimeState();
	resetContextSignal();
	agentSessionListeners.clear();
	rejectAllPending("Extension runtime reset before response was received");
	transpiler.clearTranspiledBySource();
	transpiler.bumpImportVersion();
}

rl.on("line", async (line: string) => {
	let request: JsonRpcRequest;
	try {
		request = JSON.parse(line) as JsonRpcRequest;
	} catch (error) {
		write({
			jsonrpc: "2.0",
			id: null,
			error: {
				code: -32700,
				message: `Invalid JSON: ${(error as { message?: string })?.message ?? String(error)}`,
			},
		});
		return;
	}
	if (!request.method && handleResponse(request)) return;
	try {
		if (request.method === "initialize") {
			const p = request.params ?? {};
			setRuntimeCommands(p.commands as CommandInfo[]);
			setRuntimeSessionSnapshot(p.session as RuntimeSessionSnapshot);
			if (typeof p.cacheDirectory === "string") {
				bridgeCacheDirectory = p.cacheDirectory;
				transpiler.setCacheDirectory(p.cacheDirectory);
			}
			if (typeof p.cacheEnabled === "boolean")
				transpiler.setCacheEnabled(p.cacheEnabled);
			if (typeof p.hasUi === "boolean")
				runtimeHasUI = p.hasUi;
			if (typeof p.sessionId === "string")
				runtimeSessionId = p.sessionId;
			bridgeManifest = p.bridgeManifest;
			bridgeProtocol = createBridgeProtocol(bridgeManifest as BridgeManifest | null | undefined);
			try {
				await shimRegistry.initialize(bridgeManifest);
			} catch (error) {
				console.error(
					`[pisharp-ts-bridge] bridge manifest shim generation failed: ${(error as { message?: string })?.message ?? String(error)}`,
				);
				shimRegistry.clear();
			}
			const results: ExtensionLoadResult[] = [];
			for (const extensionPath of (p.extensionPaths as string[]) ?? [])
				results.push(await loadExtensionSafely(extensionPath));
			ok(request.id, { ok: true, results });
			return;
		}
		if (request.method === "load_extension") {
			ok(request.id, await loadExtensionSafely((request.params as Record<string, unknown>)?.extensionPath as string));
			return;
		}
		if (request.method === "load_extensions") {
			const p = request.params ?? {};
			setRuntimeCommands(p.commands as CommandInfo[]);
			setRuntimeSessionSnapshot(p.session as RuntimeSessionSnapshot);
			if (typeof p.hasUi === "boolean")
				runtimeHasUI = p.hasUi;
			if (typeof p.sessionId === "string")
				runtimeSessionId = p.sessionId;
			ok(
				request.id,
				await loadExtensionsSafely((p.extensionPaths as string[]) ?? [], normalizeConcurrency(p.concurrency, Number.POSITIVE_INFINITY)),
			);
			return;
		}
		if (request.method === "start_background_load_extension") {
			const p = request.params ?? {};
			setRuntimeCommands(p.commands as CommandInfo[]);
			setRuntimeSessionSnapshot(p.session as RuntimeSessionSnapshot);
			if (typeof p.hasUi === "boolean")
				runtimeHasUI = p.hasUi;
			if (typeof p.sessionId === "string")
				runtimeSessionId = p.sessionId;
			const extensionPath = p.extensionPath as string;
			backgroundLog(`rpc start single ${extensionPath}`);
			startBackgroundLoadExtensions([extensionPath], normalizeConcurrency(p.concurrency, DEFAULT_BACKGROUND_LOAD_CONCURRENCY));
			ok(request.id, { ok: true });
			return;
		}
		if (request.method === "start_background_load_extensions") {
			const p = request.params ?? {};
			setRuntimeCommands(p.commands as CommandInfo[]);
			setRuntimeSessionSnapshot(p.session as RuntimeSessionSnapshot);
			if (typeof p.hasUi === "boolean")
				runtimeHasUI = p.hasUi;
			if (typeof p.sessionId === "string")
				runtimeSessionId = p.sessionId;
			backgroundLog(`rpc start batch count=${((p.extensionPaths as string[]) ?? []).length}`);
			startBackgroundLoadExtensions((p.extensionPaths as string[]) ?? [], normalizeConcurrency(p.concurrency, DEFAULT_BACKGROUND_LOAD_CONCURRENCY));
			ok(request.id, { ok: true });
			return;
		}
		if (request.method === "background_load_status") {
			const extensionPath = (request.params ?? {}).extensionPath as string;
			const entry = backgroundLoads.get(extensionPath);
			if (!entry) {
				ok(request.id, { extensionPath, complete: false });
				return;
			}
			ok(request.id, entry.result ? { extensionPath, complete: true, result: entry.result } : { extensionPath, complete: false });
			return;
		}
		if (request.method === "background_load_statuses") {
			const extensionPaths = ((request.params ?? {}).extensionPaths as string[]) ?? [];
			ok(request.id, {
				statuses: extensionPaths.map((extensionPath) => {
					const entry = backgroundLoads.get(extensionPath);
					return entry?.result
						? { extensionPath, complete: true, result: entry.result }
						: { extensionPath, complete: false };
				}),
			});
			return;
		}
		if (request.method === "set_runtime_has_ui") {
			runtimeHasUI = request.params?.hasUi === true;
			ok(request.id);
			return;
		}
		if (request.method === "set_runtime_session") {
			runtimeSessionId =
				typeof request.params?.sessionId === "string"
					? request.params.sessionId
					: undefined;
			ok(request.id);
			return;
		}
		if (request.method === "reset_extensions") {
			resetExtensionRuntime();
			setRuntimeCommands([]);
			ok(request.id);
			return;
		}
		if (request.method === "event") {
			const p = request.params ?? {};
			const name = p.name as string;
			if (name === "subagents:session:events") {
				const payload = p.payload as Record<string, unknown> | undefined;
				const sessionId = payload?.sessionId as string | undefined;
				const events = Array.isArray(payload?.events) ? payload.events : [];
				if (sessionId && events.length > 0) {
					for (const event of events) {
						await dispatchSubagentSessionEvent(sessionId, event);
					}
				}
				ok(request.id);
				return;
			}
			if (name === "subagents:session:event") {
				const payload = p.payload as Record<string, unknown> | undefined;
				const sessionId = payload?.sessionId as string | undefined;
				const event = payload?.event;
				if (sessionId && event) {
					await dispatchSubagentSessionEvent(sessionId, event);
				}
				ok(request.id);
				return;
			}
			ok(
				request.id,
				await dispatchEventToExtensions(name, p.payload as Record<string, unknown>),
			);
			return;
		}
		if (request.method === "emit_event") {
			const p = request.params ?? {};
			await dispatchEmitToExtensions(
				p.name as string,
				p.payload,
			);
			ok(request.id);
			return;
		}
		if (request.method === "invoke_command") {
			const { extensionId, name, args } = (request.params ?? {}) as { extensionId: string; name: string; args: string };
			ok(request.id, await invokeCommand(extensionId, name, args));
			return;
		}
		if (request.method === "invoke_tool" || request.method === "tool_execute") {
			const params = (request.params ?? {}) as Record<string, unknown>;
			ok(
				request.id,
				await invokeTool(
					(params.extensionId as string) ?? "default",
					params.name as string,
					(params.arguments ?? params.parameters) as Record<string, unknown>,
					(params.toolCallId ?? params.id) as string,
				),
			);
			return;
		}
		if (request.method === "custom_ui_input") {
			ok(request.id, handleCustomUiInput(request.params as { requestId: string; data?: string; width?: number; height?: number }));
			return;
		}
		if (
			request.method === "render_tool_call" ||
			request.method === "render_tool_result"
		) {
			const params = (request.params ?? {}) as Record<string, unknown>;
			ok(
				request.id,
				await renderToolSlot(
					(params.extensionId as string) ?? "default",
					params.name as string,
					params,
					request.method === "render_tool_call" ? "call" : "result",
				),
			);
			return;
		}
		if (request.method === "render_message") {
			const params = (request.params ?? {}) as Record<string, unknown>;
			const state = stateFor((params.extensionId as string) ?? "default");
			const handler = state.renderers.get(params.name as string);
			if (typeof handler !== "function") {
				ok(request.id, { lines: [] as string[], preserveBuiltIn: true });
				return;
			}
			try {
				const message: CustomMessageData = {
					role: "custom",
					customType: (params.customType as string) ?? "",
					content: (params.content ?? (params.text as string) ?? "") as string | unknown[],
					display: Boolean(params.display),
					details: params.details,
					timestamp: Date.now(),
				};
				const component = await handler(message, { expanded: Boolean(params.expanded) }, createBridgeTheme());
				if (component == null) {
					ok(request.id, { lines: [] as string[], preserveBuiltIn: true });
					return;
				}
				const lines = renderComponentLines(component as RenderableComponent, (params.width as number) ?? 80);
				ok(request.id, { lines, preserveBuiltIn: lines.length === 0 });
			} catch {
				ok(request.id, { lines: [] as string[], preserveBuiltIn: true });
			}
			return;
		}
		if (request.method === "decorate_message") {
			const params = (request.params ?? {}) as Record<string, unknown>;
			const state = stateFor((params.extensionId as string) ?? "default");
			const handler = state.decorators.get(params.name as string);
			if (typeof handler !== "function") {
				ok(request.id, { rows: (params.rows as unknown[]) ?? [] });
				return;
			}
			try {
				const rows = await handler(
					{
						data: (params.data as Record<string, unknown>) ?? {},
						text: (params.text as string) ?? "",
						role: (params.role as string) ?? "",
						customType: (params.customType as string) ?? "",
					},
					((params.rows as Array<{ text?: string; kind?: string; spans?: unknown }>) ?? []).map((r) => ({
						text: r.text ?? "",
						kind: r.kind ?? "normal",
						spans: r.spans,
					})),
				);
				ok(request.id, {
					rows: Array.isArray(rows) ? rows : ((params.rows as unknown[]) ?? []),
				});
			} catch {
				ok(request.id, { rows: (params.rows as unknown[]) ?? [] });
			}
			return;
		}
		if (request.method === "provider_callback") {
			const p = request.params ?? {};
			ok(request.id, {
				role: "assistant",
				content: [{ type: "text", text: "TS provider callback response" }],
				api: p.providerApi,
				stopReason: "stop",
			});
			return;
		}
		if (request.method === "signal_abort") {
			abortContextSignal();
			ok(request.id);
			return;
		}
		if (request.method === "_debug_signal_handlers") {
			// Test-only introspection: reports registered signal handler counts.
			// Used by the node:test suite to verify SIGINT/SIGBREAK handlers are registered.
			ok(request.id, {
				sigint: process.listenerCount("SIGINT"),
				sigbreak: process.listenerCount("SIGBREAK"),
			});
			return;
		}
		write({
			jsonrpc: "2.0",
			id: request.id,
			error: { code: -32601, message: `Unknown method ${request.method}` },
		});
	} catch (error) {
		fail(request.id, error as Error);
	}
});
