using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public static class PromptDialog
{
    public static Task<string?> InputAsync(string prompt, CancellationToken cancellationToken = default, ITuiDispatcher? dispatcher = null)
        => InputAsync(prompt, null, cancellationToken, dispatcher);

    public static Task<string?> InputAsync(string prompt, string? initialValue, CancellationToken cancellationToken = default, ITuiDispatcher? dispatcher = null)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<string?>(cancellationToken);

        var appContext = dispatcher as ITuiApplicationContext ?? new TerminalGuiApplicationContext(dispatcher ?? TerminalGuiDispatcher.Instance);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        appContext.Post(() =>
        {
            try
            {
                tcs.TrySetResult(RunInputDialog(appContext, prompt, initialValue));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return CompleteAndDisposeRegistrationAsync(tcs.Task, registration);
    }

    private static async Task<string?> CompleteAndDisposeRegistrationAsync(Task<string?> inputTask, CancellationTokenRegistration registration)
    {
        try
        {
            return await inputTask.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? RunInputDialog(ITuiApplicationContext appContext, string prompt, string? initialValue)
    {
        string? result = null;
        var dialog = new Window
        {
            Title = prompt,
            Width = Dim.Percent(80),
            Height = 6,
            ColorScheme = Theme.TuiTheme.PopupColorScheme
        };

        var hint = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 2,
            ReadOnly = true,
            WordWrap = false,
            Text = "Enter to submit, Esc to cancel."
        };
        var input = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Height = 1,
            Text = initialValue ?? string.Empty
        };

        void Accept()
        {
            result = input.Text?.ToString() ?? string.Empty;
            appContext.RequestStop(dialog);
        }

        void Cancel()
        {
            result = null;
            appContext.RequestStop(dialog);
        }

        input.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Accept();
            }
            else if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                Cancel();
            }
        };
        dialog.KeyDown += (_, key) =>
        {
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;
                Accept();
            }
            else if (key.KeyCode == KeyCode.Esc)
            {
                key.Handled = true;
                Cancel();
            }
        };

        dialog.Add(hint, input);
        input.SetFocus();
        appContext.Run(dialog);
        dialog.Dispose();
        return result;
    }
}
