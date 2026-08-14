using System.Diagnostics;
using PiSharp.Ai.Auth;

namespace PiSharp.Git;

/// <summary>
/// Resolves a gist-capable GitHub token. Resolution chain (first hit wins):
/// 1. <see cref="GitPluginOptions.GithubTokenEnvVar"/> environment variable;
/// 2. the auth store under <see cref="GitPluginOptions.GithubAuthStoreProvider"/>
///    (token, then OAuth-credential access);
/// 3. <c>gh auth token</c> subprocess — only when
///    <see cref="GitPluginOptions.GithubGhCliLookup"/> is enabled (default off).
/// The Copilot token (a JWT) and fine-grained PATs are rejected with an explanation —
/// neither can create gists.
/// </summary>
public sealed class GistTokenResolver(IOAuthStorage authStorage, GitPluginOptions options)
{
    public sealed record Resolution(bool Success, string? Token, string? Error);

    public async Task<Resolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var envVar = options.GithubTokenEnvVar;
        var envToken = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            var envReject = IsUnusableToken(envToken);
            if (envReject is not null)
            {
                return new Resolution(false, null, RejectMessage(envReject, envToken));
            }

            return new Resolution(true, envToken, null);
        }

        var provider = options.GithubAuthStoreProvider;
        var storedToken = await authStorage.GetTokenAsync(provider, cancellationToken);
        if (!string.IsNullOrWhiteSpace(storedToken))
        {
            var reject = IsUnusableToken(storedToken);
            if (reject is not null)
            {
                return new Resolution(false, null, RejectMessage(reject, storedToken));
            }

            return new Resolution(true, storedToken, null);
        }

        var credentials = await authStorage.GetOAuthCredentialsAsync(provider, cancellationToken);
        var access = credentials?.Access;
        if (!string.IsNullOrWhiteSpace(access))
        {
            var reject = IsUnusableToken(access);
            if (reject is not null)
            {
                return new Resolution(false, null, RejectMessage(reject, access));
            }

            return new Resolution(true, access, null);
        }

        // A stored github-copilot credential is not gist-capable — explain instead of silently
        // failing later with a generic 401.
        var copilotToken = await authStorage.GetTokenAsync("github-copilot", cancellationToken);
        var copilotHint = string.IsNullOrWhiteSpace(copilotToken) ? string.Empty : " The stored github-copilot token cannot create gists (Copilot OAuth scope is read:user only); store a classic PAT with the 'gist' scope under a separate provider.";

        if (options.GithubGhCliLookup)
        {
            var ghToken = await RunGhAuthTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                return new Resolution(true, ghToken, null);
            }
        }

        return new Resolution(false, null,
            $"No gist-capable GitHub token found. Export {envVar} (a classic PAT with the 'gist' scope), or store one " +
            $"under provider '{provider}'. Fine-grained PATs (github_pat_*) and Copilot tokens cannot create gists." +
            copilotHint);
    }

    /// <summary>
    /// Returns a rejection reason when a token is structurally unusable for the Gists API
    /// (Copilot JWT or fine-grained PAT), else null.
    /// </summary>
    internal static string? IsUnusableToken(string token)
    {
        if (token.StartsWith("github_pat_", StringComparison.Ordinal))
        {
            return "fine-grained PAT";
        }

        return LooksLikeJwt(token) ? "Copilot/OpenAI token" : null;
    }

    private static string RejectMessage(string reason, string token)
        => $"The token resolved for gist upload is a {reason}, which cannot create gists. " +
           "Supply a classic PAT with the 'gist' scope (or set it in the auth store).";

    private static bool LooksLikeJwt(string token)
    {
        var dot1 = token.IndexOf('.');
        if (dot1 <= 0)
        {
            return false;
        }

        var dot2 = token.IndexOf('.', dot1 + 1);
        return dot2 > dot1 + 1;
    }

    private static async Task<string?> RunGhAuthTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("auth");
            startInfo.ArgumentList.Add("token");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var token = stdout.Trim();
            return token.Length == 0 ? null : token;
        }
        catch
        {
            return null;
        }
    }
}
