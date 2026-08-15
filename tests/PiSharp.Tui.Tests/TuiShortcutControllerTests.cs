using System.Diagnostics;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiShortcutControllerTests
{
    [Fact]
    public async Task RefreshExtensionShortcutsRejectsInvalidAndBuiltInConflictingShortcuts()
    {
        var errors = new List<string>();
        var controller = new TuiShortcutController(new TuiShortcutControllerOptions(
            () =>
            [
                Registration("extension:bad", "ctrl+space"),
                Registration("extension:conflict", "ctrl+l"),
                Registration("extension:ok", "ctrl+k")
            ],
            NoExtensionUi.Instance,
            errors.Add));

        await controller.RefreshExtensionShortcutsAsync();

        var bindings = controller.BuildExtensionShortcutBindings();
        var binding = Assert.Single(bindings);
        Assert.Equal("extension:ok", binding.SourceId);
        Assert.Equal("ctrl+k", binding.Keys);
        Assert.Contains(Key.K.WithCtrl, binding.TerminalKeys);
        Assert.Contains(errors, error => error.Contains("Invalid extension shortcut 'ctrl+space'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("conflicts with built-in shortcut", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshExtensionShortcutsProducesInvokableBindings()
    {
        string? capturedArgs = null;
        var controller = new TuiShortcutController(new TuiShortcutControllerOptions(
            () => [new OwnedExtensionRegistration<ExtensionShortcutRegistration>(
                "shortcut:ctrl+k:extension:test",
                "extension:test",
                new ExtensionShortcutRegistration("ctrl+k", "Run test", (args, _) =>
                {
                    capturedArgs = args;
                    return Task.CompletedTask;
                }))],
            NoExtensionUi.Instance,
            _ => { }));

        await controller.RefreshExtensionShortcutsAsync();

        var binding = Assert.Single(controller.BuildExtensionShortcutBindings());
        await binding.InvokeAsync(CancellationToken.None);

        Assert.Equal(string.Empty, capturedArgs);
    }

    [Fact]
    public async Task BuildExtensionShortcutBindingsNeverConsultsSourceSynchronously()
    {
        var calls = 0;
        var controller = new TuiShortcutController(new TuiShortcutControllerOptions(
            () =>
            {
                calls++;
                return [Registration("extension:ok", "ctrl+k")];
            },
            NoExtensionUi.Instance,
            _ => { }));

        // The key path must read only the cache and never trigger a (potentially remote) fetch.
        Assert.Empty(controller.BuildExtensionShortcutBindings());
        Assert.Empty(controller.BuildExtensionShortcutBindings());
        Assert.Equal(0, calls);

        // A refresh populates the cache and the key path returns it.
        await controller.RefreshExtensionShortcutsAsync();
        Assert.Single(controller.BuildExtensionShortcutBindings());
        Assert.Equal(1, calls);

        // Invalidation clears the cache but must not re-read the source from the key path.
        controller.InvalidateExtensionShortcuts();
        Assert.Empty(controller.BuildExtensionShortcutBindings());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BuildExtensionShortcutBindingsDoesNotBlockWhenSourceIsSlow()
    {
        var controller = new TuiShortcutController(new TuiShortcutControllerOptions(
            () =>
            {
                // Simulate a remote source that is slow to respond — the key path must never wait on it.
                Thread.Sleep(2000);
                return [];
            },
            NoExtensionUi.Instance,
            _ => { }));

        var stopwatch = Stopwatch.StartNew();
        var bindings = controller.BuildExtensionShortcutBindings();
        stopwatch.Stop();

        Assert.Empty(bindings);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Key path blocked on the shortcut source for {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
    }

    private static OwnedExtensionRegistration<ExtensionShortcutRegistration> Registration(string sourceId, string keys)
        => new($"shortcut:{keys}:{sourceId}", sourceId, new ExtensionShortcutRegistration(keys, "Run", (_, _) => Task.CompletedTask));
}
