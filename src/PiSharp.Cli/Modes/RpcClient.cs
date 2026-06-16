using PiSharp.Abstractions.Messages;
using PiSharp.Abstractions.Options;
using PiSharp.Agent.Serialization;

namespace PiSharp.Cli.Modes;

public sealed class RpcClient(TextReader fromServer, TextWriter toServer)
{
    public async Task SendAsync(object command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await toServer.WriteLineAsync(AgentJsonSerializer.Serialize(command));
        await toServer.FlushAsync();
    }

    public async Task<RpcResponse?> ReadResponseAsync(CancellationToken cancellationToken = default)
    {
        var line = await fromServer.ReadLineAsync(cancellationToken);
        return line is null ? null : AgentJsonSerializer.Deserialize<RpcResponse>(line);
    }

    public Task PromptAsync(string message, string? id = null, IReadOnlyList<ImageContent>? images = null, string? streamingBehavior = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcPromptCommand("prompt", id, message, images, streamingBehavior), cancellationToken);

    public Task SteerAsync(string message, string? id = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcPromptCommand("steer", id, message), cancellationToken);

    public Task FollowUpAsync(string message, string? id = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcPromptCommand("follow_up", id, message), cancellationToken);

    public Task SetModelAsync(string provider, string modelId, string? id = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcSetModelCommand("set_model", id, provider, modelId), cancellationToken);

    public Task SetThinkingLevelAsync(ThinkingLevel level, string? id = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcSetThinkingLevelCommand("set_thinking_level", id, level.ToString().ToLowerInvariant()), cancellationToken);

    public Task SetSessionNameAsync(string name, string? id = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcSessionNameCommand("set_session_name", id, name), cancellationToken);

    public Task ForkAsync(string entryId, string? id = null, CancellationToken cancellationToken = default)
        => SendAsync(new RpcEntryCommand("fork", id, entryId), cancellationToken);
}
