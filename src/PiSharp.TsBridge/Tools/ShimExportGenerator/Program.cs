using System.Text.RegularExpressions;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ShimExportGenerator --input <json> --classification <cs> --runtime-actions <cs> --out-dir <dir>");
    return 1;
}

var inputPath = Arg(args, "--input");
var classificationPath = Arg(args, "--classification");
var runtimeActionsPath = Arg(args, "--runtime-actions");
var outDir = Arg(args, "--out-dir");

if (inputPath is null || outDir is null)
{
    Console.Error.WriteLine("Missing required argument. Usage: --input <json> --out-dir <dir> [--classification <cs>] [--runtime-actions <cs>]");
    return 1;
}

var inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
classificationPath ??= Path.GetFullPath(Path.Combine(inputDir, "SdkShimExportClassification.cs"));
runtimeActionsPath ??= Path.GetFullPath(Path.Combine(inputDir, "..", "TsBridgeManifestFactory.cs"));

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

if (!File.Exists(classificationPath))
{
    Console.Error.WriteLine($"Classification file not found: {classificationPath}");
    return 1;
}

if (!File.Exists(runtimeActionsPath))
{
    Console.Error.WriteLine($"Runtime actions file not found: {runtimeActionsPath}");
    return 1;
}

var exports = ParseInputJson(inputPath);
var classification = ParseClassification(classificationPath);
var runtimeConstants = ParseRuntimeActions(runtimeActionsPath);

foreach (var export in exports)
{
    if (!classification.TryGetValue(export.Name, out _))
    {
        Console.Error.WriteLine($"Unclassified SDK export '{export.Name}'. Add an entry to SdkShimExportClassification.All.");
        return 1;
    }
}

Directory.CreateDirectory(outDir);

var shimExportsPath = Path.Combine(outDir, "ShimExports.Auto.g.cs");
var runtimeActionsOutPath = Path.Combine(outDir, "SdkShimRuntimeActions.Auto.g.cs");

File.WriteAllText(shimExportsPath, GenerateShimExports(exports, classification, runtimeConstants));
File.WriteAllText(runtimeActionsOutPath, GenerateRuntimeActions(exports, classification, runtimeConstants));

Console.WriteLine($"Generated {shimExportsPath}");
Console.WriteLine($"Generated {runtimeActionsOutPath}");
return 0;

static string? Arg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static List<ExportEntry> ParseInputJson(string path)
{
    var json = File.ReadAllText(path);
    var match = Regex.Match(json, @"""exports""\s*:\s*\[(.*)\]", RegexOptions.Singleline);
    if (!match.Success)
        throw new InvalidOperationException("Input JSON must contain an 'exports' array.");

    var arrayContent = match.Groups[1].Value;
    var entries = new List<ExportEntry>();

    var entryRegex = new Regex(
        @"\{\s*""name""\s*:\s*""(?<name>[^""]+)""\s*,\s*""kind""\s*:\s*""(?<kind>[^""]+)""\s*,\s*""sourceModule""\s*:\s*""(?<sourceModule>[^""]+)""\s*\}",
        RegexOptions.Compiled);

    foreach (Match m in entryRegex.Matches(arrayContent))
    {
        entries.Add(new ExportEntry(
            m.Groups["name"].Value,
            m.Groups["kind"].Value,
            m.Groups["sourceModule"].Value));
    }

    return entries;
}

static Dictionary<string, ClassifiedExport> ParseClassification(string path)
{
    var source = File.ReadAllText(path);
    var result = new Dictionary<string, ClassifiedExport>(StringComparer.Ordinal);

    var regex = new Regex(
        @"\[\s*""(?<name>[^""]+)""\s*\]\s*=\s*new\s*\(\s*SdkShimExportStatus\.(?<status>\w+)\s*,\s*TsBridgeShimExportKinds\.(?<kind>\w+)\s*"
        + @"(?:,\s*Helper:\s*""(?<helper>[^""]*)""|"
        + @",\s*Value:\s*(?<value>[^,)]+)|"
        + @",\s*RuntimeAction:\s*TsBridgeRuntimeActions\.(?<runtimeAction>\w+)|"
        + @",\s*Message:\s*""(?<message>[^""]*)"")*\s*\),",
        RegexOptions.Compiled);

    foreach (Match m in regex.Matches(source))
    {
        var exportKind = m.Groups["kind"].Value switch
        {
            "JsonConst" => "json-const",
            "Helper" => "helper",
            "UnavailableFunction" => "unavailable-function",
            "AsyncUnavailableFunction" => "async-unavailable-function",
            "RuntimeFunction" => "runtime-function",
            "Namespace" => "namespace",
            _ => "helper"
        };

        var helper = m.Groups["helper"].Success ? m.Groups["helper"].Value : null;
        var runtimeActionConstant = m.Groups["runtimeAction"].Success ? m.Groups["runtimeAction"].Value : null;
        var message = m.Groups["message"].Success ? m.Groups["message"].Value : null;
        var status = m.Groups["status"].Value;
        var hasValue = m.Groups["value"].Success;
        var rawValue = hasValue ? m.Groups["value"].Value.Trim() : null;

        result[m.Groups["name"].Value] = new ClassifiedExport(
            status,
            exportKind,
            helper,
            rawValue,
            runtimeActionConstant,
            message);
    }

    return result;
}

static Dictionary<string, string> ParseRuntimeActions(string path)
{
    var source = File.ReadAllText(path);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);

    var regex = new Regex(
        @"public\s+const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]+)""\s*;",
        RegexOptions.Compiled);

    foreach (Match m in regex.Matches(source))
    {
        result[$"TsBridgeRuntimeActions.{m.Groups["name"].Value}"] = m.Groups["value"].Value;
    }

    return result;
}

static string GenerateShimExports(
    List<ExportEntry> exports,
    Dictionary<string, ClassifiedExport> classification,
    Dictionary<string, string> runtimeConstants)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine("using PiSharp.TsBridge.Protocol;");
    sb.AppendLine();
    sb.AppendLine("namespace PiSharp.TsBridge.Shims;");
    sb.AppendLine();
    sb.AppendLine("public static partial class ShimExports");
    sb.AppendLine("{");
    sb.AppendLine("    public static partial class Auto");
    sb.AppendLine("    {");
    sb.AppendLine("        public static IReadOnlyList<TsBridgeShimExport> PiCodingAgentExports()");
    sb.AppendLine("            => new TsBridgeShimExport[]");
    sb.AppendLine("            {");

    for (var i = 0; i < exports.Count; i++)
    {
        var export = exports[i];
        var classified = classification[export.Name];
        var comma = i < exports.Count - 1 ? "," : "";

        sb.Append("                new(");
        sb.Append($"\"{EscapeCSharpString(export.Name)}\", ");
        sb.Append($"\"{EscapeCSharpString(classified.ExportKind)}\"");

        if (classified.Helper is not null)
            sb.Append($", Helper: \"{EscapeCSharpString(classified.Helper)}\"");

        if (classified.Status == "Unsupported" && classified.Message is not null)
            sb.Append($", Message: \"{EscapeCSharpString(classified.Message)}\"");

        if (classified.RawValue is not null)
            sb.Append($", Value: {classified.RawValue}");

        if (classified.RuntimeActionConstant is not null)
        {
            var sdkAction = $"sdk.{export.Name}";
            sb.Append($", RuntimeAction: \"{EscapeCSharpString(sdkAction)}\"");
        }

        sb.AppendLine($"){comma}");
    }

    sb.AppendLine("            };");
    sb.AppendLine("    }");
    sb.AppendLine("}");

    return sb.ToString();
}

static string GenerateRuntimeActions(
    List<ExportEntry> exports,
    Dictionary<string, ClassifiedExport> classification,
    Dictionary<string, string> runtimeConstants)
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine();
    sb.AppendLine("namespace PiSharp.TsBridge.Shims;");
    sb.AppendLine();
    sb.AppendLine("public static partial class SdkShimRuntimeActions");
    sb.AppendLine("{");

    foreach (var export in exports)
    {
        if (!classification.TryGetValue(export.Name, out var classified))
            continue;

        if (classified.RuntimeActionConstant is not null)
        {
            var constName = ToPascalCase(export.Name);
            sb.AppendLine($"    public const string {constName} = \"sdk.{EscapeCSharpString(export.Name)}\";");
        }
    }

    sb.AppendLine("}");

    return sb.ToString();
}

static string ToPascalCase(string camelCase)
{
    if (string.IsNullOrEmpty(camelCase))
        return camelCase;
    return char.ToUpperInvariant(camelCase[0]) + camelCase.Substring(1);
}

static string EscapeCSharpString(string value)
{
    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r")
        .Replace("\t", "\\t");
}

internal sealed record ExportEntry(string Name, string Kind, string SourceModule);

internal sealed record ClassifiedExport(
    string Status,
    string ExportKind,
    string? Helper,
    string? RawValue,
    string? RuntimeActionConstant,
    string? Message);
