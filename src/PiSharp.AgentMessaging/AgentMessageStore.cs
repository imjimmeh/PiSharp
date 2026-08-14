using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.AgentMessaging;

/// <summary>
/// JSONL append-only outbox for undelivered agent messages, surviving daemon
/// restarts. Each line is either a message envelope or a tombstone
/// (<c>{"tombstone": messageId}</c>); mutations rewrite the file atomically
/// (temp file + move). All operations are serialized under one lock.
/// </summary>
public sealed class AgentMessageStore : IAsyncDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public AgentMessageStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string FilePath => Path.Combine(_directory, "agent-messages.jsonl");

    /// <summary>Appends a message envelope to the outbox.</summary>
    public async Task AppendAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AppendLineAsync(new MessageLine(Message: message), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the outbox, dropping tombstones and deduplicating by message id.
    /// Replaying the same file twice yields the same set (idempotent).
    /// </summary>
    public async Task<IReadOnlyList<AgentMessage>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messages = new Dictionary<string, AgentMessage>(StringComparer.Ordinal);
            var tombstones = new HashSet<string>(StringComparer.Ordinal);

            if (!File.Exists(FilePath))
                return [];

            foreach (var line in await File.ReadAllLinesAsync(FilePath, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                MessageLine? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<MessageLine>(line, AgentMessagingJson.Options);
                }
                catch (JsonException)
                {
                    continue; // tolerate a partially-written trailing line
                }

                if (entry is null)
                    continue;

                if (entry.Tombstone is not null)
                {
                    tombstones.Add(entry.Tombstone);
                    messages.Remove(entry.Tombstone);
                    continue;
                }

                if (entry.Message is not null && !tombstones.Contains(entry.Message.MessageId))
                    messages.TryAdd(entry.Message.MessageId, entry.Message);
            }

            return messages.Values
                .OrderBy(m => m.Timestamp)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks a message failed (TTL expiry or undeliverable), removing it from
    /// the outbox via a tombstone.
    /// </summary>
    public async Task MarkFailedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var remaining = new List<MessageLine>();
            if (File.Exists(FilePath))
            {
                foreach (var line in await File.ReadAllLinesAsync(FilePath, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    MessageLine? entry = null;
                    try
                    {
                        entry = JsonSerializer.Deserialize<MessageLine>(line, AgentMessagingJson.Options);
                    }
                    catch (JsonException)
                    {
                        continue; // tolerate torn trailing lines; the file is only written by this class
                    }

                    if (entry?.Message is not null && entry.Message.MessageId == messageId)
                        continue; // drop the expired message

                    remaining.Add(entry!);
                }
            }

            remaining.Add(new MessageLine(Tombstone: messageId));
            await RewriteAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Fails queued messages older than <paramref name="ttlHours"/>; returns the
    /// number of messages failed.
    /// </summary>
    public async Task<int> CleanupExpiredAsync(int ttlHours, CancellationToken cancellationToken = default)
    {
        if (ttlHours <= 0)
            return 0;

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(ttlHours);
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var expired = loaded
            .Where(m => m.Status == AgentMessageStatus.Queued && m.Timestamp < cutoff)
            .ToArray();

        foreach (var message in expired)
            await MarkFailedAsync(message.MessageId, cancellationToken).ConfigureAwait(false);

        return expired.Length;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        await Task.CompletedTask;
    }

    private async Task AppendLineAsync(MessageLine line, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(line, AgentMessagingJson.Options);
        await File.AppendAllTextAsync(FilePath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private async Task RewriteAsync(IReadOnlyList<MessageLine> lines, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var tempPath = FilePath + ".tmp";
        await File.WriteAllLinesAsync(
            tempPath,
            lines.Select(line => JsonSerializer.Serialize(line, AgentMessagingJson.Options)),
            cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    private sealed record MessageLine(
        [property: JsonPropertyName("message")] AgentMessage? Message = null,
        [property: JsonPropertyName("tombstone")] string? Tombstone = null);
}
