namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Receives one combined stdout/stderr byte chunk in shell-emission order.
/// </summary>
public delegate ValueTask ShellOutputBytesCallback(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

/// <summary>
/// Options for shell execution in an execution environment.
/// </summary>
public sealed record ExecutionOptions(
    string? Cwd = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null,
    Action<string>? OnStdout = null,
    Action<string>? OnStderr = null,
    ShellOutputBytesCallback? OnOutputBytes = null);
