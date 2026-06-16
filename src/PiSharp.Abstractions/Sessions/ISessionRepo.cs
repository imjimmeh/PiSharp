namespace PiSharp.Abstractions.Sessions;

public interface ISessionCreateOptions { string? Id { get; } }
public interface ISessionForkOptions { string? EntryId { get; } string? Position { get; } string? Id { get; } }
public sealed record JsonlSessionCreateOptions(string Cwd, string? Id = null, string? ParentSessionPath = null, bool PersistImmediately = false) : ISessionCreateOptions;
public sealed record JsonlSessionListOptions(string? Cwd = null);
public sealed record SessionForkOptions(string? EntryId = null, string? Position = "before", string? Id = null) : ISessionForkOptions;

public interface ISessionRepo<TMetadata, TCreateOptions, TListOptions>
    where TMetadata : ISessionMetadata
    where TCreateOptions : ISessionCreateOptions
{
    Task<ISession<TMetadata>> CreateAsync(TCreateOptions options, CancellationToken cancellationToken = default);
    Task<ISession<TMetadata>> OpenAsync(TMetadata metadata, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TMetadata>> ListAsync(TListOptions? options = default, CancellationToken cancellationToken = default);
    Task DeleteAsync(TMetadata metadata, CancellationToken cancellationToken = default);
    Task<ISession<TMetadata>> ForkAsync(TMetadata source, TCreateOptions createOptions, ISessionForkOptions forkOptions, CancellationToken cancellationToken = default);
}
