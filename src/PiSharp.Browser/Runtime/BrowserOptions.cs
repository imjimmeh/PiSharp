namespace PiSharp.Browser.Runtime;

/// <summary>
/// Resolved plugin options, including the master enabled gate.
/// </summary>
public sealed record BrowserOptions(bool Enabled, BrowserToolOptions Tool)
{
    /// <summary>
    /// Resolves the options from the environment. The master gate defaults to <c>false</c>;
    /// <c>PISHARP_BROWSER_ENABLED</c> in <c>true</c>/<c>1</c>/<c>yes</c> turns the tool on.
    /// </summary>
    /// <remarks>
    /// When the P02 settings surface (<c>IExtensionApi.Settings</c>, key
    /// <c>extensions.pisharp-browser.enabled</c>) merges, this resolver should consult it first
    /// and fall back to the environment. P02 is a parallel-wave core change, so v1 reads the
    /// environment only — keeping this plugin independently mergeable.
    /// </remarks>
    public static BrowserOptions Resolve(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var enabled = ParseEnabled(environment("PISHARP_BROWSER_ENABLED"));
        return new BrowserOptions(enabled, new BrowserToolOptions());
    }

    internal static bool ParseEnabled(string? raw)
        => raw is not null
           && (raw.Equals("true", StringComparison.OrdinalIgnoreCase)
               || raw == "1"
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
