namespace PiSharp.Memory.Abstractions.Embeddings;

/// <summary>Batch embedding request mirroring the TS <c>pisharp.embeddings</c> <c>embedMany</c> contract.</summary>
public sealed record EmbeddingRequest(
    IReadOnlyList<string> Inputs,
    string? Model = null,
    int? Dimensions = null,
    int TimeoutMs = 30_000);

/// <summary>Result of an embedding batch. <see cref="Embeddings"/> is indexed in input order.</summary>
public sealed record EmbeddingResult(
    IReadOnlyList<float[]> Embeddings,
    string ProviderId,
    string Model,
    int Dimensions,
    object? Usage = null);

/// <summary>
/// Native embedding provider abstraction (TS-parity with the OpenAI-compatible
/// contract of <c>createOpenAICompatibleProvider</c>; consumed by Memory.Vector).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Stable provider id matching <c>extensions.pisharp-memory.memory.vector.embeddingProvider</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable provider name.</summary>
    string DisplayName { get; }

    Task<EmbeddingResult> EmbedManyAsync(EmbeddingRequest request, CancellationToken ct = default);
}

/// <summary>Embedding provider registration + lookup, shared across plugin ALCs (same pattern as <see cref="MemoryProviderRegistry"/>).</summary>
public sealed class EmbeddingProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IEmbeddingProvider> _providers = new(StringComparer.Ordinal);

    public void Register(IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id))
            throw new ArgumentException("Embedding provider id must not be empty.", nameof(provider));
        lock (_gate) _providers[provider.Id] = provider;
    }

    public IEmbeddingProvider? TryGet(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate) return _providers.TryGetValue(id, out var provider) ? provider : null;
    }

    public IReadOnlyList<IEmbeddingProvider> All
    {
        get
        {
            lock (_gate) return _providers.Values.ToArray();
        }
    }
}

/// <summary>App-base static locator for embedding providers (filled by Embeddings.OpenAI etc.).</summary>
public static class EmbeddingServices
{
    public static EmbeddingProviderRegistry Providers { get; } = new();
}
