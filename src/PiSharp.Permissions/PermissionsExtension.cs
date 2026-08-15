using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-permissions",
    Name = "PiSharp Permissions",
    Version = "0.1.0",
    Description = "Approval-based permission gate: allow/deny/ask tool-call policy over the ui_request lane, session grants in P02 state, /permissions slash command.",
    SourceId = "pi:extension:pisharp-permissions")]

namespace PiSharp.Permissions;

/// <summary>
/// <c>pisharp-permissions</c> extension entry (P29). Reads the
/// <c>extensions.pisharp-permissions.*</c> settings, registers the tool-call gate middleware,
/// the <c>/permissions</c> command, and a P02 <c>settings_changed</c> subscription for live
/// policy reload. Grants are session-persisted via <see cref="GrantStore"/> and survive
/// client attach/detach by construction (daemon-resident state).
/// </summary>
public sealed class PermissionsExtension : IExtension, IAsyncDisposable
{
    internal const string SettingsNamespace = "pisharp-permissions";

    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private IExtensionApi? _api;
    private PermissionsPolicy _policy = PermissionsPolicy.Default;
    private GrantStore? _grants;
    private bool _disposed;

    /// <summary>The currently loaded policy (live-reloaded on settings changes).</summary>
    internal PermissionsPolicy Policy
    {
        get { lock (_gate) return _policy; }
    }

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _policy = PermissionsPolicy.Load(api);
        _grants = new GrantStore(api.State);

        var approvals = new ApprovalClient(api);
        var audit = new AuditRecorder(api);
        var middleware = new PermissionsMiddleware(api, () => Policy, _grants, approvals, audit);
        var command = new PermissionsSlashCommand(
            api,
            () => Policy,
            _grants,
            ct => SessionKeys.ResolveAsync(api, ct));

        _subscriptions.Add(api.Use(middleware.InvokeAsync));
        _subscriptions.Add(api.RegisterCommand(new ExtensionCommandRegistration(
            "permissions",
            "Permissions gate: /permissions [list|grant <tool> [pattern]|revoke <tool>|reset|status].",
            command.InvokeAsync)));

        // Live policy reload on any committed change under the extension namespace (P02).
        _subscriptions.Add(api.Settings.OnChange(change =>
        {
            if (change.Key.StartsWith($"extensions.{SettingsNamespace}.", StringComparison.Ordinal))
                ReloadPolicy();
        }));

        // F5/F6: install the spawn capability gates so extension shell exec and stdio MCP server
        // spawns fail closed under the live policy. Strict mode denies un-allow-listed spawns;
        // automatic mode resolves Ask→Allow (historic default unchanged); prompt mode in a
        // headless session denies (no interactive approval UI on the spawn path).
        CapabilityGates.ShellExec = request =>
        {
            var policy = Policy;
            var headless = api is { HasUi: false };
            var decision = policy.Evaluate(
                "bash",
                System.Text.Json.JsonSerializer.Serialize(new { request.Command, request.Args }),
                DangerousOpDetector.BashCategoryOf(request.Command),
                headless);
            return decision.Action == PermissionAction.Allow ? null : $"extension shell '{request.Command}' blocked: {decision.Reason}";
        };
        CapabilityGates.McpSpawn = request =>
        {
            var policy = Policy;
            // Only strict mode gates MCP spawns (keep prompt/automatic UX unchanged).
            if (policy.Mode != "strict") return null;
            var headless = api is { HasUi: false };
            var decision = policy.Evaluate(
                "mcp.spawn",
                System.Text.Json.JsonSerializer.Serialize(new { request.Command, request.Args, request.SourceId }),
                DangerousOpDetector.Unknown,
                headless);
            return decision.Action == PermissionAction.Allow ? null : decision.Reason;
        };

        return Task.CompletedTask;
    }

    private void ReloadPolicy()
    {
        var api = _api;
        if (api is null) return;
        try
        {
            lock (_gate) _policy = PermissionsPolicy.Load(api);
        }
        catch (Exception)
        {
            // Malformed settings must not take the session down: keep the last good policy.
        }
    }

    public ValueTask DisposeAsync()
    {
        List<IDisposable> subscriptions;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }
        // Uninstall the spawn gate when the permission extension goes away so a defunct
        // extension can no longer keep gating (or keep denying) other code paths.
        CapabilityGates.ShellExec = null;
        CapabilityGates.McpSpawn = null;
        foreach (var subscription in subscriptions) subscription.Dispose();
        return ValueTask.CompletedTask;
    }
}
