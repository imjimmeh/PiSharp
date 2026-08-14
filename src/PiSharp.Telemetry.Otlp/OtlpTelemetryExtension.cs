using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-telemetry-otlp", Name = "OTLP Export", Version = "0.1.0",
    Description = "Exports PiSharp traces and metrics to an OpenTelemetry collector over OTLP when telemetry.export.otlp.enabled is set.")]

namespace PiSharp.Telemetry.Otlp;

/// <summary>
/// Optional OTLP exporter plugin (plan §8 / C10). Wires <see cref="TracerProvider"/>
/// and <see cref="MeterProvider"/> to the core's OTel-native
/// <c>ActivitySource("PiSharp")</c> / <c>Meter("PiSharp")</c> surfaces when
/// <c>telemetry.export.otlp.enabled</c> is true (and the master
/// <c>telemetry.enabled</c> switch is on), and tears them down on unload or
/// when the effective configuration changes (settings_changed). The anonymous
/// install id is attached to the export resource as <c>pisharp.install_id</c>.
/// </summary>
public sealed class OtlpTelemetryExtension : IExtension, IAsyncDisposable
{
    /// <summary>Mirrors <c>TelemetryService.ActivitySourceName</c> in PiSharp.Runtime.</summary>
    private const string ActivitySourceName = "PiSharp";

    /// <summary>Mirrors <c>TelemetryService.MeterName</c> in PiSharp.Runtime.</summary>
    private const string MeterName = "PiSharp";

    private readonly object _gate = new();

    private IDisposable? _settingsSubscription;
    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;
    private OtlpExportConfig? _currentConfig;
    private bool _disposed;

    /// <summary>Current tracer provider, or null when export is off. Internal for tests.</summary>
    internal TracerProvider? TracerProvider => _tracerProvider;

    /// <summary>Current meter provider, or null when export is off. Internal for tests.</summary>
    internal MeterProvider? MeterProvider => _meterProvider;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        Refresh(api);
        _settingsSubscription = api.On(ExtensionEventNames.SettingsChanged, (_, _) =>
        {
            Refresh(api);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    /// <summary>Re-resolves the effective export config and rebuilds the providers when it changed.</summary>
    internal void Refresh(IExtensionApi api)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var installId = InstallIdResolver.TryRead(InstallIdResolver.ResolveInstallIdPath(api.Cwd));
            var config = OtlpExportOptions.Resolve(api.Settings.GetCore, Environment.GetEnvironmentVariable, installId);
            if (config.Equals(_currentConfig)) return;

            ShutdownProvidersLocked();
            _currentConfig = config;
            if (config.Enabled) BuildProvidersLocked(config);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _settingsSubscription?.Dispose();
            _settingsSubscription = null;
            ShutdownProvidersLocked();
            _currentConfig = null;
        }
        return ValueTask.CompletedTask;
    }

    internal static IReadOnlyDictionary<string, object> BuildResourceAttributes(string? installId)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(installId))
            attributes[OtlpExportOptions.InstallIdAttributeName] = installId;
        return attributes;
    }

    private void BuildProvidersLocked(OtlpExportConfig config)
    {
        var resourceAttributes = BuildResourceAttributes(config.InstallId);

        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(ActivitySourceName)
            .ConfigureResource(resource => resource.AddAttributes(resourceAttributes))
            .AddOtlpExporter(exporter => exporter.Endpoint = config.Endpoint!)
            .Build();

        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(MeterName)
            .ConfigureResource(resource => resource.AddAttributes(resourceAttributes))
            .AddOtlpExporter(exporter => exporter.Endpoint = config.Endpoint!)
            .Build();
    }

    private void ShutdownProvidersLocked()
    {
        _tracerProvider?.Dispose(); // flushes + shuts down the export pipeline
        _tracerProvider = null;
        _meterProvider?.Dispose();
        _meterProvider = null;
    }
}
