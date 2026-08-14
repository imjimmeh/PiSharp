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

        if (HasEmbeddedVersionSegment(baseUrl))
        {
            return new Uri($"{baseUrl}/{normalizedResource}");
        }

        return new Uri($"{baseUrl}/v1/{normalizedResource}");
    }

    private static bool HasEmbeddedVersionSegment(string baseUrl)
    {
        // Recognize bases whose final segment already pins an API version or
        // compatibility surface (ZAI .../api/paas/v4, Cloudflare gateway .../compat,
        // Perplexity .../anthropic, Gemini .../v1beta) so the builder does not
        // inject a spurious /v1 into the middle of the path.
        var lastSegment = baseUrl[(baseUrl.LastIndexOf('/') + 1)..];
        if (lastSegment.Length == 0) return false;
        if (lastSegment.Equals("compat", StringComparison.OrdinalIgnoreCase)) return true;
        if (lastSegment.Equals("anthropic", StringComparison.OrdinalIgnoreCase)) return true;
        if (lastSegment.StartsWith("v1beta", StringComparison.OrdinalIgnoreCase)) return true;
        return lastSegment.Length > 1
            && lastSegment[0] is 'v' or 'V'
            && lastSegment.Skip(1).All(char.IsDigit);
    }
}
