namespace PiSharp.Ast.Ast;

/// <summary>
/// Language-provider registry used by the structural tools. V1 registers exactly one
/// provider (C#/Roslyn); future providers (e.g. tree-sitter) plug in without tool changes.
/// </summary>
public sealed class AstLanguageRegistry
{
    private readonly List<IAstLanguageProvider> _providers = [];

    public void Register(IAstLanguageProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Language))
        {
            throw new ArgumentException("Provider language id must not be empty.", nameof(provider));
        }
        if (_providers.Any(existing => string.Equals(existing.Language, provider.Language, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A provider for language '{provider.Language}' is already registered.", nameof(provider));
        }
        _providers.Add(provider);
    }

    public IReadOnlyList<IAstLanguageProvider> Providers => _providers;

    /// <summary>Resolves a provider by explicit language id or by file extension.</summary>
    public IAstLanguageProvider? Resolve(string? language, string path)
    {
        if (!string.IsNullOrWhiteSpace(language))
        {
            return _providers.FirstOrDefault(provider =>
                string.Equals(provider.Language, language, StringComparison.OrdinalIgnoreCase));
        }

        return _providers.FirstOrDefault(provider => provider.SupportsFile(path));
    }

    /// <summary>Error message listing registered providers, for unsupported-language failures.</summary>
    public string UnsupportedLanguageMessage(string? language, string path)
    {
        var registered = _providers.Count == 0
            ? "none"
            : string.Join(", ", _providers.Select(provider => provider.Language));
        return $"unsupported language{(string.IsNullOrWhiteSpace(language) ? "" : $" '{language}'")} for {path} (registered: {registered})";
    }
}
