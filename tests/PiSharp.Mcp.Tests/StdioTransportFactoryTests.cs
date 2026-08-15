using PiSharp.Extensions;
using PiSharp.Mcp.Transports.Stdio;
using Xunit;

namespace PiSharp.Mcp.Tests;

/// <summary>
/// Stdio MCP spawns are fail-closed: <see cref="StdioTransportFactory.CreateAsync"/> consults the
/// transport context's spawn gate (falling back to <see cref="CapabilityGates.McpSpawn"/>) and
/// rejects the spawn when the gate returns a denial, before any process work happens.
/// </summary>
public sealed class StdioTransportFactoryTests
{
    private static McpServerConfig Server(string command = "mock-server")
        => new(
            Name: "fileserver",
            Source: "settings",
            Transport: McpTransportKind.Stdio,
            Command: command,
            Args: ["--port", "1"]);

    private static McpTransportContext Context(SpawnApproval? gate = null)
        => new(
            AuthStorage: null,
            OpenUrlAsync: (_, _) => Task.CompletedTask,
            Log: _ => { },
            SpawnGate: gate);

    [Fact]
    public async Task CreateAsync_NoGate_CreatesTransport()
    {
        var previous = CapabilityGates.McpSpawn;
        try
        {
            CapabilityGates.McpSpawn = null;
            var factory = new StdioTransportFactory();

            var transport = await factory.CreateAsync(Server(), Context(), CancellationToken.None);

            Assert.NotNull(transport);
        }
        finally
        {
            CapabilityGates.McpSpawn = previous;
        }
    }

    [Fact]
    public async Task CreateAsync_NoGate_FallsBackToCapabilityGatesSeam()
    {
        // No per-context gate → the static McpSpawn seam decides. A deny here must reject.
        var previous = CapabilityGates.McpSpawn;
        try
        {
            CapabilityGates.McpSpawn = request => $"denied {request.Command}";
            var factory = new StdioTransportFactory();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => TestCreateAsync(factory, Context(), Server()).AsTask());
        }
        finally
        {
            CapabilityGates.McpSpawn = previous;
        }
    }

    [Fact]
    public async Task CreateAsync_GateReturnsReason_ThrowsInvalidOperation()
    {
        var factory = new StdioTransportFactory();
        var context = Context(request => "denied: not allow-listed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestCreateAsync(factory, context, Server()).AsTask());
        Assert.Contains("denied: not allow-listed", error.Message);
        Assert.Contains("fileserver", error.Message);
    }

    [Fact]
    public async Task CreateAsync_GateAllows_CreatesTransport()
    {
        var factory = new StdioTransportFactory();
        var context = Context(_ => null);

        var transport = await TestCreateAsync(factory, context, Server());

        Assert.NotNull(transport);
    }

    [Fact]
    public async Task CreateAsync_GateReceivesCommandAndSourceId()
    {
        SpawnRequest? captured = null;
        var factory = new StdioTransportFactory();
        var context = Context(request =>
        {
            captured = request;
            return null;
        });

        await TestCreateAsync(factory, context, new McpServerConfig(
            Name: "ext-server",
            Source: "extension:my-ext",
            Transport: McpTransportKind.Stdio,
            Command: "npx",
            Args: ["-y", "mcp-srv"],
            SourceId: "my-ext"));

        Assert.NotNull(captured);
        Assert.Equal("mcp", captured!.Kind);
        Assert.Equal("npx", captured.Command);
        Assert.Equal(["-y", "mcp-srv"], captured.Args);
        Assert.Equal("my-ext", captured.SourceId);
        Assert.Equal("ext-server", captured.Name);
    }

    private static ValueTask<ModelContextProtocol.Client.IClientTransport> TestCreateAsync(
        StdioTransportFactory factory, McpTransportContext context, McpServerConfig config)
        => factory.CreateAsync(config, context, CancellationToken.None);
}
