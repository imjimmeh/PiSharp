using System.Text.Json;
using PiSharp.Abstractions.Environment;
using PiSharp.Agent.Resources;
using PiSharp.Extensions;
using PiSharp.InternalUrls.Resolvers;
using PiSharp.InternalUrls.Services;

[assembly: ExtensionMetadata(
    "pisharp-internal-urls",
    Name = "PiSharp Internal URL Schemes",
    Version = "0.1.0",
    Description = "Registers the built-in internal URL resolvers (skill://, agent://, diff://) against the runtime-wide InternalUrlRegistry.")]

namespace PiSharp.InternalUrls;

/// <summary>
/// Core-plugin entry that seeds the built-in internal URL scheme resolvers
/// (<c>skill://</c>, <c>agent://</c>, <c>diff://</c>) into the runtime-wide
/// registry via <see cref="IExtensionApi.Urls"/>. Each scheme is individually
/// optional and gracefully reports <c>NotFound</c> when its accessor is
/// unpopulated. Runtime state (skills, subagent results, diffs) is supplied
/// through injected accessors so the plugin stays decoupled from the harness.
/// </summary>
public sealed class InternalUrlsExtension : IExtension
{
    private readonly Func<string, Skill?>? _skillLookup;
    private readonly Func<string, JsonElement?>? _subagentResult;
    private readonly DiffLedger? _diffLedger;
    private readonly IExecutionEnv? _env;
    private readonly int _diffLedgerCapacity;

    public InternalUrlsExtension(
        Func<string, Skill?>? skillLookup = null,
        Func<string, JsonElement?>? subagentResult = null,
        DiffLedger? diffLedger = null,
        IExecutionEnv? env = null,
        int diffLedgerCapacity = DiffLedger.DefaultCapacity)
    {
        _skillLookup = skillLookup;
        _subagentResult = subagentResult;
        _diffLedger = diffLedger;
        _env = env;
        _diffLedgerCapacity = diffLedgerCapacity;
    }

    /// <summary>Parameterless constructor required by the native plugin host; all accessors degrade to API defaults.</summary>
    public InternalUrlsExtension()
        : this(null, null, null, null)
    {
    }

    public async Task InitializeAsync(IExtensionApi api, CancellationToken cancellationToken = default)
    {
        var env = _env ?? api.ExecutionEnv;
        var ledger = _diffLedger ?? new DiffLedger(_diffLedgerCapacity);
        var skillLookup = _skillLookup ?? await SkillLookupFromApiAsync(api, cancellationToken);

        api.Urls.RegisterResolver(new SkillUrlResolver(skillLookup, env));
        api.Urls.RegisterResolver(new AgentUrlResolver(_subagentResult ?? (_ => null)));
        api.Urls.RegisterResolver(new DiffUrlResolver(ledger));
    }

    /// <summary>
    /// Default skill accessor: snapshots extension-registered skills from the
    /// host API so <c>skill://</c> works in-process without a harness-bound
    /// delegate. Callers may supply a more authoritative accessor via the
    /// constructor.
    /// </summary>
    private static async Task<Func<string, Skill?>> SkillLookupFromApiAsync(IExtensionApi api, CancellationToken ct)
    {
        var skills = await api.Skills.GetAllSkillsAsync(ct);
        var map = skills.ToDictionary(
            skill => skill.Name,
            skill => new Skill(skill.Name, skill.Description, skill.Content, skill.FilePath, skill.DisableModelInvocation),
            StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var skill) ? skill : null;
    }
}
