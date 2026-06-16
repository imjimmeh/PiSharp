# Terminal.Gui example architecture reference

## Scope

This document summarizes how the upstream Terminal.Gui examples in `G:\tmp\tgui\Examples` organize apps, scenarios, reusable views, modal flows, state, data sources, background work, and testability seams. It is tailored to future PiSharp TUI design.

## Source files sampled

- `Example/Example.cs` — minimal lifecycle and result-returning `Runnable`.
- `PromptExample/Program.cs` — prompt API and result extraction.
- `ReactiveExample/Program.cs`, `ReactiveExample/LoginView.cs`, `ReactiveExample/LoginViewModel.cs`, `ReactiveExample/TerminalScheduler.cs` — MVVM/reactive patterns and main-loop scheduling.
- `CommunityToolkitExample/LoginView*.cs`, `LoginViewModel.cs` — generated/designer-like view composition and MVVM style.
- `UICatalog/Scenario.cs`, `ScenarioMetadata.cs`, `ScenarioCategory.cs`, `Runner.cs`, `UICatalogRunnable.cs` — scenario catalog architecture.
- `UICatalog/Scenarios/Editor.cs`, `TableEditor.cs`, `Progress.cs`, `MouseTester.cs`, `TextInputControls.cs` — complex screen examples.

## Application boundaries

Most examples isolate Terminal.Gui lifecycle to the entry point or scenario `Main()`:

```csharp
using IApplication app = Application.Create();
app.Init();
using Window win = new();
app.Run(win);
```

Minimal example with result:

```csharp
IApplication app = Application.Create().Init();
var userName = app.Run<ExampleWindow>().GetResult<string>();
app.Dispose();
```

PiSharp architecture:

- `PiSharp.Tui` host owns app creation, driver selection, configuration, run loop, and dispose.
- Feature screens receive a TUI context; they should not call `Application.Create()`.
- Use a dialog/screen service for modals, not direct static `Application` access from random controls.

## Scenario catalog pattern

`UICatalog` is the most reusable architecture example. `Scenario.cs` documents the extension model:

1. Create a `.cs` file in `Scenarios`.
2. Derive from `Scenario`.
3. Add `[ScenarioMetadata(name, description)]`.
4. Add `[ScenarioCategory(...)]` attributes.
5. Implement `Main()`.

`Scenario.GetScenarios()` discovers scenario subclasses by reflection and returns an ordered `ObservableCollection<Scenario>`.

`Runner.RunScenario()` creates a fresh instance by name, calls `scenario.Main()`, optionally benchmarks, disposes the scenario, and verifies view disposal under debug builds.

PiSharp translation:

```csharp
public sealed record TuiScreenDescriptor(
    string Id,
    string Title,
    string Category,
    Func<TuiScreenContext, ITuiScreen> Create);
```

Use descriptors/factories for:

- built-in screens;
- extension-contributed screens;
- diagnostics panes;
- settings pages;
- model/provider pickers.

Avoid a giant `switch` in the shell.

## Shell composition

`UICatalogRunnable` is the catalog shell. It builds the shell in `BeginInit()`:

```csharp
_menuBar = CreateMenuBar();
_statusBar = CreateStatusBar();
_categoryList = CreateCategoryList();
_scenarioList = CreateScenarioList();
Add(_menuBar, _categoryList, _scenarioList, _statusBar);
```

Responsibilities are cleanly separated:

- `CreateMenuBar()` — top-level app commands, themes, diagnostics, help.
- `CreateStatusBar()` — shortcut hints and live state.
- `CreateCategoryList()` — stable category list.
- `CreateScenarioList()` — filtered scenario table and launch behavior.
- selection handlers update source tables and cached selected indexes.

PiSharp shell should similarly own:

- menu/status chrome;
- top-level navigation/focus;
- command registry;
- extension screen hosting;
- modal/dialog service;
- lifetime and error boundaries.

Feature screens should own only their local controls/state.

## State preservation

`UICatalogRunnable` caches selected category/scenario in static fields so the shell can be rebuilt after a scenario exits while restoring prior state.

```csharp
private static int _cachedScenarioIndex;
private static int? _cachedCategoryIndex;
```

This is fine for demos, but PiSharp should prefer explicit state services:

```csharp
public sealed class TuiShellState
{
    public string? ActivePane { get; set; }
    public int SelectedConversationIndex { get; set; }
    public bool ShowStatusBar { get; set; }
}
```

Avoid static UI state where extension/session tests need isolation.

## Reusable view patterns

Examples use two view styles.

### Composite views

`Progress.ProgressDemo : FrameView` composes labels, buttons, progress bars, spinner, and nested frames. This is the preferred production style for most PiSharp controls.

Benefits:

- relies on Terminal.Gui built-ins;
- less custom rendering/input logic;
- easier to inspect and test;
- automatically inherits focus/layout behavior.

### Custom views

`MouseTester.MouseEventDemoView : View` customizes mouse state and attributes by overriding hooks such as `OnMouseStateChanged` and `OnGettingAttributeForRole`.

Use custom drawing/input in PiSharp only when built-ins cannot express the behavior, e.g. specialized transcript rendering or prompt editor affordances.

## Dialog and modal flow architecture

Patterns:

- `MessageBox.Query/ErrorQuery` for quick confirmation/error display.
- `Dialog` for custom modal content.
- `OpenDialog`/`SaveDialog` in editor/file examples.
- `Prompt<TView,TResult>` in `PromptExample` for typed input.

`PromptExample` shows result extraction from wrapped views:

```csharp
string? result = mainWindow.Prompt<TextField, string>(beginInitHandler: prompt => {
    prompt.Title = textFieldButton.Title;
    prompt.GetWrappedView().Width = 40;
    prompt.GetWrappedView().Text = "Default name";
});
```

Custom form extraction finds child controls by `Id`:

```csharp
var nameField = form.SubViews.FirstOrDefault(v => v.Id == "nameField") as TextField;
```

PiSharp:

- Wrap modal UI behind `ITuiDialogs`.
- Model explicit outcomes: accepted/cancelled/error.
- Validate before closing.
- Do not bury domain writes in dialog button handlers.

## MVVM/reactive patterns

`ReactiveExample` is the clearest state separation example.

`LoginViewModel` owns state and commands:

- reactive `Username` / `Password` properties;
- derived `IsValid`, `UsernameLength`, `PasswordLength`;
- `ReactiveCommand` for login/clear;
- no Terminal.Gui view inheritance.

`LoginView : Window, IViewFor<LoginViewModel>` owns controls and bindings:

- `TextField.TextChanged` binds back to view model;
- view-model properties bind to labels/control text;
- command events invoke view-model commands;
- subscriptions are collected in `CompositeDisposable` and disposed in `Dispose`.

PiSharp:

- Keep provider/session/conversation state outside Terminal.Gui controls.
- Use controllers/view-models for validation, derived labels, enabled command state, and async command progress.
- Dispose subscriptions when screens close.

## Background work and main-loop scheduling

The examples consistently marshal background updates to the app loop.

`Progress.cs` uses `Timer` but updates UI via `app.Invoke`:

```csharp
_systemTimer = new Timer(_ => {
    _app?.Invoke(_ => systemTimerDemo.Pulse());
}, null, 0, _systemTimerTick);
```

It also demonstrates `app.AddTimeout()` for UI-loop timers and removes them with `app.RemoveTimeout()`.

`ReactiveExample/TerminalScheduler.cs` adapts ReactiveUI scheduling to Terminal.Gui:

```csharp
_application?.Invoke(_ => {
    if (!cancellation.Token.IsCancellationRequested) {
        composite.Add(action(this, state));
    }
});
```

PiSharp:

- Introduce an `ITuiDispatcher` abstraction over `Invoke`, `AddTimeout`, and `RemoveTimeout`.
- Every provider/tool/background update should post to the dispatcher before mutating views.
- Remove timeouts and dispose timers/subscriptions in `Dispose`/`OnClosed`.
- Never block UI handlers with synchronous waits on model/tool operations.

## Data/list/table architecture

`UICatalogRunnable` adapts scenario collections into a `TableView` source:

```csharp
_scenarioList.Table = new EnumerableTableSource<Scenario>(
    newScenarioList,
    new Dictionary<string, Func<Scenario, object>> {
        { "Name", s => s.GetName() },
        { "Description", s => s.GetDescription() }
    });
```

`TableEditor.cs` uses `DataTable` and configures style/selection/editing. It keeps editor state in fields (`_currentTable`, `_tableView`, schemes, checked file-system info), then adapts to `TableView`.

PiSharp:

- Domain state should be authoritative.
- Controls should render adapters over state, not own state.
- Use `ListView` for simple rows, `TableView` for structured columns, `TreeView` for hierarchical data.
- Coalesce high-frequency conversation/tool updates before refreshing views.

## Error handling

Examples use `MessageBox.ErrorQuery` in command/file operations and scenario shell error dialogs for captured scenario logs. `UICatalogRunnable.OnIsRunningChanged` shows errors after a scenario returns.

PiSharp:

- Catch exceptions at command boundaries and background-task boundaries.
- Surface user-actionable errors in UI and log full details outside UI.
- Keep `Application.Dispose/Shutdown` protected by `finally` or `using`.

## Testing implications

The examples are demos, not test-first architecture. For PiSharp, add seams:

```csharp
public interface ITuiScreen
{
    View BuildView();
    void OnOpened();
    void OnClosed();
}

public interface ITuiDispatcher
{
    void Post(Action action);
    IDisposable Every(TimeSpan interval, Func<bool> callback);
}
```

Recommended tests:

- focus traversal and default/cancel actions;
- prompt editor typing, paste, cursor movement, and submission;
- command routing from menu/status/button/hotkey to one action;
- dispatcher use for background updates;
- dialog accepted/cancelled/invalid paths;
- table/list source refresh without losing selection unexpectedly.

## Practical PiSharp structure

Suggested organization:

```text
src/PiSharp.Tui/
  Hosting/
    TuiApplicationHost.cs
    TerminalGuiDispatcher.cs
  Shell/
    MainShell.cs
    MainMenuFactory.cs
    StatusBarController.cs
    TuiCommandRouter.cs
  Screens/
    Conversation/
    Providers/
    Settings/
    Diagnostics/
  Controls/
    PromptEditorView.cs
    ToolCallListView.cs
  Dialogs/
    TuiDialogs.cs
  State/
    ShellState.cs
    ConversationViewModel.cs
```

## Anti-patterns from demos to avoid in production

- Static fields for user/session state.
- Large lambdas that mix UI event, validation, domain mutation, and navigation.
- Timer callbacks that can outlive their view.
- Direct view mutation from background threads.
- Screen code calling `Application.Create()` or disposing the whole app.
- Hard-coded layout coordinates in complex screens.
- Dialogs without explicit result modeling.

## Bottom line

Adopt these Terminal.Gui example patterns:

- `IApplication` lifecycle at the boundary.
- descriptor/factory registration for screens.
- `Pos`/`Dim` declarative layout.
- composite views over custom drawing where possible.
- command events over raw key events for behavior.
- dispatcher/main-loop marshaling for async updates.
- disposable subscriptions/timers/dialogs.

Strengthen them for PiSharp with explicit state, test seams, command routing, and extension-safe screen registration.
