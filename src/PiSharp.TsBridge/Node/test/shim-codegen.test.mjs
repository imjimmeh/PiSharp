import { test } from "node:test";
import assert from "node:assert/strict";
import { generateModuleShimSource } from "../dist/shims/codegen.js";

const URL = "file:///fake/runtime/index.js";

test("codegen: emits a single helpers import from the given url", () => {
  const src = generateModuleShimSource(
    { specifier: "@x/y", cacheFileName: "y.mjs", exports: [{ name: "A", kind: "helper", helper: "typeString" }] },
    URL,
  );
  assert.match(src, /import \{ helpers \} from "file:\/\/\/fake\/runtime\/index\.js";/);
  assert.match(src, /export const A = helpers\.typeString;/);
});

test("codegen: each export kind renders", () => {
  const src = generateModuleShimSource(
    {
      specifier: "@x/kinds", cacheFileName: "k.mjs",
      exports: [
        { name: "C", kind: "json-const", value: 7 },
        { name: "u", kind: "unavailable-function", message: "no" },
        { name: "r", kind: "runtime-function", runtimeAction: "do.it" },
        { name: "ns", kind: "namespace", members: [{ name: "s", kind: "helper", helper: "typeString" }] },
      ],
    },
    URL,
  );
  assert.match(src, /export const C = 7;/);
  assert.match(src, /export function u\(\) \{ throw new Error\("no"\); \}/);
  assert.match(src, /export async function r\(\.\.\.args\)/);
  assert.match(src, /export const ns = \{\n {2}s: helpers\.typeString,\n\};/);
});

test("codegen: unknown helper is rejected", () => {
  assert.throws(
    () => generateModuleShimSource({ specifier: "@x/z", cacheFileName: "z.mjs", exports: [{ name: "B", kind: "helper", helper: "nope" }] }, URL),
    /Unknown shim helper 'nope'/,
  );
});

test("codegen: namespace member cannot be a runtime-function", () => {
  assert.throws(
    () => generateModuleShimSource({ specifier: "@x/n", cacheFileName: "n.mjs", exports: [{ name: "ns", kind: "namespace", members: [{ name: "r", kind: "runtime-function", runtimeAction: "x" }] }] }, URL),
    /must be top-level/,
  );
});
