using PiSharp.Agent.Core;
using PiSharp.Browser.Tools;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-browser",
    Name = "PiSharp Browser",
    Version = "0.1.0",
    Description = "Drive a headless Chromium browser: open URLs, run JavaScript, take screenshots, and read an accessibility snapshot.")]

namespace PiSharp.Browser.Runtime;

/// <summary>
/// The <c>browser</c> plugin. Gated off by default: reads <c>PISHARP_BROWSER_ENABLED</c> (env
/// fallback for <c>extensions.pisharp-browser.enabled</c> before P02 merges); when enabled it
/// registers the single <c>browser</c> tool and owns the shared <see cref="BrowserSession"/>.
/// </summary>
public sealed class BrowserExtension : IExtension, IAsyncDisposable
{
    private readonly BrowserOptions _options;
    private readonly IBrowserDriverFactory? _driverFactory;
    private BrowserSession? _session;
    private IDisposable? _shutdownHandler;
    private bool _disposed;

    /// <summary>Default instance resolved from the environment.</summary>
    public BrowserExtension()
    {
        _options = BrowserOptions.Resolve();
    }

    /// <summary>Test constructor: explicit options and an optional fake driver factory.</summary>
    internal BrowserExtension(BrowserOptions options, IBrowserDriverFactory? driverFactory = null)
    {
        _options = options;
        _driverFactory = driverFactory;
    }

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        if (_options.Enabled)
        {
            _session = new BrowserSession(_driverFactory ?? new PlaywrightBrowserDriverFactory(_options.Tool), _options.Tool);
            var session = _session;

            api.RegisterTool(new ExtensionToolRegistration(
                BrowserTool.Name,
                "Browser",
                "Drive a headless Chromium browser. `open` navigates to a URL (optionally waiting for a CSS selector); `run` evaluates JavaScript and returns the serialized result; `screenshot` captures the current page as an image attachment; `observe` returns an accessibility snapshot of the current page. The browser is a single shared tab that persists for the session.",
                BrowserTool.BuildParametersSchema(),
                (toolCallId, parameters, ct, onUpdate)
                    => BrowserTool.ExecuteAsync(toolCallId, parameters, ct, onUpdate, session, _options.Tool),
                ExecutionMode: ToolExecutionMode.Sequential));

            _shutdownHandler = api.On(ExtensionEventNames.SessionShutdown, async (_, _) => await DisposeAsync().ConfigureAwait(false));
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _shutdownHandler?.Dispose();
        _shutdownHandler = null;

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}
