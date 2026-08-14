using System.Text;
using PiSharp.Abstractions.Messages;
using PiSharp.Extensions;
using PiSharp.Memory.Abstractions;

namespace PiSharp.Memory;

/// <summary>
/// The <c>/memory</c> slash command: inspection surface for the model-facing
/// memory tools. Subcommands: (summary) | <c>list [kind]</c> | <c>show &lt;recordKey&gt;</c>
/// | <c>forget &lt;recordKey&gt;</c> (hard delete) | <c>backend</c>.
/// </summary>
public sealed class MemoryCommandHandler
{
    private readonly IExtensionApi _api;
    private readonly IMemoryStore _store;
    private readonly Func<MemorySettings> _settings;

    public MemoryCommandHandler(IExtensionApi api, IMemoryStore store, Func<MemorySettings> settings)
    {
        _api = api;
        _store = store;
        _settings = settings;
    }

    public async Task HandleAsync(string args, CancellationToken cancellationToken)
    {
        var text = await BuildResponseAsync(args, cancellationToken).ConfigureAwait(false);
        await _api.SendMessageAsync(AgentMessages.User(text), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> BuildResponseAsync(string args, CancellationToken cancellationToken)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts.Length == 0 ? string.Empty : parts[0].ToLowerInvariant();
        return command switch
        {
            "" => await BuildSummaryAsync(cancellationToken).ConfigureAwait(false),
            "list" => await BuildListAsync(parts.Skip(1).FirstOrDefault(), cancellationToken).ConfigureAwait(false),
            "show" => parts.Length > 1
                ? await BuildShowAsync(parts[1], cancellationToken).ConfigureAwait(false)
                : "Usage: /memory show <recordKey>",
            "forget" => parts.Length > 1
                ? await BuildForgetAsync(parts[1], cancellationToken).ConfigureAwait(false)
                : "Usage: /memory forget <recordKey>",
            "backend" => BuildBackendInfo(),
            _ => "Usage: /memory [list [kind]] [show <recordKey>] [forget <recordKey>] [backend]"
        };
    }

    private async Task<string> BuildSummaryAsync(CancellationToken cancellationToken)
    {
        var settings = _settings();
        var projectQuery = new MemoryQuery(Limit: int.MaxValue);
        var userQuery = new MemoryQuery(Limit: int.MaxValue);
        var projectCount = (await _store.ListAsync(MemoryScope.Project, projectQuery, cancellationToken).ConfigureAwait(false)).Count;
        var userCount = (await _store.ListAsync(MemoryScope.User, userQuery, cancellationToken).ConfigureAwait(false)).Count;

        return new StringBuilder()
            .AppendLine("## Memory")
            .AppendLine($"- Backend: {_store.Provider.Id} ({_store.Provider.DisplayName})")
            .AppendLine($"- Project key: {_store.ProjectKey}")
            .AppendLine($"- Records: {projectCount} project / {userCount} user")
            .AppendLine($"- Auto-learn: {(settings.AutolearnEnabled ? $"on (min {settings.AutolearnMinToolCalls} tool calls, autoContinue={settings.AutolearnAutoContinue})" : "off")}")
            .ToString()
            .TrimEnd();
    }

    private async Task<string> BuildListAsync(string? kind, CancellationToken cancellationToken)
    {
        MemoryKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            try { kindFilter = MemoryToolCoordinator.ParseKind(kind); }
            catch (ArgumentException) { return $"Invalid kind '{kind}': expected \"fact\", \"lesson\", \"summary\" or \"mental-model\"."; }
        }

        var query = new MemoryQuery(Kind: kindFilter, Limit: 100);
        var project = await _store.ListAsync(MemoryScope.Project, query, cancellationToken).ConfigureAwait(false);
        var user = await _store.ListAsync(MemoryScope.User, query, cancellationToken).ConfigureAwait(false);

        if (project.Count == 0 && user.Count == 0) return "No memory records.";

        var builder = new StringBuilder();
        if (project.Count > 0)
        {
            builder.AppendLine($"### Project ({project.Count})");
            builder.AppendLine(FormatRecords(project));
            builder.AppendLine();
        }
        if (user.Count > 0)
        {
            builder.AppendLine($"### User ({user.Count})");
            builder.AppendLine(FormatRecords(user));
        }
        return builder.ToString().TrimEnd();
    }

    private async Task<string> BuildShowAsync(string recordKey, CancellationToken cancellationToken)
    {
        var project = await _store.GetAsync(MemoryScope.Project, recordKey, cancellationToken).ConfigureAwait(false);
        var user = await _store.GetAsync(MemoryScope.User, recordKey, cancellationToken).ConfigureAwait(false);
        var record = project ?? user;
        return record is null
            ? $"No record '{recordKey}'."
            : $"[{record.RecordKey}] ({MemoryToolCoordinator.KindName(record.Kind)}, {(project is not null ? "project" : "user")} scope)\n{record.Title}\n\n{record.Content}";
    }

    private async Task<string> BuildForgetAsync(string recordKey, CancellationToken cancellationToken)
    {
        var deletedProject = await _store.DeleteAsync(MemoryScope.Project, recordKey, cancellationToken).ConfigureAwait(false);
        var deletedUser = !deletedProject && await _store.DeleteAsync(MemoryScope.User, recordKey, cancellationToken).ConfigureAwait(false);
        return deletedProject || deletedUser
            ? $"Forgot '{recordKey}'."
            : $"No record '{recordKey}' to forget.";
    }

    private string BuildBackendInfo()
        => $"extensions.pisharp-memory.backend = \"{_settings().Backend}\" (active: {_store.Provider.Id}).";

    private static string FormatRecords(IReadOnlyList<MemoryRecord> records)
        => string.Join("\n", records.Select(record =>
            $"- [{record.RecordKey}] {record.Title} ({MemoryToolCoordinator.KindName(record.Kind)}){(record.IsInvalidated ? " [invalidated]" : "")}"));
}
