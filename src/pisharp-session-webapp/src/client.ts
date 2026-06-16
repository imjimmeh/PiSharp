import type {
	AcceptedResult,
	CreateSessionRequest,
	ListSessionsRequest,
	PromptRequest,
	ServerEventEnvelope,
	ServerMessagesResult,
	ServerResponse,
	ServerSessionCreated,
	ServerSessionListResult,
	ServerSessionState,
	SocketMessage,
	TextCommandRequest,
	ThinkingLevel,
} from "./types";

export interface PiServerClientOptions {
	baseUrl: string;
	apiKey: string;
}

export interface CommandPayload {
	type: string;
	id?: string;
	[key: string]: unknown;
}

type PendingRequest = {
	command: string;
	resolve: (value: unknown) => void;
	reject: (error: Error) => void;
};

type EventListener = (envelope: ServerEventEnvelope) => void;
type ResponseListener = (response: ServerResponse) => void;

export class PiServerClient {
	private socket?: WebSocket;
	private commandCounter = 0;
	private readonly pending = new Map<string, PendingRequest>();
	private readonly eventListeners = new Set<EventListener>();
	private readonly responseListeners = new Set<ResponseListener>();

	constructor(private readonly options: PiServerClientOptions) {}

	get isConnected(): boolean {
		return this.socket?.readyState === WebSocket.OPEN;
	}

	connect(): Promise<void> {
		if (this.isConnected) return Promise.resolve();

		return new Promise((resolve, reject) => {
			const socket = new WebSocket(this.createWebSocketUrl());
			this.socket = socket;

			socket.addEventListener("open", () => resolve(), { once: true });
			socket.addEventListener("error", () => reject(new Error("WebSocket connection failed")), { once: true });
			socket.addEventListener("message", (event) => this.handleMessage(event.data));
			socket.addEventListener("close", () => this.rejectPending(new Error("WebSocket connection closed")));
		});
	}

	disconnect(): void {
		this.socket?.close();
		this.socket = undefined;
	}

	onEvent(listener: EventListener): () => void {
		this.eventListeners.add(listener);
		return () => this.eventListeners.delete(listener);
	}

	onResponse(listener: ResponseListener): () => void {
		this.responseListeners.add(listener);
		return () => this.responseListeners.delete(listener);
	}

	listSessions(request: ListSessionsRequest): Promise<ServerSessionListResult> {
		return this.sendCommand<ServerSessionListResult>({ type: "list_sessions", ...request });
	}

	createSession(request: CreateSessionRequest): Promise<ServerSessionCreated> {
		return this.sendCommand<ServerSessionCreated>({ type: "create_session", ...request });
	}

	disposeSession(serverSessionId: string): Promise<{ disposed: boolean }> {
		return this.sendCommand<{ disposed: boolean }>({ type: "dispose_session", serverSessionId });
	}

	prompt(request: PromptRequest): Promise<AcceptedResult> {
		return this.sendCommand<AcceptedResult>({ type: "prompt", ...request });
	}

	steer(request: TextCommandRequest): Promise<void> {
		return this.sendCommand<void>({ type: "steer", ...request });
	}

	followUp(request: TextCommandRequest): Promise<void> {
		return this.sendCommand<void>({ type: "follow_up", ...request });
	}

	queueNextTurn(request: TextCommandRequest): Promise<void> {
		return this.sendCommand<void>({ type: "queue_next_turn", ...request });
	}

	abort(serverSessionId: string): Promise<void> {
		return this.sendCommand<void>({ type: "abort", serverSessionId });
	}

	getState(serverSessionId: string): Promise<ServerSessionState> {
		return this.sendCommand<ServerSessionState>({ type: "get_state", serverSessionId });
	}

	getMessages(serverSessionId: string): Promise<ServerMessagesResult> {
		return this.sendCommand<ServerMessagesResult>({ type: "get_messages", serverSessionId });
	}

	setModel(serverSessionId: string, provider: string, modelId: string): Promise<unknown> {
		return this.sendCommand<unknown>({ type: "set_model", serverSessionId, provider, modelId });
	}

	setThinkingLevel(serverSessionId: string, level: ThinkingLevel): Promise<void> {
		return this.sendCommand<void>({ type: "set_thinking_level", serverSessionId, level });
	}

	compact(serverSessionId: string, customInstructions?: string): Promise<AcceptedResult> {
		return this.sendCommand<AcceptedResult>({ type: "compact", serverSessionId, customInstructions });
	}

	newSession(serverSessionId: string): Promise<ServerSessionState> {
		return this.sendCommand<ServerSessionState>({ type: "new_session", serverSessionId });
	}

	switchSession(serverSessionId: string, sessionIdOrPath: string): Promise<ServerSessionState> {
		return this.sendCommand<ServerSessionState>({ type: "switch_session", serverSessionId, sessionIdOrPath });
	}

	fork(serverSessionId: string, entryId?: string, newSessionId?: string): Promise<ServerSessionState> {
		return this.sendCommand<ServerSessionState>({ type: "fork", serverSessionId, entryId, newSessionId });
	}

	setSessionName(serverSessionId: string, name: string): Promise<ServerSessionState> {
		return this.sendCommand<ServerSessionState>({ type: "set_session_name", serverSessionId, name });
	}

	sendCommand<TData>(payload: CommandPayload): Promise<TData> {
		if (!this.isConnected || !this.socket) throw new Error("WebSocket is not connected");

		const id = this.nextCommandId();
		const command = { ...payload, id };
		const promise = new Promise<TData>((resolve, reject) => {
			this.pending.set(id, {
				command: command.type,
				resolve: (value) => resolve(value as TData),
				reject,
			});
		});

		try {
			this.socket.send(JSON.stringify(command));
		} catch (error) {
			this.pending.delete(id);
			return Promise.reject(error instanceof Error ? error : new Error(String(error)));
		}

		return promise;
	}

	private createWebSocketUrl(): string {
		const url = new URL(this.options.baseUrl);
		url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
		url.pathname = "/ws";
		url.search = "";
		url.searchParams.set("access_token", this.options.apiKey);
		return url.toString();
	}

	private handleMessage(raw: unknown): void {
		const text = typeof raw === "string" ? raw : "";
		const message = JSON.parse(text) as SocketMessage;

		if (message.type === "event") {
			this.emitEvent(message);
			return;
		}

		this.emitResponse(message);
		if (!message.id) return;

		const pending = this.pending.get(message.id);
		if (!pending) return;

		this.pending.delete(message.id);
		if (message.success) pending.resolve(message.data);
		else pending.reject(new Error(message.error?.message ?? `${pending.command} failed`));
	}

	private emitEvent(envelope: ServerEventEnvelope): void {
		for (const listener of this.eventListeners) listener(envelope);
	}

	private emitResponse(response: ServerResponse): void {
		for (const listener of this.responseListeners) listener(response);
	}

	private rejectPending(error: Error): void {
		for (const pending of this.pending.values()) pending.reject(error);
		this.pending.clear();
	}

	private nextCommandId(): string {
		this.commandCounter += 1;
		return `cmd_${this.commandCounter}`;
	}
}
