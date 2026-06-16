using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class PromptEditorPasteHandlerTests
{
    [Fact]
    public void TryHandleTextCollectsBracketedPasteUntilEndMarker()
    {
        var handler = new PromptEditorPasteHandler();

        Assert.True(handler.TryHandleText("\u001b[200~one", out var firstInsert));
        Assert.Null(firstInsert);

        Assert.True(handler.TryHandleText("\ntwo\u001b[201~", out var secondInsert));
        Assert.Equal("one\ntwo", secondInsert);
    }

    [Fact]
    public void TryHandleMarkerKeyStartsAndStopsTerminalGuiCoercedBracketedPaste()
    {
        var handler = new PromptEditorPasteHandler();

        Assert.True(handler.TryHandleMarkerKey(new Key(KeyCode.Insert), out var startInsert));
        Assert.Null(startInsert);
        Assert.True(handler.TryHandleText("one\ntwo", out var textInsert));
        Assert.Null(textInsert);

        Assert.True(handler.TryHandleMarkerKey(new Key(KeyCode.Insert), out var endInsert));
        Assert.Equal("one\ntwo", endInsert);
    }

    [Fact]
    public void TryHandleTextReturnsFalseForPlainTextWhenNoPasteIsPending()
    {
        var handler = new PromptEditorPasteHandler();

        Assert.False(handler.TryHandleText("abc", out var insert));
        Assert.Null(insert);
    }

    [Fact]
    public void TryHandleTextConsumesPartialStartMarkerAfterEscapeKey()
    {
        var handler = new PromptEditorPasteHandler();

        Assert.True(handler.TryHandleEscapeKey(Key.Esc));
        Assert.True(handler.TryHandleText("[20", out var insert));

        Assert.Null(insert);
    }
}
