namespace PiSharp.Cli.Packages;

public sealed record PackageUpdateRequest(
    string? Source = null,
    bool Self = false,
    bool Extensions = false,
    string? ExtensionSource = null,
    bool Force = false,
    bool Offline = false);
