namespace PiSharp.Tui.Interactive.Components;

internal interface IPromptEditorSurface
{
    string PromptText { get; }
    int CursorOffset { get; }
    void SetPromptText(string text);
    void SetCursorOffset(int offset);
    void FocusAtEnd();
    void InsertAtEnd(string text);
    bool MoveCursorVertical(int delta);
}

internal sealed class PromptEditorController(IPromptEditorSurface surface)
{
    private const int MaxPromptHistory = 200;
    private readonly PromptHistory _history = new(MaxPromptHistory);
    private readonly PromptSuggestionState _suggestions = new();
    private CancellationTokenSource? _asyncCompletionCancellation;
    private int _asyncCompletionVersion;
    private bool _allowEmptySubmit;
    private string _acceptedSuggestionSuffix = " ";
    private IPromptEditorMode _mode = new SlashCommandPromptMode(" ");

    public event Func<string, CancellationToken, Task>? Submitted;
    public event Action? SuggestionsChanged;

    public Func<string, int, IReadOnlyList<PromptCompletion>>? Complete { get; set; }
    public Func<string, int, CancellationToken, Task<IReadOnlyList<PromptCompletion>>>? CompleteAsync { get; set; }
    public Action<Action>? PostSuggestionUpdate { get; set; }
    public TimeSpan AsyncCompletionDebounceDelay { get; set; } = TimeSpan.FromMilliseconds(25);

    public Func<string, IReadOnlyList<string>>? CompleteText
    {
        set => Complete = value is null ? null : (text, _) => value(text).Select(item => new PromptCompletion(item, item, Prefix: text, AppendSpace: !string.IsNullOrEmpty(_acceptedSuggestionSuffix))).ToArray();
    }

    public bool AllowEmptySubmit
    {
        get => _allowEmptySubmit;
        set
        {
            if (_allowEmptySubmit == value) return;
            _allowEmptySubmit = value;
            RebuildMode();
        }
    }

    public string AcceptedSuggestionSuffix
    {
        get => _acceptedSuggestionSuffix;
        set
        {
            if (string.Equals(_acceptedSuggestionSuffix, value, StringComparison.Ordinal)) return;
            _acceptedSuggestionSuffix = value;
            RebuildMode();
        }
    }

    public bool IsSubmitting { get; private set; }
    public IReadOnlyList<PromptCompletion> Completions => _suggestions.Suggestions;
    public IReadOnlyList<string> Suggestions => _suggestions.Suggestions.Select(suggestion => suggestion.Value).ToArray();
    public int SelectedSuggestionIndex => _suggestions.SelectedIndex;
    public PromptCompletion? SelectedCompletion => _suggestions.SelectedCompletion;
    public string? SelectedSuggestion => _suggestions.SelectedSuggestion;
    public IReadOnlyList<string> PromptHistory => _history.Entries;

    public void SetPromptText(string text)
    {
        surface.SetPromptText(text);
        _history.ResetNavigation();
        RefreshSuggestions();
    }

    public void InsertAtEnd(string text)
    {
        surface.FocusAtEnd();
        surface.InsertAtEnd(text);
    }

    public void RecordSubmittedPrompt(string text) => _history.Record(text);

    public bool MovePromptHistory(int delta)
    {
        if (!_history.TryMove(surface.PromptText, delta, out var text)) return false;

        surface.SetPromptText(text);
        surface.FocusAtEnd();
        RefreshSuggestions();
        return true;
    }

    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        if (IsSubmitting) return;

        var text = surface.PromptText.Trim();
        if (_mode.ShouldSubmitSelectedSuggestion(text, SelectedCompletion) && !HasExactSuggestion(text))
        {
            text = SelectedCompletion!.Value;
        }

        if ((!_mode.AllowsEmptySubmit && text.Length == 0) || Submitted is null) return;

        IsSubmitting = true;
        var submittedText = text;
        InvalidateAsyncSuggestionRefresh();
        surface.SetPromptText(string.Empty);
        if (_suggestions.Clear()) SuggestionsChanged?.Invoke();
        _history.ResetNavigation();
        IsSubmitting = false;
        try
        {
            await Submitted(text, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            surface.SetPromptText(submittedText);
            surface.FocusAtEnd();
            RefreshSuggestions();
            throw;
        }
    }

    public void ClearEditor()
    {
        surface.SetPromptText(string.Empty);
        _history.ResetNavigation();
        RefreshSuggestions();
    }

    public bool TryClearEditor()
    {
        if (TryDismissSuggestions()) return true;
        if (string.IsNullOrEmpty(surface.PromptText)) return false;
        ClearEditor();
        return true;
    }

    public bool TryDismissSuggestions()
    {
        if (!_suggestions.Clear()) return false;
        SuggestionsChanged?.Invoke();
        return true;
    }

    public void RefreshSuggestions()
    {
        if (CompleteAsync is not null)
        {
            ScheduleAsyncSuggestionRefresh(surface.PromptText, surface.CursorOffset);
            return;
        }

        CancelAsyncSuggestionRefresh();
        if (_suggestions.Refresh(surface.PromptText, surface.CursorOffset, Complete)) SuggestionsChanged?.Invoke();
    }

    private void ScheduleAsyncSuggestionRefresh(string text, int cursorOffset)
    {
        CancelAsyncSuggestionRefresh();
        var cancellation = new CancellationTokenSource();
        _asyncCompletionCancellation = cancellation;
        var version = unchecked(++_asyncCompletionVersion);
        _ = RefreshSuggestionsAsync(text, cursorOffset, version, cancellation);
    }

    private void CancelAsyncSuggestionRefresh()
    {
        if (_asyncCompletionCancellation is null) return;

        _asyncCompletionCancellation.Cancel();
        _asyncCompletionCancellation = null;
    }

    private void InvalidateAsyncSuggestionRefresh()
    {
        CancelAsyncSuggestionRefresh();
        unchecked
        {
            _asyncCompletionVersion++;
        }
    }

    private async Task RefreshSuggestionsAsync(string text, int cursorOffset, int version, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            if (AsyncCompletionDebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(AsyncCompletionDebounceDelay, cancellationToken).ConfigureAwait(false);
            }

            var completeAsync = CompleteAsync;
            if (completeAsync is null) return;

            var next = await completeAsync(text, cursorOffset, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || version != _asyncCompletionVersion) return;

            PostSuggestionUpdateOrApply(() => ApplyAsyncSuggestions(next, version, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_asyncCompletionCancellation, cancellation)) _asyncCompletionCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PostSuggestionUpdateOrApply(Action action)
    {
        var post = PostSuggestionUpdate;
        if (post is null)
        {
            action();
            return;
        }

        post(action);
    }

    private void ApplyAsyncSuggestions(IReadOnlyList<PromptCompletion> next, int version, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || version != _asyncCompletionVersion) return;
        if (_suggestions.Replace(next)) SuggestionsChanged?.Invoke();
    }

    private bool HasExactSuggestion(string text)
        => _suggestions.Suggestions.Any(suggestion => string.Equals(suggestion.Value, text, StringComparison.OrdinalIgnoreCase));

    public void AcceptFirstSuggestion()
    {
        if (CompleteAsync is null) RefreshSuggestions();
        if (SelectedCompletion is not null)
        {
            var completion = SelectedCompletion;
            var prompt = surface.PromptText;
            var cursor = Math.Clamp(surface.CursorOffset, 0, prompt.Length);
            var prefix = completion.Prefix;
            var start = prefix.Length > 0 && cursor >= prefix.Length && string.Equals(prompt.Substring(cursor - prefix.Length, prefix.Length), prefix, StringComparison.Ordinal)
                ? cursor - prefix.Length
                : 0;
            var suffix = completion.AppendSpace ? _mode.AcceptedSuggestionSuffix : string.Empty;
            surface.SetPromptText(prompt[..start] + completion.Value + suffix + prompt[cursor..]);
            surface.SetCursorOffset(start + completion.Value.Length + suffix.Length);
        }
        HandleTextEdited();
    }

    public void MoveSuggestionSelection(int delta)
    {
        if (CompleteAsync is null) RefreshSuggestions();
        if (_suggestions.MoveSelection(delta)) SuggestionsChanged?.Invoke();
    }

    public bool TryHandleVerticalNavigation(int delta)
    {
        if (_mode.ShouldMoveSuggestions(surface.PromptText, Suggestions.Count))
        {
            MoveSuggestionSelection(delta);
            return true;
        }

        if (string.IsNullOrWhiteSpace(surface.PromptText) || _history.IsNavigating) return MovePromptHistory(delta);
        return surface.MoveCursorVertical(delta);
    }

    public void HandleTextEdited()
    {
        _history.ResetNavigation();
        RefreshSuggestions();
    }

    private void RebuildMode()
    {
        _mode = _allowEmptySubmit
            ? new InlineSelectionPromptMode(_acceptedSuggestionSuffix)
            : new SlashCommandPromptMode(_acceptedSuggestionSuffix);
    }
}
