import type { TransportApi, TransportOptions, PendingEntry, QueuedRegistrationRequest, SendRequestOptions } from "../types.js";

export function createJsonRpcTransport(options: TransportOptions): TransportApi {
	const pending = new Map<string, PendingEntry>();
	const pendingRegistrationRequests = new Set<Promise<unknown>>();
	const queuedRegistrationRequests: QueuedRegistrationRequest[] = [];
	let nextId = 0;
	const requestTimeoutMs = Number(options?.requestTimeoutMs ?? 60_000);
	const write = options?.write;
	if (typeof write !== "function") throw new Error("Transport write callback is required");

	function sendRequest(method: string, params?: Record<string, unknown>, options?: SendRequestOptions): Promise<unknown> {
		const id = String(++nextId);
		const promise = new Promise<unknown>((resolve, reject) => {
			const effectiveTimeoutMs = options?.timeoutMs === undefined ? requestTimeoutMs : options.timeoutMs;
			const timeout = effectiveTimeoutMs !== null && Number.isFinite(effectiveTimeoutMs) && effectiveTimeoutMs > 0
				? setTimeout(() => {
					pending.delete(id);
					reject(
						new Error(`Request '${method}' timed out after ${effectiveTimeoutMs}ms`),
					);
				}, effectiveTimeoutMs)
				: undefined;
			pending.set(id, { resolve, reject, timeout });
		});
		write({ jsonrpc: "2.0", id, method, params });
		return promise;
	}

	function trackRegistrationRequest(method: string, params?: Record<string, unknown>): Promise<unknown> {
		const promise = sendRequest(method, params).finally(() =>
			pendingRegistrationRequests.delete(promise),
		);
		pendingRegistrationRequests.add(promise);
		return promise;
	}

	function queueRegistrationRequest(method: string, params?: Record<string, unknown>): void {
		queuedRegistrationRequests.push({ method, params });
	}

	function drainQueuedRegistrationRequests(): QueuedRegistrationRequest[] {
		return queuedRegistrationRequests.splice(0, queuedRegistrationRequests.length);
	}

	async function flushRegistrationRequests(): Promise<void> {
		while (pendingRegistrationRequests.size > 0)
			await Promise.all(pendingRegistrationRequests);
	}

	function handleResponse(message: { id?: string | number | null; error?: { message?: string }; result?: unknown }): boolean {
		const id = message.id == null ? "" : String(message.id);
		const entry = pending.get(id);
		if (!entry) return false;
		pending.delete(id);
		if (entry.timeout) clearTimeout(entry.timeout);
		if (message.error)
			entry.reject(
				new Error(message.error.message ?? JSON.stringify(message.error)),
		);
		else entry.resolve(message.result);
		return true;
	}

	function rejectAllPending(errorMessage: string): void {
		for (const entry of pending.values()) {
			if (entry.timeout) clearTimeout(entry.timeout);
			entry.reject(new Error(errorMessage));
		}
		pending.clear();
	}

	return {
		sendRequest,
		trackRegistrationRequest,
		queueRegistrationRequest,
		drainQueuedRegistrationRequests,
		flushRegistrationRequests,
		handleResponse,
		rejectAllPending,
	};
}
