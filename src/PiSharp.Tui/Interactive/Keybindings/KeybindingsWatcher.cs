using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace PiSharp.Tui.Interactive.Keybindings;

/// <summary>
/// Watches the user keybindings file for changes and re-applies the effective table on the TUI
/// UI thread (debounced ~200 ms), exactly as <c>PiSharp.Tui</c>'s input layer reads it.
/// </summary>
public sealed class KeybindingsWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly string _path;
    private readonly TuiKeybindingStore _store;
    private readonly Action<Action> _uiPost;
    private readonly Action<string> _reportDiagnostic;
    private readonly ILogger<KeybindingsWatcher> _logger;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    public KeybindingsWatcher(string path, TuiKeybindingStore store, Action<Action> uiPost, Action<string> reportDiagnostic, ILogger<KeybindingsWatcher>? logger = null)
    {
        _path = path;
        _store = store;
        _uiPost = uiPost;
        _reportDiagnostic = reportDiagnostic;
        _logger = logger ?? NullLogger<KeybindingsWatcher>.Instance;

        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
            return;

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Deleted -= OnFileEvent;
            _watcher.Dispose();
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _logger.LogDebug("Keybindings change detected; reload scheduled path={Path}", _path);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => ReloadFromDisk(), null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadFromDisk()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (!KeybindingsLoader.TryReadFile(_path, out var json, out var error))
        {
            var message = error ?? $"Failed to read keybindings file '{_path}'.";
            _logger.LogWarning("Keybindings reload failed path={Path} error={Error}", _path, message);
            _uiPost(() => { if (!_disposed) _reportDiagnostic(message); });
            return;
        }

        _uiPost(() =>
        {
            if (_disposed) return;
            _store.Reload(json);
            _logger.LogDebug("Keybindings reloaded path={Path} bindings={BindingCount} diagnostics={DiagnosticCount}", _path, _store.CommandDescriptors.Count, _store.Diagnostics.Count);
            foreach (var diagnostic in _store.Diagnostics)
                _reportDiagnostic(diagnostic);
        });
    }
}
