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

    [Fact]
    public async Task CancellingSelectionDoesNotLeaveStaleSuggestionsForNextSubmit()
    {
        var prompt = new PromptEditor();
        var posted = new List<Action>();
        prompt.PostSuggestionUpdate = posted.Add;
        prompt.AsyncCompletionDebounceDelay = TimeSpan.Zero;

        var coordinator = new TuiInlineSelectionCoordinator(prompt, () => { }, action => action());
        string? submitted = null;
        prompt.Submitted += (text, _) =>
        {
            submitted = text;
            return Task.CompletedTask;
        };
        prompt.CompleteAsync = (text, _, _) => Task.FromResult<IReadOnlyList<PromptCompletion>>(
            coordinator.CurrentSession is not null
                ? coordinator.CurrentSession.Complete(text).Select(value => new PromptCompletion(value, value, Prefix: text)).ToArray()
                : Array.Empty<PromptCompletion>());

        var selectionTask = coordinator.SelectInlineAsync("Select model", ["openai/gpt-4o", "openai/gpt-4o-mini"], CancellationToken.None);

        foreach (var apply in posted) apply();
        posted.Clear();
        Assert.Equal("openai/gpt-4o", prompt.SelectedSuggestion);

        coordinator.CancelInlineSelection();
        posted.Clear();

        prompt.SetPromptText("/model");
        await prompt.SubmitAsync();

        Assert.Equal("/model", submitted);
        Assert.Null(await selectionTask);
    }
}
