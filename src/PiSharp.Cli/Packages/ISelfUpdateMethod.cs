namespace PiSharp.Cli.Packages;

public enum SelfUpdateMethodKind { DotnetTool, Unknown }

/// <summary>The backend that performs (or explains how to perform) a PiSharp self-update.</summary>
public interface ISelfUpdateMethod
{
    SelfUpdateMethodKind Kind { get; }

    /// <summary>True when this method can perform an update in-process (i.e. by spawning its package manager).</summary>
    bool CanUpdate { get; }

    /// <summary>Run the update. Throws InvalidOperationException with the tool's stderr on failure.</summary>
    Task<SelfUpdateResult> UpdateAsync(string? addSource, bool offline, CancellationToken cancellationToken);

    /// <summary>Human instructions for updating manually (used by the Unknown method and as a fallback message).</summary>
    string ManualInstructions { get; }
}

public sealed record SelfUpdateResult(
    bool Updated,
    bool AlreadyUpToDate,
    string? InstalledVersion = null);
