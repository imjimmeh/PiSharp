// src/shims/runtime/text.ts
export function stripAnsi(text: unknown): string {
  // eslint-disable-next-line no-control-regex
  return String(text ?? "").replace(/\x1b\[[0-9;]*m/g, "");
}

function coerceFrontmatterValue(value: unknown): string | number | boolean {
  const trimmed = String(value ?? "").trim();
  if (trimmed === "true") return true;
  if (trimmed === "false") return false;
  if (/^-?\d+(?:\.\d+)?$/.test(trimmed)) return Number(trimmed);
  if ((trimmed.startsWith('"') && trimmed.endsWith('"')) || (trimmed.startsWith("'") && trimmed.endsWith("'"))) return trimmed.slice(1, -1);
  return trimmed;
}

function parseFrontmatterFields(text: unknown): Record<string, string | number | boolean> {
  const data: Record<string, string | number | boolean> = {};
  for (const line of String(text ?? "").split(/\r?\n/)) {
    const match = line.match(/^\s*([A-Za-z0-9_-]+)\s*:\s*(.*?)\s*$/);
    if (match && match[1] !== undefined) data[match[1]] = coerceFrontmatterValue(match[2]);
  }
  return data;
}

export const visibleWidth = (text: unknown) => stripAnsi(text).length;
export const truncateToWidth = (text: unknown, width: unknown) => {
  const source = String(text ?? "");
  const limit = Number(width ?? 0);
  return limit > 0 && stripAnsi(source).length > limit ? stripAnsi(source).slice(0, limit) : source;
};
export const wrapTextWithAnsi = (text: unknown) => String(text ?? "").split(/\r?\n/);
export const fuzzyFilter = (
  items: Iterable<unknown>,
  query: unknown,
  selector: (value: unknown) => string = (value) => String(value ?? ""),
) => {
  const needle = String(query ?? "").toLowerCase();
  return Array.from(items ?? []).filter((item) => selector(item).toLowerCase().includes(needle));
};
export const matchesKey = (input: unknown, key: unknown) =>
  String(input ?? "").toLowerCase() === String(key ?? "").toLowerCase();
export const truncateTail = (value: unknown, maxLength = 2000) => {
  const text = String(value ?? "");
  return text.length <= maxLength ? text : text.slice(Math.max(0, text.length - maxLength));
};
export const truncateHead = (value: unknown, maxLength = 2000) => {
  const text = String(value ?? "");
  return text.length <= maxLength ? text : text.slice(0, maxLength);
};
export const formatSize = (bytes: unknown) => {
  const value = Number(bytes ?? 0);
  if (value < 1024) return String(value) + " B";
  if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " KB";
  return (value / 1024 / 1024).toFixed(1) + " MB";
};
export const parseFrontmatter = (text: unknown) => {
  const source = String(text ?? "");
  if (!source.startsWith("---\n")) return { frontmatter: {}, body: source, data: {}, content: source };
  const end = source.indexOf("\n---", 4);
  if (end < 0) return { frontmatter: {}, body: source, data: {}, content: source };
  const frontmatterText = source.slice(4, end);
  const body = source.slice(end + 4).replace(/^\r?\n/, "");
  const frontmatter = parseFrontmatterFields(frontmatterText);
  return { frontmatter, body, data: frontmatter, content: body };
};
export const stripFrontmatter = (text: unknown) => parseFrontmatter(text).body;
export const parseSkillBlock = (text: unknown) => ({ name: undefined, content: String(text ?? "") });
