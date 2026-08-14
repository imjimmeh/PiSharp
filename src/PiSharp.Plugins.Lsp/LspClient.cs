using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

namespace PiSharp.Plugins.Lsp;

/// <summary>
/// Typed LSP facade over one <see cref="ManagedRpcServer"/> (per language). Translates
/// file paths/positions into LSP <c>textDocument</c> URIs and requests; never applies edits.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly int _timeoutMs;
    private readonly ILogger _logger;

    public LspClient(ManagedRpcServer server, int timeoutMs = 10000, ILoggerFactory? loggerFactory = null)
    {
        Server = server;
        _timeoutMs = timeoutMs;
        _logger = loggerFactory?.CreateLogger<LspClient>() ?? NullLogger<LspClient>.Instance;
    }

    public ManagedRpcServer Server { get; }

    /// <summary>Pushed <c>textDocument/publishDiagnostics</c> notifications from the server.</summary>
    public event Action<JsonElement>? PublishDiagnostics;

    public Task<JsonElement> InitializeAsync(
        Uri rootUri,
        JsonElement clientCapabilities,
        object? initializationOptions = null,
        CancellationToken ct = default)
        => RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            clientInfo = new { name = "pisharp-lsp", version = "1.0.0" },
            rootUri = rootUri.AbsoluteUri,
            capabilities = clientCapabilities,
            initializationOptions,
        }, ct);

    /// <summary>Sends the <c>initialized</c> notification that completes the LSP handshake.</summary>
    public Task NotifyInitializedAsync(CancellationToken ct = default)
        => Server.NotifyAsync("initialized", new { }, ct);

    public Task DidOpenAsync(string uri, string text, string languageId, CancellationToken ct = default)
        => Server.NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId, version = 1, text },
        }, ct);

    public Task DidChangeAsync(string uri, string fullText, CancellationToken ct = default)
        => Server.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new object[] { new { text = fullText } },
        }, ct);

    public Task DidCloseAsync(string uri, CancellationToken ct = default)
        => Server.NotifyAsync("textDocument/didClose", new
        {
            textDocument = new { uri },
        }, ct);

    public Task<JsonElement> HoverAsync(string uri, int line, int character, CancellationToken ct = default)
        => RequestAsync("textDocument/hover", PositionParams(uri, line, character), ct);

    public Task<JsonElement> DefinitionAsync(string uri, int line, int character, CancellationToken ct = default)
        => RequestAsync("textDocument/definition", PositionParams(uri, line, character), ct);

    public Task<JsonElement> ReferencesAsync(string uri, int line, int character, bool includeDeclaration, CancellationToken ct = default)
        => RequestAsync("textDocument/references", new
        {
            textDocument = new { uri },
            position = Position(line, character),
            context = new { includeDeclaration },
        }, ct);

    public Task<JsonElement> PrepareRenameAsync(string uri, int line, int character, CancellationToken ct = default)
        => RequestAsync("textDocument/prepareRename", PositionParams(uri, line, character), ct);

    public Task<JsonElement> RenameAsync(string uri, int line, int character, string newName, CancellationToken ct = default)
        => RequestAsync("textDocument/rename", new
        {
            textDocument = new { uri },
            position = Position(line, character),
            newName,
        }, ct);

    public Task<JsonElement> DiagnosticsAsync(string uri, CancellationToken ct = default)
        => RequestAsync("textDocument/diagnostic", new { textDocument = new { uri } }, ct);

    public Task<JsonElement> DocumentSymbolsAsync(string uri, CancellationToken ct = default)
        => RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } }, ct);

    public Task<JsonElement> WorkspaceSymbolsAsync(string query, CancellationToken ct = default)
        => RequestAsync("workspace/symbol", new { query }, ct);

    public async Task<JsonElement> RawRequestAsync(string method, JsonElement? parameters, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        return await Server.RequestRawAsync(method, parameters, id: null, timeoutCts.Token).ConfigureAwait(false);
    }

    public Task<JsonElement> CodeActionsAsync(string uri, LspRange range, JsonElement? context, CancellationToken ct = default)
        => RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri },
            range = new { start = Position(range.Start.Line, range.Start.Character), end = Position(range.End.Line, range.End.Character) },
            context = context is { ValueKind: JsonValueKind.Object } contextElement ? (object)contextElement : new { diagnostics = Array.Empty<object>() },
        }, ct);

    public Task<JsonElement> FormattingAsync(string uri, CancellationToken ct = default)
        => RequestAsync("textDocument/formatting", new
        {
            textDocument = new { uri },
            options = new { tabSize = 4, insertSpaces = true },
        }, ct);

    public Task<JsonElement> RangeFormattingAsync(string uri, LspRange range, CancellationToken ct = default)
        => RequestAsync("textDocument/rangeFormatting", new
        {
            textDocument = new { uri },
            range = new { start = Position(range.Start.Line, range.Start.Character), end = Position(range.End.Line, range.End.Character) },
            options = new { tabSize = 4, insertSpaces = true },
        }, ct);



    private Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        return Server.RequestAsync(method, parameters, timeoutCts.Token);
    }

    private static object PositionParams(string uri, int line, int character)
        => new
        {
            textDocument = new { uri },
            position = Position(line, character),
        };

    private static object Position(int line, int character)
        => new { line, character };

    internal void RouteNotification(string method, JsonElement parameters)
    {
        if (method == "textDocument/publishDiagnostics")
        {
            _logger.LogDebug("lsp: publishDiagnostics for {Uri}", parameters.TryGetProperty("uri", out var uri) ? uri.GetString() : "?");
            PublishDiagnostics?.Invoke(parameters.Clone());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "Server dispose failed; process already gone.");
        }
    }
}
