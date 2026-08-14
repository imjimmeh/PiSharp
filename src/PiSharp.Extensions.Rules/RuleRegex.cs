using System.Text.RegularExpressions;

namespace PiSharp.Extensions.Rules;

/// <summary>
/// Regex construction and matching for stream-triggered rules (plan §5.1, §7):
/// compiled-per-rule, case-sensitive by default (<c>(?i)</c> opt-in via the pattern),
/// timeout-guarded at 250 ms so a pathological pattern never stalls the stream.
/// A timeout (or any other regex failure) yields <c>false</c> — no-match, never throws.
/// </summary>
public static class RuleRegex
{
    /// <summary>Per-rule regex match timeout (plan §7: <c>MatchTimeout = 250 ms</c>).</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static Regex Build(string pattern)
        => new(pattern, RegexOptions.None, MatchTimeout);

    public static bool Matches(Rule rule, string text)
        => Matches(rule.TriggerPattern, text);

    public static bool Matches(string? pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try
        {
            var regex = new Regex(pattern, RegexOptions.None, MatchTimeout);
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
