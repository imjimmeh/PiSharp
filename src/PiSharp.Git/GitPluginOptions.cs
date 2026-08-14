namespace PiSharp.Git;

/// <summary>
/// Plugin settings. Reads <c>PISHARP_GIT_*</c> environment variables with defaults.
/// [settings: deferred until P02] — once <c>IExtensionApi.Settings</c> (namespace
/// <c>pisharp-git</c>) lands, this class resolves settings through it; until then it
/// degrades to env vars + defaults exactly as the plan's phase gating describes.
/// Values are read at invocation time (no caching), so env changes take effect
/// immediately.
/// </summary>
public sealed class GitPluginOptions
{
    public bool CommitAutoSplit { get; init; } = true;

    public bool CommitConfirmSlashCommand { get; init; } = true;

    public bool CommitConfirmModelTool { get; init; }

    public bool CommitRunHooks { get; init; } = true;

    public bool CommitConventionalPrefix { get; init; } = true;

    public IReadOnlyList<string> CommitExcludedPathPatterns { get; init; } =
    [
        "package-lock.json",
        "pnpm-lock.yaml",
        "yarn.lock",
        "bun.lock",
        "bun.lockb",
        "deno.lock",
        "Cargo.lock",
        "poetry.lock",
        "composer.lock",
        "Gemfile.lock",
        "go.sum",
        "Pipfile.lock",
        "uv.lock",
        "flake.lock",
        "packages.lock.json",
        "**/obj/project.assets.json"
    ];

    public string ShareVisibility { get; init; } = "private";

    public string? ShareDescription { get; init; }

    public string? ShareFileName { get; init; }

    public long ShareMaxBytes { get; init; } = 1_000_000;

    public string GithubTokenEnvVar { get; init; } = "GITHUB_TOKEN";

    public string GithubAuthStoreProvider { get; init; } = "github";

    public bool GithubGhCliLookup { get; init; }

    public string GithubApiBaseUrl { get; init; } = "https://api.github.com";

    public static GitPluginOptions FromEnvironment()
    {
        bool Bool(string name, bool fallback) => Environment.GetEnvironmentVariable(name) is { } raw
            && bool.TryParse(raw, out var value) ? value : fallback;

        string String(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } raw
            ? raw : fallback;

        long Long(string name, long fallback) => Environment.GetEnvironmentVariable(name) is { } raw
            && long.TryParse(raw, out var value) ? value : fallback;

        string[] Patterns(string name, string[] fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (raw is null or { Length: 0 })
            {
                return fallback;
            }

            return raw.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return new GitPluginOptions
        {
            CommitAutoSplit = Bool("PISHARP_GIT_COMMIT_AUTOSPLIT", true),
            CommitConfirmSlashCommand = Bool("PISHARP_GIT_COMMIT_CONFIRM", true),
            CommitConfirmModelTool = Bool("PISHARP_GIT_COMMIT_CONFIRM_MODEL_TOOL", false),
            CommitRunHooks = Bool("PISHARP_GIT_COMMIT_RUN_HOOKS", true),
            CommitConventionalPrefix = Bool("PISHARP_GIT_COMMIT_CONVENTIONAL_PREFIX", true),
            CommitExcludedPathPatterns = Patterns("PISHARP_GIT_COMMIT_EXCLUDED_PATTERNS",
                new GitPluginOptions().CommitExcludedPathPatterns.ToArray()),
            ShareVisibility = String("PISHARP_GIT_SHARE_VISIBILITY", "private"),
            ShareDescription = String("PISHARP_GIT_SHARE_DESCRIPTION", ""),
            ShareFileName = String("PISHARP_GIT_SHARE_FILENAME", ""),
            ShareMaxBytes = Long("PISHARP_GIT_SHARE_MAX_BYTES", 1_000_000),
            GithubTokenEnvVar = String("PISHARP_GIT_GITHUB_TOKEN_ENV_VAR", "GITHUB_TOKEN"),
            GithubAuthStoreProvider = String("PISHARP_GIT_GITHUB_AUTH_STORE_PROVIDER", "github"),
            GithubGhCliLookup = Bool("PISHARP_GIT_GITHUB_GH_CLI_LOOKUP", false),
            GithubApiBaseUrl = String("PISHARP_GIT_GITHUB_API_BASE_URL", "https://api.github.com")
        };
    }
}
