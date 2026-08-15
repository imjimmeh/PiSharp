using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PiSharp.Logging;

/// <summary>
/// Renders one structured <c>ILogger</c> entry as a JSON-lines object:
/// <c>{ ts, level, category, eventId, message, state, exception }</c>. The <c>message</c> is
/// rendered from the structured template; named state keys (excluding the message template
/// placeholder <c>{OriginalFormat}</c>) are serialized verbatim under <c>state</c>. Values that
/// are not directly JSON-serializable fall back to their <c>ToString()</c> representation so a
/// single hostile value can never break the line.
/// </summary>
public static class JsonLogFormatter
{
    public const string FormatEnvironmentVariable = "PISHARP_LOG_FORMAT";
    public const string FormatValueJson = "json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format<TState>(
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var (message, namedState) = RenderState(state, formatter, exception);
        return JsonSerializer.Serialize(new LogLine(
            DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
            logLevel.ToString(),
            category,
            eventId.Id == 0 && string.IsNullOrEmpty(eventId.Name) ? null : new EventIdLine(eventId.Id, string.IsNullOrEmpty(eventId.Name) ? null : eventId.Name),
            message,
            namedState.Count == 0 ? null : namedState,
            exception is null ? null : new ExceptionLine(exception.GetType().FullName, exception.Message, exception.ToString())), Options);
    }

    private static (string Message, Dictionary<string, object?> State) RenderState<TState>(
        TState state,
        Func<TState, Exception?, string> formatter,
        Exception? exception)
    {
        var named = new Dictionary<string, object?>(StringComparer.Ordinal);
        var message = formatter(state, exception);
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var (key, value) in pairs)
            {
                if (key == "{OriginalFormat}" || string.IsNullOrEmpty(key)) continue;
                named[key] = NormalizeValue(value);
            }
        }

        return (message, named);
    }

    private static object? NormalizeValue(object? value) => value switch
    {
        null => null,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or System.DateTime or System.DateTimeOffset
            or System.TimeSpan or System.Guid => value,
        _ => value.ToString() ?? string.Empty
    };

    private sealed record LogLine(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("eventId")] EventIdLine? EventId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("state")] Dictionary<string, object?>? State,
        [property: JsonPropertyName("exception")] ExceptionLine? Exception);

    private sealed record EventIdLine(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record ExceptionLine(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("stackTrace")] string StackTrace);
}
