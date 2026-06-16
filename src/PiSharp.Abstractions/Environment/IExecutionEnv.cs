namespace PiSharp.Abstractions.Environment;

/// <summary>
/// Filesystem and process execution environment used by the harness.
/// </summary>
public interface IExecutionEnv : IFileSystem, IShell
{
}
