// src/shims/runtime/resources.ts
export class DefaultResourceLoader {
  constructor(_opts?: unknown) {}
  getExtensions() { return { extensions: [], diagnostics: [] }; }
  getSkills() { return { skills: [], diagnostics: [] }; }
  getPrompts() { return { prompts: [], diagnostics: [] }; }
  getThemes() { return { themes: [], diagnostics: [] }; }
  getAgentsFiles() { return { agentsFiles: [] }; }
  getSystemPrompt(): undefined { return undefined; }
  getAppendSystemPrompt() { return []; }
  extendResources(_paths: unknown) {}
  async reload() {}
}
