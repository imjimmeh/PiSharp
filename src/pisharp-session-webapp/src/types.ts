export type ThinkingLevel = "off" | "minimal" | "low" | "medium" | "high" | "xHigh" | "xhigh";

export interface ModelDescriptor {
	provider: string;
	id: string;
	name?: string;
	contextWindow?: number;
	[key: string]: unknown;
}

export interface MessageContent {
	type?: string;
	text?: string;
	[key: string]: unknown;
}

export interface AgentMessage {
	role: string;
	content?: string | MessageContent[];
	errorMessage?: string;
	usage?: unknown;
	[key: string]: unknown;
}

export interface ServerError {
	code: string;
	message: string;
	details?: unknown;
}

export interface ServerResponse<TData = unknown> {
	type: "response";
	id?: string;
	command: string;
	success: boolean;
	data?: TData;
	error?: ServerError;
}

export interface ServerEventEnvelope {
	type: "event";
	serverSessionId: string;
	sequence: number;
	timestamp: string;
	event: AgentSessionEvent;
}

export type SocketMessage = ServerResponse | ServerEventEnvelope;

export interface AgentSessionEvent {
	type: string;
	message?: AgentMessage;
	assistantMessageEvent?: unknown;
	toolCallId?: string;
	toolName?: string;
	arguments?: unknown;
	args?: unknown;
	partialResult?: unknown;
	result?: unknown;
	isError?: boolean;
	model?: ModelDescriptor;
	level?: ThinkingLevel;
	messages?: AgentMessage[];
	[key: string]: unknown;
}

export interface ServerSessionState {
	serverSessionId: string;
	runtimeSessionId: string;
	runtimeSessionPath?: string;
	sessionName?: string;
	cwd: string;
	model: ModelDescriptor;
	thinkingLevel: ThinkingLevel;
	isBusy: boolean;
	isCompacting: boolean;
	messageCount: number;
}

export interface ServerSessionCreated {
	serverSessionId: string;
	state: ServerSessionState;
}

export interface ServerMessagesResult {
	serverSessionId: string;
	messages: AgentMessage[];
}

export interface ServerPersistedSession {
	id: string;
	createdAt: string;
	cwd: string;
	path: string;
	parentSessionPath?: string;
	isLive: boolean;
	serverSessionId?: string;
}

export interface ServerSessionListResult {
	sessions: ServerPersistedSession[];
}

export interface CreateSessionRequest {
	cwd: string;
	sessionId?: string;
	sessionIdOrPath?: string;
	continueLatestForCwd?: boolean;
	sessionsRoot?: string;
	provider?: string;
	model?: string;
	thinking?: ThinkingLevel;
	scopedModels?: string[];
	tools?: string[];
	noTools?: boolean;
	noBuiltinTools?: boolean;
	extensions?: string[];
	noExtensions?: boolean;
	noSkills?: boolean;
	noPromptTemplates?: boolean;
	noThemes?: boolean;
	noContextFiles?: boolean;
}

export interface ListSessionsRequest {
	serverSessionId?: string;
	cwd?: string;
	sessionsRoot?: string;
	allCwds?: boolean;
}

export interface TextCommandRequest {
	serverSessionId: string;
	message: string;
	triggerIfIdle?: boolean;
}

export interface PromptRequest {
	serverSessionId: string;
	message: string;
	images?: MessageContent[];
}

export interface AcceptedResult {
	accepted: boolean;
}
