using System.Text.RegularExpressions;

namespace PiSharp.Tools.Search;

public static class GlobMatcher
{
    public static bool IsMatch(string glob, string relativePath)
    {
        var regex = "^" + Regex.Escape(glob.Replace('\\', '/')).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$";
        var normalizedPath = relativePath.Replace('\\', '/');
        return Regex.IsMatch(normalizedPath, regex, RegexOptions.IgnoreCase)
               || Regex.IsMatch(Path.GetFileName(normalizedPath), regex, RegexOptions.IgnoreCase);
    }
}
