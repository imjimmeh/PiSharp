using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Agent.Sessions;

public sealed class JsonlSessionRepo(IFileSystem fs, string sessionsRoot, bool writeLeafEntries = false, ILoggerFactory? loggerFactory = null) : ISessionRepo<JsonlSessionMetadata, JsonlSessionCreateOptions, JsonlSessionListOptions>
{
    private const int MaxConcurrentMetadataLoads = 10;
    private readonly ILogger _logger = loggerFactory?.CreateLogger<JsonlSessionRepo>() ?? NullLogger<JsonlSessionRepo>.Instance;

    public async Task<ISession<JsonlSessionMetadata>> CreateAsync(JsonlSessionCreateOptions options, CancellationToken cancellationToken = default)
    {
        var id = options.Id ?? Guid.CreateVersion7().ToString();
        var dir = await GetSessionDirAsync(options.Cwd, cancellationToken);
        await fs.CreateDirectoryAsync(dir, true, cancellationToken).GetOrThrowAsync();
        var path = await fs.JoinPathAsync([dir, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fff}_{id}.jsonl"], cancellationToken).GetOrThrowAsync();
        return new Session<JsonlSessionMetadata>(
            await JsonlSessionStorage.CreateAsync(fs, path, options.Cwd, id, options.ParentSessionPath, writeLeafEntries, options.PersistImmediately, cancellationToken, loggerFactory),
            loggerFactory);
    }

    public async Task<ISession<JsonlSessionMetadata>> OpenAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default)
        => new Session<JsonlSessionMetadata>(await JsonlSessionStorage.OpenAsync(fs, metadata.Path, writeLeafEntries, cancellationToken, loggerFactory), loggerFactory);

    public async Task<IReadOnlyList<JsonlSessionMetadata>> ListAsync(JsonlSessionListOptions? options = null, CancellationToken cancellationToken = default)
    {
        var dirs = options?.Cwd is null
            ? await ListSessionDirsAsync(cancellationToken)
            : [await GetSessionDirAsync(options.Cwd, cancellationToken)];
        var files = new List<string>();
        foreach (var dir in dirs)
        {
            if (!await fs.ExistsAsync(dir, cancellationToken).GetOrThrowAsync()) continue;
            foreach (var file in await fs.ListDirectoryAsync(dir, cancellationToken).GetOrThrowAsync())
            {
                if (file.Kind == FileKind.Directory || !file.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(file.Path);
            }
        }
        var sessions = await LoadMetadataManyAsync(files, cancellationToken);
        return sessions.OrderByDescending(session => session.ModifiedAt).ToArray();
    }

    public Task DeleteAsync(JsonlSessionMetadata metadata, CancellationToken cancellationToken = default)
        => fs.RemoveAsync(metadata.Path, false, true, cancellationToken).AsTaskOrThrowAsync();

    private async Task<IReadOnlyList<JsonlSessionMetadata>> LoadMetadataManyAsync(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return [];
        var sessions = new JsonlSessionMetadata?[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentMetadataLoads, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                try { sessions[index] = await JsonlSessionStorage.LoadMetadataAsync(fs, files[index], token); }
                catch (InvalidOperationException ex) { _logger.LogDebug(ex, "Invalid session entry skipped"); }
            });
        return sessions.OfType<JsonlSessionMetadata>().ToArray();
    }

    public async Task<ISession<JsonlSessionMetadata>> ForkAsync(JsonlSessionMetadata source, JsonlSessionCreateOptions createOptions, ISessionForkOptions forkOptions, CancellationToken cancellationToken = default)
    {
        var sourceSession = await OpenAsync(source, cancellationToken);
        var entries = await SessionRepoUtils.GetEntriesToForkAsync(sourceSession.Storage, forkOptions, cancellationToken);
        var fork = await CreateAsync(createOptions with { ParentSessionPath = createOptions.ParentSessionPath ?? source.Path, PersistImmediately = true }, cancellationToken);
        foreach (var entry in entries.Where(entry => writeLeafEntries || entry is not LeafEntry)) await fork.Storage.AppendEntryAsync(entry, cancellationToken);
        return fork;
    }

    private async Task<string> GetSessionDirAsync(string cwd, CancellationToken cancellationToken)
        => await fs.JoinPathAsync([await fs.AbsolutePathAsync(sessionsRoot, cancellationToken).GetOrThrowAsync(), SessionRepoUtils.EncodeCwd(cwd)], cancellationToken).GetOrThrowAsync();

    private async Task<IReadOnlyList<string>> ListSessionDirsAsync(CancellationToken cancellationToken)
    {
        var root = await fs.AbsolutePathAsync(sessionsRoot, cancellationToken).GetOrThrowAsync();
        if (!await fs.ExistsAsync(root, cancellationToken).GetOrThrowAsync()) return [];
        return (await fs.ListDirectoryAsync(root, cancellationToken).GetOrThrowAsync()).Where(entry => entry.Kind == FileKind.Directory).Select(entry => entry.Path).ToArray();
    }
}
