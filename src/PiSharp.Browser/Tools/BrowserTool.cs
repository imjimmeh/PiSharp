using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Browser.Runtime;
using PiSharp.Tools.Shared;

namespace PiSharp.Browser.Tools;

/// <summary>
/// The model-facing <c>browser</c> tool. A single registration with an <c>action</c> discriminator
/// (<c>open</c> / <c>run</c> / <c>screenshot</c> / <c>observe</c>) dispatches to a shared
/// <see cref="BrowserSession"/>.
/// </summary>
public static class BrowserTool
{
    public const string Name = "browser";

    private const string SchemaJson = """
    {
      "type": "object",
      "properties": {
        "action": {
          "type": "string",
          "enum": ["open", "run", "screenshot", "observe"],
          "description": "Action to perform on the shared browser tab."
        },
        "url": {
          "type": "string",
          "description": "Action 'open' only. URL to navigate to (http/https or file://)."
        },
        "waitFor": {
          "type": "string",
          "description": "Action 'open' only. Optional CSS selector to wait for before returning.",
          "default": null
        },
        "timeoutMs": {
          "type": "integer",
          "description": "Navigation / selector wait timeout in milliseconds.",
          "default": 30000
        },
        "script": {
          "type": "string",
          "description": "Action 'run' only. JavaScript expression or statement to evaluate in the page and whose result to return.",
          "default": null
        },
        "returnByValue": {
          "type": "boolean",
          "description": "Action 'run' only. When true, serialize the JS result by value; when false, await a Promise and return its value.",
          "default": true
        },
        "fullPage": {
          "type": "boolean",
          "description": "Action 'screenshot' only. When true, capture the full scrollable page; otherwise the viewport.",
          "default": false
        }
      },
      "required": ["action"],
      "additionalProperties": false
    }
    """;

    private static readonly JsonElement ParametersSchema =
        JsonSerializer.Deserialize<JsonElement>(SchemaJson);

    /// <summary>Builds an independent copy of the tool's JSON parameters schema.</summary>
    public static JsonElement BuildParametersSchema() => ParametersSchema.Clone();

    /// <summary>
    /// Dispatches a tool invocation against the shared <see cref="BrowserSession"/>. Matches the
    /// <see cref="ExtensionToolExecuteAsync"/> shape (plus session/options) so the extension can pass
    /// it straight into <c>ExtensionToolRegistration</c> via a closure.
    /// </summary>
    public static async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken ct,
        AgentToolUpdateCallback<object?>? onUpdate,
        BrowserSession session,
        BrowserToolOptions options)
    {
        try
        {
            var action = GetRequiredString(parameters, "action");
            return action switch
            {
                "open" => await OpenAsync(parameters, session, options, ct).ConfigureAwait(false),
                "run" => await RunAsync(parameters, session, ct).ConfigureAwait(false),
                "screenshot" => await ScreenshotAsync(parameters, session, ct).ConfigureAwait(false),
                "observe" => await ObserveAsync(parameters, session, ct).ConfigureAwait(false),
                _ => Error($"Unknown browser action '{action}'. Expected one of: open, run, screenshot, observe.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static async Task<AgentToolResult<object?>> OpenAsync(
        JsonElement p, BrowserSession session, BrowserToolOptions options, CancellationToken ct)
    {
        var url = GetRequiredString(p, "url");
        var waitFor = GetOptionalString(p, "waitFor");
        var timeoutMs = GetOptionalInt(p, "timeoutMs") ?? options.DefaultNavigationTimeoutMs;

        var (finalUrl, title) = await session.OpenAsync(url, waitFor, timeoutMs, ct).ConfigureAwait(false);
        var text = $"Opened {finalUrl}\nTitle: {title}";
        return new AgentToolResult<object?>([new TextContent(text)], new BrowserToolDetails("open", finalUrl, title));
    }

    private static async Task<AgentToolResult<object?>> RunAsync(
        JsonElement p, BrowserSession session, CancellationToken ct)
    {
        var script = GetRequiredString(p, "script");
        var returnByValue = GetOptionalBool(p, "returnByValue") ?? true;
        var resultJson = await session.RunAsync(script, returnByValue, ct).ConfigureAwait(false);
        return new AgentToolResult<object?>([new TextContent(resultJson)], new BrowserToolDetails("run"));
    }

    private static async Task<AgentToolResult<object?>> ScreenshotAsync(
        JsonElement p, BrowserSession session, CancellationToken ct)
    {
        var fullPage = GetOptionalBool(p, "fullPage") ?? false;
        var bytes = await session.ScreenshotAsync(fullPage, ct).ConfigureAwait(false);
        var processed = await ImageUtilities.ResizeIfNeededAsync(bytes, "image/png", cancellationToken: ct).ConfigureAwait(false);

        var note = "Captured screenshot [image/png]" + (processed.DimensionNote is null ? string.Empty : $"\n{processed.DimensionNote}");
        var details = new BrowserToolDetails(
            "screenshot",
            Url: session.CurrentUrl,
            Title: session.CurrentTitle,
            MimeType: processed.MimeType,
            DimensionNote: processed.DimensionNote);

        return new AgentToolResult<object?>(
            [new TextContent(note), new ImageContent(processed.MimeType, Convert.ToBase64String(processed.Data))],
            details);
    }

    private static async Task<AgentToolResult<object?>> ObserveAsync(
        JsonElement p, BrowserSession session, CancellationToken ct)
    {
        var snapshot = await session.ObserveAsync(ct).ConfigureAwait(false);
        var trunc = Truncation.TruncateHead(snapshot);
        var text = trunc.Truncated
            ? trunc.Content + "\n\n[Accessibility snapshot truncated.]"
            : trunc.Content;
        return new AgentToolResult<object?>([new TextContent(text)], new BrowserToolDetails("observe", Truncated: trunc.Truncated));
    }

    private static AgentToolResult<object?> Error(string message)
        => new([new TextContent($"Error: {message}")], new BrowserToolDetails("error"));

    private static string GetRequiredString(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidOperationException($"Missing required parameter '{name}'.");
        return value.GetString() ?? throw new InvalidOperationException($"Parameter '{name}' must be a string.");
    }

    private static string? GetOptionalString(JsonElement p, string name)
        => p.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.GetString()
            : null;

    private static int? GetOptionalInt(JsonElement p, string name)
        => p.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool? GetOptionalBool(JsonElement p, string name)
        => p.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>Structured details carried on <c>browser</c> tool results.</summary>
public sealed record BrowserToolDetails(
    string Action,
    string? Url = null,
    string? Title = null,
    string? MimeType = null,
    string? DimensionNote = null,
    bool Truncated = false);
