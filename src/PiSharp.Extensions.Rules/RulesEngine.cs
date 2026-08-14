using System.Text.RegularExpressions;
using System.Text;
using PiSharp.Abstractions.Messages;
using PiSharp.Agent.Core;
using PiSharp.Agent.Core.Streaming;

namespace PiSharp.Extensions.Rules;

/// <summary>
/// The P10 rules engine (plan §5.5): owns the deduped, priority-ordered rule set and
/// implements the stream-delta interceptor surface. Discovery pulls every registered
/// provider's rules (via an <see cref="IExtensionRuleApi"/>-backed source) and applies
/// first-wins dedup by <see cref="Rule.Name"/> (higher <see cref="Rule.Priority"/> wins;
/// ties keep the first discovered). Mid-stream, it matches only the visible text deltas
/// and fires a <see cref="StreamDeltaAction.Retry"/> on the token boundary where a
/// stream-triggered rule's pattern first matches. <see cref="PrepareMessagesAsync"/>
/// re-injects always-apply rules (incl. sticky RULES.md) near the end of the message list
/// on every request, which is what makes them survive compaction by construction.
/// </summary>
public sealed class RulesEngine : IStreamDeltaInterceptor
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<Rule>>> _ruleSource;
    private readonly RulesOptions _options;
    private readonly StringComparer _nameComparer;
    private readonly StringBuilder _accumulated = new();
    private readonly Dictionary<string, Regex> _compiled = new(StringComparer.Ordinal);
    private IReadOnlyList<Rule>? _rules;

    /// <param name="ruleSource">Returns all rules across registered providers
    /// (e.g. <c>api.Rules.GetAllRulesAsync</c>), pre-dedup; the engine dedups + orders.</param>
    public RulesEngine(
        Func<CancellationToken, Task<IReadOnlyList<Rule>>> ruleSource,
        RulesOptions options,
        StringComparer? nameComparer = null)
    {
        _ruleSource = ruleSource ?? throw new ArgumentNullException(nameof(ruleSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nameComparer = nameComparer ?? StringComparer.Ordinal;
    }

    /// <summary>True when <c>--no-rules</c> disabled the engine; all interception becomes a no-op.</summary>
    public bool Disabled => _options.Disabled;

    /// <summary>The last discovered, deduped, priority-ordered rule set (empty before discovery).</summary>
    public IReadOnlyList<Rule> Rules => _rules ?? [];

    /// <summary>The accumulated visible text from the current stream attempt (matching only).</summary>
    public string AccumulatedText => _accumulated.ToString();

    /// <summary>Re-discovers rules from <see cref="_ruleSource"/> and stores the deduped result.</summary>
    public async Task<IReadOnlyList<Rule>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var all = await _ruleSource(cancellationToken);
        _rules = DedupeAndOrder(all);
        return _rules;
    }

    /// <summary>Returns the discovered rules, discovering on first use when needed.</summary>
    public async Task<IReadOnlyList<Rule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        if (_rules is null) await DiscoverAsync(cancellationToken);
        return _rules ?? [];
    }

    /// <summary>First-wins dedup by Name (higher priority wins; ties keep the first), then
    /// ordered by descending priority, then name (deterministic).</summary>
    public IReadOnlyList<Rule> DedupeAndOrder(IReadOnlyList<Rule> rules)
    {
        var winners = new Dictionary<string, Rule>(_nameComparer);
        foreach (var rule in rules)
        {
            if (winners.TryGetValue(rule.Name, out var existing))
            {
                if (rule.Priority > existing.Priority)
                    winners[rule.Name] = rule;
            }
            else
            {
                winners[rule.Name] = rule;
            }
        }
        return winners.Values
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<StreamDeltaDecision?> InterceptDeltaAsync(
        StreamDeltaContext context,
        CancellationToken cancellationToken = default)
    {
        if (Disabled) return null;

        var rules = await GetRulesAsync(cancellationToken);
        if (rules.Count == 0) return null;

        // Match visible text deltas only; thinking/tool deltas are ignored for matching.
        if (context.Delta is not AssistantMessageEvent.TextDelta textDelta)
            return null;

        _accumulated.Append(textDelta.Delta);
        var visible = _accumulated.ToString();

        foreach (var rule in rules)
        {
            if (!rule.IsStreamTrigger) continue;
            if (Matches(rule, visible))
            {
                return new StreamDeltaDecision(
                    StreamDeltaAction.Retry,
                    SystemReminder: rule.Content,
                    Reason: $"rule:{rule.Name}:{rule.TriggerPattern}");
            }
        }

        return null;
    }

    public Task<IReadOnlyList<AgentMessage>> PrepareMessagesAsync(
        IReadOnlyList<AgentMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        // Every stream request (first attempt and each retry) starts fresh for matching.
        _accumulated.Clear();

        if (Disabled) return Task.FromResult(messages);

        return PrepareAsync(messages, cancellationToken);
    }

    private async Task<IReadOnlyList<AgentMessage>> PrepareAsync(
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken)
    {
        var rules = await GetRulesAsync(cancellationToken);

        // File always-apply rules first, then user sticky, then project sticky last so the
        // project RULES@project reminder sits closest to the current turn.
        var ordered = rules
            .Where(rule => rule.ApplyMode == RuleApplyMode.Always && rule.Name is not StickyRulesProvider.UserRuleName and not StickyRulesProvider.ProjectRuleName)
            .Concat(rules.Where(rule => rule.Name == StickyRulesProvider.UserRuleName))
            .Concat(rules.Where(rule => rule.Name == StickyRulesProvider.ProjectRuleName))
            .ToArray();

        if (ordered.Length == 0) return messages;

        var result = new List<AgentMessage>(messages);
        foreach (var rule in ordered)
        {
            var reminder = IsSticky(rule)
                ? StickyRulesInjector.Format(rule)
                : rule.Content;
            result.Add(AgentMessages.User(reminder));
        }
        return result;
    }

    private static bool IsSticky(Rule rule)
        => rule.Name is StickyRulesProvider.UserRuleName or StickyRulesProvider.ProjectRuleName
           && rule.ApplyMode == RuleApplyMode.Always;

    /// <summary>Compiled-per-rule, timeout-guarded match (plan §7); a timeout is a no-match.</summary>
    private bool Matches(Rule rule, string text)
    {
        if (rule.TriggerPattern is null) return false;
        if (!_compiled.TryGetValue(rule.TriggerPattern, out var regex))
        {
            regex = RuleRegex.Build(rule.TriggerPattern);
            _compiled[rule.TriggerPattern] = regex;
        }
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
