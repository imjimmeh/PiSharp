using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Compatibility.Settings;

namespace PiSharp.Compatibility.Resources;

public sealed record PiResourceLoadRequest(
    PiSettingsSnapshot Settings,
    string Cwd,
    IReadOnlyList<string> CliExtensions,
    IReadOnlyList<string> CliSkills,
    IReadOnlyList<string> CliPromptTemplates,
    IReadOnlyList<string> CliThemes,
    bool NoExtensions,
    bool NoSkills,
    bool NoPromptTemplates,
    bool NoThemes,
    bool NoContextFiles,
    bool NoTsExtensions = false);

public sealed record PiResourceDiagnostic(string Type, string Code, string Message, string Path);

public sealed record PiResourceContextFile(string Path, string Content);

public sealed record PiResources(
    IReadOnlyList<string> ExtensionPaths,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptTemplatePaths,
    IReadOnlyList<string> ThemePaths,
    IReadOnlyList<string> ContextFilePaths,
    IReadOnlyList<string> SystemPromptPaths,
    IReadOnlyList<PiResolvedPackage> Packages,
    IReadOnlyList<PiResourceDiagnostic> Diagnostics,
    string? SystemPrompt = null,
    IReadOnlyList<string>? AppendSystemPrompts = null,
    IReadOnlyList<PiResourceContextFile>? ContextFiles = null);

public sealed class PiResourceLoader(PiPackageResolver? packageResolver = null)
{
    private static readonly string[] ContextFileNames = ["AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];
    private readonly PiPackageResolver _packageResolver = packageResolver ?? new PiPackageResolver();

    public async Task<PiResources> LoadAsync(PiResourceLoadRequest request, CancellationToken cancellationToken = default)
    {
        var settings = request.Settings.Settings;
        var packageResult = await _packageResolver.ResolveAsync(settings.Packages, request.Cwd, request.Settings.Paths.GlobalAgentDirectory, cancellationToken);
        var diagnostics = packageResult.Diagnostics
            .Select(diagnostic => new PiResourceDiagnostic(diagnostic.Type, diagnostic.Code, diagnostic.Message, diagnostic.Reference))
            .ToList();

        var packageRoots = packageResult.Packages.Select(package => package.RootPath).ToArray();
        var extensions = request.NoExtensions || settings.NoExtensions == true
            ? []
            : ResolveConfigured("extension", request.Cwd, diagnostics,
                DiscoverDefaultExtensionPaths(request.Cwd, request.Settings.Paths.GlobalAgentDirectory)
                    .Concat(settings.Extensions)
                    .Concat(request.CliExtensions)
                    .Concat(PackageResources(packageRoots, "extensions", "extensions")));
        if (request.NoTsExtensions)
        {
            extensions = extensions.Where(path =>
                !path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        var skillsDisabled = request.NoSkills || settings.NoSkills == true;
        var skills = ResolveConfigured("skill", request.Cwd, diagnostics,
            (skillsDisabled
                ? []
                : DiscoverDefaultSkillPaths(request.Cwd, request.Settings.Paths.HomeDirectory, request.Settings.Paths.GlobalAgentDirectory)
                    .Concat(settings.Skills)
                    .Concat(PackageResources(packageRoots, "skills", "skills")))
            .Concat(request.CliSkills));
        var promptTemplates = request.NoPromptTemplates || settings.NoPromptTemplates == true ? [] : ResolveConfigured("prompt-template", request.Cwd, diagnostics, settings.PromptTemplates.Concat(request.CliPromptTemplates).Concat(PackageResources(packageRoots, "prompts", "prompts")).Concat(PackageResources(packageRoots, "promptTemplates", "prompt-templates")));
        var themes = request.NoThemes || settings.NoThemes == true ? [] : ResolveConfigured("theme", request.Cwd, diagnostics, settings.Themes.Concat(request.CliThemes).Concat(PackageResources(packageRoots, "themes", "themes")));
        var packageContext = request.NoContextFiles || settings.NoContextFiles == true ? [] : ResolveConfigured("context", request.Cwd, diagnostics, PackageResources(packageRoots, "context", "context"));
        var systemPrompts = ResolveConfigured("system-prompt", request.Cwd, diagnostics, PackageResources(packageRoots, "systemPrompts", "system-prompts"));

        var discoveredSystemPromptPath = ResolvePreferredExistingFile(
            Path.Combine(request.Cwd, ".pi", "SYSTEM.md"),
            Path.Combine(request.Settings.Paths.GlobalAgentDirectory, "SYSTEM.md"));
        var discoveredAppendPromptPath = ResolvePreferredExistingFile(
            Path.Combine(request.Cwd, ".pi", "APPEND_SYSTEM.md"),
            Path.Combine(request.Settings.Paths.GlobalAgentDirectory, "APPEND_SYSTEM.md"));

        var systemPromptPaths = AddDistinct(systemPrompts, discoveredSystemPromptPath is null ? [] : [discoveredSystemPromptPath]);
        var systemPrompt = discoveredSystemPromptPath is null ? null : await ReadOptionalTextAsync("system-prompt", discoveredSystemPromptPath, diagnostics, cancellationToken);
        var appendSystemPrompts = discoveredAppendPromptPath is null
            ? []
            : new[] { await ReadOptionalTextAsync("append-system-prompt", discoveredAppendPromptPath, diagnostics, cancellationToken) }.Where(prompt => prompt is not null).Select(prompt => prompt!).ToArray();

        var contextFilePaths = request.NoContextFiles || settings.NoContextFiles == true
            ? []
            : AddDistinct(packageContext, DiscoverContextFiles(request.Cwd, request.Settings.Paths.GlobalAgentDirectory));
        var contextFiles = request.NoContextFiles || settings.NoContextFiles == true
            ? []
            : (await LoadContextFilesAsync(contextFilePaths, diagnostics, cancellationToken)).ToArray();

        return new PiResources(extensions, skills, promptTemplates, themes, contextFilePaths, systemPromptPaths, packageResult.Packages, diagnostics, systemPrompt, appendSystemPrompts, contextFiles);
    }

    private static IReadOnlyList<string> ResolveConfigured(string type, string cwd, List<PiResourceDiagnostic> diagnostics, IEnumerable<string> paths)
    {
        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var full = Path.GetFullPath(path, cwd);
            if (!Directory.Exists(full) && !File.Exists(full))
            {
                diagnostics.Add(new PiResourceDiagnostic(type, "missing", $"Configured {type} path '{path}' does not exist.", full));
                continue;
            }
            if (seen.Add(full)) resolved.Add(full);
        }
        return resolved;
    }

    private static IReadOnlyList<string> DiscoverDefaultSkillPaths(string cwd, string homeDirectory, string globalAgentDirectory)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExistingDirectory(Path.Combine(globalAgentDirectory, "skills"), paths, seen);
        AddExistingDirectory(Path.Combine(homeDirectory, ".agents", "skills"), paths, seen);
        foreach (var directory in AncestorsRootFirst(cwd))
        {
            AddExistingDirectory(Path.Combine(directory, ".agents", "skills"), paths, seen);
            AddExistingDirectory(Path.Combine(directory, ".pi", "skills"), paths, seen);
        }
        return paths;
    }

    private static IReadOnlyList<string> DiscoverDefaultExtensionPaths(string cwd, string globalAgentDirectory)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExtensionEntries(Path.Combine(globalAgentDirectory, "extensions"), paths, seen);
        AddExtensionEntries(Path.Combine(cwd, ".pi", "extensions"), paths, seen);
        return paths;
    }

    private static void AddExtensionEntries(string directory, List<string> paths, HashSet<string> seen)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var pattern in new[] { "*.ts", "*.js" })
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(file);
                if (seen.Add(full)) paths.Add(full);
            }
        }
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var indexName in new[] { "index.ts", "index.js" })
            {
                var index = Path.GetFullPath(Path.Combine(child, indexName));
                if (File.Exists(index) && seen.Add(index)) paths.Add(index);
            }
        }
    }

    private static void AddExistingDirectory(string directory, List<string> paths, HashSet<string> seen)
    {
        var full = Path.GetFullPath(directory);
        if (Directory.Exists(full) && seen.Add(full)) paths.Add(full);
    }

    private static IReadOnlyList<string> DiscoverContextFiles(string cwd, string globalAgentDirectory)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddContextCandidates(globalAgentDirectory, paths, seen);
        foreach (var directory in AncestorsRootFirst(cwd)) AddContextCandidates(directory, paths, seen);
        return paths;
    }

    private static void AddContextCandidates(string directory, List<string> paths, HashSet<string> seen)
    {
        foreach (var name in ContextFileNames)
        {
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (File.Exists(path) && seen.Add(path)) paths.Add(path);
        }
    }

    private static IEnumerable<string> AncestorsRootFirst(string cwd)
    {
        var directories = new List<string>();
        var current = Path.GetFullPath(cwd);
        while (!string.IsNullOrWhiteSpace(current))
        {
            directories.Add(current);
            var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        directories.Reverse();
        return directories;
    }

    private static async Task<IReadOnlyList<PiResourceContextFile>> LoadContextFilesAsync(IReadOnlyList<string> paths, List<PiResourceDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var files = new List<PiResourceContextFile>();
        foreach (var path in paths)
        {
            var content = await ReadOptionalTextAsync("context", path, diagnostics, cancellationToken);
            if (content is not null) files.Add(new PiResourceContextFile(path, content));
        }
        return files;
    }

    private static async Task<string?> ReadOptionalTextAsync(string type, string path, List<PiResourceDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        try { return await File.ReadAllTextAsync(path, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new PiResourceDiagnostic(type, "read_failed", exception.Message, path));
            return null;
        }
    }

    private static string? ResolvePreferredExistingFile(string projectPath, string globalPath)
    {
        var project = Path.GetFullPath(projectPath);
        if (File.Exists(project)) return project;
        var global = Path.GetFullPath(globalPath);
        return File.Exists(global) ? global : null;
    }

    private static IReadOnlyList<string> AddDistinct(IEnumerable<string> first, IEnumerable<string> second)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in first.Concat(second)) if (seen.Add(path)) paths.Add(path);
        return paths;
    }

    private static IEnumerable<string> PackageResources(IEnumerable<string> packageRoots, string manifestKey, string fallbackChild)
    {
        foreach (var root in packageRoots)
        {
            var manifestEntries = ReadManifestEntries(root, manifestKey);
            if (manifestEntries is not null)
            {
                foreach (var entry in manifestEntries.SelectMany(entry => ExpandManifestEntry(root, entry))) yield return entry;
                continue;
            }

            var conventional = Path.Combine(root, fallbackChild);
            if (Directory.Exists(conventional)) yield return conventional;
        }
    }

    private static IReadOnlyList<string>? ReadManifestEntries(string packageRoot, string manifestKey)
    {
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(packageJsonPath)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
            if (root?["pi"] is not JsonObject pi || pi[manifestKey] is not JsonArray array) return null;
            return array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ExpandManifestEntry(string packageRoot, string entry)
    {
        if (!HasGlob(entry)) return [Path.GetFullPath(entry, packageRoot)];

        var normalizedPattern = NormalizeRelative(entry);
        if (!Directory.Exists(packageRoot)) return [];
        return Directory.EnumerateFileSystemEntries(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => GlobMatch(NormalizeRelative(Path.GetRelativePath(packageRoot, path)), normalizedPattern))
            .Select(path => Path.GetFullPath(path));
    }

    private static bool HasGlob(string entry) => entry.IndexOfAny(['*', '?', '[']) >= 0;

    private static string NormalizeRelative(string path)
        => path.Replace('\\', '/').TrimStart('.', '/');

    private static bool GlobMatch(string path, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
