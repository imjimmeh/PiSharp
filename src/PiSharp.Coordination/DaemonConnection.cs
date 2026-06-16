namespace PiSharp.Coordination;

public sealed class DaemonConnection : IAsyncDisposable
{
    private readonly CoordinationDaemon? _ownedDaemon;

    public CoordinationEndpoint Endpoint { get; }
    public CoordinationClient Client { get; }
    public string RepoRoot { get; }

    internal DaemonConnection(CoordinationEndpoint endpoint, CoordinationClient client, string repoRoot, CoordinationDaemon? ownedDaemon)
    {
        Endpoint = endpoint;
        Client = client;
        RepoRoot = repoRoot;
        _ownedDaemon = ownedDaemon;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedDaemon is not null)
            await _ownedDaemon.DisposeAsync();
    }
}
