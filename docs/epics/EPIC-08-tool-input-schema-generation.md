---
epic_id: EPIC-08
title: Tool Input JSON Schema Generation
status: proposed
priority: high
owner: unassigned
created: 2026-05-26
updated: 2026-05-26
target_version: backlog
related_docs:
  - ../specs/PRD-pi-csharp-port.md
  - ../specs/SDD-pi-csharp-port.md
  - ./EPIC-04-built-in-tools.md
related_code:
  - ../../src/PiSharp.Tools/ToolSchemas.cs
  - ../../src/PiSharp.Tools/JsonTool.cs
  - ../../src/PiSharp.Tools/Bash/BashTool.cs
  - ../../src/PiSharp.Tools/Files/ReadTool.cs
  - ../../src/PiSharp.Tools/Files/WriteTool.cs
  - ../../src/PiSharp.Tools/Edit/EditTool.cs
  - ../../src/PiSharp.Tools/Search/FindTool.cs
  - ../../src/PiSharp.Tools/Search/GrepTool.cs
  - ../../src/PiSharp.Tools/Search/LsTool.cs
  - ../../src/PiSharp.Ai/Providers/Shared/ProviderToolSerializer.cs
  - ../../tests/PiSharp.Tools.Tests/BuiltInToolsTests.cs
  - ../../tests/PiSharp.Ai.Tests/Shared/ProviderToolSerializationTests.cs
decision_summary: Generate tool input JSON schemas from typed input records using System.Text.Json schema export plus attribute metadata, replacing duplicated handwritten schema definitions.
tags:
  - tools
  - json-schema
  - system-text-json
  - refactor
  - providers
---

# EPIC-08: Tool Input JSON Schema Generation

## 1. Background And Context

### 1.1 Current Implementation

Built-in tools currently define tool input shape twice:

1. A typed C# input record used at execution time.
2. A manually authored `JsonElement` schema exposed to LLM providers.

Example from the Bash tool:

```csharp
public sealed record BashToolInput(string Command, double? Timeout = null);
```

and separately:

```csharp
private static readonly JsonElement Schema = ToolSchemas.Object(
    new Dictionary<string, JsonElement>
    {
        ["command"] = ToolSchemas.String("Bash command to execute"),
        ["timeout"] = ToolSchemas.Number("Timeout in seconds (optional, no default timeout)")
    },
    ["command"]);
```

This pattern exists across the built-in tools under `src/PiSharp.Tools`.

### 1.2 Why This Is Risky Long-Term

The current approach creates schema drift risk:

- Input records and provider schemas can silently diverge.
- Required/optional status is duplicated manually.
- Property naming conventions are duplicated manually.
- Descriptions live outside the input contract they document.
- New tool authors must understand both typed deserialization and JSON Schema authoring.

The result is unnecessary boilerplate and a higher chance of provider-facing tool schema bugs.

### 1.3 System-Level Importance

Tool schemas are sent directly to model providers and influence tool-call reliability. They are used by provider serializers for:

- Anthropic `input_schema`
- OpenAI tool/function `parameters`
- Google function declarations
- Bedrock tool config
- Mistral tool/function `parameters`

A schema-generation change must therefore preserve provider compatibility while reducing duplication.

## 2. Problem Statement

Refactor tool input schema authoring so that:

1. The typed input record is the source of truth for shape, required properties, nullability, and JSON property naming.
2. Descriptions can live on input record parameters/properties via attributes.
3. Provider-facing schemas remain compatible with all currently supported LLM providers.
4. Existing built-in tools no longer need duplicated handwritten `ToolSchemas.Object(...)` definitions for ordinary input records.
5. Extension-provided TypeScript schemas remain supported unchanged.

## 3. Goals And Non-Goals

### 3.1 Goals

- Add a schema generator based on `System.Text.Json.Schema.JsonSchemaExporter`.
- Support `[Description]` metadata on record parameters/properties.
- Use existing `System.Text.Json` naming behavior, especially camelCase from `JsonSerializerDefaults.Web`.
- Infer required properties from required constructor parameters.
- Preserve STJ-style nullable property schemas where provider-compatible, for example `"type": ["number", "null"]`.
- Ensure generated schemas work with every provider serializer currently in the repo.
- Migrate all built-in tool input schemas to generated schemas where practical.
- Keep `IAgentTool.ParametersSchema` as `JsonElement` so provider and bridge contracts remain stable.

### 3.2 Non-Goals

- No redesign of `IAgentTool` or `IAgentTool<TParameters, TDetails>`.
- No change to TypeScript extension tool schema contracts.
- No conversion to OpenAPI.
- No adoption of a large third-party schema library unless built-in STJ proves incompatible.
- No broad rewrite of tool execution logic.
- No behavior change to tool argument preparation or deserialization beyond schema metadata generation.

## 4. Options Considered

### Option A: Keep Handwritten `ToolSchemas` Definitions

Description:

- Continue manually defining every tool input schema using `ToolSchemas.Object`, `ToolSchemas.String`, `ToolSchemas.Number`, and related helpers.

Pros:

- No migration work.
- Schemas remain compact and provider-tested as-is.
- Full manual control over provider-facing JSON.

Cons:

- Duplicates input shape and schema shape.
- Easy for records and schemas to drift.
- Burdens every new tool with boilerplate.
- Descriptions are disconnected from the input members they describe.

Decision:

- Rejected.

### Option B: Write A Custom Reflection-Based Schema Generator

Description:

- Reflect over record constructor parameters/properties and generate the limited JSON Schema subset needed for tool inputs.

Pros:

- Full control over emitted schema shape.
- Could exactly match current handwritten output.
- No external dependencies.

Cons:

- Reimplements serializer naming/nullability/required inference behavior.
- Higher maintenance cost as input types become more complex.
- Risk of drifting from `System.Text.Json` behavior used during deserialization.

Decision:

- Rejected unless STJ schema export proves insufficient.

### Option C: Use `System.Text.Json.Schema.JsonSchemaExporter` (Chosen)

Description:

- Generate tool input schemas from input records using built-in .NET schema export.
- Use `JsonSerializerOptions(JsonSerializerDefaults.Web)` to align schema names with runtime deserialization.
- Use `TransformSchemaNode` to add `[Description]` metadata and any minimal provider-safety tweaks.

Pros:

- No new package dependency.
- Aligned with `System.Text.Json` contracts already used by `JsonTool<TParameters, TDetails>`.
- Supports nullable annotations and required constructor parameters through serializer options.
- Allows metadata injection through attributes.
- Lower custom code surface than a bespoke reflection generator.

Cons:

- Raw STJ schema output may include JSON Schema constructs that some provider subsets might not accept.
- Requires provider compatibility tests for nullable unions, defaults, and root object shape.
- Requires .NET 10 schema APIs.

Decision:

- Accepted.

### Option D: Use A Third-Party Library Such As NJsonSchema

Description:

- Add a lightweight external package to generate JSON Schema from C# types.

Pros:

- Mature schema-generation feature set.
- More knobs for complex schema customization.

Cons:

- Adds a dependency for a narrow use case.
- May not align exactly with `System.Text.Json` runtime behavior without extra configuration.
- Larger surface area than needed for simple tool input records.

Decision:

- Rejected for now.

## 5. Chosen Design

### 5.1 Core Shape

Extend `ToolSchemas` with generated-schema entrypoints:

- `ToolSchemas.FromType<T>()`
- `ToolSchemas.FromType(Type type)`

Generated schemas should use `System.Text.Json.Schema.JsonSchemaExporter` with serializer options aligned to `JsonTool<TParameters, TDetails>`:

- `JsonSerializerDefaults.Web`
- `DefaultJsonTypeInfoResolver`
- strict number handling
- nullable annotation awareness
- required constructor parameter awareness

### 5.2 Description Metadata

Tool input records should annotate parameters/properties with `System.ComponentModel.DescriptionAttribute`.

Preferred record style:

```csharp
public sealed record BashToolInput(
    [property: Description("Bash command to execute")]
    string Command,

    [property: Description("Timeout in seconds (optional, no default timeout)")]
    double? Timeout = null);
```

The schema exporter transform should copy `DescriptionAttribute.Description` into the generated JSON Schema node for that property.

### 5.3 Provider Compatibility Policy

Default policy:

- Keep STJ-generated schema output as close to raw as practical.
- Preserve STJ nullable property representation if providers accept it, for example:

```json
{
  "type": ["number", "null"],
  "default": null
}
```

Minimal safety policy:

- The root tool parameter schema must remain an object schema acceptable to provider tool APIs.
- If raw STJ output makes the root schema nullable, configure or transform it so the provider-facing root remains `"type": "object"`.
- Do not broadly normalize nullable property unions unless provider tests prove it is necessary.

### 5.4 Compatibility With Existing Tool Contracts

- `IAgentTool.ParametersSchema` remains `JsonElement`.
- `JsonTool<TParameters, TDetails>` can continue accepting a `JsonElement` schema through its constructor.
- TypeScript bridge tools keep using schemas supplied by extension definitions.
- Existing handwritten helpers in `ToolSchemas` can remain available for exceptional/custom schemas.

## 6. PR-Sized Task Breakdown

## PR-1: Add Generated Schema Helper And Unit Tests

### Overview

Add `ToolSchemas.FromType<T>()` and focused tests for schema generation from simple tool input records.

### Files In Scope

- `src/PiSharp.Tools/ToolSchemas.cs`
- `tests/PiSharp.Tools.Tests/ToolSchemaGenerationTests.cs` (new)

### Acceptance Criteria / Definition Of Done

- [ ] `ToolSchemas.FromType<T>()` generates a `JsonElement` schema for record input types.
- [ ] Generated property names use camelCase.
- [ ] Required constructor parameters appear in `required`.
- [ ] Optional/default constructor parameters are not required.
- [ ] `[Description]` attributes are emitted as `description` fields.
- [ ] Root schema remains provider-tool compatible as an object schema.

### Testing Criteria

- [ ] Add tests covering required string, optional nullable number, optional nullable boolean, and nested record/list inputs.
- [ ] Add snapshot-style assertions for the Bash-like input shape.
- [ ] Run `PiSharp.Tools.Tests`.

## PR-2: Verify Provider Schema Compatibility

### Overview

Add tests proving generated schemas serialize correctly through every supported provider serializer.

### Files In Scope

- `tests/PiSharp.Ai.Tests/Shared/ProviderToolSerializationTests.cs`
- `src/PiSharp.Ai/Providers/Shared/ProviderToolSerializer.cs` (only if provider-specific adaptation is required)

### Acceptance Criteria / Definition Of Done

- [ ] Anthropic payloads include generated schemas under `input_schema`.
- [ ] OpenAI Responses payloads include generated schemas under `parameters`.
- [ ] OpenAI Chat payloads include generated schemas under `function.parameters`.
- [ ] Google payloads include generated schemas under `functionDeclarations[].parameters`.
- [ ] Bedrock payloads include generated schemas under `toolSpec.inputSchema.json`.
- [ ] Mistral payloads include generated schemas under `function.parameters`.
- [ ] Nullable property unions/defaults are either accepted as-is by tests or documented with provider-specific adaptation.

### Testing Criteria

- [ ] Add provider serialization tests using a generated Bash-like schema.
- [ ] Assert provider payload JSON contains expected `command`, `timeout`, `required`, and `description` metadata.
- [ ] If a provider rejects raw STJ output in an integration/manual check, capture the failing shape and add a targeted compatibility transform.

## PR-3: Migrate Bash, Read, And Write Tool Schemas

### Overview

Migrate the simplest built-in tools from handwritten schemas to generated schemas.

### Files In Scope

- `src/PiSharp.Tools/Bash/BashTool.cs`
- `src/PiSharp.Tools/Files/ReadTool.cs`
- `src/PiSharp.Tools/Files/WriteTool.cs`
- `tests/PiSharp.Tools.Tests/BuiltInToolsTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] `BashToolInput`, `ReadToolInput`, and `WriteToolInput` include description attributes.
- [ ] Their tools use `ToolSchemas.FromType<TInput>()` instead of handwritten object schemas.
- [ ] Existing execution behavior remains unchanged.
- [ ] Existing TypeScript-compatible property names remain unchanged (`command`, `timeout`, `path`, `content`, etc.).

### Testing Criteria

- [ ] Existing Bash, Read, and Write tool tests remain green.
- [ ] Built-in schema compatibility test verifies expected property names remain present.

## PR-4: Migrate Search Tool Schemas

### Overview

Migrate `find`, `grep`, and `ls` schemas to generated schemas.

### Files In Scope

- `src/PiSharp.Tools/Search/FindTool.cs`
- `src/PiSharp.Tools/Search/GrepTool.cs`
- `src/PiSharp.Tools/Search/LsTool.cs`
- `tests/PiSharp.Tools.Tests/SearchAndLsToolTests.cs`
- `tests/PiSharp.Tools.Tests/BuiltInToolsTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] `FindToolInput`, `GrepToolInput`, and `LsToolInput` include description attributes.
- [ ] Search tools use generated schemas.
- [ ] Required/optional behavior matches current handwritten schemas.
- [ ] Existing tool argument names remain TypeScript-compatible.

### Testing Criteria

- [ ] Existing search and ls tests remain green.
- [ ] Schema assertions cover `pattern`, `glob`, `ignoreCase`, `literal`, `context`, and `limit`.

## PR-5: Migrate Edit Tool Nested Input Schema

### Overview

Migrate `EditToolInput` and nested `EditReplacement` schema generation.

### Files In Scope

- `src/PiSharp.Tools/Edit/EditTool.cs`
- `tests/PiSharp.Tools.Tests/EditToolTests.cs`
- `tests/PiSharp.Tools.Tests/BuiltInToolsTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] `EditToolInput` includes description attributes for `path` and `edits`.
- [ ] `EditReplacement` includes description attributes for `oldText` and `newText`.
- [ ] Generated nested array item schema preserves `oldText` and `newText` names.
- [ ] `path`, `edits`, `oldText`, and `newText` required behavior matches current schema.

### Testing Criteria

- [ ] Existing edit tests remain green.
- [ ] Schema assertions cover nested replacement item properties and required fields.

## PR-6: Cleanup, Documentation, And Guardrails

### Overview

Remove obsolete manual schema duplication where no longer needed and document the new pattern for future tools.

### Files In Scope

- `src/PiSharp.Tools/ToolSchemas.cs`
- `docs/epics/EPIC-04-built-in-tools.md` (if tool-authoring guidance is updated there)
- `docs/specs/SDD-pi-csharp-port.md` (if architecture docs need updating)
- `tests/PiSharp.Tools.Tests/ToolSchemaGenerationTests.cs`

### Acceptance Criteria / Definition Of Done

- [ ] Built-in tools no longer duplicate ordinary input schema definitions manually.
- [ ] `ToolSchemas` still supports manual schema helpers for custom/exceptional cases.
- [ ] Tool-authoring guidance explains `ToolSchemas.FromType<T>()` and `[Description]` usage.
- [ ] Tests guard against property-name drift from current TypeScript-compatible names.

### Testing Criteria

- [ ] Run `PiSharp.Tools.Tests`.
- [ ] Run `PiSharp.Ai.Tests` provider serialization tests.
- [ ] Run full solution tests before epic closure.

## 7. Dependencies And Risks

### 7.1 Dependencies

- .NET 10 `System.Text.Json.Schema.JsonSchemaExporter` APIs.
- `System.Text.Json` nullable and required constructor parameter metadata.
- Provider serializer behavior in `PiSharp.Ai`.
- Current `JsonTool<TParameters, TDetails>` deserialization options.

### 7.2 Key Risks

- Some providers may reject raw STJ JSON Schema constructs such as nullable type arrays or `default`.
- Root schemas may become nullable if exporter options are not configured carefully.
- Attribute placement on positional records can be easy to get wrong without `[property: Description(...)]`.
- Generated schemas may include extra keywords that provider subsets ignore or reject.
- Required inference could differ from current handwritten schemas if constructor defaults/nullability are misunderstood.

### 7.3 Risk Mitigations

- Add provider serialization tests before migrating all tools.
- Keep the generated output close to STJ raw output but allow minimal targeted transforms for provider compatibility.
- Test root object shape explicitly.
- Add schema assertions for every existing public tool argument name.
- Migrate tools in small PR slices from simplest to most nested.
- Preserve manual `ToolSchemas` helpers for escape hatches.

## 8. Rollout And Validation Plan

1. Add schema-generation helper and tests.
2. Verify generated schema payloads through all provider serializers.
3. Migrate simple built-in tools first.
4. Migrate search tools.
5. Migrate nested edit input schemas.
6. Update docs and remove unnecessary handwritten schemas.
7. Run targeted test suites after each PR:
   - `PiSharp.Tools.Tests`
   - `PiSharp.Ai.Tests`
8. Run full solution tests before epic closure.

## 9. Epic-Level Definition Of Done

- [ ] `ToolSchemas.FromType<T>()` exists and is tested.
- [ ] Built-in tool input schemas are generated from typed records where practical.
- [ ] Input descriptions live on input members via attributes.
- [ ] Provider serialization tests confirm generated schema compatibility.
- [ ] Existing tool execution tests remain green.
- [ ] Existing TypeScript-compatible tool argument names remain unchanged.
- [ ] Documentation explains the new tool input schema authoring pattern.

## 10. Useful References

### Internal

- `docs/epics/EPIC-04-built-in-tools.md`
- `src/PiSharp.Tools/ToolSchemas.cs`
- `src/PiSharp.Tools/JsonTool.cs`
- `src/PiSharp.Tools/Bash/BashTool.cs`
- `src/PiSharp.Ai/Providers/Shared/ProviderToolSerializer.cs`
- `tests/PiSharp.Tools.Tests/BuiltInToolsTests.cs`
- `tests/PiSharp.Ai.Tests/Shared/ProviderToolSerializationTests.cs`

### External

- System.Text.Json schema exporter docs: https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/extras
- `JsonSchemaExporter` API: https://learn.microsoft.com/dotnet/api/system.text.json.schema.jsonschemaexporter
- `JsonSchemaExporterOptions` API: https://learn.microsoft.com/dotnet/api/system.text.json.schema.jsonschemaexporteroptions
- `DescriptionAttribute` API: https://learn.microsoft.com/dotnet/api/system.componentmodel.descriptionattribute
- JSON Schema reference: https://json-schema.org/learn/getting-started-step-by-step
