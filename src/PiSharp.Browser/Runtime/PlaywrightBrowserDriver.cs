using System.Text.Json;
using Microsoft.Playwright;

namespace PiSharp.Browser.Runtime;

/// <summary>
/// Production <see cref="IBrowserDriver"/> backed by Microsoft.Playwright against headless
/// Chromium. The browser is created lazily on first <c>open</c> and owned by this driver; disposal
/// closes the Chromium process.
/// </summary>
internal sealed class PlaywrightBrowserDriver : IBrowserDriver
{
    private readonly BrowserToolOptions _options;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public PlaywrightBrowserDriver(BrowserToolOptions options)
    {
        _options = options;
    }

    public bool IsOpen => _page is not null;

    public string? CurrentUrl => _page?.Url;

    public string? CurrentTitle => null;

    public async Task<(string Url, string Title)> OpenAsync(string url, string? waitForSelector, int timeoutMs, CancellationToken ct)
    {
        var page = await EnsurePageAsync(ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        await page.GotoAsync(url, new PageGotoOptions { Timeout = timeoutMs, WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(waitForSelector))
        {
            ct.ThrowIfCancellationRequested();
            await page.WaitForSelectorAsync(waitForSelector, new PageWaitForSelectorOptions
            {
                Timeout = timeoutMs,
                State = WaitForSelectorState.Visible
            }).ConfigureAwait(false);
        }

        var title = await page.TitleAsync().ConfigureAwait(false);
        return (page.Url, title);
    }

    public async Task<string> RunAsync(string script, bool returnByValue, CancellationToken ct)
    {
        var page = RequirePage();
        ct.ThrowIfCancellationRequested();
        var result = await page.EvaluateAsync<JsonElement?>(script).ConfigureAwait(false);
        return SerializeJsResult(result);
    }

    public async Task<byte[]> ScreenshotAsync(bool fullPage, CancellationToken ct)
    {
        var page = RequirePage();
        ct.ThrowIfCancellationRequested();
        return await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Png,
            FullPage = fullPage
        }).ConfigureAwait(false);
    }

#pragma warning disable CS0612 // Playwright marks Accessibility.SnapshotAsync obsolete but it remains the standard a11y tree API (plan §4.1)
    public async Task<string> ObserveAsync(CancellationToken ct)
    {
        var page = RequirePage();
        ct.ThrowIfCancellationRequested();
        var snapshot = await page.Accessibility.SnapshotAsync().ConfigureAwait(false);
        if (snapshot is null)
            return "(no accessibility tree)";
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
    }
#pragma warning restore CS0612

    public async ValueTask DisposeAsync()
    {
        if (_page is not null)
        {
            await _page.CloseAsync().ConfigureAwait(false);
            _page = null;
        }
        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser = null;
        }
        // IPlaywright has no async dispose in this Playwright version; closing the browser is the
        // meaningful cleanup (it terminates the Chromium child process).
        _playwright = null;
    }

    private async Task<IPage> EnsurePageAsync(CancellationToken ct)
    {
        if (_page is not null)
            return _page;

        ct.ThrowIfCancellationRequested();
        _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);

        var launchOptions = new BrowserTypeLaunchOptions { Headless = _options.Headless };
        // The CDP `Endpoint` (relay to the user's own Chrome) is a client-side follow-on (plan §13),
        // not wired in v1 — headless launch only.
        _browser ??= await _playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);

        _page = await _browser.NewPageAsync().ConfigureAwait(false);
        return _page;
    }

    private IPage RequirePage()
        => _page ?? throw new InvalidOperationException("The browser is not open — call `open` before this action.");

    private static string SerializeJsResult(JsonElement? result)
    {
        if (result is null || result.Value.ValueKind is JsonValueKind.Null)
            return "null";
        if (result.Value.ValueKind is JsonValueKind.Undefined)
            return "undefined";
        return JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>Creates <see cref="PlaywrightBrowserDriver"/> instances for a <see cref="BrowserSession"/>.</summary>
internal sealed class PlaywrightBrowserDriverFactory : IBrowserDriverFactory
{
    private readonly BrowserToolOptions _options;

    public PlaywrightBrowserDriverFactory(BrowserToolOptions options)
    {
        _options = options;
    }

    public Task<IBrowserDriver> CreateAsync(CancellationToken ct)
        => Task.FromResult<IBrowserDriver>(new PlaywrightBrowserDriver(_options));
}
