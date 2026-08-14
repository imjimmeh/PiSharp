using System.ComponentModel;
using System.Text.Json;

namespace PiSharp.Plugins.Debug;

public sealed record DebugToolInput(
    [property: Description("Operation: attach|continue|pause|step|threads|stackTrace|scopes|variables|evaluate|setBreakpoints|disconnect|list")] string Op,
    [property: Description("Language id selecting the adapter (attach only)")] string? Language,
    [property: Description("Debug session id returned by attach (required for all ops except attach/list)")] string? SessionId,
    [property: Description("Source file path (setBreakpoints) or debuggee program (attach)")] string? Path,
    [property: Description("Breakpoint lines (1-based) for setBreakpoints")] IReadOnlyList<int>? Lines,
    [property: Description("Thread id (continue/pause/step/stackTrace)")] int? ThreadId,
    [property: Description("Frame id (scopes/evaluate)")] int? FrameId,
    [property: Description("Variables reference (variables)")] int? VariablesReference,
    [property: Description("Expression to evaluate")] string? Expression,
    [property: Description("Evaluate context: watch|repl|hover|clipboard")] string? Context,
    [property: Description("Step direction for op=step: in|out|next")] string? StepType,
    [property: Description("Extra DAP request body merged over the adapter's attach config")] JsonElement? Request,
    [property: Description("Variables paging: start index (0-based)")] int? Start,
    [property: Description("Variables paging: count")] int? Count);

public sealed record DebugSessionInfo(
    string SessionId,
    string Language,
    string State,
    DateTimeOffset CreatedAt,
    string? DebuggeeLabel,
    string? LastError);

public sealed record DebugToolDetails(
    string Op,
    string? SessionId,
    string? Language,
    DebugSessionInfo? Session,
    JsonElement? RawResult,
    string? Summary,
    string? Error);
