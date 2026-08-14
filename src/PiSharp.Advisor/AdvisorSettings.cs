using PiSharp.Extensions;

namespace PiSharp.Advisor;

/// <summary>
/// Effective advisor configuration. All knobs are off-by-default so the
/// advisor is inert until the user explicitly enables it.
/// </summary>
public sealed record AdvisorOptions(
    bool Enabled,
    string? Model,
    int TimeoutMs,
    int MaxTokens,
    int MaxTranscriptTurns,
    bool Coalesce)
{
    public static AdvisorOptions Default { get; } = new(false, null, 10000, 512, 12, true);
}

/// <summary>
/// Typed reader over <c>advisor.*</c> settings. Reads namespaced keys through
/// <see cref="IExtensionSettingsApi"/>; each key is accepted both as its bare
/// name (<c>enabled</c>) and as the plan-form <c>advisor.&lt;key&gt;</c>, falling
/// back to defaults when neither is set.
/// </summary>
public sealed class AdvisorSettings
{
    private readonly IExtensionSettingsApi _settings;

    public AdvisorSettings(IExtensionSettingsApi settings) => _settings = settings;

    public AdvisorOptions Read()
    {
        var defaults = AdvisorOptions.Default;
        return new AdvisorOptions(
            Enabled: ReadBool("enabled") ?? defaults.Enabled,
            Model: ReadString("model"),
            TimeoutMs: ReadInt("timeoutMs") ?? defaults.TimeoutMs,
            MaxTokens: ReadInt("maxTokens") ?? defaults.MaxTokens,
            MaxTranscriptTurns: ReadInt("maxTranscriptTurns") ?? defaults.MaxTranscriptTurns,
            Coalesce: ReadBool("coalesce") ?? defaults.Coalesce);
    }

    private bool? ReadBool(string key)
    {
        foreach (var candidate in Candidates(key))
        {
            var value = _settings.Get<bool?>(candidate);
            if (value.HasValue) return value.Value;
        }
        return null;
    }

    private int? ReadInt(string key)
    {
        foreach (var candidate in Candidates(key))
        {
            var value = _settings.Get<int?>(candidate);
            if (value.HasValue) return value.Value;
        }
        return null;
    }

    private string? ReadString(string key)
    {
        foreach (var candidate in Candidates(key))
        {
            var value = _settings.Get<string>(candidate);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string key)
    {
        yield return key;
        yield return "advisor." + key;
    }
}
