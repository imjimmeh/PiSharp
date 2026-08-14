using PiSharp.Extensions;

[assembly: ExtensionMetadata("pisharp-foreign-compat", Name = "PiSharp foreign rules & skills compatibility", Version = "1.0.0")]

namespace PiSharp.Plugins.ForeignCompat;

/// <summary>
/// <c>pisharp-foreign-compat</c> extension entry (P11 plan §7): a pure composition
/// plugin that ingests skills written for Claude/Codex/GitHub/Cursor/Cline/Gemini/OpenCode
/// into the P04 structured-skill pipeline (via <see cref="IExtensionSkillApi.RegisterSkillProvider"/>)
/// and foreign rule files (<c>.clinerules</c>, <c>.cursorrules</c>, Cursor MDC, Copilot
/// applyTo, Gemini config roots, repo <c>RULES.md</c>, GitHub rules) into the P10 rules
/// engine (via <see cref="IExtensionRuleApi.RegisterProvider"/>). No core changes;
/// toggles and include/ignore globs are read from <c>extensions.pisharp-foreign-compat.*</c>
/// (P02) and re-read lazily on discovery (plan §4.3).
/// </summary>
public sealed class ForeignCompatExtension : IExtension, IAsyncDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private ForeignCompatOptions? _options;

    public Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        _options = ForeignCompatOptions.FromApi(api.Settings, api.Cwd);

        // Skill providers — one per priority tier (plan §4.2), P04 ingress.
        RegisterSkillProvider(api.Skills, new ClaudeSkillProvider(_options));
        RegisterSkillProvider(api.Skills, new CodexSkillProvider(_options));
        RegisterSkillProvider(api.Skills, new GeminiSkillProvider(_options));
        RegisterSkillProvider(api.Skills, new OpenCodeSkillProvider(_options));
        RegisterSkillProvider(api.Skills, new CursorSkillProvider(_options));
        RegisterSkillProvider(api.Skills, new ClineSkillProvider(_options));
        RegisterSkillProvider(api.Skills, new GithubSkillProvider(_options));

        // Rule providers — one per foreign format family (plan §5.2), P10 ingress.
        RegisterRuleProvider(api.Rules, new ClineRuleProvider(_options));
        RegisterRuleProvider(api.Rules, new CursorRuleProvider(_options));
        RegisterRuleProvider(api.Rules, new CopilotRuleProvider(_options));
        RegisterRuleProvider(api.Rules, new GeminiRuleProvider(_options));
        RegisterRuleProvider(api.Rules, new RepoRuleProvider(_options));
        RegisterRuleProvider(api.Rules, new GithubRuleProvider(_options));

        // Mid-session settings changes are honored on the next discovery without a reload.
        _registrations.Add(api.Settings.OnChange(_ => _options?.Reload()));
        return Task.CompletedTask;
    }

    private void RegisterSkillProvider(IExtensionSkillApi skills, ISkillProvider provider)
    {
        var registration = skills.RegisterSkillProvider(provider);
        _registrations.Add(registration);
    }

    private void RegisterRuleProvider(IExtensionRuleApi rules, IRuleProvider provider)
    {
        var registration = rules.RegisterProvider(provider);
        _registrations.Add(registration);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
        return ValueTask.CompletedTask;
    }
}
