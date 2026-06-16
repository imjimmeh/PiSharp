// src/shims/runtime/session.ts
import { getBridge } from "./bridge.js";

export class SessionManager {
  cwd: string;
  _sessionId: string;
  _persist: boolean;
  _sessionFile: string | undefined;
  _entries: unknown[];
  _leafId: string | null;
  flushed: boolean;

  static inMemory(cwd: string) { return new SessionManager(cwd, false); }
  static open(file: string, _dir?: unknown) { return new SessionManager(process.cwd(), true, file); }
  static create(cwd: string, _dir?: unknown) { return new SessionManager(cwd, true); }
  static continueRecent(cwd: string, _dir?: unknown) { return new SessionManager(cwd, true); }
  static forkFrom(_sourcePath: string, targetCwd: string, _dir?: unknown) { return new SessionManager(targetCwd, true); }
  static async list(_cwd?: unknown, _dir?: unknown, _onProgress?: unknown) { return []; }
  static async listAll(_onProgress?: unknown) { return []; }

  constructor(cwd: string, persist?: boolean, file?: string) {
    this.cwd = cwd;
    this._sessionId = "session-" + Math.random().toString(36).slice(2, 10);
    this._persist = !!persist;
    this._sessionFile = file;
    this._entries = [];
    this._leafId = null;
    this.flushed = true;
  }
  getSessionFile() { return this._sessionFile; }
  getSessionId() { return this._sessionId; }
  getSessionDir(): undefined { return undefined; }
  getCwd() { return this.cwd; }
  getLeafId() { return this._leafId; }
  getEntries() { return this._entries.slice(); }
  getSessionName(): undefined { return undefined; }
  createBranchedSession(_leafId?: unknown): undefined { return undefined; }
  openSession(file: string, _dir?: unknown) { return new SessionManager(this.cwd, false, file); }
  isPersisted() { return this._persist; }
  _rewriteFile() { this.flushed = true; }
}

export class SettingsManager {
  cwd: string;
  settings: Record<string, unknown>;
  static create(cwd: string, _agentDir?: unknown) { return new SettingsManager(cwd); }
  static fromStorage(_storage?: unknown) { return new SettingsManager(process.cwd()); }
  static inMemory(_settings?: unknown) { return new SettingsManager(process.cwd()); }
  constructor(cwd: string) {
    this.cwd = cwd;
    this.settings = {};
  }
  async reload() {}
  async flush() {}
}

export class AgentSession {
  _bridge: ReturnType<typeof getBridge>;
  _sessionId: string;
  _sessionSnapshot: any;
  _listeners: Set<unknown>;
  _liveMessages: unknown[];

  constructor(bridge: ReturnType<typeof getBridge>, sessionId: string, sessionSnapshot?: any) {
    this._bridge = bridge ?? getBridge();
    this._sessionId = sessionId;
    this._sessionSnapshot = sessionSnapshot;
    this._listeners = new Set();
    this._liveMessages = Array.isArray(sessionSnapshot?.messages)
      ? sessionSnapshot.messages.slice()
      : Array.isArray(sessionSnapshot?.entries)
        ? sessionSnapshot.entries.slice()
        : [];
    this._installLiveMessageTracking();
  }
  get bridge() { return this._bridge; }
  get sessionFile() { return this._sessionSnapshot?.sessionFile; }
  get sessionId() { return this._sessionId; }
  get model() { return this._sessionSnapshot?.model ?? this.bridge?.snapshot?.model; }
  get thinkingLevel() { return this._sessionSnapshot?.thinkingLevel ?? this.bridge?.snapshot?.thinkingLevel; }
  get messages() { return this._liveMessages; }
  get isStreaming() { return false; }
  get sessionName() { return this._sessionSnapshot?.sessionName ?? this.bridge?.snapshot?.sessionName; }
  _listenerSet(): Set<(event: unknown) => void> | undefined {
    const bridge = this._bridge ?? getBridge();
    if (!bridge) return undefined;
    if (!bridge._agentSessionListeners) bridge._agentSessionListeners = new Map();
    if (!bridge._agentSessionListeners.has(this._sessionId)) bridge._agentSessionListeners.set(this._sessionId, new Set());
    return bridge._agentSessionListeners.get(this._sessionId);
  }
  _installLiveMessageTracking() {
    const subs = this._listenerSet();
    if (subs) subs.add((event: unknown) => this._applySessionEvent(event));
  }
  _applySessionEvent(event: any) {
    if (!event || typeof event !== "object") return;
    switch (event.type) {
      case "message_end":
        if (event.message) this._liveMessages.push(event.message);
        break;
      case "agent_end":
        if (Array.isArray(event.messages)) this._liveMessages = event.messages.slice();
        break;
      default:
        break;
    }
  }
  subscribe(listener: (event: unknown) => void) {
    if (typeof listener === "function") {
      const subs = this._listenerSet();
      if (subs) subs.add(listener);
    }
    return () => {
      const subs = (this._bridge ?? getBridge())?._agentSessionListeners?.get(this._sessionId);
      if (subs) subs.delete(listener);
    };
  }
  async prompt(text: unknown, promptOptions: Record<string, unknown> = {}) {
    const bridge = this.bridge!;
    const action = bridge.actions.agentSessionPrompt ?? "agentSessionPrompt";
    const result: any = await bridge.runtime(action, { sessionId: this._sessionId, content: String(text ?? ""), options: promptOptions });
    if (result?.ok === false) throw new Error(result.error ?? "session.prompt failed.");
    if (result?.value?.session) {
      this._sessionSnapshot = result.value.session;
      if (Array.isArray(result.value.session.messages)) this._liveMessages = result.value.session.messages.slice();
    }
    return result?.value ?? result;
  }
  abort() { const b = this.bridge!; return b.runtime(b.actions.agentSessionAbort ?? "agentSessionAbort", { sessionId: this._sessionId }); }
  steer(text: unknown) { const b = this.bridge!; return b.runtime(b.actions.agentSessionSteer ?? "agentSessionSteer", { sessionId: this._sessionId, content: String(text ?? "") }); }
  followUp(text: unknown) { const b = this.bridge!; return b.runtime(b.actions.agentSessionFollowUp ?? "agentSessionFollowUp", { sessionId: this._sessionId, content: String(text ?? "") }); }
  compact(customInstructions?: unknown) { const b = this.bridge!; return b.runtime(b.actions.agentSessionCompact ?? "agentSessionCompact", { sessionId: this._sessionId, instructions: customInstructions }); }
  setModel(model: unknown) { const b = this.bridge!; return b.runtime(b.actions.agentSessionSetModel ?? "agentSessionSetModel", { sessionId: this._sessionId, model }); }
  setThinkingLevel(level: unknown) { const b = this.bridge!; return b.runtime(b.actions.agentSessionSetThinkingLevel ?? "agentSessionSetThinkingLevel", { sessionId: this._sessionId, level }); }
  getSessionName() { return this._sessionSnapshot?.sessionName ?? this.bridge?.snapshot?.sessionName; }
  async setSessionName(name: unknown) { const b = this.bridge!; await b.runtime(b.actions.setSessionName ?? "setSessionName", { name }); }
  async bindExtensions(_bindings?: unknown) {}
  dispose() {
    const bridge = this._bridge ?? getBridge();
    if (bridge?._agentSessionListeners) bridge._agentSessionListeners.delete(this._sessionId);
    this._listeners.clear();
    if (this._sessionId && bridge) {
      const action = bridge.actions.agentSessionDispose ?? "agentSessionDispose";
      try { return bridge.runtime(action, { sessionId: this._sessionId }); } catch { /* ignore */ }
    }
    return undefined;
  }
}
