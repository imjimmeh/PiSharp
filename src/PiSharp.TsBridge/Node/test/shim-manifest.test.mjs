import { test } from "node:test";
import assert from "node:assert/strict";
import {
  validateManifest, validateCacheFileName, validateIdentifier, MANIFEST_SCHEMA_VERSION,
} from "../dist/shims/manifest.js";

test("manifest: accepts a valid manifest", () => {
  assert.doesNotThrow(() => validateManifest({ schemaVersion: MANIFEST_SCHEMA_VERSION, moduleShims: [] }));
});

test("manifest: rejects wrong schema version and non-array moduleShims", () => {
  assert.throws(() => validateManifest({ schemaVersion: 999, moduleShims: [] }), /schemaVersion 999/);
  assert.throws(() => validateManifest({ schemaVersion: 1, moduleShims: null }), /must be an array/);
});

test("manifest: cacheFileName must be .mjs and reject traversal", () => {
  assert.doesNotThrow(() => validateCacheFileName("ok.mjs"));
  assert.throws(() => validateCacheFileName("ok.js"), /\.mjs file name/);
  assert.throws(() => validateCacheFileName("../escape.mjs"), /Unsafe/);
  assert.throws(() => validateCacheFileName("nested/escape.mjs"), /Unsafe/);
});

test("manifest: validateIdentifier rejects invalid names", () => {
  assert.equal(validateIdentifier("okName$", "x"), "okName$");
  assert.throws(() => validateIdentifier("1bad", "x"), /Invalid x/);
});
