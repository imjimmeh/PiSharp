namespace PiSharp.Tools.Search;

public static class RelativePathFormatter
{
    public static string Format(string root, string path)
    {
        root = root.Replace('\\', '/').TrimEnd('/');
        path = path.Replace('\\', '/');
        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..] : path;
    }
}
