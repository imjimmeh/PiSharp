using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;
using PiSharp.Plugins.Lsp;
using Xunit;

namespace PiSharp.Plugins.Lsp.Tests;

/// <summary>
/// Post-write diagnostics middleware: result-content amendment after <c>edit</c>/<c>write</c>
/// tool calls, driven through the in-process harness (<see cref="MiddlewareContextBuilder"/>)
/// against a scripted in-memory fake server.
/// </summary>
public sealed class LspDiagnosticsMiddlewareTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AfterEditToolCallAmendsResultWithDiagnosticsSummary()
    {
        var (middleware, registry, server) = await CreateRunningMiddlewareAsync();
        await using var _ = server;
        try
        {
            var context = MiddlewareContextBuilder.After(
                "edit",
                Args(new { path = Path.Combine(RootPath, "hello.py") }),
                result: "File updated.");

            var amended = false;
            await middleware.HandleAsync(context, (_, _) =>
            {
                amended = true;
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.True(amended);
            Assert.True(context.Modified);
            var content = Assert.Single(context.ModifiedContent!.OfType<TextContent>());
            Assert.Contains("Diagnostics (python):", content.Text);
            Assert.Contains("1 error", content.Text);

            var diagnostic = server.Received.Single(r => r.MethodOrCommand == "textDocument/diagnostic");
            Assert.Equal(new Uri(Path.Combine(RootPath, "hello.py")).AbsoluteUri, diagnostic.ParamsOrArguments.GetProperty("textDocument").GetProperty("uri").GetString());
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task NonEditToolLeavesResultUntouched()
    {
        var (middleware, registry, server) = await CreateRunningMiddlewareAsync();
        await using var _ = server;
        try
        {
            var context = MiddlewareContextBuilder.After(
                "read",
                Args(new { path = Path.Combine(RootPath, "hello.py") }),
                result: "file contents");

            await middleware.HandleAsync(context, (_, _) => Task.CompletedTask, CancellationToken.None);

            Assert.False(context.Modified);
            Assert.Null(context.ModifiedContent);
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownLanguageLeavesResultUntouched()
    {
        var (middleware, registry, server) = await CreateRunningMiddlewareAsync();
        await using var _ = server;
        try
        {
            var context = MiddlewareContextBuilder.After(
                "edit",
                Args(new { path = Path.Combine(RootPath, "unknown.xyz") }),
                result: "File updated.");

            await middleware.HandleAsync(context, (_, _) => Task.CompletedTask, CancellationToken.None);

            Assert.False(context.Modified);
            Assert.Null(context.ModifiedContent);
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task NoRunningServerSkipsDiagnostics()
    {
        // Registry with a config but no spawned server: TryGetClient misses → no-op.
        var factory = new FakeServerProcessFactory();
        var registry = new LspServerRegistry(Configs("python"), RootPath, TimeSpan.FromMinutes(30), factory, NullLoggerFactory.Instance);
        var middleware = new LspDiagnosticsMiddleware(registry, new LspDiagnosticsService(registry));
        try
        {
            var context = MiddlewareContextBuilder.After(
                "write",
                Args(new { path = Path.Combine(RootPath, "hello.py") }),
                result: "File written.");

            await middleware.HandleAsync(context, (_, _) => Task.CompletedTask, CancellationToken.None);

            Assert.False(context.Modified);
            Assert.Empty(factory.Processes); // never spawns a server just for diagnostics
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }

    private static async Task<(LspDiagnosticsMiddleware Middleware, LspServerRegistry Registry, ScriptedWireServer Server)> CreateRunningMiddlewareAsync()
    {
        var factory = new FakeServerProcessFactory();
        var registry = new LspServerRegistry(Configs("python"), RootPath, TimeSpan.FromMinutes(30), factory, NullLoggerFactory.Instance);

        var filePath = Path.Combine(RootPath, "hello.py");
        Directory.CreateDirectory(RootPath);
        await File.WriteAllTextAsync(filePath, "x = 1\n");
        var openTask = registry.OpenFileAsync(filePath, CancellationToken.None);
        var process = await factory.NextProcessAsync.WaitAsync(Timeout);
        var server = new ScriptedWireServer(process, WireProtocol.LspJsonRpc)
        {
            OnRequest = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult<object?>(new { capabilities = new { } }),
                "textDocument/diagnostic" => Task.FromResult<object?>(new
                {
                    kind = "full",
                    items = new[]
                    {
                        new { range = new { start = new { line = 4, character = 0 }, end = new { line = 4, character = 10 } }, severity = 1, message = "undefined name 'foo'" },
                    },
                }),
                _ => Task.FromResult<object?>(new { }),
            },
        };
        server.Start();

        // Spawn the server + open the file so the middleware fast-path hits.
        await openTask.WaitAsync(Timeout);

        var middleware = new LspDiagnosticsMiddleware(registry, new LspDiagnosticsService(registry));
        return (middleware, registry, server);
    }

    private const string RootPath = @"C:\work\demo";

    private static IReadOnlyDictionary<string, LanguageServerConfig> Configs(string language)
    {
        var section = JsonDocument.Parse(
                """{"command":["pyright-langserver","--stdio"],"extensions":[".py"]}""")
            .RootElement;
        var result = LanguageServerConfigParser.Parse(section, language);
        Assert.True(result.IsOk);
        return new Dictionary<string, LanguageServerConfig> { [language] = result.Value };
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}
