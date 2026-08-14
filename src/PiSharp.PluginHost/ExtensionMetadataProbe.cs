using System.Reflection;
using System.Runtime.Loader;

namespace PiSharp.PluginHost;

/// <summary>
/// Metadata-only probe used to decide whether a discovered DLL is a loadable native plugin.
/// Reads the assembly's custom attributes in a throwaway collectible load context so that
/// support DLLs — dependencies such as <c>PiSharp.Plugins.ProtocolJsonRpc.dll</c> that ship
/// next to an entry assembly but carry no <c>ExtensionMetadataAttribute</c> — are skipped by
/// discovery instead of aborting startup. Explicitly supplied <c>--extension</c> paths bypass
/// this probe and are still validated strictly by <see cref="NativePluginHost.Load"/>.
/// </summary>
internal static class ExtensionMetadataProbe
{
    public static bool HasExtensionMetadata(string assemblyPath)
    {
        var context = new ProbeLoadContext();
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            return assembly.GetCustomAttributesData().Any(data =>
                string.Equals(data.AttributeType?.FullName, "PiSharp.Extensions.ExtensionMetadataAttribute", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            // Not a loadable plugin assembly: broken metadata, non-managed binary, or a
            // dependency that cannot be resolved from the host. Discovery skips it.
            return false;
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed class ProbeLoadContext : AssemblyLoadContext
    {
        public ProbeLoadContext()
            : base(isCollectible: true)
        {
        }
    }
}
