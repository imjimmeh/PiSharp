using System.Text.Json;
using PiSharp.Compatibility.Settings;

namespace PiSharp.Telemetry.Otlp;

/// <summary>
/// Reads the anonymous install id the core creates at
/// <c>~/.pi/PiSharp/telemetry.json</c> (plan §4.7) so exported records can
/// carry the <c>pisharp.install_id</c> correlation attribute. Read-only and
/// best-effort: a missing, malformed or unreadable file simply yields no id.
/// </summary>
internal static class InstallIdResolver
{
    /// <summary>Resolves the install-id file path for the session's home layout.</summary>
    public static string ResolveInstallIdPath(string cwd)
    {
        var paths = PiAgentPaths.FromCwd(cwd);
        return Path.Combine(paths.GlobalPiSharpDirectory, "telemetry.json");
    }

    /// <summary>Returns the installId property, or null when the file is absent/invalid.</summary>
    public static string? TryRead(string installIdFilePath)
    {
        if (string.IsNullOrWhiteSpace(installIdFilePath) || !File.Exists(installIdFilePath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(installIdFilePath));
            if (!document.RootElement.TryGetProperty("installId", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var value = id.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
