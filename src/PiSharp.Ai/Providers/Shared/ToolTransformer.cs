using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Agent.Core.Tools;

namespace PiSharp.Ai.Providers.Shared;

public sealed record ProviderToolDefinition(string Name, string Description, JsonElement ParametersSchema);

public static class ToolTransformer
{
    private const int MaxToolCallIdLength = 64;

    public static IReadOnlyList<ProviderToolDefinition> ToProviderTools(IEnumerable<IAgentTool>? tools)
        => tools?.Select(tool => new ProviderToolDefinition(tool.Name, tool.Description, tool.ParametersSchema.Clone())).ToArray()
           ?? [];

    public static string NormalizeToolCallId(string? id)
    {
        if (IsValidToolCallId(id)) return id!;

        var seed = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var hash = Convert.ToHexString(bytes).ToLowerInvariant()[..24];
        return $"tc_{hash}";
    }

    public static bool IsValidToolCallId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length > MaxToolCallIdLength) return false;
        return id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    }

    public static JsonElement ParseArgumentsOrEmpty(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return EmptyObject();
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    public static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
