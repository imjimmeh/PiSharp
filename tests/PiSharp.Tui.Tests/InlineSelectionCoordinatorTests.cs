using PiSharp.Tui.Interactive.Components;
using PiSharp.Tui.Interactive.Sessions;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class InlineSelectionCoordinatorTests
{
    [Fact]
    public async Task CompletingInlineSelectionDoesNotClearPromptText()
    {
        var prompt = new PromptEditor();
        var renderCount = 0;
        var coordinator = new TuiInlineSelectionCoordinator(prompt, () => renderCount++, action => action());

        var selectionTask = coordinator.SelectInlineAsync("Select model", ["alpha", "beta"], CancellationToken.None);
        prompt.SetPromptText("alp");

        var completed = coordinator.CompleteInlineSelection(prompt.PromptText);
        var result = await selectionTask;

        Assert.True(completed);
        Assert.Equal("alpha", result);
        Assert.Equal("alp", prompt.PromptText);
        Assert.True(renderCount >= 2);
    }
}
