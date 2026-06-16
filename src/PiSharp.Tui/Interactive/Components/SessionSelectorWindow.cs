using System.Collections.ObjectModel;
using PiSharp.Abstractions.Sessions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.SessionSelection;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

internal sealed class SessionSelectorWindow
{
    private const int MaxVisible = 10;

    private readonly Window _dialog;
    private readonly TextField _search;
    private readonly ListView _list;
    private readonly ITuiApplicationContext _appContext;
    private readonly SessionSelectorController _controller;
    private readonly SessionSelectorFilterScheduler _filterScheduler;
    private readonly ObservableCollection<string> _rowsSource;
    private readonly CancellationTokenSource _dialogLoadCts;
    private readonly Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> _loadAllSessionsAsync;
    private readonly IReadOnlyList<JsonlSessionMetadata> _currentSessions;
    private readonly TextView _hint;

    private SessionSelectorRow[] _modelRows = [];
    private int _selectedIndex;
    private JsonlSessionMetadata? _selected;
    private bool _refreshing;

    public SessionSelectorWindow(
        IReadOnlyList<JsonlSessionMetadata> currentSessions,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAllSessionsAsync,
        ITuiApplicationContext appContext,
        CancellationToken cancellationToken)
    {
        _currentSessions = currentSessions;
        _loadAllSessionsAsync = loadAllSessionsAsync;
        _appContext = appContext;
        _dialogLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _controller = new SessionSelectorController();
        _filterScheduler = new SessionSelectorFilterScheduler(_appContext.Post);
        _rowsSource = new ObservableCollection<string>();

        _dialog = new Window
        {
            Title = "Resume Session (Current Folder)",
            Width = Dim.Percent(88),
            Height = Dim.Percent(82),
            ColorScheme = Theme.TuiTheme.PopupColorScheme
        };

        var hint = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 2,
            ReadOnly = true,
            WordWrap = false,
            Text = "Type to search sessions. Tab toggles Current/All. ↑/↓ moves, Enter selects, Esc cancels."
        };
        _hint = hint;
        _search = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Height = 1
        };
        _list = new ListView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        _list.SetSource(_rowsSource);

        _search.TextChanged += (_, _) =>
        {
            _selectedIndex = 0;
            ScheduleSearchRefresh();
        };
        _list.SelectedItemChanged += (_, _) =>
        {
            if (_refreshing) return;
            var absolute = AbsoluteIndexFromVisibleSelection();
            if (absolute < 0) return;
            _selectedIndex = absolute;
            Refresh();
        };
        _list.OpenSelectedItem += (_, _) => Accept();
        _search.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Accept();
            }
            else if (key.KeyCode == KeyCode.Tab)
            {
                key.Handled = true;
                ToggleScope();
            }
        };
        _dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Accept();
            }
            else if (key.KeyCode == KeyCode.Tab)
            {
                key.Handled = true;
                ToggleScope();
            }
            else if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                _selected = null;
                _dialogLoadCts.Cancel();
                _appContext.RequestStop(_dialog);
            }
        };

        _dialog.Add(hint, _search, _list);
        _search.SetFocus();
        _controller.OnSessionsLoaded = Refresh;
    }

    public string DialogTitle => _dialog.Title?.ToString() ?? string.Empty;

    public IReadOnlyList<string> Rows
    {
        get
        {
            var result = new List<string>();
            foreach (var row in _rowsSource) result.Add(row);
            return result;
        }
    }

    public JsonlSessionMetadata? SelectedResult => _selected;

    public bool IsControllerActive => _controller.IsActive;

    public SessionSelectorScope Scope => _controller.Scope;

    internal Task? LoadingTask => _controller.LoadingTask;

    internal string HintText => _hint.Text?.ToString() ?? string.Empty;

    public JsonlSessionMetadata? Show()
    {
        try
        {
            Refresh();
            _appContext.Run(_dialog);
        }
        finally
        {
            _controller.IsActive = false;
            _filterScheduler.Dispose();
            _dialogLoadCts.Cancel();
            _dialogLoadCts.Dispose();
            _dialog.Dispose();
        }
        return _selected;
    }

    private IReadOnlyList<JsonlSessionMetadata> SessionsForScope()
        => _controller.Scope == SessionSelectorScope.Current ? _currentSessions : _controller.AllSessions ?? [];

    internal void Refresh()
    {
        if (_refreshing) return;
        _filterScheduler.CancelPending();
        _refreshing = true;
        try
        {
            _dialog.Title = _controller.Scope == SessionSelectorScope.Current ? "Resume Session (Current Folder)" : "Resume Session (All)";
            var rows = SessionSelectorModel.BuildRows(
                SessionsForScope(),
                _search.Text?.ToString() ?? string.Empty,
                showCwd: _controller.Scope == SessionSelectorScope.All,
                now: DateTimeOffset.UtcNow).ToArray();
            ApplyRows(rows);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ScheduleSearchRefresh()
    {
        _filterScheduler.Schedule(
            SessionsForScope().ToArray(),
            _search.Text?.ToString() ?? string.Empty,
            showCwd: _controller.Scope == SessionSelectorScope.All,
            DateTimeOffset.UtcNow,
            rows =>
            {
                if (_refreshing) return;
                _refreshing = true;
                try
                {
                    _dialog.Title = _controller.Scope == SessionSelectorScope.Current ? "Resume Session (Current Folder)" : "Resume Session (All)";
                    ApplyRows(rows);
                }
                finally
                {
                    _refreshing = false;
                }
            },
            _dialogLoadCts.Token);
    }

    private void ApplyRows(SessionSelectorRow[] rows)
    {
        _modelRows = rows;
        _selectedIndex = Math.Min(_selectedIndex, Math.Max(0, _modelRows.Length - 1));
        _rowsSource.Clear();
        foreach (var row in SessionSelectorDialog.RenderWindow(_modelRows, _selectedIndex)) _rowsSource.Add(row);
        var visibleSelectedIndex = _modelRows.Length == 0 ? 0 : _selectedIndex - SessionSelectorDialog.WindowStartIndex(_modelRows.Length, _selectedIndex);
        if (_rowsSource.Count > 0 && _list.SelectedItem != visibleSelectedIndex) _list.SelectedItem = visibleSelectedIndex;
    }

    private int AbsoluteIndexFromVisibleSelection()
    {
        if (_modelRows.Length == 0) return -1;
        var visibleItemCount = Math.Min(MaxVisible, _modelRows.Length);
        if (_list.SelectedItem < 0 || _list.SelectedItem >= visibleItemCount) return -1;
        var absolute = SessionSelectorDialog.WindowStartIndex(_modelRows.Length, _selectedIndex) + _list.SelectedItem;
        return absolute >= 0 && absolute < _modelRows.Length ? absolute : -1;
    }

    internal void Accept()
    {
        var absolute = AbsoluteIndexFromVisibleSelection();
        if (absolute < 0) return;
        _selected = _modelRows[absolute].Session;
        _appContext.RequestStop(_dialog);
    }

    internal void ToggleScope()
    {
        _controller.Scope = _controller.Scope == SessionSelectorScope.Current ? SessionSelectorScope.All : SessionSelectorScope.Current;
        _selectedIndex = 0;
        if (_controller.TryStartLoading(_loadAllSessionsAsync, _dialogLoadCts.Token, _appContext, out var loadingTitle, out var loadingRows))
        {
            _dialog.Title = loadingTitle;
            _rowsSource.Clear();
            foreach (var row in loadingRows) _rowsSource.Add(row);
        }
        else
        {
            Refresh();
        }
    }
}
