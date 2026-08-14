using PiSharp.Browser.Runtime;

namespace PiSharp.Browser.Tests.Fakes;

/// <summary>A hermetic <see cref="IBrowserDriver"/> that never launches a real browser.</summary>
internal sealed class FakeBrowserDriver : IBrowserDriver
{
    public const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    public bool IsOpen { get; set; } = true;
    public string? CurrentUrl { get; set; }
    public string? CurrentTitle { get; set; }
    public bool DisposeCalled { get; private set; }
    public string? LastScript { get; private set; }
    public bool? LastReturnByValue { get; private set; }
    public bool? LastFullPage { get; private set; }
    public string ScriptResult { get; set; } = "\"hello\"";
    public byte[] ScreenshotBytes { get; set; } = Convert.FromBase64String(PngBase64);
    public string Snapshot { get; set; } = "{\"role\": \"root\", \"name\": \"Test Page\"}";
    public Exception? OpenException { get; set; }
    public Exception? RunException { get; set; }
    public Exception? ScreenshotException { get; set; }
    public Exception? ObserveException { get; set; }

    public Task<(string Url, string Title)> OpenAsync(string url, string? waitForSelector, int timeoutMs, CancellationToken ct)
    {
        if (OpenException is not null)
            throw OpenException;
        CurrentUrl = url;
        CurrentTitle = "Open Title";
        return Task.FromResult((url, CurrentTitle!));
    }

    public Task<string> RunAsync(string script, bool returnByValue, CancellationToken ct)
    {
        if (RunException is not null)
            throw RunException;
        LastScript = script;
        LastReturnByValue = returnByValue;
        return Task.FromResult(ScriptResult);
    }

    public Task<byte[]> ScreenshotAsync(bool fullPage, CancellationToken ct)
    {
        if (ScreenshotException is not null)
            throw ScreenshotException;
        LastFullPage = fullPage;
        return Task.FromResult(ScreenshotBytes);
    }

    public Task<string> ObserveAsync(CancellationToken ct)
    {
        if (ObserveException is not null)
            throw ObserveException;
        return Task.FromResult(Snapshot);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalled = true;
        IsOpen = false;
        return default;
    }
}

/// <summary>Returns a configured <see cref="FakeBrowserDriver"/> for session/extension tests.</summary>
internal sealed class FakeBrowserDriverFactory : IBrowserDriverFactory
{
    public FakeBrowserDriver Driver { get; } = new();

    public int CreateCount { get; private set; }

    public Task<IBrowserDriver> CreateAsync(CancellationToken ct)
    {
        CreateCount++;
        return Task.FromResult<IBrowserDriver>(Driver);
    }
}
