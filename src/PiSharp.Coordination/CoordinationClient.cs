using System.IO.Pipes;
using System.Text.Json;

namespace PiSharp.Coordination;

public sealed class CoordinationClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _pipeName;

    public CoordinationClient(CoordinationEndpoint endpoint)
    {
        _pipeName = endpoint.PipeName;
    }

    public async Task RegisterAgentAsync(AgentRegistration registration, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "register_agent",
            registration.AgentId,
            registration.ProcessId,
            registration.SessionId,
            registration.ParentSessionId,
            registration.Cwd,
            timestamp = DateTimeOffset.UtcNow,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    public async Task UnregisterAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "unregister_agent",
            agentId,
            timestamp = DateTimeOffset.UtcNow,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    public async Task<RosterResponse> GetRosterAsync(CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new { type = "get_roster" }, SerializerOptions);
        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
        return JsonSerializer.Deserialize<RosterResponse>(response, SerializerOptions)!;
    }

    public async Task SendHeartbeatAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "heartbeat",
            agentId,
            timestamp = DateTimeOffset.UtcNow,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    public async Task SendMessageAsync(string messageId, string fromAgentId, string to, string body, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "send_message",
            messageId,
            fromAgentId,
            to,
            body,
            timestamp = DateTimeOffset.UtcNow,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    public async Task<InboxResponse> GetInboxAsync(string agentId, DateTimeOffset? sinceTimestamp = null, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "get_inbox",
            agentId,
            sinceTimestamp,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
        return JsonSerializer.Deserialize<InboxResponse>(response, SerializerOptions)!;
    }

    public async Task RecordFileReadAsync(string agentId, string path, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "record_file_read",
            agentId,
            path,
            timestamp = DateTimeOffset.UtcNow,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    public async Task RecordFileWriteAsync(string agentId, string path, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "record_file_write",
            agentId,
            path,
            timestamp = DateTimeOffset.UtcNow,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    public async Task<PreflightResponse> PreflightToolAsync(string agentId, string tool, JsonElement arguments, string? path = null, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "preflight_tool",
            agentId,
            tool,
            arguments,
            path,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
        return JsonSerializer.Deserialize<PreflightResponse>(response, SerializerOptions)!;
    }

    public async Task<BriefResponse> GetBriefAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "get_brief",
            agentId,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
        return JsonSerializer.Deserialize<BriefResponse>(response, SerializerOptions)!;
    }

    public async Task RecordSubagentEventAsync(SubagentObservedRecord record, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            type = "record_subagent_event",
            record.SubagentId,
            record.SubagentType,
            record.Description,
            record.Status,
            record.EventName,
            record.DurationMs,
            record.ToolUses,
            record.InputTokens,
            record.OutputTokens,
            record.ParentSessionId,
            record.Cwd,
            timestamp = record.Timestamp,
        }, SerializerOptions);

        var response = await SendReceiveAsync(request, cancellationToken);
        EnsureOk(response);
    }

    private async Task<string> SendReceiveAsync(string requestJson, CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(5000);
        try
        {
            await client.ConnectAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Connection to coordination daemon timed out after 5 seconds.");
        }

        using var reader = new StreamReader(client);
        using var writer = new StreamWriter(client) { AutoFlush = true };

        await writer.WriteLineAsync(requestJson.AsMemory(), cancellationToken);

        var response = await reader.ReadLineAsync(cancellationToken);
        if (response is null)
            throw new InvalidOperationException("Daemon closed connection without response.");

        return response;
    }

    private static void EnsureOk(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean())
            return;

        var error = root.TryGetProperty("error", out var errorElement)
            ? errorElement.GetString()
            : "Unknown error";

        throw new InvalidOperationException($"Daemon returned error: {error}");
    }
}
