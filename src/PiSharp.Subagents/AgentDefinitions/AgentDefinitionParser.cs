using System.Globalization;
using System.Text.Json;
using PiSharp.Abstractions.Options;
using YamlDotNet.RepresentationModel;

namespace PiSharp.Subagents.AgentDefinitions;

/// <summary>Non-fatal authoring problem found while parsing an agent definition file.</summary>
public sealed record AgentDiagnostic(string Type, string Code, string Message, string Path);

public sealed record AgentDefinitionParseResult(AgentDefinition? Definition, AgentDiagnostic? Diagnostic);

/// <summary>
/// Parses markdown agent definitions with a leading YAML frontmatter block (same delimiter rules as
/// skill frontmatter). The markdown body after the frontmatter is the default system prompt.
/// </summary>
public static class AgentDefinitionParser
{
    private const string FrontmatterDelimiter = "---";

    public static AgentDefinitionParseResult Parse(string content, string sourcePath, AgentSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (frontmatter, body) = ParseFrontmatter(content);

        var name = Scalar(frontmatter, "name");
        var description = Scalar(frontmatter, "description");
        if (string.IsNullOrWhiteSpace(name))
            return new(null, new("warning", "missing_name", "Agent frontmatter must include a name.", sourcePath));
        if (string.IsNullOrWhiteSpace(description))
            return new(null, new("warning", "missing_description", "Agent frontmatter must include a description.", sourcePath));

        var systemPrompt = Scalar(frontmatter, "systemPrompt");
        if (string.IsNullOrWhiteSpace(systemPrompt))
            systemPrompt = body;
        if (string.IsNullOrWhiteSpace(systemPrompt))
            return new(null, new("warning", "missing_system_prompt", "Agent must define a systemPrompt in frontmatter or a markdown body.", sourcePath));

        var tools = StringList(frontmatter, "tools");
        var spawns = StringList(frontmatter, "spawns");
        var autoloadSkills = StringList(frontmatter, "autoloadSkills");
        var restrictSkills = StringList(frontmatter, "skills");

        AgentDiagnostic? diagnostic = null;
        if (restrictSkills is not null && autoloadSkills is not null && autoloadSkills.Count > 0)
            diagnostic = new("warning", "skills_conflict", "Agent defines both 'skills' and 'autoloadSkills'; 'skills' wins.", sourcePath);

        var thinkingLevel = Scalar(frontmatter, "thinkingLevel") is { } thinkingText
            && Enum.TryParse<ThinkingLevel>(thinkingText, ignoreCase: true, out var parsedThinking)
                ? parsedThinking
                : (ThinkingLevel?)null;

        var outputSchema = ConvertOutputSchema(frontmatter, sourcePath, out var outputDiagnostic);
        diagnostic ??= outputDiagnostic;

        var definition = new AgentDefinition
        {
            Name = name,
            Description = description,
            SystemPrompt = systemPrompt,
            Tools = tools ?? [],
            Spawns = spawns ?? ["task"],
            Model = Scalar(frontmatter, "model"),
            ThinkingLevel = thinkingLevel,
            OutputSchema = outputSchema,
            AutoloadSkills = autoloadSkills ?? [],
            RestrictSkills = restrictSkills,
            ReadSummarize = Bool(frontmatter, "readSummarize"),
            Hide = Bool(frontmatter, "hide"),
            SourcePath = sourcePath,
            Source = source,
        };

        return new(definition, diagnostic);
    }

    public static AgentDefinitionParseResult ParseFrontmatterOnly(string content, string sourcePath, AgentSourceKind source)
        => Parse(content, sourcePath, source);

    private static JsonElement? ConvertOutputSchema(
        Dictionary<string, YamlNode> frontmatter,
        string sourcePath,
        out AgentDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (!frontmatter.TryGetValue("output", out var node) || node is null)
            return null;

        if (node is not YamlMappingNode)
        {
            diagnostic = new("warning", "output_must_be_object", "Agent 'output' must be a JSON Schema object.", sourcePath);
            return null;
        }

        return YamlNodeConverter.Convert(node);
    }

    private static (Dictionary<string, YamlNode> Frontmatter, string Body) ParseFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith(FrontmatterDelimiter))
            return (new(StringComparer.Ordinal), normalized);

        var endIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex == -1)
            return (new(StringComparer.Ordinal), normalized);

        var yamlText = normalized[4..endIndex];
        var body = normalized[(endIndex + 4)..].TrimStart();
        try
        {
            using var reader = new StringReader(yamlText);
            var stream = new YamlStream();
            stream.Load(reader);
            var root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
            if (root is null)
                return (new(StringComparer.Ordinal), body);

            var frontmatter = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var pair in root.Children)
            {
                if (pair.Key is YamlScalarNode key && key.Value is not null)
                    frontmatter[key.Value] = pair.Value;
            }
            return (frontmatter, body);
        }
        catch
        {
            return (new(StringComparer.Ordinal), body);
        }
    }

    private static string? Scalar(Dictionary<string, YamlNode> frontmatter, string key)
        => frontmatter.TryGetValue(key, out var node) && node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value
            : null;

    private static IReadOnlyList<string>? StringList(Dictionary<string, YamlNode> frontmatter, string key)
    {
        if (!frontmatter.TryGetValue(key, out var node))
            return null;
        if (node is not YamlSequenceNode sequence)
            return null;
        var items = sequence.Children
            .OfType<YamlScalarNode>()
            .Select(scalar => scalar.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        return items;
    }

    private static bool Bool(Dictionary<string, YamlNode> frontmatter, string key)
        => frontmatter.TryGetValue(key, out var node)
            && node is YamlScalarNode scalar
            && (string.Equals(scalar.Value, "true", StringComparison.OrdinalIgnoreCase)
                || scalar.Value == "1");

    /// <summary>Converts a YAML representation-model node to a JSON element (JSON is a YAML subset).</summary>
    private static class YamlNodeConverter
    {
        public static JsonElement Convert(YamlNode node)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(writer, node);
            }
            return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        }

        private static void Write(Utf8JsonWriter writer, YamlNode node)
        {
            switch (node)
            {
                case YamlScalarNode scalar:
                    WriteScalar(writer, scalar.Value);
                    break;
                case YamlSequenceNode sequence:
                    writer.WriteStartArray();
                    foreach (var child in sequence.Children)
                        Write(writer, child);
                    writer.WriteEndArray();
                    break;
                case YamlMappingNode mapping:
                    writer.WriteStartObject();
                    foreach (var pair in mapping.Children)
                    {
                        var key = (pair.Key as YamlScalarNode)?.Value ?? pair.Key.ToString();
                        writer.WritePropertyName(key);
                        Write(writer, pair.Value);
                    }
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        private static void WriteScalar(Utf8JsonWriter writer, string? value)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }
            if (bool.TryParse(value, out var flag))
            {
                writer.WriteBooleanValue(flag);
                return;
            }
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                writer.WriteNumberValue(integer);
                return;
            }
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                writer.WriteNumberValue(number);
                return;
            }
            writer.WriteStringValue(value);
        }
    }
}
