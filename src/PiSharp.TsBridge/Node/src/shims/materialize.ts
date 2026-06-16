// src/shims/materialize.ts
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath, pathToFileURL } from "node:url";
import { generateModuleShimSource } from "./codegen.js";
import { validateManifest, validateCacheFileName, type BridgeManifest } from "./manifest.js";

// Resolved at runtime from this module's own location, so it is correct no
// matter where the bridge is installed. Never bake an absolute path at build time.
const HELPER_MODULE_URL = new URL("./runtime/index.js", import.meta.url).href;

export async function materializeModuleShims({
  manifest,
  cacheDirectory,
}: {
  manifest: BridgeManifest;
  cacheDirectory?: string | null;
}): Promise<Map<string, string>> {
  validateManifest(manifest);
  const root = cacheDirectory || path.join(os.tmpdir(), "pisharp-ts-bridge-cache");
  const shimUrls = new Map<string, string>();
  for (const moduleShim of manifest.moduleShims ?? []) {
    validateCacheFileName(moduleShim.cacheFileName);
    const source = generateModuleShimSource(moduleShim, HELPER_MODULE_URL);
    const hash = createHash("sha256").update(source).digest("hex").slice(0, 12);
    const baseName = moduleShim.cacheFileName.replace(/\.mjs$/, "");
    const hashedFileName = `${baseName}.${hash}.mjs`;
    const shimPath = path.join(root, hashedFileName);
    let exists = true;
    try { await fs.access(shimPath); } catch { exists = false; }
    if (!exists) {
      await fs.mkdir(path.dirname(shimPath), { recursive: true });
      await fs.writeFile(shimPath, source, "utf8");
    }
    shimUrls.set(moduleShim.specifier, pathToFileURL(shimPath).href);
  }
  return shimUrls;
}

// `fileURLToPath` retained for callers that may need the on-disk helper path.
export const helperModulePath = fileURLToPath(HELPER_MODULE_URL);
