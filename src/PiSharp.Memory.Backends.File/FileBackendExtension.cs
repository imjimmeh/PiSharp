using PiSharp.Extensions;
using PiSharp.Memory.Abstractions;

[assembly: ExtensionMetadata(
    "pisharp-memory-file",
    Name = "PiSharp Memory (file backend)",
    Version = "1.0.0",
    Description = "JSONL + memory_summary.md memory backend: registers the 'file' IMemoryProvider for the core pisharp-memory plugin. Storage root from extensions.pisharp-memory.memory.file.path (default <project>/.pi/PiSharp/memory).")]

namespace PiSharp.Memory.Backends.File;

/// <summary>
/// Registers the <see cref="FileMemoryProvider"/> for the runtime cwd into
/// <see cref="MemoryServices.Providers"/>.
/// </summary>
public sealed class FileBackendExtension : IExtension
{
    internal const string SettingsKeyPath = "memory.file.path";

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        var configured = api.Settings.Get<string>(SettingsKeyPath);
        var rootDir = ResolveRootDir(api, configured);
        var provider = new FileMemoryProvider(rootDir, MemoryProjectKeys.Encode(api.Cwd));
        MemoryServices.Providers.Register(provider);
        return Task.CompletedTask;
    }

    internal static string ResolveRootDir(IExtensionApi api, string? configured)
        => Path.IsPathRooted(configured ?? string.Empty)
            ? configured!
            : Path.Combine(api.Cwd, configured ?? Path.Combine(".pi", "PiSharp", "memory"));
}
