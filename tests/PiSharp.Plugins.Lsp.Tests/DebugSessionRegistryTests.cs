using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.Debug;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;
using Xunit;

namespace PiSharp.Plugins.Lsp.Tests;

/// <summary>
/// DAP session state machine, adapter-event routing, idle disposal, and the full scripted
/// debug lifecycle (attach → setBreakpoints → continue → stopped → threads → stackTrace →
/// scopes → variables → evaluate → disconnect) through <see cref="DebugSessionRegistry"/>.
/// </summary>
public sealed class DebugSessionRegistryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string RootPath = @"C:\work\demo";

    [Fact]
    public async Task FullScriptedDebugLifecycle()
    {
        var harness = CreateAdapterHarness(language: "go", attachConfig: """{"request":"launch","mode":"debug","program":"${path}"}""");
        try
        {
            // attach: initialize → attach with interpolated body
            var session = await AttachAndBindAsync(harness, "go", Input(Op: "attach", Language: "go", Path: "main.go"));

            Assert.Equal("Attached", session.State);
            Assert.Equal("go", session.Language);
            Assert.Equal("main.go", session.DebuggeeLabel);

            var initialize = harness.Server.Received.Single(r => r.MethodOrCommand == "initialize");
            Assert.Equal("pisharp-debug", initialize.ParamsOrArguments.GetProperty("adapterID").GetString());

            var attach = harness.Server.Received.Single(r => r.MethodOrCommand == "launch");
            Assert.Equal(@"C:\work\demo\main.go", attach.ParamsOrArguments.GetProperty("program").GetString());
            Assert.Equal("debug", attach.ParamsOrArguments.GetProperty("mode").GetString());

            var client = await harness.Registry.GetSessionAsync(session.SessionId, CancellationToken.None).WaitAsync(Timeout);

            // setBreakpoints: 1-based lines pass through unchanged
            await client.SetBreakpointsAsync(@"C:\work\demo\main.go", new[] { 3, 7 }, CancellationToken.None).WaitAsync(Timeout);
            var setBreakpoints = harness.Server.Received.Last(r => r.MethodOrCommand == "setBreakpoints");
            var breakpoints = setBreakpoints.ParamsOrArguments.GetProperty("breakpoints");
            Assert.Equal(3, breakpoints[0].GetProperty("line").GetInt32());
            Assert.Equal(7, breakpoints[1].GetProperty("line").GetInt32());

            // continue → adapter emits stopped → state Stopped
            await client.ContinueAsync(threadId: null, CancellationToken.None).WaitAsync(Timeout);
            await harness.Server.SendEventAsync("stopped", new { reason = "breakpoint", threadId = 1 });
            await WaitUntilAsync(() => harness.Updated.Any(info => info.State == "Stopped"));
            Assert.Equal("Stopped", harness.Registry.List().Single(info => info.SessionId == session.SessionId).State);

            // threads / stackTrace / scopes / variables / evaluate
            var threads = await client.ThreadsAsync(CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal(1, threads.GetProperty("threads").GetArrayLength());

            var stackTrace = await client.StackTraceAsync(1, levels: null, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal("main", stackTrace.GetProperty("stackFrames")[0].GetProperty("name").GetString());

            var scopes = await client.ScopesAsync(1000, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal("Local", scopes.GetProperty("scopes")[0].GetProperty("name").GetString());

            var variables = await client.VariablesAsync(1001, start: 0, count: 2, CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal(2, variables.GetProperty("variables").GetArrayLength());

            var evaluate = await client.EvaluateAsync("x + 1", frameId: 1000, context: "repl", CancellationToken.None).WaitAsync(Timeout);
            Assert.Equal("3", evaluate.GetProperty("result").GetString());

            // disconnect → session disposes and is removed from the registry
            await harness.Registry.DisconnectAsync(session.SessionId, CancellationToken.None).WaitAsync(Timeout);
            Assert.DoesNotContain(harness.Registry.List(), info => info.SessionId == session.SessionId);
            Assert.Contains(harness.Server.Received, r => r.MethodOrCommand == "disconnect");

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => harness.Registry.GetSessionAsync(session.SessionId, CancellationToken.None));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task AttachRequestOverridesConfigBody()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach","host":"127.0.0.1","port":5678}""");
        try
        {
            var request = JsonSerializer.SerializeToElement(new { port = 9999 });
            var input = Input(Op: "attach", Language: "python", Request: request);
            var session = await AttachAndBindAsync(harness, "python", input);

            Assert.Equal("Attached", session.State);
            var attach = harness.Server.Received.Single(r => r.MethodOrCommand == "attach");
            Assert.Equal(9999, attach.ParamsOrArguments.GetProperty("port").GetInt32());
            Assert.Equal("127.0.0.1", attach.ParamsOrArguments.GetProperty("host").GetString());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task StoppedAndContinuedEventsDriveStateTransitions()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach"}""");
        try
        {
            var session = await AttachAndBindAsync(harness, "python", Input(Op: "attach", Language: "python"));
            Assert.Equal("Attached", session.State);

            await harness.Server.SendEventAsync("stopped", new { reason = "breakpoint", threadId = 1 });
            await WaitUntilAsync(() => harness.Updated.Any(info => info.State == "Stopped"));

            await harness.Server.SendEventAsync("continued", new { threadId = 1 });
            await WaitUntilAsync(() => harness.Updated.Any(info => info.State == "Running"));

            Assert.Equal("Running", harness.Registry.List().Single(info => info.SessionId == session.SessionId).State);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExitedEventDisposesSession()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach"}""");
        try
        {
            var session = await AttachAndBindAsync(harness, "python", Input(Op: "attach", Language: "python"));

            await harness.Server.SendEventAsync("exited", new { exitCode = 0 });
            await WaitUntilAsync(() => !harness.Registry.List().Any(info => info.SessionId == session.SessionId));

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => harness.Registry.GetSessionAsync(session.SessionId, CancellationToken.None));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task AdapterProcessExitTransitionsToFailedWithLastStderrLine()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach"}""");
        try
        {
            var session = await AttachAndBindAsync(harness, "python", Input(Op: "attach", Language: "python"));

            harness.Process.EmitStderr("Error: cannot connect to debuggee");
            harness.Process.SimulateExit();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Registry.GetSessionAsync(session.SessionId, CancellationToken.None));
            Assert.Contains("exited", exception.Message);

            var failed = harness.Registry.List().Single(info => info.SessionId == session.SessionId);
            Assert.Equal("Failed", failed.State);
            Assert.Equal("Error: cannot connect to debuggee", failed.LastError);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task InitializeFailureRecordsFailedSessionAndRethrows()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach"}""");
        try
        {
            var exception = await Assert.ThrowsAsync<JsonRpcRemoteException>(
                () => AttachAndBindAsync(
                    harness,
                    "python",
                    Input(Op: "attach", Language: "python"),
                    responder: (command, _, _) => command == "initialize"
                        ? Task.FromResult<object?>(new JsonRpcError(-32000, "adapter does not support pisharp-debug"))
                        : Task.FromResult<object?>(new { })));
            Assert.Contains("adapter does not support", exception.Message);

            // Failed sessions are removed from the registry on attach failure; the Failed
            // snapshot still fired on SessionUpdated.
            Assert.Empty(harness.Registry.List());
            var failed = Assert.Single(harness.Updated, info => info.State == "Failed");
            Assert.Contains("adapter does not support", failed.LastError);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownAdapterThrowsListingConfiguredLanguages()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach"}""");
        try
        {
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
                () => harness.Registry.AttachAsync("rust", Input(Op: "attach", Language: "rust"), CancellationToken.None));
            Assert.Contains("python", exception.Message);
            Assert.Empty(harness.Factory.Processes);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task IdleSweepDisposesSessionsWithoutTraffic()
    {
        var factory = new FakeServerProcessFactory();
        var registry = new DebugSessionRegistry(
            AdapterConfigs("python", """{"request":"attach"}"""),
            RootPath,
            TimeSpan.FromMilliseconds(80),
            factory,
            NullLoggerFactory.Instance);

        try
        {
            var attachTask = registry.AttachAsync("python", Input(Op: "attach", Language: "python"), CancellationToken.None);
            var process = await factory.NextProcessAsync.WaitAsync(Timeout);
            await using var server = new ScriptedWireServer(process, WireProtocol.Dap)
            {
                OnRequest = (command, _, _) => command switch
                {
                    "initialize" => Task.FromResult<object?>(new { supportsConfigurationDoneRequest = true }),
                    _ => Task.FromResult<object?>(new { }),
                },
            };
            server.Start();

            var session = await attachTask.WaitAsync(Timeout);
            Assert.Equal("Attached", session.State);

            await Task.Delay(150);
            await registry.DisposeIdleAsync(CancellationToken.None);

            // Idle sessions are removed from the registry when disposed.
            Assert.Empty(registry.List());
            Assert.True(factory.Processes[^1].HasExited);

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => registry.GetSessionAsync(session.SessionId, CancellationToken.None));
            await server.DisposeAsync();
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListReturnsAllSessionsInCreationOrder()
    {
        var harness = CreateAdapterHarness(language: "python", attachConfig: """{"request":"attach"}""");
        try
        {
            var first = await AttachAndBindAsync(harness, "python", Input(Op: "attach", Language: "python"));
            var second = await AttachAndBindAsync(harness, "python", Input(Op: "attach", Language: "python"));

            Assert.NotEqual(first.SessionId, second.SessionId);
            var listed = harness.Registry.List();
            Assert.Equal(2, listed.Count);
            Assert.Equal(first.SessionId, listed[0].SessionId);
            Assert.Equal(second.SessionId, listed[1].SessionId);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    // ---- helpers ----

    private static Harness CreateAdapterHarness(string language, string attachConfig)
    {
        var factory = new FakeServerProcessFactory();
        var registry = new DebugSessionRegistry(
            AdapterConfigs(language, attachConfig),
            RootPath,
            TimeSpan.FromMinutes(30),
            factory,
            NullLoggerFactory.Instance);
        return new Harness(factory, registry);
    }

    private static async Task<DebugSessionInfo> AttachAndBindAsync(
        Harness harness,
        string language,
        DebugToolInput input,
        Func<string, JsonElement, int, Task<object?>>? responder = null)
    {
        var attachTask = harness.Registry.AttachAsync(language, input, CancellationToken.None);
        var process = await harness.Factory.NextProcessAsync.WaitAsync(Timeout);
        harness.Server = new ScriptedWireServer(process, WireProtocol.Dap)
        {
            OnRequest = responder ?? DefaultResponder,
        };
        harness.Server.Start();
        return await attachTask.WaitAsync(Timeout);
    }

    private static readonly Func<string, JsonElement, int, Task<object?>> DefaultResponder = (command, _, _) => command switch
    {
        "initialize" => Task.FromResult<object?>(new { supportsConfigurationDoneRequest = true }),
        "attach" => Task.FromResult<object?>(new { }),
        "setBreakpoints" => Task.FromResult<object?>(new { breakpoints = new object[] { new { verified = true, line = 3 }, new { verified = true, line = 7 } } }),
        "continue" => Task.FromResult<object?>(new { allThreadsContinued = true }),
        "threads" => Task.FromResult<object?>(new { threads = new object[] { new { id = 1, name = "main" } } }),
        "stackTrace" => Task.FromResult<object?>(new { stackFrames = new object[] { new { id = 1000, name = "main", line = 5 } } }),
        "scopes" => Task.FromResult<object?>(new { scopes = new object[] { new { name = "Local", variablesReference = 1001, expensive = false } } }),
        "variables" => Task.FromResult<object?>(new { variables = new object[] { new { name = "x", value = "2", variablesReference = 0 }, new { name = "y", value = "true", variablesReference = 0 } } }),
        "evaluate" => Task.FromResult<object?>(new { result = "3", variablesReference = 0 }),
        "disconnect" => Task.FromResult<object?>(new { }),
        _ => Task.FromResult<object?>(new { }),
    };

    private static IReadOnlyDictionary<string, DebugAdapterConfig> AdapterConfigs(string language, string attachConfig)
    {
        var section = JsonDocument.Parse($$"""{"command":["fake-adapter"],"extensions":[".py"],"attach":{{attachConfig}}}""").RootElement;
        var result = DebugAdapterConfigParser.Parse(section, language);
        Assert.True(result.IsOk);
        return new Dictionary<string, DebugAdapterConfig> { [language] = result.Value };
    }

    private static DebugToolInput Input(
        string Op,
        string? Language = null,
        string? SessionId = null,
        string? Path = null,
        JsonElement? Request = null)
        => new(
            Op: Op,
            Language: Language,
            SessionId: SessionId,
            Path: Path,
            Lines: null,
            ThreadId: null,
            FrameId: null,
            VariablesReference: null,
            Expression: null,
            Context: null,
            StepType: null,
            Request: Request,
            Start: null,
            Count: null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }


    private sealed class Harness : IAsyncDisposable
    {
        public Harness(FakeServerProcessFactory factory, DebugSessionRegistry registry)
        {
            Factory = factory;
            Registry = registry;
            Registry.SessionUpdated += Updated.Add;
        }

        public FakeServerProcessFactory Factory { get; }

        public DebugSessionRegistry Registry { get; }

        public ScriptedWireServer Server { get; set; } = null!;

        public FakeServerProcess Process => Factory.Processes[^1];

        public List<DebugSessionInfo> Updated { get; } = [];

        public async ValueTask DisposeAsync()
        {
            if (Server is not null)
            {
                await Server.DisposeAsync();
            }

            await Registry.DisposeAsync();
        }
    }
}
