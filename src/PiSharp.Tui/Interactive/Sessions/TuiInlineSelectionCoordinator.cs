using PiSharp.Tui.Interactive.Components;

namespace PiSharp.Tui.Interactive.Sessions;

internal sealed class TuiInlineSelectionCoordinator : IDisposable
{
    private readonly PromptEditor _prompt;
    private readonly Action _scheduleRender;
    private readonly Action<Action> _dispatch;
    private bool _disposed;

    private InlineSelectionSession? _selectionSession;
    private TaskCompletionSource<string?>? _selectionCompletion;
    private CancellationTokenRegistration? _selectionRegistration;

    public InlineSelectionSession? CurrentSession => _selectionSession;

    public TuiInlineSelectionCoordinator(PromptEditor prompt, Action scheduleRender, Action<Action> dispatch)
    {
        _prompt = prompt;
        _scheduleRender = scheduleRender;
        _dispatch = dispatch;
    }

    public Task<string?> SelectInlineAsync(string title, IReadOnlyList<string> choices, CancellationToken token)
    {
        if (choices.Count == 0) return Task.FromResult<string?>(null);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => _dispatch(CancelInlineSelection));
        _dispatch(() => BeginInlineSelection(new InlineSelectionSession(title, choices), completion, registration));
        return completion.Task;
    }

    public void BeginInlineSelection(InlineSelectionSession session, TaskCompletionSource<string?> completion, CancellationTokenRegistration registration)
    {
        CancelInlineSelection();
        _selectionSession = session;
        _selectionCompletion = completion;
        _selectionRegistration = registration;
        _prompt.AllowEmptySubmit = true;
        _prompt.AcceptedSuggestionSuffix = string.Empty;
        _prompt.SetPromptText(string.Empty);
        _prompt.FocusAtEnd();
        _scheduleRender();
    }

    public bool CompleteInlineSelection(string text)
    {
        if (_selectionSession is null) return false;
        var selected = _selectionSession.Resolve(text);
        var completion = _selectionCompletion;
        EndInlineSelection(clearPrompt: false);
        completion?.TrySetResult(selected);
        return true;
    }

    public void CancelInlineSelection()
    {
        if (_selectionSession is null) return;
        var completion = _selectionCompletion;
        EndInlineSelection(clearPrompt: true);
        completion?.TrySetResult(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelInlineSelection();
    }

    private void EndInlineSelection(bool clearPrompt)
    {
        _selectionSession = null;
        _selectionCompletion = null;
        _selectionRegistration?.Dispose();
        _selectionRegistration = null;
        _prompt.AllowEmptySubmit = false;
        _prompt.AcceptedSuggestionSuffix = " ";
        if (clearPrompt) _prompt.SetPromptText(string.Empty);
        _scheduleRender();
    }
}
