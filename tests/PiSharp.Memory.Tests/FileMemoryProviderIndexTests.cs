using PiSharp.Memory.Abstractions;
using Microsoft.Extensions.Logging;
using PiSharp.Memory.Backends.File;
using Xunit;

namespace PiSharp.Memory.Tests;

/// <summary>
/// Index/flush contract: reads serve from a lazily-built in-memory index (no per-op JSONL
/// re-parse) and mutations batch into a single flush instead of rewriting records.jsonl on
/// every op. Durability is explicit: <see cref="FileMemoryProvider.FlushAsync"/> and
/// dispose persist all pending writes; unflushed writes are lost on hard crash.
/// </summary>
public sealed class FileMemoryProviderIndexTests : IDisposable
{
    private readonly string _root = MemoryTestHelpers.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private FileMemoryProvider Provider(string cwd = @"C:\proj\one") => new(_root, MemoryProjectKeys.Encode(cwd));

    private static MemoryRecord Record(string key, string? content = null)
        => new(key, MemoryKind.Fact, "Title " + key, content ?? "Content " + key, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task PutBatch_DoesNotRewriteRecordsFilePerMutation()
    {
        var provider = Provider();
        try
        {
            for (var i = 0; i < 50; i++)
                await provider.PutAsync(MemoryScope.Project, Record("facts/k" + i, content: "value " + i));

            // 50 in-memory upserts must not translate into 50 full records.jsonl rewrites.
            Assert.InRange(provider.RecordsWriteCount, 0, 2);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task PutThenGet_ServesLatestValueWithoutWaitingForFlush()
    {
        var provider = Provider();
        try
        {
            await provider.PutAsync(MemoryScope.Project, Record("facts/live", content: "first"));
            // Overwrite before any flush: the very next read must still see the newest value.
            await provider.PutAsync(MemoryScope.Project, Record("facts/live", content: "second"));

            var stored = await provider.GetAsync(MemoryScope.Project, "facts/live");
            Assert.NotNull(stored);
            Assert.Equal("second", stored!.Content);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Flush_PersistsAllPendingWrites_ForReload()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/a", content: "alpha"));
        await provider.PutAsync(MemoryScope.Project, Record("facts/b", content: "beta"));
        await provider.FlushAsync();

        var reloaded = Provider();
        try
        {
            Assert.Equal("alpha", (await reloaded.GetAsync(MemoryScope.Project, "facts/a"))!.Content);
            Assert.Equal("beta", (await reloaded.GetAsync(MemoryScope.Project, "facts/b"))!.Content);
            Assert.Equal(2, (await reloaded.ListAsync(MemoryScope.Project, new MemoryQuery())).Count);
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_FlushesPendingWritesBeforeReturning()
    {
        var provider = Provider();
        await provider.PutAsync(MemoryScope.Project, Record("facts/durable", content: "kept"));
        await provider.DisposeAsync();

        var reloaded = Provider();
        try
        {
            Assert.Equal("kept", (await reloaded.GetAsync(MemoryScope.Project, "facts/durable"))!.Content);
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task Load_SkipsMalformedLineAndKeepsValidRecords()
    {
        var logger = new ListLogger();
        var provider = new FileMemoryProvider(_root, MemoryProjectKeys.Encode(@"C:\proj\one"), logger);
        try
        {
            var path = Path.Combine(_root, "projects", MemoryProjectKeys.Encode(@"C:\proj\one"), FileMemoryProvider.RecordsFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                "this is not json\n" +
                "{\"recordKey\":\"facts/ok\",\"kind\":\"fact\",\"title\":\"Title\",\"content\":\"Content\",\"tags\":[],\"createdAt\":\"2024-01-01T00:00:00+00:00\",\"updatedAt\":\"2024-01-01T00:00:00+00:00\"}\n");

            var records = await provider.ListAsync(MemoryScope.Project, new MemoryQuery());

            var record = Assert.Single(records);
            Assert.Equal("facts/ok", record.RecordKey);
            var warning = Assert.Single(logger.Messages);
            Assert.Contains("line 1", warning);
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    private sealed class ListLogger : ILogger<FileMemoryProvider>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
