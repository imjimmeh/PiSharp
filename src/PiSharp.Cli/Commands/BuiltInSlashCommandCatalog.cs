using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public static class BuiltInSlashCommandCatalog
{
    public static ImmutableArray<IBuiltInSlashCommand> Commands { get; } =
    [
        new SettingsSlashCommand(),
        new ModelSlashCommand(),
        new ScopedModelsSlashCommand(),
        new ExportSessionSlashCommand(),
        new ImportSessionSlashCommand(),
        new ShareSessionSlashCommand(),
        new CopyLastAssistantMessageSlashCommand(),
        new NameSlashCommand(),
        new ResumeSessionSlashCommand(),
        new ChangelogSlashCommand(),
        new HotkeysSlashCommand(),
        new ForkSessionSlashCommand(),
        new SessionTreeSlashCommand(),
        new LoginSlashCommand(),
        new LogoutSlashCommand(),
        new NewSessionSlashCommand(),
        new CompactSlashCommand(),
        new ReloadSlashCommand(),
        new QuitSlashCommand()
    ];

    public static ImmutableArray<string> Names { get; } =
    [
        "settings",
        "model",
        "models",
        "scoped-models",
        "export",
        "import",
        "share",
        "copy",
        "name",
        "session",
        "changelog",
        "hotkeys",
        "fork",
        "clone",
        "tree",
        "login",
        "logout",
        "new",
        "compact",
        "reload",
        "resume",
        "quit"
    ];

    public static SlashCommandRegistry CreateRegistry()
    {
        var registry = new SlashCommandRegistry();
        foreach (var command in Commands)
        {
            foreach (var name in command.Names)
            {
                registry.Register(new SlashCommandDefinition(name, command.Description, command.ExecuteAsync));
            }
        }

        return registry;
    }
}
