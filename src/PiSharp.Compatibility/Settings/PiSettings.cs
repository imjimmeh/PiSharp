using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PiSharp.Compatibility.Tests")]

namespace PiSharp.Compatibility.Settings;

public sealed record PiSettings(
    string? DefaultProvider,
    string? DefaultModel,
    string? DefaultThinking,
    string? SessionDir,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> PromptTemplates,
    IReadOnlyList<string> Themes,
    IReadOnlyList<string> Packages,
    bool? NoExtensions,
    bool? NoSkills,
    bool? NoPromptTemplates,
    bool? NoThemes,
    bool? NoContextFiles,
    bool? Offline)
{
    internal static PiSettings FromConfiguration(IConfiguration configuration, PiSettings? arraySource = null)
        => new(
            ReadConfigString(configuration, "defaultProvider"),
            ReadConfigString(configuration, "defaultModel"),
            ReadConfigString(configuration, "defaultThinking"),
            ReadConfigString(configuration, "sessionDir"),
            arraySource?.Extensions ?? ReadConfigStringArray(configuration, "extensions"),
            arraySource?.Skills ?? ReadConfigStringArray(configuration, "skills"),
            arraySource?.PromptTemplates ?? ReadConfigStringArray(configuration, "promptTemplates"),
            arraySource?.Themes ?? ReadConfigStringArray(configuration, "themes"),
            arraySource?.Packages ?? ReadConfigStringArray(configuration, "packages"),
            ReadConfigBool(configuration, "noExtensions"),
            ReadConfigBool(configuration, "noSkills"),
            ReadConfigBool(configuration, "noPromptTemplates"),
            ReadConfigBool(configuration, "noThemes"),
            ReadConfigBool(configuration, "noContextFiles"),
            ReadConfigBool(configuration, "offline"));

    private static string? ReadConfigString(IConfiguration configuration, string name)
        => configuration[name];

    private static bool? ReadConfigBool(IConfiguration configuration, string name)
        => bool.TryParse(configuration[name], out var flag) ? flag : null;

    private static IReadOnlyList<string> ReadConfigStringArray(IConfiguration configuration, string name)
        => configuration.GetSection(name)
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
}

public enum PiSettingsLayer { GlobalLegacy, GlobalPiSharp, ProjectLegacy, ProjectPiSharp }

public sealed record PiSettingsLayerDocument(PiSettingsLayer Layer, PiSettingsDocument Document);

public sealed class PiSettingsDocument
{
    private static readonly string[] AppendableArrayKeys = ["extensions", "skills", "promptTemplates", "themes", "packages"];

    public PiSettingsDocument(JsonObject root) => Root = root;
    public JsonObject Root { get; }
    public PiSettings Settings => FromJson(Root);

    public static PiSettingsDocument Empty() => new([]);

    public static PiSettingsDocument Parse(string json)
        => string.IsNullOrWhiteSpace(json) ? Empty() : new((JsonNode.Parse(json) as JsonObject) ?? []);

    public PiSettingsDocument DeepClone() => new(Root.DeepClone().AsObject());

    public void SetString(string name, string? value)
    {
        if (value is null) Root.Remove(name);
        else Root[name] = value;
    }

    public void SetStringArray(string name, IEnumerable<string>? values)
    {
        if (values is null) Root.Remove(name);
        else Root[name] = new JsonArray(values.Select(value => JsonValue.Create(value) as JsonNode).ToArray());
    }

    public void SetBool(string name, bool? value)
    {
        if (value is null) Root.Remove(name);
        else Root[name] = value;
    }

    public string ToJson() => Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;

    internal static PiSettingsDocument Merge(PiSettingsDocument global, PiSettingsDocument project)
    {
        var merged = global.DeepClone();
        MergeObject(merged.Root, project.Root);
        return merged;
    }

    internal static PiSettingsDocument MergeMany(IEnumerable<PiSettingsLayerDocument> layers, out IReadOnlyDictionary<string, PiSettingsLayer> provenance)
    {
        var layerList = layers.ToArray();
        var merged = Empty();
        var sources = new Dictionary<string, PiSettingsLayer>(StringComparer.Ordinal);
        foreach (var layer in layerList)
        {
            RecordProvenance(layer.Document.Root, layer.Layer, sources);
            MergeObject(merged.Root, layer.Document.Root);
        }

        ApplyAppendKeys(merged.Root, layerList, sources);
        provenance = sources;
        return merged;
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonObject sourceObject && target[pair.Key] is JsonObject targetObject)
            {
                MergeObject(targetObject, sourceObject);
                continue;
            }

            target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static void RecordProvenance(JsonObject root, PiSettingsLayer layer, IDictionary<string, PiSettingsLayer> sources)
    {
        foreach (var pair in root)
        {
            if (IsPiSharpAppendContainer(pair.Key, pair.Value)) continue;
            sources[pair.Key] = layer;
        }
    }

    private static bool IsPiSharpAppendContainer(string key, JsonNode? value)
        => string.Equals(key, "pisharp", StringComparison.Ordinal) && value is JsonObject pisharp && pisharp["append"] is JsonObject;

    private static void ApplyAppendKeys(JsonObject target, IEnumerable<PiSettingsLayerDocument> layers, IDictionary<string, PiSettingsLayer> sources)
    {
        foreach (var layer in layers)
        {
            if (layer.Document.Root["pisharp"] is not JsonObject pisharp || pisharp["append"] is not JsonObject append) continue;
            foreach (var key in AppendableArrayKeys)
            {
                if (append[key] is not JsonArray values) continue;
                var merged = ReadStringArray(target, key).ToList();
                var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
                foreach (var value in values
                    .Select(item => item?.GetValue<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!))
                {
                    if (seen.Add(value)) merged.Add(value);
                }

                target[key] = new JsonArray(merged.Select(value => JsonValue.Create(value) as JsonNode).ToArray());
                sources[key] = layer.Layer;
            }
        }
    }

    private static PiSettings FromJson(JsonObject root) => new(
        ReadString(root, "defaultProvider"),
        ReadString(root, "defaultModel"),
        ReadString(root, "defaultThinking"),
        ReadString(root, "sessionDir"),
        ReadStringArray(root, "extensions"),
        ReadStringArray(root, "skills"),
        ReadStringArray(root, "promptTemplates"),
        ReadStringArray(root, "themes"),
        ReadStringArray(root, "packages"),
        ReadBool(root, "noExtensions"),
        ReadBool(root, "noSkills"),
        ReadBool(root, "noPromptTemplates"),
        ReadBool(root, "noThemes"),
        ReadBool(root, "noContextFiles"),
        ReadBool(root, "offline"));

    private static string? ReadString(JsonObject root, string name)
        => root.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? ReadBool(JsonObject root, string name)
        => root.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject root, string name)
        => root.TryGetPropertyValue(name, out var node) && node is JsonArray array
            ? array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];
}

public sealed record PiSettingsSnapshot(
    PiAgentPaths Paths,
    PiSettingsDocument Global,
    PiSettingsDocument Project,
    PiSettingsDocument Merged,
    PiSettingsDocument? GlobalPiSharp = null,
    PiSettingsDocument? ProjectPiSharp = null,
    IReadOnlyDictionary<string, PiSettingsLayer>? Provenance = null)
{
    public PiSettings? ResolvedSettings { get; init; }
    public PiSettings Settings => ResolvedSettings ?? Merged.Settings;
    public PiSettingsDocument GlobalPiSharpOrEmpty => GlobalPiSharp ?? PiSettingsDocument.Empty();
    public PiSettingsDocument ProjectPiSharpOrEmpty => ProjectPiSharp ?? PiSettingsDocument.Empty();

    public PiSettingsLayer? SourceLayerFor(string settingName)
        => Provenance is not null && Provenance.TryGetValue(settingName, out var layer) ? layer : null;
}
