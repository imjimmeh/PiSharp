using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.Research.WebSearch;


/// <summary>
/// The outcome of resolving a <c>web_search</c> request against settings:
/// either a provider id plus an effective (clamped) result count, or a
/// model-visible error. Pure policy — no I/O.
/// </summary>
public sealed record SearchPlan(string? ProviderId, int MaxResults, string? Error);

/// <summary>
/// Pure, unit-testable policy for the <c>web_search</c> tool: provider id
/// resolution (settings + per-call override), the <c>search.enabled</c> and
/// per-provider <c>enabled</c> gates, result-count clamping, and error text
/// production. Settings are read through an injected <c>Func&lt;string, object?&gt;</c>
/// (bound to <c>IExtensionApi.Settings.Get</c>) so every branch is testable
/// without a host.
/// </summary>
public sealed class SearchCoordinator
{
    /// <summary>Settings default when <c>search.enabled</c> is unset.</summary>
    public const bool DefaultEnabled = true;
    /// <summary>Settings default for <c>search.provider</c>.</summary>
    public const string DefaultProvider = "serper";
    /// <summary>Settings default for <c>search.resultCount</c>.</summary>
    public const int DefaultResultCount = 5;
    /// <summary>Tool-level clamp: minimum results per call.</summary>
    public const int MinMaxResults = 1;
    /// <summary>Tool-level clamp: maximum results per call.</summary>
    public const int MaxMaxResults = 10;

    private readonly Func<string, object?> _settings;

    public SearchCoordinator(Func<string, object?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>
    /// Resolves the provider id and effective result count for a call.
    /// <paramref name="providerOverride"/> is the per-call tool argument;
    /// <paramref name="maxResultsOverride"/> (when supplied) overrides the
    /// configured <c>search.resultCount</c>.
    /// Returns a plan with <see cref="SearchPlan.Error"/> set (and null provider)
    /// for disabled/misconfigured states; otherwise a clamped result count.
    /// </summary>
    public SearchPlan ResolvePlan(string? providerOverride, int? maxResultsOverride = null)
    {
        if (!GetBool("search.enabled", DefaultEnabled))
        {
            return new SearchPlan(
                null,
                0,
                "web_search is disabled; enable it via settings 'extensions.pisharp-research.search.enabled' (default: true).");
        }

        var providerId = string.IsNullOrWhiteSpace(providerOverride)
            ? GetString("search.provider", DefaultProvider)
            : providerOverride.Trim();
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new SearchPlan(
                null,
                0,
                "web_search has no search provider configured; set 'extensions.pisharp-research.search.provider' to a registered provider id.");
        }

        if (!GetBool($"search.providers.{providerId}.enabled", DefaultEnabled))
        {
            return new SearchPlan(
                null,
                0,
                $"Search provider '{providerId}' is disabled; enable it via settings 'extensions.pisharp-research.search.providers.{providerId}.enabled'.");
        }

        var requested = maxResultsOverride ?? GetInt("search.resultCount", DefaultResultCount);
        return new SearchPlan(providerId, ClampMaxResults(requested), null);
    }

    /// <summary>Clamps a requested result count to the tool's 1..10 window.</summary>
    public static int ClampMaxResults(int requested)
        => Math.Clamp(requested, MinMaxResults, MaxMaxResults);

    /// <summary>
    /// The conventional environment variable for a known provider id (matches
    /// the provider plugins' <c>RegisterProviderEnvVars</c> registrations).
    /// </summary>
    public static string? DefaultEnvVar(string providerId) => providerId switch
    {
        "serper" => "SERPER_API_KEY",
        "google-cse" => "GOOGLE_CSE_API_KEY",
        "brave" => "BRAVE_API_KEY",
        _ => null,
    };

    /// <summary>
    /// Model-visible missing-key error naming both the environment variable and
    /// the settings key the operator should configure.
    /// </summary>
    public static string DescribeMissingKey(string providerId, string? configuredKey)
    {
        var settingsKey = $"extensions.pisharp-research.search.providers.{providerId}.apiKey";
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            return $"Search provider '{providerId}' has no API key available: the configured key '{configuredKey}' is not set as an environment variable and no literal key was configured. Set the environment variable or a literal value at '{settingsKey}'.";
        }

        var envVar = DefaultEnvVar(providerId);
        return envVar is null
            ? $"Search provider '{providerId}' requires an API key: set its provider environment variable or configure '{settingsKey}'."
            : $"Search provider '{providerId}' requires an API key: set the {envVar} environment variable or configure '{settingsKey}'.";
    }

    /// <summary>Formats a normalized search response as numbered text for the model transcript.</summary>
    public static string FormatResults(SearchResponse response)
    {
        if (response.Results.Count == 0)
        {
            return response.Warning is null
                ? $"web_search ({response.Provider}): no results for query."
                : $"web_search ({response.Provider}): no results for query. {response.Warning}";
        }

        var lines = new List<string>(response.Results.Count * 3 + 1);
        for (var i = 0; i < response.Results.Count; i++)
        {
            var item = response.Results[i];
            lines.Add($"{i + 1}. {item.Title}");
            lines.Add($"   {item.Url}");
            lines.Add($"   {item.Snippet}");
        }

        var header = $"web_search ({response.Provider}): {response.Results.Count} result(s)" +
                     (response.TotalResults is null ? string.Empty : $" of {response.TotalResults}") +
                     (response.Warning is null ? string.Empty : $" — {response.Warning}");
        return header + "\n" + string.Join("\n", lines);
    }

    private bool GetBool(string key, bool fallback)
        => ToBool(_settings(key)) ?? fallback;

    private int GetInt(string key, int fallback)
        => ToInt(_settings(key)) ?? fallback;

    private string GetString(string key, string fallback)
    {
        var value = ToStringValue(_settings(key));
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    internal static bool? ToBool(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        JsonElement element when element.ValueKind == JsonValueKind.True => true,
        JsonElement element when element.ValueKind == JsonValueKind.False => false,
        JsonElement element when element.ValueKind == JsonValueKind.String
            && bool.TryParse(element.GetString(), out var parsed) => parsed,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    internal static int? ToInt(object? value) => value switch
    {
        null => null,
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        double number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number64)
            => number64 is >= int.MinValue and <= int.MaxValue ? (int)number64 : null,
        JsonElement element when element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), out var parsed) => parsed,
        string text when int.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    internal static double? ToDouble(object? value) => value switch
    {
        null => null,
        double number => number,
        float number => number,
        int number => number,
        long number => number,
        JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number64) => number64,
        JsonElement element when element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        string text when double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static string? ToStringValue(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        _ => null,
    };
}
