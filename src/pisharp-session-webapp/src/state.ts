import { PiServerClient } from "./client";
import type {
	AgentMessage,
	AgentSessionEvent,
	CreateSessionRequest,
	ServerEventEnvelope,
	ServerPersistedSession,
	ServerResponse,
	ServerSessionState,
	ThinkingLevel,
} from "./types";

export interface ConnectionSettings {
	baseUrl: string;
	apiKey: string;
	cwd: string;
	sessionsRoot?: string;
	allCwds: boolean;
}

export interface TranscriptItem {
	id: string;
	role: string;
	text: string;
	isStreaming: boolean;
	toolName?: string;
	toolCallId?: string;
	isError?: boolean;
	details?: unknown;
}

export interface QueueState {
	steering: AgentMessage[];
	followUp: AgentMessage[];
	nextTurn: AgentMessage[];
}

export interface AppState {
	settings: ConnectionSettings;
	connectionStatus: "disconnected" | "connecting" | "connected";
	sessions: ServerPersistedSession[];
	activeServerSessionId?: string;
	activeRuntimeSessionId?: string;
	activeSession?: ServerSessionState;
	transcript: TranscriptItem[];
	streamingMessage?: TranscriptItem;
	pendingToolCalls: string[];
	queue: QueueState;
	errorMessage?: string;
	lastResponse?: ServerResponse;
}

type StoreListener = (state: AppState) => void;

export function createInitialState(settings: ConnectionSettings): AppState {
	return {
		settings,
		connectionStatus: "disconnected",
		sessions: [],
		transcript: [],
		pendingToolCalls: [],
		queue: { steering: [], followUp: [], nextTurn: [] },
	};
}

export class PiSessionStore {
	private client?: PiServerClient;
	private stateValue: AppState;
	private readonly listeners = new Set<StoreListener>();

	constructor(settings: ConnectionSettings) {
		this.stateValue = createInitialState(settings);
	}

	get state(): AppState {
		return this.stateValue;
	}

	subscribe(listener: StoreListener): () => void {
		this.listeners.add(listener);
		listener(this.stateValue);
		return () => this.listeners.delete(listener);
	}

	updateSettings(settings: ConnectionSettings): void {
		this.setState({ ...this.stateValue, settings });
	}

	async connect(): Promise<void> {
		this.setState({ ...this.stateValue, connectionStatus: "connecting", errorMessage: undefined });
		const client = new PiServerClient({ baseUrl: this.stateValue.settings.baseUrl, apiKey: this.stateValue.settings.apiKey });
		client.onEvent((event) => this.applyEvent(event));
		client.onResponse((response) => this.applyResponse(response));

		try {
			await client.connect();
		} catch (error) {
			this.setState({ ...this.stateValue, connectionStatus: "disconnected" });
			throw error;
		}

		this.client = client;
		this.setState({ ...this.stateValue, connectionStatus: "connected" });
	}

	disconnect(): void {
		this.client?.disconnect();
		this.client = undefined;
		this.setState({ ...this.stateValue, connectionStatus: "disconnected" });
	}

	async refreshSessions(): Promise<void> {
		const result = await this.requireClient().listSessions({
			cwd: this.stateValue.settings.cwd,
			sessionsRoot: this.stateValue.settings.sessionsRoot,
			allCwds: this.stateValue.settings.allCwds,
			serverSessionId: this.stateValue.activeServerSessionId,
		});
		this.setState({ ...this.stateValue, sessions: result.sessions });
	}

	async createSession(overrides: Partial<CreateSessionRequest> = {}): Promise<void> {
		const created = await this.requireClient().createSession({
			cwd: this.stateValue.settings.cwd,
			sessionsRoot: this.stateValue.settings.sessionsRoot,
			...overrides,
		});
		await this.setActiveSession(created.serverSessionId, created.state);
		await this.refreshSessions();
	}

	async openSession(session: ServerPersistedSession): Promise<void> {
		if (session.isLive && session.serverSessionId) {
			const state = await this.requireClient().getState(session.serverSessionId);
			await this.setActiveSession(session.serverSessionId, state);
			return;
		}

		await this.createSession({ sessionIdOrPath: session.id });
	}

	async refreshActiveSession(): Promise<void> {
		const serverSessionId = this.requireActiveServerSessionId();
		const state = await this.requireClient().getState(serverSessionId);
		await this.setActiveSession(serverSessionId, state);
	}

	async prompt(message: string): Promise<void> {
		await this.requireClient().prompt({ serverSessionId: this.requireActiveServerSessionId(), message });
	}

	async steer(message: string, triggerIfIdle = false): Promise<void> {
		await this.requireClient().steer({ serverSessionId: this.requireActiveServerSessionId(), message, triggerIfIdle });
	}

	async followUp(message: string, triggerIfIdle = false): Promise<void> {
		await this.requireClient().followUp({ serverSessionId: this.requireActiveServerSessionId(), message, triggerIfIdle });
	}

	async queueNextTurn(message: string, triggerIfIdle = false): Promise<void> {
		await this.requireClient().queueNextTurn({ serverSessionId: this.requireActiveServerSessionId(), message, triggerIfIdle });
	}

	async abort(): Promise<void> {
		await this.requireClient().abort(this.requireActiveServerSessionId());
	}

	async renameActiveSession(name: string): Promise<void> {
		const state = await this.requireClient().setSessionName(this.requireActiveServerSessionId(), name);
		this.setState({ ...this.stateValue, activeSession: state });
		await this.refreshSessions();
	}

	async forkActiveSession(newSessionId?: string): Promise<void> {
		const state = await this.requireClient().fork(this.requireActiveServerSessionId(), undefined, newSessionId);
		await this.setActiveSession(this.requireActiveServerSessionId(), state);
		await this.refreshSessions();
	}

	private async setActiveSession(serverSessionId: string, state: ServerSessionState): Promise<void> {
		const messages = await this.requireClient().getMessages(serverSessionId);
		this.setState({
			...this.stateValue,
			activeServerSessionId: serverSessionId,
			activeRuntimeSessionId: state.runtimeSessionId,
			activeSession: state,
			transcript: messages.messages.map((message) => messageToTranscriptItem(message)),
			streamingMessage: undefined,
			pendingToolCalls: [],
			queue: { steering: [], followUp: [], nextTurn: [] },
			errorMessage: undefined,
		});
	}

	private applyEvent(envelope: ServerEventEnvelope): void {
		if (this.stateValue.activeServerSessionId && envelope.serverSessionId !== this.stateValue.activeServerSessionId) return;
		this.setState(reduceServerEvent(this.stateValue, envelope));
	}

	private applyResponse(response: ServerResponse): void {
		this.setState({
			...this.stateValue,
			lastResponse: response,
			errorMessage: response.success ? this.stateValue.errorMessage : response.error?.message ?? `${response.command} failed`,
		});
	}

	private requireClient(): PiServerClient {
		if (!this.client) throw new Error("Not connected");
		return this.client;
	}

	private requireActiveServerSessionId(): string {
		if (!this.stateValue.activeServerSessionId) throw new Error("No active session");
		return this.stateValue.activeServerSessionId;
	}

	private setState(state: AppState): void {
		this.stateValue = state;
		for (const listener of this.listeners) listener(this.stateValue);
	}
}

export function reduceServerEvent(state: AppState, envelope: ServerEventEnvelope): AppState {
	const event = envelope.event;

	switch (event.type) {
		case "agent_start":
		case "turn_start":
			return updateSessionFlags(state, true, false);
		case "agent_end":
		case "turn_end":
			return { ...updateSessionFlags(state, false, false), streamingMessage: undefined, pendingToolCalls: [] };
		case "message_start":
		case "message_update":
			return event.message ? { ...state, streamingMessage: messageToTranscriptItem(event.message, true) } : state;
		case "message_end":
			return event.message ? appendCompletedMessage(state, event.message) : state;
		case "tool_execution_start":
			return upsertTool(state, event, true);
		case "tool_execution_update":
			return upsertTool(state, event, true);
		case "tool_execution_end":
			return upsertTool(state, event, false);
		case "compaction_start":
			return updateSessionFlags(state, true, true);
		case "compaction_end":
			return updateSessionFlags(state, false, false);
		case "model_select":
			return event.model && state.activeSession
				? { ...state, activeSession: { ...state.activeSession, model: event.model } }
				: state;
		case "thinking_level_select":
		case "thinking_level_changed":
			return event.level && state.activeSession
				? { ...state, activeSession: { ...state.activeSession, thinkingLevel: normalizeThinkingLevel(event.level) } }
				: state;
		case "queue_update":
			return { ...state, queue: queueFromEvent(event) };
		default:
			return state;
	}
}

function updateSessionFlags(state: AppState, isBusy: boolean, isCompacting: boolean): AppState {
	return state.activeSession
		? { ...state, activeSession: { ...state.activeSession, isBusy, isCompacting } }
		: state;
}

function appendCompletedMessage(state: AppState, message: AgentMessage): AppState {
	return {
		...state,
		transcript: [...state.transcript, messageToTranscriptItem(message, false)],
		streamingMessage: undefined,
	};
}

function upsertTool(state: AppState, event: AgentSessionEvent, isStreaming: boolean): AppState {
	const toolCallId = event.toolCallId ?? "tool";
	const item: TranscriptItem = {
		id: `tool:${toolCallId}`,
		role: "tool",
		text: toolText(event),
		isStreaming,
		toolName: event.toolName,
		toolCallId,
		isError: event.isError,
		details: event.result ?? event.partialResult ?? event.arguments ?? event.args,
	};
	const transcript = state.transcript.filter((existing) => existing.id !== item.id);
	const pendingToolCalls = isStreaming
		? Array.from(new Set([...state.pendingToolCalls, toolCallId]))
		: state.pendingToolCalls.filter((id) => id !== toolCallId);

	return { ...state, transcript: [...transcript, item], pendingToolCalls };
}

function queueFromEvent(event: AgentSessionEvent): QueueState {
	const steering = event.steer ?? event.steering;
	return {
		steering: Array.isArray(steering) ? (steering as AgentMessage[]) : [],
		followUp: Array.isArray(event.followUp) ? (event.followUp as AgentMessage[]) : [],
		nextTurn: Array.isArray(event.nextTurn) ? (event.nextTurn as AgentMessage[]) : [],
	};
}

function normalizeThinkingLevel(level: ThinkingLevel): ThinkingLevel {
	return level === "xhigh" ? "xHigh" : level;
}

function messageToTranscriptItem(message: AgentMessage, isStreaming = false): TranscriptItem {
	return {
		id: message.id ? String(message.id) : `${message.role}:${Date.now()}:${Math.random().toString(36).slice(2)}`,
		role: message.role,
		text: messageText(message),
		isStreaming,
		isError: Boolean(message.errorMessage),
		details: message,
	};
}

function messageText(message: AgentMessage): string {
	if (typeof message.content === "string") return message.content;
	if (Array.isArray(message.content)) return message.content.map(contentText).join("");
	return message.errorMessage ?? "";
}

function contentText(content: { type?: string; text?: string }): string {
	if (content.type === "text") return content.text ?? "";
	if (content.type === "thinking") return content.text ?? "[thinking]";
	if (content.type === "image") return "[image]";
	return "";
}

function toolText(event: AgentSessionEvent): string {
	const value = event.result ?? event.partialResult ?? event.arguments ?? event.args;
	if (typeof value === "string") return value;
	if (value === undefined) return event.toolName ?? "tool";
	return JSON.stringify(value);
}
