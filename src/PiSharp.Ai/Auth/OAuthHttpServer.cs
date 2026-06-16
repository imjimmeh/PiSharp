using System.Net;
using System.Text;

namespace PiSharp.Ai.Auth;

public sealed class OAuthHttpServer : IDisposable
{
    private readonly string _host;
    private readonly string _callbackPath;
    private CancellationTokenRegistration _cancelRegistration;
    private readonly TaskCompletionSource<(string Code, string State)?> _codeSource = new();
    private HttpListener? _listener;
    private int _port;
    private volatile int _disposed;

    public int Port => _port;
    public string RedirectUri => $"http://{_host}:{_port}{_callbackPath}";
    public bool IsListening => _listener?.IsListening ?? false;

    public OAuthHttpServer(int port = 0, string host = "127.0.0.1", string callbackPath = "/callback")
    {
        _host = host;
        _port = port;
        _callbackPath = NormalizeCallbackPath(callbackPath);
    }

    private static string NormalizeCallbackPath(string callbackPath)
    {
        if (string.IsNullOrWhiteSpace(callbackPath)) return "/callback";

        var normalized = callbackPath.StartsWith("/", StringComparison.Ordinal)
            ? callbackPath
            : $"/{callbackPath}";
        return normalized.TrimEnd('/');
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartListener();
        _cancelRegistration = cancellationToken.Register(Dispose);
        _ = ListenLoopAsync();
        return Task.CompletedTask;
    }

    private void StartListener()
    {
        var listener = new HttpListener();
        if (_port == 0)
        {
            for (var i = 0; i < 20; i++)
            {
                _port = Random.Shared.Next(50000, 60000);
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add($"http://{_host}:{_port}{_callbackPath}/");
                    listener.Start();
                    _listener = listener;
                    return;
                }
                catch (HttpListenerException)
                {
                    try { listener.Close(); } catch { }
                    listener = new HttpListener();
                    if (i == 19) throw;
                }
            }
        }
        else
        {
            listener.Prefixes.Add($"http://{_host}:{_port}{_callbackPath}/");
            listener.Start();
            _listener = listener;
        }
    }

    private async Task ListenLoopAsync()
    {
        var listener = _listener;
        if (listener is null) return;

        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                _ = HandleRequestAsync(context);
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (request.Url?.AbsolutePath != _callbackPath)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await WriteHtmlAsync(response, ErrorHtml("Callback route not found."));
            return;
        }

        var code = request.QueryString["code"];
        var state = request.QueryString["state"];
        var error = request.QueryString["error"];

        if (error is not null)
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteHtmlAsync(response, ErrorHtml($"Authentication error: {error}"));
            return;
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteHtmlAsync(response, ErrorHtml("Missing code or state parameter."));
            return;
        }

        response.StatusCode = (int)HttpStatusCode.OK;
        await WriteHtmlAsync(response, SuccessHtml("Authentication completed. You can close this window."));
        _codeSource.TrySetResult((code, state));
    }

    public async Task<(string Code, string State)?> WaitForCodeAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var completed = await Task.WhenAny(_codeSource.Task, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed == _codeSource.Task)
                return await _codeSource.Task;
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public void CancelWait()
    {
        _codeSource.TrySetResult(null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelRegistration.Dispose();
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is not null)
        {
            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
        }
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
    {
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.OutputStream.Close();
    }

    private static string ErrorHtml(string message)
        => $"<html><body><h1>Error</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

    private static string SuccessHtml(string message)
        => $"<html><body><h1>Success</h1><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
}
