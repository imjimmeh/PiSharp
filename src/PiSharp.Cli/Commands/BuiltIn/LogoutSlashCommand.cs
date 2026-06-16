using System.Collections.Immutable;

namespace PiSharp.Cli.Commands;

public sealed class LogoutSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["logout"];

    public string Description { get; } = "Built-in /logout command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        if (context.OAuthStorage is null)
            return new SlashCommandResult(true, "Auth storage is not available.", IsError: true);

        var storedProviders = await context.OAuthStorage.ListStoredProvidersAsync(cancellationToken);
        if (storedProviders.Count == 0)
            return new SlashCommandResult(true, "No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.");

        var selected = await context.SelectAsync("Select provider to log out:", storedProviders, cancellationToken);
        if (string.IsNullOrWhiteSpace(selected))
            return new SlashCommandResult(true, "Logout cancelled.");

        await context.OAuthStorage.RemoveTokenAsync(selected, cancellationToken);
        await context.NotifyAsync($"Logged out of {selected}. Environment variables and models.json config are unchanged.", cancellationToken);
        return new SlashCommandResult(true, $"Removed stored credentials for {selected}.");
    }
}
