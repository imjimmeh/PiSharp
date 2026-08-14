using Xunit;
using PiSharp.Telemetry.Otlp;

namespace PiSharp.Telemetry.Otlp.Tests;

public sealed class OtlpExportOptionsTests
{
    private static OtlpExportConfig Resolve(
        Dictionary<string, object?>? settings = null,
        Dictionary<string, string>? env = null,
        string? installId = null)
        => OtlpExportOptions.Resolve(
            key => settings is not null && settings.TryGetValue(key, out var value) ? value : null,
            key => env is not null && env.TryGetValue(key, out var value) ? value : null,
            installId);

    [Fact]
    public void Defaults_AreDisabled()
    {
        var config = Resolve();
        Assert.False(config.Enabled);
        Assert.Null(config.Endpoint);
    }

    [Fact]
    public void Requires_BothMasterAndOtlpSwitches()
    {
        // otlp enabled but master telemetry off → disabled
        Assert.False(Resolve(new() { [OtlpExportOptions.TelemetryEnabledKey] = false, [OtlpExportOptions.OtlpEnabledKey] = true }).Enabled);
        // master on but otlp off → disabled
        Assert.False(Resolve(new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = false }).Enabled);
        // both on → enabled
        Assert.True(Resolve(new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = true }).Enabled);
    }

    [Fact]
    public void Enabled_UsesDefaultEndpoint_WhenNoneConfigured()
    {
        var config = Resolve(new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = true });
        Assert.True(config.Enabled);
        Assert.Equal(new Uri(OtlpExportOptions.DefaultEndpoint), config.Endpoint);
    }

    [Fact]
    public void Endpoint_ComesFromSettings_WhenPresent()
    {
        var config = Resolve(new()
        {
            [OtlpExportOptions.TelemetryEnabledKey] = true,
            [OtlpExportOptions.OtlpEnabledKey] = true,
            [OtlpExportOptions.OtlpEndpointKey] = "http://collector.example:4317"
        });
        Assert.Equal(new Uri("http://collector.example:4317"), config.Endpoint);
    }

    [Fact]
    public void Endpoint_EnvironmentOverridesSettings()
    {
        var config = Resolve(
            new()
            {
                [OtlpExportOptions.TelemetryEnabledKey] = true,
                [OtlpExportOptions.OtlpEnabledKey] = true,
                [OtlpExportOptions.OtlpEndpointKey] = "http://from-settings:4317"
            },
            new() { [OtlpExportOptions.OtlpEndpointEnvVar] = "http://from-env:4317" });
        Assert.Equal(new Uri("http://from-env:4317"), config.Endpoint);
    }

    [Fact]
    public void InvalidEndpoint_FallsBackToDefault()
    {
        var config = Resolve(new()
        {
            [OtlpExportOptions.TelemetryEnabledKey] = true,
            [OtlpExportOptions.OtlpEnabledKey] = true,
            [OtlpExportOptions.OtlpEndpointKey] = "not a uri"
        });
        Assert.Equal(new Uri(OtlpExportOptions.DefaultEndpoint), config.Endpoint);
    }

    [Fact]
    public void TelemetryEnv_OverridesMasterSwitch()
    {
        // Setting says off, env says on → enabled (otlp switch also on).
        Assert.True(Resolve(
            new() { [OtlpExportOptions.TelemetryEnabledKey] = false, [OtlpExportOptions.OtlpEnabledKey] = true },
            new() { [OtlpExportOptions.TelemetryEnvVar] = "1" }).Enabled);

        // Setting says on, env says off → disabled.
        Assert.False(Resolve(
            new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = true },
            new() { [OtlpExportOptions.TelemetryEnvVar] = "0" }).Enabled);

        // Unparseable env falls back to the setting.
        Assert.True(Resolve(
            new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = true },
            new() { [OtlpExportOptions.TelemetryEnvVar] = "maybe" }).Enabled);
    }

    [Fact]
    public void DoNotTrack_ForcesDisabled()
    {
        var config = Resolve(
            new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = true },
            new() { [OtlpExportOptions.DoNotTrackEnvVar] = "1" });
        Assert.False(config.Enabled);
        Assert.Null(config.Endpoint);
    }

    [Fact]
    public void InstallId_IsPassedThrough()
    {
        var config = Resolve(
            new() { [OtlpExportOptions.TelemetryEnabledKey] = true, [OtlpExportOptions.OtlpEnabledKey] = true },
            installId: "abc-123");
        Assert.Equal("abc-123", config.InstallId);
    }

    [Fact]
    public void StringSettings_AreAccepted()
    {
        Assert.True(Resolve(new() { [OtlpExportOptions.TelemetryEnabledKey] = "true", [OtlpExportOptions.OtlpEnabledKey] = "1" }).Enabled);
    }
}
