using System.Text.Json;

namespace PiSharp.InternalUrls;

/// <summary>
/// Dotted/index path navigation over a <see cref="JsonElement"/> JSON document.
/// Used by <c>agent://&lt;id&gt;/&lt;field.path&gt;</c> to pull a single field
/// out of a subagent's structured result.
/// </summary>
public static class InternalUrlFieldPath
{
    /// <summary>
    /// Selects the value at a dotted path such as <c>findings.0.path</c>.
    /// Object properties are addressed by name; array elements by a decimal
    /// index token. Returns false when any segment is missing, is not an
    /// object property or in-range array index, or the path is malformed.
    /// </summary>
    public static bool TrySelect(JsonElement root, string path, out JsonElement result)
    {
        result = default;
        if (string.IsNullOrEmpty(path))
        {
            result = root;
            return true;
        }

        var current = root;
        foreach (var rawToken in path.Split('.'))
        {
            var token = rawToken;
            if (token.Length == 0) return false;

            // Trailing array-index suffix support, e.g. "items[0].name" → "items". 
            var bracketIndex = token.IndexOf('[');
            if (bracketIndex > 0)
            {
                var indexToken = token[(bracketIndex + 1)..];
                if (!indexToken.EndsWith(']') || indexToken.Length < 2) return false;
                var property = token[..bracketIndex];
                var indexText = indexToken[..^1];
                if (!int.TryParse(indexText, out var index) || index < 0) return false;
                if (!TryDescend(current, property, index, out current)) return false;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var prop))
            {
                current = prop;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(token, out var elementIndex)
                && elementIndex >= 0
                && elementIndex < current.GetArrayLength())
            {
                current = current[elementIndex];
                continue;
            }

            return false;
        }

        result = current;
        return true;
    }

    private static bool TryDescend(JsonElement current, string property, int index, out JsonElement result)
    {
        result = default;
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Array || index < 0 || index >= value.GetArrayLength()) return false;
        result = value[index];
        return true;
    }
}
