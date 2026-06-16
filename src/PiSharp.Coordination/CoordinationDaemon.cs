using System.IO.Pipes;
using System.Text.Json;

namespace PiSharp.Coordination;

public sealed class CoordinationDaemon : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly CoordinationJsonlStore _store;
    private readonly CoordinationState _state;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts;
    private Task _serverTask;

    public CoordinationEndpoint Endpoint { get; }
    internal CoordinationState State => _state;

    private CoordinationDaemon(CoordinationJsonlStore store, CoordinationState state, string pipeName)
    {
        _store = store;
        _state = state;
        _pipeName = pipeName;
        Endpoint = new CoordinationEndpoint(pipeName);
        _cts = new CancellationTokenSource();
        _serverTask = Task.CompletedTask;
    }

    public static async Task<CoordinationDaemon> StartAsync(string directory, string pipeName)
    {
        var store = new CoordinationJsonlStore(directory);
        var state = new CoordinationState();

        var records = await store.ReadAllAsync();
        foreach (var record in records)
            state.Apply(record);

        var daemon = new CoordinationDaemon(store, state, pipeName);
        var ready = new TaskCompletionSource();
        daemon._serverTask = Task.Run(() => daemon.RunServerAsync(daemon._cts.Token, ready));
        await ready.Task;
        return daemon;
    }

    private async Task RunServerAsync(CancellationToken cancellationToken, TaskCompletionSource ready)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                ready.TrySetResult();

                try
                {
                    await server.WaitForConnectionAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    using var reader = new StreamReader(server);
                    using var writer = new StreamWriter(server) { AutoFlush = true };

                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                    {
                        var responseJson = await ProcessRequestAsync(line);
                        await writer.WriteLineAsync(responseJson);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
    }

    private async Task<string> ProcessRequestAsync(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
            return ErrorResponse("Request line is empty.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestJson);
        }
        catch (JsonException ex)
        {
            return ErrorResponse($"Malformed JSON request: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return ErrorResponse("Missing 'type' field in request.");

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
                return ErrorResponse("Blank 'type' field in request.");

            try
            {
                return type switch
                {
                    "register_agent" => await HandleRegisterAgentAsync(root),
                    "unregister_agent" => await HandleUnregisterAgentAsync(root),
                    "heartbeat" => await HandleHeartbeatAsync(root),
                    "get_roster" => HandleGetRoster(),
                    "send_message" => await HandleSendMessageAsync(root),
                    "get_inbox" => HandleGetInbox(root),
                    "record_file_read" => await HandleRecordFileReadAsync(root),
                    "record_file_write" => await HandleRecordFileWriteAsync(root),
                    "preflight_tool" => await HandlePreflightToolAsync(root),
                    "get_brief" => HandleGetBrief(root),
                    "record_subagent_event" => await HandleRecordSubagentEventAsync(root),
                    _ => ErrorResponse($"Unknown request type: '{type}'."),
                };
            }
            catch (Exception ex)
            {
                return ErrorResponse(ex.Message);
            }
        }
    }

    private async Task<string> HandleRegisterAgentAsync(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        var processId = root.TryGetProperty("processId", out var pid) ? pid.GetInt32() : 0;
        var sessionId = GetOptionalString(root, "sessionId");
        var parentSessionId = GetOptionalString(root, "parentSessionId");
        var cwd = GetRequiredString(root, "cwd");
        var timestamp = GetTimestamp(root);

        var record = new AgentRegisteredRecord(agentId, processId, sessionId, parentSessionId, cwd, timestamp);
        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private async Task<string> HandleUnregisterAgentAsync(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        var timestamp = GetTimestamp(root);

        var record = new AgentUnregisteredRecord(agentId, timestamp);
        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private async Task<string> HandleHeartbeatAsync(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        var timestamp = GetTimestamp(root);

        var record = new AgentHeartbeatRecord(agentId, timestamp);
        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private string HandleGetRoster()
    {
        var agents = _state.Agents.Values
            .Select(a => new AgentEntry(a.AgentId, a.ProcessId, a.SessionId, a.ParentSessionId, a.Cwd, a.Timestamp))
            .ToArray();

        return JsonSerializer.Serialize(
            new RosterResponse(true, "roster", agents),
            SerializerOptions);
    }

    private async Task<string> HandleSendMessageAsync(JsonElement root)
    {
        var messageId = GetRequiredString(root, "messageId");
        var fromAgentId = GetRequiredString(root, "fromAgentId");
        var to = GetRequiredString(root, "to");
        var body = GetRequiredString(root, "body");
        var timestamp = GetTimestamp(root);

        var record = new MessageSentRecord(messageId, fromAgentId, to, body, timestamp);
        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private string HandleGetInbox(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        DateTimeOffset? sinceTimestamp = null;
        if (root.TryGetProperty("sinceTimestamp", out var sinceEl) && sinceEl.ValueKind != JsonValueKind.Null)
            sinceTimestamp = sinceEl.GetDateTimeOffset();

        var messages = _state.Messages
            .Where(m => (m.To == agentId || m.To == "all")
                        && (!sinceTimestamp.HasValue || m.Timestamp > sinceTimestamp.Value))
            .OrderBy(m => m.Timestamp)
            .Select(m => new InboxMessage(m.MessageId, m.FromAgentId, m.To, m.Body, m.Timestamp))
            .ToArray();

        return JsonSerializer.Serialize(
            new InboxResponse(true, "inbox", messages),
            SerializerOptions);
    }

    private async Task<string> HandleRecordFileReadAsync(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        var path = GetRequiredString(root, "path");
        var timestamp = GetTimestamp(root);

        var normalizedPath = NormalizePathForAgent(agentId, path);
        var record = new FileReadRecord(agentId, normalizedPath, timestamp);
        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private async Task<string> HandleRecordFileWriteAsync(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        var path = GetRequiredString(root, "path");
        var timestamp = GetTimestamp(root);

        var normalizedPath = NormalizePathForAgent(agentId, path);
        var record = new FileWriteRecord(agentId, normalizedPath, timestamp);
        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private string NormalizePathForAgent(string agentId, string path)
    {
        string? cwd = null;
        if (_state.Agents.TryGetValue(agentId, out var registration))
            cwd = registration.Cwd;
        return FileToolActivityParser.NormalizePath(path, cwd);
    }

    private async Task<string> HandlePreflightToolAsync(JsonElement root)
    {
        var agentId = GetRequiredString(root, "agentId");
        var tool = GetRequiredString(root, "tool");
        var arguments = root.TryGetProperty("arguments", out var argsEl) ? argsEl : default;

        var timestamp = GetTimestamp(root);

        var requestPath = GetOptionalString(root, "path");

        FileToolActivity? activity = null;
        if (requestPath is not null)
        {
            var normalizedPath = NormalizePathForAgent(agentId, requestPath);
            activity = new FileToolActivity(FileActivityKind.Write, normalizedPath);
        }
        else
        {
            activity = FileToolActivityParser.Parse(tool, arguments);
            if (activity is not null)
            {
                var agentCwd = _state.Agents.TryGetValue(agentId, out var registration)
                    ? registration.Cwd : null;
                var normalizedPath = FileToolActivityParser.NormalizePath(activity.FilePath, agentCwd);
                activity = new FileToolActivity(activity.Kind, normalizedPath);
            }
        }

        if (activity?.Kind != FileActivityKind.Write)
            return JsonSerializer.Serialize(
                new PreflightResponse(true, "preflight", false, string.Empty),
                SerializerOptions);

        var decision = SoftConflictDetector.PreflightWrite(_state, agentId, activity.FilePath, timestamp);

        if (decision.ShouldWarn)
        {
            var mostRecentConflict = _state.RecentWrites
                .Where(w => w.AgentId != agentId
                            && string.Equals(
                                FileToolActivityParser.NormalizePath(w.Path, repoRoot: null),
                                FileToolActivityParser.NormalizePath(activity.FilePath, repoRoot: null),
                                StringComparison.Ordinal))
                .OrderByDescending(w => w.Timestamp)
                .FirstOrDefault();

            var warningRecord = new PreflightWarningRecord(
                agentId,
                activity.FilePath,
                mostRecentConflict?.AgentId ?? "unknown",
                mostRecentConflict?.Timestamp ?? timestamp,
                timestamp);

            await _store.AppendAsync(warningRecord);
            _state.Apply(warningRecord);
        }

        return JsonSerializer.Serialize(
            new PreflightResponse(true, "preflight", decision.ShouldWarn, decision.Message),
            SerializerOptions);
    }

    private string HandleGetBrief(JsonElement root)
    {
        var content = _state.Agents.Count > 0
            ? $"# Coordination Brief\n\n{_state.Agents.Count} agent(s) active.\n"
            : "# Coordination Brief\n\nNo agents registered.\n";

        return JsonSerializer.Serialize(
            new BriefResponse(true, "brief", content),
            SerializerOptions);
    }

    private async Task<string> HandleRecordSubagentEventAsync(JsonElement root)
    {
        var subagentId = GetRequiredString(root, "subagentId");
        var subagentType = TruncateSubagentField(GetOptionalString(root, "subagentType"), PiSubagentsEventAdapter.MaxTypeLength);
        var description = TruncateSubagentField(GetOptionalString(root, "description"), PiSubagentsEventAdapter.MaxDescriptionLength);
        var status = TruncateSubagentField(GetOptionalString(root, "status"), PiSubagentsEventAdapter.MaxStatusLength);
        var eventName = GetRequiredString(root, "eventName");

        if (!PiSubagentsEventAdapter.IsKnownEventName(eventName))
            throw new InvalidOperationException($"Unknown subagent event name: '{eventName}'.");

        var durationMs = GetOptionalDouble(root, "durationMs");
        var toolUses = GetOptionalInt32(root, "toolUses");
        var inputTokens = GetOptionalInt32(root, "inputTokens");
        var outputTokens = GetOptionalInt32(root, "outputTokens");
        var parentSessionId = GetOptionalString(root, "parentSessionId");
        var cwd = GetRequiredString(root, "cwd");
        var timestamp = GetTimestamp(root);

        var record = new SubagentObservedRecord(
            subagentId,
            subagentType,
            description,
            status,
            eventName,
            durationMs,
            toolUses,
            inputTokens,
            outputTokens,
            parentSessionId,
            cwd,
            timestamp);

        await _store.AppendAsync(record);
        _state.Apply(record);

        return OkResponse("ack");
    }

    private static string? TruncateSubagentField(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
            return value;
        return value[..maxLength];
    }

    private static string OkResponse(string type)
    {
        return $"{{\"ok\":true,\"type\":\"{type}\"}}";
    }

    private static string ErrorResponse(string message)
    {
        return $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(message, SerializerOptions)}}}";
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            throw new InvalidOperationException($"Missing required field '{propertyName}'.");
        return element.GetString() ?? throw new InvalidOperationException($"Field '{propertyName}' is null.");
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind != JsonValueKind.Null)
            return element.GetString();
        return null;
    }

    private static double? GetOptionalDouble(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetDouble(out var value))
                return value;
        }
        return null;
    }

    private static int? GetOptionalInt32(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetInt32(out var value))
                return value;
        }
        return null;
    }

    private static DateTimeOffset GetTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var tsElement))
            return tsElement.GetDateTimeOffset();
        return DateTimeOffset.UtcNow;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _serverTask; }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
