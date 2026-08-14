using System.Text.Json;
using PiSharp.Abstractions.Messages;
using Xunit;

namespace PiSharp.AgentMessaging.Tests;

public sealed class HubToolTests : IAsyncLifetime
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "pisharp-agentmessaging-hub", Guid.NewGuid().ToString("N"));
    private AgentRosterService _roster = null!;
    private AgentMessageStore _store = null!;
    private AgentMessageRouter _router = null!;
    private List<AgentMessage> _delivered = [];
    private HubTool _tool = null!;

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
        _tool = new HubTool("child-a", _roster, _router);
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
    public void BuildSchema_ExposesHubToolInputShape()
    {
        var schema = HubTool.BuildSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        foreach (var name in new[] { "operation", "target", "body", "delivery", "watch", "limit" })
            Assert.True(properties.TryGetProperty(name, out _), $"schema missing property '{name}'");
    }

    [Fact]
    public async Task List_ReturnsFamilyMarkdownTable()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.List)));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("| id | name | role | status |", text);
        Assert.Contains("`root`", text);
        Assert.Contains("`child-b`", text);
    }

    [Fact]
    public async Task List_RespectsLimit()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.List, Limit: 1)));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        var rows = text.Split('\n').Count(line => line.StartsWith("| `", StringComparison.Ordinal));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Send_RoutesAndReportsPerRecipientStatus()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.Send, Target: "child-b", Body: "hi", Delivery: "follow_up")));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("sent to `child-b`:delivered", text);
        Assert.Single(_delivered);
        Assert.Equal(AgentMessageDelivery.FollowUp, _delivered[0].Delivery);
    }

    [Fact]
    public async Task Send_InvalidTarget_ReportsTypedError()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.Send, Target: "nobody", Body: "hi")));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains(AgentMessagingErrorCodes.TargetInvalid, text);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Steer_AcceptsAndRoutes()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.Steer, Target: "child-b", Body: "go")));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("steer child-b accepted", text);
        Assert.Equal(AgentMessageDelivery.Steer, Assert.Single(_delivered).Delivery);
    }

    [Fact]
    public async Task Steer_MissingTarget_ReportsUsage()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.Steer)));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("target is required", text);
    }

    [Fact]
    public async Task Watch_RunningTarget_ReportsStatus()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.Watch, Target: "child-b", Watch: true)));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("status=running", text);
        Assert.Contains("subscribed", text);
    }

    [Fact]
    public async Task Watch_UnknownTarget_ReportsUnknown()
    {
        var result = await _tool.ExecuteAsync("1", Params(new HubToolInput(HubOperation.Watch, Target: "ghost")));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("unknown target 'ghost'", text);
    }

    [Fact]
    public async Task Execute_EmptyParameters_DefaultsToList()
    {
        var result = await _tool.ExecuteAsync("1", JsonSerializer.SerializeToElement(new { }));

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content)).Text;
        Assert.Contains("| id | name | role | status |", text);
    }
}
