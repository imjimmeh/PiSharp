using PiSharp.Extensions;

namespace PiSharp.Memory;

/// <summary>
/// Typed snapshot of the <c>extensions.pisharp-memory.*</c> settings. All keys
/// are read through <see cref="IExtensionSettingsApi"/> (namespace-prefixed by the
/// host); unset keys fall back to the plan's defaults, which keep memory inert
/// unless explicitly enabled.
/// </summary>
public sealed record MemorySettings(
    bool Enabled,
    string Backend,
    bool AutolearnEnabled,
    bool AutolearnAutoContinue,
    int AutolearnMinToolCalls,
    int PromptMaxRecords,
    string? VectorEmbeddingProvider,
    string? VectorEmbeddingModel,
    int? VectorEmbeddingDimensions,
    string? SqlitePath,
    string? FilePath)
{
    public const string DefaultBackend = "off";

    public static MemorySettings Default { get; } = new(
        Enabled: false,
        Backend: DefaultBackend,
        AutolearnEnabled: false,
        AutolearnAutoContinue: false,
        AutolearnMinToolCalls: DefaultMinToolCalls,
        PromptMaxRecords: DefaultPromptMaxRecords,
        VectorEmbeddingProvider: null,
        VectorEmbeddingModel: null,
        VectorEmbeddingDimensions: null,
        SqlitePath: null,
        FilePath: null);
    public const int DefaultMinToolCalls = 5;
    public const int DefaultPromptMaxRecords = 5;

    public static MemorySettings Read(IExtensionSettingsApi settings)
        => new(
            Enabled: settings.Get<bool>("enabled"),
            Backend: settings.Get<string>("backend") ?? DefaultBackend,
            AutolearnEnabled: settings.Get<bool>("autolearn.enabled"),
            AutolearnAutoContinue: settings.Get<bool>("autolearn.autoContinue"),
            AutolearnMinToolCalls: settings.Get<int>("autolearn.minToolCalls") is var calls and > 0 ? calls : DefaultMinToolCalls,
            PromptMaxRecords: settings.Get<int>("memory.prompt.maxRecords") is var records and > 0 ? records : DefaultPromptMaxRecords,
            VectorEmbeddingProvider: settings.Get<string>("memory.vector.embeddingProvider"),
            VectorEmbeddingModel: settings.Get<string>("memory.vector.embeddingModel"),
            VectorEmbeddingDimensions: settings.Get<int?>("memory.vector.embeddingDimensions"),
            SqlitePath: settings.Get<string>("memory.sqlite.path"),
            FilePath: settings.Get<string>("memory.file.path"));
}
