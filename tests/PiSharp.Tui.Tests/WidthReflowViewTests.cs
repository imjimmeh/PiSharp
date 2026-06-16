using System.Drawing;
using PiSharp.Tui.Interactive.Components;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class WidthReflowViewTests
{
    [Fact]
    public void TrackRenderWidthInvokesReflowOnlyWhenFrameWidthChanges()
    {
        var view = new TestWidthReflowView { Frame = new Rectangle(0, 0, 30, 4) };

        view.Render();
        view.Frame = new Rectangle(0, 0, 30, 6);
        view.Frame = new Rectangle(0, 0, 12, 6);

        Assert.Equal(2, view.RenderCount);
        Assert.Equal(12, view.LastWidth);
    }

    private sealed class TestWidthReflowView : WidthReflowView
    {
        public int RenderCount { get; private set; }
        public int LastWidth { get; private set; }

        public TestWidthReflowView() : base(fallbackWidth: 80)
        {
        }

        public void Render()
        {
            RenderCount++;
            LastWidth = TrackRenderWidth(Render);
        }
    }
}
