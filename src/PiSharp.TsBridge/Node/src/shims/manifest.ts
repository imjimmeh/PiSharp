export const MANIFEST_SCHEMA_VERSION = 1;

export type ShimExportKind =
  | "helper"
  | "json-const"
  | "unavailable-function"
  | "async-unavailable-function"
  | "runtime-function"
  | "namespace";

export interface BridgeManifest {
  schemaVersion: number;
  moduleShims?: ModuleShim[] | null;
}

export interface ModuleShim {
  specifier: string;
  cacheFileName: string;
  exports?: ShimExport[] | null;
}

export interface ShimExport {
  name: string;
  kind: ShimExportKind;
  helper?: string | null;
  message?: string | null;
  value?: unknown;
  members?: ShimExport[] | null;
  runtimeAction?: string | null;
}

export function validateManifest(manifest: BridgeManifest): void {
  if (!manifest || typeof manifest !== "object") throw new Error("Bridge manifest is missing.");
  if (manifest.schemaVersion !== MANIFEST_SCHEMA_VERSION)
    throw new Error(`Unsupported bridge manifest schemaVersion ${manifest.schemaVersion}.`);
  if (!Array.isArray(manifest.moduleShims)) throw new Error("Bridge manifest moduleShims must be an array.");
}

export function validateCacheFileName(cacheFileName: string): void {
  if (typeof cacheFileName !== "string" || !cacheFileName.endsWith(".mjs"))
    throw new Error("Shim cacheFileName must be a .mjs file name.");
  if (cacheFileName.includes("/") || cacheFileName.includes("\\") || cacheFileName.split(/[\\/]+/).includes(".."))
    throw new Error(`Unsafe shim cacheFileName '${cacheFileName}'.`);
}

export function validateIdentifier(value: unknown, label: string): string {
  if (typeof value !== "string" || !/^[A-Za-z_$][\w$]*$/.test(value))
    throw new Error(`Invalid ${label} '${String(value)}'.`);
  return value;
}
