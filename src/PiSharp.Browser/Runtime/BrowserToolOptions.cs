namespace PiSharp.Browser.Runtime;

/// <summary>
/// Options that control how the shared browser tab behaves at runtime.
/// </summary>
public sealed record BrowserToolOptions(
    string? Endpoint = null,                       // CDP endpoint for a future relay; null = launch headless
    bool Headless = true,
    int DefaultNavigationTimeoutMs = 30000);
