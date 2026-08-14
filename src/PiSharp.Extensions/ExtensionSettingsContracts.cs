using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Extensions;

/// <summary>
/// Which settings layer a write targets. <see cref="Source"/> writes to the layer where the key
/// currently resolves (from the merged snapshot's provenance); <see cref="Global"/> and
/// <see cref="Project"/> pin the PiSharp global/project layers explicitly.
/// </summary>
public enum ExtensionSettingsScope { Source, Global, Project }

/// <summary>
/// Describes a committed settings change. <see cref="Key"/> is the full physical path
/// (e.g. "extensions.pisharp-memory.backend" or "logging.file"), <see cref="Value"/> is the
/// JSON-serializable value after commit (null when removed), <see cref="Layer"/> is the
/// winning PiSettingsLayer name, and
/// <see cref="SourceId"/> identifies the writer (extension EffectiveSourceId or "runtime:&lt;writer&gt;").
/// </summary>
public sealed record ExtensionSettingsChange(
    string Key,
    object? Value,
    string Layer,
    string SourceId);

/// <summary>
/// Per-extension, namespaced settings surface exposed on <see cref="IExtensionApi.Settings"/>.
/// Keys are dot-separated paths of JSON property names under the extension's own namespace;
/// reads see the effective (merged across all four layers) value.
/// </summary>
public interface IExtensionSettingsApi
{
    /// <summary>Effective (merged across all layers) value of &lt;namespace&gt;.&lt;key&gt;, or null when unset.</summary>
    object? Get(string key);

    /// <summary><see cref="Get(string)"/> deserialized to <typeparamref name="T"/>; default when unset.</summary>
    T? Get<T>(string key);

    /// <summary>Reads a top-level (core) path — e.g. GetCore("logging.file") or GetCore("logging") for the section object.</summary>
    object? GetCore(string path);

    /// <summary>Writes &lt;namespace&gt;.&lt;key&gt; on the resolved layer; a null value removes the key.</summary>
    Task SetAsync(string key, object? value, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default);

    /// <summary>Removes &lt;namespace&gt;.&lt;key&gt; from the resolved layer.</summary>
    Task RemoveAsync(string key, ExtensionSettingsScope scope = ExtensionSettingsScope.Source, CancellationToken cancellationToken = default);

    /// <summary>Fired for ANY committed settings change (any writer, any key).</summary>
    IDisposable OnChange(Action<ExtensionSettingsChange> handler);

    /// <summary>Fired for committed settings changes whose key starts with <paramref name="keyPrefix"/>.</summary>
    IDisposable OnChange(string keyPrefix, Action<ExtensionSettingsChange> handler);
}

/// <summary>
/// Raw, path-parameterized settings core held by the runtime binding and shared by all extensions.
/// Namespace prefixing and validation are applied by the per-extension
/// <see cref="IExtensionSettingsApi"/> wrapper.
/// </summary>
public interface IExtensionRuntimeSettings
{
    /// <summary>Effective value of the full physical path (e.g. "extensions.pisharp-memory.backend"), or null.</summary>
    object? GetRaw(string path);

    /// <summary>Writes the full physical path; a null value removes it. Writer is attributed from the path.</summary>
    Task SetRawAsync(string path, object? value, ExtensionSettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>Removes the full physical path.</summary>
    Task RemoveRawAsync(string path, ExtensionSettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>Fired for any committed settings change (any writer, any key).</summary>
    IDisposable OnChange(Action<ExtensionSettingsChange> handler);

    /// <summary>Layer name that would receive a Source-scope write for the path, or null when unresolved.</summary>
    Task<string?> SourceLayerForAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional first-writer-wins namespace protection implemented by the runtime settings service.
/// The scoped wrapper claims its namespace before the first write so two extensions whose ids
/// normalize to the same namespace fail loudly instead of silently sharing keys.
/// </summary>
public interface IExtensionRuntimeSettingsClaimer
{
    /// <summary>Claims <paramref name="extensionNamespace"/> for <paramref name="sourceId"/>; false when already claimed by another source.</summary>
    bool TryClaimNamespace(string extensionNamespace, string sourceId);
}

/// <summary>Shared helpers for settings/state key validation and JSON conversion.</summary>
public static class ExtensionSettingKeys
{
    /// <summary>Converts a JSON node back to a CLR object (string/bool/long/double/JsonNode), or null for JSON null.</summary>
    public static object? FromJsonNode(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonValue value) return node; // JsonObject / JsonArray stay as nodes
        return value.GetValueKind() switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            JsonValueKind.Number => value.TryGetValue<long>(out var integer) ? (object)integer : value.GetValue<double>(),
            _ => value.ToJsonString()
        };
    }

    /// <summary>Validates a dot-separated settings path; each segment must match [A-Za-z0-9_-].</summary>
    public static void ValidateSettingsKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Settings key is required.", nameof(key));
        var segments = key.Split('.');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new ArgumentException($"Settings key '{key}' contains an empty segment.", nameof(key));
            foreach (var ch in segment)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_')
                    throw new ArgumentException($"Settings key '{key}' contains invalid character '{ch}'.", nameof(key));
            }
        }
    }

    /// <summary>Validates a flat state key: [A-Za-z0-9_.-]{1,128}.</summary>
    public static void ValidateStateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("State key must be between 1 and 128 characters.", nameof(key));
        foreach (var ch in key)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_' and not '.')
                throw new ArgumentException($"State key '{key}' contains invalid character '{ch}'.", nameof(key));
        }
    }

    /// <summary>Converts a value to a JSON node; non-JSON values (Func, streams, ...) throw <see cref="ArgumentException"/>.</summary>
    public static JsonNode? ToJsonNode(object? value)
    {
        if (value is null) return null;
        if (value is JsonNode node) return node.DeepClone();
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText());
        try
        {
            return JsonSerializer.SerializeToNode(value);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            throw new ArgumentException($"Value of type '{value.GetType().FullName}' is not JSON-serializable.", nameof(value), exception);
        }
    }

}
