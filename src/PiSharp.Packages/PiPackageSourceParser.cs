namespace PiSharp.Packages;

public static class PiPackageSourceParser
{
    public static PiPackageSource Parse(string reference)
    {
        if (reference.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            return ParseNpm(reference);

        if (reference.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
            return ParseGit(reference);

        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return ParseUrl(reference);

        if (IsLocalReference(reference))
            return ParseLocal(reference);

        return ParseLocal(reference);
    }

    private static PiPackageSource ParseNpm(string reference)
    {
        var body = reference[4..];
        var (identity, versionOrRef) = SplitNpmIdentity(body);
        var name = identity;
        var isPinned = versionOrRef is not null;
        return new PiPackageSource(
            PiPackageSourceKind.Npm, reference, identity, name,
            versionOrRef, null, null, null, isPinned);
    }

    private static (string Identity, string? VersionOrRef) SplitNpmIdentity(string body)
    {
        if (body.StartsWith('@'))
        {
            var secondAt = body.IndexOf('@', 1);
            if (secondAt < 0) return (body, null);
            return (body[..secondAt], body[(secondAt + 1)..]);
        }

        var versionAt = body.IndexOf('@');
        if (versionAt < 0) return (body, null);
        return (body[..versionAt], body[(versionAt + 1)..]);
    }

    private static PiPackageSource ParseGit(string reference)
    {
        var body = reference[4..];
        return ParseGitUrl(reference, body);
    }

    private static PiPackageSource ParseUrl(string reference)
    {
        return ParseGitUrl(reference, reference);
    }

    private static PiPackageSource ParseGitUrl(string original, string body)
    {
        var hashIndex = body.IndexOf('#');
        var url = hashIndex >= 0 ? body[..hashIndex] : body;
        var ref_ = hashIndex >= 0 ? body[(hashIndex + 1)..] : null;

        string host;
        string repoPath;

        if (url.Contains("://"))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                host = uri.Host;
                repoPath = uri.AbsolutePath.Trim('/').TrimEnd(".git", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                host = "";
                repoPath = url;
            }
        }
        else
        {
            var scpLikeAt = url.IndexOf('@');
            var colon = url.IndexOf(':', scpLikeAt + 1);
            if (scpLikeAt >= 0 && colon > scpLikeAt)
            {
                host = url[(scpLikeAt + 1)..colon];
                repoPath = url[(colon + 1)..].TrimEnd(".git", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                host = "";
                repoPath = url;
            }
        }

        var identity = $"{host}/{repoPath}";
        var parts = repoPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join("/", parts.Length >= 2 ? parts[^2..] : parts);
        var isPinned = ref_ is not null && ref_.Length > 0;

        return new PiPackageSource(
            PiPackageSourceKind.Git, original, identity, name,
            ref_, host, repoPath, null, isPinned);
    }

    private static PiPackageSource ParseLocal(string reference)
    {
        return new PiPackageSource(
            PiPackageSourceKind.Local, reference, reference,
            reference, null, null, null, reference, false);
    }

    private static bool IsLocalReference(string reference)
        => Path.IsPathRooted(reference)
            || reference.StartsWith(".", StringComparison.Ordinal)
            || reference.StartsWith("~", StringComparison.Ordinal)
            || (!reference.Contains(':') && !reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !reference.StartsWith("git@", StringComparison.Ordinal));
}

internal static class StringExtensions
{
    public static string TrimEnd(this string text, string suffix, StringComparison comparison)
    {
        if (text.EndsWith(suffix, comparison))
            return text[..^suffix.Length];
        return text;
    }
}
