using System.ComponentModel;
using System.Text.Json;

namespace PiSharp.Plugins.Lsp;

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record LspToolInput(
    [property: Description("Operation: hover|definition|references|rename|diagnostics|symbols|code_actions|format|request")] string Op,
    [property: Description("File path (relative or absolute) whose language selects the server. Omit for workspace symbol search.")] string? Path,
    [property: Description("0-based line for position-based ops")] int? Line,
    [property: Description("0-based character for position-based ops")] int? Character,
    [property: Description("New name for rename")] string? NewName,
    [property: Description("Query string for workspace symbol search (op=symbols without path)")] string? Query,
    [property: Description("Range for code_actions or range formatting")] LspRange? Range,
    [property: Description("Raw LSP method for op=request (e.g. \"textDocument/documentSymbol\")")] string? Method,
    [property: Description("Raw params object for op=request")] JsonElement? Params,
    [property: Description("Language id override (e.g. \"csharp\") to bypass path-based resolution")] string? Language);

public sealed record LspDiagnostic(
    LspRange Range,
    string? Severity,
    string? Code,
    string? Source,
    string Message);

public sealed record LspDiagnosticSummary(
    string? Path,
    string Language,
    IReadOnlyList<LspDiagnostic> Diagnostics);

public sealed record LspToolDetails(
    string Op,
    string? Language,
    string? ServerCommand,
    long ElapsedMs,
    LspDiagnosticSummary? Diagnostics,
    string? RenameEdits,
    string? FormatEdits,
    JsonElement? RawResult,
    string? Error);
