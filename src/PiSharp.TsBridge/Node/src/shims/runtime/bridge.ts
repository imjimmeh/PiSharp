// src/shims/runtime/bridge.ts
export interface TsBridgeRuntime {
  runtime(action: string, payload: unknown): Promise<{ ok?: boolean; error?: string; value?: unknown } | unknown>;
  actions: Record<string, string>;
  snapshot?: Record<string, unknown>;
  _agentSessionListeners?: Map<string, Set<(event: unknown) => void>>;
}

export function getBridge(): TsBridgeRuntime | undefined {
  return (globalThis as { __pisharpTsBridge?: TsBridgeRuntime }).__pisharpTsBridge;
}

export function requireBridge(context: string): TsBridgeRuntime {
  const bridge = getBridge();
  if (!bridge?.runtime) {
    throw new Error(`${context} requires an active PiSharp TypeScript extension bridge runtime.`);
  }
  return bridge;
}
