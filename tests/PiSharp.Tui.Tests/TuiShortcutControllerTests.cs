using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiShortcutControllerTests
{
    [Fact]
    public void BuildExtensionShortcutBindingsRejectsInvalidAndBuiltInConflictingShortcuts()
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

        var bindings = controller.BuildExtensionShortcutBindings();

        var binding = Assert.Single(bindings);
        Assert.Equal("extension:ok", binding.SourceId);
        Assert.Equal("ctrl+k", binding.Keys);
        Assert.Contains(Key.K.WithCtrl, binding.TerminalKeys);
        Assert.Contains(errors, error => error.Contains("Invalid extension shortcut 'ctrl+space'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("conflicts with built-in shortcut", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildExtensionShortcutBindingsInvokesRegisteredShortcutHandler()
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

        var binding = Assert.Single(controller.BuildExtensionShortcutBindings());
        await binding.InvokeAsync(CancellationToken.None);

        Assert.Equal(string.Empty, capturedArgs);
    }

    private static OwnedExtensionRegistration<ExtensionShortcutRegistration> Registration(string sourceId, string keys)
        => new($"shortcut:{keys}:{sourceId}", sourceId, new ExtensionShortcutRegistration(keys, "Run", (_, _) => Task.CompletedTask));
}
