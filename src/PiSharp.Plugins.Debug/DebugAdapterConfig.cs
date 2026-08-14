using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Abstractions;

namespace PiSharp.Plugins.Debug;

/// <summary>
/// Per-language debug adapter configuration from
/// <c>extensions.pisharp-debug.adapters.&lt;language&gt;</c>. <see cref="Attach"/> is the
/// default DAP request body for <c>attach</c>; <see cref="DebugAdapterConfigParser.Interpolate"/>
/// substitutes <c>${cwd}</c>/<c>${path}</c> before it is sent.
/// </summary>
public sealed record DebugAdapterConfig(
    IReadOnlyList<string> Command,
    IReadOnlyList<string>? Extensions,
    IReadOnlyDictionary<string, object?>? Env,
    string? WorkingDirectory,
    JsonElement? Attach,
    int TimeoutMs = 10000);

public static class DebugAdapterConfigParser
{
    public static Result<DebugAdapterConfig, string> Parse(JsonElement section, string language)
    {
        if (section.ValueKind != JsonValueKind.Object)
        {
            return Result.Err<DebugAdapterConfig, string>($"Debug adapter config for '{language}' must be a JSON object.");
        }

        if (!section.TryGetProperty("command", out var commandElement)
            || commandElement.ValueKind != JsonValueKind.Array
            || commandElement.GetArrayLength() == 0)
        {
            return Result.Err<DebugAdapterConfig, string>($"Debug adapter config for '{language}' requires a non-empty 'command' array.");
        }

        var command = ReadStringArray(commandElement);
        if (command.Length == 0)
        {
            return Result.Err<DebugAdapterConfig, string>($"Debug adapter config for '{language}' has a 'command' array with no string entries.");
        }

        var extensions = section.TryGetProperty("extensions", out var extensionsElement) && extensionsElement.ValueKind == JsonValueKind.Array
            ? ReadStringArray(extensionsElement)
            : [];
        var timeoutMs = ReadPositiveInt(section, "timeoutMs", 10000);
        if (timeoutMs <= 0)
        {
            return Result.Err<DebugAdapterConfig, string>($"Debug adapter config for '{language}' has invalid 'timeoutMs' value (expected a positive integer).");
        }

        var workingDirectory = section.TryGetProperty("workingDirectory", out var workingDirectoryElement) && workingDirectoryElement.ValueKind == JsonValueKind.String
            ? workingDirectoryElement.GetString()
            : null;

        JsonElement? attach = null;
        if (section.TryGetProperty("attach", out var attachElement) && attachElement.ValueKind == JsonValueKind.Object)
        {
            attach = attachElement.Clone();
        }

        return Result.Ok<DebugAdapterConfig, string>(new DebugAdapterConfig(
            command,
            extensions,
            ReadObjectMap(section, "env"),
            workingDirectory,
            attach,
            timeoutMs));
    }

    /// <summary>
    /// Returns a deep copy of <paramref name="attach"/> with <c>${cwd}</c> replaced by
    /// <paramref name="cwd"/> and <c>${path}</c> replaced by <paramref name="path"/>.
    /// <c>${path}</c> is left untouched when <paramref name="path"/> is null.
    /// </summary>
    public static JsonElement Interpolate(JsonElement attach, string cwd, string? path)
    {
        var node = JsonNode.Parse(attach.GetRawText());
        if (node is null)
        {
            return default;
        }

        InterpolateNode(node, cwd, path);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void InterpolateNode(JsonNode node, string cwd, string? path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is not null) InterpolateNode(property.Value, cwd, path);
                }

                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                {
                    if (item is not null) InterpolateNode(item, cwd, path);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                value.ReplaceWith(text.Replace("${cwd}", cwd).Replace("${path}", path ?? "${path}"));
                break;
        }
    }

    private static string[] ReadStringArray(JsonElement element)
        => element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();

    private static int ReadPositiveInt(JsonElement section, string name, int fallback)
        => section.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : fallback;

    private static IReadOnlyDictionary<string, object?>? ReadObjectMap(JsonElement section, string name)
    {
        if (!section.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText())
            ?? new Dictionary<string, object?>();
    }
}
