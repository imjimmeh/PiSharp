using PiSharp.Agent.Resources.Theme;
using PiSharp.Agent.Core.Events;

namespace PiSharp.Server.Runtime;

/// <summary>
/// Daemon-side theme authority (plan C8): the single owner of the merged theme-document set and the
/// active theme name. Per-session runtimes and attached clients are views of this registry — sessions
/// feed it their <c>ThemePaths</c> (settings/CLI/packages/<c>resources_discover</c>) at creation, and
/// the <c>list_themes</c>/<c>set_theme</c>/<c>get_theme</c> commands plus the <c>theme_changed</c>
/// event read and mutate it. Multi-terminal attach stays consistent because there is exactly one
/// active theme daemon-wide.
/// </summary>
public sealed class ThemeRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TuiThemeDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeName;

    /// <summary>Raised whenever the merged document set or the active theme changes.</summary>
    public event EventHandler? Changed;

    /// <summary>All known theme documents, ordered by name (case-insensitive).</summary>
    public IReadOnlyList<TuiThemeDocument> Documents
    {
        get
        {
            lock (_gate)
            {
                return _documents.Values.OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>Name of the active theme, or <c>null</c> when no theme has been activated.</summary>
    public string? ActiveName
    {
        get
        {
            lock (_gate)
            {
                return _activeName;
            }
        }
    }

    /// <summary>The active theme document, or <c>null</c> when no theme has been activated.</summary>
    public TuiThemeDocument? ActiveDocument
    {
        get
        {
            lock (_gate)
            {
                return _activeName is null || !_documents.TryGetValue(_activeName, out var document) ? null : document;
            }
        }
    }

    /// <summary>
    /// Merges theme documents discovered under <paramref name="themePaths"/> into the registry.
    /// Documents are keyed by name (case-insensitive); a later merge replaces an earlier document
    /// with the same name. Returns the number of documents merged (0 when no paths or no loadable
    /// documents). The active name is preserved across merges.
    /// </summary>
    public async Task<int> MergeAsync(IEnumerable<string>? themePaths, CancellationToken cancellationToken = default)
    {
        var paths = themePaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? [];
        if (paths.Length == 0) return 0;

        var documents = await TuiThemeDocument.LoadAllAsync(paths, cancellationToken);
        var merged = 0;
        lock (_gate)
        {
            foreach (var document in documents)
            {
                if (string.IsNullOrWhiteSpace(document.Name)) continue;
                _documents[document.Name] = document;
                merged++;
            }
        }

        if (merged > 0) Changed?.Invoke(this, EventArgs.Empty);
        return merged;
    }

    /// <summary>
    /// Activates the named theme (case-insensitive match against the merged document set). Returns
    /// <c>false</c> when no document matches, leaving the active theme unchanged.
    /// </summary>
    public bool TrySetActive(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        lock (_gate)
        {
            if (!_documents.TryGetValue(name, out var document)) return false;
            _activeName = document.Name;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Applies <paramref name="name"/> to every live session runtime (best-effort: a runtime only
    /// adopts the document when its own theme paths resolve it) and broadcasts the daemon
    /// <c>theme_changed</c> event on every live session stream. Called by the <c>set_theme</c>
    /// command and by daemon-side interception of the <c>set_theme</c> UI kind; the registry stays
    /// the single authority either way.
    /// </summary>
    public static async Task ApplyToSessionsAsync(
        ThemeRegistry registry,
        IEnumerable<LiveServerSession> sessions,
        string name,
        CancellationToken cancellationToken = default)
    {
        var liveSessions = sessions.ToArray();
        foreach (var live in liveSessions)
        {
            await live.RunExclusiveAsync(
                (runtime, token) => runtime.SetThemeByNameAsync(name, token),
                CancellationToken.None);
        }

        var changedEvent = AgentSessionEvent.FromServer(
            "theme_changed",
            new { name = registry.ActiveName, document = registry.ActiveDocument });
        foreach (var live in liveSessions)
        {
            live.EmitEvent(changedEvent);
        }
    }
}
