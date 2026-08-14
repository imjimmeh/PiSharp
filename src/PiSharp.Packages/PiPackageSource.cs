namespace PiSharp.Packages;

public enum PiPackageSourceKind { Npm, Git, Local }

public sealed record PiPackageSource(
    PiPackageSourceKind Kind,
    string Original,
    string Identity,
    string Name,
    string? VersionOrRef,
    string? Host,
    string? RepositoryPath,
    string? LocalPath,
    bool IsPinned);
