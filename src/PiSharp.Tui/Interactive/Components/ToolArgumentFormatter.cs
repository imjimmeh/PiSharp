using System.Globalization;
using System.Text.Json;

namespace PiSharp.Tui.Interactive.Components;

internal static class ToolArgumentFormatter
{
    public static string Format(JsonElement? args, bool indented)
    {
        if (args is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return string.Empty;
        if (value.ValueKind != JsonValueKind.Object) return FormatValue(value);

        var pairs = value.EnumerateObject()
            .Select(property => indented
                ? $"- {property.Name}: {FormatValue(property.Value)}"
                : $"{property.Name}: {FormatValue(property.Value)}")
            .ToArray();
        return indented ? string.Join('\n', pairs) : string.Join(", ", pairs);
    }

    private static string FormatValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => true.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            JsonValueKind.False => false.ToString(CultureInfo.InvariantCulture).ToLowerInvariant(),
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => value.GetRawText()
        };
}
