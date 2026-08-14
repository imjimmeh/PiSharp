using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

namespace PiSharp.Plugins.Lsp;

/// <summary>
/// The <c>lsp</c> tool handler. Every op returns LSP results as structured details plus a
/// readable markdown summary; the tool never applies edits (the model uses edit/write).
/// </summary>
public sealed class LspToolHandler
{
    private readonly LspServerRegistry _registry;
    private readonly Func<bool> _isEnabled;
    private readonly string _cwd;
    private readonly ILogger _logger;

    public LspToolHandler(
        LspServerRegistry registry,
        Func<bool> isEnabled,
        string cwd,
        ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _isEnabled = isEnabled;
        _cwd = cwd;
        _logger = loggerFactory?.CreateLogger<LspToolHandler>() ?? NullLogger<LspToolHandler>.Instance;
    }

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!_isEnabled())
        {
            return ErrorResult("lsp is disabled; enable it via settings 'extensions.pisharp-lsp.enabled' (default: false).");
        }

        var input = JsonSerializer.Deserialize<LspToolInput>(parameters.GetRawText());
        if (input is null)
        {
            return ErrorResult("Invalid lsp tool arguments: expected an object with 'op'.");
        }

        var stopwatch = Stopwatch.StartNew();
        LspToolDetails details;
        string content;
        try
        {
            var execution = await ExecuteOpAsync(input, cancellationToken).ConfigureAwait(false);
            details = execution.Details;
            content = execution.Content;
        }
        catch (JsonRpcRemoteException exception)
        {
            stopwatch.Stop();
            details = new LspToolDetails(input.Op, null, null, stopwatch.ElapsedMilliseconds, null, null, null, null, exception.Message);
            content = $"lsp {input.Op} failed: {exception.Message}";
        }
        catch (KeyNotFoundException exception)
        {
            stopwatch.Stop();
            details = new LspToolDetails(input.Op, null, null, stopwatch.ElapsedMilliseconds, null, null, null, null, exception.Message);
            content = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TaskCanceledException or TimeoutException)
        {
            stopwatch.Stop();
            details = new LspToolDetails(input.Op, null, null, stopwatch.ElapsedMilliseconds, null, null, null, null, exception.Message);
            content = $"lsp {input.Op} failed: {exception.Message}";
        }

        return new AgentToolResult<object?>([new TextContent(content)], details);
    }

    private async Task<(LspToolDetails Details, string Content)> ExecuteOpAsync(LspToolInput input, CancellationToken cancellationToken)
    {
        var language = input.Language ?? _registry.ResolveLanguage(input.Path);
        if (language is null)
        {
            throw new KeyNotFoundException(
                $"Cannot determine language for lsp {input.Op}: no language id given and '{input.Path}' has no configured extension. Configured languages: {string.Join(", ", _registry.Languages)}.");
        }


        LspClient client;
        if (input.Path is not null)
        {
            var absolutePath = ResolvePath(input.Path);
            client = await _registry.OpenFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            client = await _registry.GetClientAsync(language, cancellationToken).ConfigureAwait(false);
        }

        var serverCommand = string.Join(' ', client.Server.Command);
        var uri = input.Path is not null ? LspServerRegistry.PathToUri(ResolvePath(input.Path)).AbsoluteUri : null;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            (JsonElement Result, string? RenameEdits, string? FormatEdits) outcome = input.Op switch
            {
                "hover" => (await client.HoverAsync(RequireUri(uri, input), RequireLine(input), RequireCharacter(input), cancellationToken).ConfigureAwait(false), null, null),
                "definition" => (await client.DefinitionAsync(RequireUri(uri, input), RequireLine(input), RequireCharacter(input), cancellationToken).ConfigureAwait(false), null, null),
                "references" => (await client.ReferencesAsync(RequireUri(uri, input), RequireLine(input), RequireCharacter(input), includeDeclaration: true, cancellationToken).ConfigureAwait(false), null, null),
                "rename" => await ExecuteRenameAsync(client, RequireUri(uri, input), RequireLine(input), RequireCharacter(input), RequireNewName(input), cancellationToken).ConfigureAwait(false),
                "diagnostics" => (await client.DiagnosticsAsync(RequireUri(uri, input), cancellationToken).ConfigureAwait(false), null, null),
                "symbols" => uri is null
                    ? (await client.WorkspaceSymbolsAsync(input.Query ?? string.Empty, cancellationToken).ConfigureAwait(false), null, null)
                    : (await client.DocumentSymbolsAsync(uri, cancellationToken).ConfigureAwait(false), null, null),
                "code_actions" => (await client.CodeActionsAsync(RequireUri(uri, input), RequireRange(input), input.Params, cancellationToken).ConfigureAwait(false), null, null),
                "format" => input.Range is null
                    ? (await client.FormattingAsync(RequireUri(uri, input), cancellationToken).ConfigureAwait(false), null, null)
                    : (await client.RangeFormattingAsync(RequireUri(uri, input), input.Range, cancellationToken).ConfigureAwait(false), null, null),
                "request" => (await client.RawRequestAsync(RequireMethod(input), input.Params, cancellationToken).ConfigureAwait(false), null, null),
                _ => throw new InvalidOperationException($"Unknown lsp op '{input.Op}'. Supported ops: hover, definition, references, rename, diagnostics, symbols, code_actions, format, request."),
            };
            var (rawResult, renameEdits, formatEdits) = outcome;
            stopwatch.Stop();
            var details = new LspToolDetails(
                input.Op, language, serverCommand, stopwatch.ElapsedMilliseconds,
                Diagnostics: null, RenameEdits: renameEdits, FormatEdits: formatEdits,
                RawResult: rawResult, Error: null);
            return (details, FormatContent(input.Op, language, rawResult, renameEdits, formatEdits));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception, "lsp {Op} failed for language {Language}", input.Op, language);
            throw;
        }
    }

    private static async Task<(JsonElement Result, string? RenameEdits, string? FormatEdits)> ExecuteRenameAsync(
        LspClient client, string uri, int line, int character, string newName, CancellationToken cancellationToken)
    {
        // prepareRename probe: ignored when unsupported, so servers without the method still rename.
        try
        {
            await client.PrepareRenameAsync(uri, line, character, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException exception) when (exception.Code is -32601 or -32602)
        {
        }

        var result = await client.RenameAsync(uri, line, character, newName, cancellationToken).ConfigureAwait(false);
        return (result, CompactJson(result), null);
    }

    private static string FormatContent(string op, string language, JsonElement rawResult, string? renameEdits, string? formatEdits)
    {
        var summary = op switch
        {
            "hover" => FormatHover(rawResult),
            "definition" => FormatLocations(rawResult, "definition"),
            "references" => FormatLocations(rawResult, "reference"),
            "rename" => $"rename returned a WorkspaceEdit with {CountChanges(rawResult)} change(s); review and apply via edit/write.\n\n{renameEdits}",
            "diagnostics" => FormatDiagnostics(rawResult),
            "symbols" => FormatSymbols(rawResult),
            "code_actions" => FormatCodeActions(rawResult),
            "format" => $"format returned {CountEdits(rawResult)} edit(s); review and apply via edit/write.\n\n{formatEdits}",
            "request" => rawResult.ValueKind == JsonValueKind.Undefined ? "(no result)" : rawResult.GetRawText(),
            _ => rawResult.ValueKind == JsonValueKind.Undefined ? "(no result)" : rawResult.GetRawText(),
        };

        return $"lsp {op} ({language}): {summary}";
    }

    private static string FormatHover(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return "(no hover)";
        if (result.TryGetProperty("contents", out var contents))
        {
            var parts = new List<string>();
            if (contents.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contents.EnumerateArray()) parts.Add(ReadMarkup(item));
            }
            else
            {
                parts.Add(ReadMarkup(contents));
            }

            return parts.Count == 0 ? "(no hover)" : string.Join("\n\n", parts);
        }

        return result.GetRawText();
    }

    private static string ReadMarkup(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("value", out var text)) return text.GetString() ?? string.Empty;
            if (value.TryGetProperty("kind", out var kind)) return $"[{kind.GetString()}] " + (value.TryGetProperty("value", out var raw) ? raw.GetString() ?? string.Empty : string.Empty);
        }

        return value.GetRawText();
    }

    private static string FormatLocations(JsonElement result, string kind)
    {
        if (result.ValueKind != JsonValueKind.Array) return "(none)";
        if (result.GetArrayLength() == 0) return $"no {kind}s";
        var locations = result.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("uri", out var uri) ? uri.GetString() ?? "?" : "?")
            .Distinct()
            .Take(10)
            .ToList();
        return $"{result.GetArrayLength()} {kind}{(result.GetArrayLength() == 1 ? "" : "s")} (files: {string.Join(", ", locations)})";
    }

    private static string FormatDiagnostics(JsonElement result)
    {
        var items = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var itemsElement)
            ? itemsElement
            : default;
        if (items.ValueKind != JsonValueKind.Array) return "(no diagnostics)";
        return items.GetArrayLength() == 0
            ? "no diagnostics"
            : $"{items.GetArrayLength()} diagnostic(s) (see details.diagnostics for the raw report)";
    }

    private static string FormatSymbols(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return "(no symbols)";
        var names = result.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => name is not null)
            .Take(20)
            .ToList();
        return names.Count == 0 ? "(no symbols)" : $"{result.GetArrayLength()} symbol(s): {string.Join(", ", names)}";
    }

    private static string FormatCodeActions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return "(no code actions)";
        var titles = result.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("title", out _))
            .Select(item => item.GetProperty("title").GetString())
            .Where(title => title is not null)
            .Take(10)
            .ToList();
        return titles.Count == 0 ? "(no code actions)" : string.Join("; ", titles);
    }

    private static int CountChanges(JsonElement workspaceEdit)
    {
        if (workspaceEdit.ValueKind != JsonValueKind.Object || !workspaceEdit.TryGetProperty("changes", out var changes)) return 0;
        var count = 0;
        foreach (var property in changes.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array) count += property.Value.GetArrayLength();
        }

        return count;
    }

    private static int CountEdits(JsonElement result)
        => result.ValueKind == JsonValueKind.Array ? result.GetArrayLength() : 0;

    private static string? CompactJson(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? null : element.GetRawText();

    private string ResolvePath(string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_cwd, path));

    private static string RequireUri(string? uri, LspToolInput input)
        => uri ?? throw new InvalidOperationException($"lsp {input.Op} requires a file path.");

    private static int RequireLine(LspToolInput input)
        => input.Line ?? throw new InvalidOperationException($"lsp {input.Op} requires a 0-based 'line'.");

    private static int RequireCharacter(LspToolInput input)
        => input.Character ?? throw new InvalidOperationException($"lsp {input.Op} requires a 0-based 'character'.");

    private static string RequireNewName(LspToolInput input)
        => input.NewName ?? throw new InvalidOperationException("lsp rename requires 'newName'.");

    private static string RequireMethod(LspToolInput input)
        => input.Method ?? throw new InvalidOperationException("lsp request requires 'method'.");

    private static LspRange RequireRange(LspToolInput input)
        => input.Range ?? throw new InvalidOperationException("lsp code_actions requires a 'range'.");

    private static AgentToolResult<object?> ErrorResult(string message)
        => new([new TextContent(message)], null);
}
