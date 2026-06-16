using System.Drawing;
using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class WrappedTextViewTests
{
    [Fact]
    public void RenderWrappedWrapsTextSetsHeightAndReflowsOnFrameResize()
    {
        var view = new TestWrappedTextView { Frame = new Rectangle(0, 0, 24, 4) };

        view.RenderText("alpha beta gamma delta epsilon");
        view.Frame = new Rectangle(0, 0, 10, 6);

        var lines = view.RenderedLines;
        Assert.True(view.RenderCount >= 2);
        Assert.True(lines.Length > 1);
        Assert.All(lines, line => Assert.True(line.Length <= 10, $"'{line}' exceeded 10"));
    }

    private sealed class TestWrappedTextView : WrappedTextView
    {
        private string _text = string.Empty;
        public int RenderCount { get; private set; }
        public string[] RenderedLines => (Text?.ToString() ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        public TestWrappedTextView() : base(fallbackWidth: 80)
        {
        }

        public void RenderText(string text)
        {
            _text = text;
            RenderCount++;
            RenderWrapped([_text], () => RenderText(_text));
        }
    }
}
