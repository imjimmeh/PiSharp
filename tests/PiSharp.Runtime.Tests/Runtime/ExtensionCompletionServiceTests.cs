using System.Runtime.CompilerServices;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Ai;
using PiSharp.Ai.Models;
using PiSharp.Ai.Providers;
using PiSharp.Extensions;
using PiSharp.Runtime.IO;
using Xunit;

namespace PiSharp.Runtime.Tests;

/// <summary>
/// Drives the P16 advisor completion surface through the runtime binding:
/// provider resolution, text extraction, the TimeoutMs watchdog cap, and
/// Cancelled/Error classification. Each test registers its provider under a
/// unique api name so parallel test classes can never collide on the global
/// provider registry.
/// </summary>

[Collection("GlobalModelRegistry")]
public sealed class ExtensionCompletionServiceTests
{
    private const string Provider = "spine-completion-test";
    private const string ModelId = "spine-completion-model";

    [Fact]
    public async Task CompleteSimpleAsync_ReturnsProviderText()
    {
        RegisterProvider("faux-echo-test", new FauxEchoProvider("hello world"));

        await using var runtime = await CreateRuntimeAsync();
        runtime.BindExtensionRuntime();

        var result = await runtime.ExtensionBinding.CompleteSimpleAsync(Provider, ModelId, "hi", null, CancellationToken.None);

        Assert.Equal(ExtensionCompletionStatus.Ok, result.Status);
        Assert.Equal("hello world", result.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CompleteAsync_AppliesTimeoutCapAndReturnsTimeout()
    {
        RegisterProvider("slow-test", new SlowCompleteProvider(TimeSpan.FromSeconds(10)));

        await using var runtime = await CreateRuntimeAsync();
        runtime.BindExtensionRuntime();

        var options = new ExtensionCompleteRequest(Provider, ModelId, TimeoutMs: 150);
        var result = await runtime.ExtensionBinding.CompleteAsync(Provider, ModelId, null, null, options, false, CancellationToken.None);

        Assert.Equal(ExtensionCompletionStatus.Timeout, result.Status);
        Assert.Contains("150", result.Error);
    }

    [Fact]
    public async Task CompleteAsync_StreamFullOnTimeout_RunsToCompletionPastCap()
    {
        RegisterProvider("slow-test", new SlowCompleteProvider(TimeSpan.FromMilliseconds(300)));

        await using var runtime = await CreateRuntimeAsync();
        runtime.BindExtensionRuntime();

        var options = new ExtensionCompleteRequest(Provider, ModelId, TimeoutMs: 50);
        var result = await runtime.ExtensionBinding.CompleteAsync(Provider, ModelId, null, null, options, true, CancellationToken.None);

        Assert.Equal(ExtensionCompletionStatus.Ok, result.Status);
        Assert.Equal("late answer", result.Text);
    }

    [Fact]
    public async Task CompleteAsync_ProviderError_ReturnsErrorStatus()
    {
        RegisterProvider("error-test", new ThrowingProvider("boom"));

        await using var runtime = await CreateRuntimeAsync();
        runtime.BindExtensionRuntime();

        var result = await runtime.ExtensionBinding.CompleteAsync(Provider, ModelId, null, null, null, false, CancellationToken.None);

        Assert.Equal(ExtensionCompletionStatus.Error, result.Status);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task CompleteAsync_CallerCancellation_ReturnsCancelled()
    {
        RegisterProvider("faux-echo-test", new FauxEchoProvider("hello world"));

        await using var runtime = await CreateRuntimeAsync();
        runtime.BindExtensionRuntime();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await runtime.ExtensionBinding.CompleteAsync(Provider, ModelId, null, null, null, false, cts.Token);

        Assert.Equal(ExtensionCompletionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task StreamAsync_EmitsFinalDelta()
    {
        RegisterProvider("faux-echo-test", new FauxEchoProvider("hello world"));

        await using var runtime = await CreateRuntimeAsync();
        runtime.BindExtensionRuntime();

        var deltas = new List<ExtensionCompletionDelta>();
        await foreach (var delta in runtime.ExtensionBinding.StreamAsync(Provider, ModelId, null, null, null, false, CancellationToken.None))
            deltas.Add(delta);

        Assert.NotEmpty(deltas);
        Assert.True(deltas[^1].Final);
    }

    private static void RegisterProvider(string api, IModelProvider provider)
    {
        PublicApi.RegisterProvider(provider, "spine-test");
        ModelRegistry.RegisterProviderConfig(new ModelProviderConfig(Provider, api), "spine-test");
        ModelRegistry.RegisterModel(new CatalogModel(Provider, ModelId, new ModelDescriptor(Provider: Provider, Id: ModelId, Api: api)), "spine-test");
    }

    private static async Task<SessionRuntime> CreateRuntimeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-spine-completion-" + Guid.NewGuid().ToString("N"));
        var repo = new JsonlSessionRepo(new SystemExecutionEnv(root), "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        return new SessionRuntime(repo, createOptions, Harness, initial);
    }

    private static AgentHarness<JsonlSessionMetadata> Harness(ISession<JsonlSessionMetadata> session)
        => new(new AgentHarnessOptions<JsonlSessionMetadata>(session, new ModelDescriptor("test", "test", "test"), FakeStream, FakeCompletion, []));

    private static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    private static async IAsyncEnumerable<AssistantMessageEvent> FakeStream(ModelDescriptor _, AgentContext __, AgentStreamOptions ___, [EnumeratorCancellation] CancellationToken ____ = default)
    {
        await Task.Yield();
        yield return new AssistantMessageEvent.Done(AgentMessages.Assistant("ok"));
    }

    private sealed class FauxEchoProvider(string text, string api = "faux-echo-test") : IModelProvider
    {
        public string Api => api;
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            var message = AgentMessages.Assistant(text);
            yield return new AssistantMessageEvent.Start(message);
            yield return new AssistantMessageEvent.TextStart(message, 0);
            yield return new AssistantMessageEvent.TextDelta(message, 0, text);
            yield return new AssistantMessageEvent.TextEnd(message, 0);
            yield return new AssistantMessageEvent.Done(message);
        }

        public Task<AssistantMessage> CompleteAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AgentMessages.Assistant(text));
        }
    }

    private sealed class SlowCompleteProvider(TimeSpan delay, string api = "slow-test") : IModelProvider
    {
        public string Api => api;
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            yield return new AssistantMessageEvent.Done(AgentMessages.Assistant("late answer"));
        }

        public async Task<AssistantMessage> CompleteAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return AgentMessages.Assistant("late answer");
        }
    }

    private sealed class ThrowingProvider(string message, string api = "error-test") : IModelProvider
    {
        public string Api => api;
        public async IAsyncEnumerable<AssistantMessageEvent> StreamAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (true) throw new InvalidOperationException(message);
            yield return new AssistantMessageEvent.Done(AgentMessages.Assistant("unreachable"));
        }

        public Task<AssistantMessage> CompleteAsync(ModelDescriptor model, AgentContext context, AgentStreamOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }
}
