using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Keybindings;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class KeybindingsLoaderTests
{
    private static IReadOnlyList<TuiKeybinding> Defaults => TuiBuiltInShortcutCatalog.Bindings;

    private static TuiKeybinding BindingFor(IReadOnlyList<TuiKeybinding> bindings, TuiShortcutAction action)
        => Assert.Single(bindings, binding => binding.ShortcutAction == action);

    [Fact]
    public void Merge_NullOrEmptyJson_ReturnsDefaultsUnchanged()
    {
        Assert.Equal(Defaults.Select(binding => binding.Action), KeybindingsLoader.Merge(null, Defaults).EffectiveBindings.Select(binding => binding.Action));
        Assert.Empty(KeybindingsLoader.Merge(null, Defaults).Diagnostics);
        Assert.Equal(Defaults.Select(binding => binding.Action), KeybindingsLoader.Merge("", Defaults).EffectiveBindings.Select(binding => binding.Action));
    }

    [Fact]
    public void Merge_MalformedJson_KeepsDefaultsAndDiagnoses()
    {
        var result = KeybindingsLoader.Merge("{ not json", Defaults);

        Assert.Equal(Defaults.Count, result.EffectiveBindings.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Failed to parse keybindings", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_MissingRootSection_KeepsDefaultsAndDiagnoses()
    {
        var result = KeybindingsLoader.Merge("{ \"keybindings\": 42 }", Defaults);

        Assert.Equal(Defaults.Count, result.EffectiveBindings.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("keybindings", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_ActionIdOverride_ReplacesKeys()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "tools": "Ctrl+E" } }""", Defaults);

        Assert.Empty(result.Diagnostics);
        var tools = BindingFor(result.EffectiveBindings, TuiShortcutAction.ToggleToolOutput);
        Assert.Equal("Ctrl+E", tools.Keys);
        Assert.Contains(Key.E.WithCtrl, tools.TerminalKeys);

        Assert.Equal("tools", tools.Action);
    }
    [Fact]
    public void Merge_EnumNameActionId_ResolvesLikeActionString()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "ToggleToolOutput": "Ctrl+E" } }""", Defaults);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Ctrl+E", BindingFor(result.EffectiveBindings, TuiShortcutAction.ToggleToolOutput).Keys);
    }

    [Fact]
    public void Merge_ArrayValue_BindsMultipleKeys()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "clear-editor": ["Ctrl+C", "Ctrl+U"] } }""", Defaults);

        Assert.Empty(result.Diagnostics);
        var clear = BindingFor(result.EffectiveBindings, TuiShortcutAction.ClearEditor);
        Assert.Equal("Ctrl+C/Ctrl+U", clear.Keys);
        Assert.Contains(Key.C.WithCtrl, clear.TerminalKeys);
        Assert.Contains(Key.U.WithCtrl, clear.TerminalKeys);
    }

    [Fact]
    public void Merge_EmptyString_UnbindsAction()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "tools": "" } }""", Defaults);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.EffectiveBindings, binding => binding.ShortcutAction == TuiShortcutAction.ToggleToolOutput);
    }

    [Fact]
    public void Merge_NullValue_UnbindsAction()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "tools": null } }""", Defaults);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.EffectiveBindings, binding => binding.ShortcutAction == TuiShortcutAction.ToggleToolOutput);
    }

    [Fact]
    public void Merge_UnbindReleasesKeyForLaterReassignment()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "tools": "", "thinking": "Ctrl+O" } }""", Defaults);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Ctrl+O", BindingFor(result.EffectiveBindings, TuiShortcutAction.ToggleThinking).Keys);
        Assert.DoesNotContain(result.EffectiveBindings, binding => binding.ShortcutAction == TuiShortcutAction.ToggleToolOutput);
    }

    [Fact]
    public void Merge_UnknownAction_IsDiagnosedAndIgnored()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "bogus-action": "Ctrl+E" } }""", Defaults);

        Assert.Equal(Defaults.Count, result.EffectiveBindings.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Unknown keybinding action", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_InvalidKeyString_KeepsBuiltInKeysAndDiagnoses()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "tools": "Ctrl+Space" } }""", Defaults);

        Assert.Equal("Ctrl+O", BindingFor(result.EffectiveBindings, TuiShortcutAction.ToggleToolOutput).Keys);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Invalid key string", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_DuplicateKeyAcrossActions_FirstFileEntryWins()
    {
        var result = KeybindingsLoader.Merge("""{ "keybindings": { "tools": "m", "thinking": "m" } }""", Defaults);

        Assert.Equal("m", BindingFor(result.EffectiveBindings, TuiShortcutAction.ToggleToolOutput).Keys);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("already bound", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_IsDeterministic()
    {
        const string json = """{ "keybindings": { "tools": "Ctrl+E", "thinking": "" } }""";

        var first = KeybindingsLoader.Merge(json, Defaults);
        var second = KeybindingsLoader.Merge(json, Defaults);

        Assert.Equal(first.EffectiveBindings.Select(binding => (binding.Action, binding.Keys)), second.EffectiveBindings.Select(binding => (binding.Action, binding.Keys)));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public void Store_TryResolveGlobalAction_HonorsRemaps()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");

        Assert.True(store.TryResolveGlobalAction(Key.E.WithCtrl, out var remapped));
        Assert.Equal(TuiShortcutAction.ToggleToolOutput, remapped);
        Assert.False(store.TryResolveGlobalAction(Key.O.WithCtrl, out _));
    }

    [Fact]
    public void Store_TryResolveGlobalAction_ResolvesAsciiControlCodeAliasesAfterRemap()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");

        Assert.True(store.TryResolveGlobalAction(new Key((KeyCode)5), out var action));
        Assert.Equal(TuiShortcutAction.ToggleToolOutput, action);
    }

    [Fact]
    public void Store_RegistrarOverload_ResolvesViaExplicitStore()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");

        Assert.True(TuiShortcutRegistrar.TryResolveGlobalAction(Key.E.WithCtrl, store, out var action));
        Assert.Equal(TuiShortcutAction.ToggleToolOutput, action);
        Assert.False(TuiShortcutRegistrar.TryResolveGlobalAction(Key.O.WithCtrl, store, out _));
    }

    [Fact]
    public void Store_HeaderHints_ReflectEffectiveKeys()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");

        var toolsHint = Assert.Single(store.HeaderHints, hint => hint.Label == "tools");
        Assert.Equal("Ctrl+E", toolsHint.Keys);
        Assert.Equal("Ctrl+L", Assert.Single(store.HeaderHints, hint => hint.Label == "model").Keys);
    }

    [Fact]
    public void Store_CommandDescriptorsAndHotkeysText_ReflectEffectiveTable()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");

        var descriptor = Assert.Single(store.CommandDescriptors, descriptor => descriptor.Id == "tools");
        Assert.Equal("Ctrl+E", descriptor.DefaultKeys);
        Assert.Contains("Ctrl+E", store.HotkeysText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Store_HintText_UsesEffectiveKeysOrFallback()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");

        Assert.Equal("Ctrl+E", store.HintText("tools", "Ctrl+O"));
        Assert.Equal("Ctrl+E", store.HintText("tools", "Ctrl+O"));
        store.Reload(null);
        Assert.Equal("Ctrl+O", store.HintText("tools", "Ctrl+O"));
        Assert.Equal("fallback", store.HintText("missing-action", "fallback"));
    }

    [Fact]
    public void Store_Reload_RaisesChanged()
    {
        var store = new TuiKeybindingStore(Defaults);
        var changed = 0;
        store.Changed += () => changed++;

        store.Reload("""{ "keybindings": { "tools": "Ctrl+E" } }""");
        store.Reload(null);

        Assert.Equal(2, changed);
    }

    [Fact]
    public void Store_UnboundGlobalKey_IsNotResolvable()
    {
        var store = new TuiKeybindingStore(Defaults);
        store.Reload("""{ "keybindings": { "tools": "" } }""");

        Assert.False(store.TryResolveGlobalAction(Key.O.WithCtrl, out _));
    }

    [Fact]
    public void LoadFile_ReadsExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-keybindings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "keybindings": { "tools": "Ctrl+E" } }""");
            var result = KeybindingsLoader.LoadFile(path, Defaults);

            Assert.Empty(result.Diagnostics);
            Assert.Equal("Ctrl+E", BindingFor(result.EffectiveBindings, TuiShortcutAction.ToggleToolOutput).Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadFile_MissingFile_ReturnsError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"pisharp-keybindings-missing-{Guid.NewGuid():N}.json");

        Assert.False(KeybindingsLoader.TryReadFile(missing, out _, out var error));
        Assert.Contains("keybindings", error, StringComparison.OrdinalIgnoreCase);
    }
}
