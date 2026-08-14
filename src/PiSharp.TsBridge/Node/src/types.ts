export interface JsonRpcRequest {
	jsonrpc: string;
	id?: string | number | null;
	method: string;
	params?: Record<string, unknown>;
}

export interface JsonRpcErrorDetail {
	code: number;
	message: string;
}

export interface WriteFn {
	(message: object): void;
}

export interface TransportOptions {
	write: WriteFn;
	requestTimeoutMs?: number;
}

export interface SendRequestOptions {
	timeoutMs?: number | null;
}

export interface PendingEntry {
	resolve: (value: unknown) => void;
	reject: (reason: unknown) => void;
	timeout?: NodeJS.Timeout;
}

export interface QueuedRegistrationRequest {
	method: string;
	params?: Record<string, unknown>;
}

export interface TransportApi {
	sendRequest: (method: string, params?: Record<string, unknown>, options?: SendRequestOptions) => Promise<unknown>;
	trackRegistrationRequest: (method: string, params?: Record<string, unknown>) => Promise<unknown>;
	queueRegistrationRequest: (method: string, params?: Record<string, unknown>) => void;
	drainQueuedRegistrationRequests: () => QueuedRegistrationRequest[];
	flushRegistrationRequests: () => Promise<void>;
	handleResponse: (message: JsonRpcRequest) => boolean;
	rejectAllPending: (errorMessage: string) => void;
}

export interface ExtensionDescriptor {
	schemaVersion: number;
	extensionPath: string;
	sourceHash: string | null;
	dependencyHashes: DependencyHash[];
	packageName: string | null;
	packageVersion: string | null;
	tools: ToolDescriptor[];
	commands: CommandDescriptor[];
	shortcuts: ShortcutDescriptor[];
	flags: FlagDescriptor[];
	promptSections: PromptSectionDescriptor[];
	promptTransforms: PromptTransformDescriptor[];
	providers: ProviderDescriptor[];
	skills: SkillDescriptor[];
	providesServices: string[];
	consumesServices: string[];
	activation: string;
}

export interface DependencyHash {
	path: string;
	hash: string;
}

export interface ToolDescriptor {
	name: string;
	label?: string;
	description?: string;
	parameters?: unknown;
	parametersSchema?: unknown;
	executionMode?: string;
	promptSnippet?: string;
	promptGuidelines?: string;
	renderShell?: boolean;
	rendererName?: string;
	hasRenderCall: boolean;
	hasRenderResult: boolean;
	extensionId?: string;
	api?: string;
}

export interface CommandDescriptor {
	extensionId: string;
	name: string;
	description?: string;
}

export interface ShortcutDescriptor {
	extensionId: string;
	keys: string;
	description?: string;
}

export interface FlagDescriptor {
	extensionId: string;
	name: string;
	description?: string;
	type?: string;
	defaultValue?: unknown;
	default?: unknown;
}

export interface PromptSectionDescriptor {
	extensionId: string;
	id: string;
	slot?: string;
	priority?: number;
	content?: string;
	title?: string;
	protected?: boolean;
	override?: string;
}

export interface PromptTransformDescriptor {
	extensionId: string;
	name: string;
	mode?: string;
	appendMarkdown?: string;
	content?: string;
	removeSectionIds?: string[];
}

export interface ProviderDescriptor {
	extensionId: string;
	name: string;
	api?: string;
}

export interface SkillDescriptor {
	extensionId: string;
	name: string;
	description: string;
	content?: string;
	filePath?: string;
	path?: string;
	disableModelInvocation?: boolean;
	override?: string;
}

export type EventHandler = (payload: unknown, context: ExtensionContext) => unknown | Promise<unknown>;

export type CommandHandler = (args: string, context: CommandContext) => unknown | Promise<unknown>;

export type ToolExecuteFn = (
	toolCallId: string,
	args: Record<string, unknown>,
	signal: AbortSignal,
	onProgress: () => void,
	context: ExtensionContext,
) => Promise<ToolResult>;

export interface CustomMessageData {
	role: "custom";
	customType: string;
	content: string | unknown[];
	display: boolean;
	details?: unknown;
	timestamp: number;
}

export interface MessageRendererOptions {
	expanded: boolean;
}

export type MessageRendererHandler = (
	msg: CustomMessageData,
	options: MessageRendererOptions,
	theme: unknown,
) => unknown | Promise<unknown>;

export type MessageDecoratorHandler = (msg: MessageData, rows: MessageRow[]) => unknown | Promise<unknown>;

export interface MessageData {
	data: Record<string, unknown>;
	text: string;
	role: string;
	customType: string;
}

export interface MessageRow {
	text: string;
	kind: string;
	spans?: unknown;
}

export interface ToolResult {
	content: ToolContent[];
	isError?: boolean;
}

export interface ToolContent {
	type: string;
	text: string;
}

export interface ToolRegistration {
	name: string;
	execute?: ToolExecuteFn;
	label?: string;
	description?: string;
	parameters?: unknown;
	parametersSchema?: unknown;
	executionMode?: string;
	promptSnippet?: string;
	promptGuidelines?: string;
	renderShell?: boolean;
	rendererName?: string;
	renderCall?: (args: unknown, theme: BridgeTheme, context: ToolRenderContext) => RenderableComponent;
	renderResult?: (result: unknown, options: ToolRenderOptions, theme: BridgeTheme, context: ToolRenderContext) => RenderableComponent;
}

export interface ToolRenderOptions {
	expanded: boolean;
	isPartial: boolean;
}

export interface ExtensionState {
	commands: Map<string, CommandHandler>;
	tools: Map<string, ToolRegistration>;
	providers: Map<string, unknown>;
	events: Map<string, EventHandler[]>;
	extensionStatuses: Map<string, string>;
	widgetComponents: Map<string, unknown>;
	footerComponent: unknown;
	headerComponent: unknown;
	editorComponent: unknown;
	renderers: Map<string, MessageRendererHandler>;
	decorators: Map<string, MessageDecoratorHandler>;
	descriptor: ExtensionDescriptor;
	autocompleteProviders?: unknown[];
}

export interface CustomUiTerminal {
	columns: number;
	rows: number;
}

export interface CustomUiTui {
	terminal: CustomUiTerminal;
	requestRender: () => void;
}

export type CustomUiFactory = (
	tui: CustomUiTui,
	theme: BridgeTheme,
	keybindings: Record<string, unknown>,
	done: (value: unknown) => void,
) => RenderableComponent | Promise<RenderableComponent>;

export interface CustomUiSession {
	requestId: string;
	component: RenderableComponent;
	terminal: CustomUiTerminal;
	width: number;
	height: number;
	overlay: boolean;
	overlayOptions?: unknown;
	completed: boolean;
	value: unknown;
	error?: string;
	dirty: boolean;
	updateInFlight: boolean;
	updatePending: boolean;
}

export interface CustomUiSnapshot {
	requestId: string;
	lines: string[];
	width: number;
	height: number;
	completed: boolean;
	value: unknown;
	error?: string;
}

export interface CustomUiInputRequest {
	requestId: string;
	data?: string;
	width?: number;
	height?: number;
	event?: string;
}

export interface RuntimeStateOptions {
	createDescriptor: (extensionId: string) => ExtensionDescriptor;
	onDescriptorServiceUse?: (state: ExtensionState, field: "providesServices" | "consumesServices", key: string) => void;
}

export interface RuntimeStateApi {
	extensionStates: Map<string, ExtensionState>;
	stateFor: (extensionId: string) => ExtensionState;
	addEventHandler: (state: ExtensionState, eventName: string, handler: EventHandler) => { dispose: () => void };
	provideExtensionService: (extensionId: string, state: ExtensionState, key: string, api: unknown) => { dispose: () => void };
	getExtensionService: (state: ExtensionState, key: string) => unknown;
	waitForExtensionService: (state: ExtensionState, key: string, options?: { timeoutMs?: number }) => Promise<unknown>;
	resetRuntimeState: () => void;
}

export interface BridgeTheme {
	fg: (token: string, text: unknown) => string;
	bg: (token: string, text: unknown) => string;
	style: (token: string, text: unknown) => string;
	bold: (text: unknown) => string;
	italic: (text: unknown) => string;
	strikethrough: (text: unknown) => string;
}

export interface RenderableComponent {
	render: (width?: number) => string[];
	handleInput?: (data: string) => void;
	dispose?: () => void;
}

export interface ToolRenderContext {
	args: unknown;
	toolCallId: string;
	invalidate: () => void;
	lastComponent: unknown;
	state: Record<string, unknown>;
	cwd: string;
	executionStarted: boolean;
	argsComplete: boolean;
	isPartial: boolean;
	expanded: boolean;
	showImages: boolean;
	isError: boolean;
}

export interface UiApiOptions {
	extensionId: string;
	state: ExtensionState;
	sendRequest: (method: string, params?: Record<string, unknown>, options?: SendRequestOptions) => Promise<unknown>;
}

export interface UiApi {
	editor: EditorApi;
	theme: BridgeTheme;
	getAllThemes: () => Promise<unknown>;
	getTheme: () => Promise<unknown>;
	setTheme: (theme: unknown) => unknown;
	setWorkingMessage: (message: unknown) => unknown;
	setWorkingVisible: (visible: unknown) => unknown;
	setWorkingIndicator: (indicator: unknown) => unknown;
	workingIndicator: (indicator: unknown) => unknown;
	setTitle: (title: unknown) => unknown;
	setEditorText: (text: unknown) => unknown;
	getEditorText: () => Promise<string>;
	pasteToEditor: (text: unknown) => unknown;
	addAutocompleteProvider: (provider: unknown) => { dispose: () => void };
	custom(kind: unknown, payload?: Record<string, unknown>): unknown;
	custom(factory: CustomUiFactory, payload?: Record<string, unknown>): Promise<unknown>;
	customComponent: (key: unknown, factory: unknown, options?: Record<string, unknown>) => unknown;
	setEditorComponent: (factory: unknown) => void;
	getEditorComponent: () => unknown;
	getToolsExpanded: () => Promise<boolean>;
	setToolsExpanded: (expanded: boolean) => unknown;
	toolsExpanded: (expanded?: boolean) => unknown;
	notify: (message: unknown, options?: Record<string, unknown>) => unknown;
	toast: (message: unknown, options?: Record<string, unknown>) => unknown;
	confirm: (message: unknown, options?: Record<string, unknown>) => unknown;
	prompt: (message: unknown, options?: Record<string, unknown>) => unknown;
	input: (message: unknown, options?: Record<string, unknown>) => unknown;
	select: (message: unknown, options?: Record<string, unknown> | unknown[]) => unknown;
	markdown: (markdown: unknown, options?: Record<string, unknown>) => unknown;
	details: (title: unknown, body: unknown, options?: Record<string, unknown>) => unknown;
	progress: (id: unknown, value: unknown, options?: Record<string, unknown>) => unknown;
	setStatus: (key: string, text?: string) => unknown;
	status: (status: unknown, options?: Record<string, unknown>) => unknown;
	setFooter: (factory: unknown) => void;
	setHeader: (factory: unknown) => void;
	panel: (component: unknown, options?: Record<string, unknown>) => unknown;
	setWidget: (key: unknown, factory: unknown, options?: Record<string, unknown>) => unknown;
	registerMenuItem: (menu: unknown, item?: Record<string, unknown>) => unknown;
}

export interface EditorApi {
	(title: unknown, prefill?: unknown): Promise<unknown>;
	setText: (text: unknown) => unknown;
	getText: () => Promise<string>;
	paste: (text: unknown) => unknown;
	addAutocompleteProvider: (provider: unknown) => { dispose: () => void };
}

export interface RuntimeSessionSnapshot {
	sessionId?: string;
	model?: unknown;
	modelRegistry?: unknown;
	branch?: unknown[];
	entries?: Entry[];
	messages?: unknown[];
	leafId?: string;
	leafEntry?: Entry;
	tree?: unknown;
	childrenByParentId?: Record<string, Entry[]>;
	labels?: Record<string, string>;
	header?: unknown;
	cwd?: string;
	sessionDir?: string;
	sessionFile?: string;
	sessionName?: string;
	isPersisted?: boolean;
	contextUsage?: unknown;
	thinkingLevel?: string;
	flags?: Record<string, unknown>;
	activeTools?: string[];
	allTools?: string[];
	modelId?: unknown;
	[key: string]: unknown;
}

export interface Entry {
	id?: string;
	Id?: string;
	[key: string]: unknown;
}

export interface SessionManager {
	getSessionId: () => string | undefined;
	getBranch: () => unknown[];
	getEntries: () => Entry[];
	getLeafId: () => string | undefined;
	getLeafEntry: () => Entry | undefined;
	getEntry: (id: string) => Entry | undefined;
	getTree: () => unknown;
	getChildren: (parentId?: string) => Entry[];
	getLabel: (id: string) => string | undefined;
	getHeader: () => unknown;
	getCwd: () => string;
	getSessionDir: () => string | undefined;
	getSessionFile: () => string | undefined;
	getSessionName: () => string | undefined;
	isPersisted: () => boolean;
}

export interface ExtensionContext {
	extensionId: string;
	cwd: string;
	hasUI: boolean;
	ui: UiApi;
	modelRegistry: unknown;
	model: unknown;
	sessionManager: SessionManager;
	isIdle: () => Promise<boolean>;
	signal: AbortSignal;
	abort: () => unknown;
	hasPendingMessages: () => Promise<boolean>;
	shutdown: () => unknown;
	getContextUsage: () => unknown;
	compact: (options?: Record<string, unknown>) => unknown;
	getSystemPrompt: () => Promise<string>;
}

export interface SessionCommandContext extends ExtensionContext {
	waitForIdle: () => Promise<void>;
	newSession: (options?: { withSession?: (ctx: ReplacementSessionContext) => unknown }) => Promise<{ cancelled: boolean; reason?: string }>;
	fork: (entryId: string, options?: { position?: string; withSession?: (ctx: ReplacementSessionContext) => unknown }) => Promise<{ cancelled: boolean; reason?: string }>;
	navigateTree: (targetId: string, options?: Record<string, unknown>) => Promise<void>;
	switchSession: (sessionPath: string, options?: { withSession?: (ctx: ReplacementSessionContext) => unknown }) => Promise<{ cancelled: boolean; reason?: string }>;
	reload: () => Promise<void>;
}

export interface ReplacementSessionContext extends SessionCommandContext {
	sendMessage: (message: unknown, options?: Record<string, unknown>) => unknown;
	sendUserMessage: (content: unknown, options?: Record<string, unknown>) => unknown;
}

export type CommandContext = SessionCommandContext;

export interface ExtensionContextModule {
	createExtensionContext: (extensionId: string) => ExtensionContext;
	createSessionCommandContext: (extensionId: string) => SessionCommandContext;
	createReplacementSessionContext: (extensionId: string) => ReplacementSessionContext;
	createCommandContext: (extensionId: string) => CommandContext;
	updateRuntimeSnapshotFromAction: (result: unknown) => void;
}

declare global {
	var __pisharpTsBridge: TsBridgeGlobal | undefined;
}

export interface TsBridgeGlobal {
	actions: Record<string, string>;
	snapshot: RuntimeSessionSnapshot;
	setSnapshot: (session: RuntimeSessionSnapshot) => void;
	runtime: (action: string, payload?: Record<string, unknown>) => Promise<unknown>;
	_agentSessionListeners?: Map<string, Set<(event: unknown) => void>>;
}

export interface TypeScriptModule {
	default?: TypeScriptModule;
	version?: string;
	transpileModule?: (source: string, options: TypeScriptTranspileOptions) => TranspileOutput;
	createSourceFile?: (filename: string, source: string, target: number, setParentNodes: boolean, kind: number) => unknown;
	ModuleKind?: { ES2022: number };
	ScriptTarget?: { ESNext: number; ES2022: number };
	ModuleResolutionKind?: { NodeNext: number };
	ScriptKind?: { JS: number };
	SyntaxKind?: { ImportKeyword: number };
	isImportDeclaration?: (node: unknown) => boolean;
	isExportDeclaration?: (node: unknown) => boolean;
	isStringLiteral?: (node: unknown) => boolean;
	isCallExpression?: (node: unknown) => boolean;
	forEachChild?: (node: unknown, visitor: (child: unknown) => void) => void;
}

export interface TypeScriptTranspileOptions {
	compilerOptions: {
		module: number;
		target: number;
		moduleResolution: number;
		verbatimModuleSyntax: boolean;
	};
}

export interface TranspileOutput {
	outputText: string;
}

export interface LoadTimings {
	[key: string]: number;
	cacheLookup: number;
	compilerLoad: number;
	transpile: number;
	dependencyTranspile: number;
	moduleImport: number;
	activation: number;
	registrationFlush: number;
	registrationApply: number;
	total: number;
	cacheHits: number;
	cacheMisses: number;
	cacheFallbacks: number;
}

export interface TranspilerOptions {
	shimRegistry: ShimRegistryLike;
	getBridgeManifest: () => unknown;
}

export interface ShimRegistryLike {
	resolve: (specifier: string) => string | null;
	refresh: (manifest: unknown) => Promise<void>;
	require: (specifier: string) => string;
	clear: () => void;
	initialize: (manifest: unknown) => Promise<void>;
}

export interface TranspilerApi {
	moduleInfoFor: (extensionPath: string, timings: LoadTimings, options?: { forceFresh?: boolean; descriptorMetadata?: DependencyHashRecord }) => Promise<ModuleInfo>;
	importExtensionModule: (extensionPath: string, timings: LoadTimings) => Promise<ImportResult>;
	setCacheDirectory: (dir: string) => void;
	setCacheEnabled: (enabled: boolean) => void;
	clearTranspiledBySource: () => void;
	bumpImportVersion: () => void;
	registerExitCleanup: () => void;
	hashText: (text: string) => string;
}

export interface ModuleInfo {
	resolved: string;
	descriptorMetadata: DependencyHashRecord;
	modulePath: string;
	href: string;
	transpiled: boolean;
	fromCache: boolean;
}

export interface DependencyHashRecord {
	sourceHash: string | null;
	dependencyHashes: DependencyHash[];
	packageName?: string | null;
	packageVersion?: string | null;
}

export interface ImportResult {
	module: Record<string, unknown>;
	descriptorMetadata: DependencyHashRecord;
}

export interface DependencyOutput {
	specifier: string;
	path: string;
}

export interface EventDispatchOptions {
	name: string;
	payload: Record<string, unknown>;
	extensionStates: Map<string, ExtensionState>;
	createExtensionContext: (extensionId: string) => ExtensionContext;
	flushRegistrationRequests: () => Promise<void>;
}

export interface EventDispatchResult {
	systemPrompt?: string;
	messages?: unknown[];
	patch?: PromptDocumentPatch;
	action?: string;
	text?: string;
	images?: unknown[] | null;
	cancel?: boolean;
	reason?: string;
	skillPaths?: string[];
	promptPaths?: string[];
	themePaths?: string[];
	bashResult?: unknown;
	operations?: unknown;
}

export interface PromptDocumentPatch {
	removeSectionIds?: string[];
	replaceSections?: PromptSectionPatch[];
	appendSections?: PromptSectionPatch[];
}

export interface PromptSectionPatch {
	id?: string;
	kind?: string;
	slot?: string;
	priority?: number;
	contentType?: string;
	content?: string;
	protected?: boolean;
	beforeSectionId?: string;
	afterSectionId?: string;
}

export interface ExtensionServiceWaiter {
	resolve: (api: unknown) => void;
	reject: (error: unknown) => void;
}

export interface ExtensionServiceEntry {
	extensionId: string;
	api: unknown;
}

export interface PiApiDeps {
	sendRequest: (method: string, params?: Record<string, unknown>, options?: SendRequestOptions) => Promise<unknown>;
	trackRegistrationRequest: (method: string, params?: Record<string, unknown>) => Promise<unknown> | void;
	provideExtensionService: (extensionId: string, state: ExtensionState, key: string, api: unknown) => { dispose: () => void };
	getExtensionService: (state: ExtensionState, key: string) => unknown;
	waitForExtensionService: (state: ExtensionState, key: string, options?: { timeoutMs?: number }) => Promise<unknown>;
	rememberDescriptorService: (state: ExtensionState, field: "providesServices" | "consumesServices", key: string) => void;
	normalizeServiceKey: (key: string) => string;
	addEventHandler: (state: ExtensionState, eventName: string, handler: EventHandler) => { dispose: () => void };
	createUiApi: (options: UiApiOptions) => UiApi;
	emitEvent: ((channel: string, payload: unknown) => Promise<void>) | undefined;
	cwd: string;
	getCommandsSnapshot: () => CommandInfo[] | undefined;
	addCommandToSnapshot: (command: CommandInfo) => void;
	getSessionNameSnapshot: (() => string | undefined) | undefined;
	getSnapshot: () => RuntimeSessionSnapshot;
	protocol: BridgeProtocol;
}

export interface CommandInfo {
	name: string;
	description?: string;
	source?: string;
	sourceInfo?: { path?: string; source?: string; scope?: string; origin?: string };
}

export interface BridgeProtocol {
	methods: {
		registerTool: string;
		registerSkill: string;
		registerProvider: string;
		unregisterProvider: string;
		registerCommand: string;
		registerShortcut: string;
		registerFlag: string;
		registerPromptSection: string;
		registerPromptTransform: string;
		registerMessageRenderer: string;
		unregisterMessageRenderer: string;
		registerMessageDecorator: string;
		unregisterMessageDecorator: string;
		runtimeAction: string;
		uiRequest: string;
	};
	runtimeActions: {
		getAllSkills: string;
		getSelectedSkills: string;
		setSelectedSkills: string;
		getFlag: string;
		getFlags: string;
		getActiveTools: string;
		getAllTools: string;
		getCommands: string;
		waitForIdle: string;
		newSession: string;
		forkSession: string;
		navigateTree: string;
		switchSession: string;
		isIdle: string;
		hasPendingMessages: string;
		compact: string;
		getSystemPrompt: string;
		abort: string;
		shutdown: string;
		exec: string;
		getThinkingLevel: string;
		sendMessage: string;
		sendUserMessage: string;
		appendEntry: string;
		setEntryLabel: string;
		getSessionName: string;
		setSessionName: string;
		setActiveTools: string;
		setModel: string;
		setThinkingLevel: string;
		modelRolesResolve: string;
		setModelRole: string;
		reloadExtensions: string;
		installExtension: string;
		updateExtension: string;
		removeExtension: string;
		listInstalledExtensions: string;
		managedSkillCreate: string;
		managedSkillUpdate: string;
		managedSkillDelete: string;
		managedSkillList: string;
		managedSkillPromote: string;
		registerSkillProvider: string;
		discoverSkillProvider: string;
		getSkillProviderPriorities: string;
		emitEvent: string;
		listResources: string;
		readResource: string;
		completeSimple: string;
		promptAndWait: string;
		createAgentSession: string;
		agentSessionPrompt: string;
		agentSessionSteer: string;
		agentSessionFollowUp: string;
		agentSessionAbort: string;
		agentSessionCompact: string;
		agentSessionSetModel: string;
		agentSessionSetThinkingLevel: string;
		agentSessionDispose: string;
		settingsGet: string;
		settingsGetCore: string;
		settingsSet: string;
		settingsRemove: string;
		stateGet: string;
		stateSet: string;
		stateRemove: string;
		stateGetAll: string;
		stateListKeys: string;
		stateClear: string;
		stateGetSchemaVersion: string;
		stateSetSchemaVersion: string;
		stateRegisterMigration: string;
	};
}

export interface ExtensionLoadResult {
	ok: boolean;
	extensionPath: string;
	descriptor?: ExtensionDescriptor;
	skipped?: boolean;
	error?: string;
	timings?: LoadTimings;
}

export interface CommandResult {
	handled: boolean;
	isError?: boolean;
	message?: string;
}
