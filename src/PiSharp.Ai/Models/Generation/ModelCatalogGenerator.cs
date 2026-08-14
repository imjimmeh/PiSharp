using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PiSharp.Agent.Core.Models;

namespace PiSharp.Ai.Models.Generation;

public sealed record ModelCatalogGeneratorOptions(
    string OutputPath,
    Uri ModelsDevUri,
    Uri OpenRouterUri,
    Uri AiGatewayUri)
{
    public static ModelCatalogGeneratorOptions Default(string outputPath) => new(
        outputPath,
        new Uri("https://models.dev/api.json"),
        new Uri("https://openrouter.ai/api/v1/models"),
        new Uri("https://ai-gateway.vercel.sh/v1/models"));
}

public sealed record ModelCatalogGenerationResult(int TotalModels, IReadOnlyDictionary<string, int> Providers, string OutputPath);

public static class ModelCatalogGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly IReadOnlyDictionary<string, ProviderProjection> ModelsDevProviders = new Dictionary<string, ProviderProjection>(StringComparer.Ordinal)
    {
        ["amazon-bedrock"] = new("amazon-bedrock", "bedrock-converse-stream", null),
        ["anthropic"] = new("anthropic", "anthropic-messages", "https://api.anthropic.com"),
        ["openai"] = new("openai", "openai-responses", "https://api.openai.com"),
        ["google"] = new("google", "google-generative-ai", "https://generativelanguage.googleapis.com/v1beta"),
        ["google-vertex"] = new("google-vertex", "google-vertex", "https://aiplatform.googleapis.com"),
        ["mistral"] = new("mistral", "mistral-conversations", "https://api.mistral.ai"),
        ["groq"] = new("groq", "openai-completions", "https://api.groq.com/openai/v1"),
        ["cerebras"] = new("cerebras", "openai-completions", "https://api.cerebras.ai/v1"),
        ["deepseek"] = new("deepseek", "openai-completions", "https://api.deepseek.com"),
        ["xai"] = new("xai", "openai-completions", "https://api.x.ai/v1"),
        ["github-copilot"] = new("github-copilot", "github-copilot-chat", null),
        ["minimax"] = new("minimax", "openai-completions", "https://api.minimax.io/v1"),
        ["minimax-cn"] = new("minimax-cn", "openai-completions", "https://api.minimaxi.com/v1"),
        ["moonshotai"] = new("moonshotai", "openai-completions", "https://api.moonshot.ai/v1"),
        ["moonshotai-cn"] = new("moonshotai-cn", "openai-completions", "https://api.moonshot.cn/v1"),
        ["huggingface"] = new("huggingface", "openai-completions", "https://router.huggingface.co/v1"),
        ["fireworks-ai"] = new("fireworks", "anthropic-messages", "https://api.fireworks.ai/inference"),
        ["togetherai"] = new("together", "openai-completions", "https://api.together.xyz/v1"),
        ["opencode"] = new("opencode", "openai-completions", "https://api.opencode.ai/v1"),
        ["opencode-go"] = new("opencode-go", "openai-completions", "https://api.opencode.ai/v1"),
        ["kimi-for-coding"] = new("kimi-coding", "openai-completions", "https://api.moonshot.ai/v1"),
        ["cloudflare-workers-ai"] = new("cloudflare-workers-ai", "openai-completions", "https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1"),
        ["cloudflare-ai-gateway"] = new("cloudflare-ai-gateway", "openai-completions", "https://gateway.ai.cloudflare.com/v1/{account_id}/{gateway}/compat"),
        ["xiaomi"] = new("xiaomi", "openai-completions", "https://api.xiaomimimo.com/v1"),
        ["xiaomi-token-plan-cn"] = new("xiaomi-token-plan-cn", "openai-completions", "https://token-plan-cn.xiaomimimo.com/v1"),
        ["xiaomi-token-plan-ams"] = new("xiaomi-token-plan-ams", "openai-completions", "https://token-plan-ams.xiaomimimo.com/v1"),
        ["xiaomi-token-plan-sgp"] = new("xiaomi-token-plan-sgp", "openai-completions", "https://token-plan-sgp.xiaomimimo.com/v1"),
        ["zai"] = new("zai", "openai-completions", "https://api.z.ai/api/paas/v4"),
    };

    public static async Task<int> Main(string[] args)
    {
        var output = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(FindRepoRoot(Environment.CurrentDirectory), "src", "PiSharp.Ai", "Models", "Generated", "BuiltInModels.g.cs");
        var result = await GenerateAsync(ModelCatalogGeneratorOptions.Default(output));
        Console.WriteLine($"Generated {result.TotalModels} models into {result.OutputPath}");
        foreach (var provider in result.Providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {provider.Key}: {provider.Value}");
        }
        return 0;
    }

    public static async Task<ModelCatalogGenerationResult> GenerateAsync(ModelCatalogGeneratorOptions options, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        using var ownedClient = httpClient is null ? new HttpClient() : null;
        var client = httpClient ?? ownedClient!;
        var models = new List<GeneratedModel>();
        models.AddRange(await LoadModelsDevAsync(client, options.ModelsDevUri, cancellationToken));
        models.AddRange(await LoadOpenRouterAsync(client, options.OpenRouterUri, cancellationToken));
        models.AddRange(await LoadAiGatewayAsync(client, options.AiGatewayUri, cancellationToken));
        models.AddRange(ManualCodexModels());

        var deduped = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Provider) && !string.IsNullOrWhiteSpace(model.Id) && !string.IsNullOrWhiteSpace(model.Api))
            .GroupBy(model => (model.Provider, model.Id))
            .Select(group => group.First())
            .OrderBy(model => model.Provider, StringComparer.Ordinal)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();

        var output = Emit(deduped);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        await File.WriteAllTextAsync(options.OutputPath, output, cancellationToken);
        return new ModelCatalogGenerationResult(
            deduped.Length,
            deduped.GroupBy(model => model.Provider, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            options.OutputPath);
    }

    public static string Emit(IReadOnlyList<GeneratedModel> models)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("// This file is auto-generated by src/PiSharp.Ai/Models/Generation/ModelCatalogGenerator.cs");
        builder.AppendLine("// Do not edit manually — run the PiSharp.Ai model generator to update");
        builder.AppendLine();
        builder.AppendLine("using PiSharp.Agent.Core.Models;");
        builder.AppendLine("using PiSharp.Ai.Models;");
        builder.AppendLine();
        builder.AppendLine("namespace PiSharp.Ai.Models.Generated;");
        builder.AppendLine();
        builder.AppendLine("public static class BuiltInModels");
        builder.AppendLine("{");
        builder.AppendLine("    public static IReadOnlyList<CatalogModel> All { get; } =");
        builder.AppendLine("    [");

        var first = true;
        foreach (var providerGroup in models.GroupBy(model => model.Provider, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (!first) builder.AppendLine();
            builder.AppendLine($"        // {EscapeComment(providerGroup.Key)}");
            foreach (var model in providerGroup.OrderBy(model => model.Id, StringComparer.Ordinal))
            {
                if (!first) builder.AppendLine();
                first = false;
                EmitModel(builder, model);
            }
        }

        builder.AppendLine("    ];");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<GeneratedModel>> LoadModelsDevAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var stream = await client.GetStreamAsync(uri, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var models = new List<GeneratedModel>();
        foreach (var providerProperty in document.RootElement.EnumerateObject())
        {
            if (!ModelsDevProviders.TryGetValue(providerProperty.Name, out var projection)) continue;
            if (!providerProperty.Value.TryGetProperty("models", out var providerModels) || providerModels.ValueKind != JsonValueKind.Object) continue;
            foreach (var modelProperty in providerModels.EnumerateObject())
            {
                var model = modelProperty.Value;
                if (GetBool(model, "tool_call") != true) continue;
                if (ShouldSkipModelsDevModel(providerProperty.Name, modelProperty.Name)) continue;
                models.Add(ProjectModelsDevModel(projection, modelProperty.Name, model));
            }
        }
        return models;
    }

    private static async Task<IReadOnlyList<GeneratedModel>> LoadOpenRouterAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var stream = await client.GetStreamAsync(uri, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var models = new List<GeneratedModel>();
        foreach (var model in data.EnumerateArray())
        {
            if (!StringArrayContains(model, "supported_parameters", "tools")) continue;
            var id = GetString(model, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            models.Add(new GeneratedModel(
                Provider: "openrouter",
                Id: id,
                Api: "openai-completions",
                Name: GetString(model, "name") ?? id,
                BaseUrl: "https://openrouter.ai/api/v1",
                Reasoning: StringArrayContains(model, "supported_parameters", "reasoning"),
                Input: ["text"],
                ContextWindow: GetInt(model, "context_length") ?? 4096,
                MaxTokens: GetPathInt(model, ["top_provider", "max_completion_tokens"]) ?? 4096,
                Cost: new ModelCost(
                    ParsePerTokenCost(GetPathString(model, ["pricing", "prompt"])),
                    ParsePerTokenCost(GetPathString(model, ["pricing", "completion"])),
                    ParsePerTokenCost(GetPathString(model, ["pricing", "input_cache_read"])),
                    ParsePerTokenCost(GetPathString(model, ["pricing", "input_cache_write"])))));
        }
        return models;
    }

    private static async Task<IReadOnlyList<GeneratedModel>> LoadAiGatewayAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var stream = await client.GetStreamAsync(uri, cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.TryGetProperty("models", out var modelsProperty) && modelsProperty.ValueKind == JsonValueKind.Array ? modelsProperty : default;
        if (array.ValueKind != JsonValueKind.Array) return [];
        var models = new List<GeneratedModel>();
        foreach (var model in array.EnumerateArray())
        {
            var id = GetString(model, "id") ?? GetString(model, "model");
            if (string.IsNullOrWhiteSpace(id)) continue;
            models.Add(new GeneratedModel(
                Provider: "vercel-ai-gateway",
                Id: id,
                Api: "openai-completions",
                Name: GetString(model, "name") ?? id,
                BaseUrl: "https://ai-gateway.vercel.sh/v1",
                Reasoning: GetBool(model, "reasoning") ?? false,
                Input: StringArrayContains(model, "input", "image") ? ["text", "image"] : ["text"],
                ContextWindow: GetPathInt(model, ["limits", "context"]) ?? GetPathInt(model, ["limit", "context"]) ?? 4096,
                MaxTokens: GetPathInt(model, ["limits", "output"]) ?? GetPathInt(model, ["limit", "output"]) ?? 4096,
                Cost: new ModelCost(
                    GetPathDecimal(model, ["cost", "input"]),
                    GetPathDecimal(model, ["cost", "output"]),
                    GetPathDecimal(model, ["cost", "cacheRead"]) + GetPathDecimal(model, ["cost", "cache_read"]),
                    GetPathDecimal(model, ["cost", "cacheWrite"]) + GetPathDecimal(model, ["cost", "cache_write"]))));
        }
        return models;
    }

    private static GeneratedModel ProjectModelsDevModel(ProviderProjection projection, string id, JsonElement model)
    {
        var output = GetPathInt(model, ["limit", "output"]);
        var context = GetPathInt(model, ["limit", "context"]);
        return new GeneratedModel(
            Provider: projection.Provider,
            Id: id,
            Api: projection.Api,
            Name: GetString(model, "name") ?? id,
            BaseUrl: projection.Provider == "amazon-bedrock" ? GetBedrockBaseUrl(id) : projection.BaseUrl ?? string.Empty,
            Reasoning: GetBool(model, "reasoning") ?? false,
            Input: StringArrayContainsPath(model, ["modalities", "input"], "image") ? ["text", "image"] : ["text"],
            ContextWindow: context ?? 4096,
            MaxTokens: output ?? 4096,
            Cost: new ModelCost(
                GetPathDecimal(model, ["cost", "input"]),
                GetPathDecimal(model, ["cost", "output"]),
                GetPathDecimal(model, ["cost", "cache_read"]),
                GetPathDecimal(model, ["cost", "cache_write"])),
            ThinkingLevelMap: DefaultThinkingMap(GetBool(model, "reasoning") ?? false));
    }

    private static IReadOnlyList<GeneratedModel> ManualCodexModels()
    {
        // OpenAI Codex (ChatGPT OAuth) models are not fetched from models.dev;
        // keep a small explicit list to match the JavaScript generator.
        const string baseUrl = "https://chatgpt.com/backend-api";
        const int codexContext = 272000;
        const int codexSparkContext = 128000;
        const int codexMaxTokens = 128000;
        return
        [
            new("openai-codex", "gpt-5.2", "openai-codex-responses", "GPT-5.2", baseUrl, true, ["text", "image"], codexContext, codexMaxTokens, new ModelCost(1.75m, 14m, 0.175m), DefaultThinkingMap(true)),
            new("openai-codex", "gpt-5.3-codex", "openai-codex-responses", "GPT-5.3 Codex", baseUrl, true, ["text", "image"], codexContext, codexMaxTokens, new ModelCost(1.75m, 14m, 0.175m), DefaultThinkingMap(true)),
            new("openai-codex", "gpt-5.3-codex-spark", "openai-codex-responses", "GPT-5.3 Codex Spark", baseUrl, true, ["text"], codexSparkContext, codexMaxTokens, new ModelCost(1.75m, 14m, 0.175m), DefaultThinkingMap(true)),
            new("openai-codex", "gpt-5.4", "openai-codex-responses", "GPT-5.4", baseUrl, true, ["text", "image"], codexContext, codexMaxTokens, new ModelCost(2.5m, 15m, 0.25m), DefaultThinkingMap(true)),
            new("openai-codex", "gpt-5.4-mini", "openai-codex-responses", "GPT-5.4 mini", baseUrl, true, ["text", "image"], codexContext, codexMaxTokens, new ModelCost(0.75m, 4.5m, 0.075m), DefaultThinkingMap(true)),
            new("openai-codex", "gpt-5.5", "openai-codex-responses", "GPT-5.5", baseUrl, true, ["text", "image"], codexContext, codexMaxTokens, new ModelCost(5m, 30m, 0.5m), DefaultThinkingMap(true)),
        ];
    }

    private static bool ShouldSkipModelsDevModel(string provider, string id)
        => provider == "amazon-bedrock" && (id.StartsWith("ai21.jamba", StringComparison.Ordinal) || id.StartsWith("mistral.mistral-7b-instruct-v0", StringComparison.Ordinal));

    private static IReadOnlyDictionary<string, int>? DefaultThinkingMap(bool reasoning)
        => reasoning ? new Dictionary<string, int>(StringComparer.Ordinal) { ["minimal"] = 1024, ["low"] = 1024, ["medium"] = 4096, ["high"] = 16384, ["xhigh"] = 32000 } : null;

    private static string GetBedrockBaseUrl(string modelId)
        => modelId.StartsWith("eu.", StringComparison.Ordinal) ? "https://bedrock-runtime.eu-central-1.amazonaws.com" : "https://bedrock-runtime.us-east-1.amazonaws.com";

    private static void EmitModel(StringBuilder builder, GeneratedModel model)
    {
        builder.AppendLine($"        new({Literal(model.Provider)}, {Literal(model.Id)}, new ModelDescriptor(");
        builder.AppendLine($"            Provider: {Literal(model.Provider)},");
        builder.AppendLine($"            Id: {Literal(model.Id)},");
        builder.AppendLine($"            Api: {Literal(model.Api)},");
        builder.AppendLine($"            Name: {Literal(model.Name)},");
        if (!string.IsNullOrWhiteSpace(model.BaseUrl)) builder.AppendLine($"            BaseUrl: {Literal(model.BaseUrl)},");
        builder.AppendLine($"            Reasoning: {BoolLiteral(model.Reasoning)},");
        builder.AppendLine($"            ContextWindow: {model.ContextWindow},");
        builder.AppendLine($"            MaxTokens: {model.MaxTokens},");
        if (model.ThinkingLevelMap is { Count: > 0 }) EmitThinkingMap(builder, model.ThinkingLevelMap);
        builder.AppendLine($"            Input: [{string.Join(", ", model.Input.Select(Literal))}],");
        builder.Append($"            Cost: new ModelCost(Input: {DecimalLiteral(model.Cost.Input)}, Output: {DecimalLiteral(model.Cost.Output)}");
        if (model.Cost.CacheRead != 0) builder.Append($", CacheRead: {DecimalLiteral(model.Cost.CacheRead)}");
        if (model.Cost.CacheWrite != 0) builder.Append($", CacheWrite: {DecimalLiteral(model.Cost.CacheWrite)}");
        builder.AppendLine("))),");
    }

    private static void EmitThinkingMap(StringBuilder builder, IReadOnlyDictionary<string, int> map)
    {
        builder.AppendLine("            ThinkingLevelMap: new Dictionary<string, int>");
        builder.AppendLine("            {");
        foreach (var item in map.OrderBy(pair => ThinkingOrder(pair.Key)))
        {
            builder.AppendLine($"                [{Literal(item.Key)}] = {item.Value},");
        }
        builder.AppendLine("            },");
    }

    private static int ThinkingOrder(string key) => key switch { "minimal" => 0, "low" => 1, "medium" => 2, "high" => 3, "xhigh" => 4, _ => 99 };

    private static string FindRepoRoot(string start)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PiSharp.sln"))) return current.FullName;
            current = current.Parent;
        }
        return start;
    }

    private static string Literal(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static string BoolLiteral(bool value) => value ? "true" : "false";
    private static string DecimalLiteral(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture) + "m";
    private static string EscapeComment(string value) => value.Replace("*/", "* /", StringComparison.Ordinal);

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? GetPathString(JsonElement element, IReadOnlyList<string> path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int? GetPathInt(JsonElement element, IReadOnlyList<string> path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var number) ? number : null;
    }

    private static decimal GetPathDecimal(JsonElement element, IReadOnlyList<string> path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return 0;
        }
        return current.ValueKind == JsonValueKind.Number && current.TryGetDecimal(out var number) ? number : 0;
    }

    private static bool StringArrayContains(JsonElement element, string name, string expected)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return false;
        return value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == expected);
    }

    private static bool StringArrayContainsPath(JsonElement element, IReadOnlyList<string> path, string expected)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return false;
        }
        return current.ValueKind == JsonValueKind.Array && current.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == expected);
    }

    private static decimal ParsePerTokenCost(string? value)
        => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed * 1_000_000m : 0;

    private sealed record ProviderProjection(string Provider, string Api, string? BaseUrl);
}

public sealed record GeneratedModel(
    string Provider,
    string Id,
    string Api,
    string Name,
    string BaseUrl,
    bool Reasoning,
    IReadOnlyList<string> Input,
    int ContextWindow,
    int MaxTokens,
    ModelCost Cost,
    IReadOnlyDictionary<string, int>? ThinkingLevelMap = null);
