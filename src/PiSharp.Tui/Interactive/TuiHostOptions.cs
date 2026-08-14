using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Tools;
using PiSharp.Agent.Resources.Theme;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive.Components;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive;

public sealed record TuiCommandDispatchRequest(
    string Text,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>> SelectAsync,
    Func<string, CancellationToken, Task<string?>> InputAsync,
    Func<string, bool, CancellationToken, Task> NotifyAsync,
    Func<Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>, Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>>, JsonlSessionMetadata?, CancellationToken, Task<JsonlSessionMetadata?>>? SelectSessionMetadataAsync = null);

public sealed record TuiCommandDispatchResult(bool Handled, bool ShouldExit = false);

public sealed record TuiSessionSnapshot(
    string SessionId,
    string? SessionFile,
    string? SessionName,
    IReadOnlyList<SessionTreeEntry> BranchEntries);

public sealed record TuiInputHookResult(bool Handled, string Text, IReadOnlyList<ImageContent>? Images);

internal sealed record TuiHostRunContext(
    Window Window,
    HeaderView Header,
    ChatView Chat,
    PromptEditor Prompt,
    FooterView Footer,
    IConsoleDriver Driver,
    Func<TuiRenderState> GetState,
    Action RequestRender,
    Action<string> InvokeCommand);

public sealed record TuiHostOptions(
    ITuiRuntimeFacade Runtime,
    string SessionId,
    string? SessionFile,
    Func<CancellationToken, Task<string?>> GetSessionNameAsync,
    Func<TuiCommandDispatchRequest, CancellationToken, Task<TuiCommandDispatchResult>>? DispatchCommandAsync = null,
    Func<string, IReadOnlyList<string>>? CompleteCommand = null,
    string? WorkingDirectory = null,
    Func<TuiRenderState, TuiFooterSnapshot>? FooterSnapshot = null,
    Action<ExtensionUiBridgeHost>? ConfigureUiBridge = null,
    IReadOnlyList<string>? StartupMessages = null,
    Func<Func<string, Task>, CancellationToken, Task>? PostStartupChecksAsync = null,
    TuiThemeDocument? Theme = null,
    Func<IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>>>? GetExtensionShortcuts = null,
    Func<ExtensionRegistry?>? GetExtensionRegistry = null,
    Func<string, IAgentTool?>? ResolveTool = null,
    Func<CancellationToken, Task>? CycleThinkingLevelAsync = null,
    Func<string, string, CancellationToken, Task<(string Text, IReadOnlyList<ImageContent> Images)>>? ProcessFileReferencesAsync = null,
    Func<string, IReadOnlyList<ImageContent>?, string, CancellationToken, Task<TuiInputHookResult>>? ProcessInputAsync = null,
    Func<CancellationToken, Task<TuiSessionSnapshot>>? GetSessionSnapshotAsync = null,
    Func<string, CancellationToken, Task>? ForkFromEntryAsync = null,
    Func<TuiExtensionLoadStatus>? GetExtensionLoadStatus = null,
    ITerminalScreenSession? TerminalScreenSession = null,
    IReadOnlySet<string>? ExtensionLoadCommandWhitelist = null,
    TuiTimingOptions? TimingOptions = null,
    ILoggerFactory? LoggerFactory = null)
{
    public Func<CancellationToken, Task>? OnHarnessReplaced { get; set; }

    internal IConsoleDriver? ConsoleDriver { get; init; }

    internal TuiProfilingCounters? ProfilingCounters { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; } = LoggerFactory;

    internal Func<TuiHostRunContext, CancellationToken, Task>? BeforeRunAsync { get; init; }

    internal TimeSpan? TransientSystemMessageLifetime { get; init; }
}
