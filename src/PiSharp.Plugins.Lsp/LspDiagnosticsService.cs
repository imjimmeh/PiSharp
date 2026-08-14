using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Plugins.ProtocolJsonRpc.JsonRpc;

namespace PiSharp.Plugins.Lsp;

/// <summary>
/// Pulls diagnostics for one file: primary path is the LSP 3.17 <c>textDocument/diagnostic</c>
/// pull request; when the server answers <c>-32601</c> (MethodNotFound) it falls back to a
/// full-sync <c>didChange</c> followed by capturing pushed
/// <c>textDocument/publishDiagnostics</c> notifications within the timeout.
/// </summary>
public sealed class LspDiagnosticsService
{
    private readonly LspServerRegistry _registry;
    private readonly ILogger _logger;

    public LspDiagnosticsService(LspServerRegistry registry, ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _logger = loggerFactory?.CreateLogger<LspDiagnosticsService>() ?? NullLogger<LspDiagnosticsService>.Instance;
    }

    /// <summary>
    /// Returns the diagnostic summary for <paramref name="absolutePath"/>, or null when no
    /// server is running, the pull timed out, or the server is unreachable.
    /// </summary>
    public async Task<LspDiagnosticSummary?> PullAsync(
        string absolutePath,
        string language,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!_registry.TryGetClient(language, out var client) || client is null)
        {
            return null;
        }

        var uri = LspServerRegistry.PathToUri(absolutePath).AbsoluteUri;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var result = await client.DiagnosticsAsync(uri, timeoutCts.Token).ConfigureAwait(false);
            var diagnostics = ExtractPullDiagnostics(result);
            return new LspDiagnosticSummary(absolutePath, language, diagnostics);
        }
        catch (JsonRpcRemoteException exception) when (exception.Code == -32601)
        {
            _logger.LogDebug("Server for '{Language}' does not support textDocument/diagnostic; falling back to publishDiagnostics capture.", language);
            return await CapturePushedDiagnosticsAsync(client, uri, absolutePath, language, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Diagnostics pull for '{Path}' timed out after {TimeoutMs}ms.", absolutePath, timeout.TotalMilliseconds);
            return null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "Diagnostics pull for '{Path}' failed.", absolutePath);
            return null;
        }
    }

    private async Task<LspDiagnosticSummary?> CapturePushedDiagnosticsAsync(
        LspClient client,
        string uri,
        string absolutePath,
        string language,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
        await client.DidChangeAsync(uri, text, ct).ConfigureAwait(false);

        var captured = new List<LspDiagnostic>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublishDiagnostics(JsonElement payload)
        {
            if (!payload.TryGetProperty("uri", out var payloadUri) || payloadUri.GetString() != uri) return;
            captured.AddRange(ExtractPublishedDiagnostics(payload));
            completion.TrySetResult(true);
        }

        client.PublishDiagnostics += OnPublishDiagnostics;
        try
        {
            await Task.WhenAny(completion.Task, Task.Delay(timeout, timeoutCts.Token)).ConfigureAwait(false);
        }
        finally
        {
            client.PublishDiagnostics -= OnPublishDiagnostics;
        }

        return new LspDiagnosticSummary(absolutePath, language, captured);
    }

    private static IReadOnlyList<LspDiagnostic> ExtractPullDiagnostics(JsonElement result)
    {
        var diagnostics = new List<LspDiagnostic>();

        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("kind", out var kind))
        {
            // DocumentDiagnosticReport: { kind: "full", items: [...] } | { kind: "unchanged", resultId } | { relatedDocuments: {...} }
            if (kind.GetString() == "full" && result.TryGetProperty("items", out var items))
            {
                diagnostics.AddRange(ReadDiagnostics(items));
            }
            else if (result.TryGetProperty("relatedDocuments", out var related) && related.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in related.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("kind", out var relatedKind)
                        && relatedKind.GetString() == "full"
                        && property.Value.TryGetProperty("items", out var relatedItems))
                    {
                        diagnostics.AddRange(ReadDiagnostics(relatedItems));
                    }
                }
            }
        }

        return diagnostics;
    }

    private static IReadOnlyList<LspDiagnostic> ExtractPublishedDiagnostics(JsonElement payload)
    {
        if (!payload.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return ReadDiagnostics(diagnostics);
    }

    private static IReadOnlyList<LspDiagnostic> ReadDiagnostics(JsonElement items)
    {
        var diagnostics = new List<LspDiagnostic>();
        if (items.ValueKind != JsonValueKind.Array) return diagnostics;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("range", out var range))
            {
                continue;
            }

            diagnostics.Add(new LspDiagnostic(
                Range: ReadRange(range),
                Severity: ReadSeverity(item),
                Code: ReadCode(item),
                Source: item.TryGetProperty("source", out var source) ? source.GetString() : null,
                Message: item.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty));
        }

        return diagnostics;
    }

    private static LspRange ReadRange(JsonElement range)
    {
        LspPosition ReadPosition(string name)
            => range.TryGetProperty(name, out var position)
                ? new LspPosition(
                    position.TryGetProperty("line", out var line) && line.TryGetInt32(out var lineValue) ? lineValue : 0,
                    position.TryGetProperty("character", out var character) && character.TryGetInt32(out var characterValue) ? characterValue : 0)
                : new LspPosition(0, 0);

        return new LspRange(ReadPosition("start"), ReadPosition("end"));
    }

    private static string? ReadSeverity(JsonElement item)
    {
        if (!item.TryGetProperty("severity", out var severity) || !severity.TryGetInt32(out var value))
        {
            return null;
        }

        return value switch
        {
            1 => "Error",
            2 => "Warning",
            3 => "Information",
            4 => "Hint",
            _ => null,
        };
    }

    private static string? ReadCode(JsonElement item)
    {
        if (!item.TryGetProperty("code", out var code)) return null;
        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Compact human summary: "2 errors, 1 warning: line 12: 'x' is not defined; …"</summary>
    public static string FormatSummary(LspDiagnosticSummary summary)
    {
        if (summary.Diagnostics.Count == 0) return "no diagnostics";

        var counts = summary.Diagnostics
            .GroupBy(diagnostic => diagnostic.Severity ?? "Error")
            .Select(group => $"{group.Count()} {group.Key.ToLowerInvariant()}{(group.Count() == 1 ? "" : "s")}")
            .ToList();

        var details = summary.Diagnostics
            .Take(5)
            .Select(diagnostic => $"line {diagnostic.Range.Start.Line + 1}: {diagnostic.Message}")
            .ToList();

        var head = string.Join(", ", counts);
        return details.Count == 0 ? head : $"{head}: {string.Join("; ", details)}";
    }
}
