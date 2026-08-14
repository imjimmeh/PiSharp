---
name: model-providers
description: >
  Use when working with LLM providers and credentials: IModelProvider
  implementations, the model registry/catalog (BuiltInModels.g.cs), models.json
  loading, API-key detection and OAuth credential resolution, provider
  registration, or adding a new provider. Also covers regenerating the model
  catalog instead of hand-editing it.
type: cross-cutting
scope:
  - src/PiSharp.Ai/**
  - src/PiSharp.Ai.ModelGenerator/**
  - docs/pisharp-providers.md
  - docs/pisharp-adding-a-provider.md
related_skills:
  - local-development
  - settings-and-resources
  - agent-harness
last_verified:
  commit: "646522ccc6edc48acc39e4545cd120af9f1dafba"
  date: "2026-08-14"
confidence: high
---

# Model Providers and Credentials

## When to use this skill

Use this skill when:

- adding or changing an LLM provider (Anthropic, OpenAI, etc.);
- changing credential resolution (env keys, OAuth);
- updating the model catalog (`BuiltInModels.g.cs`, `models.json`);
- changing the provider registry or API registry;
- debugging provider selection.

Typical tasks include:

- implementing `IModelProvider`;
- registering a provider in `BuiltInProviders.RegisterAll`;
- adding env-key detection;
- regenerating `BuiltInModels.g.cs` via the model generator;
- adding provider tests.

Do not use this skill for:

- settings precedence — use [settings-and-resources](../settings-and-resources/SKILL.md);
- the agent loop — use [agent-harness](../agent-harness/SKILL.md);
- build/test mechanics — use [local-development](../local-development/SKILL.md).

## Responsibilities and boundaries

This area owns:

- the provider contract (`IModelProvider`);
- provider registration and the model registry;
- the generated model catalog;
- credential detection/resolution (env keys, OAuth).

This area does not own:

- tool registration (tools-and-commands);
- session/system-prompt composition (agent-harness);
- settings precedence (settings-and-resources).

## Architecture

`IModelProvider` is the streaming/completion contract. Providers register
through `BuiltInProviders.RegisterAll` into an API registry; the model registry
resolves models from the generated catalog (`BuiltInModels.g.cs`, regenerated
by `PiSharp.Ai.ModelGenerator` at CLI build) plus `models.json`. Credentials
come from `EnvApiKeyDetector` (environment variables) and
`ProviderCredentialResolver` (OAuth tokens stored in `~/.pi/agent/auth.json`).

### Important components

| Component | Location | Responsibility |
|---|---|---|
| Provider contract | `src/PiSharp.Ai/Providers/IModelProvider.cs` | Streaming + completion |
| Provider registry | `src/PiSharp.Ai` (`BuiltInProviders.RegisterAll`) | Registers ~11 built-in providers |
| Model registry | `src/PiSharp.Ai` (`ModelRegistry`) | Resolves models from catalog + models.json |
| Model catalog | `src/PiSharp.Ai` (`BuiltInModels.g.cs`) | Generated; do not hand-edit |
| Catalog generator | `src/PiSharp.Ai.ModelGenerator` | Regenerates `BuiltInModels.g.cs` |
| Credentials | `src/PiSharp.Ai` (`EnvApiKeyDetector`, `ProviderCredentialResolver`) | Env keys + OAuth (`~/.pi/agent/auth.json`) |
| Provider docs | `docs/pisharp-providers.md`, `docs/pisharp-adding-a-provider.md` | Recipes |

### Main flow

1. Runtime asks the model registry for a model.
2. Registry resolves the provider + model entry (catalog or models.json).
3. Provider authenticates via env key or resolved OAuth credential.
4. Provider streams/completes through `IModelProvider`.

## Project terminology

| Term | Meaning in this repository |
|---|---|
| Provider | An LLM backend implementing `IModelProvider` |
| Model catalog | Generated `BuiltInModels.g.cs` + `models.json` model definitions |
| ApiRegistry | Provider registration point (`BuiltInProviders.RegisterAll`) |
| EnvApiKeyDetector | Resolves API keys from environment variables |
| ProviderCredentialResolver | Resolves OAuth/credential storage (`~/.pi/agent/auth.json`) |

## Important entry points
- [`skills/SKILL.md`](../../SKILL.md): project router — routing index for all PiSharp project skills.


- [`src/PiSharp.Ai/Providers/IModelProvider.cs`](../../../src/PiSharp.Ai/Providers/IModelProvider.cs)
- [`docs/pisharp-adding-a-provider.md`](../../../docs/pisharp-adding-a-provider.md):
  the provider-addition recipe.
- [`docs/pisharp-providers.md`](../../../docs/pisharp-providers.md)

## Dependencies and consumers

### Depends on

- `src/PiSharp.Ai` internals; nothing external beyond HTTP + model APIs.

### Consumed by

- `AgentHarness` (model selection), the CLI, the daemon.

### External systems

- LLM APIs (Anthropic, OpenAI, etc.); OAuth providers.

## Invariants

The following must remain true:

1. `BuiltInModels.g.cs` is generated — never hand-edit it; regenerate via
   `PiSharp.Ai.ModelGenerator`.
2. Every provider registers in `BuiltInProviders.RegisterAll`.
3. Credential values are never logged; only resolution mechanisms are
   documented.
4. Provider tests follow the `BuiltInProvidersTests` pattern (no live API calls
   in unit tests).
5. CI builds with `-p:RunModelCatalogGenerationOnBuild=false` — catalog
   regeneration happens locally via the generator, not in CI.

## Common change workflows

### Add an LLM provider

Follow `docs/pisharp-adding-a-provider.md`:

1. Implement `IModelProvider` (streaming + completion).
2. Register the provider in `BuiltInProviders.RegisterAll`.
3. Add env-key detection to `EnvApiKeyDetector` (and OAuth resolution if
   applicable).
4. Regenerate the model catalog via the generator (add models to `models.json`
   if applicable).
5. Add provider tests following the `BuiltInProvidersTests` pattern.

Files commonly changed together:

- `src/PiSharp.Ai/Providers/**`
- `src/PiSharp.Ai/BuiltInProviders.cs`
- `src/PiSharp.Ai/BuiltInModels.g.cs` (regenerated, not hand-edited)
- `tests/PiSharp.Ai.Tests/**`

Validation:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Ai.Tests/PiSharp.Ai.Tests.csproj
```

### Change credential resolution

1. Change `EnvApiKeyDetector` / `ProviderCredentialResolver` behavior.
2. Update tests; keep credential values out of logs and tests.

Files commonly changed together:

- `src/PiSharp.Ai/**` (credential resolution)
- `tests/PiSharp.Ai.Tests/**`

Validation:

```bash
dotnet test tests/PiSharp.Ai.Tests/PiSharp.Ai.Tests.csproj
```

## Testing and validation

Run for all changes in this area:

```bash
dotnet build PiSharp.sln
dotnet test tests/PiSharp.Ai.Tests/PiSharp.Ai.Tests.csproj
```

Run conditionally:

```bash
dotnet test PiSharp.sln
```

## Operational considerations

- OAuth tokens live in `~/.pi/agent/auth.json` (user-specific); never copy
  values into docs or tests — document the mechanism only.
- Env keys: support both the legacy Pi variable names and PiSharp names where
  the compatibility layer requires it (see
  [settings-and-resources](../settings-and-resources/SKILL.md)).

## Common mistakes

- Do not hand-edit `BuiltInModels.g.cs` — regenerate it.
- Do not call live LLM APIs in unit tests; mock the provider transport.
- Do not log API keys or OAuth tokens.
- Do not add a provider without registering it in `BuiltInProviders.RegisterAll`.

## Legacy and deprecated patterns

- Original JS Pi provider variable names are compatibility surfaces; the
  `EnvApiKeyDetector` maps them — keep legacy names working where documented.

## Existing authoritative documentation

- [`docs/pisharp-adding-a-provider.md`](../../../docs/pisharp-adding-a-provider.md)

  * Covers the provider-addition recipe end to end.
  * Treat as authoritative and current.

- [`docs/pisharp-providers.md`](../../../docs/pisharp-providers.md)

  * Covers provider list and env key conventions.
  * Treat as authoritative for the provider list; verify counts against
    `BuiltInProviders.RegisterAll`.

## Known ambiguity and technical debt

- The built-in provider count (~11) drifts as providers are added; always check
  `BuiltInProviders.RegisterAll` for the current set.
- Provider/model naming must stay compatible with JS Pi where
  `compatibility-layer` guarantees apply.

## Evidence and verification

This skill was verified against commit `646522ccc6edc48acc39e4545cd120af9f1dafba`.

Primary evidence:

- [`src/PiSharp.Ai/Providers/IModelProvider.cs`](../../../src/PiSharp.Ai/Providers/IModelProvider.cs)
- [`docs/pisharp-adding-a-provider.md`](../../../docs/pisharp-adding-a-provider.md)
- [`src/PiSharp.Ai.ModelGenerator`](../../../src/PiSharp.Ai.ModelGenerator)
- [`.github/workflows/ci.yml`](../../../.github/workflows/ci.yml)
  (`RunModelCatalogGenerationOnBuild=false`)
