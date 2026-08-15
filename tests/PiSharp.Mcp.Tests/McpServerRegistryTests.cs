using PiSharp.Mcp;
using Xunit;

namespace PiSharp.Mcp.Tests;

/// <summary>
/// Extension-contributed MCP servers carry their provenance (<see cref="McpServerConfig.SourceId"/>)
/// so the host can honor the originating extension in spawn-gate decisions.
/// </summary>
public sealed class McpServerRegistryTests
{
    private static McpServerConfig StdioServer()
        => new(
            Name: "fileserver",
            Source: "settings",
            Transport: McpTransportKind.Stdio,
            Command: "mock-server");

    [Fact]
    public void RegisterServer_StampsSourceId_OnStoredConfig()
    {
        try
        {
            McpServerRegistry.ClearForTesting();
            McpServerRegistry.RegisterServer("ext:my-ext", StdioServer());

            var stored = Assert.Single(McpServerRegistry.GetContributedServers());
            Assert.Equal("ext:my-ext", stored.SourceId);
        }
        finally
        {
            McpServerRegistry.ClearForTesting();
        }
    }

    [Fact]
    public void RegisterServer_OverwritesPreviousServerForSameSource()
    {
        try
        {
            McpServerRegistry.ClearForTesting();
            McpServerRegistry.RegisterServer("ext:my-ext", StdioServer());
            McpServerRegistry.RegisterServer("ext:my-ext", StdioServer() with { Name = "second" });

            var stored = Assert.Single(McpServerRegistry.GetContributedServers());
            Assert.Equal("second", stored.Name);
            Assert.Equal("ext:my-ext", stored.SourceId);
        }
        finally
        {
            McpServerRegistry.ClearForTesting();
        }
    }

    [Fact]
    public void UnregisterBySource_RemovesServer()
    {
        try
        {
            McpServerRegistry.ClearForTesting();
            McpServerRegistry.RegisterServer("ext:my-ext", StdioServer());
            Assert.Single(McpServerRegistry.GetContributedServers());

            McpServerRegistry.UnregisterBySource("ext:my-ext");

            Assert.Empty(McpServerRegistry.GetContributedServers());
        }
        finally
        {
            McpServerRegistry.ClearForTesting();
        }
    }
}
