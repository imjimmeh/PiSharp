using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using Xunit;

namespace PiSharp.Server.Tests;

public sealed class ServerSessionRegistryTests
{
    [Fact]
    public void ApiKeyValidatorAcceptsBearerAndQueryToken()
    {
        var validator = new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" });
        var bearer = new DefaultHttpContext();
        bearer.Request.Headers.Authorization = "Bearer secret";
        var query = new DefaultHttpContext();
        query.Request.QueryString = new QueryString("?access_token=secret");

        Assert.True(validator.Validate(bearer));
        Assert.True(validator.Validate(query));
    }

    [Fact]
    public async Task RegistryCreatesDistinctRuntimePerServerSession()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var first = await registry.CreateAsync(new CreateServerSessionRequest(TempRoot()));
        var second = await registry.CreateAsync(new CreateServerSessionRequest(TempRoot()));

        Assert.NotEqual(first.ServerSessionId, second.ServerSessionId);
        Assert.True(registry.TryGet(first.ServerSessionId, out var firstLive));
        Assert.True(registry.TryGet(second.ServerSessionId, out var secondLive));
        Assert.NotSame(firstLive.Runtime, secondLive.Runtime);
    }

    [Fact]
    public async Task ListSessionsAnnotatesLiveOwnership()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var created = await registry.CreateAsync(new CreateServerSessionRequest(TempRoot()));
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        await live.Runtime.Session.Storage.AppendEntryAsync(UserEntry("live", "hello"));

        var result = await registry.ListSessionsAsync(new ListSessionsCommand(ServerCommandTypes.ListSessions, ServerSessionId: created.ServerSessionId));

        var session = Assert.Single(result.Sessions);
        Assert.Equal(created.State.RuntimeSessionId, session.Id);
        Assert.True(session.IsLive);
        Assert.Equal(created.ServerSessionId, session.ServerSessionId);
    }

    [Fact]
    public async Task ListSessionsCanRunWithoutLiveRuntimeAndHonorsCwdFilter()
    {
        var sessionsRoot = Path.Combine(Path.GetTempPath(), "pisharp-list-test-" + Guid.NewGuid().ToString("N"));
        var firstCwd = TempRoot();
        var secondCwd = TempRoot();
        var firstRepo = new JsonlSessionRepo(new SystemExecutionEnv(firstCwd), sessionsRoot);
        var secondRepo = new JsonlSessionRepo(new SystemExecutionEnv(secondCwd), sessionsRoot);
        var first = await firstRepo.CreateAsync(new JsonlSessionCreateOptions(firstCwd, "first"));
        var second = await secondRepo.CreateAsync(new JsonlSessionCreateOptions(secondCwd, "second"));
        await first.Storage.AppendEntryAsync(UserEntry("first-message", "first"));
        await second.Storage.AppendEntryAsync(UserEntry("second-message", "second"));
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));

        var filtered = await registry.ListSessionsAsync(new ListSessionsCommand(ServerCommandTypes.ListSessions, Cwd: firstCwd, SessionsRoot: sessionsRoot));
        var all = await registry.ListSessionsAsync(new ListSessionsCommand(ServerCommandTypes.ListSessions, Cwd: firstCwd, SessionsRoot: sessionsRoot, AllCwds: true));

        var only = Assert.Single(filtered.Sessions);
        Assert.Equal(first.Metadata.Id, only.Id);
        Assert.Equal(firstCwd, only.Cwd);
        Assert.Contains(all.Sessions, session => session.Id == first.Metadata.Id);
        Assert.Contains(all.Sessions, session => session.Id == second.Metadata.Id);
    }

    [Fact]
    public async Task ServerPromptUsesRuntimeInputHook()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:test", ExtensionEventNames.Input, (evt, _) => { evt.TransformInput("server transformed"); return Task.CompletedTask; });
        var manager = new ExtensionManager(registry);
        var serverRegistry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd, manager));
        var created = await serverRegistry.CreateAsync(new CreateServerSessionRequest(TempRoot()));
        Assert.True(serverRegistry.TryGet(created.ServerSessionId, out var live));
        var handler = new PiSharp.Server.WebSockets.PiServerWebSocketHandler(serverRegistry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = string.Empty }), Microsoft.Extensions.Logging.Abstractions.NullLogger<PiSharp.Server.WebSockets.PiServerWebSocketHandler>.Instance);
        var json = System.Text.Json.JsonSerializer.Serialize(new { type = ServerCommandTypes.Prompt, id = "p1", serverSessionId = created.ServerSessionId, message = "original" }, ServerJsonSerializer.Options);
        await handler.DispatchTextCommandAsync(json, cancellationToken: CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        IReadOnlyList<AgentMessage> messages = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            messages = (await live.Runtime.Session.BuildContextAsync()).Messages;
            if (messages.OfType<UserMessage>().Any()) break;
            await Task.Delay(25);
        }
        var user = Assert.Single(messages.OfType<UserMessage>());
        Assert.Equal("server transformed", Assert.IsType<TextContent>(Assert.Single(user.Content)).Text);
    }

    [Fact]
    public async Task LiveSessionPublishesSequencedFlatEvents()
    {
        var runtime = await CreateRuntimeAsync(TempRoot());
        await using var live = new LiveServerSession("srv_test", runtime);

        await using var events = live.ReadEventsAsync().GetAsyncEnumerator();
        var firstEventTask = events.MoveNextAsync().AsTask();
        await Task.Yield();
        await runtime.Harness.PromptAsync("hello");
        Assert.True(await firstEventTask);
        var first = events.Current;

        Assert.Equal("srv_test", first.ServerSessionId);
        Assert.True(first.Sequence > 0);
        Assert.NotNull(first.Event.Type);
        var json = ServerJsonSerializer.Serialize(first);
        Assert.Contains("\"serverSessionId\":\"srv_test\"", json);
        Assert.Contains("\"sequence\":", json);
        Assert.Contains("\"event\":{\"type\":", json);
        Assert.DoesNotContain("\"event\":{\"event\":", json);
    }

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root, ExtensionManager? extensionManager = null)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [], Extensions: extensionManager?.Registry)), initial, extensionManager: extensionManager);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static MessageEntry UserEntry(string id, string text)
        => new() { Id = id, ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User(text) };

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-server-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
