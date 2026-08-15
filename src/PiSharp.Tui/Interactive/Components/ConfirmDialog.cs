using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Components;

public static class ConfirmDialog
{
    public static Task<bool> ConfirmAsync(string title, string? message, CancellationToken cancellationToken = default, ITuiDispatcher? dispatcher = null)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<bool>(cancellationToken);

        var appContext = dispatcher as ITuiApplicationContext ?? new TerminalGuiApplicationContext(dispatcher ?? TerminalGuiDispatcher.Instance);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        appContext.Post(() =>
        {
            try
            {
                tcs.TrySetResult(RunConfirmDialog(appContext, title, message));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return CompleteAndDisposeRegistrationAsync(tcs.Task, registration);
    }

    private static async Task<bool> CompleteAndDisposeRegistrationAsync(Task<bool> confirmTask, CancellationTokenRegistration registration)
    {
        try
        {
            return await confirmTask.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool RunConfirmDialog(ITuiApplicationContext appContext, string title, string? message)
    {
        var confirmed = false;
        var dialog = new Window
        {
            Title = title,
            Width = Dim.Percent(80),
            Height = 8,
            ColorScheme = Theme.TuiTheme.PopupColorScheme
        };

        var prompt = new TextView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(2),
            Height = 4,
            ReadOnly = true,
            WordWrap = true,
            Text = message ?? string.Empty
        };
        var hint = new TextView
        {
            X = 1,
            Y = 5,
            Width = Dim.Fill(2),
            Height = 1,
            ReadOnly = true,
            WordWrap = false,
            Text = "Enter to confirm, Esc to cancel."
        };

        void Accept()
        {
            confirmed = true;
            appContext.RequestStop(dialog);
        }

        void Cancel()
        {
            confirmed = false;
            appContext.RequestStop(dialog);
        }

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

        dialog.Add(prompt, hint);
        dialog.SetFocus();
        appContext.Run(dialog);
        dialog.Dispose();
        return confirmed;
    }
}
