using YamlDotNet.Serialization;

namespace PiSharp.Extensions.Rules;

/// <summary>
/// Parsed result of a rule file's optional YAML frontmatter plus its markdown body.
/// A file with neither <c>always: true</c> nor a <c>pattern</c> is a discovery warning
/// and is skipped by the consumer (plan §5.1).
public sealed record RuleFrontmatterResult(
    string Name,
    bool Always,
    string? Pattern,
    int Priority,
    string Content);

/// Parses the optional YAML frontmatter of a rules file (plan §5.1):
/// <c>name</c>, <c>always</c>, <c>pattern</c>, <c>priority</c>. Body = the markdown
/// after the closing <c>---</c>. Defaults mirror the skill convention: name = file stem,
/// always = false, pattern = null, priority = 0.
/// </summary>
public static class RuleFrontmatter
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public static RuleFrontmatterResult Parse(string fileText, string filePath)
    {
        var normalized = fileText.Replace("\r\n", "\n").Replace("\r", "\n");
        var name = (string?)null;
        var always = false;
        var pattern = (string?)null;
        var priority = 0;
        string body;

        if (normalized.StartsWith("---"))
        {
            var endIndex = normalized.IndexOf("\n---", 3);
            if (endIndex != -1)
            {
                var frontmatterYaml = normalized[4..endIndex];
                var frontmatter = TryParseFrontmatter(frontmatterYaml);
                name = ReadString(frontmatter, "name");
                always = ReadBool(frontmatter, "always") ?? always;
                pattern = ReadString(frontmatter, "pattern");
                priority = ReadInt(frontmatter, "priority") ?? priority;
                body = normalized[(endIndex + 4)..].TrimStart();
            }
            else
            {
                body = normalized;
            }
        }
        else
        {
            body = normalized;
        }

        name ??= string.IsNullOrWhiteSpace(filePath)
            ? "rule"
            : Path.GetFileNameWithoutExtension(filePath);

        return new RuleFrontmatterResult(name, always, pattern, priority, body.Trim());
    }

    private static Dictionary<string, object?> TryParseFrontmatter(string yaml)
    {
        try
        {
            return Deserializer.Deserialize<Dictionary<string, object?>>(yaml) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadString(Dictionary<string, object?> frontmatter, string key)
        => frontmatter.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool? ReadBool(Dictionary<string, object?> frontmatter, string key)
    {
        if (!frontmatter.TryGetValue(key, out var value)) return null;
        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? ReadInt(Dictionary<string, object?> frontmatter, string key)
    {
        if (!frontmatter.TryGetValue(key, out var value)) return null;
        if (value is int number) return number;
        if (value is long longNumber && longNumber is >= int.MinValue and <= int.MaxValue) return (int)longNumber;
        return value is string text && int.TryParse(text, out var parsed) ? parsed : null;
    }
}
