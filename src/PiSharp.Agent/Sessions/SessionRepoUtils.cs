using PiSharp.Abstractions;
using PiSharp.Abstractions.Errors;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;

namespace PiSharp.Agent.Sessions;

public static class SessionRepoUtils
{
    public static string CreateEntryId(Func<string, bool> exists)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.CreateVersion7().ToString("N")[..16];
            if (!exists(id)) return id;
        }
        return Guid.CreateVersion7().ToString();
    }

    public static string EncodeCwd(string cwd) => $"--{cwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-')}--";

    public static async Task<IReadOnlyList<SessionTreeEntry>> GetEntriesToForkAsync<TMetadata>(ISessionStorage<TMetadata> storage, ISessionForkOptions options, CancellationToken cancellationToken = default) where TMetadata : ISessionMetadata
    {
        if (options.EntryId is null) return await storage.GetEntriesAsync(cancellationToken);
        var target = await storage.GetEntryAsync(options.EntryId, cancellationToken) ?? throw new InvalidOperationException($"Entry {options.EntryId} not found");
        if ((options.Position ?? "before") == "at") return await storage.GetPathToRootAsync(target.Id, cancellationToken);
        if (target is not MessageEntry { Message: UserMessage }) throw new InvalidOperationException($"Entry {options.EntryId} is not a user message");
        return await storage.GetPathToRootAsync(target.ParentId, cancellationToken);
    }

    public static async Task<T> GetOrThrowAsync<T>(this Task<Result<T, FileError>> task)
    {
        var result = await task;
        return result.IsOk ? result.Value : throw result.Error;
    }

    public static async Task AsTaskOrThrowAsync(this Task<Result<Unit, FileError>> task)
    {
        var result = await task;
        if (result.IsErr) throw result.Error;
    }
}
