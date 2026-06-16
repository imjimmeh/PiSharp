using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Messages;

namespace PiSharp.Agent.Serialization;

public sealed class AgentMessageJsonConverter : JsonConverter<AgentMessage>
{
    private static readonly UsageInfo EmptyUsage = new(Cost: new UsageCost());

    public override AgentMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var role = root.GetProperty("role").GetString();
        var timestamp = ReadTimestamp(root);
        return role switch
        {
            "user" => new UserMessage(ReadContent(root, options), timestamp),
            "assistant" => new AssistantMessage(
                ReadContent(root, options),
                root.TryGetProperty("api", out var api) ? api.GetString() : null,
                root.TryGetProperty("provider", out var provider) ? provider.GetString() : null,
                root.TryGetProperty("model", out var model) ? model.GetString() : null,
                root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null
                    ? usage.Deserialize<UsageInfo>(options)
                    : EmptyUsage,
                root.TryGetProperty("stopReason", out var stopReason) ? stopReason.GetString() : null,
                root.TryGetProperty("errorMessage", out var errorMessage) ? errorMessage.GetString() : null,
                timestamp,
                root.TryGetProperty("responseModel", out var responseModel) ? responseModel.GetString() : null,
                root.TryGetProperty("responseId", out var responseId) ? responseId.GetString() : null,
                ReadDiagnostics(root, options)),
            "toolResult" => new ToolResultMessage(
                root.TryGetProperty("toolCallId", out var toolCallId) ? toolCallId.GetString() ?? string.Empty : root.GetProperty("toolUseId").GetString() ?? string.Empty,
                root.TryGetProperty("toolName", out var toolName) ? toolName.GetString() ?? string.Empty : string.Empty,
                ReadContent(root, options),
                root.TryGetProperty("details", out var details) ? details.Clone() : null,
                root.TryGetProperty("isError", out var isError) && isError.GetBoolean(),
                timestamp),
            "bashExecution" => new BashExecutionMessage(
                root.GetProperty("command").GetString() ?? string.Empty,
                root.TryGetProperty("output", out var output) ? output.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind != JsonValueKind.Null ? exitCode.GetInt32() : null,
                root.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean(),
                root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean(),
                root.TryGetProperty("fullOutputPath", out var fullOutputPath) ? fullOutputPath.GetString() : null,
                root.TryGetProperty("excludeFromContext", out var exclude) && exclude.GetBoolean(),
                timestamp),
            "custom" => ReadCustom(root, options, timestamp),
            "branchSummary" => new BranchSummaryMessage(root.GetProperty("summary").GetString() ?? string.Empty, root.GetProperty("fromId").GetString() ?? string.Empty, timestamp),
            "compactionSummary" => new CompactionSummaryMessage(root.GetProperty("summary").GetString() ?? string.Empty, root.TryGetProperty("tokensBefore", out var tokens) ? tokens.GetInt32() : 0, timestamp),
            _ => throw new JsonException($"Unknown agent message role '{role}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, AgentMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);
        switch (value)
        {
            case UserMessage user:
                WriteContent(writer, user.Content, options);
                break;
            case AssistantMessage assistant:
                WriteContent(writer, assistant.Content, options);
                WriteStringIfNotNull(writer, "api", assistant.Api);
                WriteStringIfNotNull(writer, "provider", assistant.Provider);
                WriteStringIfNotNull(writer, "model", assistant.Model);
                writer.WritePropertyName("usage");
                JsonSerializer.Serialize(writer, assistant.Usage ?? EmptyUsage, options);
                WriteStringIfNotNull(writer, "stopReason", assistant.StopReason);
                WriteStringIfNotNull(writer, "errorMessage", assistant.ErrorMessage);
                WriteStringIfNotNull(writer, "responseModel", assistant.ResponseModel);
                WriteStringIfNotNull(writer, "responseId", assistant.ResponseId);
                if (assistant.Diagnostics is not null)
                {
                    writer.WritePropertyName("diagnostics");
                    JsonSerializer.Serialize(writer, assistant.Diagnostics, options);
                }
                break;
            case ToolResultMessage tool:
                writer.WriteString("toolCallId", tool.ToolUseId);
                writer.WriteString("toolName", tool.ToolName);
                WriteContent(writer, tool.Content, options);
                writer.WritePropertyName("details");
                JsonSerializer.Serialize(writer, tool.Details, options);
                writer.WriteBoolean("isError", tool.IsError);
                break;
            case BashExecutionMessage bash:
                writer.WriteString("command", bash.Command);
                writer.WriteString("output", bash.Output);
                if (bash.ExitCode is null) writer.WriteNull("exitCode"); else writer.WriteNumber("exitCode", bash.ExitCode.Value);
                writer.WriteBoolean("cancelled", bash.Cancelled);
                writer.WriteBoolean("truncated", bash.Truncated);
                WriteStringIfNotNull(writer, "fullOutputPath", bash.FullOutputPath);
                writer.WriteBoolean("excludeFromContext", bash.ExcludeFromContext);
                break;
            case CustomMessage custom:
                writer.WriteString("customType", custom.CustomType);
                writer.WritePropertyName("content");
                if (custom.ContentBlocks is not null) JsonSerializer.Serialize(writer, custom.ContentBlocks, options);
                else writer.WriteStringValue(custom.TextContent ?? string.Empty);
                writer.WriteBoolean("display", custom.Display);
                writer.WritePropertyName("details");
                JsonSerializer.Serialize(writer, custom.Details, options);
                break;
            case BranchSummaryMessage branch:
                writer.WriteString("summary", branch.Summary);
                writer.WriteString("fromId", branch.FromId);
                break;
            case CompactionSummaryMessage compaction:
                writer.WriteString("summary", compaction.Summary);
                writer.WriteNumber("tokensBefore", compaction.TokensBefore);
                break;
        }
        WriteTimestamp(writer, value.Timestamp);
        writer.WriteEndObject();
    }

    private static CustomMessage ReadCustom(JsonElement root, JsonSerializerOptions options, DateTimeOffset timestamp)
    {
        var content = root.GetProperty("content");
        var details = root.TryGetProperty("details", out var detailsElement) ? (object)detailsElement.Clone() : null;
        var display = root.TryGetProperty("display", out var displayElement) && displayElement.GetBoolean();
        var customType = root.GetProperty("customType").GetString() ?? string.Empty;
        return content.ValueKind == JsonValueKind.String
            ? new CustomMessage(customType, content.GetString(), null, display, details, timestamp)
            : new CustomMessage(customType, null, content.Deserialize<IReadOnlyList<MessageContent>>(options), display, details, timestamp);
    }

    private static IReadOnlyList<MessageContent> ReadContent(JsonElement root, JsonSerializerOptions options)
    {
        var content = root.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            return [new TextContent(content.GetString() ?? string.Empty)];
        }
        return content.Deserialize<IReadOnlyList<MessageContent>>(options) ?? [];
    }

    private static void WriteContent(Utf8JsonWriter writer, IReadOnlyList<MessageContent> content, JsonSerializerOptions options)
    {
        writer.WritePropertyName("content");
        JsonSerializer.Serialize(writer, content, options);
    }

    private static void WriteStringIfNotNull(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is not null) writer.WriteString(propertyName, value);
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var timestamp)) return DateTimeOffset.UtcNow;
        return timestamp.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.GetInt64())
            : DateTimeOffset.Parse(timestamp.GetString() ?? DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, DateTimeOffset timestamp)
        => writer.WriteNumber("timestamp", timestamp.ToUnixTimeMilliseconds());

    private static IReadOnlyList<ProviderDiagnostic>? ReadDiagnostics(JsonElement root, JsonSerializerOptions options)
    {
        if (!root.TryGetProperty("diagnostics", out var diagnostics)) return null;
        return diagnostics.ValueKind == JsonValueKind.Array
            ? diagnostics.Deserialize<IReadOnlyList<ProviderDiagnostic>>(options)
            : null;
    }
}
