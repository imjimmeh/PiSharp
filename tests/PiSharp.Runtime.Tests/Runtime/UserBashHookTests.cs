using System.Runtime.CompilerServices;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Runtime.Tests.Runtime;

public sealed class UserBashHookTests
{
    [Fact]
    public async Task FirstHandlerReturningResultWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        var firstCalled = false;
        registry.RegisterHandler("extension:first", ExtensionEventNames.UserBash, (evt, _) =>
        {
            firstCalled = true;
            var payload = Assert.IsType<ExtensionUserBashPayload>(evt.Payload);
            evt.SetUserBashResult(result: new ExtensionBashResult(payload.Command, 0, "first result", ""));
            return Task.CompletedTask;
        });
        var secondCalled = false;
        registry.RegisterHandler("extension:second", ExtensionEventNames.UserBash, (evt, _) =>
        {
            secondCalled = true;
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        var result = await runtime.DispatchUserBashAsync("test command", false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("first result", result.Output);
        Assert.True(firstCalled);
        Assert.False(secondCalled);
    }

    [Fact]
    public async Task FailureIsolationLetsLaterHandlerSucceed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        registry.RegisterHandler("extension:throw", ExtensionEventNames.UserBash, (evt, _) =>
        {
            throw new InvalidOperationException("This handler failed");
        });
        registry.RegisterHandler("extension:ok", ExtensionEventNames.UserBash, (evt, _) =>
        {
            var payload = Assert.IsType<ExtensionUserBashPayload>(evt.Payload);
            evt.SetUserBashResult(result: new ExtensionBashResult(payload.Command, 0, "ok result", ""));
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        var result = await runtime.DispatchUserBashAsync("test command", false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ok result", result.Output);
    }

    [Fact]
    public async Task FirstHandlerReturningOperationsWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-ops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        var secondCalled = false;
        registry.RegisterHandler("extension:operations", ExtensionEventNames.UserBash, (evt, _) =>
        {
            evt.SetUserBashResult(operations: new ExtensionBashOperations(Command: "rewritten", Cwd: root));
            return Task.CompletedTask;
        });
        registry.RegisterHandler("extension:second", ExtensionEventNames.UserBash, (evt, _) =>
        {
            secondCalled = true;
            evt.SetUserBashResult(result: new ExtensionBashResult("original", 0, "second result", ""));
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        var result = await runtime.DispatchUserBashAsync("original", false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Operations);
        Assert.Equal("rewritten", result.Operations.Command);
        Assert.Equal(root, result.Operations.Cwd);
        Assert.Null(result.Result);
        Assert.False(secondCalled);
    }

    [Fact]
    public async Task NoHandlersReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session);

        var result = await runtime.DispatchUserBashAsync("test command", false, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PayloadContainsCommandExcludeFromContextAndCwd()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ExtensionRegistry();
        ExtensionUserBashPayload? captured = null;
        registry.RegisterHandler("extension:test", ExtensionEventNames.UserBash, (evt, _) =>
        {
            captured = Assert.IsType<ExtensionUserBashPayload>(evt.Payload);
            return Task.CompletedTask;
        });
        var manager = new ExtensionManager(registry);
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root) with { Cwd = root };
        var session = await repo.CreateAsync(createOptions);
        var runtime = new SessionRuntime(repo, createOptions, Harness, session, extensionManager: manager);

        await runtime.DispatchUserBashAsync("ls -la", true, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("ls -la", captured.Command);
        Assert.True(captured.ExcludeFromContext);
        Assert.Equal(root, captured.Cwd);
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        var message = AgentMessages.Assistant("ok");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }
}
