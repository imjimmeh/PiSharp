using PiSharp.Abstractions.Messages;

namespace PiSharp.Abstractions.Sessions;

public abstract record SessionTreeEntry
{
    public abstract string Type { get; }
    public required string Id { get; init; }
    public required string? ParentId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record MessageEntry : SessionTreeEntry
{
    public const string TypeName = "message";
    public override string Type => TypeName;
    public required AgentMessage Message { get; init; }
}

public sealed record ThinkingLevelChangeEntry : SessionTreeEntry
{
    public const string TypeName = "thinking_level_change";
    public override string Type => TypeName;
    public required string ThinkingLevel { get; init; }
}

public sealed record ModelChangeEntry : SessionTreeEntry
{
    public const string TypeName = "model_change";
    public override string Type => TypeName;
    public required string Provider { get; init; }
    public required string ModelId { get; init; }
}

public sealed record CompactionEntry : SessionTreeEntry
{
    public const string TypeName = "compaction";
    public override string Type => TypeName;
    public required string Summary { get; init; }
    public required string FirstKeptEntryId { get; init; }
    public required int TokensBefore { get; init; }
    public object? Details { get; init; }
    public bool? FromHook { get; init; }
}

public sealed record BranchSummaryEntry : SessionTreeEntry
{
    public const string TypeName = "branch_summary";
    public override string Type => TypeName;
    public required string FromId { get; init; }
    public required string Summary { get; init; }
    public object? Details { get; init; }
    public bool? FromHook { get; init; }
}

public sealed record CustomEntry : SessionTreeEntry
{
    public const string TypeName = "custom";
    public override string Type => TypeName;
    public required string CustomType { get; init; }
    public object? Data { get; init; }
}

public sealed record CustomMessageEntry : SessionTreeEntry
{
    public const string TypeName = "custom_message";
    public override string Type => TypeName;
    public required string CustomType { get; init; }
    public required object Content { get; init; }
    public object? Details { get; init; }
    public bool Display { get; init; }
}

public sealed record LabelEntry : SessionTreeEntry
{
    public const string TypeName = "label";
    public override string Type => TypeName;
    public required string TargetId { get; init; }
    public string? Label { get; init; }
}

public sealed record SessionInfoEntry : SessionTreeEntry
{
    public const string TypeName = "session_info";
    public override string Type => TypeName;
    public string? Name { get; init; }
}

public sealed record LeafEntry : SessionTreeEntry
{
    public const string TypeName = "leaf";
    public override string Type => TypeName;
    public string? TargetId { get; init; }
}
