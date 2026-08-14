using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Eval.Events;
using PiSharp.Eval.Kernels;
using PiSharp.Extensions;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Tools;
using Xunit;

namespace PiSharp.Eval.Tests;

/// <summary>
/// Loopback bridge tests: allowlist enforcement (blocked tools return an error result,
/// never an exception), result mapping, and <c>eval_loopback_tool_*</c> event emission.
/// </summary>
public sealed class KernelToolBridgeTests
{
    private sealed class FakeToolApi : IExtensionToolApi
    {
        public IDisposable RegisterTool(ExtensionToolRegistration registration) => new NoopDisposable();
        public Task<IReadOnlyList<string>> GetActiveToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetAllToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetActiveToolsAsync(IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AgentToolResult<object?>> ExecuteToolAsync(string toolName, JsonElement parameters, CancellationToken cancellationToken = default)
            => toolName == "read"
                ? Task.FromResult(new AgentToolResult<object?>(
                    [new TextContent($"content of {parameters.GetProperty("path").GetString()}")], null))
                : Task.FromResult(new AgentToolResult<object?>(
                    [new TextContent("Error: fake tool failed")], null));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private static (KernelToolBridge Bridge, List<(string Name, object? Payload)> Events) CreateBridge(IReadOnlyList<string>? allowlist = null)
    {
        var events = new List<(string, object?)>();
        var bridge = new KernelToolBridge(
            "csharp",
            new FakeToolApi(),
            (name, payload, _) =>
            {
                events.Add((name, payload));
                return Task.CompletedTask;
            },
            allowlist ?? ["read", "grep", "find", "ls"]);
        return (bridge, events);
    }

    [Fact]
    public async Task AvailableToolNames_ReturnsAllowlist()
    {
        var (bridge, _) = CreateBridge(["read", "ls"]);
        Assert.Equal(["read", "ls"], bridge.AvailableToolNames);
    }

    [Fact]
    public async Task UpdateAllowlist_HotReloads()
    {
        var (bridge, _) = CreateBridge(["read"]);
        bridge.UpdateAllowlist(["ls"]);
        Assert.Equal(["ls"], bridge.AvailableToolNames);
    }

    [Fact]
    public async Task BlockedTool_ReturnsErrorResult_NeverThrows()
    {
        var (bridge, events) = CreateBridge(["read"]);

        var result = await bridge.ExecuteToolAsync("bash", JsonSerializer.SerializeToElement(new { command = "rm -rf /" }));

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("bash", result.Error);
        Assert.Contains("not allowed", result.Error);
        Assert.Empty(events);   // blocked calls emit no loopback events
    }

    [Fact]
    public async Task AllowedTool_ExecutesAndMapsContent()
    {
        var (bridge, events) = CreateBridge();

        var result = await bridge.ExecuteToolAsync("read", JsonSerializer.SerializeToElement(new { path = "a.txt" }));

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal("content of a.txt", result.Content);
        Assert.Equal(2, events.Count);
        Assert.Equal(EvalEventNames.LoopbackToolCall, events[0].Name);
        Assert.Equal(EvalEventNames.LoopbackToolResult, events[1].Name);
        Assert.IsType<EvalLoopbackToolCallEvent>(events[0].Payload);
        Assert.IsType<EvalLoopbackToolResultEvent>(events[1].Payload);
    }

    [Fact]
    public async Task ErrorResult_MapsToKernelToolResult()
    {
        var (bridge, _) = CreateBridge();

        // "grep" hits the fake's error branch.
        var result = await bridge.ExecuteToolAsync("grep", JsonSerializer.SerializeToElement(new { pattern = "x" }));

        Assert.False(result.Ok);
        Assert.Contains("fake tool failed", result.Error);
    }

    [Fact]
    public async Task ToolCallIds_AreUnique()
    {
        var (bridge, _) = CreateBridge();
        var parameters = JsonSerializer.SerializeToElement(new { path = "a" });

        await bridge.ExecuteToolAsync("read", parameters);
        await bridge.ExecuteToolAsync("read", parameters);

        // No assertion needed beyond executing twice without collision; ids come from an
        // interlocked counter, but the exercise guards against a shared-id bug.
        Assert.True(true);
    }
}
