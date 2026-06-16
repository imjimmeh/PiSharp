using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.TsBridge.JsonRpc;
using PiSharp.TsBridge.Protocol;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class JsonRpcConnectionTests
{
    [Fact]
    public void JsonRpcRequestsUseVersionTwo()
    {
        var request = new JsonRpcRequest("2.0", "initialize", new { }, "1");
        Assert.Equal("2.0", request.Jsonrpc);
    }

    [Fact]
    public async Task RequestAsyncCompletesWhenCallerSynchronizationContextDoesNotPumpContinuations()
    {
        var input = new ChannelLineReader();
        var output = new AsyncLineWriter();
        await using var connection = new JsonRpcConnection(input, output, NullLoggerFactory.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pumpTask = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), timeout.Token);

        var previousContext = SynchronizationContext.Current;
        Task<JsonElement> requestTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            requestTask = connection.RequestAsync("event", new { name = "input" }, timeout.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        try
        {
            var requestLine = await output.ReadLineAsync(timeout.Token);
            using var requestJson = JsonDocument.Parse(requestLine);
            var requestId = requestJson.RootElement.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(requestId));

            await input.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"" + requestId + "\",\"result\":{\"ok\":true}}", timeout.Token);

            var response = await requestTask.WaitAsync(TimeSpan.FromSeconds(1), timeout.Token);

            Assert.True(response.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await input.CompleteAsync();
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task PumpAsyncLogsInboundRequestMethodAndDuration()
    {
        var input = new ChannelLineReader();
        var output = new AsyncLineWriter();
        var loggerFactory = new RecordingLoggerFactory();
        await using var connection = new JsonRpcConnection(input, output, loggerFactory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pumpTask = connection.PumpAsync(async (request, ct) =>
        {
            handlerStarted.TrySetResult();
            await handlerCanComplete.Task.WaitAsync(ct);
            return new { ok = true };
        }, timeout.Token);

        await input.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"99\",\"method\":\"test_method\",\"params\":{}}", timeout.Token);

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);

        var methodLogged = loggerFactory.Messages.Any(m => m.Contains("test_method", StringComparison.Ordinal));
        Assert.True(methodLogged, $"Expected method 'test_method' to be logged. Logged messages: {string.Join(", ", loggerFactory.Messages)}");

        handlerCanComplete.TrySetResult();

        await Task.Delay(50, timeout.Token);

        var durationLogged = loggerFactory.Messages.Any(m => m.Contains("duration", StringComparison.OrdinalIgnoreCase) || m.Contains("ms", StringComparison.Ordinal));
        Assert.True(durationLogged, $"Expected duration to be logged. Logged messages: {string.Join(", ", loggerFactory.Messages)}");

        await input.CompleteAsync();
        try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RequestAsyncLogsDurationOnCompletion()
    {
        var input = new ChannelLineReader();
        var output = new AsyncLineWriter();
        var loggerFactory = new RecordingLoggerFactory();
        await using var connection = new JsonRpcConnection(input, output, loggerFactory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pumpTask = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), timeout.Token);

        var requestTask = connection.RequestAsync("test_request", new { }, timeout.Token);

        var requestLine = await output.ReadLineAsync(timeout.Token);
        using var requestJson = JsonDocument.Parse(requestLine);
        var requestId = requestJson.RootElement.GetProperty("id").GetString();

        await input.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"" + requestId + "\",\"result\":{}}", timeout.Token);

        await requestTask.WaitAsync(TimeSpan.FromSeconds(1), timeout.Token);

        var responseLog = loggerFactory.Messages.LastOrDefault(m => m.Contains("response") && m.Contains("test_request"));
        Assert.NotNull(responseLog);
        Assert.True(responseLog.Contains("ms", StringComparison.Ordinal) || responseLog.Contains("duration", StringComparison.OrdinalIgnoreCase),
            $"Expected response log to include duration. Got: {responseLog}");

        await input.CompleteAsync();
        try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
            => await _lines.Writer.WriteAsync(line, cancellationToken);

        public ValueTask CompleteAsync()
        {
            _lines.Writer.TryWrite(null);
            _lines.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            => await _lines.Reader.ReadAsync(cancellationToken);
    }

    private sealed class AsyncLineWriter : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public override Encoding Encoding => Encoding.UTF8;

        public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
            => await _lines.Reader.ReadAsync(cancellationToken);

        public override Task WriteLineAsync(string? value)
            => Task.Run(async () => await _lines.Writer.WriteAsync(value ?? string.Empty));
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class RecordingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            messages.Add(formatter(state, exception));
        }
    }
}
