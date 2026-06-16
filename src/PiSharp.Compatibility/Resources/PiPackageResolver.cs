namespace PiSharp.Compatibility.Resources;

public sealed record PiResolvedPackage(string Reference, string RootPath, string Source);
public sealed record PiPackageDiagnostic(string Type, string Code, string Message, string Reference);

public sealed class PiPackageResolver
{
    public Task<(IReadOnlyList<PiResolvedPackage> Packages, IReadOnlyList<PiPackageDiagnostic> Diagnostics)> ResolveAsync(
        IEnumerable<string> packageReferences,
        string cwd,
        string globalAgentDirectory,
        CancellationToken cancellationToken = default)
    {
        var packages = new List<PiResolvedPackage>();
        var diagnostics = new List<PiPackageDiagnostic>();
        foreach (var reference in packageReferences.Where(reference => !string.IsNullOrWhiteSpace(reference)).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = ResolveReference(reference, cwd, globalAgentDirectory);
            if (resolved is not null)
            {
                packages.Add(resolved);
                continue;
            }

            diagnostics.Add(new PiPackageDiagnostic("package", "missing", $"Package reference '{reference}' was not found locally, in the Pi npm install, in the Pi git cache, or in the Pi agent cache.", reference));
        }

        return Task.FromResult<(IReadOnlyList<PiResolvedPackage>, IReadOnlyList<PiPackageDiagnostic>)>((packages, diagnostics));
    }

    private static PiResolvedPackage? ResolveReference(string reference, string cwd, string globalAgentDirectory)
    {
        if (IsLocalReference(reference))
        {
            var local = Path.GetFullPath(reference, cwd);
            if (Directory.Exists(local)) return new PiResolvedPackage(reference, local, "local");
        }

        if (reference.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            var packageName = NpmPackageName(reference[4..]);
            foreach (var root in NpmRoots(globalAgentDirectory, packageName))
            {
                if (Directory.Exists(root)) return new PiResolvedPackage(reference, root, "npm");
            }
        }

        var gitRoot = GitRoot(globalAgentDirectory, reference);
        if (gitRoot is not null && Directory.Exists(gitRoot)) return new PiResolvedPackage(reference, gitRoot, "git");

        var cached = Path.Combine(globalAgentDirectory, "packages", Sanitize(reference));
        if (Directory.Exists(cached)) return new PiResolvedPackage(reference, cached, "cache");

        return null;
    }

    private static bool IsLocalReference(string reference)
        => Path.IsPathRooted(reference)
            || reference.StartsWith(".", StringComparison.Ordinal)
            || reference.StartsWith("~", StringComparison.Ordinal)
            || (!reference.Contains(':') && Directory.Exists(reference));

    private static IEnumerable<string> NpmRoots(string globalAgentDirectory, string packageName)
    {
        var packageParts = packageName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        yield return Path.Combine([globalAgentDirectory, "packages", "node_modules", .. packageParts]);
        yield return Path.Combine([globalAgentDirectory, "npm", "node_modules", .. packageParts]);
        yield return Path.Combine([globalAgentDirectory, "npm", "install", "global", "node_modules", .. packageParts]);
    }

    private static string NpmPackageName(string spec)
    {
        if (spec.StartsWith('@'))
        {
            var secondAt = spec.IndexOf('@', 1);
            return secondAt < 0 ? spec : spec[..secondAt];
        }

        var versionAt = spec.IndexOf('@');
        return versionAt < 0 ? spec : spec[..versionAt];
    }

    private static string? GitRoot(string globalAgentDirectory, string reference)
    {
        var text = reference.StartsWith("git:", StringComparison.OrdinalIgnoreCase) ? reference[4..] : reference;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var path = StripGitSuffix(uri.AbsolutePath.Trim('/'));
            return Path.Combine([globalAgentDirectory, "git", uri.Host, .. path.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        }

        var scpLikeAt = text.IndexOf('@');
        var colon = text.IndexOf(':', scpLikeAt + 1);
        if (scpLikeAt >= 0 && colon > scpLikeAt)
        {
            var host = text[(scpLikeAt + 1)..colon];
            var path = StripGitSuffix(text[(colon + 1)..]);
            return Path.Combine([globalAgentDirectory, "git", host, .. path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);
        }

        var parts = text.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries);
        var githubIndex = Array.FindIndex(parts, part => string.Equals(part, "github.com", StringComparison.OrdinalIgnoreCase));
        if (githubIndex >= 0 && parts.Length > githubIndex + 2)
        {
            return Path.Combine([globalAgentDirectory, "git", "github.com", .. parts.Skip(githubIndex + 1).Select(StripGitSuffix)]);
        }

        return null;
    }

    private static string StripGitSuffix(string text)
        => text.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;

    public static string Sanitize(string reference)
        => string.Concat(reference.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-'));
}
