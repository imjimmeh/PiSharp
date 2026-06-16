namespace PiSharp.Tools.Search;

public static class ExternalSearchCommand
{
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
