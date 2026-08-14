namespace PiSharp.Plugins.ForeignCompat.Tests;

/// <summary>Scoped temp directory for a fake foreign-source repo (skills + rules).</summary>
internal sealed class TempRepo : IDisposable
{
    public TempRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "pisharp-foreign-compat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string WriteFile(string relativePath, string content)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var path = Path.Combine(Root, normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void DeleteFile(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        if (File.Exists(path)) File.Delete(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Options pinned to a single temp repo — no real home/global dirs in tests.</summary>
internal static class RepoOptions
{
    public static ForeignCompatOptions For(TempRepo repo, Action<ForeignCompatOptions>? configure = null)
    {
        var options = new ForeignCompatOptions
        {
            Roots = [repo.Root],
            RepoRoot = repo.Root,
        };
        configure?.Invoke(options);
        return options;
    }
}
