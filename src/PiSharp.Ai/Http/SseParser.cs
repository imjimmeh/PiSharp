using System.Runtime.CompilerServices;
using System.Text;

namespace PiSharp.Ai.Http;

public sealed record SseEvent(string? Event, string Data, string? Id = null, int? Retry = null);

public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? eventName = null;
        string? id = null;
        int? retry = null;
        var data = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    if (data[^1] == '\n') data.Length--;
                    yield return new SseEvent(eventName, data.ToString(), id, retry);
                    eventName = null;
                    id = null;
                    retry = null;
                    data.Clear();
                }
                continue;
            }

            if (line[0] == ':') continue;

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    break;
                case "id":
                    id = value;
                    break;
                case "retry" when int.TryParse(value, out var retryValue):
                    retry = retryValue;
                    break;
            }
        }

        if (data.Length > 0)
        {
            if (data[^1] == '\n') data.Length--;
            yield return new SseEvent(eventName, data.ToString(), id, retry);
        }
    }

    public static IAsyncEnumerable<SseEvent> ReadAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return ReadAsync(new MemoryStream(bytes), cancellationToken);
    }
}
