using Microsoft.Extensions.Configuration;

namespace PiSharp.Compatibility.Settings;

public sealed record PiLoggingSettings(string? File, string? Level, int? MaxFiles, bool Json = false)
{
    public static PiLoggingSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("logging");
        return new PiLoggingSettings(
            section["file"],
            section["level"],
            ReadPositiveInt(section["maxFiles"]),
            ReadBool(section["json"]));
    }

    private static int? ReadPositiveInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static bool ReadBool(string? value)
        => bool.TryParse(value, out var parsed) && parsed;
}
