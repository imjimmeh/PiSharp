using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;
using Xunit;

namespace PiSharp.Plugins.ProtocolJsonRpc.Tests;

/// <summary>
/// Framing and lifecycle edge cases not covered by the shared Lsp.Tests project:
/// split frames across reads, multiple frames per read, CRLF/bare-LF headers, malformed
/// header and oversized-frame guards, the fake process-factory round-trip, and
/// <c>Kill(entireProcessTree)</c> on dispose. All traffic flows over the in-memory fake
/// process pipes — no real processes.
/// </summary>
public sealed class TransportEdgeCaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // ---- framing: split/multiple/CRLF-LF/malformed/oversized (drives the connection directly) ----

    [Fact]
    public async Task ResponseFramesSplitAcrossReadsAreAssembled()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var request = connection.RequestAsync("echo", new { value = 42 }, CancellationToken.None);
            var (id, _) = await ReadClientRequestAsync(process.ServerInput);

            // Send the response header + the first half of the payload.
            var body = Encoding.UTF8.GetBytes(JsonFrame(id, """{"ok":true,"value":42}"""));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            var half = body.Length / 2;
            await process.ServerOutput.WriteAsync(header.Concat(body.Take(half)).ToArray());
            await process.ServerOutput.FlushAsync();

            // Give the pump a moment to read the partial body before writing the rest.
            await Task.Delay(20);
            await process.ServerOutput.WriteAsync(body.Skip(half).ToArray());
            await process.ServerOutput.FlushAsync();

            var result = await request.WaitAsync(Timeout);
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(42, result.GetProperty("value").GetInt32());
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task MultipleResponseFramesInOneWriteEachResolveTheirOwnRequest()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var first = connection.RequestAsync("one", null, CancellationToken.None);
            var (firstId, _) = await ReadClientRequestAsync(process.ServerInput);
            var second = connection.RequestAsync("two", null, CancellationToken.None);
            var (secondId, _) = await ReadClientRequestAsync(process.ServerInput);

            // Both response frames concatenated into a single write.
            var frame1 = Frame(JsonFrame(firstId, """{"tag":"first"}"""));
            var frame2 = Frame(JsonFrame(secondId, """{"tag":"second"}"""));
            await process.ServerOutput.WriteAsync(frame1.Concat(frame2).ToArray());
            await process.ServerOutput.FlushAsync();

            Assert.Equal("first", (await first.WaitAsync(Timeout)).GetProperty("tag").GetString());
            Assert.Equal("second", (await second.WaitAsync(Timeout)).GetProperty("tag").GetString());
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Theory]
    [InlineData("Content-Length: {0}\r\n\r\n")]              // CRLF header terminator per spec
    [InlineData("Content-Length: {0}\n\n")]                  // bare-LF terminator tolerated
    public async Task HeaderLineEndingsCrlfAndBareLfAreAccepted(string headerFormat)
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var request = connection.RequestAsync("echo", new { value = 7 }, CancellationToken.None);
            var (id, _) = await ReadClientRequestAsync(process.ServerInput);

            var body = Encoding.UTF8.GetBytes(JsonFrame(id, """{"echoed":7}"""));
            var header = Encoding.ASCII.GetBytes(string.Format(headerFormat, body.Length));
            await process.ServerOutput.WriteAsync(header.Concat(body).ToArray());
            await process.ServerOutput.FlushAsync();

            var result = await request.WaitAsync(Timeout);
            Assert.Equal(7, result.GetProperty("echoed").GetInt32());
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task MissingContentLengthHeaderFaultsPendingWithInvalidData()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var request = connection.RequestAsync("echo", null, CancellationToken.None);
            await ReadClientRequestAsync(process.ServerInput);

            // A header block with no Content-Length: the terminator is present but the
            // required header is absent.
            await process.ServerOutput.WriteAsync(Encoding.ASCII.GetBytes("\r\n\r\n{\"id\":1,\"result\":{}}"));
            await process.ServerOutput.FlushAsync();

            await Assert.ThrowsAsync<InvalidDataException>(() => request.WaitAsync(Timeout));
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task MalformedContentLengthValueFaultsPendingWithInvalidData()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var request = connection.RequestAsync("echo", null, CancellationToken.None);
            await ReadClientRequestAsync(process.ServerInput);

            await process.ServerOutput.WriteAsync(Encoding.ASCII.GetBytes("Content-Length: abc\r\n\r\n"));
            await process.ServerOutput.FlushAsync();

            await Assert.ThrowsAsync<InvalidDataException>(() => request.WaitAsync(Timeout));
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task OversizedFrameIsRejectedAndFaultsPending()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var request = connection.RequestAsync("echo", null, CancellationToken.None);
            await ReadClientRequestAsync(process.ServerInput);

            // More than the MaxFrameBytes guard (64 MiB).
            var oversized = FramedJsonRpcConnection.MaxFrameBytes + 1;
            await process.ServerOutput.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {oversized}\r\n\r\n"));
            await process.ServerOutput.FlushAsync();

            await Assert.ThrowsAsync<InvalidDataException>(() => request.WaitAsync(Timeout));
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task OutOfOrderResponsesCorrelateByPendingId()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var first = connection.RequestAsync("one", null, CancellationToken.None);
            var (firstId, _) = await ReadClientRequestAsync(process.ServerInput);
            var second = connection.RequestAsync("two", null, CancellationToken.None);
            var (secondId, _) = await ReadClientRequestAsync(process.ServerInput);

            // Respond in the reverse order: second first, then first.
            await process.ServerOutput.WriteAsync(Frame(JsonFrame(secondId, """{"tag":"second"}""")));
            await process.ServerOutput.FlushAsync();
            await process.ServerOutput.WriteAsync(Frame(JsonFrame(firstId, """{"tag":"first"}""")));
            await process.ServerOutput.FlushAsync();

            Assert.Equal("second", (await second.WaitAsync(Timeout)).GetProperty("tag").GetString());
            Assert.Equal("first", (await first.WaitAsync(Timeout)).GetProperty("tag").GetString());
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task NotificationsNeverResolvePending()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var request = connection.RequestAsync("echo", null, CancellationToken.None);
            var (id, _) = await ReadClientRequestAsync(process.ServerInput);

            // A notification frame (no id) arrives; it must NOT complete the pending request.
            await process.ServerOutput.WriteAsync(Frame("""{"jsonrpc":"2.0","method":"window/logMessage","params":{"message":"hi"}}"""));
            await process.ServerOutput.FlushAsync();
            await Task.Delay(50);
            Assert.False(request.IsCompleted);

            // Only the matching response resolves it.
            await process.ServerOutput.WriteAsync(Frame(JsonFrame(id, """{"ok":true}""")));
            await process.ServerOutput.FlushAsync();
            Assert.True((await request.WaitAsync(Timeout)).GetProperty("ok").GetBoolean());
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task InboundRequestGetsCannedResponseFromHandler()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync(
            (message, _) => message.Method == "workspace/configuration"
                ? Task.FromResult<object?>(new { sections = new[] { "typescript" } })
                : Task.FromResult<object?>(null),
            CancellationToken.None);

        try
        {
            // Peer sends a request to the client; the canned handler answers it.
            await process.ServerOutput.WriteAsync(Frame("""{"jsonrpc":"2.0","id":"9","method":"workspace/configuration","params":{"section":"typescript"}}"""));
            await process.ServerOutput.FlushAsync();

            var response = await ReadFramedMessageAsync(process.ServerInput);
            using var doc = JsonDocument.Parse(response);
            Assert.Equal("9", doc.RootElement.GetProperty("id").GetString());
            Assert.Equal("typescript", doc.RootElement.GetProperty("result").GetProperty("sections")[0].GetString());
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task PumpCloseFaultsEveryPendingRequestWithIOException()
    {
        var process = new FakeServerProcess(new ProcessStartInfo("fake"));
        var connection = new FramedJsonRpcConnection(process.StandardOutput, process.StandardInput, NullLoggerFactory.Instance);
        var pump = connection.PumpAsync((_, _) => Task.FromResult<object?>(null), CancellationToken.None);

        try
        {
            var first = connection.RequestAsync("one", null, CancellationToken.None);
            await ReadClientRequestAsync(process.ServerInput);
            var second = connection.RequestAsync("two", null, CancellationToken.None);
            await ReadClientRequestAsync(process.ServerInput);

            // Peer disappears: EOF on the input stream faults every pending request.
            process.SimulateExit();

            await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(Timeout));
            await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(Timeout));
        }
        finally
        {
            await connection.DisposeAsync();
            process.Dispose();
            await IgnorePumpExit(pump);
        }
    }

    [Fact]
    public async Task ManagedRpcServerRoundTripsThroughFakeProcessFactory()
    {
        var factory = new FakeServerProcessFactory();
        var server = new ManagedRpcServer("test", new[] { "fake-lsp", "--flag" }, factory, NullLoggerFactory.Instance);

        try
        {
            var process = factory.Processes.Single();
            Assert.Equal("fake-lsp", process.StartInfo.FileName);
            Assert.Equal("--flag", process.StartInfo.ArgumentList.Single());

            var request = server.RequestAsync("initialize", new { processId = 1 }, CancellationToken.None);
            var message = await ReadFramedMessageAsync(process.ServerInput);
            using var doc = JsonDocument.Parse(message);
            Assert.Equal("initialize", doc.RootElement.GetProperty("method").GetString());

            // Respond so the request completes.
            var id = doc.RootElement.GetProperty("id").GetRawText();
            await process.ServerOutput.WriteAsync(Frame(JsonFrame(id, """{"capabilities":{}}""")));
            await process.ServerOutput.FlushAsync();

            var result = await request.WaitAsync(Timeout);
            Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").ValueKind);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsyncInvokesKillWithEntireProcessTreeOnDispose()
    {
        var factory = new FakeServerProcessFactory();
        var server = new ManagedRpcServer("test", new[] { "fake-lsp" }, factory, NullLoggerFactory.Instance);
        var process = factory.Processes.Single();

        Assert.False(process.KillCalled);
        await server.DisposeAsync();

        Assert.True(process.KillCalled);
        Assert.True(process.LastKillEntireProcessTree);
    }

    [Fact]
    public async Task DoesNotKillAlreadyExitedProcessOnDispose()
    {
        var factory = new FakeServerProcessFactory();
        var server = new ManagedRpcServer("test", new[] { "fake-lsp" }, factory, NullLoggerFactory.Instance);
        var process = factory.Processes.Single();

        process.SimulateExit();
        Assert.True(server.HasExited);

        await server.DisposeAsync();
        Assert.False(process.KillCalled);
    }

    // ---- helpers ----
    private static string JsonFrame(string id, string resultJson)
        => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}";

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
    }

    /// <summary>Reads the next framed client message and returns (id, body bytes).</summary>
    private static async Task<(string Id, byte[] Body)> ReadClientRequestAsync(Stream input)
    {
        var message = await ReadFramedMessageAsync(input);
        using var doc = JsonDocument.Parse(message);
        return (doc.RootElement.GetProperty("id").GetRawText(), message);
    }

    private static async Task<byte[]> ReadFramedMessageAsync(Stream input)
    {
        var headers = new MemoryStream();
        var trailing = new byte[4];
        var trailingLength = 0;
        int read;
        while ((read = await ReadByteAsync(input)) != -1)
        {
            var b = (byte)read;
            headers.WriteByte(b);
            if (trailingLength < 4) trailing[trailingLength++] = b;
            else
            {
                trailing[0] = trailing[1];
                trailing[1] = trailing[2];
                trailing[2] = trailing[3];
                trailing[3] = b;
            }
            var window = Math.Min(trailingLength, 4);
            if (EndsWith(trailing, window, "\r\n\r\n") || EndsWith(trailing, window, "\n\n"))
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headers.ToArray());
        var length = 0;
        foreach (var line in headerText.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(trimmed[(trimmed.IndexOf(':') + 1)..].Trim());
                break;
            }
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var n = await input.ReadAsync(buffer.AsMemory(offset, length - offset));
            if (n == 0) throw new EndOfStreamException("Client closed before sending a full frame.");
            offset += n;
        }

        return buffer;
    }

    private static async Task<int> ReadByteAsync(Stream input)
    {
        var buffer = new byte[1];
        var read = await input.ReadAsync(buffer);
        return read == 0 ? -1 : buffer[0];
    }

    private static bool EndsWith(byte[] buffer, int length, string suffix)
    {
        if (length < suffix.Length) return false;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (buffer[length - suffix.Length + i] != (byte)suffix[i]) return false;
        }
        return true;
    }

    private static async Task IgnorePumpExit(Task pump)
    {
        try { await pump.WaitAsync(Timeout); }
        catch (Exception) { /* pump exits with IO/InvalidData on close — expected */ }
    }
}
