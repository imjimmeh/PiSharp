using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Keybindings;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class KeybindingsWatcherTests
{
    [Fact]
    public async Task ReloadOnFileChange_UpdatesStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pisharp-kbw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "keybindings.json");
        File.WriteAllText(path, """{ "keybindings": { "tools": "Ctrl+O" } }""");
        var store = new TuiKeybindingStore(TuiBuiltInShortcutCatalog.Bindings);
        try
        {
            using var watcher = new KeybindingsWatcher(path, store, action => action(), _ => { });

            // Watcher does no initial load; store holds built-in defaults until the file changes.
            Assert.True(store.TryResolveGlobalAction(Key.O.WithCtrl, out _));

            File.WriteAllText(path, """{ "keybindings": { "tools": "Ctrl+E" } }""");
            await WaitUntilAsync(
                () => !store.TryResolveGlobalAction(Key.O.WithCtrl, out _) && store.TryResolveGlobalAction(Key.E.WithCtrl, out _),
                TimeSpan.FromSeconds(5));

            Assert.True(store.TryResolveGlobalAction(Key.E.WithCtrl, out var action));
            Assert.Equal(TuiShortcutAction.ToggleToolOutput, action);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReloadOnFileChange_ReportsValidationDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pisharp-kbw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "keybindings.json");
        File.WriteAllText(path, """{ "keybindings": {} }""");
        var store = new TuiKeybindingStore(TuiBuiltInShortcutCatalog.Bindings);
        var diagnostics = new List<string>();
        try
        {
            using var watcher = new KeybindingsWatcher(path, store, action => action(), diagnostics.Add);

            File.WriteAllText(path, """{ "keybindings": { "tools": "Ctrl+Space" } }""");
            await WaitUntilAsync(() => diagnostics.Count > 0, TimeSpan.FromSeconds(5));

            Assert.Contains(diagnostics, diagnostic => diagnostic.Contains("Invalid key string", StringComparison.Ordinal));
            Assert.True(store.TryResolveGlobalAction(Key.O.WithCtrl, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Disposing_Watcher_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pisharp-kbw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "keybindings.json");
        File.WriteAllText(path, """{ "keybindings": {} }""");
        try
        {
            var watcher = new KeybindingsWatcher(path, new TuiKeybindingStore(TuiBuiltInShortcutCatalog.Bindings), action => action(), _ => { });
            watcher.Dispose();
            watcher.Dispose(); // double dispose is a no-op
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
