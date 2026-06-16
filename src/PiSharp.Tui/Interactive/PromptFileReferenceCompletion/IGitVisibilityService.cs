namespace PiSharp.Tui.Interactive;

internal interface IGitVisibilityService
{
    IEnumerable<string> EnumerateVisiblePaths(string baseDirectory, bool recursive);
}
