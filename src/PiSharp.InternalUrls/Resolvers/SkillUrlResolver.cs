using PiSharp.Abstractions.Environment;
using PiSharp.Agent.Resources;
using PiSharp.Extensions;

namespace PiSharp.InternalUrls.Resolvers;

/// <summary>
/// Resolves <c>skill://&lt;name&gt;</c> to a skill's body and
/// <c>skill://&lt;name&gt;/&lt;relpath&gt;</c> to an asset file inside that
/// skill's directory (containment-checked). Skills are looked up through an
/// injected accessor bound to the harness's skill resolution, never by
/// re-scanning the filesystem.
/// </summary>
public sealed class SkillUrlResolver(Func<string, Skill?> skillLookup, IExecutionEnv? env = null) : IInternalUrlResolver
{
    private readonly Func<string, Skill?> _skillLookup = skillLookup
        ?? throw new ArgumentNullException(nameof(skillLookup));

    public string Scheme => "skill";

    public async ValueTask<InternalUrlResult> ResolveAsync(InternalUrlRequest request, CancellationToken ct)
    {
        if (!InternalUrlSecurity.TryParseTarget(request.Target, out var segments))
            return Blocked(request.Target);

        var name = segments[0];
        var skill = _skillLookup(name);
        if (skill is null)
            return NotFound($"Unknown skill '{name}'.");

        // skill://<name> → skill body.
        if (segments.Count == 1)
            return Resolved(skill.Content);

        // skill://<name>/<relpath...> → containment-checked asset read.
        if (env is null)
            return Forbidden("Skill asset reads require an execution environment.");

        var skillDir = Path.GetDirectoryName(skill.FilePath);
        if (string.IsNullOrWhiteSpace(skillDir))
            return NotFound($"Skill '{name}' has no on-disk location for asset reads.");

        var relative = string.Join(Path.DirectorySeparatorChar, segments.Skip(1));
        var absolute = Path.Combine(skillDir, relative);
        if (!InternalUrlSecurity.IsContainedWithin(absolute, skillDir))
            return Blocked(request.Target);

        var read = await env.ReadTextFileAsync(absolute, ct);
        if (read.IsErr)
            return NotFound($"Skill asset '{string.Join('/', segments.Skip(1))}' not found.");

        return Resolved(read.Value);
    }

    private static InternalUrlResult Resolved(string content) => new(true, content);

    private static InternalUrlResult Blocked(string target)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.TraversalBlocked, $"Traversal blocked in skill:// target '{target}'."));

    private static InternalUrlResult NotFound(string reason)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.NotFound, reason));

    private static InternalUrlResult Forbidden(string reason)
        => new(false, null, new InternalUrlError(InternalUrlErrorKind.Forbidden, reason));
}
