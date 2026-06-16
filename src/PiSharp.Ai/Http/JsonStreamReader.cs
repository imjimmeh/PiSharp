using System.Text;
using System.Text.Json;

namespace PiSharp.Ai.Http;

public static class JsonStreamReader
{
    public static async Task<JsonElement> ReadObjectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return ParseObjectOrEmpty(text);
    }

    public static JsonElement ParseObjectOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyObject();

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            try
            {
                using var repaired = JsonDocument.Parse(RepairControlCharacters(json));
                return repaired.RootElement.Clone();
            }
            catch (JsonException)
            {
                return EmptyObject();
            }
        }
    }

    public static string RepairControlCharacters(string json)
    {
        var builder = new StringBuilder(json.Length);
        var inString = false;
        var escaping = false;

        foreach (var ch in json)
        {
            if (!inString)
            {
                builder.Append(ch);
                if (ch == '"') inString = true;
                continue;
            }

            if (escaping)
            {
                builder.Append(ch);
                escaping = false;
                continue;
            }

            switch (ch)
            {
                case '\\':
                    builder.Append(ch);
                    escaping = true;
                    break;
                case '"':
                    builder.Append(ch);
                    inString = false;
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch)) builder.Append($"\\u{(int)ch:x4}");
                    else builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
