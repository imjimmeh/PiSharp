using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;
using PiSharp.Ai.Registry;

namespace PiSharp.Ai;

public static class PublicApi
{
    public static IReadOnlyList<CatalogModel> Models => ModelRegistry.GetAllModels();

    public static IReadOnlyList<RegisteredApiProvider> Providers => ApiRegistry.List();

    public static IReadOnlyList<RegisteredApiProvider> RegisterBuiltInProviders(HttpClient? httpClient = null, IProviderCredentialResolver? credentialResolver = null)
        => BuiltInProviders.RegisterAll(httpClient, credentialResolver);

    public static int LoadModelsJson(string? path, string sourceId = "models.json")
        => ModelsJsonCatalogLoader.Load(path, sourceId);

    public static int ClearBuiltInProviders()
        => BuiltInProviders.Clear();

    public static RegisteredApiProvider RegisterProvider(IModelProvider provider, string? sourceId = null)
        => ApiRegistry.Register(provider, sourceId);

    public static int UnregisterProviderSource(string sourceId)
        => ApiRegistry.UnregisterBySource(sourceId) + ModelRegistry.UnregisterBySource(sourceId);

    public static IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        => ApiRegistry.StreamAsync(model, context, options ?? new AgentStreamOptions(), cancellationToken);

    public static Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions? options = null,
        CancellationToken cancellationToken = default)
        => ApiRegistry.CompleteAsync(model, context, options ?? new AgentStreamOptions(), cancellationToken);

    public static IAsyncEnumerable<AssistantMessageEvent> StreamSimpleAsync(
        string provider,
        string modelId,
        string prompt,
        AgentStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveCatalogModel(provider, modelId);
        return StreamAsync(model, new AgentContext(string.Empty, [AgentMessages.User(prompt)], []), options, cancellationToken);
    }

    public static Task<AssistantMessage> CompleteSimpleAsync(
        string provider,
        string modelId,
        string prompt,
        AgentStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveCatalogModel(provider, modelId);
        return CompleteAsync(model, new AgentContext(string.Empty, [AgentMessages.User(prompt)], []), options, cancellationToken);
    }

    public static ModelDescriptor ResolveCatalogModel(string provider, string modelId)
    {
        var model = ModelRegistry.GetModel(provider, modelId);
        if (model is null)
        {
            throw new InvalidOperationException($"Unknown provider/model pair '{provider}/{modelId}'.");
        }

        return model.Descriptor;
    }
}
