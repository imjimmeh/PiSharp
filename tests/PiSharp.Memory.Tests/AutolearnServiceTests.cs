using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Events;
using Xunit;
using PiSharp.Extensions;
namespace PiSharp.Memory.Tests;

public sealed class AutolearnServiceTests
{
    private static readonly string[] CaptureKeywords = ["Memory capture"];

    private static AgentEvent.AgentEnd AgentEndWithToolCalls(int count)
        => new(Enumerable.Range(1, count)
            .Select(i => (AgentMessage)AgentMessages.ToolResult($"t{i}", "grep", "ok"))
            .ToArray());

    private static AgentHarnessOwnEvent.Settled Settled(int nextTurnCount = 0)
        => new(nextTurnCount);

    // --- threshold math ---

    [Fact]
    public void CountToolCalls_CountsDistinctCallsFromAgentEnd()
    {
        var payload = AgentEndWithToolCalls(6);
        Assert.Equal(6, AutolearnService.CountToolCalls(payload));
    }

    [Fact]
    public void CountToolCalls_DeduplicatesSharedIds()
    {
        var messages = new AgentMessage[]
        {
            new AssistantMessage([new ToolCallContent("t1", "grep", JsonSerializer.SerializeToElement(new { }))]),
            AgentMessages.ToolResult("t1", "grep", "ok"),
            AgentMessages.ToolResult("t2", "read", "ok")
        };
        Assert.Equal(2, AutolearnService.CountToolCalls(new AgentEvent.AgentEnd(messages)));
    }

    [Fact]
    public void CountToolCalls_FromJsonShape()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            messages = Enumerable.Range(1, 5).Select(i => new
            {
                role = "toolResult",
                toolUseId = $"t{i}",
                toolName = "grep",
                content = new[] { new { type = "text", text = "ok" } }
            }).ToArray()
        });

        Assert.Equal(5, AutolearnService.CountToolCalls(payload));
    }

    [Fact]
    public void CountToolCalls_UnknownPayload_IsZero()
    {
        Assert.Equal(0, AutolearnService.CountToolCalls("not an event"));
        Assert.Equal(0, AutolearnService.CountToolCalls(null));
    }

    [Fact]
    public void NextTurnCount_ReadsSettledPayload()
    {
        Assert.Equal(0, AutolearnService.NextTurnCount(Settled(0)));
        Assert.Equal(3, AutolearnService.NextTurnCount(Settled(3)));
        Assert.Null(AutolearnService.NextTurnCount("nope"));
    }

    // --- capture behavior through the harness ---

    [Fact]
    public async Task Capture_FiresOnceWhenThresholdMetAndIdle()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file",
            ["autolearn.enabled"] = true,
            ["autolearn.minToolCalls"] = 5
        });

        await harness.FireEventAsync("agent_end", AgentEndWithToolCalls(6));
        await harness.FireEventAsync("settled", Settled(0));

        var capture = Assert.Single(harness.SentMessages, message => message.Message.Role == "user");
        Assert.True(capture.TriggerTurn);
        Assert.Contains(CaptureKeywords[0], string.Join("\n", capture.Message switch
        {
            UserMessage user => user.Content.OfType<TextContent>().Select(c => c.Text),
            _ => []
        }), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(harness.EmittedEvents, entry => entry.Name == MemoryEventNames.AutolearnCaptureStart);
        Assert.Contains(harness.EmittedEvents, entry => entry.Name == MemoryEventNames.AutolearnCaptureEnd);
    }

    [Fact]
    public async Task Capture_FiresExactlyOncePerSettledRun()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file",
            ["autolearn.enabled"] = true
        });

        await harness.FireEventAsync("agent_end", AgentEndWithToolCalls(6));
        await harness.FireEventAsync("settled", Settled(0));
        await harness.FireEventAsync("settled", Settled(0));

        Assert.Single(harness.SentMessages, message => message.Message.Role == "user");
    }

    [Fact]
    public async Task Capture_NotFiredWhenBelowThreshold()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file",
            ["autolearn.enabled"] = true,
            ["autolearn.minToolCalls"] = 5
        });

        await harness.FireEventAsync("agent_end", AgentEndWithToolCalls(4));
        await harness.FireEventAsync("settled", Settled(0));

        Assert.Empty(harness.SentMessages);
    }

    [Fact]
    public async Task Capture_NotFiredWhenTurnsStillQueued()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file",
            ["autolearn.enabled"] = true
        });

        await harness.FireEventAsync("agent_end", AgentEndWithToolCalls(6));
        await harness.FireEventAsync("settled", Settled(2));

        Assert.Empty(harness.SentMessages);
    }

    [Fact]
    public async Task Capture_NotFiredWhenAutolearnDisabled()
    {
        var harness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file"
        });

        await harness.FireEventAsync("agent_end", AgentEndWithToolCalls(6));
        await harness.FireEventAsync("settled", Settled(0));

        Assert.Empty(harness.SentMessages);
        Assert.Empty(harness.EmittedEvents);
    }

    [Fact]
    public async Task Capture_NudgeVsDirective_WordingDiffersByAutoContinue()
    {
        var nudgeHarness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file",
            ["autolearn.enabled"] = true,
            ["autolearn.autoContinue"] = false
        });
        await nudgeHarness.FireEventAsync("agent_end", AgentEndWithToolCalls(6));
        await nudgeHarness.FireEventAsync("settled", Settled(0));
        var nudge = string.Join("\n", nudgeHarness.SentMessages[^1].Message switch
        {
            UserMessage user => user.Content.OfType<TextContent>().Select(c => c.Text),
            _ => []
        });
        Assert.Contains("Optional memory capture", nudge);

        var directiveHarness = await MemoryHarness.CreateAsync(settings: new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["backend"] = "file",
            ["autolearn.enabled"] = true,
            ["autolearn.autoContinue"] = true
        });
        await directiveHarness.FireEventAsync("agent_end", AgentEndWithToolCalls(6));
        await directiveHarness.FireEventAsync("settled", Settled(0));
        var directive = string.Join("\n", directiveHarness.SentMessages[^1].Message switch
        {
            UserMessage user => user.Content.OfType<TextContent>().Select(c => c.Text),
            _ => []
        });
        Assert.Contains("Memory capture (auto-learn)", directive);
    }
}
