// src/shims/runtime/misc.ts
import os from "node:os";
import path from "node:path";

export const getAgentDir = () => path.join(os.homedir(), ".pi", "agent");
export const identityOrEmptyObject = (value: unknown) => value ?? {};
export const identity = (value: unknown) => value;
export const stringValue = (value: unknown) => String(value ?? "");
export const emptyObjectFunction = () => ({});
export const emptyArrayFunction = () => [];
