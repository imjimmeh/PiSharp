using System.Text.RegularExpressions;
using PiSharp.Abstractions.Environment;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PiSharp.Agent.Resources;

public sealed record PromptTemplateRegistration(string Name, string? Description, string Content, string SourcePath)
{
    public PromptTemplate ToTemplate() => new(Name, Description, Content);
}

public sealed class PromptTemplateCatalog
{
    private readonly Dictionary<string, PromptTemplateRegistration> _templates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PromptTemplateRegistration> Templates
        => _templates.Values.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static PromptTemplateCatalog Empty { get; } = new();

    public bool TryGet(string name, out PromptTemplateRegistration template)
        => _templates.TryGetValue(NormalizeName(name), out template!);

    public void Add(PromptTemplateRegistration template)
    {
        if (!string.IsNullOrWhiteSpace(template.Name))
            _templates[NormalizeName(template.Name)] = template with { Name = NormalizeName(template.Name) };
    }

    public string FormatInvocation(string name, IReadOnlyList<string>? args = null)
        => TryGet(name, out var template)
            ? PromptTemplateEngine.FormatInvocation(template.ToTemplate(), args ?? [])
            : throw new InvalidOperationException($"Prompt template '{name}' was not loaded.");

    public static async Task<(PromptTemplateCatalog Catalog, IReadOnlyList<PromptTemplateDiagnostic> Diagnostics)> LoadAsync(
        IExecutionEnv env,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var catalog = new PromptTemplateCatalog();
        var diagnostics = new List<PromptTemplateDiagnostic>();
        var fileLoadTasks = new List<Task<(PromptTemplateRegistration? Template, PromptTemplateDiagnostic? Diagnostic)>>();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var info = await env.GetFileInfoAsync(path, cancellationToken);
            if (info.IsErr)
            {
                diagnostics.Add(new("warning", "stat_failed", info.Error.Message, path));
                continue;
            }

            if (info.Value.Kind == FileKind.File)
            {
                fileLoadTasks.Add(LoadFileAsync(env, info.Value.Path, cancellationToken));
                continue;
            }

            var (templates, loadDiagnostics) = await PromptTemplateEngine.LoadAsync(env, info.Value.Path, cancellationToken);
            diagnostics.AddRange(loadDiagnostics);
            foreach (var template in templates)
                catalog.Add(new PromptTemplateRegistration(template.Name, template.Description, template.Content, info.Value.Path));
        }

        var fileResults = await Task.WhenAll(fileLoadTasks);
        foreach (var (template, diagnostic) in fileResults)
        {
            if (template is not null) catalog.Add(template);
            if (diagnostic is not null) diagnostics.Add(diagnostic);
        }

        return (catalog, diagnostics);
    }

    private static async Task<(PromptTemplateRegistration? Template, PromptTemplateDiagnostic? Diagnostic)> LoadFileAsync(
        IExecutionEnv env,
        string path,
        CancellationToken cancellationToken)
    {
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return (null, new PromptTemplateDiagnostic("warning", "unsupported_file", "Prompt template files must be markdown.", path));

        var read = await env.ReadTextFileAsync(path, cancellationToken);
        if (read.IsErr) return (null, new PromptTemplateDiagnostic("warning", "read_failed", read.Error.Message, path));

        var (frontmatter, body) = ParseFrontmatter(read.Value);
        var description = frontmatter.TryGetValue("description", out var value) ? value?.ToString() : null;
        return (new PromptTemplateRegistration(Path.GetFileNameWithoutExtension(path), description, body, path), null);
    }

    private static (Dictionary<string, object?> Frontmatter, string Body) ParseFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---", StringComparison.Ordinal)) return ([], normalized);
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end == -1) return ([], normalized);
        try
        {
            var deserializer = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
            return (deserializer.Deserialize<Dictionary<string, object?>>(normalized[4..end]) ?? [], normalized[(end + 4)..].TrimStart());
        }
        catch
        {
            return ([], normalized[(end + 4)..].TrimStart());
        }
    }

    private static string NormalizeName(string name) => Regex.Replace(name.Trim().TrimStart('/'), @"\s+", "-");
}
