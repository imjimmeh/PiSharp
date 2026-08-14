namespace PiSharp.Packages;

public sealed class PiPackageManager
{
    private readonly string _packageRoot;
    private readonly IPackageProcessRunner _runner;

    public PiPackageManager(string packageRoot, IPackageProcessRunner runner)
    {
        _packageRoot = packageRoot;
        _runner = runner;
    }

    public async Task NpmInstallAsync(string reference, bool offline = false, bool force = false)
    {
        var source = PiPackageSourceParser.Parse(reference);

        if (offline) return;
        if (!force && source.IsPinned) return;

        Directory.CreateDirectory(_packageRoot);
        var pkgJson = Path.Combine(_packageRoot, "package.json");
        if (!File.Exists(pkgJson))
        {
            await File.WriteAllTextAsync(pkgJson, """{"private":true}""");
        }

        var packageSpec = source.VersionOrRef is not null
            ? $"{source.Name}@{source.VersionOrRef}"
            : source.Name;

        await _runner.RunAsync("npm", $"install {packageSpec}", _packageRoot);
    }

    public async Task NpmUninstallAsync(string reference)
    {
        var source = PiPackageSourceParser.Parse(reference);

        Directory.CreateDirectory(_packageRoot);
        var pkgJson = Path.Combine(_packageRoot, "package.json");
        if (!File.Exists(pkgJson))
        {
            await File.WriteAllTextAsync(pkgJson, """{"private":true}""");
        }

        await _runner.RunAsync("npm", $"uninstall {source.Name}", _packageRoot);
    }

    public async Task GitInstallAsync(string reference, bool offline = false, bool force = false)
    {
        var source = PiPackageSourceParser.Parse(reference);
        if (offline) return;
        if (!force && source.IsPinned) return;

        var gitDir = GetManagedGitPath(source);
        EnsureInsideManagedRoot(gitDir);
        Directory.CreateDirectory(Path.GetDirectoryName(gitDir)!);

        var cloneUrl = reference.StartsWith("git:", StringComparison.OrdinalIgnoreCase)
            ? reference[4..]
            : reference;

        var hashIndex = cloneUrl.IndexOf('#');
        var url = hashIndex >= 0 ? cloneUrl[..hashIndex] : cloneUrl;

        await _runner.RunAsync("git", $"clone {url} {gitDir}");
    }

    public async Task<bool> LocalInstallAsync(string localPath)
    {
        if (!Directory.Exists(localPath)) return false;
        return true;
    }

    public async Task GitUpdateAsync(string reference)
    {
        var source = PiPackageSourceParser.Parse(reference);
        await GitUpdateAsync(source, GetManagedGitPath(source));
    }

    public async Task GitUpdateAsync(string reference, string targetPath)
    {
        var source = PiPackageSourceParser.Parse(reference);
        await GitUpdateAsync(source, targetPath);
    }

    private async Task GitUpdateAsync(PiPackageSource source, string targetPath)
    {
        EnsureInsideManagedRoot(targetPath);
        if (source.IsPinned) return;

        await _runner.RunAsync("git", "pull", targetPath);
    }

    private string GetManagedGitPath(PiPackageSource source)
    {
        var repositoryParts = (source.RepositoryPath ?? source.Name)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (repositoryParts.Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException($"Repository path traversal is not allowed for package source {source.Original}.");
        }

        return Path.Combine([_packageRoot, "git", source.Host ?? "unknown", .. repositoryParts]);
    }

    private void EnsureInsideManagedRoot(string targetPath)
    {
        var packageRootFull = Path.GetFullPath(_packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetFull = Path.GetFullPath(targetPath);
        var packageRootPrefix = packageRootFull + Path.DirectorySeparatorChar;

        if (!targetFull.Equals(packageRootFull, StringComparison.OrdinalIgnoreCase)
            && !targetFull.StartsWith(packageRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Target path {targetPath} is not inside managed root {_packageRoot}.");
        }
    }
}
