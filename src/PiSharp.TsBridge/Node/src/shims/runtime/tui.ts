// src/shims/runtime/tui.ts
export class Text {
  text: string;
  x: number;
  y: number;
  constructor(text: unknown = "", x = 0, y = 0) {
    this.text = String(text ?? "");
    this.x = x;
    this.y = y;
  }
  setText(text: unknown): void {
    this.text = String(text ?? "");
  }
  render(): string[] {
    return this.text.split(/\r?\n/);
  }
}

export class Container {
  children: unknown[];
  constructor(children: unknown[] = []) {
    this.children = Array.isArray(children) ? children : [];
  }
  add(child: unknown): unknown {
    this.children.push(child);
    return child;
  }
  render(width?: number): string[] {
    return this.children.flatMap((child) =>
      child && typeof (child as { render?: unknown }).render === "function"
        ? (child as { render: (w?: number) => string[] }).render(width)
        : String(child ?? ""),
    );
  }
}

export class Spacer {
  render(): string[] {
    return [""];
  }
}

export class Focusable {}
export class TUI {}

export class DynamicBorder {
  options: Record<string, unknown>;
  constructor(options: Record<string, unknown> = {}) {
    this.options = options;
  }
  render(): string[] {
    return [];
  }
}

export class SettingsList extends Container {}
export class Input extends Text {}
