using PiSharp.Server.Contracts;

namespace PiSharp.Server.Hosting;

public sealed record PiServerHostOptions
{
    public required string ApiKey { get; init; }
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public Func<PiServerHostContext, string, SlashCommandExecutionOptions?, CancellationToken, Task<ServerCommandResult>>? RunCommandAsync { get; init; }
    public Func<string, CancellationToken, Task<IReadOnlyList<string>>>? CompleteCommandAsync { get; init; }
    public Func<ProcessInputRequest, CancellationToken, Task<ProcessInputResult>>? ProcessInputAsync { get; init; }
    public Func<CancellationToken, Task<ServerStartupMessages>>? GetStartupMessagesAsync { get; init; }
    public Func<Action<string>, CancellationToken, Task>? PostStartupChecksAsync { get; init; }
}
