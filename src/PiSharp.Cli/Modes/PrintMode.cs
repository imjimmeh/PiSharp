using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Serialization;
using PiSharp.Cli.IO;
using PiSharp.Runtime;

namespace PiSharp.Cli.Modes;

public enum PrintOutputMode { Text, Json }

public sealed record PrintModeOptions(
    PrintOutputMode Mode,
    string? InitialMessage = null,
    IReadOnlyList<string>? Messages = null,
    IReadOnlyList<ImageContent>? InitialImages = null);

public static class PrintMode
{
    public static async Task<int> RunAsync(SessionRuntime runtime, PrintModeOptions options, IConsoleIO console, CancellationToken cancellationToken = default, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(nameof(PrintMode));
        var prompts = BuildPromptList(options);
        logger.LogInformation("Print mode started mode={Mode} promptCount={PromptCount}", options.Mode, prompts.Count);
        if (prompts.Count == 0) return 0;
        return options.Mode == PrintOutputMode.Json
            ? await RunJsonAsync(runtime, options, prompts, console, logger, cancellationToken)
            : await RunTextAsync(runtime, options, prompts, console, logger, cancellationToken);
    }

    private static async Task<int> RunTextAsync(SessionRuntime runtime, PrintModeOptions options, IReadOnlyList<string> prompts, IConsoleIO console, ILogger logger, CancellationToken cancellationToken)
    {
        AssistantMessage? last = null;
        for (var index = 0; index < prompts.Count; index++)
        {
            logger.LogDebug("Print prompt submitted index={Index} length={Length}", index, prompts[index].Length);
            var result = await runtime.SubmitPromptAsync(prompts[index], options.InitialImages, "rpc", cancellationToken);
            if (result is not null) last = result;
        }
        if (last is null) return 0;
        if (IsFailure(last))
        {
            logger.LogError("Print mode agent failure stopReason={StopReason} hasError={HasError}", last.StopReason, !string.IsNullOrWhiteSpace(last.ErrorMessage));
            await console.Error.WriteLineAsync(last.ErrorMessage ?? last.StopReason ?? "Agent failed.");
            return 1;
        }
        var text = string.Concat(last.Content.OfType<TextContent>().Select(content => content.Text));
        if (text.Length > 0) await console.Out.WriteLineAsync(text);
        return 0;
    }

    private static async Task<int> RunJsonAsync(SessionRuntime runtime, PrintModeOptions options, IReadOnlyList<string> prompts, IConsoleIO console, ILogger logger, CancellationToken cancellationToken)
    {
        await using var guard = StdoutGuard.TakeOver(console);
        using var subscription = runtime.Harness.Subscribe(async (evt, _) => await guard.ProtocolOut.WriteLineAsync(AgentJsonSerializer.Serialize(evt)));
        for (var index = 0; index < prompts.Count; index++)
        {
            logger.LogDebug("Print prompt submitted index={Index} length={Length}", index, prompts[index].Length);
            await runtime.SubmitPromptAsync(prompts[index], options.InitialImages, "rpc", cancellationToken);
        }
        await guard.ProtocolOut.FlushAsync();
        return 0;
    }

    private static IReadOnlyList<string> BuildPromptList(PrintModeOptions options)
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
