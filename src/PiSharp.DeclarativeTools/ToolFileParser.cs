using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent.Core;
using PiSharp.Extensions;
using PiSharp.Tools;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PiSharp.DeclarativeTools;

/// <summary>
/// Parses <c>.md</c>, <c>.json</c>, and script tool files into validated
/// <see cref="ToolDefinition"/>s or per-file diagnostics (plan §5.1, §7).
/// YAML frontmatter keys are kebab-case; JSON document keys are camelCase;
/// both are normalized to camelCase internally.
/// </summary>
public sealed class ToolFileParser
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "name", "label", "description", "parameters", "required",
        "promptSnippet", "promptGuidelines", "executionMode", "renderer",
        "override", "timeoutSeconds", "allowNonZeroExit", "additionalProperties"
    };

    private static readonly HashSet<string> ScriptOnlyKeys = new(StringComparer.Ordinal)
    {
        "timeoutSeconds", "allowNonZeroExit"
    };

    private const string DefaultScriptDescription = "Runs the {0} script. Reads JSON arguments from stdin.";

    /// <summary>
    /// Parses a candidate tool file. Returns null with a non-null <paramref name="diagnostic"/>
    /// when the file must be skipped.
    /// </summary>
    public ToolDefinition? Parse(ToolFile file, out string? diagnostic)
    {
        diagnostic = null;
        string text;
        try
        {
            text = File.ReadAllText(file.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"Failed to read tool file: {exception.Message}";
            return null;
        }

        JsonObject metadata;
        string? body = null;
        if (file.Kind == DeclarativeToolKind.Json)
        {
            var json = ParseJsonDocument(text, out var jsonError);
            if (json is null)
            {
                diagnostic = jsonError;
                return null;
            }
            metadata = json;
        }
        else
        {
            var (frontmatter, bodyText, frontmatterError) = ParseFrontmatter(text);
            if (frontmatterError is not null)
            {
                diagnostic = frontmatterError;
                return null;
            }
            metadata = frontmatter;
            body = bodyText;
        }

        // Unknown-key rejection (typo protection) applies to both encodings.
        foreach (var key in metadata.Select(pair => pair.Key))
        {
            if (KnownKeys.Contains(key)) continue;
            if (file.Kind == DeclarativeToolKind.Script && ScriptOnlyKeys.Contains(key)) continue;
            diagnostic = $"Unknown key '{key}'.";
            return null;
        }

        var name = ReadString(metadata, "name") ?? file.DefaultName;
        if (!IsValidName(name))
        {
            diagnostic = $"Invalid tool name '{name}'; names must match [A-Za-z0-9_-]+.";
            return null;
        }

        var description = ReadString(metadata, "description");
        if (file.Kind == DeclarativeToolKind.Script)
        {
            description ??= string.Format(CultureInfo.InvariantCulture, DefaultScriptDescription, name);
        }
        else if (string.IsNullOrWhiteSpace(description))
        {
            diagnostic = "Tool file must include a non-empty description.";
            return null;
        }

        if (file.Kind == DeclarativeToolKind.Markdown && !string.IsNullOrWhiteSpace(body))
            description = description + "\n\n" + body;

        var (schema, schemaDiagnostic) = ToolSchemaBuilder.Build(
            ReadNode(metadata, "parameters"),
            ReadRequired(metadata),
            ReadBool(metadata, "additionalProperties") ?? false);
        if (schemaDiagnostic is not null)
        {
            diagnostic = schemaDiagnostic;
            return null;
        }

        var executionMode = ReadExecutionMode(metadata, out var modeDiagnostic);
        if (modeDiagnostic is not null)
        {
            diagnostic = modeDiagnostic;
            return null;
        }

        var overridePolicy = ReadOverridePolicy(metadata, out var overrideDiagnostic);
        if (overrideDiagnostic is not null)
        {
            diagnostic = overrideDiagnostic;
            return null;
        }

        var timeout = ReadTimeout(metadata, out var timeoutDiagnostic);
        if (timeoutDiagnostic is not null)
        {
            diagnostic = timeoutDiagnostic;
            return null;
        }

        return new ToolDefinition(
            Name: name,
            Label: ReadString(metadata, "label") ?? name,
            Description: description,
            ParametersSchema: schema ?? ToolSchemas.Object(new Dictionary<string, JsonElement>(), []),
            PromptSnippet: ReadString(metadata, "promptSnippet"),
            PromptGuidelines: ReadStringList(metadata, "promptGuidelines"),
            ExecutionMode: executionMode,
            RendererName: ReadString(metadata, "renderer"),
            Override: overridePolicy,
            ScriptPath: file.Kind == DeclarativeToolKind.Script ? file.Path : null,
            Timeout: timeout,
            AllowNonZeroExit: ReadBool(metadata, "allowNonZeroExit") ?? false,
            SourcePath: file.Path);
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var ch in name)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_') return false;
        }
        return true;
    }

    private static ToolExecutionMode? ReadExecutionMode(JsonObject metadata, out string? diagnostic)
    {
        diagnostic = null;
        var node = ReadNode(metadata, "executionMode");
        if (node is not { } value || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text?.ToLowerInvariant() switch
        {
            "sequential" => ToolExecutionMode.Sequential,
            "parallel" => ToolExecutionMode.Parallel,
            _ => SetDiagnostic<ToolExecutionMode?>($"Invalid execution-mode '{text}'; expected 'sequential' or 'parallel'.", out diagnostic)
        };
    }

    private static ExtensionOverridePolicy ReadOverridePolicy(JsonObject metadata, out string? diagnostic)
    {
        diagnostic = null;
        var node = ReadNode(metadata, "override");
        if (node is not { } value || value.ValueKind != JsonValueKind.String) return ExtensionOverridePolicy.Reject;
        var text = value.GetString();
        return text?.ToLowerInvariant() switch
        {
            null or "reject" => ExtensionOverridePolicy.Reject,
            "extension" => ExtensionOverridePolicy.Override,
            "builtin" => ExtensionOverridePolicy.OverrideBuiltIn,
            _ => SetDiagnostic<ExtensionOverridePolicy>($"Invalid override '{text}'; expected 'reject', 'extension', or 'builtin'.", out diagnostic)
        };
    }

    private static TimeSpan? ReadTimeout(JsonObject metadata, out string? diagnostic)
    {
        diagnostic = null;
        var node = ReadNode(metadata, "timeoutSeconds");
        if (node is not { } value) return null;
        var seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => double.NaN
        };
        if (double.IsNaN(seconds) || seconds <= 0)
            return SetDiagnostic<TimeSpan?>($"Invalid timeout-seconds '{value.GetRawText()}'; expected a positive number.", out diagnostic);
        return TimeSpan.FromSeconds(seconds);
    }

    private static IReadOnlyList<string> ReadRequired(JsonObject metadata)
    {
        var node = ReadNode(metadata, "required");
        if (node is not { } value) return [];
        if (value.ValueKind == JsonValueKind.String) return [value.GetString()!];
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringList(JsonObject metadata, string key)
    {
        var node = ReadNode(metadata, key);
        if (node is not { } value) return [];
        if (value.ValueKind == JsonValueKind.String) return [value.GetString()!];
        if (value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static string? ReadString(JsonObject metadata, string key)
        => ReadNode(metadata, key) is { } node && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static bool? ReadBool(JsonObject metadata, string key)
        => ReadNode(metadata, key) is { } node && node.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? node.GetBoolean()
            : null;

    private static JsonElement? ReadNode(JsonObject metadata, string key)
        => metadata.TryGetPropertyValue(key, out var node) && node is not null ? JsonSerializer.SerializeToElement(node) : null;

    private static T SetDiagnostic<T>(string message, out string? diagnostic)
    {
        diagnostic = message;
        return default!;
    }

    private static JsonObject? ParseJsonDocument(string text, out string? error)
    {
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            error = $"Invalid JSON: {exception.Message}";
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Tool file root must be a JSON object.";
                return null;
            }
            if (FindDuplicateKey(document.RootElement, out var duplicate))
            {
                error = $"Duplicate key '{duplicate}'.";
                return null;
            }
            // Clone: JsonObject.Create keeps a lazy reference to the document, which we dispose here.
            return JsonObject.Create(document.RootElement.Clone());
        }
    }

    private static bool FindDuplicateKey(JsonElement element, out string? duplicate)
    {
        duplicate = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    duplicate = property.Name;
                    return true;
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                if (FindDuplicateKey(property.Value, out duplicate)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindDuplicateKey(item, out duplicate)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Splits <c>---</c>-delimited YAML frontmatter (SkillManager-style) and converts
    /// the parsed map to a camelCase-keyed <see cref="JsonObject"/>. Returns an empty
    /// frontmatter when the content does not start with <c>---</c>.
    /// </summary>
    private static (JsonObject Frontmatter, string? Body, string? Error) ParseFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart();
        if (!normalized.StartsWith("---")) return (new JsonObject(), null, null);
        var endIndex = normalized.IndexOf("\n---", 3);
        if (endIndex == -1) return (new JsonObject(), null, "Frontmatter is missing a closing '---' delimiter.");
        var yaml = normalized[4..endIndex];
        var body = normalized[(endIndex + 4)..].TrimStart();
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .WithTypeConverter(new YamlScalarTypeConverter())
                .Build();
            var raw = deserializer.Deserialize<Dictionary<string, object?>>(yaml) ?? [];
            var frontmatter = new JsonObject();
            foreach (var (key, value) in raw)
            {
                if (ToJsonNode(value) is { } node) frontmatter[ToCamelCase(key)] = node;
            }
            return (frontmatter, body, null);
        }
        catch (Exception exception)
        {
            return (new JsonObject(), null, $"Invalid YAML frontmatter: {exception.Message}");
        }
    }

    private static string ToCamelCase(string key)
    {
        var builder = new System.Text.StringBuilder(key.Length);
        var uppercaseNext = false;
        foreach (var ch in key)
        {
            if (ch == '-')
            {
                uppercaseNext = true;
                continue;
            }
            builder.Append(uppercaseNext ? char.ToUpperInvariant(ch) : ch);
            uppercaseNext = false;
        }
        return builder.ToString();
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        List<object?> list => new JsonArray([.. list.Select(ToJsonNode)]),
        Dictionary<object, object?> map => ToJsonObject(map),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static JsonObject ToJsonObject(Dictionary<object, object?> map)
    {
        var json = new JsonObject();
        foreach (var (key, value) in map)
        {
            if (ToJsonNode(value) is { } node) json[Convert.ToString(key, CultureInfo.InvariantCulture)!] = node;
        }
        return json;
    }

    /// <summary>
    /// YamlDotNet deserializes scalars to <see cref="string"/> when the target type is
    /// <c>object</c>. This converter restores YAML typing for plain (unquoted) scalars —
    /// booleans, integers, floats, nulls — while quoted scalars stay strings — and
    /// handles nested mappings/sequences so typed values propagate recursively.
    /// </summary>
    private sealed class YamlScalarTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(object);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.Current is Scalar scalar)
            {
                parser.MoveNext();
                return scalar.Style == ScalarStyle.Plain ? ResolvePlainScalar(scalar.Value) : scalar.Value;
            }

            if (parser.Current is MappingStart)
            {
                parser.MoveNext();
                var map = new Dictionary<object, object?>();
                while (parser.Current is not MappingEnd)
                {
                    var key = rootDeserializer(typeof(object));
                    var value = rootDeserializer(typeof(object));
                    map[key!] = value;
                }
                parser.MoveNext();
                return map;
            }

            if (parser.Current is SequenceStart)
            {
                parser.MoveNext();
                var list = new List<object?>();
                while (parser.Current is not SequenceEnd)
                {
                    list.Add(rootDeserializer(typeof(object)));
                }
                parser.MoveNext();
                return list;
            }

            return rootDeserializer(type);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
            => throw new NotSupportedException("YamlScalarTypeConverter is read-only.");

        private static object? ResolvePlainScalar(string value) => value switch
        {
            "null" or "Null" or "NULL" or "~" => null,
            "true" or "True" or "TRUE" => true,
            "false" or "False" or "FALSE" => false,
            _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) => floating,
            _ => value
        };
    }
}
