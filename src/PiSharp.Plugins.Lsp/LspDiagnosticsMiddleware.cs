using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

namespace PiSharp.Plugins.Lsp;

/// <summary>
/// Post-write diagnostics hook: registered via <c>IExtensionApi.Use(...)</c>. After an
/// <c>edit</c>/<c>write</c> tool call, when the gate is on and a server is already running
/// for the edited file's language, pulls diagnostics (bounded by the configured timeout) and
/// appends a compact summary to the tool result content. Fast no-op paths: gate off, other
/// tools, unknown language, no running server, pull timeout.
/// </summary>
public sealed class LspDiagnosticsMiddleware
{
    private readonly LspServerRegistry _registry;
    private readonly LspDiagnosticsService _diagnostics;
    private readonly ILogger _logger;

    public LspDiagnosticsMiddleware(
        LspServerRegistry registry,
        LspDiagnosticsService diagnostics,
        ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _diagnostics = diagnostics;
        _logger = loggerFactory?.CreateLogger<LspDiagnosticsMiddleware>() ?? NullLogger<LspDiagnosticsMiddleware>.Instance;
    }

    /// <summary>Middleware delegate for <c>IExtensionApi.Use</c>. Never throws; failures degrade to a no-op.</summary>
    public async Task HandleAsync(
        ExtensionMiddlewareContext context,
        ExtensionNext next,
        CancellationToken cancellationToken)
    {
        if (context.AfterToolCall is not { } after)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var diagnosticsNote = await TryPullDiagnosticsNoteAsync(after.ToolCall, after.Args, cancellationToken).ConfigureAwait(false);
        await next(context, cancellationToken).ConfigureAwait(false);

        if (diagnosticsNote is not null)
        {
            var existing = after.Result.Content ?? [];
            var amended = existing.Append(new TextContent(diagnosticsNote)).ToArray();
            context.ModifyToolResult(content: amended, isError: null);
        }
    }

    private async Task<string?> TryPullDiagnosticsNoteAsync(
        PiSharp.Abstractions.Messages.ToolCallContent toolCall,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (toolCall.Name is not ("edit" or "write"))
        {
            return null;
        }

        if (!args.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return null;
        }

        var language = _registry.ResolveLanguage(pathElement.GetString());
        if (language is null)
        {
            return null;
        }

        if (!_registry.TryGetClient(language, out _))
        {
            _logger.LogDebug("No running server for '{Language}'; skipping post-write diagnostics.", language);
            return null;
        }

        var absolutePath = Path.GetFullPath(pathElement.GetString()!);
        var summary = await _diagnostics.PullAsync(absolutePath, language, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        return summary is null
            ? null
            : $"\n\nDiagnostics ({language}): {LspDiagnosticsService.FormatSummary(summary)}";
    }
}
