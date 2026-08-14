# Adding a Model Provider

This guide covers how a new model provider is added to PiSharp. It is written so a solo engineer can follow it end to end. Before writing any code, decide which of the three paths applies.

## Decision flow

1. **Is the endpoint OpenAI-compatible (or Anthropic-compatible)?** Then use the **recipe path**: no provider class at all. Add catalog entries (shipped in `BuiltInModels.g.cs` or user-defined in `~/.pi/agent/models.json`), an env-var mapping in `EnvApiKeyDetector.ProviderEnvVarMap`, and a correct `baseUrl`. See "Adding an OpenAI-compatible provider" in [pisharp-providers.md](pisharp-providers.md) for the two URL rules and the canonical example.
2. **Is the endpoint a first-class built-in provider?** (Non-OpenAI-compatible shape, OAuth-backed, or a strategic core provider — e.g. GitHub Copilot, Anthropic, Bedrock.) Follow this guide: a core `HttpModelProvider` subclass in `src/PiSharp.Ai/Providers/<Name>/`.
3. **Is it a private/exotic endpoint?** (Self-hosted gateway, custom SAML proxy.) Ship it as a **native extension plugin**: implement `IExtension`, call `api.RegisterProvider(new MyProvider())` on load, and define models in `~/.pi/agent/models.json`. The class skeleton below is the same *shape* — the differences are the project location, the registration touchpoints (step 4 becomes: `IExtensionApi.RegisterProvider` + `models.json`; there is no generator-map change because a plugin cannot contribute catalog models), and the internals caveat: the OpenAI mapper/parser/endpoint helpers (`OpenAICompletionsRequestMapper`, `OpenAICompletionsStreamParser`, `OpenAIEndpoint`) are `internal` to `PiSharp.Ai` and invisible from a plugin assembly — a plugin provider must build its payload and parse its stream itself.

## Class skeleton
```csharp
using System.Runtime.CompilerServices;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Providers.Shared;

namespace PiSharp.Ai.Providers.Example;

public sealed class ExampleProvider : HttpModelProvider
{
    public const string ApiName = "example";

    public ExampleProvider(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        : base(ApiName, httpClient, credentialResolver)
    {
    }

    public override async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. Credentials. Use requireAuthentication: false when you want a friendly
        //    error event instead of a thrown exception (see the Copilot provider).
        var credentials = await ResolveCredentialsAsync(model, options, cancellationToken: cancellationToken).ConfigureAwait(false);

        // 2. Payload. Reuse the OpenAI-compatible mapper or build your own JsonElement.
        var payload = await InvokePayloadHookAsync(BuildPayload(model, context, options), options, cancellationToken).ConfigureAwait(false);

        // 3. URL + request. CreateJsonRequest applies credential headers and Authorization.
        using var request = CreateJsonRequest(HttpMethod.Post, EndpointUrl(model), payload, credentials);
        // Add provider-specific static headers here, e.g.:
        //   request.Headers.TryAddWithoutValidation("X-Custom", "value");

        // 4. Send, hook, parse.
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await InvokeResponseHookAsync(response, options, cancellationToken).ConfigureAwait(false);
        await foreach (var evt in ParseStream(model, response, cancellationToken).ConfigureAwait(false)) yield return evt;
    }

    private static JsonElement BuildPayload(ModelDescriptor model, AgentContext context, AgentStreamOptions options)
        => OpenAICompletionsRequestMapper.BuildPayload(model, context, options);

    private static Uri EndpointUrl(ModelDescriptor model)
    {
        // OpenAIEndpoint.Url handles /v1 appending and full-path bases; build your own
        // when the route is not /v1-shaped (e.g. Copilot's /chat/completions).
        return OpenAIEndpoint.Url(model, "chat/completions");
    }

    private static System.Collections.Generic.IAsyncEnumerable<AssistantMessageEvent> ParseStream(
        ModelDescriptor model, HttpResponseMessage response, CancellationToken cancellationToken)
        => OpenAICompletionsStreamParser.ParseAsync(model, response, cancellationToken);
}
```

> **Plugin caveat:** `OpenAICompletionsRequestMapper`, `OpenAICompletionsStreamParser`, and `OpenAIEndpoint` are `internal` to `PiSharp.Ai`. The skeleton above compiles as-is for a **core** provider (same assembly). A plugin provider must implement payload building and stream parsing itself using only public API.

Notes:

- **Shared types live in core.** The provider class is the only new public type in the plugin/extension story; never define shared provider interfaces inside a plugin assembly.
- **`CompleteAsync` is inherited** from `HttpModelProvider` (it drains `StreamAsync`).
- **Error path:** yield `ErrorAfterStart(model, "...", Logger, cancellationToken)` for recoverable auth failures (missing token, expired subscription) so users see a hint instead of a raw 401. Use `ResolveCredentialsAsync(model, options, requireAuthentication: false, ...)` and check `credentials.IsAuthenticated`.

## Registration touchpoints (core provider)

All four are required; missing any one breaks dispatch or tests.

1. **`src/PiSharp.Ai/Providers/BuiltInProviders.cs`** — add `ExampleProvider.ApiName` to `ApiNames` and `ApiRegistry.Register(new ExampleProvider(httpClient, credentialResolver), SourceId)` to `RegisterAll`.
2. **`src/PiSharp.Ai/Models/Generation/ModelCatalogGenerator.cs`** — add a `ModelsDevProviders` entry (`new("<provider>", "<api>", "<baseUrl>")`) so models.dev models project to your api, then regenerate `BuiltInModels.g.cs` (`dotnet run --project src/PiSharp.Ai.ModelGenerator -- src/PiSharp.Ai/Models/Generated/BuiltInModels.g.cs`). If regeneration is not runnable, hand-edit the generated rows following the file's existing shape — never delete existing rows, keep the builder deterministic. For providers without a models.dev listing, add models via `~/.pi/agent/models.json` instead.
3. **`src/PiSharp.Ai/Auth/EnvApiKeyDetector.cs`** — add the `<PROVIDER>_API_KEY` env-var mapping (OAuth-backed providers such as Copilot deliberately have none).
4. **`tests/PiSharp.Ai.Tests/Providers/BuiltInProvidersTests.cs`** — the `BuiltInRegistrationRegistersExpectedProviderApisWithBuiltInSource` loop covers the new api automatically once `ApiNames` lists it; add an explicit registration test asserting `SourceId == "built-in"` and the provider type.

## Test skeleton

Follow `tests/PiSharp.Ai.Tests` conventions: `ApiRegistry.Clear()` per test, `CapturingHandler`/`StaticCredentialResolver` from `ProviderTestHelpers` for stub HTTP, `ProviderMatrixTests` for the api matrix.

```csharp
// Registration (BuiltInProvidersTests)
[Fact]
public void ExampleProviderIsRegisteredAsBuiltIn()
{
    BuiltInProviders.RegisterAll();
    var registration = ApiRegistry.Get(ExampleProvider.ApiName);
    Assert.NotNull(registration);
    Assert.Equal(BuiltInProviders.SourceId, registration!.SourceId);
    Assert.IsType<ExampleProvider>(registration.Provider);
}

// Contract: dispatch by descriptor api (ProviderContractTests style)
//   ApiRegistry.StreamAsync(new ModelDescriptor("example", "m", ExampleProvider.ApiName), ...)
//   must reach the example provider, and EnsureApiMatches must reject a mismatched api.

// Streaming with stub HTTP (OpenAIProviderTests style)
[Fact]
public async Task StreamsStubSseToTextAndTerminalEvents()
{
    var handler = new CapturingHandler("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
                                       "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
    var provider = new ExampleProvider(new HttpClient(handler),
        new StaticCredentialResolver(new ProviderCredentialResult(ApiKey: "key", IsAuthenticated: true)));
    var events = new List<AssistantMessageEvent>();
    await foreach (var evt in provider.StreamAsync(new ModelDescriptor("example", "m", ExampleProvider.ApiName),
        new AgentContext("system", [], []), new AgentStreamOptions())) events.Add(evt);

    Assert.Contains(events, evt => evt is AssistantMessageEvent.TextStart);
    Assert.IsType<AssistantMessageEvent.Done>(events.Last());
}
```

Acceptance: `dotnet build PiSharp.Ai` and `dotnet test tests/PiSharp.Ai.Tests` are green; a manual turn on a real key completes; missing credentials show the intended error path.
