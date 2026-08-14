using System.Text;
using System.Text.Json;
using PiSharp.Eval.Kernels;
using PiSharp.Extensions;

namespace PiSharp.Eval;

/// <summary>
/// Snapshot persistence for eval kernels. Persists through the extension state API
/// (namespace <c>pisharp-eval</c>, keys <c>kernels.&lt;sessionId&gt;.&lt;kernelName&gt;</c>)
/// when available, else plain JSON files under
/// <c>~/.pi/PiSharp/eval/kernels/&lt;sessionId&gt;/&lt;kernelName&gt;.json</c>. The file
/// layout is identical either way so migration is trivial. Enforces a size cap; over-cap
/// snapshots are stored lossy (variable values dropped, names/types kept).
/// </summary>
public sealed class KernelSnapshotStore
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new() { WriteIndented = true };

    private readonly IExtensionStateApi? _state;
    private readonly string _fallbackRoot;
    private readonly int _maxBytes;
    private readonly Dictionary<string, KernelSnapshot> _lastSaved = new(StringComparer.Ordinal);

    public KernelSnapshotStore(IExtensionStateApi? state, string? fallbackRoot = null, int maxBytes = EvalOptions.DefaultSnapshotMaxBytes)
    {
        _state = state;
        _fallbackRoot = fallbackRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "PiSharp", "eval", "kernels");
        _maxBytes = maxBytes > 0 ? maxBytes : EvalOptions.DefaultSnapshotMaxBytes;
    }

    public static string Key(string sessionId, string kernelName)
        => $"kernels.{sessionId}.{kernelName}";

    public async Task SaveAsync(string sessionId, string kernelName, KernelSnapshot snapshot, CancellationToken ct = default)
    {
        var (stored, json) = Prepare(snapshot);

        if (_state is not null)
        {
            try
            {
                await _state.SetAsync(Key(sessionId, kernelName), stored, ExtensionStateScope.User, ct);
                lock (_lastSaved) _lastSaved[Key(sessionId, kernelName)] = stored;
                return;
            }
            catch (Exception)
            {
                // State API unavailable/failed — fall through to the file store.
            }
        }

        var dir = Path.Combine(_fallbackRoot, SanitizeSegment(sessionId));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SanitizeSegment(kernelName) + ".json");
        await File.WriteAllTextAsync(path, json, ct);
        lock (_lastSaved) _lastSaved[Key(sessionId, kernelName)] = stored;
    }

    public async Task<KernelSnapshot?> LoadAsync(string sessionId, string kernelName, CancellationToken ct = default)
    {
        if (_state is not null)
        {
            try
            {
                var fromState = await _state.GetAsync<KernelSnapshot>(Key(sessionId, kernelName), ExtensionStateScope.User, ct);
                if (fromState is not null) return fromState;
            }
            catch (Exception)
            {
                // Fall through to the file store.
            }
        }

        var path = Path.Combine(_fallbackRoot, SanitizeSegment(sessionId), SanitizeSegment(kernelName) + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<KernelSnapshot>(text, SnapshotJsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public KernelSnapshot? LastSaved(string sessionId, string kernelName)
    {
        lock (_lastSaved) return _lastSaved.TryGetValue(Key(sessionId, kernelName), out var snapshot) ? snapshot : null;
    }

    public bool TryGetBytes(string sessionId, string kernelName, out long bytes)
    {
        var snapshot = LastSaved(sessionId, kernelName);
        if (snapshot is null) { bytes = 0; return false; }
        bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(snapshot, SnapshotJsonOptions));
        return true;
    }

    private (KernelSnapshot Stored, string Json) Prepare(KernelSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions);
        if (Encoding.UTF8.GetByteCount(json) <= _maxBytes) return (snapshot, json);

        // Over cap: drop variable values (lossy truncation), keep names/types.
        var lossy = snapshot with
        {
            Lossy = true,
            Variables = snapshot.Variables.Select(v => v with { Json = null, Lossy = true }).ToArray(),
        };
        return (lossy, JsonSerializer.Serialize(lossy, SnapshotJsonOptions));
    }

    private static string SanitizeSegment(string value)
        => string.Concat((value ?? "default").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
}
