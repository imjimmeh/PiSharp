using PiSharp.Agent.Core.Models;

namespace PiSharp.Ai.Providers.OpenAI;

internal static class OpenAIEndpoint
{
    public static Uri Url(ModelDescriptor model, string resource)
    {
        var normalizedResource = resource.Trim('/');
        var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl)
            ? "https://api.openai.com"
            : model.BaseUrl.TrimEnd('/');

        if (model.Api == "openai-codex-responses" && normalizedResource == "responses")
        {
            if (baseUrl.EndsWith("/codex/responses", StringComparison.OrdinalIgnoreCase)) return new Uri(baseUrl);
            if (baseUrl.EndsWith("/codex", StringComparison.OrdinalIgnoreCase)) return new Uri($"{baseUrl}/responses");
            return new Uri($"{baseUrl}/codex/responses");
        }

        if (baseUrl.EndsWith($"/{normalizedResource}", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(baseUrl);
        }

        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri($"{baseUrl}/{normalizedResource}");
        }

        return new Uri($"{baseUrl}/v1/{normalizedResource}");
    }
}
