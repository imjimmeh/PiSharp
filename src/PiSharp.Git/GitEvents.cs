namespace PiSharp.Git;

/// <summary>Plugin-emitted event payloads. Names are plugin-defined (the bus accepts arbitrary
/// names) and are visible to native + TS extensions, not streamed as daemon client events.</summary>
public static class GitEventNames
{
    public const string CommitCreated = "git_commit_created";
    public const string ShareCompleted = "git_share_completed";
}

public sealed record CommitCreatedEvent(
    string Repo,
    string? HeadBefore,
    string Hash,
    string Message,
    IReadOnlyList<string> Files);

public sealed record ShareCompletedEvent(
    string Url,
    string GistId,
    string FileName,
    long Bytes);
