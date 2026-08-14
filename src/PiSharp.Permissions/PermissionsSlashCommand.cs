using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

namespace PiSharp.Permissions;

/// <summary>
/// The <c>/permissions</c> slash command (P29 §4.2): <c>list</c> (rules + defaults + grants),
/// <c>grant &lt;tool&gt; [pattern]</c>, <c>revoke &lt;tool&gt;</c>, <c>reset</c> (clear grants,
/// restore defaults), and <c>status</c> (mode, headless posture).
/// </summary>
public sealed class PermissionsSlashCommand
{
    private readonly IExtensionApi _api;
    private readonly Func<PermissionsPolicy> _policy;
    private readonly GrantStore _grants;
    private readonly Func<CancellationToken, Task<string>> _sessionKeyResolver;

    public PermissionsSlashCommand(
        IExtensionApi api,
        Func<PermissionsPolicy> policy,
        GrantStore grants,
        Func<CancellationToken, Task<string>> sessionKeyResolver)
    {
        _api = api;
        _policy = policy;
        _grants = grants;
        _sessionKeyResolver = sessionKeyResolver;
    }

    public async Task InvokeAsync(string args, CancellationToken cancellationToken = default)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length == 0 ? "list" : parts[0].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "list":
                    await ListAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "grant":
                    await GrantAsync(parts, cancellationToken).ConfigureAwait(false);
                    break;
                case "revoke":
                    await RevokeAsync(parts, cancellationToken).ConfigureAwait(false);
                    break;
                case "reset":
                    await _grants.ClearAsync(cancellationToken).ConfigureAwait(false);
                    await ReplyAsync($"[permissions] All session grants cleared; defaults restored.").ConfigureAwait(false);
                    break;
                case "status":
                    await StatusAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ReplyAsync("Usage: /permissions [list|grant <tool> [pattern]|revoke <tool>|reset|status]").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await ReplyAsync($"[permissions] {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task ListAsync(CancellationToken cancellationToken)
    {
        var policy = _policy();
        var grants = await _grants.ListAsync(null, cancellationToken).ConfigureAwait(false);
        var lines = new List<string>
        {
            $"[permissions] mode={policy.Mode} headlessDeny={policy.HeadlessDeny} grantTtlSeconds={policy.GrantTtlSeconds} audit={policy.Audit}",
            $"allow: {FormatRules(policy.AllowRules)}",
            $"deny: {FormatRules(policy.DenyRules)}",
            $"ask: {FormatRules(policy.AskRules)}",
            $"grants: {(grants.Count == 0 ? "none" : string.Join("; ", grants.Select(FormatGrant)))}"
        };
        await ReplyAsync(string.Join("\n", lines)).ConfigureAwait(false);
    }

    private async Task GrantAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
        {
            await ReplyAsync("Usage: /permissions grant <tool> [pattern]").ConfigureAwait(false);
            return;
        }

        var tool = parts[1];
        var pattern = parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : null;
        var sessionKey = await _sessionKeyResolver(cancellationToken).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(_policy().GrantTtlSeconds);
        await _grants.StoreAsync(new PermissionGrant(sessionKey, tool, pattern, PermissionAction.Allow, expiresAt), cancellationToken).ConfigureAwait(false);
        var patternText = pattern is null ? string.Empty : $" matching pattern '{pattern}'";
        await ReplyAsync($"[permissions] Allowed '{tool}'{patternText} for session '{sessionKey}' until {expiresAt:u}.").ConfigureAwait(false);
    }

    private async Task RevokeAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
        {
            await ReplyAsync("Usage: /permissions revoke <tool>").ConfigureAwait(false);
            return;
        }

        var tool = parts[1];
        var sessionKey = await _sessionKeyResolver(cancellationToken).ConfigureAwait(false);
        await _grants.RevokeAsync(tool, sessionKey, cancellationToken).ConfigureAwait(false);
        await ReplyAsync($"[permissions] Revoked grants for '{tool}' in session '{sessionKey}'.").ConfigureAwait(false);
    }

    private async Task StatusAsync(CancellationToken cancellationToken)
    {
        var policy = _policy();
        var posture = !_api.HasUi
            ? (policy.HeadlessDeny ? "headless: ask auto-denies" : "headless: ask auto-allows")
            : "interactive: ask prompts";
        await ReplyAsync($"[permissions] mode={policy.Mode}; {posture}; grantTtlSeconds={policy.GrantTtlSeconds}").ConfigureAwait(false);
    }

    private static string FormatRules(IReadOnlyList<PermissionRule> rules)
        => rules.Count == 0
            ? "(none)"
            : string.Join(", ", rules.Select(rule => rule.Pattern is null ? rule.Tool : $"{rule.Tool}~/{rule.Pattern}/"));

    private static string FormatGrant(PermissionGrant grant)
        => $"{grant.Action.ToString().ToLowerInvariant()}:{grant.Tool}@{grant.SessionKey} until {grant.ExpiresAt:u}";

    private Task ReplyAsync(string text)
        => _api.SendMessageAsync(AgentMessages.User(text));
}
