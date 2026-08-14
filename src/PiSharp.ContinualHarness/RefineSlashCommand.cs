using System.Text;
using System.Text.Json;
using PiSharp.ContinualHarness.Contracts;
using PiSharp.Extensions;

namespace PiSharp.ContinualHarness;

/// <summary>
/// The <c>/refine</c> slash command: bare interactive flow, a direct
/// <c>kind action name [content|@file]</c> form, and the <c>list</c>/<c>show</c>/<c>diff</c>/
/// <c>rollback</c>/<c>sync</c> subcommands. Executes through the same service path as the model tool.
/// </summary>
public sealed class RefineSlashCommand
{
    private const int MaxContentBytes = 64 * 1024;

    private readonly IExtensionApi _api;
    private readonly HarnessRefinementService _service;
    private readonly IHarnessSettings _settings;
    private readonly string _sessionId;
    private readonly Func<bool> _gate;

    public RefineSlashCommand(IExtensionApi api, HarnessRefinementService service, IHarnessSettings settings, Func<bool>? gate = null, string sessionId = "user")
    {
        _api = api;
        _service = service;
        _settings = settings;
        _sessionId = sessionId;
        _gate = gate ?? (() => true);
    }

    public async Task InvokeAsync(string args, CancellationToken ct = default)
    {
        if (!_gate())
        {
            await NotifyAsync("Continual-harness is disabled (the 'refine' flag is off).", ExtensionUiSeverity.Warning, ct);
            return;
        }

        var tokens = Tokenize(args);
        try
        {
            if (tokens.Count == 0)
            {
                await RunInteractiveAsync(ct);
                return;
            }

            switch (tokens[0].ToLowerInvariant())
            {
                case "list": await RunListAsync(tokens, ct); return;
                case "show": await RunShowAsync(tokens, ct); return;
                case "diff": await RunDiffAsync(tokens, ct); return;
                case "rollback": await RunRollbackAsync(tokens, ct); return;
                case "sync": await RunSyncAsync(ct); return;
                default: await RunDirectAsync(tokens, ct); return;
            }
        }
        catch (HarnessConflictException conflict)
        {
            await NotifyAsync($"Conflict: {conflict.Message}\n---\n{conflict.Diff}", ExtensionUiSeverity.Error, ct);
        }
        catch (HarnessRejectedException rejected)
        {
            await NotifyAsync(rejected.Message, ExtensionUiSeverity.Warning, ct);
        }
    }

    // --- Subcommands -------------------------------------------------------

    private async Task RunListAsync(List<string> tokens, CancellationToken ct)
    {
        string? kindFilter = null;
        if (tokens.Count > 1) kindFilter = tokens[1].ToLowerInvariant();

        var entries = AllEffectiveEntries().Where(e => kindFilter is null || e.Key.Kind.ToString().ToLowerInvariant() == kindFilter);
        var lines = entries.Select(HarnessRefinementFormatter.FormatEntry).ToList();
        await NotifyAsync(lines.Count == 0 ? "(no refinements)" : string.Join('\n', lines), ExtensionUiSeverity.Info, ct);
    }

    private async Task RunShowAsync(List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 3) { await NotifyAsync("usage: /refine show <kind> <name> [version]", ExtensionUiSeverity.Warning, ct); return; }
        var kind = ParseKind(tokens[1]);
        var name = tokens[2];
        var version = tokens.Count > 3 && int.TryParse(tokens[3], out var v) ? v : (int?)null;

        var record = FindRecord(kind, name, version);
        if (record is null) { await NotifyAsync($"No record for {new HarnessEntryKey(kind, name)}", ExtensionUiSeverity.Warning, ct); return; }

        var text = HarnessRefinementFormatter.RenderContent(record.Content);
        await NotifyAsync($"{HarnessRefinementFormatter.FormatRecord(record)}\n\n{text}", ExtensionUiSeverity.Info, ct);
    }

    private async Task RunDiffAsync(List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 3) { await NotifyAsync("usage: /refine diff <kind> <name> [v1] [v2]", ExtensionUiSeverity.Warning, ct); return; }
        var kind = ParseKind(tokens[1]);
        var name = tokens[2];

        var store = _service.Local.History(new HarnessEntryKey(kind, name)).Count > 0
            ? _service.Local
            : _service.Global;
        var history = store!.History(new HarnessEntryKey(kind, name));

        var currentVersion = history.Count > 0 ? history[^1].Version : 0;
        var v2 = tokens.Count > 4 && int.TryParse(tokens[4], out var v2v) ? v2v : currentVersion;
        var v1 = tokens.Count > 3 && int.TryParse(tokens[3], out var v1v) ? v1v : currentVersion - 1;

        var older = store.At(new HarnessEntryKey(kind, name), v1)?.Content;
        var newer = store.At(new HarnessEntryKey(kind, name), v2)?.Content;
        if (older is null || newer is null) { await NotifyAsync("versions not found", ExtensionUiSeverity.Warning, ct); return; }

        var diff = HarnessRefinementFormatter.Diff(
            HarnessRefinementFormatter.RenderContent(older.Value),
            HarnessRefinementFormatter.RenderContent(newer.Value));
        await NotifyAsync($"diff {kind}/{name} v{v1} -> v{v2}\n{diff}", ExtensionUiSeverity.Info, ct);
    }

    private async Task RunRollbackAsync(List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 3) { await NotifyAsync("usage: /refine rollback <kind> <name> [version]", ExtensionUiSeverity.Warning, ct); return; }
        var kind = ParseKind(tokens[1]);
        var name = tokens[2];
        var version = tokens.Count > 3 && int.TryParse(tokens[3], out var v) ? v : (int?)null;
        var force = tokens.Contains("--force", StringComparer.Ordinal);

        HarnessRefinementScope? scope = null;
        foreach (var candidate in new[] { HarnessRefinementScope.Local, HarnessRefinementScope.Global })
        {
            var store = candidate == HarnessRefinementScope.Local ? _service.Local : _service.Global;
            if (store is not null && store.History(new HarnessEntryKey(kind, name)).Count > 0) { scope = candidate; break; }
        }
        if (scope is null) { await NotifyAsync($"No refinements for {kind.ToString().ToLowerInvariant()}/{name}", ExtensionUiSeverity.Warning, ct); return; }

        var record = await _service.RollbackAsync(kind, name, version, scope.Value, author: "user", force: force, ct);
        await NotifyAsync($"Rolled back: {HarnessRefinementFormatter.FormatRecord(record)}", ExtensionUiSeverity.Success, ct);
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        var touched = 0;
        foreach (var store in Stores())
        {
            foreach (var key in store!.Effective.Keys.Where(k => k.Kind != HarnessRefinementKind.Prompt).ToList())
            {
                await _service.ReSyncTargetAsync(key, ct);
                touched++;
            }
        }
        await NotifyAsync($"Re-synced {touched} file/API-backed target(s).", ExtensionUiSeverity.Info, ct);
    }

    // --- Direct form -------------------------------------------------------

    private async Task RunDirectAsync(List<string> tokens, CancellationToken ct)
    {
        if (tokens.Count < 3)
        {
            await NotifyAsync("usage: /refine <kind> <action> <name> [content|@file] [--scope=local|global] [--force] [--evidence=<text>] [--reason=<text>] [--description=<text>]", ExtensionUiSeverity.Warning, ct);
            return;
        }

        var kind = ParseKind(tokens[0]);
        var action = ParseAction(tokens[1]);
        var name = tokens[2];

        var evidenceToken = Flag(tokens, "evidence");
        var reason = Flag(tokens, "reason");
        var force = tokens.Contains("--force", StringComparer.Ordinal);
        var scope = ParseScope(Flag(tokens, "scope") ?? _settings.DefaultScope);

        JsonElement? content = null;
        if (action is HarnessRefinementAction.Create or HarnessRefinementAction.Update)
        {
            var contentArg = tokens.Count > 3 ? tokens[3] : null;
            content = BuildContent(kind, name, contentArg, Flag(tokens, "description"));
        }

        var evidence = evidenceToken is null
            ? new List<RefinementEvidence>()
            : new List<RefinementEvidence> { new(_sessionId, EntryId: null, HarnessRefinementFormatter.BoundExcerpt(evidenceToken)) };

        await _service.ApplyAsync(kind, action, name, content, scope, author: "user", evidence, force, reason, ct);
        await NotifyAsync($"Refined: {kind.ToString().ToLowerInvariant()}/{name} ({action.ToString().ToLowerInvariant()}, {scope.ToString().ToLowerInvariant()})", ExtensionUiSeverity.Success, ct);
    }

    private static JsonElement? BuildContent(HarnessRefinementKind kind, string name, string? contentArg, string? description)
    {
        var text = ReadContent(contentArg);
        return kind == HarnessRefinementKind.Skill
            ? JsonSerializer.SerializeToElement(new { description = string.IsNullOrWhiteSpace(description) ? name : description, content = text, disableModelInvocation = false })
            : JsonSerializer.SerializeToElement(new { markdown = text });
    }

    private static string ReadContent(string? contentArg)
    {
        if (contentArg is null) return string.Empty;
        var text = contentArg.StartsWith('@')
            ? File.ReadAllText(contentArg[1..])
            : contentArg;
        if (text.Length > MaxContentBytes)
            throw new HarnessRejectedException($"Content exceeds the {MaxContentBytes}-byte command bound.");
        return text;
    }

    // --- Interactive form --------------------------------------------------

    private async Task RunInteractiveAsync(CancellationToken ct)
    {
        var ui = _api.Ui;
        var kindToken = await ui.SelectAsync("Which harness state to refine?", ["prompt", "memory", "skill", "subagent"], ct);
        if (kindToken is null) return;
        var kind = ParseKind(kindToken);

        var actionToken = await ui.SelectAsync("Action?", ["create", "update", "delete"], ct);
        if (actionToken is null) return;
        var action = ParseAction(actionToken);

        var name = await ui.InputAsync("Entry name (slug):", null, ct);
        if (string.IsNullOrWhiteSpace(name)) return;

        JsonElement? content = null;
        if (action is HarnessRefinementAction.Create or HarnessRefinementAction.Update)
        {
            var markdown = await ui.InputAsync("Content markdown (or @path):", null, ct);
            if (markdown is null) return;
            content = BuildContent(kind, name, markdown, null);
        }

        var scope = ParseScope(_settings.DefaultScope);
        var confirmed = await ui.ConfirmAsync($"Apply {action.ToString().ToLowerInvariant()} {kind.ToString().ToLowerInvariant()}/{name}?", ct);
        if (!confirmed) return;

        await _service.ApplyAsync(kind, action, name, content, scope, author: "user", [], force: false, reason: null, ct);
        await NotifyAsync($"Refined: {kind.ToString().ToLowerInvariant()}/{name}", ExtensionUiSeverity.Success, ct);
    }

    // --- Helpers -----------------------------------------------------------

    private IEnumerable<HarnessStore?> Stores()
    {
        yield return _service.Local;
        if (_service.Global is not null) yield return _service.Global;
    }

    private IEnumerable<HarnessEntry> AllEffectiveEntries()
    {
        var entries = new List<HarnessEntry>();
        if (_service.Global is not null)
            entries.AddRange(_service.Global.Effective.Values);
        foreach (var local in _service.Local.Effective.Values)
        {
            entries.RemoveAll(e => e.Key.Equals(local.Key));
            entries.Add(local);
        }
        return entries;
    }

    private HarnessRefinementRecord? FindRecord(HarnessRefinementKind kind, string name, int? version)
    {
        var key = new HarnessEntryKey(kind, name);
        foreach (var store in Stores())
        {
            if (store is null) continue;
            if (version is { } v)
            {
                var record = store.At(key, v);
                if (record is not null) return record;
            }
            else
            {
                var entry = store.Get(key);
                if (entry is not null)
                {
                    var record = store.At(key, entry.Version);
                    if (record is not null) return record;
                }
            }
        }
        return null;
    }

    private static HarnessRefinementKind ParseKind(string token)
        => token.ToLowerInvariant() switch
        {
            "prompt" => HarnessRefinementKind.Prompt,
            "memory" => HarnessRefinementKind.Memory,
            "skill" => HarnessRefinementKind.Skill,
            "subagent" or "agent" => HarnessRefinementKind.Subagent,
            _ => throw new HarnessRejectedException($"Unknown refinement kind '{token}'."),
        };

    private static HarnessRefinementAction ParseAction(string token)
        => token.ToLowerInvariant() switch
        {
            "create" => HarnessRefinementAction.Create,
            "update" => HarnessRefinementAction.Update,
            "delete" => HarnessRefinementAction.Delete,
            "rollback" => HarnessRefinementAction.Rollback,
            _ => throw new HarnessRejectedException($"Unknown action '{token}'."),
        };

    private static HarnessRefinementScope ParseScope(string? token)
        => token?.ToLowerInvariant() switch
        {
            "global" => HarnessRefinementScope.Global,
            _ => HarnessRefinementScope.Local,
        };

    private static string? Flag(List<string> tokens, string name)
    {
        var prefix = "--" + name + "=";
        foreach (var token in tokens)
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
                return token[prefix.Length..];
        }
        return null;
    }

    internal static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in input)
        {
            if (c == '"') { inQuotes = !inQuotes; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private Task NotifyAsync(string message, ExtensionUiSeverity severity, CancellationToken ct)
        => _api.Ui.NotifyAsync(message, severity, ct);
}
