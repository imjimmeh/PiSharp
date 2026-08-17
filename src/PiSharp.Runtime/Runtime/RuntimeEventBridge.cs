using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Sessions;
using PiSharp.Agent.Core.Events;
using PiSharp.Agent.Harness;
using PiSharp.Agent.Sessions;
using PiSharp.Extensions;
using PiSharp.Runtime.Telemetry;
using PiSharp.TsBridge;

namespace PiSharp.Runtime;

public sealed class RuntimeEventBridge : IDisposable
{
    private const int BridgeForwardingQueueCapacity = 2048;
    private readonly ILogger _logger;
    private IDisposable? _bridgeForwardingSubscription;
    private CancellationTokenSource? _bridgeForwardingCancellation;
    private Channel<AgentHarnessEvent>? _bridgeForwardingQueue;
    private Task? _bridgeForwardingWorker;
    private IDisposable? _bridgeBeforeAgentStartSubscription;
    private IDisposable? _bridgeBeforePromptRenderSubscription;
    private IDisposable? _bridgeInputSubscription;
    private IDisposable? _bridgeSessionBeforeSwitchSubscription;
    private IDisposable? _bridgeSessionBeforeForkSubscription;
    private IDisposable? _bridgeSettingsChangedSubscription;
    private IDisposable? _bridgeSessionShutdownSubscription;
    private IDisposable? _telemetryInstrumentorSubscription;
    private HarnessTelemetryInstrumentor? _harnessTelemetryInstrumentor;

    public RuntimeEventBridge(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<RuntimeEventBridge>() ?? NullLogger<RuntimeEventBridge>.Instance;
    }

    public void BindHarnessEventForwarding(
        AgentHarness<JsonlSessionMetadata> harness,
        TsExtensionHost? tsHost,
        ExtensionManager? extensionManager)
    {
        UnbindHarnessEventForwarding();
        if (tsHost is null) return;
        StartBridgeForwardingWorker(tsHost);
        _bridgeForwardingSubscription = harness.Subscribe((evt, token) =>
            evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.BeforeAgentStart or AgentHarnessOwnEvent.BeforePromptRender }
                ? Task.CompletedTask
                : evt is AgentHarnessEvent.Own { Event: AgentHarnessOwnEvent.SessionStart }
                    ? tsHost.ForwardEventAsync(evt, token)
                : QueueBridgeForwardingEvent(evt));
        if (extensionManager is not null)
        {
            _bridgeBeforePromptRenderSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.BeforePromptRender,
                tsHost.ForwardExtensionEventAsync);
            _bridgeBeforeAgentStartSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.BeforeAgentStart,
                tsHost.ForwardExtensionEventAsync);
            _bridgeInputSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.Input,
                tsHost.ForwardExtensionEventAsync);
            _bridgeSessionBeforeSwitchSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.SessionBeforeSwitch,
                tsHost.ForwardExtensionEventAsync);
            _bridgeSessionBeforeForkSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.SessionBeforeFork,
                tsHost.ForwardExtensionEventAsync);
            _bridgeSessionShutdownSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.SessionShutdown,
                tsHost.ForwardExtensionEventAsync);
            _bridgeSettingsChangedSubscription = extensionManager.Registry.RegisterHandler(
                "extension:ts-bridge",
                ExtensionEventNames.SettingsChanged,
                tsHost.ForwardExtensionEventAsync);
        }
    }

    public void BindTelemetryInstrumentation(AgentHarness<JsonlSessionMetadata> harness, TelemetryService? telemetry)
    {
        _telemetryInstrumentorSubscription?.Dispose();
        _telemetryInstrumentorSubscription = null;
        if (telemetry is null) return;
        var instrumentor = new HarnessTelemetryInstrumentor(telemetry);
        _harnessTelemetryInstrumentor = instrumentor;
        _telemetryInstrumentorSubscription = harness.Subscribe(instrumentor.OnEventAsync);
    }

    public void UnbindHarnessEventForwarding()
    {
        _bridgeForwardingSubscription?.Dispose();
        _bridgeForwardingSubscription = null;
        StopBridgeForwardingWorker();
        _bridgeBeforeAgentStartSubscription?.Dispose();
        _bridgeBeforeAgentStartSubscription = null;
        _bridgeBeforePromptRenderSubscription?.Dispose();
        _bridgeBeforePromptRenderSubscription = null;
        _bridgeInputSubscription?.Dispose();
        _bridgeInputSubscription = null;
        _bridgeSessionBeforeSwitchSubscription?.Dispose();
        _bridgeSessionBeforeSwitchSubscription = null;
        _bridgeSessionBeforeForkSubscription?.Dispose();
        _bridgeSessionBeforeForkSubscription = null;
        _bridgeSessionShutdownSubscription?.Dispose();
        _bridgeSettingsChangedSubscription?.Dispose();
        _bridgeSettingsChangedSubscription = null;
        _bridgeSessionShutdownSubscription = null;
    }

    public void UnbindTelemetryInstrumentation()
    {
        _telemetryInstrumentorSubscription?.Dispose();
        _telemetryInstrumentorSubscription = null;
        _harnessTelemetryInstrumentor = null;
    }

    private void StartBridgeForwardingWorker(TsExtensionHost tsHost)
    {
        var cancellation = new CancellationTokenSource();
        var queue = Channel.CreateBounded<AgentHarnessEvent>(new BoundedChannelOptions(BridgeForwardingQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _bridgeForwardingCancellation = cancellation;
        _bridgeForwardingQueue = queue;
        _bridgeForwardingWorker = Task.Run(() => RunBridgeForwardingWorkerAsync(tsHost, queue.Reader, cancellation.Token), CancellationToken.None);
    }

    private Task QueueBridgeForwardingEvent(AgentHarnessEvent evt)
    {
        var queue = _bridgeForwardingQueue;
        var cancellation = _bridgeForwardingCancellation;
        if (queue is null || cancellation is null || cancellation.IsCancellationRequested) return Task.CompletedTask;
        if (!queue.Writer.TryWrite(evt)) _ = WriteBridgeForwardingEventAsync(queue.Writer, evt, cancellation.Token);
        return Task.CompletedTask;
    }

    private static async Task WriteBridgeForwardingEventAsync(ChannelWriter<AgentHarnessEvent> writer, AgentHarnessEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(evt, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
        }
    }

    private async Task RunBridgeForwardingWorkerAsync(TsExtensionHost tsHost, ChannelReader<AgentHarnessEvent> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await tsHost.ForwardEventAsync(evt, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(exception, "Ignoring TypeScript bridge event forwarding failure");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StopBridgeForwardingWorker()
    {
        _bridgeForwardingQueue?.Writer.TryComplete();
        _bridgeForwardingCancellation?.Cancel();
        _bridgeForwardingCancellation?.Dispose();
        _bridgeForwardingCancellation = null;
        _bridgeForwardingQueue = null;
        _bridgeForwardingWorker = null;
    }

    public void Dispose()
    {
        UnbindHarnessEventForwarding();
        UnbindTelemetryInstrumentation();
    }
}
