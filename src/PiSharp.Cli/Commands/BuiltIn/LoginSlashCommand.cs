using PiSharp.Ai;
using PiSharp.Ai.Auth;
using PiSharp.Ai.Models;
using System.Collections.Immutable;
using System.Linq;

namespace PiSharp.Cli.Commands;

public sealed class LoginSlashCommand : IBuiltInSlashCommand
{
    public ImmutableArray<string> Names { get; } = ["login"];

    public string Description { get; } = "Built-in /login command";

    public async Task<SlashCommandResult> ExecuteAsync(SlashCommandContext context, string args, CancellationToken cancellationToken)
    {
        if (context.OAuthStorage is null)
            return new SlashCommandResult(true, "Auth storage is not available.", IsError: true);

        var availableProviders = PublicApi.Models
            .Select(model => model.Descriptor.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (availableProviders.Length == 0)
            return new SlashCommandResult(true, "No model providers available. /login cannot be used.", IsError: true);

        string? selectedProvider;
        if (string.IsNullOrWhiteSpace(args))
        {
            var options = availableProviders
                .Select(provider => OAuthProviderRegistry.IsOAuthProvider(provider) ? $"{provider} (OAuth available)" : provider)
                .ToArray();
            selectedProvider = await context.SelectAsync("Select provider to log in:", options, cancellationToken);
            if (string.IsNullOrWhiteSpace(selectedProvider))
                return new SlashCommandResult(true, "Login cancelled.");

            var bracketIndex = selectedProvider.IndexOf(" (OAuth", StringComparison.Ordinal);
            if (bracketIndex > 0)
                selectedProvider = selectedProvider[..bracketIndex];
        }
        else
        {
            selectedProvider = args.Trim();
            if (!availableProviders.Contains(selectedProvider, StringComparer.OrdinalIgnoreCase))
                return new SlashCommandResult(true, $"Provider '{selectedProvider}' is not available.", IsError: true);
        }

        if (OAuthProviderRegistry.IsOAuthProvider(selectedProvider))
        {
            return await LoginOAuthAsync(context, selectedProvider, cancellationToken);
        }

        return await LoginApiKeyAsync(context, selectedProvider, cancellationToken);
    }

    private static async Task<SlashCommandResult> LoginOAuthAsync(SlashCommandContext context, string providerId, CancellationToken cancellationToken)
    {
        var oauthProvider = OAuthProviderRegistry.Get(providerId);
        if (oauthProvider is null)
            return new SlashCommandResult(true, $"OAuth provider '{providerId}' is not available.", IsError: true);

        await context.NotifyAsync($"Starting {oauthProvider.Name} OAuth login...", cancellationToken);

        var callbacks = new OAuthLoginCallbacks(
            OnAuth: async authInfo =>
            {
                if (context.OpenUrlAsync is not null)
                    await context.OpenUrlAsync(authInfo.Url, cancellationToken);

                await context.NotifyAsync(authInfo.Url, cancellationToken);
                if (authInfo.Instructions is not null)
                    await context.NotifyAsync(authInfo.Instructions, cancellationToken);
            },
            OnPrompt: async (prompt, token) => await context.InputAsync(prompt.Message, token) ?? string.Empty,
            OnProgress: message => context.NotifyAsync(message, cancellationToken));

        try
        {
            var credentials = await oauthProvider.LoginAsync(callbacks, cancellationToken);
            await context.OAuthStorage!.SetOAuthCredentialsAsync(providerId, credentials, cancellationToken);
            await context.NotifyAsync($"Logged into {oauthProvider.Name} successfully.", cancellationToken);
            return new SlashCommandResult(true, $"Successfully authenticated with {oauthProvider.Name}.");
        }
        catch (Exception exception)
        {
            return new SlashCommandResult(true, $"OAuth login failed: {exception.Message}", IsError: true);
        }
    }

    private static async Task<SlashCommandResult> LoginApiKeyAsync(SlashCommandContext context, string providerId, CancellationToken cancellationToken)
    {
        var apiKey = await context.InputAsync($"Enter API key for {providerId}:", cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new SlashCommandResult(true, "Login cancelled.");

        await context.OAuthStorage!.SetTokenAsync(providerId, apiKey, cancellationToken);

        var conflictMessages = new List<string>();
        var envKey = EnvApiKeyDetector.GetEnvApiKey(providerId);
        if (!string.IsNullOrWhiteSpace(envKey) && !string.Equals(envKey, EnvApiKeyDetector.AuthenticatedMarker, StringComparison.Ordinal))
        {
            conflictMessages.Add("existing environment variable");
        }

        var providerConfig = ModelRegistry.GetProviderConfig(providerId);
        if (providerConfig?.ApiKey is not null)
        {
            conflictMessages.Add("provider config apiKey");
        }

        if (conflictMessages.Count > 0)
        {
            await context.NotifyAsync($"API key for {providerId} saved. Overriding conflicting {string.Join(" and ", conflictMessages)}.", cancellationToken);
        }
        else
        {
            await context.NotifyAsync($"API key for {providerId} saved.", cancellationToken);
        }

        return new SlashCommandResult(true, $"API key for {providerId} stored successfully. Use /model to select a model.");
    }
}
