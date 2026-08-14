namespace PiSharp.Browser.Runtime;

/// <summary>
/// Facade over a single shared browser tab. Actions are serialized with a per-plugin gate so
/// <c>open</c> / <c>run</c> / <c>screenshot</c> / <c>observe</c> never race on the shared tab.
/// The underlying driver is created lazily on first <see cref="OpenAsync"/>.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IBrowserDriverFactory _factory;
    private readonly BrowserToolOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowserDriver? _driver;
    private bool _disposed;

    public BrowserSession(IBrowserDriverFactory factory, BrowserToolOptions options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsOpen => _driver?.IsOpen ?? false;

    public string? CurrentUrl => _driver?.CurrentUrl;

    public string? CurrentTitle => _driver?.CurrentTitle;

    public async Task<(string Url, string Title)> OpenAsync(
        string url, string? waitForSelector, int timeoutMs, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var driver = await EnsureDriverAsync(ct).ConfigureAwait(false);
            return await driver.OpenAsync(url, waitForSelector, timeoutMs, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RunAsync(string script, bool returnByValue, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var driver = RequireOpen();
            return await driver.RunAsync(script, returnByValue, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ScreenshotAsync(bool fullPage, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var driver = RequireOpen();
            return await driver.ScreenshotAsync(fullPage, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ObserveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var driver = RequireOpen();
            return await driver.ObserveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_driver is not null)
            {
                await _driver.DisposeAsync().ConfigureAwait(false);
                _driver = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<IBrowserDriver> EnsureDriverAsync(CancellationToken ct)
    {
        if (_driver is null)
            _driver = await _factory.CreateAsync(ct).ConfigureAwait(false);
        return _driver;
    }

    private IBrowserDriver RequireOpen()
    {
        ThrowIfDisposed();
        var driver = _driver;
        if (driver is null || !driver.IsOpen)
            throw new InvalidOperationException("The browser is not open — call `open` before this action.");
        return driver;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BrowserSession));
    }
}
