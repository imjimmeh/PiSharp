using Microsoft.Extensions.Logging;
using PiSharp.Abstractions.Sessions;
using PiSharp.Ai.Auth;
using PiSharp.Agent.Core.Events;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Runtime;
using PiSharp.Compatibility.Settings;
using PiSharp.Packages;
using PiSharp.Runtime;
using PiSharp.Server.Contracts;
using PiSharp.Server.Hosting;
using PiSharp.Server.Runtime;
using PiSharp.Server.UiBridge;

namespace PiSharp.Cli.Modes;

/// <summary>
/// Static factory that builds every <see cref="PiServerHostOptions"/> command/input/startup
/// delegate bound to a session's <see cref="SessionRuntime"/> and a session-scoped UI bridge,
/// mirroring <see cref="InteractiveMode"/>'s in-process command handling so daemon-mode slash
/// commands, input processing, completion, and startup messages execute instead of returning
/// <c>not_available</c>.
/// </summary>
public static class DaemonCommandHost
{
    private const long InteractiveResponseTimeoutSeconds = 5 * 60;

    /// <summary>
    /// Builds the daemon host options with every command/input/startup delegate wired to the
    /// session runtime. <paramref name="resolveSession"/> supplies the target session for
    /// <c>process_input</c> (whose request carries no session id); when null that lane keeps
    /// its guarded default.
    /// </summary>
    public static PiServerHostOptions CreateHostOptions(
        string apiKey,
        ILoggerFactory? loggerFactory = null,
        Func<LiveServerSession?>? resolveSession = null)
    {
        return new PiServerHostOptions
        {
            ApiKey = apiKey,
            LoggerFactory = loggerFactory,
            RunCommandAsync = async (context, text, options, ct) =>
            {
                var runtime = context.Session.Runtime;
                var logger = loggerFactory?.CreateLogger(nameof(DaemonCommandHost));
                var name = text.Trim()[1..].Split([' '], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                logger?.LogDebug("daemon run_command start text={Text}", text);
                var slash = new SlashCommandContext(
                    name,
                    runtime,
                    SelectAsync: (label, choices, t) => UiSelectAsync(context, label, choices, t),
                    InputAsync: (prompt, t) => UiInputAsync(context, prompt, t),
                    NotifyAsync: (msg, t) => UiNotifyAsync(context.Session, msg),
                    OAuthStorage: new FileOAuthStorage(PiAgentPaths.FromCwd(runtime.Session.Metadata.Cwd).AuthPath),
                    SubmitPromptAsync: null,
                    SelectSessionMetadataAsync: (loadCurrent, loadAll, current, t) => UiSelectSessionAsync(context, loadCurrent, loadAll, current, t),
                    OpenUrlAsync: OAuthBrowserLauncher.OpenAsync);
                var result = await SlashCommandRegistryFactory.Create(runtime).ExecuteAsync(text, slash, ct);
                logger?.LogDebug("daemon run_command end text={Text} handled={Handled} shouldExit={ShouldExit} isError={IsError} hasMessage={HasMessage}",
                    text, result.Handled, result.ShouldExit, result.IsError, result.Message is not null);
                return new ServerCommandResult(result.Handled, result.Message, result.IsError, result.ShouldExit);
            },
            CompleteCommandAsync = (session, text, ct) =>
                Task.FromResult(SlashCommandRegistryFactory.Create(session.Runtime).Complete(text)),
            ProcessInputAsync = async (request, ct) =>
            {
                var runtime = resolveSession?.Invoke()?.Runtime ?? throw new InvalidOperationException("No daemon session available for process_input.");
                if (request.Text.StartsWith("!", StringComparison.Ordinal) && request.Text.Length > 1)
                {
                    var excludeFromContext = request.Text.StartsWith("!!", StringComparison.Ordinal);
                    var command = excludeFromContext ? request.Text[2..].Trim() : request.Text[1..].Trim();
                    if (!string.IsNullOrEmpty(command))
                    {
                        var bashResult = await runtime.DispatchUserBashAsync(command, excludeFromContext, ct).ConfigureAwait(false);
                        if (bashResult?.Result is { } execution)
                        {
                            var output = execution.Error.Length > 0 ? $"{execution.Output}\n\n{execution.Error}" : execution.Output;
                            return new ProcessInputResult(true, output, null);
                        }
                    }
                }
                var result = await runtime.DispatchInputAsync(request.Text, request.Images, request.Source, ct).ConfigureAwait(false);
                return new ProcessInputResult(result.Handled, result.Text, result.Images);
            },
            GetStartupMessagesAsync = (session, ct) =>
                Task.FromResult(new ServerStartupMessages(StartupResourceSummary.Create(session.Runtime))),
            PostStartupChecksAsync = async (session, emit, ct) =>
            {
                var runtime = session.Runtime;
                var offline = string.Equals(Environment.GetEnvironmentVariable("PI_OFFLINE"), "1", StringComparison.Ordinal)
                    || runtime.SettingsSnapshot?.Settings.Offline == true;
                if (offline) return;
                var packages = runtime.Resources?.Packages ?? [];
                var checker = new NpmOutdatedChecker(new NpmRegistryClient(new HttpClient()));
                var outdated = await checker.CheckAsync(packages, ct);
                var message = OutdatedPackagesSummary.Format(outdated);
                if (message is not null) emit(message);
                await CheckSelfUpdateAsync(runtime, message => { emit(message); return Task.CompletedTask; }, ct);
            },
            GetMcpStatusAsync = _ => UnavailableMcpStatus(),
        };
    }

    private static async Task<string?> UiSelectAsync(PiServerHostContext context, string label, IReadOnlyList<string> choices, CancellationToken ct)
    {
        var response = await context.UiBridge.RequestUiAsync(
            new ServerUiIntent(Guid.NewGuid().ToString("N"), "select", label, label, choices, null),
            context.Session, TimeSpan.FromSeconds(InteractiveResponseTimeoutSeconds), ct);
        return response.Cancelled ? null : response.Value?.ToString();
    }

    private static async Task<string?> UiInputAsync(PiServerHostContext context, string prompt, CancellationToken ct)
    {
        var response = await context.UiBridge.RequestUiAsync(
            new ServerUiIntent(Guid.NewGuid().ToString("N"), "input", prompt, prompt, null, null),
            context.Session, TimeSpan.FromSeconds(InteractiveResponseTimeoutSeconds), ct);
        return response.Cancelled ? null : response.Value?.ToString();
    }

    private static Task UiNotifyAsync(LiveServerSession session, string message)
    {
        session.EmitEvent(AgentSessionEvent.FromServer("system_message", new { text = message }));
        return Task.CompletedTask;
    }

    private static async Task<JsonlSessionMetadata?> UiSelectSessionAsync(
        PiServerHostContext context,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadCurrent,
        Func<CancellationToken, Task<IReadOnlyList<JsonlSessionMetadata>>> loadAll,
        JsonlSessionMetadata? current,
        CancellationToken ct)
    {
        var sessions = await loadAll(ct).ConfigureAwait(false);
        if (sessions.Count == 0) return current;
        var labels = sessions.Select(session => $"{session.Id} {session.Path}").ToArray();
        var selected = await UiSelectAsync(context, "Select session", labels, ct).ConfigureAwait(false);
        if (selected is null) return null;
        return sessions.FirstOrDefault(session =>
            string.Equals(session.Id, selected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(session.Path, selected, StringComparison.OrdinalIgnoreCase))
            ?? current;
    }

    /// <summary>
    /// Wire-level "unavailable" MCP status (no servers). The MCP client plugin (PiSharp.Mcp)
    /// ships in an app-base assembly the CLI cannot reference, so no status provider is
    /// reachable from the daemon host; returning the default keeps <c>mcp_status</c> from
    /// crashing the command lane.
    /// </summary>
    private static Task<McpStatusResult> UnavailableMcpStatus()
        => Task.FromResult(new McpStatusResult([]));

    private static async Task CheckSelfUpdateAsync(
        SessionRuntime runtime,
        Func<string, Task> inject,
        CancellationToken token)
    {
        try
        {
            var checkOnStartup = true;
            try
            {
                var node = runtime.SettingsSnapshot?.Merged.Root["selfUpdate"]?["checkOnStartup"];
                if (node is not null) checkOnStartup = node.GetValue<bool>();
            }
            catch
            {
                // Unparseable value; default to on.
            }
            if (!checkOnStartup) return;

            var selfInfo = await new SelfUpdateChecker(new NuGetRegistryClient(new HttpClient()))
                .CheckAsync(VersionInfo.Current, offline: false, token);
            var selfMessage = SelfUpdateSummary.Format(selfInfo);
            if (selfMessage is not null) await inject(selfMessage);
        }
        catch
        {
            // The startup check lane must never crash the daemon.
        }
    }
}
