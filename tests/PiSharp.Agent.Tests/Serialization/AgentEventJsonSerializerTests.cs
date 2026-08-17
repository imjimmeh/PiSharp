using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Core.Models;
using PiSharp.Agent.Core.Streaming;
using PiSharp.Agent.Serialization;
using Xunit;

namespace PiSharp.Agent.Tests.Serialization;

public sealed class AgentEventJsonSerializerTests
{
    [Fact]
    public void SerializesCoreHarnessEventWithSnakeCaseEventType()
    {
        var json = AgentJsonSerializer.Serialize<AgentHarnessEvent>(new AgentHarnessEvent.Core(new AgentEvent.AgentStart()));

        Assert.Contains("\"type\":\"core\"", json);
        Assert.Contains("\"event\":{\"type\":\"agent_start\"}", json);
    }

    [Fact]
    public void SerializesModelSelectHarnessEvent()
    {
        var current = new ModelDescriptor("openai", "gpt-4o", "openai-responses");
        var previous = new ModelDescriptor("anthropic", "claude-sonnet-4-5", "anthropic-messages");
        var json = AgentJsonSerializer.Serialize<AgentHarnessEvent>(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ModelSelect(current, previous, "rpc")));

        Assert.Contains("\"type\":\"own\"", json);
        Assert.Contains("\"type\":\"model_select\"", json);
        Assert.Contains("\"provider\":\"openai\"", json);
        Assert.Contains("\"source\":\"rpc\"", json);
    }

    [Fact]
    public void SerializesThinkingLevelSelectAsLowercaseValues()
    {
        var json = AgentJsonSerializer.Serialize<AgentHarnessEvent>(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelSelect(ThinkingLevel.High, ThinkingLevel.Off)));

        Assert.Contains("\"type\":\"thinking_level_select\"", json);
        Assert.Contains("\"level\":\"high\"", json);
        Assert.Contains("\"previousLevel\":\"off\"", json);
    }

    [Fact]
    public void SerializesFlatAgentSessionEventWithoutHarnessEnvelope()
    {
        using var args = JsonDocument.Parse("{\"path\":\"README.md\"}");
        var flat = new AgentHarnessEvent.Core(new AgentEvent.ToolExecutionStart("tc1", "read", args.RootElement.Clone())).ToFlat();

        var json = AgentJsonSerializer.Serialize(flat);

        Assert.Contains("\"type\":\"tool_execution_start\"", json);
        Assert.Contains("\"toolCallId\":\"tc1\"", json);
        Assert.Contains("\"toolName\":\"read\"", json);
        Assert.Contains("\"arguments\":{\"path\":\"README.md\"}", json);
        Assert.DoesNotContain("\"event\"", json);
        Assert.DoesNotContain("\"data\"", json);
    }

    [Fact]
    public void SerializesNewSessionLevelHarnessEvents()
    {
        var json = AgentJsonSerializer.Serialize<AgentHarnessEvent>(new AgentHarnessEvent.Own(new AgentHarnessOwnEvent.ThinkingLevelChanged(ThinkingLevel.High)));

        Assert.Contains("\"type\":\"thinking_level_changed\"", json);
        Assert.Contains("\"level\":\"high\"", json);
    }

    [Fact]
    public void SerializesMessageEndWithAgentMessageConverter()
    {
        var message = AgentMessages.Assistant("hello");
        var json = AgentJsonSerializer.Serialize<AgentEvent>(new AgentEvent.MessageEnd(message));

        Assert.Contains("\"type\":\"message_end\"", json);
        Assert.Contains("\"role\":\"assistant\"", json);
        Assert.Contains("hello", json);
    }

    [Fact]
    public void SerializesMessageUpdateWithConcreteAssistantEventData()
    {
        var message = AgentMessages.Assistant("hel");
        var update = new AssistantMessageEvent.TextDelta(message, 0, "lo");
        var json = AgentJsonSerializer.Serialize<AgentEvent>(new AgentEvent.MessageUpdate(message, update));

        Assert.Contains("\"type\":\"message_update\"", json);
        Assert.Contains("\"assistantMessageEvent\":{\"type\":\"text_delta\"", json);
        Assert.Contains("\"contentIndex\":0", json);
        Assert.Contains("\"delta\":\"lo\"", json);
    }

    [Fact]
    public void ModelDescriptor_WithModelCompat_SerializesAndDeserializesPolymorphically()
    {
        var modelWithOpenAi = new ModelDescriptor(
            "openai", "gpt-4o", "openai-responses",
            Compat: new OpenAICompat(Strict: true, MaxTokensField: "max_completion_tokens"));

        var json = AgentJsonSerializer.Serialize(modelWithOpenAi);
        Assert.Contains("\"compat\":{\"type\":\"openai\",\"strict\":true,\"maxTokensField\":\"max_completion_tokens\"}", json);

        var roundTripped = AgentJsonSerializer.Deserialize<ModelDescriptor>(json);
        Assert.NotNull(roundTripped);
        var openAiCompat = Assert.IsType<OpenAICompat>(roundTripped.Compat);
        Assert.True(openAiCompat.Strict);
        Assert.Equal("max_completion_tokens", openAiCompat.MaxTokensField);

        var modelWithAnthropic = new ModelDescriptor(
            "anthropic", "claude-3-7-sonnet", "anthropic-messages",
            Compat: new AnthropicCompat(CacheControl: "ephemeral"));

        var jsonAnthropic = AgentJsonSerializer.Serialize(modelWithAnthropic);
        Assert.Contains("\"compat\":{\"type\":\"anthropic\",\"cacheControl\":\"ephemeral\"}", jsonAnthropic);

        var roundTrippedAnthropic = AgentJsonSerializer.Deserialize<ModelDescriptor>(jsonAnthropic);
        Assert.NotNull(roundTrippedAnthropic);
        var anthropicCompat = Assert.IsType<AnthropicCompat>(roundTrippedAnthropic.Compat);
        Assert.Equal("ephemeral", anthropicCompat.CacheControl);
    }
}
