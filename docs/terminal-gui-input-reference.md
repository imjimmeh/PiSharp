# Terminal.Gui input, paste, and command handling reference

## Scope

This document summarizes keyboard, mouse, focus, text editing, paste/clipboard, shortcuts, and async UI-update patterns found in `G:\tmp\tgui\Examples`.

High-value source files:

- `UICatalog/Scenarios/Keys.cs`
- `UICatalog/Scenarios/KeyBindings.cs`
- `UICatalog/Scenarios/MouseTester.cs`
- `UICatalog/Scenarios/TextInputControls.cs`
- `UICatalog/Scenarios/Editor.cs`
- `UICatalog/UICatalogRunnable.cs`
- `ReactiveExample/TerminalScheduler.cs`
- `ReactiveExample/LoginView.cs`
- `PromptExample/Program.cs`

## Input model layers

The examples show three distinct input layers:

1. **Application/driver layer** — raw-ish keyboard/mouse observation and app-wide bindings.
2. **View/control layer** — `TextField`, `TextView`, `ListView`, `TableView`, `MenuBar`, etc. process built-in editing/navigation.
3. **Command layer** — high-level `Command` events such as `Accepting`, `Accepted`, `Activating`, `CommandNotBound`, and menu/status `Action`s.

PiSharp should prefer the highest layer that fits the task. Use command events for app behavior; use view-level key/mouse only for custom controls; inspect driver/app events mainly for diagnostics.

## Keyboard events

`Keys.cs` demonstrates app-level and view-level key streams:

```csharp
// G:\tmp\tgui\Examples\UICatalog\Scenarios\Keys.cs
app.Keyboard.KeyDown += (_, e) => labelAppKeypress.Text = FormatKeyEvent(e);
app.Keyboard.KeyUp += (_, e) => labelAppKeyUp.Text = FormatKeyEvent(e);
edit.KeyDown += (_, e) => lastTextFieldKeyDownLabel.Text = FormatKeyEvent(e);
edit.KeyDownNotHandled += (_, a) => keyDownNotHandledList.Add($"{a}");
```

The example also tracks swallowed ANSI sequences:

```csharp
app.Driver!.GetInputProcessor().AnsiSequenceSwallowed +=
    (_, e) => swallowedList.Add(e.Replace("\x1b", "Esc"));
```

Guidance:

- Use `app.Keyboard.KeyDown/KeyUp` for diagnostics, global telemetry, or app-wide command infrastructure.
- Use `View.KeyDown` only when a particular control needs custom behavior.
- Prefer `KeyDownNotHandled` for fallback behavior after native controls had first chance.
- Avoid using raw key events to implement normal text entry.

## Key bindings and commands

`KeyBindings.cs` uses `Application.DefaultKeyBindings`, `View.DefaultKeyBindings`, each view's `KeyBindings`, and `HotKeyBindings` to inspect what commands are already bound.

`UICatalogRunnable.CreateScenarioList` customizes `TableView` key bindings to make a table behave more like a list:

```csharp
scenarioList.KeyBindings.Remove(Key.Home);
scenarioList.KeyBindings.Add(Key.Home, Command.Start);
scenarioList.KeyBindings.Remove(Key.End);
scenarioList.KeyBindings.Add(Key.End, Command.End);
scenarioList.MultiSelect = false;
scenarioList.KeyBindings.Remove(Key.A.WithCtrl);
```

Guidance:

- Bind keys to `Command` values, then handle command events, instead of scattering raw-key comparisons.
- Before assigning a global shortcut, check app/menu/status/view bindings for conflicts.
- Modify built-in bindings sparingly and document why.
- For prompt editing, preserve native text navigation/editing unless PiSharp deliberately overrides it.

## Command events

Examples wire behavior to command events:

- `Button.Accepting` for buttons.
- `ListView`/`TableView.Accepted` for open/activate selected item.
- `Window.Accepting` for default accept behavior.
- `View.CommandNotBound` for fallbacks/custom command handling.

Examples:

```csharp
// Example/Example.cs
btnLogin.Accepting += (s, e) => {
    // validate login
    e.Handled = true;
};
```

```csharp
// UICatalogRunnable.cs
scenarioList.Accepted += ScenarioView_OpenSelectedItem;
```

```csharp
// MouseTester.cs
runnable.CommandNotBound += (_, args) => {
    if (args.Context!.Command != Command.DeleteAll) return;
    // clear logs
    args.Handled = true;
};
```

Guidance:

- Treat command events as the preferred surface for application actions.
- Always set `e.Handled = true` when a command should stop propagating.
- Keep event handlers thin: event -> controller command -> state update -> render.

## TextField vs TextView

### `TextField`

Use `TextField` for single-line input. `TextInputControls.cs` demonstrates:

- `TextChanging` for before/preview text and autocomplete source updates.
- `TextChanged` for committed changes and mirroring.
- `Autocomplete` and `AppendAutocomplete`.
- validation controls like `TextValidateField`.

```csharp
textField.TextChanging += TextFieldTextChanging;
textField.TextChanged += (sender, _) => {
    labelMirroringTextField.Text = ((TextField)sender!).Text;
};
```

### `TextView`

Use `TextView` for multi-line prompt/editor surfaces. `TextInputControls.cs` explicitly warns that typed edits use `ContentsChanged`, while `TextChanged` is for explicit property assignment:

```csharp
textView.ContentsChanged += (_, _) => {
    labelMirroringTextView.Text = textView.Text;
};
textView.Text = "TextView with some more test text.";
```

`TextView` features used in examples:

- `ScrollBars`
- `ReadOnly`
- `Multiline`
- `WordWrap`
- `TabKeyAddsTab`
- `Autocomplete`
- `UnwrappedCursorPositionChanged`
- `Copy`, `Cut`, `Paste`, `SelectAll`
- file load/save (`Load`, `CloseFile`, `ClearHistoryChanges`)

PiSharp prompt guidance:

- Prefer `TextView` for the composer if multi-line editing and paste matter.
- Use `ContentsChanged` to observe user edits.
- Be deliberate about `TabKeyAddsTab`; if true, Tab inserts tabs rather than navigating focus.
- Do not intercept Enter/Tab/Escape at a parent level without first understanding how `TextView` binds them.

## Paste and clipboard

Direct paste references are concentrated in `Editor.cs`. It uses built-in `TextView` clipboard methods and exposes them through menu items:

```csharp
// G:\tmp\tgui\Examples\UICatalog\Scenarios\Editor.cs
new MenuItem { Title = Strings.cmdCopy, Key = Key.C.WithCtrl, Action = Copy },
new MenuItem { Title = Strings.cmdCut, Key = Key.W.WithCtrl, Action = Cut },
new MenuItem { Title = Strings.cmdPaste, Key = Key.Y.WithCtrl, Action = Paste },
```

```csharp
private void Copy () => _textView?.Copy();
private void Cut () => _textView?.Cut();
private void Paste () => _textView?.Paste();
```

The editor status bar also displays OS clipboard support:

```csharp
new Shortcut(Key.Empty, $"OS Clipboard IsSupported : {app.Clipboard!.IsSupported}", null)
```

What examples do **not** cover well:

- bracketed paste protocol details;
- huge paste throttling/backpressure;
- paste-as-command vs paste-as-text policy;
- custom prompt submission semantics during paste.

PiSharp guidance:

- Use built-in `TextView.Paste()`/clipboard behavior first.
- Do not implement paste as a loop over raw key events unless Terminal.Gui exposes no better hook.
- Add tests for multi-line paste, large paste, paste into empty/non-empty prompt, paste while autocomplete is open, and paste followed by submit.
- If bracketed paste is required, inspect Terminal.Gui driver/input source in addition to examples; examples only show built-in clipboard/paste APIs.

## Mouse handling

`MouseTester.cs` demonstrates driver, app, and view mouse events:

```csharp
app.Driver!.MouseEvent += (_, mouse) => { /* driver-level log */ };
app.Mouse.MouseEvent += (_, mouse) => { /* app-level log */ };
demo.MouseEvent += (_, mouse) => { /* view-level log */ };
```

It also demonstrates mapping mouse gestures to commands:

```csharp
MouseBindings.ReplaceCommands(MouseFlags.LeftButtonPressed, Command.Down);
MouseBindings.ReplaceCommands(MouseFlags.LeftButtonReleased, Command.Up);
MouseBindings.ReplaceCommands(MouseFlags.LeftButtonClicked, Command.Accept);
MouseBindings.ReplaceCommands(MouseFlags.LeftButtonDoubleClicked, Command.Open);
```

And custom rendering based on mouse state:

```csharp
protected override void OnMouseStateChanged(EventArgs<MouseState> args)
{
    base.OnMouseStateChanged(args);
    Border.LineStyle = args.Value.HasFlag(MouseState.PressedOutside)
        ? LineStyle.Dotted
        : LineStyle.Single;
    SetNeedsDraw();
}
```

PiSharp:

- Prefer built-in mouse behavior for standard controls.
- For custom controls, map mouse input to high-level commands (`Accept`, `Open`, `Down`, `Up`) and handle commands.
- Keep keyboard parity for every mouse-only action.

## Focus and tab handling

Patterns:

- Interactive controls set `CanFocus = true`.
- Decorative `Shortcut`/`StatusBar` entries often use `CanFocus = false`.
- `UICatalogRunnable` explicitly restores focus to `_scenarioList` after a scenario returns.
- `TextView.TabKeyAddsTab` changes whether Tab edits text or moves focus.

PiSharp:

- Set the initial focus intentionally after screen/dialog open.
- Make focus traversal part of TUI tests.
- Do not use global Tab handling that breaks `TextView.TabKeyAddsTab` behavior.

## Menus, status bars, and shortcuts

`MenuBar` and `StatusBar` bind the same action styles used by controls:

```csharp
new MenuItem {
    Title = Strings.cmdQuit,
    HelpText = "Quit UI Catalog",
    Key = Application.GetDefaultKey(Command.Quit),
    Action = RequestStop,
    Command = Command.Quit
}
```

```csharp
new Shortcut {
    CanFocus = false,
    Title = "Quit",
    Key = Application.GetDefaultKey(Command.Quit),
    Action = RequestStop
}
```

PiSharp:

- Centralize command definitions; bind them to menu items, status shortcuts, buttons, and hotkeys.
- Use status bar to expose discoverable shortcuts and live state.
- Avoid assigning arbitrary Ctrl keys without checking Terminal.Gui defaults and text editor needs.

## Async/background input effects

UI updates from background work are marshaled to the app loop.

`TextInputControls.cs`:

```csharp
Task.Run(async () => {
    await Task.Delay(1000);
    app.Invoke(() => acceptView.Text = "");
});
```

`Progress.cs`:

```csharp
_systemTimer = new Timer(_ => {
    _app?.Invoke(_ => systemTimerDemo.Pulse());
}, null, 0, _systemTimerTick);
```

`ReactiveExample/TerminalScheduler.cs` wraps ReactiveUI scheduling over `IApplication.Invoke` and `AddTimeout`.

PiSharp:

- Inject a dispatcher wrapper around `IApplication.Invoke`, `AddTimeout`, and `RemoveTimeout`.
- Never mutate controls directly from provider/tool/background threads.
- Dispose timers/subscriptions when screens close.

## Recommended PiSharp input architecture

```text
Terminal.Gui event/key/mouse/menu/status
  -> TuiCommandRouter
  -> screen/controller/view-model command
  -> domain/runtime operation
  -> state change
  -> dispatcher.Post(render/update)
```

Rules:

- Let controls own normal text editing and selection.
- Route app behavior through commands.
- Preserve native keybindings unless explicitly replaced.
- Keep raw key/mouse diagnostics separate from production command handling.
- Add integration tests for prompt editor: typing, cursor movement, selection, paste, submit, cancel, focus transitions.

## Gaps needing source/API inspection

The examples are insufficient for these topics:

- exact bracketed paste support;
- terminal driver handling for pasted bytes vs clipboard API;
- IME/composition behavior;
- full shortcut conflict resolution across platforms;
- behavior of very large paste/input bursts;
- focus behavior under nested modals and extension-provided controls.

For those, inspect Terminal.Gui source/tests in addition to examples.
