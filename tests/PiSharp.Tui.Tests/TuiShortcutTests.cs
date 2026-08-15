using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Core.Models;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class TuiShortcutTests
{
    [Fact]
    public void BuiltInShortcutCatalogMatchesExistingBindingInventory()
    {
        Assert.Equal(TuiKeybindings.Defaults.Select(binding => binding.Action), TuiBuiltInShortcutCatalog.Bindings.Select(binding => binding.Action));
        Assert.Equal(TuiKeybindings.GlobalShortcuts.Select(binding => binding.Action), TuiBuiltInShortcutCatalog.GlobalShortcuts.Select(binding => binding.Action));
        Assert.Equal(TuiKeybindings.CommandDescriptors.Select(descriptor => descriptor.Id), TuiBuiltInShortcutCatalog.CommandDescriptors.Select(descriptor => descriptor.Id));
    }

    [Fact]
    public void BuiltInShortcutCatalogExposesOrderedHeaderHints()
    {
        var hints = TuiBuiltInShortcutCatalog.HeaderHints.ToArray();

        Assert.Equal(["model", "tools", "thoughts", "thinking", "details"], hints.OrderBy(hint => hint.Order).Select(hint => hint.Label));
        Assert.Equal(["Ctrl+L", "Ctrl+O", "Ctrl+T", "Ctrl+Y", "Ctrl+H"], hints.OrderBy(hint => hint.Order).Select(hint => hint.Keys));
    }

    [Fact]
    public void BuiltInShortcutCatalogProvidesDefaultDispatcherCommands()
    {
        var actions = TuiBuiltInShortcutCatalog.Commands.Select(command => command.Action).ToArray();

        Assert.Contains(TuiShortcutAction.AbortRequest, actions);
        Assert.Contains(TuiShortcutAction.OpenModelSelector, actions);
        Assert.Contains(TuiShortcutAction.ToggleToolOutput, actions);
        Assert.Contains(TuiShortcutAction.CycleThinkingLevel, actions);
        Assert.Contains(TuiShortcutAction.ToggleHeaderDetails, actions);
    }

    [Fact]
    public void KeybindingFacadeExposesCatalogHeaderHints()
        => Assert.Equal(
            TuiBuiltInShortcutCatalog.HeaderHints.Select(hint => (hint.Label, hint.Keys, hint.Order)),
            TuiKeybindings.HeaderHints.Select(hint => (hint.Label, hint.Keys, hint.Order)));

    [Fact]
    public void GovernedGlobalKeybindingsHaveUniqueActionsAndTerminalKeys()
    {
        var governed = TuiKeybindings.Defaults
            .Where(binding => binding.RegistrationPolicy == TuiShortcutRegistrationPolicy.GlobalShortcut)
            .ToArray();

        Assert.NotEmpty(governed);
        Assert.Equal(governed.Length, governed.Select(binding => binding.ShortcutAction).Distinct().Count());
        Assert.All(governed, binding => Assert.NotEmpty(binding.TerminalKeys));
    }

    [Fact]
    public void ParserParsesNamedKeysAndModifierCombinations()
    {
        Assert.True(TuiShortcutKeyParser.TryParse("ctrl+k", out var ctrlK));
        Assert.Contains(Key.K.WithCtrl, ctrlK);
        Assert.Contains(new Key((KeyCode)11), ctrlK);

        Assert.True(TuiShortcutKeyParser.TryParse("shift+tab", out var shiftTab));
        Assert.Equal([Key.Tab.WithShift], shiftTab);

        Assert.True(TuiShortcutKeyParser.TryParse("alt+k", out var altK));
        Assert.Equal([Key.K.WithAlt], altK);

        Assert.True(TuiShortcutKeyParser.TryParse("pagedown", out var pageDown));
        Assert.Equal([Key.PageDown], pageDown);
    }

    [Fact]
    public void ParserRejectsUnsupportedKeys()
    {
        Assert.False(TuiShortcutKeyParser.TryParse("ctrl+space", out _));
        Assert.False(TuiShortcutKeyParser.TryParse("ctrl+k+x", out _));
    }

    [Fact]
    public void ParserDoesNotAliasCtrlHToBackspace()
    {
        Assert.True(TuiShortcutKeyParser.TryParse("ctrl+h", out var ctrlH));

        Assert.Contains(Key.H.WithCtrl, ctrlH);
        Assert.DoesNotContain(Key.Backspace, ctrlH);
    }

    [Fact]
    public void RegistrarResolvesEveryGovernedGlobalKeyFromMetadata()
    {
        var governed = TuiKeybindings.Defaults
            .Where(binding => binding.RegistrationPolicy == TuiShortcutRegistrationPolicy.GlobalShortcut)
            .ToArray();

        foreach (var binding in governed)
        {
            foreach (var key in binding.TerminalKeys)
            {
                Assert.True(TuiShortcutRegistrar.TryResolveGlobalAction(key, out var action));
                Assert.Equal(binding.ShortcutAction, action);
            }
        }
    }

    [Fact]
    public void RegistrarResolvesAsciiControlCodeAliasesForCtrlLetterShortcuts()
    {
        var cases = new (Key Key, TuiShortcutAction Action)[]
        {
            (new Key((KeyCode)3), TuiShortcutAction.ClearEditor),
            (new Key((KeyCode)4), TuiShortcutAction.ExitInteractiveMode),
            (new Key(KeyCode.Clear), TuiShortcutAction.OpenModelSelector),
            (new Key((KeyCode)15), TuiShortcutAction.ToggleToolOutput),
            (new Key((KeyCode)25), TuiShortcutAction.CycleThinkingLevel),
            (new Key((KeyCode)18), TuiShortcutAction.ShowSessionTree),
            (new Key((KeyCode)20), TuiShortcutAction.ToggleThinking),
            (new Key((KeyCode)21), TuiShortcutAction.ClearEditor)
        };

        foreach (var (key, expected) in cases)
        {
            Assert.True(TuiShortcutRegistrar.TryResolveGlobalAction(key, out var action));
            Assert.Equal(expected, action);
        }
    }

    [Fact]
    public void RegistrarDoesNotTreatBackspaceAsCtrlHHeaderShortcut()
    {
        Assert.False(TuiShortcutRegistrar.TryResolveGlobalAction(Key.Backspace, out _));
    }

    [Fact]
    public void EscapeDispatchesAbortRequestShortcut()
    {
        var invoked = new List<string>();
        var dispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();
        var context = new TuiShortcutContext(
            () => invoked.Add("abort"),
            () => invoked.Add("exit"),
            () => invoked.Add("clear"),
            () => invoked.Add("model"),
            () => invoked.Add("tree"),
            () => invoked.Add("tools"),
            () => invoked.Add("thinking"),
            () => invoked.Add("thinking-level"),
            () => invoked.Add("header"));
        var key = Key.Esc;

        Assert.True(TuiShortcutRegistrar.TryDispatchShortcutKey(key, dispatcher, context, () => [], _ => { }));

        Assert.True(key.Handled);
        Assert.Equal(["abort"], invoked);
    }

    [Fact]
    public void DispatcherRoutesRegisteredActionsAndRejectsUnknownActions()
    {
        var invoked = new List<TuiShortcutAction>();
        var dispatcher = new TuiShortcutDispatcher([
            new DelegateTuiShortcutCommand(TuiShortcutAction.OpenModelSelector, _ => invoked.Add(TuiShortcutAction.OpenModelSelector))
        ]);
        var context = new TuiShortcutContext();

        Assert.True(dispatcher.TryDispatch(TuiShortcutAction.OpenModelSelector, context));
        Assert.False(dispatcher.TryDispatch(TuiShortcutAction.ToggleToolOutput, context));
        Assert.Equal([TuiShortcutAction.OpenModelSelector], invoked);
    }

    [Fact]
    public void RegistrarDispatchesFocusedViewShortcutAndMarksHandled()
    {
        var invoked = new List<string>();
        var dispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();
        var context = new TuiShortcutContext(
            () => invoked.Add("abort"),
            () => invoked.Add("exit"),
            () => invoked.Add("clear"),
            () => invoked.Add("model"),
            () => invoked.Add("tree"),
            () => invoked.Add("tools"),
            () => invoked.Add("thinking"),
            () => invoked.Add("thinking-level"),
            () => invoked.Add("header"));
        var key = new Key((KeyCode)20);

        Assert.True(TuiShortcutRegistrar.TryDispatchShortcutKey(key, dispatcher, context, () => [], _ => { }));

        Assert.True(key.Handled);
        Assert.Equal(["thinking"], invoked);
    }

    [Fact]
    public void RegistrarSkipsAlreadyHandledFocusedViewShortcut()
    {
        var invoked = false;
        var dispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();
        var context = new TuiShortcutContext(Toggle, Toggle, Toggle, Toggle, Toggle, Toggle, Toggle, Toggle, Toggle);
        var key = Key.T.WithCtrl;
        key.Handled = true;

        Assert.False(TuiShortcutRegistrar.TryDispatchShortcutKey(key, dispatcher, context, () => [], _ => { }));

        Assert.False(invoked);
        Assert.True(key.Handled);

        void Toggle() => invoked = true;
    }

    [Fact]
    public void RegistrarDispatchesHandledGlobalShortcutWhenPromptAllowsIt()
    {
        var invoked = new List<string>();
        var dispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();
        var context = new TuiShortcutContext(
            () => invoked.Add("abort"),
            () => invoked.Add("exit"),
            () => invoked.Add("clear"),
            () => invoked.Add("model"),
            () => invoked.Add("tree"),
            () => invoked.Add("tools"),
            () => invoked.Add("thinking"),
            () => invoked.Add("thinking-level"),
            () => invoked.Add("header"));
        var key = Key.C.WithCtrl;
        key.Handled = true;

        Assert.True(TuiShortcutRegistrar.TryDispatchShortcutKey(
            key,
            dispatcher,
            context,
            () => [],
            _ => { },
            allowHandledGlobalShortcuts: true));

        Assert.Equal(["clear"], invoked);
        Assert.True(key.Handled);
    }

    [Fact]
    public void DefaultDispatcherUsesInjectedShortcutContextCapabilities()
    {
        var invoked = new List<string>();
        var dispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();
        var context = new TuiShortcutContext(
            () => invoked.Add("abort"),
            () => invoked.Add("exit"),
            () => invoked.Add("clear"),
            () => invoked.Add("model"),
            () => invoked.Add("tree"),
            () => invoked.Add("tools"),
            () => invoked.Add("thinking"),
            () => invoked.Add("thinking-level"),
            () => invoked.Add("header"),
            () => invoked.Add("ctrl-c"));

        Assert.True(dispatcher.TryDispatch(TuiShortcutAction.ClearEditor, context));
        Assert.True(dispatcher.TryDispatch(TuiShortcutAction.CycleThinkingLevel, context));
        Assert.True(dispatcher.TryDispatch(TuiShortcutAction.ToggleHeaderDetails, context));

        Assert.Equal(["ctrl-c", "thinking-level", "header"], invoked);
    }

    [Fact]
    public async Task RegistrarDispatchesSingleExtensionShortcut()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binding = new TuiExtensionShortcutBinding(
            "extension:test",
            "ctrl+k",
            "Run test",
            TuiShortcutKeyParser.Parse("ctrl+k"),
            _ => { invoked.SetResult(); return Task.CompletedTask; });
        var errors = new List<string>();

        Assert.True(TuiShortcutRegistrar.TryDispatchExtensionShortcut(new Key((KeyCode)11), [binding], errors.Add));
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task RegistrarUsesCurrentLoggerFactoryForShortcutFailures()
    {
        using var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var previous = TuiShortcutRegistrar.LoggerFactory;
        TuiShortcutRegistrar.LoggerFactory = loggerFactory;
        try
        {
            var binding = new TuiExtensionShortcutBinding(
                "extension:test",
                "ctrl+k",
                "Run test",
                TuiShortcutKeyParser.Parse("ctrl+k"),
                _ => throw new InvalidOperationException("shortcut failed"));

            Assert.True(TuiShortcutRegistrar.TryDispatchExtensionShortcut(new Key((KeyCode)11), [binding], _ => { }));

            await WaitUntilAsync(() => provider.Entries.Any(entry => entry.Message.Contains("failed", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            TuiShortcutRegistrar.LoggerFactory = previous;
        }
    }

    [Fact]
    public async Task RegistrarDoesNotInvokeDuplicateExtensionShortcutConflicts()
    {
        var invoked = false;
        var bindings = new[]
        {
            new TuiExtensionShortcutBinding("extension:a", "ctrl+k", "A", TuiShortcutKeyParser.Parse("ctrl+k"), _ => { invoked = true; return Task.CompletedTask; }),
            new TuiExtensionShortcutBinding("extension:b", "ctrl+k", "B", TuiShortcutKeyParser.Parse("ctrl+k"), _ => { invoked = true; return Task.CompletedTask; })
        };
        var errors = new List<string>();

        Assert.False(TuiShortcutRegistrar.TryDispatchExtensionShortcut(Key.K.WithCtrl, bindings, errors.Add));
        await Task.Delay(50);

        Assert.False(invoked);
        Assert.Single(errors);
        Assert.Contains("extension:a", errors[0]);
        Assert.Contains("extension:b", errors[0]);
    }

    [Fact]
    public void RegistrarRejectsExtensionShortcutThatConflictsWithBuiltIn()
    {
        var binding = new TuiExtensionShortcutBinding(
            "extension:test",
            "ctrl+l",
            "Conflicts",
            TuiShortcutKeyParser.Parse("ctrl+l"),
            _ => Task.CompletedTask);
        var errors = new List<string>();

        Assert.False(TuiShortcutRegistrar.TryDispatchExtensionShortcut(Key.L.WithCtrl, [binding], errors.Add));

        Assert.Single(errors);
        Assert.Contains("built-in", errors[0]);
    }

    [Fact]
    public void HotkeyTextIncludesExtensionShortcuts()
    {
        var text = TuiHotkeyText.Render(TuiKeybindings.Defaults, [new OwnedExtensionRegistration<ExtensionShortcutRegistration>(
            "shortcut:ctrl+k:extension:test",
            "extension:test",
            new ExtensionShortcutRegistration("ctrl+k", "Run test", (_, _) => Task.CompletedTask))]);

        Assert.Contains("Built-in shortcuts:", text);
        Assert.Contains("Extension shortcuts:", text);
        Assert.Contains("ctrl+k", text);
        Assert.Contains("extension:test", text);
    }

    [Fact]
    public void HotkeyTextRenderFromBindingsIncludesExtensionShortcuts()
    {
        var text = TuiHotkeyText.RenderFromBindings(TuiKeybindings.CommandDescriptors,
            [new TuiExtensionShortcutBinding("extension:test", "ctrl+k", "Run test", [Key.K.WithCtrl], _ => Task.CompletedTask)]);

        Assert.Contains("Built-in shortcuts:", text);
        Assert.Contains("Extension shortcuts:", text);
        Assert.Contains("ctrl+k", text);
        Assert.Contains("Run test", text);
        Assert.Contains("extension:test", text);
    }

    [Fact]
    public void PromptEditorDoesNotExposeAppShortcutEvents()
    {
        var eventNames = typeof(PromptEditor).GetEvents().Select(e => e.Name).ToArray();

        Assert.DoesNotContain("AbortRequested", eventNames);
        Assert.DoesNotContain("ExitRequested", eventNames);
        Assert.DoesNotContain("ClearRequested", eventNames);
        Assert.DoesNotContain("ModelSelectionRequested", eventNames);
        Assert.DoesNotContain("SessionTreeRequested", eventNames);
        Assert.DoesNotContain("ToolsToggleRequested", eventNames);
        Assert.DoesNotContain("ThinkingToggleRequested", eventNames);
        Assert.DoesNotContain("HeaderToggleRequested", eventNames);
    }

    [Fact]
    public void PromptEditorLeavesAppShortcutsForHostRegistrar()
    {
        var prompt = new PromptEditor();
        prompt.SetPromptText("to-clear");

        prompt.NewKeyDownEvent(Key.C.WithCtrl);
        prompt.NewKeyDownEvent(Key.L.WithCtrl);

        Assert.Equal("to-clear", prompt.PromptText);
    }

    [Fact]
    public void CommandDescriptorsCoverBuiltInActionsAndRenderInHotkeysAndHelp()
    {
        var descriptors = TuiKeybindings.CommandDescriptors;
        Assert.NotEmpty(descriptors);
        Assert.All(descriptors, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.Title));
            Assert.False(string.IsNullOrWhiteSpace(d.DefaultKeys));
        });

        var withSlash = descriptors.Where(d => d.SlashCommand is not null).ToArray();
        Assert.NotEmpty(withSlash);
        Assert.All(withSlash, d => Assert.StartsWith("/", d.SlashCommand!, StringComparison.Ordinal));

        var abortDescriptor = Assert.Single(descriptors, d => string.Equals(d.Id, "abort", StringComparison.Ordinal));
        Assert.NotNull(abortDescriptor.SlashCommand);
        Assert.Equal("Esc", abortDescriptor.DefaultKeys);

        var hotkeyText = TuiHotkeyText.RenderFromDescriptors(descriptors, []);
        Assert.Contains("Built-in shortcuts:", hotkeyText);
        Assert.Contains(abortDescriptor.DefaultKeys, hotkeyText);
        Assert.Contains(abortDescriptor.Description, hotkeyText);
    }

    [Fact]
    public void CommandDescriptorActionDispatchesCorrectly()
    {
        var descriptors = TuiKeybindings.CommandDescriptors;
        Assert.NotEmpty(descriptors);

        var abortDescriptor = Assert.Single(descriptors, d => string.Equals(d.Id, "abort", StringComparison.Ordinal));
        var invoked = false;
        var context = new TuiShortcutContext(
            () => invoked = true,
            Noop, Noop, Noop, Noop, Noop, Noop, Noop, Noop);
        var dispatcher = TuiShortcutDispatcher.CreateDefaultAppDispatcher();

        Assert.True(dispatcher.TryDispatch(abortDescriptor.ShortcutAction, context));
        Assert.True(invoked);
    }

    [Fact]
    public async Task CommandControllerHelpOutputUsesDescriptors()
    {
        var state = TuiRenderState.Empty("sid", "session.jsonl",
            new ModelDescriptor("test", "model", "test"),
            ThinkingLevel.Off, null);
        var descriptors = TuiKeybindings.CommandDescriptors
            .Where(d => d.SlashCommand is not null)
            .Take(4)
            .ToArray();
        Assert.NotEmpty(descriptors);

        var hotkeyText = TuiHotkeyText.RenderFromDescriptors(TuiKeybindings.CommandDescriptors, []);
        var controller = new TuiCommandController(new TuiCommandControllerOptions(
            () => state,
            next => state = next,
            () => { },
            () => { },
            () => hotkeyText));

        var result = await controller.TryHandleCommandAsync("/help", CancellationToken.None);
        Assert.True(result);
        var item = Assert.Single(state.Transcript);
        Assert.Equal("system", item.Role);
        Assert.Contains(descriptors[0].SlashCommand!, item.Text);
        Assert.Contains(descriptors[0].DefaultKeys, item.Text);
    }

    private static void Noop()
    {
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not met.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    [Fact]
    public void PromptEditorAcceptsInjectedEditorKeyMap()
    {
        var prompt = new PromptEditor(PromptEditorKeyMap.CreateEmpty());
        var submitted = false;
        prompt.Submitted += (_, _) =>
        {
            submitted = true;
            return Task.CompletedTask;
        };
        prompt.SetPromptText("hello");

        prompt.NewKeyDownEvent(Key.Enter);

        Assert.False(submitted);
        Assert.Equal("hello", prompt.PromptText);
    }
}
