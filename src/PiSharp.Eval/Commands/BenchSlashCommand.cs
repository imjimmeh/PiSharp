using PiSharp.Eval.Bench;

namespace PiSharp.Eval.Commands;

/// <summary>
/// The <c>/bench</c> slash command. Grammar: <c>/bench [spec-path] [--runs N]</c>. Without a
/// spec path, the default spec is <c>&lt;benchDir&gt;/bench.json</c> (or the single spec file
/// found in <c>&lt;benchDir&gt;</c>). Returns the human-readable summary text.
/// </summary>
public sealed class BenchSlashCommand
{
    private readonly BenchRunner _runner;
    private readonly string _benchDir;

    public BenchSlashCommand(BenchRunner runner, string benchDir)
    {
        _runner = runner;
        _benchDir = benchDir;
    }

    public async Task<string> HandleAsync(string args, CancellationToken cancellationToken = default)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var specPath = ResolveSpecPath(tokens);
        if (specPath is null)
        {
            var (path, error) = ResolveDefaultSpec();
            if (error is not null) return error;
            specPath = path;
        }

        if (!File.Exists(specPath))
            return $"Bench spec not found: {specPath}";

        BenchSpec spec;
        try
        {
            spec = BenchSpecParser.Parse(await File.ReadAllTextAsync(specPath, cancellationToken));
        }
        catch (BenchSpecException ex)
        {
            return $"Invalid bench spec {specPath}: {ex.Message}";
        }

        try
        {
            var summary = await _runner.RunAsync(spec, ParseRunsOverride(tokens),
                baseDir: Path.GetDirectoryName(Path.GetFullPath(specPath)), ct: cancellationToken);
            return $"Bench run finished.\n\n{_runner.FormatSummary(summary)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Bench run failed: {ex.Message}";
        }
    }

    private int? ParseRunsOverride(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == "--runs" && int.TryParse(tokens[i + 1], out var runs) && runs >= 1)
                return runs;
        }
        return null;
    }

    private string? ResolveSpecPath(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "--runs") { i++; continue; }
            if (tokens[i].StartsWith("--")) continue;
            return Path.IsPathRooted(tokens[i]) ? tokens[i] : Path.GetFullPath(tokens[i]);
        }
        return null;
    }

    private (string? Path, string? Error) ResolveDefaultSpec()
    {
        var defaultPath = Path.Combine(_benchDir, "bench.json");
        if (File.Exists(defaultPath)) return (defaultPath, null);
        if (Directory.Exists(_benchDir))
        {
            var specs = Directory.GetFiles(_benchDir, "*.json");
            if (specs.Length == 1) return (specs[0], null);
            if (specs.Length > 1)
                return (null, $"Multiple spec files found in {_benchDir}; pass one explicitly: {string.Join(", ", specs.Select(Path.GetFileName))}");
        }
        return (defaultPath, null);
    }
}
