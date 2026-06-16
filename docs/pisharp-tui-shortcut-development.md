# PiSharp TUI Shortcut Development

This guide explains how built-in TUI shortcuts are structured in PiSharp and how to add new ones without reintroducing hardcoded metadata islands.

## Architecture summary

Built-in TUI shortcuts use an explicit catalog pattern:

- `ITuiBuiltInShortcutCommand` extends `ITuiShortcutCommand` with metadata.
- Global executable built-ins live in one class per logical shortcut under `src/PiSharp.Tui/Interactive/BuiltInShortcuts/`.
- `TuiBuiltInShortcutCatalog` is the source of truth for built-in shortcut inventory and projections.
- `TuiKeybindings` is a compatibility facade over the catalog.
- `TuiShortcutDispatcher.CreateDefaultAppDispatcher()` builds from the catalog.
- `TuiShortcutRegistrar` resolves built-in global keys from catalog-backed metadata.
- `HeaderView` renders declarative header hints instead of hardcoding action-id lookups.

This keeps behavior, key metadata, help metadata, slash-command hints, and header-hint metadata together.

## Source of truth

Use these components as the architecture roots:

- Inventory and ordering: `src/PiSharp.Tui/Interactive/TuiBuiltInShortcutCatalog.cs`
- Individual executable shortcut behavior: `src/PiSharp.Tui/Interactive/BuiltInShortcuts/*.cs`
- Built-in dispatcher wiring: `src/PiSharp.Tui/Interactive/TuiShortcutDispatcher.cs`
- Built-in global key map: `src/PiSharp.Tui/Interactive/TuiShortcutRegistrar.cs`
- Header hint rendering: `src/PiSharp.Tui/Interactive/Components/HeaderView.cs`

`TuiKeybindings` should stay thin. Do not move built-in architecture back into it.

## Adding a built-in executable shortcut

1. Create a class under `src/PiSharp.Tui/Interactive/BuiltInShortcuts/`.
2. Implement `ITuiBuiltInShortcutCommand` directly or derive from `TuiBuiltInShortcutCommandBase`.
3. Keep the key metadata in `Binding`.
4. Keep the execution behavior in `Execute(TuiShortcutContext context)`.
5. If the shortcut should appear in the header, expose a `HeaderHint`.
6. Register the class in `TuiBuiltInShortcutCatalog.Commands` in stable order.
7. Update tests in `tests/PiSharp.Tui.Tests/TuiShortcutTests.cs`.

## Header hint rules

Header-visible shortcuts should declare their own hint metadata through `TuiHeaderHintDescriptor`:

- `Label`
- `Keys`
- `Order`

`HeaderView` should render from ordered header hints. It should not know built-in action ids such as `model`, `thinking-level`, or `header`.

If a new visible shortcut must appear in the header, add the metadata to the shortcut class and let `TuiBuiltInShortcutCatalog.HeaderHints` project it.

## Non-executable metadata

Prompt-editor-local and transcript-navigation entries can remain structured `TuiKeybinding` records in `TuiBuiltInShortcutCatalog.Bindings` when no separate executable command object is useful.

That is acceptable as long as the catalog remains the only source of truth.

## Testing guidance

Prefer these test layers:

- Inventory, ordering, dispatcher behavior, registrar behavior, and header hints: `tests/PiSharp.Tui.Tests/TuiShortcutTests.cs`
- Visible header behavior and rendering stability: `tests/PiSharp.Tui.Tests/TuiRenderingTests.cs`

Useful targeted commands while iterating:

```powershell
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter FullyQualifiedName~TuiShortcutTests
dotnet test tests/PiSharp.Tui.Tests/PiSharp.Tui.Tests.csproj --filter FullyQualifiedName~TuiRenderingTests.Header
```

## What not to do

- Do not add another giant metadata blob to `TuiKeybindings`.
- Do not hardcode built-in delegates inside `TuiShortcutDispatcher.CreateDefaultAppDispatcher()`.
- Do not hardcode built-in header shortcut lookups in `HeaderView`.
- Do not duplicate built-in key inventories between dispatcher, registrar, help text, and header rendering.
- Do not add reflection-based discovery for built-in shortcuts.
- Do not redesign extension shortcut conflict policy as part of ordinary built-in shortcut additions.
