import type { RuntimeStateOptions, RuntimeStateApi, ExtensionState, ExtensionServiceWaiter, ExtensionServiceEntry, EventHandler } from "../types.js";

function normalizeServiceKey(key: string): string {
	if (typeof key !== "string" || !key.trim())
		throw new Error("Extension service key must be a non-empty string");
	return key.trim();
}

export function createRuntimeState(options: RuntimeStateOptions): RuntimeStateApi {
	const extensionStates = new Map<string, ExtensionState>();
	const extensionServices = new Map<string, ExtensionServiceEntry>();
	const extensionServiceWaiters = new Map<string, ExtensionServiceWaiter[]>();
	const createDescriptor = options?.createDescriptor;
	const onDescriptorServiceUse = options?.onDescriptorServiceUse;
	if (typeof createDescriptor !== "function")
		throw new Error("createDescriptor callback is required");

	function stateFor(extensionId: string): ExtensionState {
		if (!extensionStates.has(extensionId)) {
			extensionStates.set(extensionId, {
				commands: new Map(),
				tools: new Map(),
				providers: new Map(),
				events: new Map(),
				extensionStatuses: new Map(),
				widgetComponents: new Map(),
				footerComponent: undefined,
				headerComponent: undefined,
				editorComponent: undefined,
				renderers: new Map(),
				decorators: new Map(),
				descriptor: createDescriptor(extensionId),
			});
		}
		return extensionStates.get(extensionId)!;
	}

	function addEventHandler(state: ExtensionState, eventName: string, handler: EventHandler): { dispose: () => void } {
		const handlers = state.events.get(eventName) ?? [];
		handlers.push(handler);
		state.events.set(eventName, handlers);
		return {
			dispose: () => {
				const current = state.events.get(eventName) ?? [];
				const next = current.filter((item) => item !== handler);
				if (next.length === 0) state.events.delete(eventName);
				else state.events.set(eventName, next);
			},
		};
	}

	function provideExtensionService(extensionId: string, state: ExtensionState, key: string, api: unknown): { dispose: () => void } {
		const serviceKey = normalizeServiceKey(key);
		if (api === undefined || api === null)
			throw new Error(`Extension service '${serviceKey}' requires an API object`);
		onDescriptorServiceUse?.(state, "providesServices", serviceKey);
		extensionServices.set(serviceKey, { extensionId, api });
		const waiters = extensionServiceWaiters.get(serviceKey) ?? [];
		extensionServiceWaiters.delete(serviceKey);
		for (const waiter of waiters) waiter.resolve(api);
		return {
			dispose: () => {
				const current = extensionServices.get(serviceKey);
				if (current?.extensionId === extensionId)
					extensionServices.delete(serviceKey);
			},
		};
	}

	function getExtensionService(state: ExtensionState, key: string): unknown {
		const serviceKey = normalizeServiceKey(key);
		onDescriptorServiceUse?.(state, "consumesServices", serviceKey);
		return extensionServices.get(serviceKey)?.api;
	}

	function waitForExtensionService(state: ExtensionState, key: string, options: { timeoutMs?: number } = {}): Promise<unknown> {
		const serviceKey = normalizeServiceKey(key);
		onDescriptorServiceUse?.(state, "consumesServices", serviceKey);
		const existing = extensionServices.get(serviceKey)?.api;
		if (existing) return Promise.resolve(existing);
		return new Promise<unknown>((resolve, reject) => {
			let timeout: NodeJS.Timeout | undefined;
			const waiters = extensionServiceWaiters.get(serviceKey) ?? [];
			const waiter: ExtensionServiceWaiter = {
				resolve: (api: unknown) => {
					if (timeout) clearTimeout(timeout);
					resolve(api);
				},
				reject: (error: unknown) => {
					if (timeout) clearTimeout(timeout);
					reject(error);
				},
			};
			waiters.push(waiter);
			extensionServiceWaiters.set(serviceKey, waiters);
			const timeoutMs = Number(options?.timeoutMs);
			if (Number.isFinite(timeoutMs) && timeoutMs > 0) {
				timeout = setTimeout(() => {
					const current = extensionServiceWaiters.get(serviceKey) ?? [];
					const remaining = current.filter((item) => item !== waiter);
					if (remaining.length === 0) extensionServiceWaiters.delete(serviceKey);
					else extensionServiceWaiters.set(serviceKey, remaining);
					reject(
						new Error(`Timed out waiting for extension service '${serviceKey}'`),
					);
				}, timeoutMs);
			}
		});
	}

	function resetRuntimeState(): void {
		extensionStates.clear();
		for (const waiters of extensionServiceWaiters.values()) {
			for (const waiter of waiters) {
				waiter.reject(
					new Error("Extension runtime reset before service became available"),
				);
			}
		}
		extensionServiceWaiters.clear();
		extensionServices.clear();
	}

	return {
		extensionStates,
		stateFor,
		addEventHandler,
		provideExtensionService,
		getExtensionService,
		waitForExtensionService,
		resetRuntimeState,
	};
}
