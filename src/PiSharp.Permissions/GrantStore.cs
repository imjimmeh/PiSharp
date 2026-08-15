using System.Text;
using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// Session-persisted grants over <see cref="IExtensionStateApi"/> (P02 State, daemon-resident).
/// Keys are versioned (<c>grant.&lt;action&gt;.v2.&lt;tool&gt;.&lt;session&gt;</c> with each component
/// encoded as unpadded base64url, so dotted tool names like <c>mcp.foo.read</c> are data, not
/// delimiters) while remaining within the <c>[A-Za-z0-9_.-]</c> state-key charset (P29 §10 note).
/// Legacy <c>grant.&lt;action&gt;.&lt;tool&gt;.&lt;session&gt;</c> keys stay readable; values carry the
/// optional rule pattern and the expiry. Expired grants are pruned lazily on read.
/// </summary>
public sealed class GrantStore
{
    private const string GrantPrefix = "grant.";

    private readonly IExtensionStateApi _state;
    private readonly TimeProvider _time;

    public GrantStore(IExtensionStateApi state, TimeProvider? time = null)
    {
        _state = state;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Builds the storage key for a grant (v2 base64url-encoded components).</summary>
    public static string KeyFor(PermissionAction action, string tool, string sessionKey)
        => $"{GrantPrefix}{action.ToString().ToLowerInvariant()}.v2.{EncodeComponent(Sanitize(tool))}.{EncodeComponent(Sanitize(sessionKey))}";

    /// <summary>Builds the legacy pre-v2 storage key shape for a grant.</summary>
    private static string LegacyKeyFor(PermissionAction action, string tool, string sessionKey)
        => $"{GrantPrefix}{action.ToString().ToLowerInvariant()}.{Sanitize(tool)}.{Sanitize(sessionKey)}";

    /// <summary>
    /// Returns the live grant for (tool, session) that applies to <paramref name="serializedArgs"/>
    /// (pattern grants match against the JSON-serialized args; a null pattern matches everything).
    /// Allow is checked before Deny. Expired grants are pruned while looking.
    /// </summary>
    public async Task<PermissionGrant?> FindAsync(
        string tool,
        string sessionKey,
        string? serializedArgs = null,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        foreach (var action in new[] { PermissionAction.Allow, PermissionAction.Deny })
        {
            var key = KeyFor(action, tool, sessionKey);
            var stored = await _state.GetAsync<StoredGrant>(key, ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
            stored ??= await _state.GetAsync<StoredGrant>(LegacyKeyFor(action, tool, sessionKey), ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
            if (stored is null) continue;
            if (stored.ExpiresAt <= now)
            {
                await _state.RemoveAsync(key, ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (GrantMatches(stored, serializedArgs))
            {
                return new PermissionGrant(sessionKey, tool, stored.Pattern, action, stored.ExpiresAt);
            }
        }
        return null;
    }

    public Task StoreAsync(PermissionGrant grant, CancellationToken cancellationToken = default)
        => _state.SetAsync(
            KeyFor(grant.Action, grant.Tool, grant.SessionKey),
            new StoredGrant(grant.Pattern, grant.ExpiresAt),
            ExtensionStateScope.User,
            cancellationToken);

    /// <summary>Removes both the allow and deny grants for (tool, session).</summary>
    public async Task RevokeAsync(string tool, string sessionKey, CancellationToken cancellationToken = default)
    {
        await _state.RemoveAsync(KeyFor(PermissionAction.Allow, tool, sessionKey), ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
        await _state.RemoveAsync(KeyFor(PermissionAction.Deny, tool, sessionKey), ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
        await _state.RemoveAsync(LegacyKeyFor(PermissionAction.Allow, tool, sessionKey), ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
        await _state.RemoveAsync(LegacyKeyFor(PermissionAction.Deny, tool, sessionKey), ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists grants, optionally narrowed to one session key. Expired grants are pruned while
    /// enumerating.
    /// </summary>
    public async Task<IReadOnlyList<PermissionGrant>> ListAsync(string? sessionKey = null, CancellationToken cancellationToken = default)
    {
        var all = await _state.GetAllAsync(ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var results = new List<PermissionGrant>();
        foreach (var pair in all)
        {
            if (!pair.Key.StartsWith(GrantPrefix, StringComparison.Ordinal)) continue;
            if (!TryParseKey(pair.Key, out var action, out var tool, out var session)) continue;
            if (sessionKey is not null && !string.Equals(session, Sanitize(sessionKey), StringComparison.Ordinal)) continue;
            var stored = ReadStoredGrant(pair.Value);
            if (stored is null) continue;
            if (stored.ExpiresAt <= now)
            {
                await _state.RemoveAsync(pair.Key, ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
                continue;
            }
            results.Add(new PermissionGrant(session, tool, stored.Pattern, action, stored.ExpiresAt));
        }
        return results;
    }

    /// <summary>Removes every grant key (the <c>/permissions reset</c> path).</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var keys = await _state.ListKeysAsync(ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            if (key.StartsWith(GrantPrefix, StringComparison.Ordinal))
                await _state.RemoveAsync(key, ExtensionStateScope.User, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Sanitize(string value)
    {
        var sanitized = string.Concat((value ?? string.Empty).ToLowerInvariant().Select(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
        return sanitized.Length == 0 ? "default" : sanitized;
    }

    /// <summary>True when a stored grant applies: no pattern, or the pattern matches the serialized args.</summary>
    private static bool GrantMatches(StoredGrant stored, string? serializedArgs)
    {
        if (string.IsNullOrWhiteSpace(stored.Pattern)) return true;
        if (serializedArgs is null) return false;
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                serializedArgs,
                stored.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false; // malformed stored pattern: treat as non-matching (safe)
        }
    }

    /// <summary>
    /// Parses a stored grant key in either shape: the legacy 4-part form
    /// (<c>grant.&lt;action&gt;.&lt;tool&gt;.&lt;session&gt;</c>) or the v2 5-part form
    /// (<c>grant.&lt;action&gt;.v2.&lt;tool&gt;.&lt;session&gt;</c> with base64url-encoded components).
    /// </summary>
    private static bool TryParseKey(string key, out PermissionAction action, out string tool, out string session)
    {
        action = PermissionAction.Allow;
        tool = string.Empty;
        session = string.Empty;
        var parts = key.Split('.');
        if (parts.Length is not (4 or 5) || parts[0] != "grant") return false;
        if (!Enum.TryParse<PermissionAction>(parts[1], true, out var parsed) || parsed == PermissionAction.Ask) return false;
        action = parsed;
        if (parts.Length == 5)
        {
            if (parts[2] != "v2") return false;
            if (!TryDecodeComponent(parts[3], out tool) || !TryDecodeComponent(parts[4], out session)) return false;
            return true;
        }
        tool = parts[2];
        session = parts[3];
        return true;
    }

    /// <summary>
    /// Encodes a component as unpadded base64url, staying within the <c>[A-Za-z0-9_.-]</c>
    /// state-key charset (standard base64 would introduce <c>+</c>, <c>/</c>, and <c>=</c>).
    /// </summary>
    private static string EncodeComponent(string value)
    {
        var raw = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecodeComponent(string encoded, out string value)
    {
        value = string.Empty;
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static StoredGrant? ReadStoredGrant(object? value)
    {
        if (value is StoredGrant grant) return grant;
        if (value is System.Text.Json.Nodes.JsonNode node)
        {
            return System.Text.Json.JsonSerializer.Deserialize<StoredGrant>(node.ToJsonString());
        }
        return null;
    }

    /// <summary>Serialized grant value: optional rule pattern + expiry.</summary>
    public sealed record StoredGrant(string? Pattern, DateTimeOffset ExpiresAt);
}
