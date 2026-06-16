using System.Text.Json;
using PiSharp.TsBridge.Protocol;

namespace PiSharp.TsBridge;

internal interface ITsBridgeClient : IAsyncDisposable
{
    bool IsStarted { get; }
    IReadOnlyList<string> RecentStandardError { get; }

    Task StartAsync(
        Func<JsonRpcRequest, CancellationToken, Task<object?>> requestHandler,
        object initializePayload,
        CancellationToken cancellationToken = default);

    Task<JsonElement> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);

    Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
}
