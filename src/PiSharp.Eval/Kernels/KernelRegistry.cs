namespace PiSharp.Eval.Kernels;

/// <summary>
/// Options for a per-runtime <see cref="KernelRegistry"/>.
/// </summary>
public sealed record KernelRegistryOptions
{
    /// <summary>Session id used to scope persisted snapshots. Defaults to "default".</summary>
    public string SessionId { get; init; } = "default";

    /// <summary>
    /// Restore the latest persisted snapshot when starting a kernel for a session
    /// (<c>eval.kernel.restoreOnStart</c>, default true).
    /// </summary>
    public bool RestoreOnStart { get; init; } = true;

    /// <summary>
    /// Provider of the latest persisted snapshot for a (kernelName, sessionId) pair, used
    /// for restore-on-start. Wired by the extension to <see cref="KernelSnapshotStore"/>.
    /// </summary>
    public Func<string, string, CancellationToken, Task<KernelSnapshot?>>? SnapshotProvider { get; init; }
}

/// <summary>
/// Per-session kernel registry. Creates/starts kernels by name from
/// <see cref="EvalKernelRegistry.Factories"/>, applies start options (cwd, loopback bridge,
/// restore snapshot), and serializes executions on a per-kernel gate so the model cannot
/// interleave kernel state. Disposes all kernels on session shutdown / extension unload.
/// </summary>
public sealed class KernelRegistry : IAsyncDisposable
{
    private readonly string _cwd;
    private readonly Func<string, IKernelToolBridge?>? _toolBridgeFactory;
    private readonly KernelRegistryOptions _options;
    private readonly Dictionary<string, KernelEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public KernelRegistry(string cwd, IKernelToolBridge? toolBridge = null, KernelRegistryOptions? options = null)
        : this(cwd, _ => toolBridge, options)
    {
    }

    public KernelRegistry(string cwd, Func<string, IKernelToolBridge?>? toolBridgeFactory, KernelRegistryOptions? options = null)
    {
        _cwd = cwd;
        _toolBridgeFactory = toolBridgeFactory;
        _options = options ?? new KernelRegistryOptions();
    }

    public string SessionId => _options.SessionId;

    /// <summary>All running kernels (snapshot).</summary>
    public IReadOnlyList<IKernel> Kernels
    {
        get { lock (_entries) return _entries.Values.Select(e => e.Kernel).ToArray(); }
    }

    public bool Has(string kernelName)
    {
        lock (_entries) return _entries.ContainsKey(kernelName);
    }

    public IKernel? Get(string kernelName)
    {
        lock (_entries) return _entries.TryGetValue(kernelName, out var entry) ? entry.Kernel : null;
    }

    /// <summary>
    /// Returns the running kernel for <paramref name="kernelName"/>, creating and starting it
    /// (with restore-on-start) on first use. Concurrent calls for the same name are serialized.
    /// </summary>
    public async Task<IKernel> GetOrStartAsync(string kernelName, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_entries)
            {
                if (_entries.TryGetValue(kernelName, out var existing)) return existing.Kernel;
            }

            var factory = EvalKernelRegistry.FindFactory(kernelName)
                ?? throw new InvalidOperationException(
                    $"Unknown eval kernel '{kernelName}'. Registered kernels: {(EvalKernelRegistry.Factories.Count == 0 ? "(none)" : string.Join(", ", EvalKernelRegistry.Factories.Select(f => f.KernelName)))}.");

            var kernel = factory.Create();
            KernelSnapshot? restore = null;
            if (_options.RestoreOnStart && _options.SnapshotProvider is not null)
            {
                restore = await _options.SnapshotProvider(kernelName, _options.SessionId, ct);
            }

            await kernel.StartAsync(new KernelStartOptions(
                Cwd: _cwd,
                ToolBridge: _toolBridgeFactory?.Invoke(kernelName),
                SessionId: _options.SessionId,
                RestoreSnapshot: restore), ct);

            lock (_entries) _entries[kernelName] = new KernelEntry(kernel, new SemaphoreSlim(1, 1));
            return kernel;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Executes <paramref name="code"/> on the named kernel, serialized by the per-kernel
    /// gate: a second, concurrent <c>eval</c> on the same kernel waits until the first
    /// completes, so executions cannot interleave kernel state.
    /// </summary>
    public async Task<KernelExecuteResult> ExecuteAsync(string kernelName, string code,
        KernelExecuteOptions? options = null, CancellationToken ct = default)
    {
        var kernel = await GetOrStartAsync(kernelName, ct);
        KernelEntry entry;
        lock (_entries) entry = _entries[kernelName];

        await entry.Gate.WaitAsync(ct);
        try
        {
            return await kernel.ExecuteAsync(code, options, ct);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<KernelSnapshot> SnapshotAsync(string kernelName, CancellationToken ct = default)
    {
        var kernel = await GetOrStartAsync(kernelName, ct);
        return await kernel.SnapshotAsync(ct);
    }

    public async Task ResetAsync(string kernelName, CancellationToken ct = default)
    {
        var kernel = await GetOrStartAsync(kernelName, ct);
        await kernel.ResetAsync(ct);
    }

    /// <summary>
    /// Snapshots every running kernel (via <paramref name="beforeDispose"/>, if provided — the
    /// extension uses this to persist final state) and disposes all kernels.
    /// </summary>
    public async Task DisposeAllAsync(
        Func<IKernel, CancellationToken, Task>? beforeDispose = null,
        CancellationToken ct = default)
    {
        IKernel[] kernels;
        await _gate.WaitAsync(ct);
        try
        {
            lock (_entries)
            {
                kernels = _entries.Values.Select(e => e.Kernel).ToArray();
                _entries.Clear();
            }
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }

        foreach (var kernel in kernels)
        {
            try
            {
                if (beforeDispose is not null) await beforeDispose(kernel, ct);
            }
            catch (Exception)
            {
                // Snapshot failure must not prevent kernel teardown.
            }
            try
            {
                await kernel.DisposeAsync();
            }
            catch (Exception)
            {
                // Dispose failure must not prevent the remaining kernels from being disposed.
            }
        }
    }

    public async ValueTask DisposeAsync()
        => await DisposeAllAsync();

    private sealed record KernelEntry(IKernel Kernel, SemaphoreSlim Gate);
}
