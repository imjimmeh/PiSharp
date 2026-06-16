# PiSharp Tools

PiSharp tools are model-callable operations implemented through `IAgentTool` in `PiSharp.Agent.Core.Tools`. Built-in tools live in `src/PiSharp.Tools`, while extensions can register additional tools through `IExtensionApi.Tools` or `IExtensionApi.RegisterTool()`.

## Built-in tools

`BuiltInTools.CreateAll()` registers these tools:

| Tool | Purpose |
| --- | --- |
| `read` | Read text files and supported image files. |
| `bash` | Execute shell commands through the configured `IExecutionEnv`. |
| `edit` | Apply exact-text replacements to existing files. |
| `write` | Create or overwrite files. |
| `grep` | Search file contents. |
| `find` | Find files by pattern. |
| `ls` | List directory contents. |

`BuiltInTools.CreateReadOnly()` registers only `read`, `grep`, `find`, and `ls`.

The runtime can restrict available tools with CLI/runtime options:

- `--tools` / `-t` selects a comma-separated active-tool list.
- `--no-tools` / `-nt` disables all tools.
- `--no-builtin-tools` / `-nbt` starts without built-in tools while still allowing extensions to register tools.

## Tool contract

All tools implement `IAgentTool`:

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Label { get; }
    string Description { get; }
    string? PromptSnippet => null;
    IReadOnlyList<string> PromptGuidelines => [];
    JsonElement ParametersSchema { get; }
    ToolExecutionMode? ExecutionMode { get; }
    JsonElement PrepareArguments(JsonElement args);

    Task<AgentToolResult<object?>> ExecuteAsync(
        string toolCallId,
        JsonElement parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<object?>? onUpdate = null);
}
```

Typed tools can implement `IAgentTool<TParameters, TDetails>` and use .NET types internally while still exposing JSON to the model/provider layer.

## Parameters and schemas

Every tool exposes a JSON schema through `ParametersSchema`. Built-in tools use `ToolSchemas` and related helpers to produce JSON-compatible schemas. Extension tools provide the schema directly as a `JsonElement`.

`PrepareArguments()` is called before execution and can normalize model-supplied arguments. Most tools return the arguments unchanged; extensions can supply a custom preparation delegate.

## Results and details

Tools return `AgentToolResult<TDetails>`:

```csharp
public sealed record AgentToolResult<TDetails>(
    IReadOnlyList<MessageContent> Content,
    TDetails Details,
    bool Terminate = false);
```

- `Content` is model-visible message content, usually text.
- `Details` is structured metadata for UI, logs, or extension handling.
- `Terminate` can stop the current loop when a tool intentionally ends execution.

The TypeScript bridge maps TypeScript tool results into the same shape: content, details, terminate, and error state.

## Execution mode and updates

`ToolExecutionMode` can be `Sequential` or `Parallel`. A null execution mode lets the agent loop choose the default behavior.

Tools may report partial progress through `AgentToolUpdateCallback<TDetails>`. The harness publishes tool execution start/update/end events so UI and extensions can display progress.

## File and shell behavior

Tool implementations share utilities in `src/PiSharp.Tools/Shared`:

- `PathUtilities` resolves paths relative to the execution environment/current working directory.
- `FileMutationQueue` serializes file mutations to avoid unsafe concurrent writes.
- `Truncation` and `OutputAccumulator` keep large outputs bounded and report truncation metadata.
- `ImageUtilities` supports image handling for `read`.

All filesystem and shell access goes through `IExecutionEnv`, `IFileSystem`, and `IShell` abstractions from `PiSharp.Abstractions`, which keeps tools testable and allows alternate execution environments.

User-entered shell commands use the same policy boundary and are extension-visible through `user_bash`:

- In the interactive TUI, enter `! <command>` to offer a command to the user-bash hook surface.
- Enter `!! <command>` to request `excludeFromContext`.
- In RPC mode, send `{ "type": "bash", "command": "...", "excludeFromContext": true }`.
- RPC `abort_bash` is accepted and returns a no-active-operation response until a long-lived bash runner is introduced.

Extensions may handle the request by returning a completed result or operations that transform command metadata. The first result wins. Hook handling does not bypass shell/tool policy checks.

## Package CLI

PiSharp supports Pi-style package lifecycle commands before runtime startup:

```bash
pisharp install npm:@scope/package
pisharp install npm:@scope/package@1.2.3 --force
pisharp install git:https://github.com/org/repo.git#main
pisharp install ./local-package --local
pisharp remove npm:@scope/package
pisharp uninstall npm:@scope/package
pisharp update
pisharp update npm:@scope/package
pisharp update --extension npm:@scope/package
pisharp update --extensions
pisharp update self
pisharp list
```

Supported package source syntax:

- `npm:<name>` or `npm:<name>@<version>` installs through npm into the managed package root.
- `git:<url>` or `git:<url>#<ref>` clones or updates a Git package under the managed package root. The URL body may use HTTPS, SSH, or SCP-like Git syntax.
- Bare `https://`, `http://`, and `ssh://` URLs are treated as Git sources.
- Relative, absolute, and `~` paths are treated as local packages.

Global package installs update the global Pi/PiSharp package settings. `--local` writes only project settings. Package contents are resolved by the runtime through `pi.extensions`, `pi.skills`, `pi.promptTemplates`, `pi.themes`, and conventional package child directories.

The managed install root is `~/.pi/agent/packages`. Git package paths are normalized under that root and protected against escaping the managed tree. Local package references are not copied; they are recorded as references after validation.

`--offline` prevents network install/update work for npm and Git sources but still updates settings for install commands. Self-update commands are parsed for parity, but PiSharp reports that self-update is not implemented and should be handled through the user's package manager.

## Registering extension tools

Native extensions register tools with `ExtensionToolRegistration`:

```csharp
api.RegisterTool(new ExtensionToolRegistration(
    Name: "greet",
    Label: "Greet",
    Description: "Greet someone by name.",
    ParametersSchema: schema,
    ExecuteAsync: async (toolCallId, parameters, token, onUpdate) =>
    {
        var name = parameters.GetProperty("name").GetString() ?? "world";
        return new AgentToolResult<object?>(
            [new TextContent($"Hello, {name}!")],
            new { name });
    }));
```

Registration options include:

- `ExecutionMode`
- `PromptSnippet`
- `PromptGuidelines`
- `PrepareArguments`
- `RenderShell`
- `RendererName`
- `Override`

By default, duplicate tool names are rejected. Use `ExtensionOverridePolicy.Override` for extension tool overrides and `ExtensionOverridePolicy.OverrideBuiltIn` when intentionally replacing a built-in tool.

## Tool events and middleware

Tool execution is visible to extensions through events such as:

- `tool_call`
- `tool_result`
- `tool_execution_start`
- `tool_execution_update`
- `tool_execution_end`

Native extension middleware can inspect and block tool calls or modify tool results:

```csharp
api.Use(async (context, next, cancellationToken) =>
{
    if (context.BeforeToolCall?.ToolName == "bash")
    {
        // Set context.Blocked and context.BlockReason to deny execution.
    }

    await next(context, cancellationToken);
});
```
