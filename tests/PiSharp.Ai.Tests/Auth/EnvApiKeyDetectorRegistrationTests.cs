using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

/// <summary>
/// Verifies <see cref="EnvApiKeyDetector.RegisterProviderEnvVars"/>: it extends
/// the ambient credential map beyond the built-in model providers, last-write
/// wins per provider, and existing model-provider lookups are unaffected.
/// </summary>
public sealed class EnvApiKeyDetectorRegistrationTests : IDisposable
{
    private static readonly string[] EnvVars = ["P28_SVC_KEY", "P28_SVC_KEY_A", "P28_SVC_KEY_B"];

    private readonly Dictionary<string, string?> _original = new();

    public EnvApiKeyDetectorRegistrationTests()
    {
        foreach (var name in EnvVars) _original[name] = Environment.GetEnvironmentVariable(name);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
    }

    private static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    [Fact]
    public void RegisterProviderEnvVars_EnablesResolutionForNewService()
    {
        Set("P28_SVC_KEY", "svc-key");
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_KEY"]);

        Assert.Equal("svc-key", EnvApiKeyDetector.GetEnvApiKey("p28-svc"));
    }

    [Fact]
    public void RegisterProviderEnvVars_LastWriteWins()
    {
        Set("P28_SVC_KEY_A", "key-a");
        Set("P28_SVC_KEY_B", "key-b");
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_KEY_A"]);
        Assert.Equal("key-a", EnvApiKeyDetector.GetEnvApiKey("p28-svc"));

        // Re-registering with a different env var replaces the previous mapping.
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_KEY_B"]);
        Assert.Equal("key-b", EnvApiKeyDetector.GetEnvApiKey("p28-svc"));
    }

    [Fact]
    public void RegisterProviderEnvVars_DoesNotAffectModelProviders()
    {
        Set("P28_SVC_KEY", "svc-key");
        // Registering a new service must not clobber existing model providers.
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_KEY"]);

        Assert.Equal("svc-key", EnvApiKeyDetector.GetEnvApiKey("p28-svc"));
        Assert.Null(EnvApiKeyDetector.GetEnvApiKey("anthropic")); // ANTHROPIC_* env vars unset in this test
    }
}
