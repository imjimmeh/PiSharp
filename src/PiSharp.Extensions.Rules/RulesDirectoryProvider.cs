namespace PiSharp.Extensions.Rules;

/// <summary>
/// Built-in rule provider (P10 plan §5.5): walks rule roots (<c>rules/*.md</c> /
/// <c>rules/*.mdc</c> and single <c>rules.md</c> files) and parses optional frontmatter
/// into <see cref="Rule"/> records. Roots are scanned in order — project-local roots are
/// passed nearest-first — and a rule <see cref="Rule.Name"/> collision resolves within the
/// provider by nearest-root-wins (first scan hit), matching the context-file ancestor walk.
/// Files with neither <c>always: true</c> nor a <c>pattern</c> are skipped and reported
/// through <see cref="OnWarning"/> when provided.
/// </summary>
public sealed class RulesDirectoryProvider : IRuleProvider
{
    public const string ProviderName = "rules-dir";
    public const int ProviderPriority = 100;

    private readonly IReadOnlyList<string> _ruleRoots;
    private readonly IReadOnlyList<string> _singleFileCandidates;

    /// <summary>Receives a discovery warning per malformed/skipped rule file, if non-null.</summary>
    public Action<string>? OnWarning { get; }

    public RulesDirectoryProvider(
        IReadOnlyList<string> ruleRoots,
        IReadOnlyList<string>? singleFileCandidates = null,
        Action<string>? onWarning = null)
    {
        _ruleRoots = ruleRoots ?? throw new ArgumentNullException(nameof(ruleRoots));
        _singleFileCandidates = singleFileCandidates ?? [];
        OnWarning = onWarning;
    }

    public string Name => ProviderName;

    public int Priority => ProviderPriority;

    public Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<string>(_singleFileCandidates);
        foreach (var root in _ruleRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in EnumerateMarkdownFiles(root))
                files.Add(file);
        }

        var rules = new List<Rule>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(file);
            if (!seenPaths.Add(fullPath)) continue;

            string fileText;
            try
            {
                fileText = File.ReadAllText(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                OnWarning?.Invoke($"Skipping unreadable rule file '{fullPath}': {exception.Message}");
                continue;
            }

            var parsed = RuleFrontmatter.Parse(fileText, fullPath);
            if (parsed.Always is false && string.IsNullOrWhiteSpace(parsed.Pattern))
            {
                OnWarning?.Invoke($"Skipping rule file '{fullPath}': neither 'always: true' nor a 'pattern' is set.");
                continue;
            }

            if (!seenNames.Add(parsed.Name))
            {
                OnWarning?.Invoke($"Skipping duplicate rule name '{parsed.Name}' from '{fullPath}' (nearest root wins).");
                continue;
            }

            rules.Add(new Rule(
                Name: parsed.Name,
                Content: parsed.Content,
                Path: fullPath,
                Priority: parsed.Priority,
                ApplyMode: parsed.Always ? RuleApplyMode.Always : RuleApplyMode.StreamTrigger,
                TriggerPattern: parsed.Always ? null : parsed.Pattern));
        }

        return Task.FromResult<IReadOnlyList<Rule>>(rules);
    }

    private static IEnumerable<string> EnumerateMarkdownFiles(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mdc", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }
}
