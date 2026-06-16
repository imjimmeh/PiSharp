using System.Collections.ObjectModel;
using PiSharp.Tui.Interactive.Theme;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public enum TuiSidebarSide { Left, Right }

/// <summary>
/// A composite sidebar: an optional built-in "Modified Files" list (Left side only) followed by
/// one read-only FrameView per extension widget whose placement matches this side.
/// </summary>
public sealed class TuiSidebarView : FrameView
{
    private readonly TuiSidebarSide _side;
    private readonly Action<string>? _insertPath;
    private readonly ListView? _modifiedFilesList;
    private readonly View _widgetStack;
    private IReadOnlyList<string> _modifiedFiles = [];

    public TuiSidebarView(TuiSidebarSide side, Action<string>? insertPath = null)
    {
        _side = side;
        _insertPath = insertPath;
        Title = side == TuiSidebarSide.Left ? "Files" : "Panels";
        BorderStyle = LineStyle.Single;
        ColorScheme = TuiTheme.DefaultColorScheme;
        CanFocus = true;

        if (side == TuiSidebarSide.Left)
        {
            _modifiedFilesList = new ListView
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(50),
                CanFocus = true
            };
            _modifiedFilesList.OpenSelectedItem += (_, _) => InsertFileAt(_modifiedFilesList.SelectedItem);
            Add(_modifiedFilesList);
            _widgetStack = new View { X = 0, Y = Pos.Bottom(_modifiedFilesList), Width = Dim.Fill(), Height = Dim.Fill() };
        }
        else
        {
            _widgetStack = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        }

        Add(_widgetStack);
    }

    /// <summary>Pure filter used by the coordinator/tests: visible slots for this side ordered by Id.</summary>
    public static IReadOnlyList<TuiBridgeSlot> SlotsForSide(TuiRenderState state, TuiSidebarSide side)
    {
        var placement = side == TuiSidebarSide.Left ? "sidebar-left" : "sidebar-right";
        return state.BridgeSlots
            .Where(slot => slot.Visible && string.Equals(slot.Placement, placement, StringComparison.Ordinal))
            .OrderBy(slot => slot.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public void SetModifiedFiles(IReadOnlyList<string> files)
    {
        _modifiedFiles = files;
        _modifiedFilesList?.SetSource(new ObservableCollection<string>(files.ToList()));
    }

    public void InsertFileAt(int index)
    {
        if (index < 0 || index >= _modifiedFiles.Count) return;
        _insertPath?.Invoke(_modifiedFiles[index]);
    }

    /// <summary>Rebuild widget frames from state. Called from the render coordinator.</summary>
    public void Render(TuiRenderState state)
    {
        // Clear all existing widgets
        var toRemove = new List<View>();
        foreach (var subview in _widgetStack.Subviews)
        {
            toRemove.Add(subview);
        }
        foreach (var view in toRemove)
        {
            _widgetStack.Remove(view);
        }

        var slots = SlotsForSide(state, _side);
        var y = 0;
        foreach (var slot in slots)
        {
            var frame = new FrameView
            {
                Title = slot.Title,
                X = 0, Y = y, Width = Dim.Fill(), Height = Dim.Absolute(3)
            };
            frame.Add(new TextView
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
                ReadOnly = true, WordWrap = true, CanFocus = false,
                Text = slot.Content
            });
            _widgetStack.Add(frame);
            y += 4;
        }
    }
}
