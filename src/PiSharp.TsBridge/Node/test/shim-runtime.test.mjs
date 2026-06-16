// test/shim-runtime.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";

import * as schema from "../dist/shims/runtime/schema.js";

test("schema: objectSchema marks optional members as not required", () => {
  const result = schema.objectSchema({ a: schema.typeString(), b: schema.typeOptional(schema.typeNumber()) });
  assert.deepEqual(result.required, ["a"]);
  assert.equal(result.properties.b.optional, undefined); // optional flag stripped
});

test("schema: stringEnum copies values", () => {
  assert.deepEqual(schema.stringEnum(["x", "y"]), { type: "string", enum: ["x", "y"] });
});

test("schema: getSupportedThinkingLevels is the fixed list", () => {
  assert.deepEqual(schema.getSupportedThinkingLevels(), ["off", "minimal", "low", "medium", "high", "xhigh"]);
});

import * as text from "../dist/shims/runtime/text.js";

test("text: stripAnsi removes color codes", () => {
  assert.equal(text.stripAnsi("\u001b[31mred\u001b[0m"), "red");
});

test("text: truncateToWidth respects visible width", () => {
  assert.equal(text.truncateToWidth("hello", 3), "hel");
  assert.equal(text.truncateToWidth("hi", 10), "hi");
});

test("text: parseFrontmatter splits frontmatter and body", () => {
  const result = text.parseFrontmatter("---\nname: demo\nflag: true\n---\nbody here");
  assert.equal(result.data.name, "demo");
  assert.equal(result.data.flag, true);
  assert.equal(result.body, "body here");
});

test("text: formatSize formats units", () => {
  assert.equal(text.formatSize(512), "512 B");
  assert.equal(text.formatSize(2048), "2.0 KB");
});

import * as tui from "../dist/shims/runtime/tui.js";

test("tui: Text.render splits lines", () => {
  assert.deepEqual(new tui.Text("a\nb").render(), ["a", "b"]);
});

test("tui: Container renders nested children", () => {
  const c = new tui.Container();
  c.add(new tui.Text("x"));
  c.add(new tui.Spacer());
  assert.deepEqual(c.render(), ["x", ""]);
});

test("tui: SettingsList and Input extend their bases", () => {
  assert.ok(new tui.SettingsList() instanceof tui.Container);
  assert.ok(new tui.Input() instanceof tui.Text);
});

import * as events from "../dist/shims/runtime/events.js";
import * as misc from "../dist/shims/runtime/misc.js";
import { DefaultResourceLoader } from "../dist/shims/runtime/resources.js";

test("events: predicates read tool name across shapes", () => {
  assert.equal(events.isToolCallEventType("edit", { tool_name: "edit" }), true);
  assert.equal(events.isEditToolResult({ name: "edit" }), true);
  assert.equal(events.isWriteToolResult({ toolName: "write" }), true);
});

test("misc: identity/empty helpers", () => {
  assert.equal(misc.identity(5), 5);
  assert.deepEqual(misc.emptyObjectFunction(), {});
  assert.deepEqual(misc.emptyArrayFunction(), []);
});

test("resources: DefaultResourceLoader returns empty buckets", () => {
  const loader = new DefaultResourceLoader();
  assert.deepEqual(loader.getSkills(), { skills: [], diagnostics: [] });
});

import { SessionManager, AgentSession } from "../dist/shims/runtime/session.js";
import * as sdk from "../dist/shims/runtime/sdk.js";

test("session: SessionManager.inMemory is not persisted", () => {
  const sm = SessionManager.inMemory("/tmp/x");
  assert.equal(sm.isPersisted(), false);
  assert.equal(sm.getCwd(), "/tmp/x");
});

test("session: AgentSession seeds live messages from snapshot", () => {
  const s = new AgentSession(undefined, "sid", { messages: [{ id: 1 }] });
  assert.deepEqual(s.messages, [{ id: 1 }]);
});

test("sdk: completeSimple without bridge throws", async () => {
  await assert.rejects(() => sdk.completeSimple("m", {}), /active PiSharp TypeScript extension bridge runtime/);
});

import { helpers, helperNames } from "../dist/shims/runtime/index.js";

const LEGACY_KNOWN_HELPERS = [
  "undefinedValue","typeAny","typeUnknown","typeNull","typeString","typeNumber",
  "typeInteger","typeBoolean","typeArray","typeObject","typeRecord","typeOptional",
  "typeLiteral","typeUnion","typeIntersect","stringEnum","getSupportedThinkingLevels",
  "visibleWidth","truncateToWidth","wrapTextWithAnsi","fuzzyFilter","matchesKey",
  "Text","Container","Spacer","SettingsList","Focusable","Input","TUI",
  "isToolCallEventType","isEditToolResult","isWriteToolResult","getAgentDir",
  "truncateTail","truncateHead","formatSize","parseFrontmatter","stripFrontmatter",
  "parseSkillBlock","identityOrEmptyObject","identity","stringValue",
  "emptyObjectFunction","emptyArrayFunction","DynamicBorder","DefaultResourceLoader",
  "SessionManager","SettingsManager","AgentSession","completeSimple","createAgentSession",
];

test("index: helperNames matches the legacy knownHelpers set exactly", () => {
  assert.deepEqual([...helperNames].sort(), [...LEGACY_KNOWN_HELPERS].sort());
});

test("index: helpers object is keyed by helperNames", () => {
  assert.deepEqual(Object.keys(helpers).sort(), [...LEGACY_KNOWN_HELPERS].sort());
});
