using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.DeclarativeTools;

/// <summary>
/// Effective declarative-tools configuration, read from the namespaced
/// <c>extensions.pisharp-declarative-tools.*</c> settings keys (plan §9).
/// </summary>
public sealed record DeclarativeToolsOptions(
    bool Enabled,
    IReadOnlyList<string> ToolsDir,
    TimeSpan? TimeoutSeconds)
{
    public static DeclarativeToolsOptions Default { get; } = new(Enabled: true, ToolsDir: [], TimeoutSeconds: null);
}

/// <summary>
/// Typed reader over the plugin's settings. Empty <see cref="DeclarativeToolsOptions.ToolsDir"/>
/// means "use the built-in default tool directories".
/// </summary>
public sealed class DeclarativeToolsSettings
{
    private readonly IExtensionSettingsApi _settings;

    public DeclarativeToolsSettings(IExtensionSettingsApi settings) => _settings = settings;

    public DeclarativeToolsOptions Read()
    {
        var defaults = DeclarativeToolsOptions.Default;
        var timeoutSeconds = ReadTimeoutSeconds();
        return new DeclarativeToolsOptions(
            Enabled: _settings.Get<bool?>("enabled") ?? defaults.Enabled,
            ToolsDir: ReadToolsDir(),
            TimeoutSeconds: timeoutSeconds is null ? null : TimeSpan.FromSeconds(timeoutSeconds.Value));
    }

    private IReadOnlyList<string> ReadToolsDir()
    {
        var value = _settings.Get<JsonElement?>("toolsDir");
        if (value is not { } element) return [];
        if (element.ValueKind == JsonValueKind.String)
            return [element.GetString()!];
        if (element.ValueKind != JsonValueKind.Array) return [];
        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString()!);
        }
        return result;
    }

    private double? ReadTimeoutSeconds()
    {
        var value = _settings.Get<JsonElement?>("timeoutSeconds");
        if (value is not { } element) return null;
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(element.GetString(), out var parsed) => parsed,
            _ => null
        };
    }
}
