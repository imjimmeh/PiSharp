# Terminal.Gui examples reference for PiSharp

## Scope

This reference summarizes upstream Terminal.Gui examples from `G:\tmp\tgui\Examples` for future PiSharp TUI work. The examples are Terminal.Gui v2-style and primarily use `IApplication`, `Application.Create().Init()`, `Runnable`, command events, `Pos`/`Dim` layout, and built-in controls.

Use this as a pattern guide, not a package API guarantee. Verify exact signatures against PiSharp's Terminal.Gui package before copying snippets.

## Source inventory

Surveyed `151` C# files under these example roots:

- `Example/`, `SelfContained/`, `NativeAot/` — minimal app lifecycles.
- `PromptExample/` — prompt dialogs and typed result extraction.
- `ReactiveExample/` and `CommunityToolkitExample/` — view-model/data-binding patterns.
- `ShortcutTest/`, `InlineCLI/`, `InlineSelect/`, `InlineColorPicker/` — focused control samples.
- `UICatalog/` — large scenario catalog and most control/input/lifecycle examples.

High-value files:

- `Example/Example.cs`
- `PromptExample/Program.cs`
- `ReactiveExample/LoginView.cs`
- `ReactiveExample/LoginViewModel.cs`
- `ReactiveExample/TerminalScheduler.cs`
- `UICatalog/Scenario.cs`
- `UICatalog/Runner.cs`
- `UICatalog/UICatalogRunnable.cs`
- `UICatalog/Scenarios/TextInputControls.cs`
- `UICatalog/Scenarios/Editor.cs`
- `UICatalog/Scenarios/KeyBindings.cs`
- `UICatalog/Scenarios/Keys.cs`
- `UICatalog/Scenarios/MouseTester.cs`
- `UICatalog/Scenarios/Progress.cs`
- `UICatalog/Scenarios/TableEditor.cs`

## Application lifecycle

Modern examples prefer `IApplication` instances:

```csharp
// G:\tmp\tgui\Examples\Example\Example.cs
IApplication app = Application.Create ().Init ();
var userName = app.Run<ExampleWindow> ().GetResult<string> ();
app.Dispose ();
```

Other examples use `using`:

```csharp
using IApplication app = Application.Create ();
app.Init ();
using Window win = new ();
app.Run (win);
```

PiSharp guidance:

- Keep `Application.Create/Init/Run/Dispose` in the TUI host only.
- Screens and controls should not initialize or shut down Terminal.Gui.
- Prefer `using`/`Dispose` for `IApplication`, `Window`, `Dialog`, and custom views with subscriptions/timers.
- If a screen launches a modal, call `app.Run(dialog)` from an owning shell/dialog service, not from deep business logic.

## Core containers

### `Runnable`

`Runnable` is used for root views that can return a result and participate in app lifecycle. `ExampleWindow : Runnable<string?>` sets `Result` and calls `App!.RequestStop()` after login succeeds.

```csharp
// G:\tmp\tgui\Examples\Example\Example.cs
public sealed class ExampleWindow : Runnable<string?>
{
    public ExampleWindow ()
    {
        Title = $"Example App ({Application.GetDefaultKey (Command.Quit)} to quit)";
        // Add controls...
    }
}
```

PiSharp: model top-level screens as result-capable only when a result matters. For ordinary panes, use a `View`/`Window` plus controller state.

### `Window`

Most scenarios create a `Window` as the root content surface:

```csharp
using Window win = new ();
win.Title = GetQuitKeyAndName ();
```

Use `Window` for PiSharp full-screen TUI shells and major modal-like views.

### `Dialog` and `MessageBox`

Examples use `Dialog` for custom modal content and `MessageBox.Query/ErrorQuery` for quick notifications/confirmations. `UICatalogRunnable.ShowAboutDialog` creates a `Dialog`, adds content, focuses the default button, then runs it with `App?.Run(dialog)`.

PiSharp:

- Use `MessageBox` only for short questions/errors.
- Use a typed dialog service for forms, confirmation with validation, and multi-field workflows.
- Return explicit results; do not infer success from mutable controls after arbitrary close paths.

### `FrameView`

Examples use `FrameView` to group controls with a border/title, especially in `Progress` and layout demos. In `Progress`, `ProgressDemo : FrameView` nests a settings frame plus progress controls.

PiSharp: use `FrameView` for panels (settings, diagnostics, tool details). Do not use it as a full app shell.

## Layout idioms

Terminal.Gui examples rely on declarative geometry:

- `X`, `Y`, `Width`, `Height`
- `Pos.Right(view)`, `Pos.Bottom(view)`, `Pos.Left(view)`, `Pos.Top(view)`
- `Pos.Center()`, `Pos.AnchorEnd()`
- `Dim.Fill()`, `Dim.Fill(otherOrMargin)`, `Dim.Percent(n)`, `Dim.Auto()`

Examples:

```csharp
// label/field pairing from Example.cs style
var userNameText = new TextField {
    X = Pos.Right(usernameLabel) + 1,
    Width = Dim.Fill()
};
```

```csharp
// UICatalog scenario list: table fills remaining space beside categories
TableView scenarioList = new () {
    X = Pos.Right(_categoryList) - 1,
    Y = Pos.Bottom(_menuBar),
    Width = Dim.Fill(),
    Height = Dim.Height(_categoryList)
};
```

PiSharp rules:

- Prefer relative layout over hard-coded coordinates.
- Reserve top/bottom rows for `MenuBar`/`StatusBar` explicitly.
- Use `Dim.Percent` for split panes and `Dim.Fill` for main content.
- Use fixed widths only for short labels, buttons, and known-width fields.

## Common controls and usage patterns

### Labels and fields

`Label` + `TextField` pairs are widespread. Use labels as non-focusable captions; use `TextField` for single-line values.

`TextInputControls.cs` demonstrates:

- `TextField.TextChanging` for predictive/autocomplete source updates.
- `TextField.TextChanged` for mirroring committed text.
- `TextValidateField` with `TextRegexProvider`/masked providers for validation.

PiSharp: use `TextField` for filters/settings. Keep validation in a controller/view-model; use control events to trigger it.

### `TextView`

`TextView` is the rich multi-line editing control. `Editor.cs` builds a text editor around it:

```csharp
_textView = new TextView {
    X = 0,
    Y = 1,
    Width = Dim.Fill(),
    Height = Dim.Fill(1),
    ScrollBars = true
};
```

`TextInputControls.cs` notes an important event distinction:

```csharp
// Use ContentsChanged to detect if the user typed something in a TextView.
// TextChanged only fires if TextView.Text is explicitly set.
textView.ContentsChanged += (_, _) => { labelMirroringTextView.Text = textView.Text; };
```

PiSharp: prompt editor should prefer `TextView` or a wrapper around it for multi-line editing/paste. Do not rebuild editing from raw key events unless the built-in control cannot satisfy requirements.

### Buttons and command events

Examples use `Button.Accepting` rather than old `Clicked` patterns:

```csharp
btnLogin.Accepting += (s, e) => {
    // validate, mutate result, request stop
    e.Handled = true;
};
```

PiSharp: button/menu/status handlers should call controller commands. Keep handlers thin.

### Lists and tables

`ListView` is used for simple collections: categories, log streams, key event logs.

`TableView` is used for structured data and sometimes styled to behave like a list. `UICatalogRunnable.CreateScenarioList` configures `TableView` with full row selection, hidden headers, column widths, and selected scenario launch via `Accepted`.

PiSharp:

- Use `ListView` for simple command/model/history lists.
- Use `TableView` for diagnostics, tool-call tables, provider capability matrices, or file/process tables.
- Store authoritative data outside the control; set source/table from state.

### Menu and status bars

`MenuBar` is the global command surface. `StatusBar` exposes shortcut hints and live state.

`UICatalogRunnable.CreateMenuBar` uses `MenuItem` with `Title`, `HelpText`, `Key`, `Action`, and `Command`. It also embeds controls with `CommandView` for menu checkboxes/selectors.

`CreateStatusBar` uses `Shortcut` items, sometimes with `BindKeyToApplication = true`, and computes unbound F-keys to avoid conflicts.

PiSharp:

- Define each action once, then bind it to menu/status/button/hotkey surfaces.
- Treat status bar as discoverability and state display, not the only command path.
- Avoid key conflicts by checking app/view key bindings before assigning global shortcuts.

### Selection controls

Examples use `CheckBox`, `OptionSelector`, and `FlagSelector` heavily for live settings. Changes usually mutate a view property and call `SetNeedsDraw()`/`SetNeedsDisplay()`.

PiSharp: use them for settings and feature flags. Keep defaults visible and persist through config/state, not static UI fields.

### Progress/spinner controls

`Progress.cs` demonstrates both `System.Threading.Timer` with `app.Invoke` and `app.AddTimeout` without threads. It disposes timers in `Dispose`.

PiSharp: use progress bars only when determinate. For LLM/tool activity, prefer status text/spinner unless total progress is known.

## Styling and themes

Examples use:

- `ConfigurationManager.RuntimeConfig` for themes.
- `SchemeName = SchemeManager.SchemesToSchemeName(...)` for per-view schemes.
- `SetScheme(...)` for specific custom attributes.
- `ThemeManager.Theme` updates via menus.

PiSharp:

- Centralize color/theme mapping.
- Do not emit raw ANSI inside Terminal.Gui views.
- Use text/border/symbols in addition to color for focus/error state.

## Focus and navigation

Examples set `CanFocus = true` for interactive views and make menus/status shortcuts non-focusable where needed. `UICatalogRunnable` explicitly focuses scenario lists after returning from a scenario.

PiSharp:

- Set initial focus on the primary control of each screen/dialog.
- Add views in visual/tab order unless using a dedicated navigation API.
- Test focus between transcript, prompt editor, menus, dialogs, and side panes.

## Architecture lessons

- Keep Terminal.Gui lifecycle at app boundary.
- Compose UI from built-in controls before custom drawing.
- Use screen/scenario descriptors for discoverable feature registration.
- Keep state in controllers/view-models, not controls.
- Marshal background updates through `app.Invoke`/dispatcher.
- Dispose subscriptions, timers, dialogs, windows, and app instances.

## Anti-patterns to avoid copying

- Demo-style local mutable state captured by large event lambdas in production screens.
- Blocking I/O/model calls inside UI event handlers.
- Updating controls from background threads without `app.Invoke`.
- Leaving timers/subscriptions alive after a view closes.
- Treating `TableView` cells or `TextView.Text` as the only domain state.
- Hard-coding coordinates for complex layouts.

## Quick PiSharp checklist

- [ ] Host owns `Application.Create/Init/Run/Dispose`.
- [ ] Main shell reserves menu/status rows.
- [ ] Layout uses `Pos`/`Dim` instead of terminal-size math.
- [ ] Prompt uses a built-in text editing control where possible.
- [ ] Commands are centralized and bound to multiple surfaces.
- [ ] Background updates use an injected UI dispatcher.
- [ ] Dialogs return explicit results and validate before closing.
- [ ] Timers/subscriptions are disposed on close.
- [ ] Focus/paste/keybindings are covered by tests.
