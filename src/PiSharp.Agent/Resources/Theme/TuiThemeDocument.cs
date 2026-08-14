using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Agent.Resources.Theme;

public sealed record TuiThemeDiagnostic(string Type, string Code, string Message, string Path);

public sealed record TuiThemeDocument(
    string Name,
    IReadOnlyDictionary<string, string>? Tokens = null,
    TuiColorSchemeDocument? Default = null,
    TuiColorSchemeDocument? Dialog = null,
    TuiColorSchemeDocument? Menu = null)
{
    public static async Task<(TuiThemeDocument? Theme, IReadOnlyList<TuiThemeDiagnostic> Diagnostics)> LoadFirstAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<TuiThemeDiagnostic>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            foreach (var candidate in ExpandCandidates(path))
            {
                var (document, diagnostic) = await LoadAsync(candidate, cancellationToken);
                if (diagnostic is not null) diagnostics.Add(diagnostic);
                if (document is not null) return (document, diagnostics);
            }

        return (null, diagnostics);
    }

    public static async Task<IReadOnlyList<TuiThemeDocument>> LoadAllAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<TuiThemeDocument>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            foreach (var candidate in ExpandCandidates(path))
            {
                var (document, _) = await LoadAsync(candidate, cancellationToken);
                if (document is not null) documents.Add(document);
            }
        return documents;
    }

    public static async Task<(TuiThemeDocument? Theme, TuiThemeDiagnostic? Diagnostic)> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return (null, null);
        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<TuiThemeDocument>(
                stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Converters = { new JsonStringEnumConverter() }
                },
                cancellationToken);
            return (document, null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, new TuiThemeDiagnostic("warning", "theme_load_failed", exception.Message, path));
        }
    }

    private static IEnumerable<string> ExpandCandidates(string path)
        => File.Exists(path)
            ? [path]
            : Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
}

public sealed record TuiColorSchemeDocument(
    string? NormalForeground = null,
    string? NormalBackground = null,
    string? FocusForeground = null,
    string? FocusBackground = null,
    string? HotNormalForeground = null,
    string? HotNormalBackground = null,
    string? HotFocusForeground = null,
    string? HotFocusBackground = null,
    string? DisabledForeground = null,
    string? DisabledBackground = null);
