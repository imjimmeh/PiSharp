using Xunit;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using PiSharp.Extensions;
using PiSharp.Extensions.Testing;
using PiSharp.Telemetry.Otlp;

namespace PiSharp.Telemetry.Otlp.Tests;

/// <summary>
/// Lifecycle tests for <see cref="OtlpTelemetryExtension"/>. All tests that
/// attach OTel listeners live in this single class so xunit runs them
/// sequentially — a provider left behind by a parallel test would make the
/// gate-off assertions (no listener attached) flaky.
/// </summary>
public sealed class OtlpTelemetryExtensionTests
{
    private const string ActivitySourceName = "PiSharp"; // mirrors the extension + core
    private const string MeterName = "PiSharp";

    private static async Task<FakeExtensionApi> CreateApiAsync(Dictionary<string, object?>? settings = null)
    {
        var api = new FakeExtensionApi { Cwd = Path.GetTempPath() };
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
                await api.Settings.SetAsync(key, value);
        }
        return api;
    }

    private static async Task DispatchSettingsChangedAsync(FakeExtensionApi api)
    {
        var handler = Assert.Single(api.RegisteredHandlers, item => item.EventName == ExtensionEventNames.SettingsChanged).Handler;
        await handler(new ExtensionEvent(ExtensionEventNames.SettingsChanged, null!), CancellationToken.None);
    }

    [Fact]
    public void AssemblyCarriesExtensionMetadata()
    {
        var attribute = typeof(OtlpTelemetryExtension).Assembly.GetCustomAttribute<ExtensionMetadataAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("pisharp-telemetry-otlp", attribute!.Id);
        Assert.False(string.IsNullOrWhiteSpace(attribute.Name));
    }

    [Fact]
    public async Task GateOff_LoadsWithoutAttachingExporters()
    {
        var api = await CreateApiAsync(); // telemetry off by default
        await using var extension = new OtlpTelemetryExtension();
        await extension.InitializeAsync(api);

        Assert.Null(extension.TracerProvider);
        Assert.Null(extension.MeterProvider);

        // No listener attached to the core's emit surface.
        using var source = new ActivitySource(ActivitySourceName);
        Assert.Null(source.StartActivity("probe"));
    }

    [Fact]
    public async Task GateOn_WiresTracerAndMeterProviders()
    {
        var api = await CreateApiAsync(new()
        {
            [OtlpExportOptions.TelemetryEnabledKey] = true,
            [OtlpExportOptions.OtlpEnabledKey] = true
        });
        await using var extension = new OtlpTelemetryExtension();
        await extension.InitializeAsync(api);

        Assert.NotNull(extension.TracerProvider);
        Assert.NotNull(extension.MeterProvider);

        // The core's ActivitySource("PiSharp") now has a listener: spans start.
        using var source = new ActivitySource(ActivitySourceName);
        using var activity = source.StartActivity("probe");
        Assert.NotNull(activity);

        // The core's Meter("PiSharp") now has a listener: instruments are enabled.
        using var meter = new Meter(MeterName);
        var counter = meter.CreateCounter<long>("test.counter");
        Assert.True(counter.Enabled);
    }

    [Fact]
    public async Task GateOff_RequiresMasterSwitch()
    {
        // otlp.enabled without telemetry.enabled → still no providers.
        var api = await CreateApiAsync(new() { [OtlpExportOptions.OtlpEnabledKey] = true });
        await using var extension = new OtlpTelemetryExtension();
        await extension.InitializeAsync(api);

        Assert.Null(extension.TracerProvider);
        Assert.Null(extension.MeterProvider);
    }

    [Fact]
    public async Task SettingsChanged_EnablesAndThenDisablesExport()
    {
        var api = await CreateApiAsync();
        await using var extension = new OtlpTelemetryExtension();
        await extension.InitializeAsync(api);
        Assert.Null(extension.TracerProvider);

        // Toggle both switches on, then dispatch settings_changed → providers appear.
        await api.Settings.SetAsync(OtlpExportOptions.TelemetryEnabledKey, true);
        await api.Settings.SetAsync(OtlpExportOptions.OtlpEnabledKey, true);
        await DispatchSettingsChangedAsync(api);
        Assert.NotNull(extension.TracerProvider);
        Assert.NotNull(extension.MeterProvider);

        // Toggle otlp back off → providers are torn down again.
        await api.Settings.SetAsync(OtlpExportOptions.OtlpEnabledKey, false);
        await DispatchSettingsChangedAsync(api);
        Assert.Null(extension.TracerProvider);
        Assert.Null(extension.MeterProvider);

        using var source = new ActivitySource(ActivitySourceName);
        Assert.Null(source.StartActivity("probe"));
    }

    [Fact]
    public async Task Dispose_TearsDownProvidersAndListeners()
    {
        var api = await CreateApiAsync(new()
        {
            [OtlpExportOptions.TelemetryEnabledKey] = true,
            [OtlpExportOptions.OtlpEnabledKey] = true
        });
        var extension = new OtlpTelemetryExtension();
        await extension.InitializeAsync(api);
        Assert.NotNull(extension.TracerProvider);

        using var source = new ActivitySource(ActivitySourceName);
        using var meter = new Meter(MeterName);
        var counter = meter.CreateCounter<long>("test.counter");

        await extension.DisposeAsync();

        Assert.Null(extension.TracerProvider);
        Assert.Null(extension.MeterProvider);
        Assert.Null(source.StartActivity("probe"));
        Assert.False(counter.Enabled);
    }

    [Fact]
    public void BuildResourceAttributes_MapsInstallId()
    {
        var withId = OtlpTelemetryExtension.BuildResourceAttributes("install-42");
        Assert.Equal("install-42", withId[OtlpExportOptions.InstallIdAttributeName]);

        Assert.Empty(OtlpTelemetryExtension.BuildResourceAttributes(null));
        Assert.Empty(OtlpTelemetryExtension.BuildResourceAttributes("  "));
    }
}
