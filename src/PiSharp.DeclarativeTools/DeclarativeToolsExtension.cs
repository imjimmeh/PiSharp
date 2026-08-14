using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-declarative-tools",
    Name = "PiSharp Declarative Tools",
    Version = "1.0.0",
    Description = "File-based custom tools: drop a .md, .json, or .sh/.bash/.py script into a tool directory and it becomes a model-callable tool.")]

namespace PiSharp.DeclarativeTools;

/// <summary>
/// <c>pisharp-declarative-tools</c> extension entry (plan §7). Scans the configured
/// tool directories, parses declarative/script tool files, registers every accepted
/// tool through <see cref="IExtensionApi.RegisterTool"/>, subscribes to settings
/// changes for hot re-discovery, and surfaces per-file load results through the
/// <c>/declarative-tools</c> slash command.
/// </summary>
public sealed class DeclarativeToolsExtension : IExtension, IAsyncDisposable
{
    public const string NamespacePrefix = "extensions.pisharp-declarative-tools";

    private readonly ToolDirectoryScanner _scanner = new();
    private readonly ToolFileParser _parser = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<string, IDisposable> _toolRegistrations = new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();

    private IExtensionApi? _api;
    private DeclarativeToolsOptions _options = DeclarativeToolsOptions.Default;
    private DeclarativeToolLoadReport _report = DeclarativeToolLoadReport.None;
    private bool _commandRegistered;

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _api = api;
        _options = new DeclarativeToolsSettings(api.Settings).Read();

        if (!_commandRegistered)
        {
            api.RegisterCommand(new ExtensionCommandRegistration(
                "declarative-tools",
                "List declarative tool load results: discovered directories, loaded tools, and skipped files.",
                OnDeclarativeToolsCommandAsync));
            _commandRegistered = true;
        }

        _subscriptions.Add(api.Settings.OnChange(OnSettingsChanged));

        if (_options.Enabled)
        {
            await RediscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _report = new DeclarativeToolLoadReport(DateTimeOffset.UtcNow, [], [], Disabled: true);
        }
    }

    /// <summary>The latest discovery report (primarily for tests).</summary>
    public DeclarativeToolLoadReport LatestReport => _report;

    private void OnSettingsChanged(ExtensionSettingsChange change)
    {
        if (!IsOwnSettingsKey(change.Key)) return;
        var api = _api;
        if (api is null) return;
        _options = new DeclarativeToolsSettings(api.Settings).Read();
        _ = RediscoverAsync(CancellationToken.None);
    }

    private static bool IsOwnSettingsKey(string key)
        => key.StartsWith(NamespacePrefix, StringComparison.Ordinal)
           || key is "enabled" or "toolsDir" or "timeoutSeconds" or "additionalProperties";

    private async Task RediscoverAsync(CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null) return;

        IReadOnlyList<string> directories = Array.Empty<string>();
        var entries = new List<ToolLoadEntry>();
        var nextRegistrations = new Dictionary<string, IDisposable>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            directories = _scanner.ResolveToolDirectories(_options.ToolsDir, api.Cwd);
            var files = _scanner.Scan(directories);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ToolDefinition? definition;
                string? diagnostic;
                try
                {
                    definition = _parser.Parse(file, out diagnostic);
                }
                catch (Exception exception)
                {
                    definition = null;
                    diagnostic = $"Parse failed: {exception.Message}";
                }
                if (definition is null)
                {
                    entries.Add(new ToolLoadEntry(file.DefaultName, ToolLoadStatus.Skipped, diagnostic ?? "Unknown parse failure."));
                    continue;
                }

                if (!seenNames.Add(definition.Name))
                {
                    entries.Add(new ToolLoadEntry(definition.Name, ToolLoadStatus.Skipped, "Duplicate tool name; first definition wins."));
                    continue;
                }

                if (definition.IsScript && api.ExecutionEnv is null)
                {
                    entries.Add(new ToolLoadEntry(definition.Name, ToolLoadStatus.Skipped, "Host does not provide an execution environment; script tools cannot run."));
                    continue;
                }

                try
                {
                    var registration = RegisterTool(api, definition);
                    nextRegistrations[definition.Name] = registration;
                    entries.Add(new ToolLoadEntry(definition.Name, ToolLoadStatus.Loaded, definition.IsScript ? $"script ({Path.GetExtension(definition.ScriptPath)})" : "declarative"));
                }
                catch (Exception exception)
                {
                    entries.Add(new ToolLoadEntry(definition.Name, ToolLoadStatus.Skipped, $"Registration failed: {exception.Message}"));
                }
            }
        }
        catch (Exception exception)
        {
            // Errors never kill the plugin: record the failure in the report.
            entries.Add(new ToolLoadEntry("-", ToolLoadStatus.Skipped, $"Discovery failed: {exception.Message}"));
        }

        lock (_registrationGate)
        {
            foreach (var (name, handle) in _toolRegistrations)
            {
                if (nextRegistrations.ContainsKey(name)) continue;
                handle.Dispose();
            }
            _toolRegistrations.Clear();
            foreach (var (name, handle) in nextRegistrations) _toolRegistrations[name] = handle;
        }

        _report = new DeclarativeToolLoadReport(DateTimeOffset.UtcNow, directories, entries, Disabled: false);
    }

    private IDisposable RegisterTool(IExtensionApi api, ToolDefinition definition)
    {
        var executeAsync = definition.IsScript
            ? ScriptToolExecutor.Create(api.ExecutionEnv!, definition, _options.TimeoutSeconds)
            : RejectDeclarativeInvocation(definition);

        return api.RegisterTool(new ExtensionToolRegistration(
            Name: definition.Name,
            Label: definition.Label,
            Description: definition.Description,
            ParametersSchema: definition.ParametersSchema,
            ExecuteAsync: executeAsync,
            ExecutionMode: definition.ExecutionMode,
            PromptSnippet: definition.PromptSnippet,
            PromptGuidelines: definition.PromptGuidelines,
            RendererName: definition.RendererName,
            Override: definition.Override));
    }

    private static ExtensionToolExecuteAsync RejectDeclarativeInvocation(ToolDefinition definition)
        => (_, _, _, _) => throw new InvalidOperationException(
            $"Declarative tool '{definition.Name}' has no executable body; add a script or convert it to a script tool.");

    private async Task OnDeclarativeToolsCommandAsync(string args, CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null) return;
        await api.SendMessageAsync(AgentMessages.User(_report.Format()), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        lock (_registrationGate)
        {
            foreach (var handle in _toolRegistrations.Values) handle.Dispose();
            _toolRegistrations.Clear();
        }
        await Task.CompletedTask;
    }
}
