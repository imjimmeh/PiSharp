using System.Collections.Concurrent;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Ai.Providers;

namespace PiSharp.Ai.Registry;

public sealed record RegisteredApiProvider(
    string Api,
    IModelProvider Provider,
    string? SourceId = null)
{
    public IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureApiMatches(model);
        return Provider.StreamAsync(model, context, options, cancellationToken);
    }

    public Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureApiMatches(model);
        return Provider.CompleteAsync(model, context, options, cancellationToken);
    }

    private void EnsureApiMatches(ModelDescriptor model)
    {
        if (!StringComparer.Ordinal.Equals(model.Api, Api))
        {
            throw new InvalidOperationException($"Mismatched api: {model.Api} expected {Api}.");
        }
    }
}

public static class ApiRegistry
{
    private static readonly ConcurrentDictionary<string, RegisteredApiProvider> Providers = new(StringComparer.Ordinal);

    public static IReadOnlyList<RegisteredApiProvider> List()
        => Providers.Values.OrderBy(provider => provider.Api, StringComparer.Ordinal).ToArray();

    public static RegisteredApiProvider Register(IModelProvider provider, string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(provider.Api)) throw new ArgumentException("Provider API must be non-empty.", nameof(provider));

        var registration = new RegisteredApiProvider(provider.Api, provider, sourceId);
        Providers[provider.Api] = registration;
        return registration;
    }

    public static RegisteredApiProvider? Get(string api)
        => Providers.TryGetValue(api, out var registration) ? registration : null;

    public static RegisteredApiProvider Resolve(string api)
        => Get(api) ?? throw new InvalidOperationException($"No API provider registered for '{api}'.");

    public static bool Unregister(string api)
        => Providers.TryRemove(api, out _);

    public static bool Unregister(string api, string sourceId)
    {
        if (!Providers.TryGetValue(api, out var registration)) return false;
        if (!string.Equals(registration.SourceId, sourceId, StringComparison.Ordinal)) return false;
        return Providers.TryRemove(api, out _);
    }

    public static int UnregisterBySource(string sourceId)
    {
        var removed = 0;
        foreach (var registration in Providers.Values.Where(provider => provider.SourceId == sourceId).ToArray())
        {
            if (Providers.TryRemove(registration.Api, out _)) removed++;
        }
        return removed;
    }

    public static void Clear()
        => Providers.Clear();

    public static IAsyncEnumerable<AssistantMessageEvent> StreamAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
        => Resolve(model.Api).StreamAsync(model, context, options, cancellationToken);

    public static Task<AssistantMessage> CompleteAsync(
        ModelDescriptor model,
        AgentContext context,
        AgentStreamOptions options,
        CancellationToken cancellationToken = default)
        => Resolve(model.Api).CompleteAsync(model, context, options, cancellationToken);
}
