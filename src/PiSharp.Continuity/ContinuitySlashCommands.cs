using PiSharp.Continuity.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Continuity;

/// <summary>
/// The interactive <c>/goal</c>, <c>/heartbeat</c>, <c>/cron</c>, and
/// <c>/autonomous</c> slash-command handlers. Registered via
/// <c>ExtensionCommandRegistration</c> (handler signature
/// <c>(string args, CancellationToken)</c>); all report through the injected
/// <see cref="IExtensionUi"/>.
/// </summary>
public sealed partial class ContinuitySlashCommands
{
    private readonly IExtensionUi _ui;
    private readonly GoalService _goals;
    private readonly ContinuityScheduler _scheduler;
    private readonly HeartbeatService _heartbeat;
    private readonly AutonomousRunner _autonomous;

    public ContinuitySlashCommands(
        IExtensionUi ui,
        GoalService goals,
        ContinuityScheduler scheduler,
        HeartbeatService heartbeat,
        AutonomousRunner autonomous)
    {
        _ui = ui;
        _goals = goals;
        _scheduler = scheduler;
        _heartbeat = heartbeat;
        _autonomous = autonomous;
    }

    public async Task OnGoalAsync(string args, CancellationToken ct)
    {
        args = args.Trim();
        if (args.Length == 0)
        {
            await ShowCurrentGoalAsync(ct);
            return;
        }

        var keyword = FirstWord(args);
        switch (keyword)
        {
            case "start":
                await ApplyAsync(() => _goals.StartAsync(ct), "Goal started.", ct);
                return;
            case "pause":
                await ApplyAsync(() => _goals.PauseAsync(ct), "Goal paused.", ct);
                return;
            case "resume":
                await ApplyAsync(() => _goals.ResumeAsync(ct), "Goal resumed.", ct);
                return;
            case "complete":
                await ApplyAsync(() => _goals.CompleteAsync(ct), "Goal completed.", ct);
                return;
            case "clear":
                await ApplyAsync(() => _goals.ClearAsync(ct), "Goal cleared.", ct);
                return;
            case "progress":
                await ApplyAsync(() => _goals.SetProgressAsync(Rest(args, "progress"), ct), "Progress updated.", ct);
                return;
            case "budget":
                if (!long.TryParse(Rest(args, "budget").Trim(), out var tokens))
                {
                    await _ui.NotifyAsync("/goal budget <tokens> (0 = unlimited)", ExtensionUiSeverity.Warning, ct);
                    return;
                }
                await ApplyAsync(() => _goals.SetBudgetAsync(tokens, ct), "Budget updated.", ct);
                return;
            default:
                await ApplyAsync(() => _goals.SetGoalAsync(args, null, ct), "Goal set.", ct);
                return;
        }
    }

    public async Task OnHeartbeatAsync(string args, CancellationToken ct)
    {
        args = args.Trim();
        if (args.Length == 0 || args == "status")
        {
            var s = _heartbeat.State;
            var text = s is null or { Enabled: false } ? "heartbeat: disabled"
                : $"heartbeat: enabled every {s.IntervalMinutes} min";
            await _ui.NotifyAsync(text, ExtensionUiSeverity.Info, ct);
            return;
        }
        if (args == "off")
        {
            await _heartbeat.DisableAsync(ct);
            await _ui.NotifyAsync("heartbeat disabled.", ExtensionUiSeverity.Info, ct);
            return;
        }
        if (int.TryParse(args, out var minutes))
        {
            await _heartbeat.SetAsync(minutes, ct);
            await _ui.NotifyAsync($"heartbeat enabled every {minutes} min.", ExtensionUiSeverity.Info, ct);
            return;
        }
        await _ui.NotifyAsync("/heartbeat <minutes> | off", ExtensionUiSeverity.Warning, ct);
    }

    public async Task OnCronAsync(string args, CancellationToken ct)
    {
        args = args.Trim();
        if (args.Length == 0 || args == "list")
        {
            var jobs = _scheduler.Jobs;
            if (jobs.Count == 0)
            {
                await _ui.NotifyAsync("cron: no jobs.", ExtensionUiSeverity.Info, ct);
                return;
            }
            var lines = jobs.Select(j => $"{j.Name} [{j.Id[..8]}] cron={j.Cron} next={j.NextRunAt:u} enabled={j.Enabled}");
            await _ui.NotifyAsync("cron jobs:\n" + string.Join("\n", lines), ExtensionUiSeverity.Info, ct);
            return;
        }

        var op = FirstWord(args);
        var rest = Rest(args, op);
        switch (op)
        {
            case "add":
                await CronAddAsync(rest, ct);
                return;
            case "remove":
                if (!await _scheduler.CancelJobAsync(rest.Trim(), ct))
                    await _ui.NotifyAsync($"cron: no job matching '{rest.Trim()}'.", ExtensionUiSeverity.Warning, ct);
                else
                    await _ui.NotifyAsync("cron: job removed.", ExtensionUiSeverity.Info, ct);
                return;
            case "enable":
            case "disable":
                {
                    var enabled = op == "enable";
                    if (!await _scheduler.SetJobEnabledAsync(rest.Trim(), enabled, ct))
                        await _ui.NotifyAsync($"cron: no job matching '{rest.Trim()}'.", ExtensionUiSeverity.Warning, ct);
                    else
                        await _ui.NotifyAsync($"cron: job {op}d.", ExtensionUiSeverity.Info, ct);
                    return;
                }
            default:
                await _ui.NotifyAsync("/cron add <name> \"<cron>\" \"<prompt>\" | remove|enable|disable <id|name>", ExtensionUiSeverity.Warning, ct);
                return;
        }
    }

    public async Task OnAutonomousAsync(string args, CancellationToken ct)
    {
        args = args.Trim();
        if (args == "stop")
        {
            await _autonomous.StopAsync(ct);
            await _ui.NotifyAsync("autonomous run aborted.", ExtensionUiSeverity.Info, ct);
            return;
        }

        var (command, _) = ParseAutonomousArgs(args);
        try
        {
            var result = await _autonomous.StartAsync(command, _goals.Goal?.Objective, ct);
            await _ui.NotifyAsync($"autonomous run started ({result.RunId[..8]}).", ExtensionUiSeverity.Info, ct);
        }
        catch (InvalidOperationException ex)
        {
            await _ui.NotifyAsync(ex.Message, ExtensionUiSeverity.Warning, ct);
        }
    }

    private static (AutonomousCommand command, string? message) ParseAutonomousArgs(string args)
    {
        int? maxTurns = null, timeout = null;
        long? maxTokens = null;
        var gates = new List<QualityGate>();
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var messageParts = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            switch (words[i])
            {
                case "--max-turns" when i + 1 < words.Length && int.TryParse(words[++i], out var t): maxTurns = t; break;
                case "--max-tokens" when i + 1 < words.Length && long.TryParse(words[++i], out var tk): maxTokens = tk; break;
                case "--timeout-min" when i + 1 < words.Length && int.TryParse(words[++i], out var tm): timeout = tm; break;
                case "--no-gates": gates = []; break;
                case "--gate" when i + 1 < words.Length:
                    gates.Add(new QualityGate(Guid.NewGuid().ToString(), words[++i], 60, 3));
                    break;
                default:
                    messageParts.Add(words[i]);
                    break;
            }
        }
        var message = messageParts.Count > 0 ? string.Join(' ', messageParts) : null;
        return (new AutonomousCommand(message, maxTurns, maxTokens, timeout, gates.Count == 0 ? null : gates), message);
    }

    private async Task CronAddAsync(string rest, CancellationToken ct)
    {
        var name = TakeQuotedOrWord(rest, out var afterName);
        var cron = TakeQuotedOrWord(afterName, out var afterCron);
        var prompt = TakeQuotedOrWord(afterCron, out _);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cron) || string.IsNullOrWhiteSpace(prompt))
        {
            await _ui.NotifyAsync("/cron add <name> \"<cron>\" \"<prompt>\"", ExtensionUiSeverity.Warning, ct);
            return;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var schedule = new CronSchedule(cron);
            var job = new ContinuityJob(Guid.NewGuid().ToString(), name, cron, prompt, true, now, schedule.Next(now));
            await _scheduler.AddJobAsync(job, ct);
            await _ui.NotifyAsync($"cron job '{name}' added.", ExtensionUiSeverity.Info, ct);
        }
        catch (FormatException ex)
        {
            await _ui.NotifyAsync($"cron: {ex.Message}", ExtensionUiSeverity.Error, ct);
        }
    }

    private async Task ShowCurrentGoalAsync(CancellationToken ct)
    {
        var goal = (await _goals.GetGoalAsync(ct)).Goal;
        if (goal is null)
        {
            await _ui.NotifyAsync("no goal set. Use /goal <objective>.", ExtensionUiSeverity.Info, ct);
            return;
        }
        var budget = goal.MaxTokens is { } m ? $"{m:N0}" : "unlimited";
        var text = $"goal: {goal.Objective}\nstatus: {goal.Status.ToString().ToLowerInvariant()}\ntokens: {goal.TokensUsed:N0}/{budget}"
            + (string.IsNullOrWhiteSpace(goal.Progress) ? string.Empty : $"\nprogress: {goal.Progress}");
        await _ui.NotifyAsync(text, ExtensionUiSeverity.Info, ct);
    }

    private async Task ApplyAsync(Func<Task<ContinuityGoalResult>> op, string ok, CancellationToken ct)
    {
        var result = await op();
        if (result.Goal is null)
            await _ui.NotifyAsync("no goal set.", ExtensionUiSeverity.Warning, ct);
        else
            await _ui.NotifyAsync(ok, ExtensionUiSeverity.Info, ct);
    }

    private static string FirstWord(string text)
    {
        var i = text.IndexOf(' ');
        return i < 0 ? text : text[..i];
    }

    private static string Rest(string text, string word)
    {
        var i = text.IndexOf(word, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        return text[(i + word.Length)..].Trim();
    }

    private static string TakeQuotedOrWord(string text, out string remainder)
    {
        text = text.Trim();
        remainder = string.Empty;
        if (text.Length == 0) return string.Empty;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) { remainder = string.Empty; return text[1..]; }
            remainder = text[(end + 1)..];
            return text[1..end];
        }
        var i = text.IndexOf(' ');
        if (i < 0) { remainder = string.Empty; return text; }
        remainder = text[(i + 1)..];
        return text[..i];
    }
}
