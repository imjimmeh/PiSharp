using System.Text.Json;
using PiSharp.Abstractions.Messages;
using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class AgentMessageToolTests : IAsyncLifetime
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pisharp-agentmessaging-amtool", Guid.NewGuid().ToString("N"));
    private AgentRosterService _roster = null!;
    private AgentMessageStore _store = null!;
    private AgentMessageRouter _router = null!;
    private List<AgentMessage> _delivered = [];
    private AgentMessageTool _tool = null!;

    public Task InitializeAsync()
    {
        _roster = new AgentRosterService();
        _roster.Register(TestAgents.Agent("root"));
        _roster.Register(TestAgents.Agent("child-a", parent: "root"));
        _roster.Register(TestAgents.Agent("child-b", parent: "root"));
        _store = new AgentMessageStore(_storeDir);
        _router = new AgentMessageRouter(_roster, _store, new AgentMessagingOptions(), (m, _) =>
        {
            _delivered.Add(m);
            return Task.CompletedTask;
        });
        _tool = new AgentMessageTool("child-a", _roster, _router);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _router.DisposeAsync();
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }

    private static JsonElement Params(object input)
        => JsonSerializer.SerializeToElement(input, AgentMessagingJson.Options);

    [Fact]
    public void BuildSchema_ExposesActionReceiverBody()
    {
        var schema = AgentMessageTool.BuildSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        foreach (var name in new[] { "action", "receiver", "body", "since", "limit" })
            Assert.True(properties.TryGetProperty(name, out _), $"schema missing property '{name}'");
    }

    [Fact]
    public async Task Send_ToParent_ResolvesParentAndDelivers()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "send", receiver = "parent", body = "report back" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("sent to `root`:delivered", text);

        var delivered = Assert.Single(_delivered);
        Assert.Equal(["root"], delivered.ToAgentIds);
        Assert.Equal("report back", delivered.Body);
    }

    [Fact]
    public async Task Send_ToSibling_Delivers()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "send", receiver = "child-b", body = "hi sibling" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("sent to `child-b`:delivered", text);
    }

    [Fact]
    public async Task Send_ToAll_Broadcasts()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "send", receiver = "all", body = "hi all" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("`root`", text);
        Assert.Contains("`child-b`", text);
        Assert.Equal(2, _delivered.Count);
    }

    [Fact]
    public async Task Send_UnknownReceiver_ReportsTypedError()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "send", receiver = "ghost", body = "hi" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains(AgentMessagingErrorCodes.TargetInvalid, text);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Send_MissingBody_ReportsUsage()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "send", receiver = "parent" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("body is required", text);
    }

    [Fact]
    public async Task Read_EmptyInbox_ReportsNoMessages()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "read" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("no messages", text);
    }

    [Fact]
    public async Task Read_ReturnsDeliveredMessagesNewestFirst()
    {
        await _router.SendAsync("child-b", ["child-a"], "first", AgentMessageDelivery.Steer);
        await _router.SendAsync("child-b", ["child-a"], "second", AgentMessageDelivery.Steer);

        var result = await _tool.ExecuteAsync("1", Params(new { action = "read" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("**child-b**", text);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public async Task Read_RespectsLimit()
    {
        await _router.SendAsync("child-b", ["child-a"], "first", AgentMessageDelivery.Steer);
        await _router.SendAsync("child-b", ["child-a"], "second", AgentMessageDelivery.Steer);

        var result = await _tool.ExecuteAsync("1", Params(new { action = "read", limit = 1 }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("second", text);
        Assert.DoesNotContain("first", text);
    }

    [Fact]
    public async Task UnknownAction_ReportsUsage()
    {
        var result = await _tool.ExecuteAsync("1", Params(new { action = "spam" }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("action must be 'send' or 'read'", text);
    }
}
