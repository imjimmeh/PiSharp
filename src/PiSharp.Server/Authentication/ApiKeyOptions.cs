namespace PiSharp.Server.Authentication;

public sealed record ApiKeyOptions
{
    public required string ApiKey { get; init; }
}
