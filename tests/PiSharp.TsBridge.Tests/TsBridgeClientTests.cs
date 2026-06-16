using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.TsBridge.JsonRpc;
using Xunit;

namespace PiSharp.TsBridge.Tests;

public sealed class TsBridgeClientTests
{
    [Fact]
    public async Task StartAsyncSendsInitializeRequestWithBridgeManifest()
    {
        await using var fixture = new ClientFixture(respondToInitialize: true);
        var client = fixture.CreateClient();

        await client.StartAsync(
            (_, _) => Task.FromResult<object?>(null),
            new { bridgeManifest = new { version = 1 } },
            CancellationToken.None);

        var request = await fixture.Writer.WaitForMethodAsync("initialize");
        Assert.True(request.TryGetProperty("params", out var parameters));
        Assert.True(parameters.TryGetProperty("bridgeManifest", out _));
    }

    [Fact]
    public async Task RecentStandardErrorCapturesProcessErrorLines()
    {
        await using var fixture = new ClientFixture(respondToInitialize: true);
        var client = fixture.CreateClient();

        await client.StartAsync(
            (_, _) => Task.FromResult<object?>(null),
            new { bridgeManifest = new { version = 1 } },
            CancellationToken.None);

        fixture.Process.EmitStandardError("node failed loudly");

        Assert.Contains("node failed loudly", client.RecentStandardError);
    }

    [Fact]
    public async Task RequestAsyncFailsWhenConnectionClosesBeforeResponse()
    {
        await using var fixture = new ClientFixture(respondToInitialize: true);
        var client = fixture.CreateClient();
        await client.StartAsync(
            (_, _) => Task.FromResult<object?>(null),
            new { bridgeManifest = new { version = 1 } },
            CancellationToken.None);

        var pendingRequest = client.RequestAsync("load_extensions", new { extensionPaths = Array.Empty<string>() }, CancellationToken.None);
        await fixture.Writer.WaitForMethodAsync("load_extensions");
        fixture.Reader.Complete();

        var exception = await Assert.ThrowsAsync<IOException>(async () => await pendingRequest);
        Assert.Contains("closed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ClientFixture : IAsyncDisposable
    {
        public ChannelLineReader Reader { get; }
        public RespondingLineWriter Writer { get; }
        public FakeBridgeProcess Process { get; }

        public ClientFixture(bool respondToInitialize)
        {
            Reader = new ChannelLineReader();
            Writer = new RespondingLineWriter(respondToInitialize);
            Process = new FakeBridgeProcess(Reader, Writer);
        }

        public NodeTsBridgeClient CreateClient()
            => new(new TsBridgeOptions(), new FakeBridgeProcessFactory(Process));

        public async ValueTask DisposeAsync()
        {
            Reader.Complete();
            await Task.Yield();
        }
    }

    private sealed class FakeBridgeProcessFactory(FakeBridgeProcess process) : IBridgeProcessFactory
    {
        public IBridgeProcess Start(ProcessStartInfo startInfo) => process;
    }

    private sealed class FakeBridgeProcess : IBridgeProcess
    {
        public TextReader StandardOutput { get; }
        public TextWriter StandardInput { get; }
        public bool HasExited { get; private set; }
        public event Action<string?>? StandardErrorReceived;

        public FakeBridgeProcess(ChannelLineReader reader, RespondingLineWriter writer)
        {
            StandardOutput = reader;
            StandardInput = writer.AttachReader(reader);
        }

        public void BeginErrorReadLine()
        {
        }

        public void EmitStandardError(string line) => StandardErrorReceived?.Invoke(line);

        public void Kill(bool entireProcessTree) => HasExited = true;

        public void Dispose() => HasExited = true;
    }

    private sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (!await _lines.Reader.WaitToReadAsync(cancellationToken)) return null;
            return _lines.Reader.TryRead(out var line) ? line : null;
        }

        public ValueTask EnqueueLineAsync(string line) => _lines.Writer.WriteAsync(line);

        public void Complete() => _lines.Writer.TryComplete();
    }

    private sealed class RespondingLineWriter(bool respondToInitialize) : TextWriter
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new(StringComparer.Ordinal);
        private ChannelLineReader? _reader;

        public override Encoding Encoding => Encoding.UTF8;

        public RespondingLineWriter AttachReader(ChannelLineReader reader)
        {
            _reader = reader;
            return this;
        }

        public override async Task WriteLineAsync(string? value)
        {
            if (value is null) return;

            using var document = JsonDocument.Parse(value);
            var root = document.RootElement.Clone();
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            RequestFor(method).TrySetResult(root);

            if (respondToInitialize && method == "initialize")
            {
                var id = root.GetProperty("id").GetString();
                await _reader!.EnqueueLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{}}}}");
            }
        }

        public Task<JsonElement> WaitForMethodAsync(string method)
            => RequestFor(method).Task.WaitAsync(TimeSpan.FromSeconds(5));

        private TaskCompletionSource<JsonElement> RequestFor(string method)
            => _requests.GetOrAdd(method, _ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
