using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime;
using PiSharp.Runtime.IO;

namespace PiSharp.Acp.Tests;

/// <summary>
/// Builds a minimal, isolated <see cref="SessionRuntime"/> over a JSONL-backed session repo in a
/// temp directory, with fake provider streams and optional fake tools / extension manager. Mirrors
/// <c>ModeTestRuntime</c> (tests/PiSharp.Cli.Tests) but stays within the PiSharp.Acp reference set.
/// </summary>
internal static class AcpTestRuntime
{
    public static async Task<SessionRuntime> CreateAsync(
        AgentStreamAsync? stream = null,
        ExtensionManager? extensionManager = null,
        IReadOnlyList<IAgentTool>? tools = null,
        string? cwd = null)
    {
        var root = cwd ?? Path.Combine(Path.GetTempPath(), "pisharp-acp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = new SystemExecutionEnv(root);
        var repo = new JsonlSessionRepo(env, "sessions");
        var createOptions = new JsonlSessionCreateOptions(root);
        var initial = await repo.CreateAsync(createOptions);
        var fakeTools = tools ?? (IReadOnlyList<IAgentTool>)[];
        var activeNames = fakeTools.Select(tool => tool.Name).ToArray();

        AgentHarness<JsonlSessionMetadata> Factory(ISession<JsonlSessionMetadata> session) => new(
            new AgentHarnessOptions<JsonlSessionMetadata>(
                session,
                new ModelDescriptor("test", "test", "test"),
                stream ?? FakeStream("ok"),
                FakeCompletion,
                fakeTools,
                ActiveToolNames: activeNames.Length == 0 ? null : activeNames,
                Extensions: extensionManager?.Registry));

        return new SessionRuntime(repo, createOptions, Factory, initial, extensionManager: extensionManager);
    }

    public static AgentStreamAsync FakeStream(string text)
        => (_, _, _, _) => StreamHelper(text);

    public static AgentCompletionAsync FakeCompletion => (_, _, _, _) => Task.FromResult(AgentMessages.Assistant("ok"));

    /// <summary>Stream that issues one tool call then stops once a tool result is present in context.</summary>
    public static AgentStreamAsync ToolThenStopStream(string toolName, string toolCallId = "tool-1", string argsJson = "{}")
        => (model, context, options, cancellationToken) => ToolStreamHelper(toolName, toolCallId, argsJson, context);

    /// <summary>A stream that emits one start (nonzero text) then hangs until aborted.</summary>
    public static AgentStreamAsync HangingStartStream
        => (_, _, _, cancellationToken) => HangingStartStreamInner(cancellationToken);

    private static async IAsyncEnumerable<AssistantMessageEvent> HangingStartStreamInner(CancellationToken cancellationToken)
    {
        yield return new AssistantMessageEvent.Start(new AssistantMessage([new TextContent("hang")], StopReason: "stop"));
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> StreamHelper(string text)
    {
        await Task.Yield();
        var message = new AssistantMessage([new TextContent(text)], StopReason: "stop");
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> ToolStreamHelper(string toolName, string toolCallId, string argsJson, AgentContext context)
    {
        await Task.Yield();
        AssistantMessage message;
        if (context.Messages.OfType<ToolResultMessage>().Any())
        {
            message = new AssistantMessage([new TextContent("done")], StopReason: "stop");
        }
        else
        {
            using var args = JsonDocument.Parse(argsJson);
            message = new AssistantMessage([new ToolCallContent(toolCallId, toolName, args.RootElement.Clone())], StopReason: "tool_use");
        }
        yield return new AssistantMessageEvent.Start(message);
        yield return new AssistantMessageEvent.Done(message);
    }

    /// <summary>A fake tool that records invocations and returns fixed content.</summary>
    public sealed class CountingTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Label => $"{name} label";
        public string Description => name;
        public int CallCount { get; private set; }
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => ToolExecutionMode.Sequential;
        public JsonElement PrepareArguments(JsonElement args) => args;

        public Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
        {
            CallCount++;
            return Task.FromResult(new AgentToolResult<object?>([new TextContent($"{name} result")], new { name }));
        }
    }

    /// <summary>Parses a JSON string into a cloneable <see cref="JsonElement"/> for codec tests.</summary>
    public static JsonElement JsonEl(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
