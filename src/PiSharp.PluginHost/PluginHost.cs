using System.Reflection;
using PiSharp.Extensions;

namespace PiSharp.PluginHost;

public sealed class NativePluginHost(PluginHostOptions options)
{
    private readonly Dictionary<string, LoadedNativePlugin> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference> _unloaded = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Discover()
    {
        var explicitPaths = options.ExplicitPluginPaths ?? [];
        var discovered = options.PluginDirectories
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories));
        return explicitPaths.Concat(discovered).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public LoadedNativePlugin Load(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var context = new PluginLoadContext(fullPath);
        var assembly = context.LoadPluginAssembly();
        var metadata = ReadMetadata(assembly, fullPath);
        metadata.Validate();
        var extensionType = assembly.GetTypes().FirstOrDefault(type => !type.IsAbstract && typeof(IExtension).IsAssignableFrom(type))
            ?? throw new InvalidOperationException($"Plugin '{fullPath}' does not contain an IExtension implementation.");
        var extension = (IExtension?)Activator.CreateInstance(extensionType)
            ?? throw new InvalidOperationException($"Could not create extension '{extensionType.FullName}'.");
        var plugin = new LoadedNativePlugin(metadata, extension, context, new WeakReference(context, trackResurrection: false));
        _loaded[metadata.EffectiveSourceId] = plugin;
        return plugin;
    }

    public bool Unload(string sourceId)
    {
        if (!_loaded.Remove(sourceId, out var plugin)) return false;
        _unloaded[sourceId] = plugin.LoadContextReference;
        plugin.Context.Unload();
        return true;
    }

    public bool IsUnloaded(string sourceId)
    {
        if (_loaded.ContainsKey(sourceId)) return false;
        if (!_unloaded.TryGetValue(sourceId, out var reference)) return false;
        for (var i = 0; i < 10 && reference.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return !reference.IsAlive;
    }

    private static ExtensionDescriptor ReadMetadata(Assembly assembly, string path)
    {
        var attr = assembly.GetCustomAttribute<ExtensionMetadataAttribute>()
            ?? assembly.GetTypes().Select(type => type.GetCustomAttribute<ExtensionMetadataAttribute>()).FirstOrDefault(attr => attr is not null);
        if (attr is null) throw new InvalidOperationException($"Plugin '{path}' is missing ExtensionMetadataAttribute.");
        return ExtensionDescriptor.FromMetadata(attr, path);
    }
}

public sealed record LoadedNativePlugin(ExtensionDescriptor Descriptor, IExtension Extension, PluginLoadContext Context, WeakReference LoadContextReference);
