namespace PiSharp.Telemetry.Otlp;

/// <summary>
/// Effective OTLP export configuration: whether export is enabled at all, the
/// endpoint to ship to, and the anonymous install id to attach as the
/// <c>pisharp.install_id</c> resource attribute.
/// </summary>
public sealed record OtlpExportConfig(bool Enabled, Uri? Endpoint, string? InstallId);

/// <summary>
/// Pure resolution of the OTLP export settings (plan §8): core settings
/// <c>telemetry.export.otlp.enabled</c> / <c>telemetry.export.otlp.endpoint</c>,
/// gated on the master <c>telemetry.enabled</c> switch, with environment
/// overrides <c>PISHARP_TELEMETRY</c> (overrides the master switch),
/// <c>DO_NOT_TRACK=1</c> (force off) and <c>PISHARP_OTLP_ENDPOINT</c>
/// (overrides the endpoint). No I/O — the settings accessor and environment
/// reader are injected so the precedence rules are unit-testable directly.
/// </summary>
public static class OtlpExportOptions
{
    public const string TelemetryEnabledKey = "telemetry.enabled";
    public const string OtlpEnabledKey = "telemetry.export.otlp.enabled";
    public const string OtlpEndpointKey = "telemetry.export.otlp.endpoint";

    public const string TelemetryEnvVar = "PISHARP_TELEMETRY";
    public const string DoNotTrackEnvVar = "DO_NOT_TRACK";
    public const string OtlpEndpointEnvVar = "PISHARP_OTLP_ENDPOINT";

    /// <summary>OTLP/gRPC default endpoint per OTel convention.</summary>
    public const string DefaultEndpoint = "http://localhost:4317";

    /// <summary>Resource attribute carrying the anonymous install id on exported records (plan §4.7).</summary>
    public const string InstallIdAttributeName = "pisharp.install_id";

    public static OtlpExportConfig Resolve(
        Func<string, object?> readCoreSetting,
        Func<string, string?> getEnvironmentVariable,
        string? installId = null)
    {
        var doNotTrack = IsTruthy(getEnvironmentVariable(DoNotTrackEnvVar));
        var telemetryEnabled = ResolveBool(readCoreSetting(TelemetryEnabledKey), getEnvironmentVariable(TelemetryEnvVar));
        var otlpEnabled = ResolveBool(readCoreSetting(OtlpEnabledKey), env: null);

        if (doNotTrack || !telemetryEnabled || !otlpEnabled)
            return new OtlpExportConfig(false, null, installId);

        return new OtlpExportConfig(true, ResolveEndpoint(readCoreSetting(OtlpEndpointKey), getEnvironmentVariable(OtlpEndpointEnvVar)), installId);
    }

    /// <summary>Setting first, environment override second, OTel default last.</summary>
    private static bool ResolveBool(object? setting, string? env)
    {
        if (env is not null && TryParseBool(env, out var envValue)) return envValue;
        return TryParseBool(setting, out var settingValue) && settingValue;
    }

    private static Uri ResolveEndpoint(object? setting, string? env)
    {
        if (!string.IsNullOrWhiteSpace(env) && TryParseUri(env, out var envUri)) return envUri;
        if (setting is string endpoint && !string.IsNullOrWhiteSpace(endpoint) && TryParseUri(endpoint, out var settingUri)) return settingUri;
        return new Uri(DefaultEndpoint, UriKind.Absolute);
    }

    private static bool IsTruthy(string? env) => env is not null && TryParseBool(env, out var value) && value;

    private static bool TryParseUri(string value, out Uri uri)
    {
        try
        {
            uri = new Uri(value, UriKind.Absolute);
            return true;
        }
        catch (UriFormatException)
        {
            uri = null!;
            return false;
        }
    }

    private static bool TryParseBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case long l:
                result = l != 0;
                return true;
            case double d:
                result = d != 0;
                return true;
            case string s when bool.TryParse(s, out var parsed):
                result = parsed;
                return true;
            case "1":
                result = true;
                return true;
            case "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
