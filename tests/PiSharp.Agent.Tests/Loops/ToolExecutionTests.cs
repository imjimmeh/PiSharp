using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Loops;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Loops;
using Xunit;

namespace PiSharp.Agent.Tests.Loops;

public sealed class ToolExecutionTests
{
    [Fact]
    public async Task SequentialExecutionEmitsResultMessageBeforeNextStart()
    {
        using var args = JsonDocument.Parse("{}");
        var events = new List<string>();
        var tool = new FakeTool("one", delayMs: 1);
        var assistant = new AssistantMessage([new ToolCallContent("tc1", "one", args.RootElement.Clone())]);
        var batch = await ToolCallExecutor.ExecuteAsync(new AgentContext("system", [], [tool]), assistant, Config(ToolExecutionMode.Sequential), e => events.Add(e.GetType().Name), CancellationToken.None);
        Assert.Single(batch.Messages);
        Assert.Contains("ToolExecutionStart", events);
        Assert.Contains("ToolExecutionEnd", events);
        Assert.Contains("MessageEnd", events);
    }

    [Fact]
    public async Task ParallelExecutionEmitsMessagesInSourceOrder()
    {
        using var args = JsonDocument.Parse("{}");
        var toolA = new FakeTool("a", delayMs: 30);
        var toolB = new FakeTool("b", delayMs: 1);
        var assistant = new AssistantMessage([
            new ToolCallContent("a1", "a", args.RootElement.Clone()),
            new ToolCallContent("b1", "b", args.RootElement.Clone())
        ]);
        var batch = await ToolCallExecutor.ExecuteAsync(new AgentContext("system", [], [toolA, toolB]), assistant, Config(ToolExecutionMode.Parallel), _ => { }, CancellationToken.None);
        Assert.Equal(["a", "b"], batch.Messages.Select(message => message.ToolName));
    }

    private static AgentLoopConfig Config(ToolExecutionMode mode)
    {
        AgentStreamAsync stream = (_, _, _, _) => EmptyStream();
        return new AgentLoopConfig(new ModelDescriptor("test", "test/model", "test"), stream) with { ToolExecution = mode };
    }

    private static async IAsyncEnumerable<AssistantMessageEvent> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class FakeTool : IAgentTool
    {
        private readonly string _name;
        private readonly int _delayMs;

        public FakeTool(string name, int delayMs = 0)
        {
            _name = name;
            _delayMs = delayMs;
        }

        public string Name => _name;
        public string Label => _name;
        public string Description => _name;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement.Clone();
        public ToolExecutionMode? ExecutionMode => null;

        public JsonElement PrepareArguments(JsonElement args) => args;

        public async Task<AgentToolResult<object?>> ExecuteAsync(string toolCallId, JsonElement parameters, CancellationToken cancellationToken = default, AgentToolUpdateCallback<object?>? onUpdate = null)
        {
            await Task.Delay(_delayMs, cancellationToken);
            return new AgentToolResult<object?>([new TextContent(_name)], new { name = _name });
        }
    }
}
