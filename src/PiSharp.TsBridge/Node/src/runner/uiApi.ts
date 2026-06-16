import type {
	BridgeTheme,
	CustomUiFactory,
	CustomUiInputRequest,
	CustomUiSession,
	CustomUiSnapshot,
	ExtensionState,
	RenderableComponent,
	SendRequestOptions,
	ToolRenderContext,
	UiApi,
	UiApiOptions,
} from "../types.js";

/**
 * Options forwarded to sendRequest for long-lived ui_requests that block on
 * human input (custom overlays, confirm/prompt/select/editor). Disabling the
 * JSON-RPC timeout prevents the default 60s limit from rejecting a request
 * while the user is still interacting with the UI.
 */
const NO_TIMEOUT: SendRequestOptions = { timeoutMs: null };

const THEME_FG_ANSI = new Map<string, string>([
	["success", "32"],
	["error", "31"],
	["warning", "33"],
	["accent", "36"],
	["borderAccent", "36"],
	["muted", "90"],
	["dim", "90"],
	["text", "37"],
	["toolDiffAdded", "32"],
	["toolDiffRemoved", "31"],
	["toolDiffContext", "90"],
]);

const THEME_BG_ANSI = new Map<string, string>([
	["selectedBg", "44"],
	["userMessageBg", "44"],
	["customMessageBg", "45"],
	["toolPendingBg", "44"],
	["toolSuccessBg", "42"],
	["toolErrorBg", "41"],
]);

const DEFAULT_CUSTOM_UI_COLUMNS = 80;
const DEFAULT_CUSTOM_UI_ROWS = 24;

export const customUiSessions = new Map<string, CustomUiSession>();

type UiOptions = Record<string, unknown>;

function createCustomUiTheme(): BridgeTheme {
	const text = (value: unknown): string => String(value ?? "");
	return {
		fg: (_token: string, value: unknown): string => text(value),
		bg: (_token: string, value: unknown): string => text(value),
		style: (_token: string, value: unknown): string => text(value),
		bold: (value: unknown): string => text(value),
		italic: (value: unknown): string => text(value),
		strikethrough: (value: unknown): string => text(value),
	};
}

function resolveCustomUiDimensions(width?: number, height?: number): { width: number; height: number } {
	return {
		width: typeof width === "number" && Number.isFinite(width) ? width : process.stdout.columns ?? DEFAULT_CUSTOM_UI_COLUMNS,
		height: typeof height === "number" && Number.isFinite(height) ? height : process.stdout.rows ?? DEFAULT_CUSTOM_UI_ROWS,
	};
}

function createCustomUiSnapshot(session: CustomUiSession): CustomUiSnapshot {
	return {
		requestId: session.requestId,
		lines: renderComponentLines(session.component, session.width),
		width: session.width,
		height: session.height,
		completed: session.completed,
		value: session.value,
		error: session.error,
	};
}

function createCustomUiRequestPayload(session: CustomUiSession): Record<string, unknown> {
	return {
		mode: "interactive-component",
		requestId: session.requestId,
		lines: renderComponentLines(session.component, session.width),
		width: session.width,
		height: session.height,
		overlay: session.overlay,
		overlayOptions: session.overlayOptions,
		completed: session.completed,
		value: session.value,
		error: session.error,
	};
}

export function handleCustomUiInput(request: CustomUiInputRequest): CustomUiSnapshot {
	const session = customUiSessions.get(request.requestId);
	if (!session) throw new Error(`Unknown custom UI session '${request.requestId}'`);

	if (typeof request.width === "number" && Number.isFinite(request.width)) {
		session.width = request.width;
		session.terminal.columns = request.width;
	}
	if (typeof request.height === "number" && Number.isFinite(request.height)) {
		session.height = request.height;
		session.terminal.rows = request.height;
	}
	if (request.event === "resize") {
		return createCustomUiSnapshot(session);
	}
	if (typeof request.data === "string" && typeof session.component.handleInput === "function") {
		try {
			session.component.handleInput(request.data);
		} catch (error) {
			session.error = error instanceof Error ? error.message : String(error);
			session.completed = true;
		}
	}

	return createCustomUiSnapshot(session);
}

export function createBridgeTheme(): BridgeTheme {
	const wrap = (open: string, close: string, text: unknown): string =>
		`\x1b[${open}m${String(text ?? "")}\x1b[${close}m`;
	return {
		fg: (token: string, text: unknown): string => wrap(THEME_FG_ANSI.get(token) ?? "37", "39", text),
		bg: (token: string, text: unknown): string => wrap(THEME_BG_ANSI.get(token) ?? "40", "49", text),
		style: (token: string, text: unknown): string =>
			wrap(THEME_FG_ANSI.get(token) ?? "37", "39", text),
		bold: (text: unknown): string => wrap("1", "22", text),
		italic: (text: unknown): string => wrap("3", "23", text),
		strikethrough: (text: unknown): string => wrap("9", "29", text),
	};
}

export function renderComponentLines(component: RenderableComponent, width: number = 120): string[] {
	if (!component || typeof component.render !== "function") return [];
	const rendered = component.render(width);
	if (Array.isArray(rendered)) return rendered.map(String);
	if (rendered === undefined || rendered === null) return [];
	return [String(rendered)];
}

export function createToolRenderContext(request: { toolCallId?: string; isPartial?: boolean; expanded?: boolean; isError?: boolean }, args: unknown, cwd: string): ToolRenderContext {
	return {
		args,
		toolCallId: request.toolCallId ?? "",
		invalidate: () => {},
		lastComponent: undefined,
		state: {},
		cwd,
		executionStarted: true,
		argsComplete: true,
		isPartial: request.isPartial ?? false,
		expanded: request.expanded ?? false,
		showImages: false,
		isError: request.isError ?? false,
	};
}

export function createUiApi({ extensionId, state, sendRequest }: UiApiOptions): UiApi {
	const ui = (kind: string, payload: Record<string, unknown> = {}, sourceId: string = extensionId, requestOptions?: SendRequestOptions): Promise<unknown> =>
		sendRequest("ui_request", {
			requestId: `${Date.now()}-${Math.random()}`,
			extensionId: sourceId,
			kind,
			title: payload?.title ?? payload?.message ?? kind,
			message: payload?.message,
			options: payload?.options,
			component: payload?.component,
			payload,
		}, requestOptions);
	// Interactive value requests block until the user answers, so they must not
	// be subject to the default JSON-RPC timeout.
	const uiValue = async (kind: string, payload: Record<string, unknown> = {}, sourceId: string = extensionId): Promise<unknown> => {
		const response = await ui(kind, payload, sourceId, NO_TIMEOUT);
		if (response && typeof response === "object" && "value" in response) return (response as { value: unknown }).value;
		return response;
	};
	const choicePayload = (message: unknown, options: UiOptions | unknown[] = {}): Record<string, unknown> =>
		Array.isArray(options)
			? { message, options }
			: { message, ...options };
	const bridgeTheme = createBridgeTheme();
	const footerData = {
		getGitBranch: () => null,
		getExtensionStatuses: () => new Map(state.extensionStatuses),
		getAvailableProviderCount: () => 0,
		onBranchChange: () => () => {},
	};
	const renderLines = (component: RenderableComponent): string[] => renderComponentLines(component, 120);
	const setComponent = (slot: string, kind: string, factory: unknown): void => {
		const existing = state[slot as keyof Pick<ExtensionState, "footerComponent" | "headerComponent" | "editorComponent">];
		if (existing && typeof (existing as { dispose?: () => void }).dispose === "function") (existing as { dispose: () => void }).dispose();
		if (factory === undefined) {
			(state as unknown as Record<string, unknown>)[slot] = undefined;
			void ui(kind, {});
			return;
		}
		let component: RenderableComponent;
		const tui = {
			requestRender: () =>
				void ui(kind, { message: renderLines(component).join("\n") }),
		};
		component =
			typeof factory === "function"
				? factory(tui, bridgeTheme, footerData) as RenderableComponent
				: factory as RenderableComponent;
		(state as unknown as Record<string, unknown>)[slot] = component;
		void ui(kind, { message: renderLines(component).join("\n") });
	};
	const editor = Object.assign(
		(title: unknown, prefill?: unknown) => uiValue("editor", { message: title, initialValue: prefill }),
		{
			setText: (text: unknown) => ui("editor_set_text", { text } as Record<string, unknown>),
			getText: async () => String((await ui("editor_get_text", {}) as Record<string, unknown> | undefined)?.value ?? ""),
			paste: (text: unknown) => ui("editor_paste", { text } as Record<string, unknown>),
			addAutocompleteProvider: (provider: unknown) => {
				state.autocompleteProviders ??= [];
				state.autocompleteProviders.push(provider);
				return { dispose: () => { state.autocompleteProviders = (state.autocompleteProviders ?? []).filter((item: unknown) => item !== provider); } };
			},
		},
	);
	return {
		editor,
		theme: bridgeTheme,
		getAllThemes: async () => ((await ui("get_all_themes", {}) as Record<string, unknown> | undefined)?.value as unknown[]) ?? [],
		getTheme: async () => ((await ui("get_theme", {}) as Record<string, unknown> | undefined)?.value as BridgeTheme) ?? bridgeTheme,
		setTheme: (theme: unknown) => ui("set_theme", { theme } as Record<string, unknown>),
		setWorkingMessage: (message: unknown) => ui("working_message", { message } as Record<string, unknown>),
		setWorkingVisible: (visible: unknown) => ui("working_visible", { visible } as Record<string, unknown>),
		setWorkingIndicator: (indicator: unknown) => ui("working_indicator", { indicator } as Record<string, unknown>),
		workingIndicator: (indicator: unknown) => ui("working_indicator", { indicator } as Record<string, unknown>),
		setTitle: (title: unknown) => ui("title", { title } as Record<string, unknown>),
		setEditorText: (text: unknown) => ui("editor_set_text", { text } as Record<string, unknown>),
		getEditorText: async () => String((await ui("editor_get_text", {}) as Record<string, unknown> | undefined)?.value ?? ""),
		pasteToEditor: (text: unknown) => ui("editor_paste", { text } as Record<string, unknown>),
		addAutocompleteProvider: (provider: unknown) => {
			state.autocompleteProviders ??= [];
			state.autocompleteProviders.push(provider);
			return { dispose: () => { state.autocompleteProviders = (state.autocompleteProviders ?? []).filter((item: unknown) => item !== provider); } };
		},
		custom: (kind: unknown, payload: Record<string, unknown> = {}) => {
			if (typeof kind !== "function") return ui(kind ? String(kind) : "custom", payload);
			const customFactory = kind as CustomUiFactory;
			const { width, height } = resolveCustomUiDimensions(
				typeof payload.width === "number" ? payload.width : undefined,
				typeof payload.height === "number" ? payload.height : undefined,
			);
			const requestId = typeof payload.requestId === "string" && payload.requestId.trim()
				? payload.requestId
				: `custom-ui-${Date.now()}-${Math.random().toString(36).slice(2)}`;
			const session: CustomUiSession = {
				requestId,
				component: { render: () => [] },
				terminal: { columns: width, rows: height },
				width,
				height,
				overlay: payload.overlay === true,
				overlayOptions: payload.overlayOptions,
				completed: false,
				value: undefined,
				dirty: false,
				updateInFlight: false,
				updatePending: false,
			};
			customUiSessions.set(requestId, session);
			const done = (value: unknown): void => {
				session.completed = true;
				session.value = value;
			};
			const sendCustomUiRequest = (kind: "custom" | "custom_update"): Promise<unknown> =>
				// The "custom" request is long-lived — it stays open until the user
				// closes the overlay — so disable the timeout. "custom_update" is a
				// quick render flush and keeps the default timeout.
				ui(kind, createCustomUiRequestPayload(session), extensionId, kind === "custom" ? NO_TIMEOUT : undefined);
			const flushCustomUiUpdate = async (): Promise<void> => {
				if (session.completed) return;
				if (session.updateInFlight) {
					session.updatePending = true;
					return;
				}

				session.updateInFlight = true;
				try {
					while (!session.completed) {
						session.updatePending = false;
						session.dirty = false;
						try {
							await sendCustomUiRequest("custom_update");
						} catch {
							// Ignore transient update failures; the session may still complete normally.
						}

						if (!session.updatePending) break;
					}
				} finally {
					session.updateInFlight = false;
					if (!session.completed) session.dirty = false;
				}
			};
			const tui = {
				terminal: session.terminal,
				requestRender: () => {
					if (session.completed) return;
					session.dirty = true;
					if (session.updateInFlight) {
						session.updatePending = true;
						return;
					}

					void flushCustomUiUpdate();
				},
			};
			const customTheme = createCustomUiTheme();
			const keybindings = (payload.keybindings ?? {}) as Record<string, unknown>;
			return (async () => {
				try {
					session.component = await Promise.resolve(customFactory(tui, customTheme, keybindings, done)) as RenderableComponent;
					const response = await sendCustomUiRequest("custom");
					if (response && typeof response === "object" && "error" in response && (response as { error?: unknown }).error) {
						throw new Error(String((response as { error?: unknown }).error));
					}
					if (response && typeof response === "object" && "value" in response) {
						return (response as { value: unknown }).value;
					}
					if (session.completed) return session.value;
					return response;
				} finally {
					customUiSessions.delete(requestId);
					if (typeof session.component.dispose === "function") {
						try {
							session.component.dispose();
						} catch {
							// Ignore dispose failures during teardown.
						}
					}
				}
			})();
		},
		customComponent: (key: unknown, factory: unknown, options: Record<string, unknown> = {}) => ui("custom", { key, message: renderLines(typeof factory === "function" ? factory({ requestRender: () => {} }, bridgeTheme) as RenderableComponent : factory as RenderableComponent).join("\n"), ...options }),
		setEditorComponent: (factory: unknown) => setComponent("editorComponent", "editor_component", factory),
		getEditorComponent: () => state.editorComponent,
		getToolsExpanded: async () => ((await ui("tools_expanded_get", {}) as Record<string, unknown> | undefined)?.value as boolean) ?? false,
		setToolsExpanded: (expanded: boolean) => ui("tools_expanded_set", { expanded } as Record<string, unknown>),
		toolsExpanded: (expanded?: boolean) => expanded === undefined ? ui("tools_expanded_get", {}) : ui("tools_expanded_set", { expanded } as Record<string, unknown>),
		notify: (message: unknown, options: Record<string, unknown> = {}) => ui("notify", { message, ...options }),
		toast: (message: unknown, options: Record<string, unknown> = {}) => ui("notify", { message, ...options }),
		confirm: (message: unknown, options: UiOptions = {}) => uiValue("confirm", { message, ...options }),
		prompt: (message: unknown, options: UiOptions = {}) => uiValue("prompt", { message, ...options }),
		input: (message: unknown, options: UiOptions = {}) => uiValue("prompt", { message, ...options }),
		select: (message: unknown, options: UiOptions | unknown[] = {}) => uiValue("select", choicePayload(message, options)),
		markdown: (markdown: unknown, options: Record<string, unknown> = {}) => ui("markdown", { markdown, ...options }),
		details: (title: unknown, body: unknown, options: Record<string, unknown> = {}) =>
			ui("details", { title, body, ...options }),
		progress: (id: unknown, value: unknown, options: Record<string, unknown> = {}) =>
			ui("progress", { id, value, ...options }),
		setStatus: (key: string, text?: string) => {
			if (text === undefined) state.extensionStatuses.delete(key);
			else state.extensionStatuses.set(key, String(text));
			return ui("status", { message: text } as Record<string, unknown>, key);
		},
		status: (status: unknown, options: Record<string, unknown> = {}) =>
			ui("status", { message: status, ...options }),
		setFooter: (factory: unknown) => setComponent("footerComponent", "footer", factory),
		setHeader: (factory: unknown) => setComponent("headerComponent", "header", factory),
		panel: (component: unknown, options: Record<string, unknown> = {}) => ui("widget", { component, ...options }),
		setWidget: (key: unknown, factory: unknown, options: Record<string, unknown> = {}) => {
			const widgetKey = String(key ?? extensionId);
			const existing = state.widgetComponents?.get(widgetKey);
			if (existing && typeof (existing as { dispose?: () => void }).dispose === "function")
				(existing as { dispose: () => void }).dispose();
			if (factory === undefined) {
				state.widgetComponents?.delete(widgetKey);
				return ui("widget", {}, widgetKey);
			}
			let component: RenderableComponent;
			const tui = {
				terminal: {
					columns: process.stdout.columns ?? DEFAULT_CUSTOM_UI_COLUMNS,
					rows: process.stdout.rows ?? DEFAULT_CUSTOM_UI_ROWS,
				},
				requestRender: () =>
					void ui(
						"widget",
						{ message: renderLines(component).join("\n"), ...options },
						widgetKey,
					),
			};
			component =
				typeof factory === "function" ? factory(tui, bridgeTheme) as RenderableComponent : factory as RenderableComponent;
			state.widgetComponents?.set(widgetKey, component);
			return ui(
				"widget",
				{ message: renderLines(component).join("\n"), ...options },
				widgetKey,
			);
		},
		registerMenuItem: (menu: unknown, item: Record<string, unknown> = {}) =>
			ui("register_menu_item", { menu, ...item }),
	};
}
