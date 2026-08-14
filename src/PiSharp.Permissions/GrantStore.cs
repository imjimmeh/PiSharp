using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// <summary>
/// Session-persisted grants over <see cref="IExtensionStateApi"/> (P02 State, daemon-resident).
/// Keys follow <c>grant.&lt;action&gt;.&lt;tool&gt;.&lt;session&gt;</c> (plan §3.5 adapted to the
/// <c>[A-Za-z0-9_.-]</c> state-key charset, P29 §10 note); values carry the optional rule
/// pattern and the expiry. Expired grants are pruned lazily on read.
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

    /// <summary>Builds the storage key for a grant.</summary>
    public static string KeyFor(PermissionAction action, string tool, string sessionKey)
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

    private static bool TryParseKey(string key, out PermissionAction action, out string tool, out string session)
    {
        action = PermissionAction.Allow;
        tool = string.Empty;
        session = string.Empty;
        var parts = key.Split('.');
        if (parts.Length != 4 || parts[0] != "grant") return false;
        if (!Enum.TryParse<PermissionAction>(parts[1], true, out var parsed) || parsed == PermissionAction.Ask) return false;
        action = parsed;
        tool = parts[2];
        session = parts[3];
        return true;
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
