using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

/// <summary>
/// Verifies the P28 service (non-model) credential resolver precedence:
/// configured env-var-name > configured literal > ambient env map, the
/// <c>"&lt;authenticated&gt;"</c> marker, and unknown-service behavior.
/// </summary>
public sealed class ServiceCredentialResolverTests : IDisposable
{
    private static readonly string[] EnvVars =
    [
        "P28_SVC_AMBIENT", "P28_SVC_CONFIGURED", "P28_SVC_LITERAL_LOOKUP"
    ];

    private readonly Dictionary<string, string?> _original = new();

    public ServiceCredentialResolverTests()
    {
        foreach (var name in EnvVars) _original[name] = Environment.GetEnvironmentVariable(name);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
    }

    private static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    [Fact]
    public async Task AmbientEnvVar_ResolvesWhenNoConfiguredKey()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_AMBIENT"]);
        Set("P28_SVC_AMBIENT", "ambient-value");
        var resolver = new ServiceCredentialResolver();

        var result = await resolver.ResolveAsync("p28-svc");

        Assert.Equal("ambient-value", result.ApiKey);
        Assert.True(result.IsAuthenticated);
    }

    [Fact]
    public async Task ConfiguredEnvName_WinsOverAmbient()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_AMBIENT"]);
        Set("P28_SVC_AMBIENT", "ambient-value");
        Set("P28_SVC_CONFIGURED", "configured-value");
        var resolver = new ServiceCredentialResolver();

        var result = await resolver.ResolveAsync("p28-svc", new ServiceCredentialOptions(ConfiguredKey: "P28_SVC_CONFIGURED"));

        Assert.Equal("configured-value", result.ApiKey);
    }

    [Fact]
    public async Task ConfiguredLiteral_UsedDirectly()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_AMBIENT"]);
        Set("P28_SVC_AMBIENT", "ambient-value");
        var resolver = new ServiceCredentialResolver();

        // "sk-p28-literal" is not env-var shaped (lowercase, no underscore), so it is a literal key.
        var result = await resolver.ResolveAsync("p28-svc", new ServiceCredentialOptions(ConfiguredKey: "sk-p28-literal"));

        Assert.Equal("sk-p28-literal", result.ApiKey);
    }

    [Fact]
    public async Task ConfiguredEnvNameUnset_FallsThroughToAmbient()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_AMBIENT"]);
        Set("P28_SVC_AMBIENT", "ambient-value");
        Set("P28_SVC_CONFIGURED", null); // env-name-shape but unset
        var resolver = new ServiceCredentialResolver();

        var result = await resolver.ResolveAsync("p28-svc", new ServiceCredentialOptions(ConfiguredKey: "P28_SVC_CONFIGURED"));

        Assert.Equal("ambient-value", result.ApiKey);
    }

    [Fact]
    public async Task AuthenticatedMarker_IgnoresConfiguredEnvVar_UsesAmbientOnly()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_AMBIENT"]);
        Set("P28_SVC_AMBIENT", "ambient-value");
        Set("P28_SVC_CONFIGURED", "configured-value");
        var resolver = new ServiceCredentialResolver();

        // The marker signals "use ambient only", so the configured env var is skipped and ambient wins.
        var result = await resolver.ResolveAsync("p28-svc", new ServiceCredentialOptions(ConfiguredKey: EnvApiKeyDetector.AuthenticatedMarker));

        Assert.Equal("ambient-value", result.ApiKey);
    }

    [Fact]
    public async Task AuthenticatedMarker_NoAmbient_ReturnsNullUnauthenticated()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc-2", ["P28_SVC_LITERAL_LOOKUP"]);
        Set("P28_SVC_LITERAL_LOOKUP", null);
        var resolver = new ServiceCredentialResolver();

        var result = await resolver.ResolveAsync("p28-svc-2", new ServiceCredentialOptions(ConfiguredKey: EnvApiKeyDetector.AuthenticatedMarker));

        Assert.Null(result.ApiKey);
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public async Task UnknownService_NoConfigured_ReturnsUnauthenticated()
    {
        var resolver = new ServiceCredentialResolver();

        var result = await resolver.ResolveAsync("p28-unknown-service");

        Assert.Null(result.ApiKey);
        Assert.False(result.IsAuthenticated);
    }

    [Fact]
    public async Task ResolvedKey_IsSentAsBearerAuthHeader()
    {
        EnvApiKeyDetector.RegisterProviderEnvVars("p28-svc", ["P28_SVC_AMBIENT"]);
        Set("P28_SVC_AMBIENT", "secret-key-42");
        var resolver = new ServiceCredentialResolver();

        var result = await resolver.ResolveAsync("p28-svc");

        Assert.Equal("Bearer secret-key-42", result.Headers!["Authorization"]);
    }
}
