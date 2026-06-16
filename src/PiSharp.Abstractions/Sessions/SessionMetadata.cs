namespace PiSharp.Abstractions.Sessions;

public interface ISessionMetadata
{
    string Id { get; }

    DateTimeOffset CreatedAt { get; }
}

public sealed record JsonlSessionMetadata : ISessionMetadata
{
    public JsonlSessionMetadata(
        string id,
        DateTimeOffset createdAt,
        string cwd,
        string path,
        string? parentSessionPath = null,
        DateTimeOffset modifiedAt = default,
        int messageCount = 0,
        string firstMessage = "(no messages)",
        string allMessagesText = "",
        string? name = null)
    {
        Id = id;
        CreatedAt = createdAt;
        Cwd = cwd;
        Path = path;
        ParentSessionPath = parentSessionPath;
        ModifiedAt = modifiedAt == default ? createdAt : modifiedAt;
        MessageCount = messageCount;
        FirstMessage = firstMessage;
        AllMessagesText = allMessagesText;
        Name = name;
    }

    public string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string Cwd { get; init; }
    public string Path { get; init; }
    public string? ParentSessionPath { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public int MessageCount { get; init; }
    public string FirstMessage { get; init; }
    public string AllMessagesText { get; init; }
    public string? Name { get; init; }
}
