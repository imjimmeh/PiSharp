using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core.Tools;
using PiSharp.Memory.Abstractions;

namespace PiSharp.Memory;

/// <summary>
/// Executes the five memory tools (<c>retain</c>, <c>recall</c>, <c>reflect</c>,
/// <c>memory_edit</c>, <c>learn</c>) against the active store. Every tool returns a
/// typed blocked result when the active backend is "off", and structured
/// <see cref="MemoryToolDetails"/> otherwise.
/// </summary>
public sealed class MemoryToolCoordinator
{
    public const string BlockedMessage =
        "Memory backend is off. Set extensions.pisharp-memory.backend to \"file\" | \"vector\" | \"sqlite\" (via /settings or settings.json).";

    private static readonly JsonSerializerOptions ToolInputOptions = new(JsonSerializerDefaults.Web);

    private readonly IMemoryStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentMessage>>>? _sessionContextProvider;

    /// <summary>Optional managed-skill promoter (P04). Null when the host exposes no managed-skill surface.</summary>
    public Func<string, string?, CancellationToken, Task<string?>>? SkillPromoter { get; set; }

    public MemoryToolCoordinator(
        IMemoryStore store,
        Func<CancellationToken, Task<IReadOnlyList<AgentMessage>>>? sessionContextProvider = null)
    {
        _store = store;
        _sessionContextProvider = sessionContextProvider;
    }

    public async Task<AgentToolResult<object?>> ExecuteAsync(
        string toolName,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IsBlocked)
            {
                return BlockedResult(toolName);
            }

            return toolName switch
            {
                "retain" => await RetainAsync(parameters, cancellationToken).ConfigureAwait(false),
                "recall" => await RecallAsync(parameters, cancellationToken).ConfigureAwait(false),
                "reflect" => await ReflectAsync(parameters, cancellationToken).ConfigureAwait(false),
                "memory_edit" => await MemoryEditAsync(parameters, cancellationToken).ConfigureAwait(false),
                "learn" => await LearnAsync(parameters, cancellationToken).ConfigureAwait(false),
                _ => ErrorResult(toolName, $"Unknown memory tool '{toolName}'.")
            };
        }
        catch (JsonException exception)
        {
            return ErrorResult(toolName, $"Invalid arguments for {toolName}: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return ErrorResult(toolName, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ErrorResult(toolName, $"{toolName} failed: {exception.Message}");
        }
    }

    private bool IsBlocked => string.Equals(_store.Provider.Id, "off", StringComparison.Ordinal);

    private async Task<AgentToolResult<object?>> RetainAsync(JsonElement parameters, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<RetainToolInput>(parameters, ToolInputOptions)
            ?? throw new ArgumentException("retain requires an object argument.");
        if (string.IsNullOrWhiteSpace(input.Title)) throw new ArgumentException("retain requires a non-empty 'title'.");
        if (string.IsNullOrWhiteSpace(input.Content)) throw new ArgumentException("retain requires a non-empty 'content'.");

        var scope = ParseScope(input.Scope);
        var kind = ParseKind(input.Kind);
        var key = string.IsNullOrWhiteSpace(input.RecordKey) ? DefaultKey(kind, input.Title) : input.RecordKey.Trim();
        var now = DateTimeOffset.UtcNow;
        var record = new MemoryRecord(key, kind, input.Title.Trim(), input.Content.Trim(), input.Tags ?? [], now, now);

        await _store.PutAsync(scope, record, ct).ConfigureAwait(false);
        return OkResult(
            $"Retained [{key}] ({KindName(kind)}) in {ScopeName(scope)} scope.",
            new MemoryToolDetails("retain", RecordKey: key, Backend: _store.Provider.Id));
    }

    private async Task<AgentToolResult<object?>> RecallAsync(JsonElement parameters, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<RecallToolInput>(parameters, ToolInputOptions)
            ?? throw new ArgumentException("recall requires an object argument.");

        var query = new MemoryQuery(
            Text: string.IsNullOrWhiteSpace(input.Query) ? null : input.Query,
            Kind: ParseKindOrNull(input.Kind),
            Tags: input.Tags,
            IncludeInvalidated: input.IncludeInvalidated ?? false,
            Limit: ClampLimit(input.Limit ?? 10));

        var records = await _store.RecallAsync(ParseScope(input.Scope), query, ct).ConfigureAwait(false);
        var content = records.Count == 0
            ? "No matching memory records."
            : $"Memory ({records.Count} record{(records.Count == 1 ? "" : "s")}):\n" + FormatRecords(records);
        return OkResult(content, new MemoryToolDetails("recall", Count: records.Count, Backend: _store.Provider.Id));
    }

    private async Task<AgentToolResult<object?>> ReflectAsync(JsonElement parameters, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<ReflectToolInput>(parameters, ToolInputOptions)
            ?? throw new ArgumentException("reflect requires an object argument.");

        var query = new MemoryQuery(
            Text: string.IsNullOrWhiteSpace(input.Topic) ? null : input.Topic,
            Limit: 8);
        var records = await _store.RecallAsync(ParseScope(input.Scope), query, ct).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("## Memory reflection");
        builder.AppendLine();
        builder.AppendLine("Synthesize the material below into durable, reusable knowledge, then call `retain` with kind = \"summary\" (or `learn` for lessons) to store your synthesis. This tool is read-only; it stores nothing.");
        builder.AppendLine();

        if (records.Count > 0)
        {
            builder.AppendLine("### Related memory records");
            builder.AppendLine(FormatRecords(records));
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("(No related memory records found.)");
            builder.AppendLine();
        }

        if ((input.IncludeContext ?? true) && _sessionContextProvider is not null)
        {
            var messages = await _sessionContextProvider(ct).ConfigureAwait(false);
            var recent = messages.TakeLast(12).ToArray();
            if (recent.Length > 0)
            {
                builder.AppendLine($"### Recent session context (last {recent.Length} messages)");
                foreach (var message in recent)
                {
                    var text = message is ToolResultMessage
                        ? "(tool result)"
                        : string.Join(" ", message.GetType().Name);
                    builder.AppendLine($"- [{message.Role}] {Truncate(text, 140)}");
                }
            }
        }

        return OkResult(builder.ToString().TrimEnd(),
            new MemoryToolDetails("reflect", Count: records.Count, Backend: _store.Provider.Id));
    }

    private async Task<AgentToolResult<object?>> MemoryEditAsync(JsonElement parameters, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<MemoryEditToolInput>(parameters, ToolInputOptions)
            ?? throw new ArgumentException("memory_edit requires an object argument.");
        if (string.IsNullOrWhiteSpace(input.RecordKey)) throw new ArgumentException("memory_edit requires a 'recordKey'.");

        var scope = ParseScope(input.Scope);
        var updated = (await _store.UpdateAsync(scope, input.RecordKey.Trim(), record =>
        {
            var next = record;
            if (input.Title is not null) next = next with { Title = input.Title };
            if (input.Content is not null) next = next with { Content = input.Content };
            if (input.Tags is not null) next = next with { Tags = input.Tags };
            if (input.Invalidate == true && !next.IsInvalidated) next = next with { InvalidatedAt = DateTimeOffset.UtcNow };
            return next;
        }, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException("memory_edit failed: the store returned no record.");


        var action = input.Invalidate == true ? "Invalidated" : "Updated";
        return OkResult(
            $"{action} [{updated.RecordKey}] ({KindName(updated.Kind)}).\n" + FormatRecord(updated),
            new MemoryToolDetails("memory_edit", RecordKey: updated.RecordKey, Backend: _store.Provider.Id));
    }

    private async Task<AgentToolResult<object?>> LearnAsync(JsonElement parameters, CancellationToken ct)
    {
        var input = JsonSerializer.Deserialize<LearnToolInput>(parameters, ToolInputOptions)
            ?? throw new ArgumentException("learn requires an object argument.");
        if (string.IsNullOrWhiteSpace(input.Title)) throw new ArgumentException("learn requires a non-empty 'title'.");
        if (string.IsNullOrWhiteSpace(input.Lesson)) throw new ArgumentException("learn requires a non-empty 'lesson'.");

        var scope = ParseScope(input.Scope);
        var key = DefaultKey(MemoryKind.Lesson, input.Title);
        var now = DateTimeOffset.UtcNow;
        var record = new MemoryRecord(key, MemoryKind.Lesson, input.Title.Trim(), input.Lesson.Trim(), input.Tags ?? [], now, now);
        await _store.PutAsync(scope, record, ct).ConfigureAwait(false);

        var content = $"Stored lesson [{key}] in {ScopeName(scope)} scope.";
        var details = new MemoryToolDetails("learn", RecordKey: key, Backend: _store.Provider.Id);

        if (input.Promote == true)
        {
            if (string.IsNullOrWhiteSpace(input.SkillName))
            {
                details = details with { Warning = "promote was requested without a 'skillName'; the lesson is stored but not promoted." };
                content += " Promotion skipped: 'skillName' is required when promote is true.";
            }
            else if (SkillPromoter is null)
            {
                details = details with { Warning = "the P04 managed-skill store is not wired in this extension host; the lesson is stored but not promoted." };
                content += " Promotion unavailable: no managed-skill store is wired in this host (P04).";
            }
            else
            {
                var skillName = await SkillPromoter(input.SkillName, input.SkillDescription, ct).ConfigureAwait(false);
                content += $" Promoted to managed skill '{skillName}'.";
            }
        }

        return OkResult(content, details);
    }

    // --- shared formatting / parsing helpers ---

    private static AgentToolResult<object?> OkResult(string content, MemoryToolDetails details)
        => new([new TextContent(content)], details);

    private AgentToolResult<object?> BlockedResult(string toolName)
        => new([new TextContent(BlockedMessage)], new MemoryToolDetails(toolName, Blocked: true, Backend: _store.Provider.Id));

    private static AgentToolResult<object?> ErrorResult(string toolName, string message)
        => new([new TextContent(message)], new MemoryToolDetails(toolName, Error: true, ErrorMessage: message));

    private static string FormatRecords(IReadOnlyList<MemoryRecord> records)
        => string.Join("\n", records.Select(FormatRecord));

    private static string FormatRecord(MemoryRecord record)
    {
        var tags = record.Tags.Count > 0 ? $" tags={string.Join(",", record.Tags)}" : string.Empty;
        var invalidated = record.IsInvalidated ? " [invalidated]" : string.Empty;
        var oneLine = record.Content.Replace('\n', ' ').Trim();
        return $"- [{record.RecordKey}] {record.Title} ({KindName(record.Kind)}{invalidated}{tags}): {Truncate(oneLine, 200)}";
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    internal static MemoryScope ParseScope(string? scope)
        => string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)
            ? MemoryScope.Project
            : string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase)
                ? MemoryScope.User
                : throw new ArgumentException($"Invalid scope '{scope}': expected \"user\" or \"project\".");

    internal static MemoryKind ParseKind(string? kind)
    {
        // The tool schema declares kind optional with "fact" as the default.
        if (string.IsNullOrWhiteSpace(kind)) return MemoryKind.Fact;
        return ParseKindOrNull(kind) ?? throw new ArgumentException($"Invalid kind '{kind}': expected \"fact\", \"lesson\", \"summary\" or \"mental-model\".");
    }

    internal static MemoryKind? ParseKindOrNull(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return kind.Trim().ToLowerInvariant() switch
        {
            "fact" => MemoryKind.Fact,
            "lesson" => MemoryKind.Lesson,
            "summary" => MemoryKind.Summary,
            "mental-model" or "mentalmodel" => MemoryKind.MentalModel,
            _ => throw new ArgumentException($"Invalid kind '{kind}': expected \"fact\", \"lesson\", \"summary\" or \"mental-model\".")
        };
    }

    internal static string KindName(MemoryKind kind) => kind switch
    {
        MemoryKind.Fact => "fact",
        MemoryKind.Lesson => "lesson",
        MemoryKind.Summary => "summary",
        MemoryKind.MentalModel => "mental-model",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static string ScopeName(MemoryScope scope) => scope == MemoryScope.User ? "user" : "project";

    internal static int ClampLimit(int limit) => Math.Clamp(limit, 1, 100);

    internal static string DefaultKey(MemoryKind kind, string title)
    {
        var prefix = kind switch
        {
            MemoryKind.Fact => "facts",
            MemoryKind.Lesson => "lessons",
            MemoryKind.Summary => "summaries",
            MemoryKind.MentalModel => "mental-models",
            _ => "records"
        };
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH-mm-ss'Z'");
        return $"{prefix}/{timestamp}-{Slugify(title)}";
    }

    internal static string Slugify(string text)
    {
        // Map every non-alphanumeric char to '-', collapse runs, then keep up to
        // six words so generated keys stay short and stable.
        var slugged = string.Concat(text.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));
        return string.Join('-', slugged
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Take(6));
    }
}
