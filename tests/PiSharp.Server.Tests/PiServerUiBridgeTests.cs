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
    [Fact]
    public async Task Select_ThroughTargetScopedOverload_ResolvesToAnsweredValue()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var bridge = new ServerUiBridge(registry);
        var target = await CreateSessionAsync(registry);

        var intent = new ServerUiIntent("ui-select-1", "select", "Pick", "Choose one", ["alpha", "beta"], null);
        var responseTask = bridge.RequestUiAsync(intent, target, TimeSpan.FromSeconds(30), CancellationToken.None);

        // The client reads the ui_request off the target session's event lane and answers.
        await foreach (var envelope in target.ReadEventsAsync(0, CancellationToken.None))
        {
            if (envelope.Event.Type != "ui_request") continue;
            var requestId = JsonDocument.Parse(JsonSerializer.Serialize(envelope.Event.Data))
                .RootElement.GetProperty("requestId").GetString();
            Assert.Equal(intent.RequestId, requestId);
            bridge.ResolveUiAsync(requestId!, "beta", cancelled: false);
            break;
        }

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

        var intent = new ServerUiIntent("ui-select-2", "select", "Pick", "Choose one", ["alpha"], null);
        var responseTask = bridge.RequestUiAsync(intent, target, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Answer only after the bridge's default 5s auto-cancel would have fired: a generous
        // per-call timeout must keep the request pending until the client responds.
        await foreach (var envelope in target.ReadEventsAsync(0, CancellationToken.None))
        {
            if (envelope.Event.Type != "ui_request") continue;
            await Task.Delay(TimeSpan.FromSeconds(5).Add(TimeSpan.FromMilliseconds(300)));
            bridge.ResolveUiAsync(intent.RequestId, "alpha", cancelled: false);
            break;
        }

        var response = await responseTask;
        Assert.False(response.Cancelled);
        Assert.Equal("alpha", response.Value);
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
