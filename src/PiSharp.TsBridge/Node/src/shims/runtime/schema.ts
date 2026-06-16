// src/shims/runtime/schema.ts
type Options = Record<string, unknown>;

function schema(type: string, options: Options = {}): Record<string, unknown> {
  return { type, ...options };
}

export function objectSchema(properties: Record<string, unknown> = {}, options: Options = {}): Record<string, unknown> {
  const normalized: Record<string, unknown> = {};
  const required: string[] = [];
  for (const [key, value] of Object.entries(properties ?? {})) {
    const { optional, ...property } = value && typeof value === "object" ? (value as Record<string, unknown>) : {};
    normalized[key] = property;
    if (optional !== true) required.push(key);
  }
  return { type: "object", properties: normalized, ...(required.length ? { required } : {}), ...options };
}

export const undefinedValue = undefined;
export const typeAny = (options: Options = {}) => ({ ...options });
export const typeUnknown = (options: Options = {}) => ({ ...options });
export const typeNull = (options: Options = {}) => schema("null", options);
export const typeString = (options: Options = {}) => schema("string", options);
export const typeNumber = (options: Options = {}) => schema("number", options);
export const typeInteger = (options: Options = {}) => schema("integer", options);
export const typeBoolean = (options: Options = {}) => schema("boolean", options);
export const typeArray = (items: unknown = {}, options: Options = {}) => ({ type: "array", items, ...options });
export const typeObject = objectSchema;
export const typeRecord = (key: unknown = {}, value: unknown = {}, options: Options = {}) =>
  ({ type: "object", additionalProperties: value, propertyNames: key, ...options });
export const typeOptional = (value: Options = {}) => ({ ...value, optional: true });
export const typeLiteral = (value: unknown, options: Options = {}) => ({ const: value, ...options });
export const typeUnion = (anyOf: unknown[] = [], options: Options = {}) => ({ anyOf, ...options });
export const typeIntersect = (allOf: unknown[] = [], options: Options = {}) => ({ allOf, ...options });
export const stringEnum = (values: Iterable<unknown>, options: Options = {}) =>
  ({ type: "string", enum: Array.from(values ?? []), ...options });
export const getSupportedThinkingLevels = () => ["off", "minimal", "low", "medium", "high", "xhigh"];
