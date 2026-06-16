using PiSharp.Abstractions.Environment;

namespace PiSharp.Tools.Shared;

public static class PathUtilities
{
    public static async Task<string> ResolvePathAsync(IFileSystem fileSystem, string path)
    {
        var absolute = await fileSystem.AbsolutePathAsync(path).ConfigureAwait(false);
        return absolute.GetOrThrow(error => error);
    }

    public static async Task CreateParentDirectoryAsync(IFileSystem fileSystem, string absolutePath, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrWhiteSpace(parent)) return;
        var created = await fileSystem.CreateDirectoryAsync(parent, recursive: true, cancellationToken).ConfigureAwait(false);
        created.GetOrThrow(error => error);
    }
}
