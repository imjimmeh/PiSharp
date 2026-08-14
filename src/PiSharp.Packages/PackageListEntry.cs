using PiSharp.Compatibility.Settings;

namespace PiSharp.Packages;

public sealed record PackageListEntry(string Source, PiSettingsLayer Layer);
