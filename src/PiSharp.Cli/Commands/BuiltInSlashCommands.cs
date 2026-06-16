namespace PiSharp.Cli.Commands;

public static class BuiltInSlashCommands
{
    public static readonly string[] Names = [.. BuiltInSlashCommandCatalog.Names];

    public static SlashCommandRegistry CreateRegistry()
        => BuiltInSlashCommandCatalog.CreateRegistry();
}
