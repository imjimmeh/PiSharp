using System.Text.Json;

namespace PiSharp.Eval.Kernel.CSharp;

/// <summary>
/// Helpers referenced by generated kernel-restore scripts. The C# kernel writes a setup
/// script that re-declares each snapshot variable via <see cref="FromJson{T}"/> so values
/// round-trip through their JSON representation; a lossy variable (or one whose value is
/// not JSON-serializable) restores as <c>default</c> with a warning line.
/// </summary>
public static class KernelGlobals
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Deserializes a JSON value; returns <c>default(T)</c> when null or malformed.</summary>
    public static T? FromJson<T>(string? json)
    {
        if (json is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, WebJsonOptions) ?? default;
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
