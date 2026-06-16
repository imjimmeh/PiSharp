using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Compatibility.Resources;
using PiSharp.Extensions;
using PiSharp.Runtime;

namespace PiSharp.Cli;

internal static class StartupBenchmarkFormatter
{
    public static string Render(StartupBenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Startup benchmark report");
        builder.AppendLine($"Total: {Format(report.Total)}");
        builder.AppendLine();
        builder.AppendLine("Phases:");

        foreach (var phase in report.Phases.OrderByDescending(phase => phase.Duration))
        {
            builder.AppendLine($"  {phase.Name,-28} {Format(phase.Duration)}");
        }

        if (report.NativeExtensions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Native extensions:");
            foreach (var timing in report.NativeExtensions.OrderByDescending(item => item.Total))
            {
                builder.AppendLine($"  {timing.Path}");
                builder.AppendLine($"    load: {Format(timing.LoadDuration)}  init: {Format(timing.InitializeDuration)}  total: {Format(timing.Total)}");
                if (!timing.Success && !string.IsNullOrWhiteSpace(timing.Error)) builder.AppendLine($"    error: {timing.Error}");
            }
        }

        if (report.TypeScriptExtensions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("TypeScript extensions:");
            foreach (var timing in report.TypeScriptExtensions.OrderByDescending(item => item.Total))
            {
                builder.AppendLine($"  {timing.Path}");
                builder.AppendLine($"    load: {Format(timing.LoadDuration)}  init: {Format(timing.InitializeDuration)}  total: {Format(timing.Total)}");
                AppendBridgeTimings(builder, timing);
                if (!timing.Success && !string.IsNullOrWhiteSpace(timing.Error)) builder.AppendLine($"    error: {timing.Error}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendBridgeTimings(StringBuilder builder, StartupBenchmarkExtensionTiming timing)
    {
        var bridge = timing.BridgeTimings;
        if (bridge is null) return;
        builder.AppendLine(
            $"    bridge: cache: {FormatMilliseconds(bridge.CacheLookup)}  compiler: {FormatMilliseconds(bridge.CompilerLoad)}  " +
            $"transpile: {FormatMilliseconds(bridge.Transpile)}  deps: {FormatMilliseconds(bridge.DependencyTranspile)}");
        builder.AppendLine(
            $"            import: {FormatMilliseconds(bridge.ModuleImport)}  activation: {FormatMilliseconds(bridge.Activation)}  " +
            $"registrations: {FormatMilliseconds(bridge.RegistrationFlush)}  total: {FormatMilliseconds(bridge.Total)}");
        builder.AppendLine(
            $"            cache hits: {bridge.CacheHits}  misses: {bridge.CacheMisses}  fallbacks: {bridge.CacheFallbacks}");
    }

    private static string Format(TimeSpan duration)
        => $"{duration.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} ms";

    private static string FormatMilliseconds(double milliseconds)
        => $"{milliseconds.ToString("F2", CultureInfo.InvariantCulture)} ms";
}

public static class StartupResourceSummary
{
    public static IReadOnlyList<string> Create(SessionRuntime runtime)
        => runtime.Resources is null ? [] : Create(runtime.Resources, runtime.ExtensionManager);

    public static IReadOnlyList<string> Create(PiResources resources, ExtensionManager? extensionManager = null)
    {
        var lines = new List<string>();
        AddLine(lines, "Loaded extensions", ExtensionNames(resources, extensionManager));
        AddLine(lines, "Loaded skills", SkillNames(resources.SkillPaths));
        AddLine(lines, "Loaded prompt templates", ResourceNames(resources.PromptTemplatePaths));
        AddLine(lines, "Loaded themes", ResourceNames(resources.ThemePaths));
        AddLine(lines, "Loaded packages", resources.Packages.Select(PackageDisplayName));

        if (lines.Count == 0) lines.Add("No packages or resources loaded.");

        if (resources.Diagnostics.Count > 0)
        {
            lines.Add($"Resource warnings: {resources.Diagnostics.Count}");
        }

        return [string.Join(Environment.NewLine, lines)];
    }

    private static IEnumerable<string> ExtensionNames(PiResources resources, ExtensionManager? extensionManager)
    {
        var names = new List<string>();
        if (extensionManager is not null)
        {
            names.AddRange(extensionManager.Loaded.Select(extension => extension.Descriptor.Name));
        }

        names.AddRange(resources.ExtensionPaths
            .Where(path => !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => PackageNameForPath(resources.Packages, path) ?? ResourceName(path)));
        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? PackageNameForPath(IEnumerable<PiResolvedPackage> packages, string resourcePath)
    {
        var fullResourcePath = Path.GetFullPath(resourcePath);
        var package = packages
            .Select(package => new { Package = package, Root = Path.GetFullPath(package.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) })
            .Where(entry => fullResourcePath.StartsWith(entry.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(fullResourcePath, entry.Root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Root.Length)
            .FirstOrDefault()?.Package;
        if (package is null) return null;
        return ReadPackageName(package.RootPath) ?? package.Reference;
    }

    private static string? ReadPackageName(string packageRoot)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
            return root?["name"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SkillNames(IEnumerable<string> paths)
        => paths.SelectMany(SkillNamesForPath).Where(name => !string.IsNullOrWhiteSpace(name));

    private static IEnumerable<string> SkillNamesForPath(string path)
    {
        if (File.Exists(path)) return [SkillNameFromFile(path)];
        if (!Directory.Exists(path)) return [SkillNameFromFile(path)];

        var files = Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories)
            .Where(file => string.Equals(Path.GetFileName(file), "SKILL.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(file)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return files.Length == 0 ? [ResourceName(path)] : files.Select(SkillNameFromFile);
    }

    private static string SkillNameFromFile(string path)
    {
        var frontmatterName = ReadFrontmatterName(path);
        if (!string.IsNullOrWhiteSpace(frontmatterName)) return frontmatterName!;
        return string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? path)
            : Path.GetFileNameWithoutExtension(path);
    }

    private static string? ReadFrontmatterName(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            if (!string.Equals(reader.ReadLine(), "---", StringComparison.Ordinal)) return null;
            for (var line = reader.ReadLine(); line is not null && !string.Equals(line, "---", StringComparison.Ordinal); line = reader.ReadLine())
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase)) continue;
                return trimmed[5..].Trim().Trim('"', '\'');
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static IEnumerable<string> ResourceNames(IEnumerable<string> paths)
        => paths.Select(ResourceName).Where(name => !string.IsNullOrWhiteSpace(name));

    private static string ResourceName(string path)
    {
        var full = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (string.IsNullOrWhiteSpace(name)) return full;
        return string.Equals(Path.GetExtension(name), ".ts", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
    }

    private static void AddLine(List<string> lines, string label, IEnumerable<string> values)
    {
        var formatted = FormatList(values);
        if (formatted is not null) lines.Add($"{label}: {formatted}");
    }

    private static string PackageDisplayName(PiResolvedPackage package)
        => ReadPackageName(package.RootPath) ?? package.Reference;

    private static string? FormatList(IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return items.Length == 0 ? null : string.Join(", ", items);
    }
}
