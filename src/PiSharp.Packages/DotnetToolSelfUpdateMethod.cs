namespace PiSharp.Packages;

/// <summary>The self-update method for global dotnet-tool installs: delegates to `dotnet tool update --global PiSharp.Cli`.</summary>
public sealed class DotnetToolSelfUpdateMethod : ISelfUpdateMethod
{
    private readonly IPackageProcessRunner _runner;

    public DotnetToolSelfUpdateMethod(IPackageProcessRunner runner)
    {
        _runner = runner;
    }

    public SelfUpdateMethodKind Kind => SelfUpdateMethodKind.DotnetTool;
    public bool CanUpdate => true;
    public string ManualInstructions => "Run `dotnet tool update --global PiSharp.Cli`";

    public async Task<SelfUpdateResult> UpdateAsync(string? addSource, bool offline, CancellationToken cancellationToken)
    {
        if (offline)
        {
            return new SelfUpdateResult(Updated: false, AlreadyUpToDate: false);
        }

        var arguments = "tool update --global PiSharp.Cli";
        if (!string.IsNullOrWhiteSpace(addSource))
            arguments += $" --add-source \"{addSource}\"";

        var result = await _runner.RunCaptureAsync("dotnet", arguments, cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet tool update failed with exit code {result.ExitCode}.{(string.IsNullOrWhiteSpace(result.StdErr) ? string.Empty : " " + result.StdErr.Trim())}");
        }

        var output = (result.StdOut ?? string.Empty) + " " + (result.StdErr ?? string.Empty);
        var installedVersion = ParseInstalledVersion(output);
        return new SelfUpdateResult(
            Updated: output.Contains("successfully updated", StringComparison.OrdinalIgnoreCase),
            AlreadyUpToDate: output.Contains("already up to date", StringComparison.OrdinalIgnoreCase),
            InstalledVersion: installedVersion);
    }

    /// <summary>Defensively parses the SDK's "successfully updated to version 'X'" / "Tool 'pisharp' was successfully updated to version '1.2.3'."
    /// Falls back to null when the wording changes.</summary>
    private static string? ParseInstalledVersion(string output)
    {
        var marker = "version";
        var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var rest = output[(idx + marker.Length)..];
        var quote = rest.IndexOf('\'');
        if (quote < 0) return null;
        var end = rest.IndexOf('\'', quote + 1);
        if (end < 0) return null;
        var version = rest[(quote + 1)..end];
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}
