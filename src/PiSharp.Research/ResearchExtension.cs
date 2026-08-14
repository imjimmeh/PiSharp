using PiSharp.Ai.Auth;
using PiSharp.Extensions;
using PiSharp.Research.Http;
using PiSharp.Research.Pdf;
using PiSharp.Research.Web;
using PiSharp.Research.WebSearch;
using PiSharp.Tools;

[assembly: ExtensionMetadata(
    "pisharp-research",
    Name = "PiSharp research & retrieval",
    Version = "1.0.0",
    Description = "Web search (web_search tool), PDF content extraction, and https/http URL reading for the read tool.")]

namespace PiSharp.Research;

/// <summary>
/// <c>pisharp-research</c> extension entry: registers the <c>web_search</c>
/// tool, the PDF content extractor, and the <c>https</c>/<c>http</c> URL
/// resolvers. Reads <c>extensions.pisharp-research.*</c> settings per call so
/// edits hot-apply; env-var credential changes require a daemon restart
/// (environment is process-scoped).
/// </summary>
public sealed class ResearchExtension : IExtension, IDisposable
{
    internal const string DefaultProvider = "serper";
    internal const int DefaultResultCount = 5;
    internal const double DefaultSearchTimeoutSeconds = 15;
    internal const int DefaultMaxPdfBytes = 25 * 1024 * 1024;
    internal const int DefaultMaxPdfPages = 100;
    internal const int DefaultFetchMaxBytes = 5 * 1024 * 1024;
    internal const double DefaultFetchTimeoutSeconds = 20;

    private IExtensionApi? _api;
    private IDisposable? _extractorRegistration;
    private ResearchHttpClient? _http;
    private IDisposable? _settingsSubscription;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _settingsSubscription = api.Settings.OnChange(_ => OnSettingsChanged());

        var searchTool = new WebSearchTool(
            providerLookup: api.Search.GetProvider,
            providerList: () => api.Search.Providers,
            settings: api.Settings.Get,
            credentials: new ServiceCredentialResolver());

        api.RegisterTool(new ExtensionToolRegistration(
            Name: "web_search",
            Label: "web_search",
            Description: "Search the web for the given query. Returns up to N results with title, URL, and snippet. Use for current APIs, docs, and repro research.",
            ParametersSchema: ToolSchemas.FromType<WebSearchInput>(),
            ExecuteAsync: (toolCallId, parameters, ct, _) => searchTool.ExecuteAsync(toolCallId, parameters, ct),
            PromptSnippet: "Search the web",
            PromptGuidelines: ["Prefer web_search over guessing at APIs newer than your training data."]));

        RefreshPdfExtractor();
        RefreshUrlResolvers();
        return Task.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        RefreshPdfExtractor();
        RefreshUrlResolvers();
    }

    private void RefreshPdfExtractor()
    {
        var api = _api;
        if (api is null) return;

        _extractorRegistration?.Dispose();
        _extractorRegistration = null;
        if (!GetBool(api, "pdf.enabled", true)) return;

        var maxBytes = GetInt(api, "pdf.maxBytes", DefaultMaxPdfBytes);
        var maxPages = GetInt(api, "pdf.maxPages", DefaultMaxPdfPages);
        try
        {
            _extractorRegistration = api.Files.RegisterContentExtractor(new PdfFileContentExtractor(maxBytes, maxPages));
        }
        catch (NotSupportedException)
        {
            // Host without a file-content extractor registry: PDFs fall back to UTF-8 decoding.
        }
    }

    private void RefreshUrlResolvers()
    {
        var api = _api;
        if (api is null) return;

        var previousHttp = _http;
        if (GetBool(api, "fetch.enabled", true))
        {
            var http = new ResearchHttpClient(TimeSpan.FromSeconds(GetDouble(api, "fetch.timeoutSeconds", DefaultFetchTimeoutSeconds)));
            var maxBytes = GetInt(api, "fetch.maxBytes", DefaultFetchMaxBytes);
            var pdfExtractor = new PdfTextExtractor(
                GetInt(api, "pdf.maxBytes", DefaultMaxPdfBytes),
                GetInt(api, "pdf.maxPages", DefaultMaxPdfPages));
            try
            {
                api.Urls.RegisterResolver(new WebUrlResolver("https", http, pdfExtractor, maxBytes), overrideExisting: true);
                api.Urls.RegisterResolver(new WebUrlResolver("http", http, pdfExtractor, maxBytes), overrideExisting: true);
                _http = http;
            }
            catch (NotSupportedException)
            {
                // Host without an internal URL registry: https/http reads stay filesystem-only.
                http.Dispose();
            }
        }

        previousHttp?.Dispose();
    }

    public void Dispose()
    {
        _settingsSubscription?.Dispose();
        _extractorRegistration?.Dispose();
        _http?.Dispose();
    }

    private static bool GetBool(IExtensionApi api, string key, bool fallback)
        => WebSearch.SearchCoordinator.ToBool(api.Settings.Get(key)) ?? fallback;

    private static int GetInt(IExtensionApi api, string key, int fallback)
        => WebSearch.SearchCoordinator.ToInt(api.Settings.Get(key)) ?? fallback;

    private static double GetDouble(IExtensionApi api, string key, double fallback)
        => WebSearch.SearchCoordinator.ToDouble(api.Settings.Get(key)) ?? fallback;
}
