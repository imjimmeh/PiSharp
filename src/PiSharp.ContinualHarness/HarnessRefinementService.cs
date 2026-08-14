using System.Text.Json;
using PiSharp.ContinualHarness.Contracts;
using PiSharp.Extensions;

namespace PiSharp.ContinualHarness;

/// <summary>
/// Single apply path for every entry point (slash command, model tool, rollback). Pipeline:
/// validate -> evidence -> resolve target -> clobber/create-collision check -> write target ->
/// append journal record -> append session audit -> emit event.
/// </summary>
public sealed class HarnessRefinementService
{
    // Event names follow the repo snake_case convention.
    public const string HarnessRefinementApplied = "harness_refinement_applied";
    public const string HarnessRefinementRejected = "harness_refinement_rejected";
    public const string HarnessStateChanged = "harness_state_changed";
    public const string AuditCustomType = "harness.refinement";

    private readonly HarnessStore _local;
    private readonly HarnessStore? _global;
    private readonly Func<HarnessRefinementKind, HarnessRefinementScope, IRefinementTarget> _targetFactory;
    private readonly Func<string, object?, CancellationToken, Task>? _emitEvent;
    private readonly Func<string, object?, CancellationToken, Task>? _appendAudit;
    private readonly IHarnessSettings _settings;
    private readonly IExtensionUi? _ui;
    private readonly Func<DateTimeOffset> _clock;

    public HarnessRefinementService(
        HarnessStore local,
        HarnessStore? global,
        Func<HarnessRefinementKind, HarnessRefinementScope, IRefinementTarget> targetFactory,
        IHarnessSettings settings,
        Func<string, object?, CancellationToken, Task>? emitEvent = null,
        Func<string, object?, CancellationToken, Task>? appendAudit = null,
        IExtensionUi? ui = null,
        Func<DateTimeOffset>? clock = null)
    {
        _local = local;
        _global = global;
        _targetFactory = targetFactory;
        _settings = settings;
        _emitEvent = emitEvent;
        _appendAudit = appendAudit;
        _ui = ui;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public HarnessStore Local => _local;
    public HarnessStore? Global => _global;

    // --- Public apply path -------------------------------------------------

    public async Task<HarnessRefinementRecord> ApplyAsync(
        HarnessRefinementKind kind,
        HarnessRefinementAction action,
        string name,
        JsonElement? content,
        HarnessRefinementScope scope,
        string author,
        IReadOnlyList<RefinementEvidence>? evidence = null,
        bool force = false,
        string? reason = null,
        CancellationToken ct = default)
    {
        return await ApplyCoreAsync(kind, action, name, content, scope, author, evidence, force, reason, targetVersion: null, ct);
    }

    public Task<HarnessRefinementRecord> RollbackAsync(
        HarnessRefinementKind kind,
        string name,
        int? targetVersion = null,
        HarnessRefinementScope scope = HarnessRefinementScope.Local,
        string author = "user",
        bool force = false,
        CancellationToken ct = default)
    {
        var store = StoreFor(scope, throwIfUnavailable: true);
        var key = new HarnessEntryKey(kind, name);
        var existing = store.Get(key);
        var history = store.History(key);

        if (history.Count == 0)
            throw new HarnessRejectedException($"No refinements exist for '{key}'.");

        var effectiveVersion = existing?.Version ?? history[^1].Version;
        var resolvedTarget = targetVersion
            ?? (effectiveVersion - 1);

        if (resolvedTarget < 1)
            throw new HarnessRejectedException($"Cannot roll back '{key}': no version before {effectiveVersion}.");

        var targetRecord = store.At(key, resolvedTarget)
            ?? throw new HarnessRejectedException($"No version {resolvedTarget} of '{key}' exists.");

        // Roll back through the normal write path (restores content; a user action needs no citation).
        return ApplyCoreAsync(
            kind, HarnessRefinementAction.Rollback, name, targetRecord.Content, scope, author,
            evidence: [], force, reason: $"rollback to v{resolvedTarget}",
            targetVersion: resolvedTarget, ct);
    }

    // --- Re-sync (host-edit re-basing) ------------------------------------

    public async Task<HarnessEntry> ReSyncTargetAsync(HarnessEntryKey key, CancellationToken ct = default)
    {
        var store = _local.History(key).Count > 0 ? _local : _global;
        store ??= _local;

        if (store.History(key).Count == 0)
            throw new HarnessRejectedException($"No refinements exist for '{key}' in either journal.");

        var target = _targetFactory(key.Kind, store.Scope);
        var current = store.Get(key)
            ?? throw new HarnessRejectedException($"'{key}' is not currently active.");
        var actual = await target.ReadBackAsync(key.Name, ct);

        // Re-base the daemon view onto the host's on-disk content for file-backed targets.
        JsonElement rebased;
        var targetPath = await target.DescribeAsync(key.Name, ct);
        if (File.Exists(targetPath))
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { markdown = File.ReadAllText(targetPath) }));
            rebased = doc.RootElement.Clone();
        }
        else
        {
            rebased = current.Content;
        }

        return store.ApplyResync(key, rebased, actual);
    }

    // --- Core pipeline -----------------------------------------------------

    private async Task<HarnessRefinementRecord> ApplyCoreAsync(
        HarnessRefinementKind kind,
        HarnessRefinementAction action,
        string name,
        JsonElement? content,
        HarnessRefinementScope scope,
        string author,
        IReadOnlyList<RefinementEvidence>? evidence,
        bool force,
        string? reason,
        int? targetVersion,
        CancellationToken ct)
    {
        ValidateName(name);

        var kindName = kind.ToString().ToLowerInvariant();
        if (!_settings.AllowedKinds.Contains(kindName, StringComparer.OrdinalIgnoreCase))
            throw new HarnessRejectedException($"Kind '{kindName}' is not allowed by configuration.");

        var store = StoreFor(scope, throwIfUnavailable: true);

        if (kind == HarnessRefinementKind.Skill && scope == HarnessRefinementScope.Local)
            throw new HarnessRejectedException("Skill refinements are global-only in v1 (the P04 managed-skill store is user-global); use --scope=global.");

        if (action is HarnessRefinementAction.Create or HarnessRefinementAction.Update)
        {
            if (content is null || content.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new HarnessRejectedException($"{action} requires content.");
            var size = content.Value.GetRawText().Length;
            if (size > _settings.MaxContentBytes)
                throw new HarnessRejectedException($"Content exceeds maxContentBytes ({_settings.MaxContentBytes}).");
        }

        if (_settings.RequireEvidence && action != HarnessRefinementAction.Rollback && (evidence is null || evidence.Count == 0))
            throw new HarnessRejectedException("requireEvidence is on; provide at least one evidence citation.");

        var target = _targetFactory(kind, scope);
        var key = new HarnessEntryKey(kind, name);
        var existing = store.Get(key);
        var effectiveForce = force || string.Equals(_settings.ConflictPolicy, "force", StringComparison.OrdinalIgnoreCase);

        HarnessSyncedWith? appliedSynced;
        JsonElement recordContent;

        switch (action)
        {
            case HarnessRefinementAction.Create:
            {
                var actual = await target.ReadBackAsync(name, ct);
                var collision = actual.Sha256 is not null && kind != HarnessRefinementKind.Prompt;
                if (collision && !await ResolveConflictAsync(key, target, existing?.SyncedWith, actual, "create collision", effectiveForce, ct))
                {
                    await RejectAndThrowAsync(key, target, actual, "create collision", ct);
                }
                appliedSynced = await target.ApplyCreateAsync(name, content!.Value, ct);
                recordContent = content.Value.Clone();
                break;
            }
            case HarnessRefinementAction.Update:
            {
                if (existing is null)
                    throw new HarnessRejectedException($"Cannot update '{key}': no such entry (use create).");
                var actual = await target.ReadBackAsync(name, ct);
                if (Conflicts(existing.SyncedWith, actual) && !await ResolveConflictAsync(key, target, existing.SyncedWith, actual, "host-edit conflict", effectiveForce, ct))
                {
                    await RejectAndThrowAsync(key, target, actual, "host-edit conflict", ct);
                }
                appliedSynced = await target.ApplyUpdateAsync(name, content!.Value, ct);
                recordContent = content.Value.Clone();
                break;
            }
            case HarnessRefinementAction.Delete:
            {
                if (existing is null)
                    throw new HarnessRejectedException($"Cannot delete '{key}': no such entry.");
                var actual = await target.ReadBackAsync(name, ct);
                if (Conflicts(existing.SyncedWith, actual) && !await ResolveConflictAsync(key, target, existing.SyncedWith, actual, "host-edit conflict", effectiveForce, ct))
                {
                    await RejectAndThrowAsync(key, target, actual, "host-edit conflict", ct);
                }
                await target.ApplyDeleteAsync(name, existing.Content, ct);
                recordContent = existing.Content.Clone(); // delete carries last-known content
                appliedSynced = null;
                break;
            }
            case HarnessRefinementAction.Rollback:
            {
                var restoredContent = content!.Value;
                if (existing is not null)
                    appliedSynced = await target.ApplyUpdateAsync(name, restoredContent, ct);
                else
                    appliedSynced = await target.ApplyCreateAsync(name, restoredContent, ct);
                recordContent = restoredContent.Clone();
                break;
            }
            default:
                throw new HarnessRejectedException($"Unsupported action '{action}'.");
        }

        var version = NextVersion(store, key);

        var record = new HarnessRefinementRecord
        {
            RefinementId = 0, // assigned by the store on append
            Timestamp = _clock(),
            Scope = scope,
            Kind = kind,
            Name = name,
            Action = action,
            Version = version,
            Content = recordContent,
            Author = author,
            Evidence = evidence ?? [],
            SyncedWith = appliedSynced,
            TargetVersion = targetVersion,
            Reason = reason,
            Deleted = action == HarnessRefinementAction.Delete,
        };

        await store.AppendAsync(record, ct);

        if (_appendAudit is not null)
        {
            try { await _appendAudit(AuditCustomType, record, ct); }
            catch { /* audit failure must not fail the refinement */ }
        }

        if (_emitEvent is not null)
        {
            await _emitEvent(HarnessRefinementApplied, record, ct);
            await _emitEvent(HarnessStateChanged, new { key, version, scope = scope.ToString().ToLowerInvariant() }, ct);
        }

        return record;
    }

    private async Task<bool> ResolveConflictAsync(HarnessEntryKey key, IRefinementTarget target, HarnessSyncedWith? expected, HarnessSyncedWith? actual, string label, bool effectiveForce, CancellationToken ct)
    {
        if (effectiveForce) return true;
        if (string.Equals(_settings.ConflictPolicy, "ask", StringComparison.OrdinalIgnoreCase) && _ui is not null)
        {
            var describe = await target.DescribeAsync(key.Name, ct);
            return await _ui.ConfirmAsync($"Harness conflict on '{key}' at {describe} ({label}). Force apply and clobber the target?", ct);
        }
        return false;
    }

    private async Task RejectAndThrowAsync(HarnessEntryKey key, IRefinementTarget target, HarnessSyncedWith? actual, string label, CancellationToken ct)
    {
        var describe = await target.DescribeAsync(key.Name, ct);
        if (_emitEvent is not null)
            await _emitEvent(HarnessRefinementRejected, new { key, targetPath = describe, reason = label }, ct);
        throw new HarnessConflictException(key, describe, expected: null, actual, diff: label);
    }

    private HarnessStore StoreFor(HarnessRefinementScope scope, bool throwIfUnavailable)
    {
        var store = scope == HarnessRefinementScope.Local ? _local : _global;
        if (store is null && throwIfUnavailable)
            throw new HarnessRejectedException($"The {scope.ToString().ToLowerInvariant()} journal is not available.");
        return store ?? _local;
    }

    private static int NextVersion(HarnessStore store, HarnessEntryKey key)
    {
        var history = store.History(key);
        return history.Count == 0 ? 1 : history[^1].Version + 1;
    }

    private static bool Conflicts(HarnessSyncedWith? expected, HarnessSyncedWith? actual)
    {
        if (expected is null) return false;
        if (expected.Path?.StartsWith("journal:", StringComparison.Ordinal) == true) return false; // journal-backed target
        if (expected.Sha256 is not null && actual?.Sha256 is not null)
            return !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal);
        if (expected.FileMtimeUtc is not null && actual?.FileMtimeUtc is not null)
            return (expected.FileMtimeUtc.Value - actual.FileMtimeUtc.Value).Duration() > TimeSpan.FromMilliseconds(500);
        if (expected.FileMtimeUtc is not null && actual?.FileMtimeUtc is null)
            return true; // host deleted the file since we last wrote it
        return false;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new HarnessRejectedException("Entry name must not be empty.");
        if (name.Length > 128)
            throw new HarnessRejectedException("Entry name must be at most 128 characters.");
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\n', '\r']) >= 0)
            throw new HarnessRejectedException($"Entry name '{name}' contains characters that are not allowed in a slug.");
    }
}
