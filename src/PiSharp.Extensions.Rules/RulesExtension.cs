using PiSharp.Agent.Core.Prompting;
using PiSharp.Extensions;

[assembly: ExtensionMetadata(
    "pisharp-rules",
    Name = "PiSharp Rules",
    Version = "1.0.0",
    Description = "Registers the --no-rules / --disable-sticky flags, file and sticky rule providers, and the rules stream-delta interceptor.")]

namespace PiSharp.Extensions.Rules;
/// <summary>
/// <c>pisharp-rules</c> extension entry (P10 plan §5.5): registers the <c>--no-rules</c>
/// (and <c>--disable-sticky</c>) flags, the built-in file + sticky rule providers, the
/// <see cref="RulesEngine"/> stream-delta interceptor, a <c>rules.always</c> prompt
/// contributor, and the <c>resources_discover</c>/<c>resources_update</c>/<c>session_start</c>
/// discovery triggers. When <c>--no-rules</c> is set, discovery and matching are disabled:
/// no providers, no injection.
/// </summary>
public sealed class RulesExtension : IExtension, IDisposable
{
    public const string NoRulesFlag = "no-rules";
    public const string DisableStickyFlag = "disable-sticky";

    private readonly string? _overrideHomeDirectory;
    private readonly Action<RulesEngine>? _registerInterceptor;
    private readonly List<IDisposable> _registrations = [];
    private RulesEngine? _engine;

    /// <summary>
    /// <paramref name="homeDirectory"/> overrides the user-profile root for tests;
    /// <paramref name="registerInterceptor"/> is invoked with the engine once created so the
    /// host can slot it into the <c>stream-delta:{sourceId}</c> registry collection (the
    /// extension API does not yet surface interceptor registration).
    /// </summary>
    public RulesExtension(
        string? homeDirectory = null,
        Action<RulesEngine>? registerInterceptor = null)
    {
        _overrideHomeDirectory = homeDirectory;
        _registerInterceptor = registerInterceptor;
    }

    /// <summary>Parameterless constructor required by the native plugin host; all options use defaults.</summary>
    public RulesExtension()
    {
    }

    /// <summary>The engine owned by this extension (the interceptor instance).</summary>
    public RulesEngine? Engine => _engine;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        api.RegisterFlag(new ExtensionFlagRegistration(
            NoRulesFlag, "Disables rule discovery and matching.", DefaultValue: false));
        api.RegisterFlag(new ExtensionFlagRegistration(
            DisableStickyFlag, "Disables the synthesized RULES.md rules.", DefaultValue: false));

        var disabled = api.GetFlag(NoRulesFlag) is true;
        var disableSticky = api.GetFlag(DisableStickyFlag) is true;

        var home = _overrideHomeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentDir = Path.Combine(home, ".pi", "agent");

        var options = new RulesOptions
        {
            RuleRoots = ComputeRuleRoots(api.Cwd, Path.Combine(agentDir, "rules")),
            UserStickyRulesPath = Path.Combine(agentDir, "RULES.md"),
            Cwd = api.Cwd,
            Disabled = disabled,
            DisableSticky = disableSticky,
        };

        if (!disabled)
        {
            var fileProvider = new RulesDirectoryProvider(
                options.RuleRoots,
                singleFileCandidates: [Path.Combine(agentDir, "rules.md")]);
            _registrations.Add(api.Rules.RegisterProvider(fileProvider));

            if (!disableSticky)
            {
                _registrations.Add(api.Rules.RegisterProvider(
                    new StickyRulesProvider(options.UserStickyRulesPath, api.Cwd)));
            }
        }

        _engine = new RulesEngine(
            ct => api.Rules.GetAllRulesAsync(ct),
            options);

        _registrations.Add(api.Prompt.RegisterContributor(
            new RulesPromptContributor(() => _engine?.Rules ?? [])));

        _registrations.Add(api.On(ExtensionEventNames.ResourcesDiscover, (_, ct) => DiscoverAsync(ct)));
        _registrations.Add(api.On(ExtensionEventNames.ResourcesUpdate, (_, ct) => DiscoverAsync(ct)));
        _registrations.Add(api.On(ExtensionEventNames.SessionStart, (_, ct) => DiscoverAsync(ct)));

        RegisterInterceptor(api, _engine);
        return Task.CompletedTask;
    }

    private void RegisterInterceptor(IExtensionApi api, RulesEngine engine)
    {
        if (api.StreamDelta is { } streamDelta)
        {
            _registrations.Add(streamDelta.RegisterInterceptor(engine));
            return;
        }
        _registerInterceptor?.Invoke(engine);
    }

    private Task DiscoverAsync(CancellationToken cancellationToken)
        => _engine?.DiscoverAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Rule roots in nearest-first order: the cwd's <c>.pi/rules</c>, then each ancestor's
    /// <c>.pi/rules</c> outward (deduped), then the user-wide <c>~/.pi/agent/rules</c>. A
    /// rule <see cref="Rule.Name"/> collision resolves nearest-root-wins.
    /// </summary>
    public static IReadOnlyList<string> ComputeRuleRoots(string cwd, string userRulesDir)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var current = Path.GetFullPath(cwd);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, ".pi", "rules");
            if (Directory.Exists(candidate))
            {
                var full = Path.GetFullPath(candidate);
                if (seen.Add(full)) roots.Add(full);
            }
            var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }

        if (Directory.Exists(userRulesDir))
        {
            var full = Path.GetFullPath(userRulesDir);
            if (seen.Add(full)) roots.Add(full);
        }

        return roots;
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }
}
