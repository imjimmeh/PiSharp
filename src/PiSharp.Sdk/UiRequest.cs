namespace PiSharp.Sdk;

/// <summary>
/// A UI request bridged from the daemon over the <c>ui_request</c> event lane. A headless SDK
/// consumer answers it with <see cref="UiResponse"/> via
/// <see cref="SessionConnection.SetUiRequestHandler"/> (or opts into automatic decline with
/// <see cref="AttachOptions.AutoHandleUiRequests"/>).
/// </summary>
public sealed record UiRequest(
    string RequestId,
    string Kind,
    string Title,
    string? Message,
    IReadOnlyList<string>? Options,
    object? Component,
    string? ExtensionId = null);

/// <summary>Response to a <see cref="UiRequest"/>: either a value or a cancellation.</summary>
public sealed record UiResponse(string RequestId, object? Value = null, bool Cancelled = false);
