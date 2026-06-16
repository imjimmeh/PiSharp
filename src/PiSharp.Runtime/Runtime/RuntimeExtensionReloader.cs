namespace PiSharp.Runtime;

internal sealed class RuntimeExtensionReloader
{
    public async Task ReloadAsync(SessionRuntime runtime, CancellationToken cancellationToken = default)
    {
        if (runtime.ExtensionManager is null) return;
        await runtime.ExtensionLoadCoordinator.InvalidateAsync(cancellationToken);
        foreach (var sourceId in runtime.ExtensionManager.Registry.SourceIds.ToArray()) runtime.ExtensionManager.Unload(sourceId);
        await PiRuntimeBootstrap.LoadExtensionsIntoAsync(runtime, cancellationToken);
    }
}
