using PiSharp.Ai.Auth;

namespace PiSharp.Mcp;

/// <summary>
/// C1 upgrade seam: the parallel Spine-1 work exposes the runtime's shared <c>IOAuthStorage</c>
/// on <c>ExtensionRuntimeBinding</c> (same instance <c>ProviderCredentialResolver</c> uses, so the
/// plugin never opens a second writer on auth.json). Until that lands, the host falls back to
/// constructing its own <c>FileOAuthStorage(PiAgentPaths.AuthPath)</c>.
/// // TODO: use binding.RuntimeAuthStorage when Spine-1 lands — wire this resolver in the extension
/// entry point instead of the FileOAuthStorage fallback.
/// </summary>
internal static class McpRuntimeAuthStorage
{
    public static Func<IOAuthStorage?> Resolver { get; set; } = () => null;

    public static IOAuthStorage? TryResolve() => Resolver();
}
