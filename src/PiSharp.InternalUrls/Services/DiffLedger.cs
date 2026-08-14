namespace PiSharp.InternalUrls.Services;

/// <summary>
/// In-memory, capacity-bounded ledger of the most recent unified diffs produced
/// by the <c>edit</c> tool, keyed by absolute file path. Per-session and
/// daemon-resident; feeds the <c>diff://</c> internal URL resolver. Thread-safe.
/// </summary>
public sealed class DiffLedger(int capacity)
{
    public const int DefaultCapacity = 256;

    public DiffLedger() : this(DefaultCapacity) { }

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _diffs = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private (string? Path, string? Diff) _latest;

    public int Capacity { get; } = capacity > 0 ? capacity : DefaultCapacity;

    /// <summary>Records a unified diff for a file, making it the most recent diff overall.</summary>
    public void Record(string absolutePath, string unifiedDiff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentNullException.ThrowIfNull(unifiedDiff);

        lock (_gate)
        {
            if (_diffs.ContainsKey(absolutePath))
            {
                _order.Remove(absolutePath);
            }
            else if (_diffs.Count >= Capacity)
            {
                var victim = _order.First!.Value;
                _order.RemoveFirst();
                _diffs.Remove(victim);
            }

            _diffs[absolutePath] = unifiedDiff;
            _order.AddLast(absolutePath);
            _latest = (absolutePath, unifiedDiff);
        }
    }

    /// <summary>Returns the most recent diff recorded across all files.</summary>
    public bool TryGetLatest(out string? absolutePath, out string? diff)
    {
        lock (_gate)
        {
            absolutePath = _latest.Path;
            diff = _latest.Diff;
            return _latest.Path is not null;
        }
    }

    /// <summary>Returns the most recent diff recorded for a specific absolute path.</summary>
    public bool TryGetForPath(string absolutePath, out string diff)
    {
        lock (_gate)
        {
            if (absolutePath is not null && _diffs.TryGetValue(absolutePath, out var value))
            {
                diff = value;
                return true;
            }

            diff = string.Empty;
            return false;
        }
    }

    public int Count
    {
        get { lock (_gate) return _diffs.Count; }
    }

    public IReadOnlyList<string> Paths
    {
        get { lock (_gate) return _order.ToArray(); }
    }

    /// <summary>Removes all recorded diffs.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _diffs.Clear();
            _order.Clear();
            _latest = default;
        }
    }
}
