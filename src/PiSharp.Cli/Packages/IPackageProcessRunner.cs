namespace PiSharp.Cli.Packages;

public interface IPackageProcessRunner
{
    Task RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default);
}
