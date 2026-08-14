using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Runtime;
using Xunit;

namespace PiSharp.Acp.Tests;

/// <summary>
/// Session lifecycle and turn serialization at the <see cref="AcpSessionManager"/> level
/// (plan §4.2 / §9 / §10): new/close/load, session_not_found, no-active-session, and the
/// cross-turn <c>turn_in_progress</c> guard.
/// </summary>
public sealed class AcpSessionLifecycleTests
{
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NewAsync_AssignsSessionIdAndActivates()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var info = await manager.NewAsync(cwd);
        Assert.StartsWith(AcpSessionManager.SessionIdPrefix, info.SessionId);
        Assert.Equal(info.SessionId, manager.ActiveSessionId);
        Assert.False(manager.IsTurnActive);
    }

    [Fact]
    public async Task NewAsync_WrongCwd_RejectedAsInvalidParams()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var manager = new AcpSessionManager(runtime);
        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => manager.NewAsync("C:/definitely/not/the/cwd"));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task CloseAsync_DeactivatesSession()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var info = await manager.NewAsync(cwd);
        await manager.CloseAsync(info.SessionId);
        Assert.Null(manager.ActiveSessionId);
    }

    [Fact]
    public async Task CloseAsync_UnknownSession_ThrowsSessionNotFound()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var manager = new AcpSessionManager(runtime);
        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => manager.CloseAsync("sess_ghost"));
        Assert.Equal(AcpErrorCodes.ServerError, ex.Code);
        Assert.Contains("session_not_found", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_PersistedSession_LoadsAndReactivates()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var info = await manager.NewAsync(cwd);
        await manager.PromptAsync(info.SessionId, [new AcpTextBlock("persist me")]);
        await manager.CloseAsync(info.SessionId);
        Assert.Null(manager.ActiveSessionId);

        var loaded = await manager.LoadAsync(info.SessionId, cwd, replay: true);
        Assert.Equal(info.SessionId, loaded.SessionId);
        Assert.Equal(info.SessionId, manager.ActiveSessionId);
    }

    [Fact]
    public async Task LoadAsync_UnknownSession_ThrowsSessionNotFound()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => manager.LoadAsync("sess_missing", cwd, replay: false));
        Assert.Equal(AcpErrorCodes.ServerError, ex.Code);
        Assert.Contains("session_not_found", ex.Message);
    }

    [Fact]
    public async Task PromptAsync_NoActiveSession_Throws()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var manager = new AcpSessionManager(runtime);
        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => manager.PromptAsync("sess_x", [new AcpTextBlock("hi")]));
        Assert.Equal(AcpErrorCodes.ServerError, ex.Code);
        Assert.Contains("no active session", ex.Message);
    }

    [Fact]
    public async Task PromptAsync_WrongSessionId_Throws()
    {
        var runtime = await AcpTestRuntime.CreateAsync();
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var info = await manager.NewAsync(cwd);
        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => manager.PromptAsync("sess_other", [new AcpTextBlock("hi")]));
        Assert.Equal(AcpErrorCodes.ServerError, ex.Code);
    }

    [Fact]
    public async Task PromptAsync_CompletesAndReturnsAssistantMessage()
    {
        var runtime = await AcpTestRuntime.CreateAsync(stream: AcpTestRuntime.FakeStream("greetings"));
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var info = await manager.NewAsync(cwd);
        var message = await manager.PromptAsync(info.SessionId, [new AcpTextBlock("hello")]);
        Assert.NotNull(message);
        Assert.False(manager.IsTurnActive);
    }

    [Fact]
    public async Task TurnSerialization_SecondPromptWhileActive_ThrowsTurnInProgress()
    {
        var (stream, gate) = BlockingStream();
        var runtime = await AcpTestRuntime.CreateAsync(stream: stream);
        var cwd = runtime.Session.Metadata.Cwd;
        var manager = new AcpSessionManager(runtime);
        var info = await manager.NewAsync(cwd);

        var turn = manager.PromptAsync(info.SessionId, [new AcpTextBlock("first")]);
        await WaitUntil(() => manager.IsTurnActive);
        Assert.True(manager.IsTurnActive);

        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => manager.PromptAsync(info.SessionId, [new AcpTextBlock("second")]));
        Assert.Equal(AcpErrorCodes.ServerError, ex.Code);
        Assert.Contains("turn_in_progress", ex.Message);

        gate.SetResult();
        var result = await turn;
        Assert.NotNull(result);
        Assert.False(manager.IsTurnActive);
    }

    /// <summary>Stream that emits a start then blocks until a test-owned gate is released.</summary>
    private static (AgentStreamAsync stream, TaskCompletionSource gate) BlockingStream()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentStreamAsync stream = (_, _, _, ct) => BlockingInner(gate.Task, ct);
        return (stream, gate);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> BlockingInner(Task gate, CancellationToken ct)
    {
        await Task.Yield();
        var message = new AssistantMessage([new TextContent("hang")], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        await gate.WaitAsync(ct);
        yield return new AssistantMessageEvent.Done(message);
    }
}
