// src/shims/runtime/sdk.ts
import { requireBridge } from "./bridge.js";
import { AgentSession } from "./session.js";

export const completeSimple = async (model: unknown, context: unknown, options: Record<string, unknown> = {}) => {
  const bridge = requireBridge("completeSimple");
  const action = bridge.actions.completeSimple ?? "completeSimple";
  const result: any = await bridge.runtime(action, { model, context, options });
  if (result?.ok === false) throw new Error(result.error ?? "completeSimple failed.");
  return result?.value ?? result;
};

export const createAgentSession = async (options: Record<string, unknown> = {}) => {
  const bridge = requireBridge("createAgentSession");
  const action = bridge.actions.createAgentSession ?? "createAgentSession";
  const result: any = await bridge.runtime(action, { options });
  if (result?.ok === false) throw new Error(result.error ?? "createAgentSession failed.");
  const data = result?.value ?? result ?? {};
  const session = new AgentSession(bridge, data.sessionId, data.session);
  return { session, extensionsResult: data.extensionsResult ?? { extensions: [], diagnostics: [] }, modelFallbackMessage: data.modelFallbackMessage };
};
