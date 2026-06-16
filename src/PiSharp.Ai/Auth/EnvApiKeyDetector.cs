namespace PiSharp.Ai.Auth;

public static class EnvApiKeyDetector
{
    public const string AuthenticatedMarker = "<authenticated>";

    private static readonly Dictionary<string, string[]> ProviderEnvVarMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = ["ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"],
        ["openai"] = ["OPENAI_API_KEY"],
        ["azure-openai-responses"] = ["AZURE_OPENAI_API_KEY"],
        ["google"] = ["GEMINI_API_KEY"],
        ["mistral"] = ["MISTRAL_API_KEY"],
        ["openrouter"] = ["OPENROUTER_API_KEY"],
        ["together"] = ["TOGETHER_API_KEY"],
        ["fireworks"] = ["FIREWORKS_API_KEY"],
        ["groq"] = ["GROQ_API_KEY"],
        ["xai"] = ["XAI_API_KEY"],
        ["deepseek"] = ["DEEPSEEK_API_KEY"],
        ["cerebras"] = ["CEREBRAS_API_KEY"],
        ["moonshot"] = ["MOONSHOT_API_KEY"],
        ["kimi"] = ["KIMI_API_KEY"],
    };

    public static string? GetEnvApiKey(string provider)
    {
        if (string.Equals(provider, "google-vertex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "amazon-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return HasAmbientCredentials(provider) ? AuthenticatedMarker : null;
        }

        if (!ProviderEnvVarMap.TryGetValue(provider, out var envVars)) return null;

        foreach (var envVar in envVars)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string> GetProviderHeaders(string provider)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(provider, "google-vertex", StringComparison.OrdinalIgnoreCase))
        {
            var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? Environment.GetEnvironmentVariable("GCLOUD_PROJECT");
            var location = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");

            if (!string.IsNullOrWhiteSpace(project)) headers["x-goog-user-project"] = project;
            if (!string.IsNullOrWhiteSpace(location)) headers["x-goog-location"] = location;
        }

        return headers;
    }

    public static bool HasAmbientCredentials(string provider)
    {
        if (string.Equals(provider, "google-vertex", StringComparison.OrdinalIgnoreCase))
        {
            var gac = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
            var location = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");
            return !string.IsNullOrWhiteSpace(gac)
                && File.Exists(gac)
                && !string.IsNullOrWhiteSpace(project)
                && !string.IsNullOrWhiteSpace(location);
        }

        if (string.Equals(provider, "amazon-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"));
        }

        return !string.IsNullOrWhiteSpace(GetEnvApiKey(provider));
    }
}
