using PiSharp.Tui.Interactive.Components;

namespace PiSharp.Tui.Interactive;

public static class TuiRenderRequestRouter
{
    public static IDisposable ConnectPromptSuggestions(PromptEditor prompt, Action scheduleRender)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(scheduleRender);

        void HandleSuggestionsChanged() => scheduleRender();

        prompt.SuggestionsChanged += HandleSuggestionsChanged;
        return new Subscription(() => prompt.SuggestionsChanged -= HandleSuggestionsChanged);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            dispose();
        }
    }
}
