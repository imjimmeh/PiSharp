using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;
using PiSharp.Plugins.ProtocolJsonRpc.Process;

namespace PiSharp.Plugins.Lsp;

public sealed record LspServerStatus(
    string Language,
    string? CommandLine,
    bool Running,
    bool Initialized,
    int OpenFileCount,
    DateTimeOffset? StartedAt,
    string? LastError);

/// <summary>
/// The LSP request muxer: one server process per configured language, requests multiplexed
/// by id over the server's single stdio pipe. Spawns lazily, respawns after a crash,
/// tracks open files, and reports status for the extension events/status surface.
/// </summary>
public sealed class LspServerRegistry : IAsyncDisposable
{
    private static readonly JsonElement ClientCapabilities = JsonDocument.Parse(
        """{"workspace":{"workspaceFolders":true},"textDocument":{"synchronization":{"dynamicRegistration":false,"willSave":false,"didSave":false}}}""")
        .RootElement.Clone();

    private readonly IReadOnlyDictionary<string, LanguageServerConfig> _configs;
    private readonly Dictionary<string, string> _extensionIndex;
    private readonly string _rootPath;
    private readonly Uri _rootUri;
    private readonly IServerProcessFactory _processFactory;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ServerEntry> _servers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastActivity = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private int _disposed;

    public LspServerRegistry(
        IReadOnlyDictionary<string, LanguageServerConfig> configs,
        string rootPath,
        TimeSpan idleTimeout,
        IServerProcessFactory? processFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        _configs = configs;
        _rootPath = Path.GetFullPath(rootPath);
        _rootUri = new Uri(PathToUri(_rootPath).AbsoluteUri);
        _idleTimeout = idleTimeout;
        _processFactory = processFactory ?? new SystemServerProcessFactory();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<LspServerRegistry>() ?? NullLogger<LspServerRegistry>.Instance;
        _extensionIndex = BuildExtensionIndex(configs);
    }

    public event Action<LspServerStatus>? ServerStatusChanged;

    /// <summary>Config languages (ordered), for "unknown language" error messages.</summary>
    public IReadOnlyCollection<string> Languages => _configs.Keys.ToArray();

    public IReadOnlyDictionary<string, LspServerStatus> Status
    {
        get
        {
            lock (_servers)
            {
                var snapshot = new Dictionary<string, LspServerStatus>(StringComparer.Ordinal);
                foreach (var (language, entry) in _servers)
                {
                    snapshot[language] = ToStatus(entry);
                }

                return snapshot;
            }
        }
    }

    /// <summary>Resolves a file path to a configured language via the config extension tables, or null.</summary>
    public string? ResolveLanguage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return null;
        return _extensionIndex.TryGetValue(extension, out var language) ? language : null;
    }

    /// <summary>No-spawn lookup used by the diagnostics middleware fast path.</summary>
    public bool TryGetClient(string language, out LspClient? client)
    {
        lock (_servers)
        {
            if (_servers.TryGetValue(language, out var entry) && !entry.Client.Server.HasExited)
            {
                client = entry.Client;
                return true;
            }

            client = null;
            return false;
        }
    }

    /// <summary>
    /// Lazy spawn + <c>initialize</c>/<c>initialized</c> handshake; respawns when the previous
    /// process crashed. No <c>didOpen</c> — use <see cref="OpenFileAsync"/> for file-backed ops.
    /// </summary>
    public async Task<LspClient> GetClientAsync(string language, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_configs.TryGetValue(language, out var config))
        {
            throw new KeyNotFoundException(
                $"No language server configured for language '{language}'. Configured languages: {string.Join(", ", _configs.Keys)}.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_servers.TryGetValue(language, out var existing))
            {
                if (!existing.Client.Server.HasExited)
                {
                    Touch(language);
                    return existing.Client;
                }

                _logger.LogWarning("lsp server '{Language}' exited; respawning.", language);
                lock (_servers) _servers.Remove(language);
                lock (_lastActivity) _lastActivity.Remove(language);
                FireStatus(language);
                await DisposeEntryCoreAsync(language, existing).ConfigureAwait(false);
            }

            var client = await SpawnAndInitializeAsync(language, config, ct).ConfigureAwait(false);
            Touch(language);
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resolves the file's language, ensures a server (spawn + handshake), sends
    /// <c>didOpen</c> with the current file text, and tracks the file as open.
    /// </summary>
    public async Task<LspClient> OpenFileAsync(string absolutePath, CancellationToken ct = default)
    {
        var language = ResolveLanguage(absolutePath)
            ?? throw new KeyNotFoundException($"No language server configured for file extension '{Path.GetExtension(absolutePath)}'. Configured languages: {string.Join(", ", _configs.Keys)}.");

        var client = await GetClientAsync(language, ct).ConfigureAwait(false);
        var config = _configs[language];
        var uri = PathToUri(absolutePath).AbsoluteUri;
        var text = await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);

        lock (_servers)
        {
            if (_servers.TryGetValue(language, out var entry) && entry.OpenFiles.Add(uri))
            {
                FireStatus(language);
            }
        }

        await client.DidOpenAsync(uri, text, config.LanguageId ?? language, ct).ConfigureAwait(false);
        return client;
    }

    public async Task CloseFileAsync(string absolutePath, CancellationToken ct = default)
    {
        var language = ResolveLanguage(absolutePath);
        if (language is null) return;
        var uri = PathToUri(absolutePath).AbsoluteUri;

        lock (_servers)
        {
            if (!_servers.TryGetValue(language, out var entry) || !entry.OpenFiles.Remove(uri))
            {
                return;
            }

            FireStatus(language);
        }

        if (TryGetClient(language, out var client) && client is not null)
        {
            await client.DidCloseAsync(uri, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes servers with no traffic for longer than the configured idle timeout.</summary>
    public async Task DisconnectIdleAsync(CancellationToken ct = default)
    {
        var idleLanguages = new List<string>();
        lock (_lastActivity)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (language, lastActivity) in _lastActivity)
            {
                if (now - lastActivity >= _idleTimeout) idleLanguages.Add(language);
            }
        }

        foreach (var language in idleLanguages)
        {
            ServerEntry? entry = null;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_servers)
                {
                    if (!_servers.TryGetValue(language, out entry)) continue;
                    _servers.Remove(language);
                }

                lock (_lastActivity) _lastActivity.Remove(language);
                FireStatus(language);
            }
            finally
            {
                _gate.Release();
            }

            if (entry is not null)
            {
                await DisposeEntryCoreAsync(language, entry).ConfigureAwait(false);
            }
        }
    }

    private async Task<LspClient> SpawnAndInitializeAsync(string language, LanguageServerConfig config, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(config.TimeoutMs);

        var server = new ManagedRpcServer(language, config.Command, _processFactory, _loggerFactory);
        var client = new LspClient(server, config.TimeoutMs, _loggerFactory);
        server.EnqueueInboundHandler((message, _) => HandleServerRequestAsync(client, config, message));

        lock (_servers) _servers[language] = new ServerEntry(client, config);
        FireStatus(language);

        try
        {
            var initializationOptions = config.Init is not null && config.Init.TryGetValue("initializationOptions", out var options) && options is not null
                ? options
                : null;
            _ = await client.InitializeAsync(_rootUri, ClientCapabilities, initializationOptions, timeoutCts.Token).ConfigureAwait(false);
            await client.NotifyInitializedAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_servers) _servers.Remove(language);
            await DisposeEntryCoreAsync(language, new ServerEntry(client, config)).ConfigureAwait(false);
            FireStatus(language);
            throw;
        }

        lock (_servers)
        {
            if (_servers.TryGetValue(language, out var entry)) entry.Initialized = true;
        }

        FireStatus(language);
        return client;
    }

    private async Task<object?> HandleServerRequestAsync(LspClient client, LanguageServerConfig config, InboundRpcMessage message)
    {
        var parameters = message.Params ?? default;

        if (message.Method == "textDocument/publishDiagnostics")
        {
            client.RouteNotification(message.Method, parameters);
            return null;
        }

        if (message.IsNotification)
        {
            _logger.LogDebug("lsp '{Language}' notification {Method}", client.Server.Key, message.Method);
            return null;
        }

        return message.Method switch
        {
            "workspace/configuration" => AnswerConfiguration(config, parameters),
            "client/registerCapability" => new { },
            "workspace/applyEdit" => new { applied = false },
            _ => new JsonRpcError(-32601, $"Method not found: {message.Method}"),
        };
    }

    private static object AnswerConfiguration(LanguageServerConfig config, JsonElement parameters)
    {
        var items = new List<object?>();
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                var section = item.TryGetProperty("section", out var sectionElement) ? sectionElement.GetString() : null;
                if (section is not null
                    && config.Init is not null
                    && config.Init.TryGetValue("workspaceConfiguration", out var map)
                    && map is JsonElement mapElement
                    && mapElement.ValueKind == JsonValueKind.Object
                    && mapElement.TryGetProperty(section, out var value))
                {
                    items.Add(JsonSerializer.Deserialize<object?>(value.GetRawText()));
                }
                else
                {
                    items.Add(null);
                }
            }
        }

        return new { items };
    }

    private void Touch(string language)
    {
        lock (_lastActivity) _lastActivity[language] = DateTimeOffset.UtcNow;
    }

    private async Task DisposeEntryCoreAsync(string language, ServerEntry entry)
    {
        try
        {
            await entry.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Dispose of lsp server '{Language}' failed; process already gone.", language);
        }
    }

    private void FireStatus(string language)
    {
        LspServerStatus? status;
        lock (_servers)
        {
            if (!_servers.TryGetValue(language, out var entry)) return;
            status = ToStatus(entry);
        }

        ServerStatusChanged?.Invoke(status);
    }

    private LspServerStatus ToStatus(ServerEntry entry)
    {
        var running = !entry.Client.Server.HasExited;
        return new LspServerStatus(
            Language: entry.Client.Server.Key,
            CommandLine: string.Join(' ', entry.Config.Command),
            Running: running,
            Initialized: running && entry.Initialized,
            OpenFileCount: entry.OpenFiles.Count,
            StartedAt: entry.StartedAt,
            LastError: running ? null : "server process exited");
    }

    private static Dictionary<string, string> BuildExtensionIndex(IReadOnlyDictionary<string, LanguageServerConfig> configs)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, config) in configs)
        {
            foreach (var extension in config.Extensions)
            {
                var normalized = extension.StartsWith('.') ? extension : "." + extension;
                index.TryAdd(normalized, language);
            }
        }

        return index;
    }

    internal static Uri PathToUri(string path)
        => new(Path.GetFullPath(path));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        List<ServerEntry> entries;
        lock (_servers)
        {
            entries = _servers.Values.ToList();
            _servers.Clear();
        }

        lock (_lastActivity) _lastActivity.Clear();

        foreach (var entry in entries)
        {
            await DisposeEntryCoreAsync(entry.Client.Server.Key, entry).ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private sealed class ServerEntry(LspClient client, LanguageServerConfig config)
    {
        public LspClient Client { get; } = client;

        public LanguageServerConfig Config { get; } = config;

        public HashSet<string> OpenFiles { get; } = new(StringComparer.Ordinal);

        public bool Initialized { get; set; }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    }
}
