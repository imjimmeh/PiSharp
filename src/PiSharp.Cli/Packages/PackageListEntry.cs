using PiSharp.Compatibility.Settings;

namespace PiSharp.Cli.Packages;

public sealed record PackageListEntry(string Source, PiSettingsLayer Layer);
