using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.UiBridge;
using Xunit;

namespace PiSharp.Server.Tests;

/// <summary>
/// Session-scoped UI bridge overloads: a request driven through the target-scoped
/// <c>RequestUiAsync(intent, target, responseTimeout, ct)</c> overload must emit its
/// <c>ui_request</c> onto the specific <see cref="LiveServerSession"/> (not the global
/// most-recent session) and honor an explicit per-call response timeout instead of the bridge's
/// fixed auto-cancel window. The "client" here is a raw event reader on the target session that
/// answers by calling <c>ResolveUiAsync</c> — the same resolution the server's <c>ui_response</c>
/// command invokes on the wire.
/// </summary>
public sealed class PiServerUiBridgeTests
{
    /// <summary>Fail-fast window for the event readers: if a <c>ui_request</c> never arrives the
    /// reader's cancellation fires and the test fails instead of hanging the suite.</summary>
    private static readonly TimeSpan ReaderTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Select_ThroughTargetScopedOverload_EmitsOnTargetNotNewestSession_AndResolves()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry);
        var target = await CreateSessionAsync(registry);
        _ = await CreateSessionAsync(registry); // created AFTER target
        var newest = registry.Sessions.MaxBy(session => session.Id, StringComparer.Ordinal)!;
        Assert.NotSame(target, newest); // the global fallback lane must point at a different session

        var intent = new ServerUiIntent("ui-select-1", "select", "Pick", "Choose one", ["alpha", "beta"], null);
        var responseTask = bridge.RequestUiAsync(intent, target, TimeSpan.FromSeconds(30), CancellationToken.None);

        // The client reads the ui_request off the target session's event lane and answers.
        using var readerCts = new CancellationTokenSource(ReaderTimeout);
        var targetRequestId = await ReadFirstUiRequestIdAsync(target, readerCts.Token);
        Assert.Equal(intent.RequestId, targetRequestId);

        // The globally-most-recent session must NOT have received the request: the target-scoped
        // overload must not fall back to SelectSession().
        Assert.DoesNotContain(newest.EventLog.ReplayFrom(0).Events,
            envelope => envelope.Event.Type == "ui_request");

        bridge.ResolveUiAsync(intent.RequestId, "beta", cancelled: false);

        var response = await responseTask;
        Assert.False(response.Cancelled);
        Assert.Equal("beta", response.Value);
    }

    [Fact]
    public async Task Select_GenerousResponseTimeout_DoesNotAutoCancelBeforeClientAnswers()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry);
        var target = await CreateSessionAsync(registry);
        _ = await CreateSessionAsync(registry);
        var newest = registry.Sessions.MaxBy(session => session.Id, StringComparer.Ordinal)!;
        Assert.NotSame(target, newest);

        var intent = new ServerUiIntent("ui-select-2", "select", "Pick", "Choose one", ["alpha"], null);
        var responseTask = bridge.RequestUiAsync(intent, target, TimeSpan.FromSeconds(30), CancellationToken.None);

        using var readerCts = new CancellationTokenSource(ReaderTimeout);
        var targetRequestId = await ReadFirstUiRequestIdAsync(target, readerCts.Token);
        Assert.Equal(intent.RequestId, targetRequestId);

        // Answer only after the bridge's default 5s auto-cancel would have fired: a generous
        // per-call timeout must keep the request pending until the client responds.
        await Task.Delay(TimeSpan.FromSeconds(5).Add(TimeSpan.FromMilliseconds(300)));
        bridge.ResolveUiAsync(intent.RequestId, "alpha", cancelled: false);

        var response = await responseTask;
        Assert.False(response.Cancelled);
        Assert.Equal("alpha", response.Value);

        Assert.DoesNotContain(newest.EventLog.ReplayFrom(0).Events,
            envelope => envelope.Event.Type == "ui_request");
    }

    /// <summary>Per-kind default timeouts (Task 9): interactive kinds must get a generous default
    /// (~5 min) instead of the bridge's fixed 5s, so a human answering a <c>select</c> dialog over
    /// the wire is not auto-cancelled server-side; fire-and-forget kinds keep the short default.
    /// Here a <c>select</c> issued with <c>responseTimeout: null</c> is answered ~6s after emission —
    /// beyond today's fixed 5s auto-cancel — and the caller must still receive the value.
    /// Sole slow test for the per-kind timeout feature (~6s).</summary>
    [Fact]
    public async Task Select_NullResponseTimeout_InteractiveKindDefault_ClientAnswersAfterSixSeconds()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry);
        var target = await CreateSessionAsync(registry);

        var intent = new ServerUiIntent("ui-select-6s", "select", "Pick", "Choose one", ["alpha"], null);
        var responseTask = bridge.RequestUiAsync(intent, target, responseTimeout: null, CancellationToken.None);

        using var readerCts = new CancellationTokenSource(ReaderTimeout);
        var targetRequestId = await ReadFirstUiRequestIdAsync(target, readerCts.Token);
        Assert.Equal(intent.RequestId, targetRequestId);

        // Answer ~6s after emission: the bridge's fixed 5s default would already have auto-cancelled,
        // so only a generous per-kind default keeps the request pending until the client responds.
        await Task.Delay(TimeSpan.FromSeconds(6));
        bridge.ResolveUiAsync(intent.RequestId, "alpha", cancelled: false);

        var response = await responseTask;
        Assert.False(response.Cancelled);
        Assert.Equal("alpha", response.Value);
    }

    /// <summary>Per-kind default timeout resolution (Task 9): interactive kinds get a generous
    /// default, fire-and-forget kinds keep the short bridge default.</summary>
    [Fact]
    public void TimeoutFor_InteractiveKinds_AreGenerous()
    {
        foreach (var kind in new[] { "select", "input", "editor", "confirm", "custom", "custom_update" })
            Assert.True(ServerUiBridge.TimeoutFor(kind) > TimeSpan.FromMinutes(1), $"{kind} should get a generous default timeout");

        Assert.Equal(ServerUiBridge.TimeoutFor("notify"), ServerUiBridge.TimeoutFor("status"));
        Assert.True(ServerUiBridge.TimeoutFor("notify") < TimeSpan.FromSeconds(30));
    }

    /// <summary>Reads the session's event lane and returns the request id of the first
    /// <c>ui_request</c> it sees (null if the lane ends first). Cancellation is provided by the
    /// caller so a missing emission fails fast instead of hanging.</summary>
    private static async Task<string?> ReadFirstUiRequestIdAsync(LiveServerSession session, CancellationToken cancellationToken)
    {
        await foreach (var envelope in session.ReadEventsAsync(0, cancellationToken))
        {
            if (envelope.Event.Type != "ui_request") continue;
            return JsonDocument.Parse(JsonSerializer.Serialize(envelope.Event.Data))
                .RootElement.GetProperty("requestId").GetString();
        }
        return null;
    }

    // --- shared helpers (mirrors ServerUiBridgeThemeTests) ---

    private static async Task<LiveServerSession> CreateSessionAsync(ServerSessionRegistry registry)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ui-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var created = await registry.CreateAsync(new CreateServerSessionRequest(
            Cwd: root,
            NoTools: true,
            NoBuiltinTools: true,
            NoExtensions: true,
            NoSkills: true,
            NoPromptTemplates: true,
            NoThemes: true,
            NoContextFiles: true), CancellationToken.None);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        return live!;
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])), initial);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
