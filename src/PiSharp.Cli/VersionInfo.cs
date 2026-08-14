using System.Reflection;

namespace PiSharp.Cli;

/// <summary>Single source of truth for "what version am I".</summary>
public static class VersionInfo
{
    /// <summary>Informational version (AssemblyInformationalVersionAttribute), normalized:
    /// strips a "+build" suffix, falls back to AssemblyVersion when the attribute is absent.</summary>
    public static string Current
    {
        get
        {
            var assembly = typeof(VersionInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+', StringComparison.Ordinal);
                if (plus >= 0) informational = informational[..plus];
                return informational;
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
