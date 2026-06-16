// test/shim-pin.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import os from "node:os";
import path from "node:path";
import { mkdtemp, rm } from "node:fs/promises";

// NOTE: Task 11 repoints this import to "../dist/shims/materialize.js".
import { materializeModuleShims } from "../dist/shims/materialize.js";

const MANIFEST = {
  schemaVersion: 1,
  moduleShims: [
    {
      specifier: "@pin/all-kinds",
      cacheFileName: "pin-all-kinds.mjs",
      exports: [
        { name: "TypeString", kind: "helper", helper: "typeString" },
        { name: "AgentSession", kind: "helper", helper: "AgentSession" },
        { name: "MAX", kind: "json-const", value: 42 },
        { name: "gone", kind: "unavailable-function", message: "nope" },
        { name: "goneAsync", kind: "async-unavailable-function", message: "nope-async" },
        { name: "callRuntime", kind: "runtime-function", runtimeAction: "pin.action" },
        {
          name: "ns",
          kind: "namespace",
          members: [
            { name: "str", kind: "helper", helper: "typeString" },
            { name: "lit", kind: "json-const", value: "x" },
          ],
        },
      ],
    },
  ],
};

async function importGeneratedShim() {
  const cacheDirectory = await mkdtemp(path.join(os.tmpdir(), "shim-pin-"));
  const urls = await materializeModuleShims({ manifest: MANIFEST, cacheDirectory });
  const url = urls.get("@pin/all-kinds");
  assert.ok(url, "shim URL should be registered for the specifier");
  const mod = await import(url);
  return { mod, cacheDirectory };
}

test("pin: helper exports resolve to real helper values", async () => {
  const { mod, cacheDirectory } = await importGeneratedShim();
  try {
    assert.deepEqual(mod.TypeString(), { type: "string" });
    assert.equal(typeof mod.AgentSession, "function"); // class
  } finally {
    await rm(cacheDirectory, { recursive: true, force: true });
  }
});

test("pin: json-const, unavailable, and namespace behave", async () => {
  const { mod, cacheDirectory } = await importGeneratedShim();
  try {
    assert.equal(mod.MAX, 42);
    assert.throws(() => mod.gone(), /nope/);
    await assert.rejects(async () => mod.goneAsync(), /nope-async/);
    assert.deepEqual(mod.ns.str(), { type: "string" });
    assert.equal(mod.ns.lit, "x");
  } finally {
    await rm(cacheDirectory, { recursive: true, force: true });
  }
});
