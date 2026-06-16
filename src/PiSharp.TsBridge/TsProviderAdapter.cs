using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge;

public static class TsProviderAdapter
{
    public static IModelProvider Create(TsProviderConfig config, TsExtensionHost? host = null)
    {
        Register(config, ModelRegistry.DynamicSourceId, host);
        return RequiresCallbackTransport(config)
            ? new CallbackTsProvider(config, host, null)
            : new DeclarativeTsProvider(config);
    }

    public static IModelProvider? Register(TsProviderConfig config, string sourceId, TsExtensionHost? host = null, Func<CancellationToken, Task>? ensureReadyAsync = null)
    {
        ModelRegistry.RegisterProviderConfig(new ModelProviderConfig(config.Name, config.Api, config.BaseUrl, config.ApiKey, config.Headers), sourceId);

        foreach (var model in config.Models ?? [])
        {
            var provider = string.IsNullOrWhiteSpace(model.Provider) ? config.Name : model.Provider;
            var descriptor = new ModelDescriptor(
                provider,
                model.Id,
                config.Api,
                model.Name ?? model.Id,
                config.BaseUrl ?? string.Empty,
                model.Reasoning,
                model.ContextWindow,
                model.MaxTokens,
                Headers: config.Headers,
                ApiKey: config.ApiKey);
            ModelRegistry.RegisterModel(new CatalogModel(provider, model.Id, descriptor), sourceId);
        }

        return RequiresCallbackTransport(config) ? new CallbackTsProvider(config, host, ensureReadyAsync) : null;
    }

    private static bool RequiresCallbackTransport(TsProviderConfig config)
        => config.HasCustomStreamHandler;

    private sealed class CallbackTsProvider(TsProviderConfig config, TsExtensionHost? host, Func<CancellationToken, Task>? ensureReadyAsync) : IModelProvider
    {
        public string Api => config.Api;

        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var message = await CompleteAsync(model, context, options, cancellationToken);
            yield return new AssistantMessageEvent.Start(message);
            yield return new AssistantMessageEvent.Done(message);
        }

        public async Task<AssistantMessage> CompleteAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken cancellationToken = default)
        {
            if (host is null) return new AssistantMessage([new TextContent($"TS provider '{config.Name}' requires bridge callbacks but no host is available.")], Api: config.Api, Provider: model.Provider, Model: model.Id, StopReason: "error", ErrorMessage: "ts_provider_host_missing");
            if (ensureReadyAsync is not null) await ensureReadyAsync(cancellationToken);
            return await host.CompleteProviderAsync(new TsProviderCallbackRequest(config.Api, "complete", new { model, context, options }), cancellationToken);
        }
    }

    private sealed class DeclarativeTsProvider(TsProviderConfig config) : IModelProvider
    {
        public string Api => config.Api;

        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var message = await CompleteAsync(model, context, options, cancellationToken);
            yield return new AssistantMessageEvent.Start(message);
            yield return new AssistantMessageEvent.Done(message);
        }

        public Task<AssistantMessage> CompleteAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new AssistantMessage([new TextContent($"TS provider '{config.Name}' is declarative. Register it through TsProviderAdapter.Register so existing API transport '{config.Api}' can handle completion.")], Api: config.Api, Provider: model.Provider, Model: model.Id, StopReason: "error", ErrorMessage: "ts_provider_transport_not_registered"));
    }
}
