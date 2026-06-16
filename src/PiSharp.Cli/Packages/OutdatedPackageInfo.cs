namespace PiSharp.Cli.Packages;

public sealed record OutdatedPackageInfo(string Name, string InstalledVersion, string LatestVersion);
