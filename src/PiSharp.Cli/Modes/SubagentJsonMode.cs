using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Serialization;
using PiSharp.Cli.IO;
using PiSharp.Runtime;
using PiSharp.Runtime.Subagents;

namespace PiSharp.Cli.Modes;

public static class SubagentJsonMode
{
    public static async Task<int> RunAsync(SessionRuntime runtime, SubagentJsonModeOptions options, IConsoleIO console, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(console);

        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(nameof(SubagentJsonMode));
        var prompts = BuildPromptList(options);
        logger.LogInformation("Subagent JSON mode started promptCount={PromptCount}", prompts.Count);

        await using var guard = StdoutGuard.TakeOver(console);
        await using var service = new SubagentSessionService(runtime);
        var handle = await service.CreateAsync(new SubagentSessionOptions(), cancellationToken);
        await guard.ProtocolOut.WriteLineAsync(AgentJsonSerializer.Serialize(new
        {
            type = "session",
            sessionId = handle.SessionId,
            sessionFile = handle.Session.Metadata.Path,
            cwd = handle.Session.Metadata.Cwd
        }).AsMemory(), cancellationToken);

        await using var writer = new JsPiSubagentEventWriter(guard.ProtocolOut, leaveOpen: true);
        using var subscription = service.Subscribe(handle.SessionId, (evt, ct) => writer.WriteAsync([evt], ct));

        AssistantMessage? finalMessage = null;
        foreach (var prompt in prompts)
        {
            logger.LogDebug("Subagent prompt submitted length={Length}", prompt.Length);
            var result = await service.PromptAsync(handle.SessionId, prompt, cancellationToken);
            finalMessage = result.FinalMessage;
        }

        await guard.ProtocolOut.FlushAsync(cancellationToken);
        if (finalMessage is not null && IsFailure(finalMessage))
        {
            logger.LogError("Subagent JSON mode agent failure stopReason={StopReason} hasError={HasError}", finalMessage.StopReason, !string.IsNullOrWhiteSpace(finalMessage.ErrorMessage));
            return 1;
        }
        return 0;
    }

    private static IReadOnlyList<string> BuildPromptList(SubagentJsonModeOptions options)
    {
        var prompts = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.InitialMessage)) prompts.Add(options.InitialMessage);
        if (options.Messages is not null) prompts.AddRange(options.Messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        return prompts;
    }

    private static bool IsFailure(AssistantMessage message)
        => string.Equals(message.StopReason, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(message.StopReason, "aborted", StringComparison.OrdinalIgnoreCase)
           || !string.IsNullOrWhiteSpace(message.ErrorMessage);
}
