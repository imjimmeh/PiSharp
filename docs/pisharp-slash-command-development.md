# PiSharp Slash Command Development

This guide explains how built-in slash commands are structured in PiSharp and how to add new ones without reintroducing central switchboards.

## Architecture summary

Built-in slash commands use an explicit catalog pattern:

- `IBuiltInSlashCommand` is the built-in command contract.
- Each logical built-in command lives in its own file under `src/PiSharp.Cli/Commands/BuiltIn/`.
- `BuiltInSlashCommandCatalog` is the source of truth for built-in command inventory.
- `SlashCommandRegistryFactory` composes one registry from built-ins, extensions, skills, and prompt templates.
- `SlashCommandContext` is an execution context plus shared helpers, not a home for built-in command business logic.

This keeps names, aliases, descriptions, and behavior together while keeping registry composition explicit.

## Source of truth

Use these components as the architecture roots:

- Inventory and ordering: `src/PiSharp.Cli/Commands/BuiltInSlashCommandCatalog.cs`
- Individual built-in behavior: `src/PiSharp.Cli/Commands/BuiltIn/*.cs`
- Registry composition: `src/PiSharp.Cli/Commands/SlashCommandRegistryFactory.cs`

`BuiltInSlashCommands` is now a compatibility facade. Do not move logic back into it.

## Adding a built-in slash command

1. Create a class under `src/PiSharp.Cli/Commands/BuiltIn/` implementing `IBuiltInSlashCommand`.
2. Keep aliases in the class via `Names`.
3. Keep the description in the class via `Description`.
4. Put command behavior in `ExecuteAsync(...)`.
5. Use `SlashCommandContext` only for runtime access and reusable helper methods.
6. Register the new class in `BuiltInSlashCommandCatalog.Commands`.
7. If the command should appear under an existing logical alias group, add the alias to that command class instead of creating a second command class.
8. Update tests in `tests/PiSharp.Cli.Tests/Commands/SlashCommandRegistryTests.cs`.

## Alias rules

- Group aliases by logical command, not by file-per-alias.
- Preserve published slash-command inventory order unless there is an intentional behavior change.
- Keep `BuiltInSlashCommandCatalog.Names` stable when callers or tests depend on exact ordering.

## Registry composition rules

All slash-command registries should come from `SlashCommandRegistryFactory.Create(runtime)`.

Composition order is:

1. Built-ins
2. Extension commands
3. Skill commands
4. Prompt-template commands

This order preserves built-in precedence while still allowing extension collisions to be registered with suffixes by `SlashCommandRegistry`.

Do not rebuild ad hoc registries inside CLI modes or tests unless the test is specifically about raw registry behavior.

## Testing guidance

Prefer these test layers:

- Registry and inventory behavior: `tests/PiSharp.Cli.Tests/Commands/SlashCommandRegistryTests.cs`
- Registry composition behavior: tests that exercise `SlashCommandRegistryFactory`
- Focused command tests only when a command has complicated logic that is clearer to test directly

Run targeted CLI tests while iterating:

```powershell
dotnet test tests/PiSharp.Cli.Tests/PiSharp.Cli.Tests.csproj --filter FullyQualifiedName~SlashCommandRegistryTests
```

## What not to do

- Do not add a large dispatcher `switch` back into `BuiltInSlashCommands`.
- Do not put new built-in command behavior back into `SlashCommandContext`.
- Do not split aliases, descriptions, and behavior across separate global blobs.
- Do not add reflection-based discovery for built-ins.
- Do not duplicate registry composition logic across `InteractiveMode`, `RpcMode`, or tests.
