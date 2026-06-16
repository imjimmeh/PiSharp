import type {} from "node:stream";
import type {
	BridgeProtocol,
	ExtensionContext,
	ExtensionContextModule,
	ExtensionState,
	ReplacementSessionContext,
	RuntimeSessionSnapshot,
	SessionCommandContext,
	SessionManager,
	UiApi,
	UiApiOptions,
} from "../types.js";

let sharedAbortController = new AbortController();

export function abortContextSignal(): void {
	sharedAbortController.abort();
}

export function resetContextSignal(): void {
	sharedAbortController = new AbortController();
}

interface ModelRegistryLike {
	getAll(): unknown[];
	getAvailable(): unknown[];
	find(provider: string, modelId: string): unknown | undefined;
	getError(): string | undefined;
	refresh(): void;
	authStorage: { getApiKey(provider: string): Promise<string | undefined>; hasAuth(provider: string): boolean };
	hasConfiguredAuth(model: { provider: string }): boolean;
	getProviderDisplayName(provider: string): string;
}

function createModelRegistryWrapper(raw: unknown): ModelRegistryLike {
	const data = (raw as Record<string, unknown>) ?? {};
	const models = (Array.isArray(data.models) ? data.models : []) as Record<string, unknown>[];
	const providerConfigs = (Array.isArray(data.providerConfigs) ? data.providerConfigs : []) as Record<string, unknown>[];

	const providerConfigByName = new Map<string, Record<string, unknown>>();
	for (const config of providerConfigs) {
		const provider = config?.provider as string | undefined;
		if (provider) providerConfigByName.set(provider, config);
	}

	return {
		getAll: () => models,
		getAvailable: () => models,
		find: (provider: string, modelId: string) =>
			models.find((m) => m.provider === provider && m.id === modelId),
		getError: () => undefined,
		refresh: () => {},
		authStorage: {
			getApiKey: async () => undefined,
			hasAuth: () => false,
		},
		hasConfiguredAuth: () => false,
		getProviderDisplayName: (provider: string) => provider,
	};
}

export function createExtensionContextModule(
	bridgeProtocol: BridgeProtocol,
	runtimeSessionSnapshot: RuntimeSessionSnapshot,
	getRuntimeHasUI: () => boolean,
	sendRuntimeAction: (extensionId: string, action: string, payload?: Record<string, unknown>) => Promise<unknown>,
	sendRequest: (method: string, params?: Record<string, unknown>) => Promise<unknown>,
	createUiApi: (options: UiApiOptions) => UiApi,
	stateFor: (extensionId: string) => ExtensionState,
	getRuntimeSessionId: () => string | undefined,
): ExtensionContextModule {
	function createExtensionContext(extensionId: string): ExtensionContext {
		const ui = createUiApi({
			extensionId,
			state: stateFor(extensionId),
			sendRequest,
		});
		const sessionEntries = () => runtimeSessionSnapshot.entries ?? [];
		const entryById = (id: string) => sessionEntries().find((entry) => entry?.id === id || entry?.Id === id);
		return {
			extensionId,
			cwd: process.cwd(),
			hasUI: getRuntimeHasUI(),
			ui,
			get modelRegistry() { return createModelRegistryWrapper(runtimeSessionSnapshot.modelRegistry); },
			get model() { return runtimeSessionSnapshot.model; },
			sessionManager: {
				getSessionId: (): string | undefined => getRuntimeSessionId(),
				getBranch: (): unknown[] => runtimeSessionSnapshot.branch ?? [],
				getEntries: (): unknown[] => runtimeSessionSnapshot.entries ?? [],
				getLeafId: (): string | undefined => runtimeSessionSnapshot.leafId,
				getLeafEntry: () => runtimeSessionSnapshot.leafEntry ?? entryById(runtimeSessionSnapshot.leafId ?? ""),
				getEntry: (id: string) => entryById(id),
				getTree: () => runtimeSessionSnapshot.tree ?? { entries: sessionEntries(), childrenByParentId: runtimeSessionSnapshot.childrenByParentId ?? {} },
				getChildren: (parentId = "") => (runtimeSessionSnapshot.childrenByParentId ?? {})[parentId ?? ""] ?? [],
				getLabel: (id: string) => (runtimeSessionSnapshot.labels ?? {})[id] as string | undefined,
				getHeader: () => runtimeSessionSnapshot.header,
				getCwd: (): string => (runtimeSessionSnapshot.cwd as string) ?? process.cwd(),
				getSessionDir: (): string | undefined => runtimeSessionSnapshot.sessionDir,
				getSessionFile: (): string | undefined => runtimeSessionSnapshot.sessionFile,
			getSessionName: (): string | undefined => runtimeSessionSnapshot.sessionName,
			setSessionName: async (name: string) => { await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.setSessionName, { name }); },
			isPersisted: (): boolean => runtimeSessionSnapshot.isPersisted === true,
			} as SessionManager,
			isIdle: async () => ((await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.isIdle)) as Record<string, unknown>)?.value === true,
			signal: sharedAbortController.signal,
			abort: () => sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.abort),
			hasPendingMessages: async () => ((await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.hasPendingMessages)) as Record<string, unknown>)?.value === true,
			shutdown: () => sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.shutdown),
			getContextUsage: () => runtimeSessionSnapshot.contextUsage,
			compact: (options = {}) => sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.compact, options),
			getSystemPrompt: async (): Promise<string> => (await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.getSystemPrompt) as Record<string, unknown>)?.value as string ?? "",
		};
	}

	let snapshotUpdateSerial = 0;
	function updateRuntimeSnapshotFromAction(result: unknown): void {
		const value = (result as Record<string, unknown>)?.value ?? result;
		const session = (value as Record<string, unknown>)?.session ?? (result as Record<string, unknown>)?.session;
		if (session) {
			for (const key of Object.keys(runtimeSessionSnapshot))
				delete runtimeSessionSnapshot[key];
			if (session && typeof session === "object")
				Object.assign(runtimeSessionSnapshot, session);
		}
	}

	function createSessionCommandContext(extensionId: string): SessionCommandContext {
		return {
			...createExtensionContext(extensionId),
			waitForIdle: async () => {
				const result = await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.waitForIdle);
				updateRuntimeSnapshotFromAction(result);
			},
			newSession: async (options = {}) => {
				const raw = await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.newSession);
				updateRuntimeSnapshotFromAction(raw);
				const result = raw as Record<string, unknown>;
				const value = result?.value ?? result ?? {};
				if (!(value as Record<string, unknown>).cancelled && typeof options.withSession === "function") {
					await options.withSession(createReplacementSessionContext(extensionId));
				}
				return { cancelled: (value as Record<string, unknown>).cancelled === true, reason: (value as Record<string, unknown>).reason as string | undefined };
			},
			fork: async (entryId, options = {}) => {
				const raw = await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.forkSession, { entryId, position: options.position });
				updateRuntimeSnapshotFromAction(raw);
				const result = raw as Record<string, unknown>;
				const value = result?.value ?? result ?? {};
				if (!(value as Record<string, unknown>).cancelled && typeof options.withSession === "function") await options.withSession(createReplacementSessionContext(extensionId));
				return { cancelled: (value as Record<string, unknown>).cancelled === true, reason: (value as Record<string, unknown>).reason as string | undefined };
			},
			navigateTree: async (targetId, options = {}) => {
				const result = await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.navigateTree, { targetId, options });
				updateRuntimeSnapshotFromAction(result);
			},
			switchSession: async (sessionPath, options = {}) => {
				const raw = await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.switchSession, { sessionPath, options });
				updateRuntimeSnapshotFromAction(raw);
				const result = raw as Record<string, unknown>;
				const value = result?.value ?? result ?? {};
				if (!(value as Record<string, unknown>).cancelled && typeof options.withSession === "function") await options.withSession(createReplacementSessionContext(extensionId));
				return { cancelled: (value as Record<string, unknown>).cancelled === true, reason: (value as Record<string, unknown>).reason as string | undefined };
			},
			reload: async () => {
				await sendRuntimeAction(extensionId, bridgeProtocol.runtimeActions.reloadExtensions);
			},
		};
	}

	function createReplacementSessionContext(extensionId: string): ReplacementSessionContext {
		const sendMessageAndRefreshSnapshot = async (action: string, payload: Record<string, unknown>) => {
			const result = await sendRuntimeAction(extensionId, action, payload);
			updateRuntimeSnapshotFromAction(result);
			return result;
		};
		return {
			...createSessionCommandContext(extensionId),
			sendMessage: (message, options = {}) =>
				sendMessageAndRefreshSnapshot(bridgeProtocol.runtimeActions.sendMessage, { message, options }),
			sendUserMessage: (content, options = {}) =>
				sendMessageAndRefreshSnapshot(bridgeProtocol.runtimeActions.sendUserMessage, { content, options }),
		};
	}

	function createCommandContext(extensionId: string): SessionCommandContext {
		return createSessionCommandContext(extensionId);
	}

	return {
		createExtensionContext,
		createSessionCommandContext,
		createReplacementSessionContext,
		createCommandContext,
		updateRuntimeSnapshotFromAction,
	};
}
