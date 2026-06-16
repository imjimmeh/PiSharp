import { test } from "node:test";
import assert from "node:assert/strict";
import os from "node:os";
import path from "node:path";
import { readFile, mkdtemp, rm, readdir } from "node:fs/promises";
import { materializeModuleShims } from "../dist/shims/materialize.js";

const MANIFEST = {
  schemaVersion: 1,
  moduleShims: [{
    specifier: "@m/t",
    cacheFileName: "mt.mjs",
    exports: [
      { name: "TypeString", kind: "helper", helper: "typeString" },
      { name: "MAX", kind: "json-const", value: 9 },
    ],
  }],
};

test("materialize: generated shim imports helpers by file url and resolves", async () => {
  const cacheDirectory = await mkdtemp(path.join(os.tmpdir(), "shim-mat-"));
  try {
    const urls = await materializeModuleShims({ manifest: MANIFEST, cacheDirectory });
    const url = urls.get("@m/t");
    assert.ok(url);

    const files = await readdir(cacheDirectory);
    const src = await readFile(path.join(cacheDirectory, files[0]), "utf8");
    assert.match(src, /import \{ helpers \} from "file:\/\/.*runtime\/index\.js";/);

    const mod = await import(url);
    assert.deepEqual(mod.TypeString(), { type: "string" });
    assert.equal(mod.MAX, 9);
  } finally {
    await rm(cacheDirectory, { recursive: true, force: true });
  }
});

test("materialize: identical manifest is idempotent (single cache file)", async () => {
  const cacheDirectory = await mkdtemp(path.join(os.tmpdir(), "shim-mat2-"));
  try {
    await materializeModuleShims({ manifest: MANIFEST, cacheDirectory });
    await materializeModuleShims({ manifest: MANIFEST, cacheDirectory });
    const files = await readdir(cacheDirectory);
    assert.equal(files.length, 1);
  } finally {
    await rm(cacheDirectory, { recursive: true, force: true });
  }
});
