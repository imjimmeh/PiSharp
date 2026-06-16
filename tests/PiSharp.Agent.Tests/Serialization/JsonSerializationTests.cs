using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Serialization;
using Xunit;

namespace PiSharp.Agent.Tests.Serialization;

public sealed class JsonSerializationTests
{
    [Fact]
    public void SessionEntrySerializesExactJsonlDiscriminator()
    {
        var entry = new BranchSummaryEntry
        {
            Id = "abc",
            ParentId = null,
            Timestamp = DateTimeOffset.Parse("2026-05-21T12:00:00+00:00"),
            FromId = "old",
            Summary = "summary"
        };

        var json = AgentJsonSerializer.Serialize<SessionTreeEntry>(entry);
        Assert.Contains("\"type\":\"branch_summary\"", json);
        Assert.DoesNotContain("branchsummaryentry", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionEntryDeserializesExactJsonlDiscriminator()
    {
        const string json = "{\"type\":\"message\",\"id\":\"m1\",\"parentId\":null,\"timestamp\":\"2026-05-21T12:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"timestamp\":1779364800000}}";
        var entry = AgentJsonSerializer.Deserialize<SessionTreeEntry>(json);
        var message = Assert.IsType<MessageEntry>(entry);
        Assert.Equal("message", message.Type);
        Assert.IsType<UserMessage>(message.Message);
    }

    [Fact]
    public void MessageContentSerializesToolCallBlocks()
    {
        using var args = JsonDocument.Parse("{\"path\":\"README.md\"}");
        MessageContent content = new ToolCallContent("tc1", "read", args.RootElement.Clone());
        var json = JsonSerializer.Serialize(content, AgentJsonSerializer.Options);
        Assert.Contains("\"type\":\"toolCall\"", json);
        Assert.Contains("\"name\":\"read\"", json);
    }

    [Fact]
    public void AssistantMessageDeserializesOldShapeWithoutNewFields()
    {
        const string json = "{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"api\":\"test\",\"provider\":\"provider\",\"model\":\"model\",\"timestamp\":1779364800000}";

        var message = JsonSerializer.Deserialize<AgentMessage>(json, AgentJsonSerializer.Options);

        var assistant = Assert.IsType<AssistantMessage>(message);
        Assert.Null(assistant.ResponseId);
        Assert.Null(assistant.ResponseModel);
        Assert.Null(assistant.Diagnostics);
        Assert.Equal("hello", Assert.IsType<TextContent>(assistant.Content[0]).Text);
        Assert.NotNull(assistant.Usage);
        Assert.Equal(0, assistant.Usage.Cost!.Total);
    }

    [Fact]
    public void AssistantMessageRoundTripsNewMetadata()
    {
        AgentMessage message = new AssistantMessage(
            [new TextContent("hello", "text-sig")],
            Api: "test-api",
            Provider: "test-provider",
            Model: "test-model",
            ResponseModel: "response-model",
            ResponseId: "response-id",
            Diagnostics: [new ProviderDiagnostic("warning", "diagnostic")]);

        var json = JsonSerializer.Serialize(message, AgentJsonSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<AgentMessage>(json, AgentJsonSerializer.Options);

        var assistant = Assert.IsType<AssistantMessage>(deserialized);
        Assert.Equal("response-id", assistant.ResponseId);
        Assert.Equal("response-model", assistant.ResponseModel);
        Assert.Equal("warning", Assert.Single(assistant.Diagnostics!).Type);
        Assert.Equal("text-sig", Assert.IsType<TextContent>(assistant.Content[0]).TextSignature);
    }

    [Fact]
    public void AssistantMessageRoundTripsUsage()
    {
        AgentMessage message = new AssistantMessage(
            [new TextContent("hello")],
            Provider: "test-provider",
            Usage: new UsageInfo(Input: 10, Output: 5, CacheRead: 2, CacheWrite: 1, TotalTokens: 18,
                Cost: new UsageCost(Input: 0.01m, Output: 0.02m, CacheRead: 0.001m, CacheWrite: 0.002m, Total: 0.033m)));

        var json = JsonSerializer.Serialize(message, AgentJsonSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<AgentMessage>(json, AgentJsonSerializer.Options);

        var assistant = Assert.IsType<AssistantMessage>(deserialized);
        Assert.NotNull(assistant.Usage);
        Assert.Equal(10, assistant.Usage.Input);
        Assert.Equal(5, assistant.Usage.Output);
        Assert.Equal(18, assistant.Usage.TotalTokens);
        Assert.Equal(0.033m, assistant.Usage.Cost!.Total);
    }

    [Fact]
    public void AssistantMessageUsageSerializesDefaultCostForPiCompatibility()
    {
        AgentMessage message = new AssistantMessage(
            [new TextContent("hello")],
            Provider: "test-provider",
            Usage: new UsageInfo(Input: 10, Output: 5, CacheRead: 2, CacheWrite: 1, TotalTokens: 18));

        var json = JsonSerializer.Serialize(message, AgentJsonSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<AgentMessage>(json, AgentJsonSerializer.Options);

        Assert.Contains("\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}", json);
        var assistant = Assert.IsType<AssistantMessage>(deserialized);
        Assert.NotNull(assistant.Usage);
        Assert.NotNull(assistant.Usage.Cost);
        Assert.Equal(0, assistant.Usage.Cost.Total);
    }

    [Fact]
    public void AssistantMessageSerializesDefaultUsageForPiCompatibility()
    {
        AgentMessage message = new AssistantMessage([new TextContent("hello")], Provider: "test-provider");

        var json = JsonSerializer.Serialize(message, AgentJsonSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<AgentMessage>(json, AgentJsonSerializer.Options);

        Assert.Contains("\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}", json);
        var assistant = Assert.IsType<AssistantMessage>(deserialized);
        Assert.NotNull(assistant.Usage);
        Assert.Equal(0, assistant.Usage.TotalTokens);
        Assert.Equal(0, assistant.Usage.Cost!.Total);
    }

    [Fact]
    public void MessageContentRoundTripsSignaturesAndRedaction()
    {
        MessageContent[] contents =
        [
            new TextContent("text", "text-sig"),
            new ThinkingContent("think", "thinking-sig", Redacted: true),
        ];

        var json = JsonSerializer.Serialize(contents, AgentJsonSerializer.Options);
        var deserialized = JsonSerializer.Deserialize<MessageContent[]>(json, AgentJsonSerializer.Options)!;

        Assert.Equal("text-sig", Assert.IsType<TextContent>(deserialized[0]).TextSignature);
        var thinking = Assert.IsType<ThinkingContent>(deserialized[1]);
        Assert.Equal("thinking-sig", thinking.ThinkingSignature);
        Assert.True(thinking.Redacted);
    }
}
