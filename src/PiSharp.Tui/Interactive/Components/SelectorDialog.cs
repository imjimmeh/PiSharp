using System.Collections.ObjectModel;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public static class SelectorDialog
{
    private const int MaxVisible = 10;

    public static Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default, ITuiApplicationContext? appContext = null)
    {
        if (options.Count == 0) return Task.FromResult<string?>(null);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<string?>(cancellationToken);

        var ctx = appContext ?? new TerminalGuiApplicationContext();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        ctx.Post(() =>
        {
            try
            {
                completion.TrySetResult(RunSelector(ctx, title, options));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return CompleteAndDisposeRegistrationAsync(completion.Task, registration);
    }

    private static async Task<string?> CompleteAndDisposeRegistrationAsync(Task<string?> selectionTask, CancellationTokenRegistration registration)
    {
        try
        {
            return await selectionTask.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<string> RenderWindow(IReadOnlyList<string> options, int selectedIndex, int maxVisible = MaxVisible)
    {
        if (options.Count == 0) return ["  No matching models"];
        var startIndex = WindowStartIndex(options.Count, selectedIndex, maxVisible);
        var endIndex = Math.Min(startIndex + maxVisible, options.Count);
        var rows = new List<string>();
        for (var i = startIndex; i < endIndex; i++)
        {
            var option = options[i];
            rows.Add(i == selectedIndex ? $"→ {option}" : $"  {option}");
        }
        if (startIndex > 0 || endIndex < options.Count) rows.Add($"  ({selectedIndex + 1}/{options.Count})");
        return rows;
    }

    internal static int WindowStartIndex(int itemCount, int selectedIndex, int maxVisible = MaxVisible)
        => itemCount == 0 ? 0 : Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, itemCount - maxVisible));

    public static string? ResolveArrowSelection(IReadOnlyList<string> options, int selectedIndex)
        => selectedIndex >= 0 && selectedIndex < options.Count ? options[selectedIndex] : null;

    private static string? RunSelector(ITuiApplicationContext appContext, string title, IReadOnlyList<string> options)
    {
        var filtered = options.ToArray();
        var rows = new ObservableCollection<string>(RenderWindow(filtered, 0));
        var selectedFilteredIndex = 0;
        var refreshing = false;
        string? selected = null;

        var dialog = new Window
        {
            Title = title,
            Width = Dim.Percent(80),
            Height = Dim.Percent(80),
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
            Text = "Type to fuzzy filter models. ↑/↓ moves, Enter selects, Esc cancels."
        };
        var search = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Height = 1
        };
        var list = new ListView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        list.SetSource(rows);

        void Refresh()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var query = search.Text?.ToString() ?? string.Empty;
                filtered = TuiFuzzyMatcher.Filter(options, query, option => option).ToArray();
                selectedFilteredIndex = Math.Min(selectedFilteredIndex, Math.Max(0, filtered.Length - 1));
                rows.Clear();
                foreach (var row in RenderWindow(filtered, selectedFilteredIndex)) rows.Add(row);
                var visibleSelectedIndex = filtered.Length == 0 ? 0 : selectedFilteredIndex - WindowStartIndex(filtered.Length, selectedFilteredIndex);
                if (rows.Count > 0 && list.SelectedItem != visibleSelectedIndex) list.SelectedItem = visibleSelectedIndex;
            }
            finally
            {
                refreshing = false;
            }
        }

        int AbsoluteIndexFromVisibleSelection()
        {
            if (filtered.Length == 0) return -1;
            var visibleItemCount = Math.Min(MaxVisible, filtered.Length);
            if (list.SelectedItem < 0 || list.SelectedItem >= visibleItemCount) return -1;
            var startIndex = WindowStartIndex(filtered.Length, selectedFilteredIndex);
            var absolute = startIndex + list.SelectedItem;
            return absolute >= 0 && absolute < filtered.Length ? absolute : -1;
        }

        void Accept()
        {
            selected = ResolveArrowSelection(filtered, selectedFilteredIndex);
            if (selected is null) return;
            appContext.RequestStop(dialog);
        }

        search.TextChanged += (_, _) =>
        {
            selectedFilteredIndex = 0;
            Refresh();
        };
        list.SelectedItemChanged += (_, _) =>
        {
            if (refreshing) return;
            var absolute = AbsoluteIndexFromVisibleSelection();
            if (absolute < 0) return;
            selectedFilteredIndex = absolute;
            Refresh();
        };
        list.OpenSelectedItem += (_, _) => Accept();
        search.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Enter) return;
            key.Handled = true;
            Accept();
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Accept();
            }
            else if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                selected = null;
                appContext.RequestStop(dialog);
            }
        };

        dialog.Add(hint, search, list);
        search.SetFocus();
        Refresh();
        appContext.Run(dialog);
        dialog.Dispose();
        return selected;
    }
}
