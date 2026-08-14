namespace PiSharp.Browser.Runtime;

/// <summary>
/// Thin seam over the browser-automation surface (Playwright) so the tool and session
/// logic can be unit-tested hermetically without launching a real browser. The production
/// implementation is <see cref="PlaywrightBrowserDriver"/>; tests substitute a fake.
/// </summary>
public interface IBrowserDriver : IAsyncDisposable
{
    bool IsOpen { get; }

    string? CurrentUrl { get; }

    string? CurrentTitle { get; }

    /// <summary>Navigates the shared tab to <paramref name="url"/>, optionally waiting for a CSS selector.</summary>
    Task<(string Url, string Title)> OpenAsync(string url, string? waitForSelector, int timeoutMs, CancellationToken ct);

    /// <summary>Evaluates <paramref name="script"/> in the page and returns a JSON-serialized result.</summary>
    Task<string> RunAsync(string script, bool returnByValue, CancellationToken ct);

    /// <summary>Captures the current page (viewport or full page) as PNG bytes.</summary>
    Task<byte[]> ScreenshotAsync(bool fullPage, CancellationToken ct);

    /// <summary>Returns an accessibility snapshot of the current page as text.</summary>
    Task<string> ObserveAsync(CancellationToken ct);
}

/// <summary>
/// Creates lazily-instantiated <see cref="IBrowserDriver"/> instances. A factory indirection keeps
/// the <see cref="BrowserSession"/> hermetically testable (a fake factory returns a fake driver).
/// </summary>
public interface IBrowserDriverFactory
{
    Task<IBrowserDriver> CreateAsync(CancellationToken ct);
}
