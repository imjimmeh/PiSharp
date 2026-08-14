using PiSharp.Extensions;
using PiSharp.Memory.Abstractions;

[assembly: ExtensionMetadata(
    "pisharp-memory-off",
    Name = "PiSharp Memory (off backend)",
    Version = "1.0.0",
    Description = "No-op memory backend: registers the 'off' IMemoryProvider so the core pisharp-memory plugin can answer blocked results when memory is disabled.")]

namespace PiSharp.Memory.Backends.Off;

/// <summary>
/// Registers the <see cref="OffMemoryProvider"/> into <see cref="MemoryServices.Providers"/>
/// so the core plugin can resolve backend "off". Safe to load unconditionally.
/// </summary>
public sealed class OffBackendExtension : IExtension
{
    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        MemoryServices.Providers.Register(new OffMemoryProvider());
        return Task.CompletedTask;
    }
}
