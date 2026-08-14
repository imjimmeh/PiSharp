using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Ai.Auth;
using PiSharp.Extensions;

namespace PiSharp.Research.WebSearch;

/// <summary>
/// The <c>web_search</c> tool execution: provider selection (per-call override
/// over settings), the enabled gates, credential resolution through
/// <see cref="IServiceCredentialResolver"/>, per-request timeout from settings,
/// and model-visible error text (never exceptions). Providers are looked up via
/// an injected delegate so the tool is testable without a host.
/// </summary>
public sealed class WebSearchTool
{
    /// <summary>Settings default for <c>search.timeoutSeconds</c>.</summary>
    public const double DefaultTimeoutSeconds = 15;

    private readonly Func<string, ISearchProvider?> _providerLookup;
    private readonly Func<IReadOnlyList<ISearchProvider>> _providerList;
    private readonly Func<string, object?> _settings;
    private readonly IServiceCredentialResolver _credentials;
    private readonly SearchCoordinator _coordinator;

    public WebSearchTool(
        Func<string, ISearchProvider?> providerLookup,
        Func<IReadOnlyList<ISearchProvider>> providerList,
        Func<string, object?> settings,
        IServiceCredentialResolver credentials)
    {
        ArgumentNullException.ThrowIfNull(providerLookup);
        ArgumentNullException.ThrowIfNull(providerList);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        _providerLookup = providerLookup;
        _providerList = providerList;
        _settings = settings;
        _credentials = credentials;
        _coordinator = new SearchCoordinator(settings);
    }


    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Execute delegate shape consumed by <see cref="ExtensionToolRegistration"/>.</summary>
    public Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<WebSearchInput>(parameters.GetRawText(), SerializerOptions);
        if (input is null || string.IsNullOrWhiteSpace(input.Query))
        {
            return Task.FromResult(ErrorResult("web_search requires a 'query' argument."));
        }

        return ExecuteCoreAsync(input, cancellationToken);
    }

    private async Task<AgentToolResult<object?>> ExecuteCoreAsync(WebSearchInput input, CancellationToken cancellationToken)
    {
        var plan = _coordinator.ResolvePlan(input.Provider, input.MaxResults);
        if (plan.Error is not null)
        {
            return ErrorResult(plan.Error);
        }

        var provider = _providerLookup(plan.ProviderId!);
        if (provider is null)
        {
            var registered = RegisteredProviderIds();
            var listing = registered.Count == 0 ? "none registered" : string.Join(", ", registered);
            return ErrorResult($"Unknown search provider '{plan.ProviderId}'. Registered providers: {listing}.");
        }

        var configuredKey = ToStringValue(_settings($"search.providers.{plan.ProviderId}.apiKey"));
        var credential = await _credentials.ResolveAsync(
            plan.ProviderId!,
            new ServiceCredentialOptions(ConfiguredKey: configuredKey, UseAuthHeader: false),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(credential.ApiKey) && !credential.IsAuthenticated)
        {
            return ErrorResult(SearchCoordinator.DescribeMissingKey(plan.ProviderId!, configuredKey));
        }

        var parameters = BuildParameters(plan.ProviderId!);
        var timeoutSeconds = SearchCoordinator.ToDouble(_settings("search.timeoutSeconds")) ?? DefaultTimeoutSeconds;
        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromSeconds(DefaultTimeoutSeconds);

        var request = new SearchRequest(
            input.Query.Trim(),
            plan.MaxResults,
            credential.ApiKey,
            parameters);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var response = await provider.SearchAsync(request, timeoutCts.Token).ConfigureAwait(false);

            var details = new WebSearchDetails(
                Provider: plan.ProviderId!,
                Query: request.Query,
                Results: response.Results,
                TotalResults: response.TotalResults,
                Warning: response.Warning);
            return new AgentToolResult<object?>(
                [new TextContent(SearchCoordinator.FormatResults(response))],
                details);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ErrorResult($"web_search failed: search provider '{plan.ProviderId}' timed out after {timeout.TotalSeconds:0.#}s (extensions.pisharp-research.search.timeoutSeconds).");
        }
        catch (HttpRequestException exception)
        {
            return ErrorResult($"web_search failed: search provider '{plan.ProviderId}' HTTP error: {exception.Message}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or TimeoutException)
        {
            return ErrorResult($"web_search failed: search provider '{plan.ProviderId}' error: {exception.Message}");
        }
    }

    private IReadOnlyDictionary<string, string?>? BuildParameters(string providerId)
    {
        var cx = ToStringValue(_settings($"search.providers.{providerId}.cx"));
        if (string.IsNullOrWhiteSpace(cx)) return null;
        return new Dictionary<string, string?> { ["cx"] = cx };
    }

    private IReadOnlyList<string> RegisteredProviderIds()
        => _providerList().Select(provider => provider.Id).ToArray();

    private static AgentToolResult<object?> ErrorResult(string message)
        => new([new TextContent(message)], new WebSearchDetails(string.Empty, string.Empty, [], null, null));

    private static string? ToStringValue(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        _ => null,
    };
}
