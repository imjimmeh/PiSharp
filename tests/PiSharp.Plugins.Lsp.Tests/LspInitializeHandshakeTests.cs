using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.Lsp;
using Xunit;

namespace PiSharp.Plugins.Lsp.Tests;

/// <summary>
/// The LSP <c>initialize</c>/<c>initialized</c> handshake and <c>didOpen</c> flow through
/// <see cref="LspServerRegistry"/> against a scripted in-memory fake server.
/// </summary>
public sealed class LspInitializeHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string RootPath = @"C:\work\demo";

    [Fact]
    public async Task GetClientAsyncSpawnsServerAndCompletesInitializeHandshake()
    {
        var factory = new FakeServerProcessFactory();
        var registry = new LspServerRegistry(Configs("python"), RootPath, TimeSpan.FromMinutes(30), factory, NullLoggerFactory.Instance);

        var getClientTask = registry.GetClientAsync("python", CancellationToken.None);
        var process = await factory.NextProcessAsync.WaitAsync(Timeout);
        await using var server = new ScriptedWireServer(process, WireProtocol.LspJsonRpc)
        {
            OnRequest = (method, _, _) => method switch
            {
                _ => Task.FromResult<object?>(new { }),
            },
        };
        server.Start();

        try
        {
            var client = await getClientTask.WaitAsync(Timeout);

            Assert.NotNull(client);
            Assert.False(client.Server.HasExited);
            Assert.Equal("python", client.Server.Key);
            Assert.Equal(new[] { "pyright-langserver", "--stdio" }, client.Server.Command);

            var initialize = server.Received.Single(r => r.MethodOrCommand == "initialize");
            Assert.False(initialize.IsNotification);
            Assert.Equal(
                new Uri(Path.GetFullPath(RootPath)).AbsoluteUri,
                initialize.ParamsOrArguments.GetProperty("rootUri").GetString());
            Assert.Equal(JsonValueKind.Object, initialize.ParamsOrArguments.GetProperty("capabilities").ValueKind);

            // The initialized notification completes the handshake.
            Assert.Contains(server.Received, r => r.MethodOrCommand == "initialized" && r.IsNotification);
            Assert.Single(factory.Processes);
        }
        finally
        {
            await registry.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpenFileAsyncSendsDidOpenWithResolvedUriAndLanguageId()
    {
        var factory = new FakeServerProcessFactory();
        var registry = new LspServerRegistry(Configs("python"), RootPath, TimeSpan.FromMinutes(30), factory, NullLoggerFactory.Instance);
        var filePath = Path.Combine(RootPath, "hello.py");
        Directory.CreateDirectory(RootPath);
        await File.WriteAllTextAsync(filePath, "print('hi')\n");

        var openTask = registry.OpenFileAsync(filePath, CancellationToken.None);
        var process = await factory.NextProcessAsync.WaitAsync(Timeout);
        await using var server = new ScriptedWireServer(process, WireProtocol.LspJsonRpc)
        {
            OnRequest = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult<object?>(new { capabilities = new { } }),
                _ => Task.FromResult<object?>(new { }),
            },
        };
        server.Start();

        try
        {
            var client = await openTask.WaitAsync(Timeout);

            Assert.NotNull(client);

            await WaitUntilAsync(() => server.Received.Any(r => r.MethodOrCommand == "textDocument/didOpen"));
            var didOpen = server.Received.Single(r => r.MethodOrCommand == "textDocument/didOpen");
            var textDocument = didOpen.ParamsOrArguments.GetProperty("textDocument");
            Assert.Equal(new Uri(filePath).AbsoluteUri, textDocument.GetProperty("uri").GetString());
            Assert.Equal("python", textDocument.GetProperty("languageId").GetString());
            Assert.Equal("print('hi')\n", textDocument.GetProperty("text").GetString());
        }
        finally
        {
            await registry.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnknownLanguageThrowsListingConfiguredLanguages()
    {
        var factory = new FakeServerProcessFactory();
        var registry = new LspServerRegistry(Configs("python"), RootPath, TimeSpan.FromMinutes(30), factory);

        try
        {
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(
                () => registry.GetClientAsync("rust", CancellationToken.None));
            Assert.Contains("python", exception.Message);
            Assert.Empty(factory.Processes);
        }
        finally
        {
            await registry.DisposeAsync();
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }


    private static IReadOnlyDictionary<string, LanguageServerConfig> Configs(string language)
    {
        var section = JsonDocument.Parse(
                """{"command":["pyright-langserver","--stdio"],"extensions":[".py"],"init":{"initializationOptions":{"x":1}}}""")
            .RootElement;
        var result = LanguageServerConfigParser.Parse(section, language);
        Assert.True(result.IsOk);
        return new Dictionary<string, LanguageServerConfig> { [language] = result.Value };
    }
}
