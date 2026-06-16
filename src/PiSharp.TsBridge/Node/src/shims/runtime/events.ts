// src/shims/runtime/events.ts
type ToolEvent = {
  toolName?: unknown;
  tool_name?: unknown;
  name?: unknown;
  tool?: { name?: unknown };
};

export const isToolCallEventType = (toolName: unknown, event: ToolEvent | null | undefined) =>
  (event?.toolName ?? event?.tool_name ?? event?.name ?? event?.tool?.name) === toolName;
export const isEditToolResult = (event: ToolEvent | null | undefined) =>
  event?.toolName === "edit" || event?.tool_name === "edit" || event?.name === "edit";
export const isWriteToolResult = (event: ToolEvent | null | undefined) =>
  event?.toolName === "write" || event?.tool_name === "write" || event?.name === "write";
