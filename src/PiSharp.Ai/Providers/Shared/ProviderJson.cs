using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Ai.Providers.Shared;

internal static class ProviderJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
