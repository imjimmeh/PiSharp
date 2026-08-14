namespace PiSharp.Cli.Packages;

/// <summary>The self-update method for non-tool installs (source/dev builds): never spawns `dotnet tool update`.</summary>
public sealed class UnknownSelfUpdateMethod : ISelfUpdateMethod
{
    public SelfUpdateMethodKind Kind => SelfUpdateMethodKind.Unknown;
    public bool CanUpdate => false;
    public string ManualInstructions => "Run `dotnet tool update --global PiSharp.Cli`";

    public Task<SelfUpdateResult> UpdateAsync(string? addSource, bool offline, CancellationToken cancellationToken)
        => Task.FromResult(new SelfUpdateResult(Updated: false, AlreadyUpToDate: false));
}

/// <summary>Locates the running install's self-update method.</summary>
public static class SelfUpdateMethodDetector
{
    /// <summary>Location-based detection (no subprocess in the common path);
    /// falls back to `dotnet tool list --global` when the location is ambiguous.</summary>
    public static async Task<ISelfUpdateMethod> DetectAsync(
        string? assemblyBaseDirectory,
        IPackageProcessRunner runner,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assemblyBaseDirectory))
            return new UnknownSelfUpdateMethod();

        // Global dotnet-tool layout: ~/.dotnet/tools/.store/pisharp.cli/<version>/...
        var basePath = assemblyBaseDirectory.Replace('\\', '/');
        if (basePath.Contains(".store/", StringComparison.OrdinalIgnoreCase)
            && basePath.Contains("pisharp.cli", StringComparison.OrdinalIgnoreCase))
        {
            return new DotnetToolSelfUpdateMethod(runner);
        }

        // Ambiguous location: probe `dotnet tool list --global` (the same probe install.sh uses).
        try
        {
            var listing = await runner.RunCaptureAsync("dotnet", "tool list --global", cancellationToken: cancellationToken);
            if (listing.ExitCode == 0 && (listing.StdOut ?? string.Empty).Contains("PiSharp.Cli", StringComparison.OrdinalIgnoreCase))
                return new DotnetToolSelfUpdateMethod(runner);
        }
        catch
        {
            // Fall through to Unknown.
        }

        return new UnknownSelfUpdateMethod();
    }
}
