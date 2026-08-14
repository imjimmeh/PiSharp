namespace PiSharp.Server.Hosting;

public sealed record PiServerHostOptions
{
    public required string ApiKey { get; init; }
}
