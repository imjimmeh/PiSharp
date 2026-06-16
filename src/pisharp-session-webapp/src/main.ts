import "./app.css";
import { PiSessionStore } from "./state";
import type { AppState, ConnectionSettings, TranscriptItem } from "./state";
import type { ServerPersistedSession } from "./types";

const SETTINGS_KEY = "pisharp-session-webapp:settings";
const appElement = document.getElementById("app");
let localError: string | undefined;

if (!appElement) {
	throw new Error("App container not found");
}

const app = appElement;
const store = new PiSessionStore(loadSettings());
store.subscribe((state) => render(state));

function loadSettings(): ConnectionSettings {
	const stored = localStorage.getItem(SETTINGS_KEY);
	if (stored) {
		try {
			return { ...defaultSettings(), ...(JSON.parse(stored) as Partial<ConnectionSettings>) };
		} catch {
			localStorage.removeItem(SETTINGS_KEY);
		}
	}
	return defaultSettings();
}

function defaultSettings(): ConnectionSettings {
	return {
		baseUrl: "http://localhost:5000",
		apiKey: "",
		cwd: "",
		sessionsRoot: undefined,
		allCwds: false,
	};
}

function saveSettings(settings: ConnectionSettings): void {
	localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
}

function render(state: AppState): void {
	app.innerHTML = `
		<main class="app-shell">
			<aside class="sidebar">
				${renderConnectionPanel(state)}
				${renderSessionPanel(state)}
			</aside>
			<section class="main-pane">
				${renderActiveHeader(state)}
				${renderTranscript(state)}
				${renderComposer(state)}
			</section>
		</main>
	`;
	bindHandlers(state);
}

function renderConnectionPanel(state: AppState): string {
	return `
		<section class="panel">
			<h1>PiSharp Sessions</h1>
			<form id="connection-form" class="field-grid">
				<div class="field-row">
					<label for="base-url">API URL</label>
					<input id="base-url" name="baseUrl" value="${escapeHtml(state.settings.baseUrl)}" placeholder="http://localhost:5000" />
				</div>
				<div class="field-row">
					<label for="api-key">API key</label>
					<input id="api-key" name="apiKey" type="password" value="${escapeHtml(state.settings.apiKey)}" placeholder="PISHARP_SERVER_API_KEY" />
				</div>
				<div class="field-row">
					<label for="cwd">CWD</label>
					<input id="cwd" name="cwd" value="${escapeHtml(state.settings.cwd)}" placeholder="G:/code/AI/pi/PiSharp" />
				</div>
				<div class="field-row">
					<label for="sessions-root">Sessions root</label>
					<input id="sessions-root" name="sessionsRoot" value="${escapeHtml(state.settings.sessionsRoot ?? "")}" placeholder="optional" />
				</div>
				<label class="toolbar">
					<input id="all-cwds" name="allCwds" type="checkbox" ${state.settings.allCwds ? "checked" : ""} />
					<span>List all CWDs</span>
				</label>
				<div class="toolbar">
					<button type="submit">${state.connectionStatus === "connected" ? "Reconnect" : "Connect"}</button>
					<button id="disconnect" type="button" ${state.connectionStatus === "disconnected" ? "disabled" : ""}>Disconnect</button>
				</div>
			</form>
			<p class="muted">Status: ${state.connectionStatus}</p>
			${localError ? `<p class="error">${escapeHtml(localError)}</p>` : ""}
			${state.errorMessage ? `<p class="error">${escapeHtml(state.errorMessage)}</p>` : ""}
		</section>
	`;
}

function renderSessionPanel(state: AppState): string {
	return `
		<section class="panel session-list-panel">
			<div class="toolbar">
				<button id="refresh-sessions" ${state.connectionStatus !== "connected" ? "disabled" : ""}>Refresh sessions</button>
				<button id="create-session" ${state.connectionStatus !== "connected" ? "disabled" : ""}>New session</button>
			</div>
			<div class="session-list">
				${state.sessions.length === 0 ? `<p class="muted">No sessions loaded.</p>` : state.sessions.map((session) => renderSessionCard(session, state)).join("")}
			</div>
		</section>
	`;
}

function renderSessionCard(session: ServerPersistedSession, state: AppState): string {
	const active = state.activeRuntimeSessionId === session.id;
	return `
		<button class="session-card ${active ? "active" : ""}" data-open-session="${escapeHtml(session.id)}">
			<strong>${escapeHtml(session.id)}</strong>
			<span class="muted">${escapeHtml(session.cwd)}</span>
			<span class="muted">${formatDate(session.createdAt)}${session.isLive ? " · live" : ""}</span>
		</button>
	`;
}

function renderActiveHeader(state: AppState): string {
	const active = state.activeSession;
	if (!active) {
		return `<header class="panel"><h2>No active session</h2><p class="muted">Create or open a session to start interacting.</p></header>`;
	}

	return `
		<header class="panel">
			<div class="toolbar">
				<h2>${escapeHtml(active.sessionName ?? active.runtimeSessionId)}</h2>
				<button id="refresh-active">Refresh</button>
				<button id="abort-active" ${active.isBusy || active.isCompacting ? "" : "disabled"}>Abort</button>
			</div>
			<p class="muted">${escapeHtml(active.cwd)} · ${escapeHtml(active.model.name ?? `${active.model.provider}/${active.model.id}`)} · ${active.thinkingLevel} · ${active.isCompacting ? "compacting" : active.isBusy ? "busy" : "idle"}</p>
			<form id="rename-form" class="toolbar">
				<input id="session-name" value="${escapeHtml(active.sessionName ?? "")}" placeholder="Session name" />
				<button type="submit">Rename</button>
			</form>
			<form id="fork-form" class="toolbar">
				<input id="fork-id" value="" placeholder="Optional new session id" />
				<button type="submit">Fork</button>
			</form>
		</header>
	`;
}

function renderTranscript(state: AppState): string {
	const items = state.streamingMessage ? [...state.transcript, state.streamingMessage] : state.transcript;
	return `
		<section class="transcript">
			${items.length === 0 ? `<p class="muted">No messages yet.</p>` : items.map(renderTranscriptItem).join("")}
		</section>
	`;
}

function renderTranscriptItem(item: TranscriptItem): string {
	return `
		<article class="message ${escapeHtml(item.role)} ${item.isError ? "error" : ""}">
			<strong>${escapeHtml(item.toolName ?? item.role)}${item.isStreaming ? " …" : ""}</strong>
			<div>${escapeHtml(item.text || " ")}</div>
		</article>
	`;
}

function renderComposer(state: AppState): string {
	const disabled = !state.activeSession;
	return `
		<section class="composer">
			<form id="message-form" class="field-grid">
				<textarea id="message-input" placeholder="Send a prompt to the active PiSharp session" ${disabled ? "disabled" : ""}></textarea>
				<div class="toolbar">
					<button type="submit" ${disabled ? "disabled" : ""}>Prompt</button>
					<button type="button" data-queue-kind="steer" ${disabled ? "disabled" : ""}>Steer</button>
					<button type="button" data-queue-kind="followUp" ${disabled ? "disabled" : ""}>Follow up</button>
					<button type="button" data-queue-kind="nextTurn" ${disabled ? "disabled" : ""}>Queue next turn</button>
				</div>
			</form>
		</section>
	`;
}

function bindHandlers(state: AppState): void {
	document.getElementById("connection-form")?.addEventListener("submit", (event) => {
		event.preventDefault();
		void runAction(async () => {
			const settings = readSettingsFromForm();
			saveSettings(settings);
			store.updateSettings(settings);
			store.disconnect();
			await store.connect();
			await store.refreshSessions();
		});
	});

	document.getElementById("disconnect")?.addEventListener("click", () => {
		store.disconnect();
	});

	document.getElementById("refresh-sessions")?.addEventListener("click", () => {
		void runAction(() => store.refreshSessions());
	});

	document.getElementById("create-session")?.addEventListener("click", () => {
		void runAction(() => store.createSession());
	});

	for (const button of document.querySelectorAll<HTMLButtonElement>("[data-open-session]")) {
		button.addEventListener("click", () => {
			const session = state.sessions.find((candidate) => candidate.id === button.dataset.openSession);
			if (session) void runAction(() => store.openSession(session));
		});
	}

	document.getElementById("refresh-active")?.addEventListener("click", () => {
		void runAction(() => store.refreshActiveSession());
	});

	document.getElementById("abort-active")?.addEventListener("click", () => {
		void runAction(() => store.abort());
	});

	document.getElementById("rename-form")?.addEventListener("submit", (event) => {
		event.preventDefault();
		void runAction(() => store.renameActiveSession(inputValue("session-name")));
	});

	document.getElementById("fork-form")?.addEventListener("submit", (event) => {
		event.preventDefault();
		const id = inputValue("fork-id").trim();
		void runAction(() => store.forkActiveSession(id || undefined));
	});

	document.getElementById("message-form")?.addEventListener("submit", (event) => {
		event.preventDefault();
		const input = inputValue("message-input").trim();
		if (input) void runAction(() => store.prompt(input));
	});

	for (const button of document.querySelectorAll<HTMLButtonElement>("[data-queue-kind]")) {
		button.addEventListener("click", () => {
			const input = inputValue("message-input").trim();
			if (!input) return;
			void runAction(() => sendQueuedMessage(button.dataset.queueKind, input));
		});
	}
}

function readSettingsFromForm(): ConnectionSettings {
	const sessionsRoot = inputValue("sessions-root").trim();
	return {
		baseUrl: inputValue("base-url").trim(),
		apiKey: inputValue("api-key"),
		cwd: inputValue("cwd").trim(),
		sessionsRoot: sessionsRoot || undefined,
		allCwds: checkboxValue("all-cwds"),
	};
}

async function sendQueuedMessage(kind: string | undefined, input: string): Promise<void> {
	if (kind === "steer") await store.steer(input, true);
	else if (kind === "followUp") await store.followUp(input, true);
	else await store.queueNextTurn(input, true);
}

async function runAction(action: () => Promise<void>): Promise<void> {
	localError = undefined;
	try {
		await action();
	} catch (error) {
		localError = error instanceof Error ? error.message : String(error);
		render(store.state);
	}
}

function inputValue(id: string): string {
	const input = document.getElementById(id);
	return input instanceof HTMLInputElement || input instanceof HTMLTextAreaElement ? input.value : "";
}

function checkboxValue(id: string): boolean {
	const input = document.getElementById(id);
	return input instanceof HTMLInputElement ? input.checked : false;
}

function escapeHtml(value: string): string {
	return value
		.replaceAll("&", "&amp;")
		.replaceAll("<", "&lt;")
		.replaceAll(">", "&gt;")
		.replaceAll('"', "&quot;")
		.replaceAll("'", "&#039;");
}

function formatDate(value: string): string {
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}
