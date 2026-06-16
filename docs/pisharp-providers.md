# PiSharp Model Providers

PiSharp routes model calls through `IModelProvider` from `PiSharp.Ai.Providers`. Providers can be built in, registered by native .NET extensions, or adapted from TypeScript extensions through the bridge.

## Provider contract

```csharp
public interface IModelProvider
{
    string Api { get; }

    IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);

    Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default);
}
```

- `Api` is the provider API key/name used for selection.
- `StreamAsync()` emits assistant message events for interactive/streaming turns.
- `CompleteAsync()` returns a complete assistant message for non-streaming use cases.

## Built-in providers

Current built-in provider API names include:

- `anthropic`
- `openai`
- `openai-responses`
- `openai-chat-completions`
- `azure-openai-responses`
- `openai-codex-responses`
- `google`
- `google-vertex`
- `amazon-bedrock`
- `mistral`
- Faux/test provider support

`PiRuntimeBootstrap` calls `PiSharp.Ai.PublicApi.RegisterBuiltInProviders()` during startup.

## Model catalog and selection

PiSharp can load model catalog overrides from:

```text
~/.pi/agent/models.json
```

Model selection can come from:

- `--provider <name>`
- `--model <model>`
- `defaultProvider` setting
- `defaultModel` setting
- extension/runtime model changes

Thinking level can come from `--thinking`, runtime options, or extension APIs.

## Public provider API

`PiSharp.Ai.PublicApi` exposes provider/model operations such as:

- `RegisterBuiltInProviders()`
- `RegisterProvider()`
- `UnregisterProviderSource()`
- `LoadModelsJson()`
- `StreamAsync()`
- `CompleteAsync()`
- `StreamSimpleAsync()`
- `CompleteSimpleAsync()`

Native extensions usually call `api.RegisterProvider(provider)` rather than using `PublicApi` directly.

## Credentials

Runtime credential resolution uses `ProviderCredentialResolver` and `FileOAuthStorage` by default. OAuth/auth data is stored at:

```text
~/.pi/agent/auth.json
```

For OAuth-backed providers such as OpenAI Codex, PiSharp prefers credentials in the nested `providers` object and removes stale legacy root-level provider entries when saving refreshed credentials.

Environment and ambient credentials currently include:

| Provider | Environment / ambient credential source |
| --- | --- |
| `anthropic` | `ANTHROPIC_OAUTH_TOKEN`, then `ANTHROPIC_API_KEY` |
| `openai`, OpenAI-compatible aliases | `OPENAI_API_KEY` unless a model/provider config supplies another API key setting |
| `azure-openai-responses` | `AZURE_OPENAI_API_KEY` |
| `google` | `GEMINI_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `google-vertex` | `GOOGLE_APPLICATION_CREDENTIALS` plus `GOOGLE_CLOUD_PROJECT` and `GOOGLE_CLOUD_LOCATION` |
| `amazon-bedrock` | `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` |

Provider/model configuration can also supply headers or an API-key literal/environment-variable name through the model catalog.

## Registering providers from extensions

Native extensions can implement `IModelProvider` and register it:

```csharp
public sealed class ExampleProvider : IModelProvider
{
    public string Api => "example";

    public IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        // Return or yield assistant streaming events.
        throw new NotImplementedException();
    }

    public Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        // Return one assistant message.
        throw new NotImplementedException();
    }
}

api.RegisterProvider(new ExampleProvider());
```

TypeScript extensions can also register providers. PiSharp adapts those through `TsProviderAdapter`, registering the provider with `PublicApi` and calling back into Node when needed.

## Provider events

Extensions can observe provider/model flow through events including:

- `before_provider_request`
- `before_provider_payload`
- `after_provider_response`
- `model_select`
- `thinking_level_select`
- `thinking_level_changed`

These events are available to native extensions and forwarded to TypeScript extensions through the bridge.

## When to use native vs TypeScript providers

Use a native provider when you need:

- Direct .NET HTTP/auth libraries.
- Efficient streaming without bridge serialization.
- Tight integration with PiSharp runtime abstractions.
- Strong cancellation and disposal behavior.

Use a TypeScript provider when you need:

- Compatibility with existing Pi provider code.
- Node.js packages or TypeScript-only dependencies.
