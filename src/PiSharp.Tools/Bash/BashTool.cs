using System.ComponentModel;
using System.Text;
using PiSharp.Abstractions.Environment;
using PiSharp.Abstractions.Errors;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Tools.Shared;

namespace PiSharp.Tools.Bash;

public sealed class BashTool(IExecutionEnv env, BashToolOptions? options = null) : JsonTool<BashToolInput, BashToolDetails?>(ToolSchemas.FromType<BashToolInput>())
{
    private const int UpdateThrottleMilliseconds = 100;
    private readonly IExecutionEnv _env = env;
    private readonly BashToolOptions _options = options ?? new BashToolOptions();

    public override string Name => "bash";
    public override string Label => "bash";
    public override string Description => $"Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last {Truncation.DefaultMaxLines} lines or {Truncation.DefaultMaxBytes / 1024}KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.";
    public override string PromptSnippet => "Execute bash commands (ls, grep, find, etc.)";

    public override async Task<AgentToolResult<BashToolDetails?>> ExecuteAsync(
        string toolCallId,
        BashToolInput parameters,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback<BashToolDetails?>? onUpdate = null)
    {
        var command = _options.CommandPrefix is null ? parameters.Command : $"{_options.CommandPrefix}\n{parameters.Command}";
        var spawnContext = _options.SpawnHook?.Invoke(new BashSpawnContext(command, _env.Cwd, _options.Environment)) ?? new BashSpawnContext(command, _env.Cwd, _options.Environment);
        var output = new OutputAccumulator(_env, new OutputAccumulatorOptions(TempFilePrefix: "pi-bash"));
        var receivedByteOutput = false;
        var lastUpdateAt = DateTimeOffset.MinValue;

        if (onUpdate is not null) onUpdate(new AgentToolResult<BashToolDetails?>([], null));

        async ValueTask HandleBytes(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            receivedByteOutput = true;
            await output.AppendAsync(data, token).ConfigureAwait(false);
            if (onUpdate is null) return;
            var now = DateTimeOffset.UtcNow;
            if (now - lastUpdateAt < TimeSpan.FromMilliseconds(UpdateThrottleMilliseconds)) return;
            lastUpdateAt = now;
            var snapshot = await output.SnapshotAsync(persistIfTruncated: true, token).ConfigureAwait(false);
            onUpdate(new AgentToolResult<BashToolDetails?>([new TextContent(snapshot.Content)], ToDetails(snapshot)));
        }

        var execOptions = new ExecutionOptions(
            Cwd: spawnContext.Cwd,
            Environment: spawnContext.Environment,
            Timeout: parameters.Timeout is > 0 ? TimeSpan.FromSeconds(parameters.Timeout.Value) : null,
            OnOutputBytes: HandleBytes);

        var shellResult = await _env.ExecAsync(spawnContext.Command, execOptions, cancellationToken).ConfigureAwait(false);
        if (shellResult.IsOk && !receivedByteOutput)
        {
            var combined = shellResult.Value.Stdout + shellResult.Value.Stderr;
            if (combined.Length > 0) await output.AppendAsync(Encoding.UTF8.GetBytes(combined), cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await FinishOutputAsync(output, cancellationToken).ConfigureAwait(false);
        if (shellResult.IsErr)
        {
            var status = shellResult.Error.Code switch
            {
                ExecutionErrorCode.Aborted => "Command aborted",
                ExecutionErrorCode.Timeout => parameters.Timeout is > 0 ? $"Command timed out after {parameters.Timeout.Value:g} seconds" : "Command timed out",
                _ => shellResult.Error.Message
            };
            throw new InvalidOperationException(AppendStatus(FormatOutput(snapshot, output).Text, status), shellResult.Error);
        }

        if (shellResult.Value.ExitCode != 0)
        {
            throw new InvalidOperationException(AppendStatus(FormatOutput(snapshot, output).Text, $"Command exited with code {shellResult.Value.ExitCode}"));
        }

        var (Text, Details) = FormatOutput(snapshot, output);
        return new AgentToolResult<BashToolDetails?>([new TextContent(Text)], Details);
    }

    private static async Task<OutputSnapshot> FinishOutputAsync(OutputAccumulator output, CancellationToken cancellationToken)
    {
        await output.FinishAsync(cancellationToken).ConfigureAwait(false);
        return await output.SnapshotAsync(persistIfTruncated: true, cancellationToken).ConfigureAwait(false);
    }

    private static (string Text, BashToolDetails? Details) FormatOutput(OutputSnapshot snapshot, OutputAccumulator output, string emptyText = "(no output)")
    {
        var text = string.IsNullOrEmpty(snapshot.Content) ? emptyText : snapshot.Content;
        var details = ToDetails(snapshot);
        if (snapshot.Truncation.Truncated)
        {
            var startLine = snapshot.Truncation.TotalLines - snapshot.Truncation.OutputLines + 1;
            var endLine = snapshot.Truncation.TotalLines;
            if (snapshot.Truncation.LastLinePartial)
            {
                text += $"\n\n[Showing last {Truncation.FormatSize(snapshot.Truncation.OutputBytes)} of line {endLine} (line is {Truncation.FormatSize(output.GetLastLineBytes())}). Full output: {snapshot.FullOutputPath}]";
            }
            else if (snapshot.Truncation.TruncatedBy == "lines")
            {
                text += $"\n\n[Showing lines {startLine}-{endLine} of {snapshot.Truncation.TotalLines}. Full output: {snapshot.FullOutputPath}]";
            }
            else
            {
                text += $"\n\n[Showing lines {startLine}-{endLine} of {snapshot.Truncation.TotalLines} ({Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit). Full output: {snapshot.FullOutputPath}]";
            }
        }
        return (text, details);
    }

    private static BashToolDetails? ToDetails(OutputSnapshot snapshot)
        => snapshot.Truncation.Truncated || snapshot.FullOutputPath is not null ? new BashToolDetails(snapshot.Truncation.Truncated ? snapshot.Truncation : null, snapshot.FullOutputPath) : null;

    private static string AppendStatus(string text, string status) => string.IsNullOrEmpty(text) ? status : $"{text}\n\n{status}";

}

public sealed record BashToolInput(
    [property: Description("Bash command to execute")]
    string Command,

    [property: Description("Timeout in seconds (optional, no default timeout)")]
    double? Timeout = null);

public sealed record BashToolDetails(TruncationResult? Truncation = null, string? FullOutputPath = null);

public sealed record BashToolOptions(string? CommandPrefix = null, IReadOnlyDictionary<string, string>? Environment = null, BashSpawnHook? SpawnHook = null);

public sealed record BashSpawnContext(string Command, string Cwd, IReadOnlyDictionary<string, string>? Environment = null);

public delegate BashSpawnContext BashSpawnHook(BashSpawnContext context);
