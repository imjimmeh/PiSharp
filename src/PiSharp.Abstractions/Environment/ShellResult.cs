namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Completed shell command output.
/// </summary>
public sealed record ShellResult(
    string Stdout,
    string Stderr,
    int ExitCode);
