using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Runtime.IO;
using PiSharp.Server.Authentication;
using PiSharp.Server.Contracts;
using PiSharp.Server.Runtime;
using PiSharp.Server.Serialization;
using PiSharp.Server.WebSockets;
using Xunit;

namespace PiSharp.Server.Tests;

public sealed class PiServerWebSocketHandlerTests
{
    [Fact]
    public async Task HttpHandshakeRejectsMissingApiKey()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var context = new DefaultHttpContext();

        await handler.HandleHttpAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnknownCommandReturnsFailureResponse()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));

        var response = await handler.DispatchTextCommandAsync("{\"id\":\"1\",\"type\":\"missing\"}");

        Assert.False(response.Success);
        Assert.Equal("unknown_command", response.Error?.Code);
    }

    [Fact]
    public async Task CreateSessionCommandCreatesRuntimeAndReturnsState()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var command = JsonSerializer.Serialize(new { id = "1", type = ServerCommandTypes.CreateSession, cwd = TempRoot() }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        Assert.Single(registry.Sessions);
    }

    [Fact]
    public async Task ListSessionsCommandWithLiveSessionReturnsPersistedSessions()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createCommand = JsonSerializer.Serialize(new { id = "create", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options);
        var createResponse = await handler.DispatchTextCommandAsync(createCommand);
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));
        await live.Runtime.Session.Storage.AppendEntryAsync(UserEntry("live", "hello"));
        var listCommand = JsonSerializer.Serialize(new { id = "list", type = ServerCommandTypes.ListSessions, serverSessionId = created.ServerSessionId }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(listCommand);

        Assert.True(response.Success);
        var result = Assert.IsType<ServerSessionListResult>(response.Data);
        var session = Assert.Single(result.Sessions);
        Assert.Equal(created.State.RuntimeSessionId, session.Id);
        Assert.True(session.IsLive);
        Assert.Equal(created.ServerSessionId, session.ServerSessionId);
    }

    [Fact]
    public async Task ListSessionsCommandWithoutLiveSessionUsesCwdAndSessionsRoot()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var cwd = TempRoot();
        var sessionsRoot = Path.Combine(cwd, "custom-sessions");
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(cwd), sessionsRoot);
        var persisted = await repo.CreateAsync(new JsonlSessionCreateOptions(cwd, "persisted"));
        await persisted.Storage.AppendEntryAsync(UserEntry("persisted", "hello"));
        var metadata = persisted.Metadata;
        var command = JsonSerializer.Serialize(new { id = "list", type = ServerCommandTypes.ListSessions, cwd, sessionsRoot }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.True(response.Success);
        var result = Assert.IsType<ServerSessionListResult>(response.Data);
        var session = Assert.Single(result.Sessions);
        Assert.Equal(metadata.Id, session.Id);
        Assert.False(session.IsLive);
        Assert.Null(session.ServerSessionId);
    }

    [Fact]
    public async Task ListSessionsCommandWithoutLiveSessionRequiresCwd()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var command = JsonSerializer.Serialize(new { id = "list", type = ServerCommandTypes.ListSessions }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
        Assert.Contains("cwd is required", response.Error?.Message);
    }

    [Fact]
    public async Task AttachCommand_ReplaysFromSinceSequence()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));

        await EmitTurnAsync(live);
        var head = live.EventLog.HeadSequence;

        var attachResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "a", type = ServerCommandTypes.Attach, serverSessionId = created.ServerSessionId, sinceSequence = head - 1
        }, ServerJsonSerializer.Options));

        Assert.True(attachResponse.Success);
        var result = Assert.IsType<AttachResult>(attachResponse.Data);
        Assert.Equal(head, result.HeadSequence);
        Assert.False(result.Gap);
    }

    [Fact]
    public async Task AttachCommand_WithOldSinceSequence_ReportsGap()
    {
        var registry = new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd));
        var handler = CreateHandler(registry);
        var cwd = TempRoot();
        var createResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new { id = "c", type = ServerCommandTypes.CreateSession, cwd }, ServerJsonSerializer.Options));
        var created = Assert.IsType<ServerSessionCreated>(createResponse.Data);
        Assert.True(registry.TryGet(created.ServerSessionId, out var live));

        await EmitTurnAsync(live);
        var head = live.EventLog.HeadSequence;
        var sample = live.EventLog.ReplayFrom(1).Events[0];
        live.EventLog.Append(sample with { Sequence = head + 1000 });

        var attachResponse = await handler.DispatchTextCommandAsync(JsonSerializer.Serialize(new
        {
            id = "a", type = ServerCommandTypes.Attach, serverSessionId = created.ServerSessionId, sinceSequence = 1
        }, ServerJsonSerializer.Options));

        Assert.True(attachResponse.Success);
        var result = Assert.IsType<AttachResult>(attachResponse.Data);
        Assert.True(result.Gap);
        Assert.Equal(head + 1000, result.HeadSequence);
    }

    [Fact]
    public async Task AttachCommand_UnknownSession_ReturnsFailure()
    {
        var handler = CreateHandler(new ServerSessionRegistry((request, _) => CreateRuntimeAsync(request.Cwd)));
        var command = JsonSerializer.Serialize(new { id = "a", type = ServerCommandTypes.Attach, serverSessionId = "missing", sinceSequence = 0 }, ServerJsonSerializer.Options);

        var response = await handler.DispatchTextCommandAsync(command);

        Assert.False(response.Success);
        Assert.Equal("command_failed", response.Error?.Code);
    }

    private static PiServerWebSocketHandler CreateHandler(ServerSessionRegistry registry)
        => new(registry, new ApiKeyValidator(new ApiKeyOptions { ApiKey = "secret" }), NullLogger<PiServerWebSocketHandler>.Instance);

    private static async Task<PiSharp.Runtime.SessionRuntime> CreateRuntimeAsync(string root)
    {
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new PiSharp.Runtime.SessionRuntime(repo, createOptions, session => new AgentHarness<JsonlSessionMetadata>(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, [])), initial);
    }

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static MessageEntry UserEntry(string id, string text)
        => new() { Id = id, ParentId = null, Timestamp = DateTimeOffset.UtcNow, Message = AgentMessages.User(text) };

    private static Task EmitTurnAsync(LiveServerSession live)
        => live.RunExclusiveAsync((runtime, _) => runtime.Harness.PromptAsync("hi", [], CancellationToken.None));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-ws-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
