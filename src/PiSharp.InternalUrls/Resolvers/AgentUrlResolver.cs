using System.Text.Json;
using PiSharp.Extensions;

namespace PiSharp.InternalUrls.Resolvers;

/// <summary>
/// Resolves <c>agent://&lt;id&gt;</c> to a subagent's structured result (JSON)
/// and <c>agent://&lt;id&gt;/&lt;field.path&gt;</c> to a single dotted/indexed
/// field of it. The result is retrieved through an injected accessor bound to
/// the P06 subagent-handle store.
/// </summary>
public sealed class AgentUrlResolver(Func<string, JsonElement?> resultLookup) : IInternalUrlResolver
{
    private readonly Func<string, JsonElement?> _resultLookup = resultLookup
        ?? throw new ArgumentNullException(nameof(resultLookup));

    public string Scheme => "agent";

    public ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
    {
        // Target shape: "<id>" or "<id>/<field.path>". The id must be a plain
        // name (no separators, no ".."); the traversal guard already rejects
        // hard separators, so validate defensively.
        if (!InternalUrlSecurity.TryParseTarget(request.Target, out var segments))
            return ValueTask.FromResult(Blocked(request.Target));

        var id = segments[0];
        if (!InternalUrlSecurity.IsPlainName(id))
            return ValueTask.FromResult(Blocked(request.Target));

        var result = _resultLookup(id);
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ValueTask.FromResult(NotFound($"No subagent result for id '{id}'."));

        if (segments.Count == 1)
            return ValueTask.FromResult(Resolved(result.Value.GetRawText()));

        var fieldPath = string.Join('.', segments.Skip(1));
        if (!InternalUrlFieldPath.TrySelect(result.Value, fieldPath, out var selected))
            return ValueTask.FromResult(NotFound($"Field '{fieldPath}' not found on agent '{id}'."));

        return ValueTask.FromResult(Resolved(selected.GetRawText()));
    }

    private static InternalUrlResult Resolved(string content) => new(true, content);

    private static InternalUrlResult Blocked(string target)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.TraversalBlocked, $"Traversal blocked in agent:// target '{target}'."));

    private static InternalUrlResult NotFound(string reason)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.NotFound, reason));
}
