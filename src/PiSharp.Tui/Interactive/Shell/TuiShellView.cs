using Microsoft.Extensions.Logging;
using PiSharp.Tui.Interactive;
using Terminal.Gui;

using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Theme;

namespace PiSharp.Tui.Interactive.Shell;

internal sealed class TuiShellView
{
    public const int InlineSuggestionHeight = 10;
    public const int MenuBarHeight = 1;
    public const int SidebarWidthPercent = 25;

    public Window Window { get; }
    public MenuBar MenuBar { get; }
    public TuiSidebarView LeftSidebar { get; }
    public TuiSidebarView RightSidebar { get; }
    public HeaderView Header { get; }
    public ChatView Chat { get; }
    public WorkingIndicatorView WorkingIndicator { get; }
    public TextView PromptTitle { get; }
    public PromptEditor Prompt { get; }
    public LineView PromptBottomBorder { get; }
    public TextView EditorComponent { get; }
    public InlineSuggestionListView Suggestions { get; }
    public FooterView Footer { get; }
    public ScrollBar ChatScrollBar { get; }
    internal TuiProfilingCounters? ProfilingCounters { get; set; }

    private TuiBridgeSlot? _editorComponentSlot;

    public TuiShellView(ILoggerFactory? loggerFactory = null)
    {
        Window = new Window
        {
            Title = string.Empty,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            BorderStyle = LineStyle.None,
            ColorScheme = TuiTheme.DefaultColorScheme
        };
        MenuBar = new MenuBar
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Menus = []
        };
        Header = new HeaderView
        {
            X = 0,
            Y = MenuBarHeight,
            Width = Dim.Fill(),
            Height = 3
        };
        Chat = new ChatView
        {
            X = Pos.Absolute(ChatView.ChatHorizontalGutter),
            Y = 3 + MenuBarHeight,
            Width = Dim.Fill(1 + ChatView.ChatHorizontalGutter),
            Height = Dim.Fill(7)
        };
        WorkingIndicator = new WorkingIndicatorView
        {
            X = 0,
            Y = Pos.AnchorEnd(8),
            Width = Dim.Fill(),
            Height = 1
        };
        PromptTitle = new TextView
        {
            X = 0,
            Y = Pos.AnchorEnd(7),
            Width = Dim.Fill(),
            Height = 1,
            ReadOnly = true,
            WordWrap = false,
            CanFocus = false
        };
        Prompt = new PromptEditor(loggerFactory: loggerFactory)
        {
            X = 0,
            Y = Pos.AnchorEnd(6),
            Width = Dim.Fill(),
            Height = 3
        };
        PromptBottomBorder = new LineView(Orientation.Horizontal)
        {
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 1
        };
        // Backing editor shown in place of the prompt title/prompt/bottom-border while an
        // editor-placement bridge slot is active. Hidden (not destroyed) so the default prompt
        // editor is restored when the slot is removed.
        EditorComponent = new TextView
        {
            X = 0,
            Y = Pos.AnchorEnd(6),
            Width = Dim.Fill(),
            Height = 3,
            Multiline = true,
            WordWrap = true,
            Visible = false,
            ColorScheme = TuiTheme.DefaultColorScheme
        };
        Footer = new FooterView(loggerFactory)
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 2
        };
        Suggestions = new InlineSuggestionListView
        {
            X = 1,
            Y = Pos.AnchorEnd(2 + InlineSuggestionHeight),
            Width = Dim.Fill(2),
            Height = InlineSuggestionHeight,
            ColorScheme = TuiTheme.PopupColorScheme,
            Visible = false
        };
        ChatScrollBar = new ScrollBar
        {
            X = Pos.AnchorEnd(ChatView.ChatHorizontalGutter + 1),
            Y = 3 + MenuBarHeight,
            Width = 1,
            Height = Dim.Fill(7),
            AutoShow = true,
            CanFocus = false
        };
        LeftSidebar = new TuiSidebarView(TuiSidebarSide.Left, path => Prompt.InsertText(path + " "))
        {
            X = 0,
            Y = MenuBarHeight,
            Width = Dim.Percent(SidebarWidthPercent),
            Height = Dim.Fill(7)
        };
        RightSidebar = new TuiSidebarView(TuiSidebarSide.Right)
        {
            X = Pos.Percent(100 - SidebarWidthPercent),
            Y = MenuBarHeight,
            Width = Dim.Percent(SidebarWidthPercent),
            Height = Dim.Fill(7)
        };

        Header.ColorScheme = TuiTheme.DefaultColorScheme;
        Chat.ColorScheme = TuiTheme.DefaultColorScheme;
        WorkingIndicator.ColorScheme = TuiTheme.DefaultColorScheme;
        PromptTitle.ColorScheme = TuiTheme.PromptBorderColorScheme;
        Prompt.ColorScheme = TuiTheme.DefaultColorScheme;
        PromptBottomBorder.ColorScheme = TuiTheme.PromptBorderColorScheme;
        Footer.ColorScheme = TuiTheme.DefaultColorScheme;

        Window.Add(LeftSidebar);
        Window.Add(RightSidebar);
        Window.Add(Header);
        Window.Add(Chat);
        Window.Add(ChatScrollBar);
        Window.Add(WorkingIndicator);
        Window.Add(PromptTitle);
        Window.Add(PromptBottomBorder);
        Window.Add(Prompt);
        Window.Add(EditorComponent);
        Window.Add(Suggestions);
        Window.Add(Footer);
        Window.Add(MenuBar);

        ChatScrollBar.PositionChanged += (_, e) => Chat.ScrollTo(e.CurrentValue);
    }

    public void SetSidebarVisibility(bool leftVisible, bool rightVisible)
    {
        LeftSidebar.Visible = leftVisible;
        RightSidebar.Visible = rightVisible;
    }

    public bool HasActiveEditorComponent => _editorComponentSlot is not null;

    /// <summary>
    /// Shows the editor-placement slot's content in place of the default prompt editor, or hides
    /// it and restores the prompt when <paramref name="slot"/> is null. The prompt is hidden, not
    /// destroyed, so all its wiring (suggestions, submission, history) survives.
    /// </summary>
    public void SetEditorComponentSlot(TuiBridgeSlot? slot)
    {
        _editorComponentSlot = slot;
        var active = slot is not null;
        PromptTitle.Visible = !active;
        Prompt.Visible = !active;
        PromptBottomBorder.Visible = !active;
        EditorComponent.Visible = active;
        if (active && !string.Equals(EditorComponent.Text?.ToString(), slot!.Content, StringComparison.Ordinal))
            EditorComponent.Text = slot.Content;
    }

    public void ApplyLayout(TuiShellLayoutMetrics metrics)
    {
        ProfilingCounters?.Increment(TuiProfilingCounterNames.LayoutApply);

        var promptHeight = metrics.PromptHeight;
        var footerHeight = metrics.FooterHeight;
        var bottomReserved = metrics.BottomReserved;
        var suggestionsVisible = metrics.SuggestionsVisible;
        var suggestionsHeight = metrics.SuggestionsHeight;

        Header.Y = MenuBarHeight;
        Chat.Y = metrics.HeaderHeight + MenuBarHeight;
        Chat.Height = Dim.Fill(bottomReserved);
        ChatScrollBar.Y = metrics.HeaderHeight + MenuBarHeight;
        ChatScrollBar.Height = Dim.Fill(bottomReserved);

        LeftSidebar.Height = Dim.Fill(bottomReserved);
        RightSidebar.Height = Dim.Fill(bottomReserved);

        Chat.X = LeftSidebar.Visible ? Pos.Right(LeftSidebar) : Pos.Absolute(ChatView.ChatHorizontalGutter);
        Chat.Width = Dim.Fill((LeftSidebar.Visible ? 1 : ChatView.ChatHorizontalGutter + 1) + (RightSidebar.Visible ? (int)(Window.Frame.Width * SidebarWidthPercent / 100.0) : 0));
        ChatScrollBar.X = RightSidebar.Visible
            ? Pos.Left(RightSidebar) - 1
            : LeftSidebar.Visible ? Pos.AnchorEnd(1) : Pos.AnchorEnd(ChatView.ChatHorizontalGutter + 1);

        Footer.Y = Pos.AnchorEnd(footerHeight);
        PromptBottomBorder.Y = Pos.AnchorEnd(footerHeight + TuiLayoutMetrics.PromptBorderHeight);
        Prompt.Height = promptHeight;
        Prompt.Y = Pos.AnchorEnd(footerHeight + TuiLayoutMetrics.PromptBorderHeight + promptHeight);
        PromptTitle.Y = Pos.AnchorEnd(footerHeight + TuiLayoutMetrics.PromptBorderHeight + promptHeight + TuiLayoutMetrics.PromptTitleHeight);
        // The editor-placement slot occupies the same region as the hidden prompt trio.
        EditorComponent.Y = Pos.AnchorEnd(footerHeight + TuiLayoutMetrics.PromptBorderHeight + promptHeight + TuiLayoutMetrics.PromptTitleHeight);
        EditorComponent.Height = promptHeight + TuiLayoutMetrics.PromptBorderHeight + TuiLayoutMetrics.PromptTitleHeight;
        Suggestions.Height = suggestionsHeight;
        Suggestions.Y = Pos.AnchorEnd(footerHeight + TuiLayoutMetrics.PromptBorderHeight + promptHeight + TuiLayoutMetrics.PromptTitleHeight + suggestionsHeight);
        WorkingIndicator.Y = Pos.AnchorEnd(bottomReserved);
    }
}
