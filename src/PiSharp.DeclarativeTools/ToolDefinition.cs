using System.Text.Json;
using PiSharp.Agent.Core;
using PiSharp.Extensions;

namespace PiSharp.DeclarativeTools;

/// <summary>
/// A parsed, validated tool ready to become an <see cref="ExtensionToolRegistration"/>.
/// Produced by <see cref="ToolFileParser"/>; declarative-only files (no script) have a
/// null <see cref="ScriptPath"/>.
/// </summary>
public sealed record ToolDefinition(
    string Name,
    string Label,
    string Description,
    JsonElement ParametersSchema,
    string? PromptSnippet,
    IReadOnlyList<string> PromptGuidelines,
    ToolExecutionMode? ExecutionMode,
    string? RendererName,
    ExtensionOverridePolicy Override,
    string? ScriptPath,
    TimeSpan? Timeout,
    bool AllowNonZeroExit,
    string SourcePath)
{
    public bool IsScript => ScriptPath is not null;

    /// <summary>
    /// Structural equality across the fields that affect registration, used by the
    /// settings-driven diff to avoid re-registering unchanged tools.
    /// </summary>
    public bool SameRegistration(ToolDefinition other)
        => string.Equals(Name, other.Name, StringComparison.Ordinal)
           && string.Equals(Label, other.Label, StringComparison.Ordinal)
           && string.Equals(Description, other.Description, StringComparison.Ordinal)
           && string.Equals(ParametersSchema.GetRawText(), other.ParametersSchema.GetRawText(), StringComparison.Ordinal)
           && string.Equals(PromptSnippet, other.PromptSnippet, StringComparison.Ordinal)
           && ExecutionMode == other.ExecutionMode
           && string.Equals(RendererName, other.RendererName, StringComparison.Ordinal)
           && Override == other.Override
           && string.Equals(ScriptPath, other.ScriptPath, StringComparison.Ordinal)
           && Timeout == other.Timeout
           && AllowNonZeroExit == other.AllowNonZeroExit
           && PromptGuidelines.SequenceEqual(other.PromptGuidelines, StringComparer.Ordinal);
}
