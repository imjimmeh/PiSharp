// src/shims/runtime/index.ts
import * as schema from "./schema.js";
import * as text from "./text.js";
import * as tui from "./tui.js";
import * as events from "./events.js";
import * as misc from "./misc.js";
import { DefaultResourceLoader } from "./resources.js";
import { SessionManager, SettingsManager, AgentSession } from "./session.js";
import { completeSimple, createAgentSession } from "./sdk.js";

export const helpers = {
  undefinedValue: schema.undefinedValue,
  typeAny: schema.typeAny,
  typeUnknown: schema.typeUnknown,
  typeNull: schema.typeNull,
  typeString: schema.typeString,
  typeNumber: schema.typeNumber,
  typeInteger: schema.typeInteger,
  typeBoolean: schema.typeBoolean,
  typeArray: schema.typeArray,
  typeObject: schema.typeObject,
  typeRecord: schema.typeRecord,
  typeOptional: schema.typeOptional,
  typeLiteral: schema.typeLiteral,
  typeUnion: schema.typeUnion,
  typeIntersect: schema.typeIntersect,
  stringEnum: schema.stringEnum,
  getSupportedThinkingLevels: schema.getSupportedThinkingLevels,
  visibleWidth: text.visibleWidth,
  truncateToWidth: text.truncateToWidth,
  wrapTextWithAnsi: text.wrapTextWithAnsi,
  fuzzyFilter: text.fuzzyFilter,
  matchesKey: text.matchesKey,
  Text: tui.Text,
  Container: tui.Container,
  Spacer: tui.Spacer,
  SettingsList: tui.SettingsList,
  Focusable: tui.Focusable,
  Input: tui.Input,
  TUI: tui.TUI,
  isToolCallEventType: events.isToolCallEventType,
  isEditToolResult: events.isEditToolResult,
  isWriteToolResult: events.isWriteToolResult,
  getAgentDir: misc.getAgentDir,
  truncateTail: text.truncateTail,
  truncateHead: text.truncateHead,
  formatSize: text.formatSize,
  parseFrontmatter: text.parseFrontmatter,
  stripFrontmatter: text.stripFrontmatter,
  parseSkillBlock: text.parseSkillBlock,
  identityOrEmptyObject: misc.identityOrEmptyObject,
  identity: misc.identity,
  stringValue: misc.stringValue,
  emptyObjectFunction: misc.emptyObjectFunction,
  emptyArrayFunction: misc.emptyArrayFunction,
  DynamicBorder: tui.DynamicBorder,
  DefaultResourceLoader,
  SessionManager,
  SettingsManager,
  AgentSession,
  completeSimple,
  createAgentSession,
} as const;

export type HelperName = keyof typeof helpers;

export const helperNames: readonly HelperName[] = Object.keys(helpers) as HelperName[];
