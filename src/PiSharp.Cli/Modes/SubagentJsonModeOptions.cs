namespace PiSharp.Cli.Modes;

public sealed record SubagentJsonModeOptions(
    string? InitialMessage = null,
    IReadOnlyList<string>? Messages = null);
