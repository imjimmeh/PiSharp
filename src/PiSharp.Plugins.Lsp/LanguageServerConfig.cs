using System.Text.Json;
using PiSharp.Abstractions;

namespace PiSharp.Plugins.Lsp;

/// <summary>
/// Per-language server configuration from <c>extensions.pisharp-lsp.servers.&lt;language&gt;</c>.
/// </summary>
public sealed record LanguageServerConfig(
    IReadOnlyList<string> Command,
    IReadOnlyList<string> Extensions,
    string? LanguageId = null,
    IReadOnlyDictionary<string, object?>? Env = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, object?>? Init = null,
    string Sync = "full",
    int TimeoutMs = 10000);

public static class LanguageServerConfigParser
{
    public static Result<LanguageServerConfig, string> Parse(JsonElement section, string language)
    {
        if (section.ValueKind != JsonValueKind.Object)
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' must be a JSON object.");
        }

        if (!section.TryGetProperty("command", out var commandElement)
            || commandElement.ValueKind != JsonValueKind.Array
            || commandElement.GetArrayLength() == 0)
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' requires a non-empty 'command' array.");
        }

        var command = ReadStringArray(commandElement);
        if (command.Length == 0)
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' has a 'command' array with no string entries.");
        }

        if (!section.TryGetProperty("extensions", out var extensionsElement)
            || extensionsElement.ValueKind != JsonValueKind.Array
            || extensionsElement.GetArrayLength() == 0)
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' requires a non-empty 'extensions' array.");
        }

        var extensions = ReadStringArray(extensionsElement);
        if (extensions.Length == 0)
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' has an 'extensions' array with no string entries.");
        }

        var sync = section.TryGetProperty("sync", out var syncElement)
            ? syncElement.ValueKind == JsonValueKind.String ? syncElement.GetString() ?? "full" : "full"
            : "full";
        if (sync is not ("full" or "incremental"))
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' has invalid 'sync' value '{sync}' (expected 'full' or 'incremental').");
        }

        var timeoutMs = ReadPositiveInt(section, "timeoutMs", 10000);
        if (timeoutMs <= 0)
        {
            return Result.Err<LanguageServerConfig, string>($"Language server config for '{language}' has invalid 'timeoutMs' value (expected a positive integer).");
        }

        var languageId = section.TryGetProperty("languageId", out var languageIdElement) && languageIdElement.ValueKind == JsonValueKind.String
            ? languageIdElement.GetString()
            : null;
        var workingDirectory = section.TryGetProperty("workingDirectory", out var workingDirectoryElement) && workingDirectoryElement.ValueKind == JsonValueKind.String
            ? workingDirectoryElement.GetString()
            : null;

        return Result.Ok<LanguageServerConfig, string>(new LanguageServerConfig(
            command,
            extensions,
            languageId,
            ReadObjectMap(section, "env"),
            workingDirectory,
            ReadObjectMap(section, "init"),
            sync,
            timeoutMs));
    }

    private static string[] ReadStringArray(JsonElement element)
        => element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();

    private static int ReadPositiveInt(JsonElement section, string name, int fallback)
    {
        if (section.TryGetProperty(name, out var element) && element.TryGetInt32(out var value))
        {
            return value;
        }

        return fallback;
    }

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
