using PiSharp.Ai.Auth;
using Xunit;

namespace PiSharp.Ai.Tests.Auth;

public sealed class EnvApiKeyDetectorTests : IDisposable
{
    private readonly Dictionary<string, string?> _original = new();

    public EnvApiKeyDetectorTests()
    {
        foreach (var name in AllEnvVars) _original[name] = Environment.GetEnvironmentVariable(name);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original) Environment.SetEnvironmentVariable(name, value);
    }

    private static readonly string[] AllEnvVars =
    [
        "ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY",
        "GEMINI_API_KEY", "MISTRAL_API_KEY", "OPENROUTER_API_KEY", "TOGETHER_API_KEY",
        "FIREWORKS_API_KEY", "GROQ_API_KEY", "XAI_API_KEY", "DEEPSEEK_API_KEY", "CEREBRAS_API_KEY",
        "MOONSHOT_API_KEY", "KIMI_API_KEY", "HUGGINGFACE_API_KEY", "HF_TOKEN", "MINIMAX_API_KEY",
        "ZAI_API_KEY", "OPENCODE_API_KEY", "XIAOMI_API_KEY", "CLOUDFLARE_API_KEY", "PERPLEXITY_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_LOCATION", "GCLOUD_PROJECT",
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY"
    ];

    private static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    [Theory]
    [InlineData("openrouter", "OPENROUTER_API_KEY")]
    [InlineData("together", "TOGETHER_API_KEY")]
    [InlineData("fireworks", "FIREWORKS_API_KEY")]
    [InlineData("groq", "GROQ_API_KEY")]
    [InlineData("xai", "XAI_API_KEY")]
    [InlineData("deepseek", "DEEPSEEK_API_KEY")]
    [InlineData("cerebras", "CEREBRAS_API_KEY")]
    [InlineData("minimax", "MINIMAX_API_KEY")]
    [InlineData("minimax-cn", "MINIMAX_API_KEY")]
    [InlineData("moonshotai", "MOONSHOT_API_KEY")]
    [InlineData("moonshotai-cn", "MOONSHOT_API_KEY")]
    [InlineData("kimi-coding", "KIMI_API_KEY")]
    [InlineData("zai", "ZAI_API_KEY")]
    [InlineData("opencode", "OPENCODE_API_KEY")]
    [InlineData("opencode-go", "OPENCODE_API_KEY")]
    [InlineData("xiaomi", "XIAOMI_API_KEY")]
    [InlineData("xiaomi-token-plan-cn", "XIAOMI_API_KEY")]
    [InlineData("xiaomi-token-plan-ams", "XIAOMI_API_KEY")]
    [InlineData("xiaomi-token-plan-sgp", "XIAOMI_API_KEY")]
    [InlineData("cloudflare-workers-ai", "CLOUDFLARE_API_KEY")]
    [InlineData("cloudflare-ai-gateway", "CLOUDFLARE_API_KEY")]
    [InlineData("perplexity", "PERPLEXITY_API_KEY")]
    public void CataloguedProviderResolvesDocumentedEnvVar(string provider, string envVar)
    {
        Set(envVar, "sk-test-value");

        Assert.Equal("sk-test-value", EnvApiKeyDetector.GetEnvApiKey(provider));
    }

    [Fact]
    public void HuggingFaceAcceptsBothEnvVarNamesPreferringApiKeyConvention()
    {
        Set("HUGGINGFACE_API_KEY", "hf-api-convention");
        Set("HF_TOKEN", "hf-docs-convention");

        Assert.Equal("hf-api-convention", EnvApiKeyDetector.GetEnvApiKey("huggingface"));

        Set("HUGGINGFACE_API_KEY", null);
        Assert.Equal("hf-docs-convention", EnvApiKeyDetector.GetEnvApiKey("huggingface"));

        Set("HF_TOKEN", null);
        Assert.Null(EnvApiKeyDetector.GetEnvApiKey("huggingface"));
    }

    [Fact]
    public void LegacyMoonshotAndKimiKeysStillResolveForCatalogNames()
    {
        Set("MOONSHOT_API_KEY", "moonshot-legacy");
        Set("KIMI_API_KEY", "kimi-legacy");

        Assert.Equal("moonshot-legacy", EnvApiKeyDetector.GetEnvApiKey("moonshot"));
        Assert.Equal("moonshot-legacy", EnvApiKeyDetector.GetEnvApiKey("moonshotai"));
        Assert.Equal("kimi-legacy", EnvApiKeyDetector.GetEnvApiKey("kimi"));
        Assert.Equal("kimi-legacy", EnvApiKeyDetector.GetEnvApiKey("kimi-coding"));
    }

    [Fact]
    public void GoogleVertexAndAmazonBedrockRequireAmbientCredentials()
    {
        var credsFile = Path.Combine(Path.GetTempPath(), $"pi-gac-{Guid.NewGuid():N}.json");
        File.WriteAllText(credsFile, "{}");
        try
        {
            Set("GOOGLE_APPLICATION_CREDENTIALS", credsFile);
            Set("GOOGLE_CLOUD_PROJECT", "my-project");
            Set("GOOGLE_CLOUD_LOCATION", "us-central1");
            Set("AWS_ACCESS_KEY_ID", "AKID");
            Set("AWS_SECRET_ACCESS_KEY", "secret");

            Assert.Equal(EnvApiKeyDetector.AuthenticatedMarker, EnvApiKeyDetector.GetEnvApiKey("google-vertex"));
            Assert.Equal(EnvApiKeyDetector.AuthenticatedMarker, EnvApiKeyDetector.GetEnvApiKey("amazon-bedrock"));

            Set("AWS_SECRET_ACCESS_KEY", null);
            Assert.Null(EnvApiKeyDetector.GetEnvApiKey("amazon-bedrock"));
        }
        finally
        {
            File.Delete(credsFile);
        }
    }

    [Fact]
    public void GoogleVertexWithoutValidAmbientCredentialsReturnsNull()
    {
        Set("GOOGLE_CLOUD_PROJECT", "my-project");
        Set("GOOGLE_CLOUD_LOCATION", "us-central1");

        Assert.Null(EnvApiKeyDetector.GetEnvApiKey("google-vertex"));
    }

    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("")]
    [InlineData("github-copilot")]
    public void UnknownOrOAuthOnlyProvidersReturnNull(string provider)
    {
        Assert.Null(EnvApiKeyDetector.GetEnvApiKey(provider));
    }

    [Fact]
    public void ProviderLookupIsCaseInsensitive()
    {
        Set("ZAI_API_KEY", "zai-key");

        Assert.Equal("zai-key", EnvApiKeyDetector.GetEnvApiKey("ZAI"));
    }
}
