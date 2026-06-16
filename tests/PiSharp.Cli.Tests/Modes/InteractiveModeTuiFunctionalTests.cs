using System.Text;
using PiSharp.Cli.Modes;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Cli.Tests.Modes;

public sealed class InteractiveModeTuiFunctionalTests
{
    [Fact]
    public async Task ModelCommandTypedThroughTuiOpensModelSelectorWhenExtensionCommandCollides()
    {
        var extensionManager = new ExtensionManager();
        extensionManager.Registry.RegisterCommand("test-extension", new ExtensionCommandRegistration(
            "model",
            "conflicting extension model command",
            (_, _) => Task.CompletedTask));
        var runtime = await ModeTestRuntime.CreateAsync(extensionManager: extensionManager);
        var driver = new FakeDriver();
        driver.SetWindowSize(100, 30);
        var terminal = new RecordingTerminalScreenSession();
        var ready = new TaskCompletionSource<TuiHostRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = InteractiveMode.CreateTuiHostOptions(runtime) with
        {
            ConsoleDriver = driver,
            TerminalScreenSession = terminal,
            BeforeRunAsync = (context, _) =>
            {
                ready.TrySetResult(context);
                return Task.CompletedTask;
            }
        };
        var host = new TuiHost(options);
        using var runCancellation = new CancellationTokenSource();
        var runTask = Task.Run(() => host.RunAsync(runCancellation.Token), CancellationToken.None);

        try
        {
            var context = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await TypeApplicationTextAsync("/model");
            await SendApplicationKeyAsync(Key.Enter);

            await WaitUntilAsync(
                () => ExtractScreenText(driver).Contains("Select model", StringComparison.Ordinal),
                driver);

            Application.Invoke(() => Application.RequestStop(context.Window));
            Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (!runTask.IsCompleted)
            {
                runCancellation.Cancel();
                Application.Invoke(() => Application.RequestStop());
                await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));
            }
        }
    }

    private static async Task TypeApplicationTextAsync(string text)
    {
        foreach (var ch in text)
            await SendApplicationKeyAsync(new Key((KeyCode)ch));
    }

    private static async Task SendApplicationKeyAsync(Key key)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Invoke(() =>
        {
            try
            {
                Application.RaiseKeyDownEvent(key);
                sent.TrySetResult();
            }
            catch (Exception ex)
            {
                sent.TrySetException(ex);
            }
        });
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, FakeDriver driver)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition not met within 5 seconds. Screen:\n{ExtractScreenText(driver)}");
            await Task.Delay(50);
        }
    }

    private static string ExtractScreenText(FakeDriver driver)
    {
        var contents = driver.Contents!;
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var line = new StringBuilder(driver.Cols);
            for (var column = 0; column < driver.Cols; column++)
            {
                var rune = contents[row, column].Rune;
                line.Append(rune.Value == 0 ? ' ' : rune.ToString());
            }
            lines.Add(line.ToString().TrimEnd());
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines);
    }

    private sealed class RecordingTerminalScreenSession : ITerminalScreenSession
    {
        public void Enter()
        {
        }

        public void RestoreBracketedPaste()
        {
        }

        public void Exit()
        {
        }

        public void Dispose()
        {
        }
    }
}
