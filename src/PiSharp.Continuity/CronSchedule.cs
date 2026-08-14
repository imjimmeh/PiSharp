namespace PiSharp.Continuity;

/// <summary>
/// v1 cron grammar: <c>minute hour day-of-month month day-of-week</c>. Fields
/// support <c>*</c>, <c>*/n</c>, <c>a-b</c>, and <c>a,b</c>; aliases
/// <c>@hourly</c> (0 * * * *), <c>@daily</c> (0 0 * * *), <c>@weekly</c>
/// (0 0 * * 0). Pure and immutable: <see cref="Next"/> computes the next
/// occurrence strictly after a given instant. DateTime/dom-dow combination uses
/// the standard cron union rule when both day fields are restricted.
/// </summary>
public sealed class CronSchedule
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;
    private readonly string _expression;

    private const int MaxLookaheadDays = 400;

    public string Expression => _expression;

    public CronSchedule(string expression)
    {
        _expression = expression;
        var parts = ExpandAlias(expression.Trim());
        var tokens = parts.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 5)
            throw new FormatException($"Cron expression '{expression}' must have exactly 5 fields (minute hour day-of-month month day-of-week).");

        _minutes = ParseField(tokens[0], 0, 59, "minute");
        _hours = ParseField(tokens[1], 0, 23, "hour");
        _daysOfMonth = ParseField(tokens[2], 1, 31, "day-of-month");
        _months = ParseField(tokens[3], 1, 12, "month");
        _daysOfWeek = ParseField(tokens[4], 0, 7, "day-of-week");
        // Normalize day-of-week 7 → 0 (both are Sunday in the common cron dialect).
        if (_daysOfWeek.Remove(7)) _daysOfWeek.Add(0);

        _domRestricted = tokens[2] != "*";
        _dowRestricted = tokens[4] != "*";
    }

    private static string ExpandAlias(string expression) => expression.ToLowerInvariant() switch
    {
        "@hourly" => "0 * * * *",
        "@daily" or "@midnight" => "0 0 * * *",
        "@weekly" => "0 0 * * 0",
        _ => expression,
    };

    private static HashSet<int> ParseField(string text, int min, int max, string fieldName)
    {
        var result = new HashSet<int>();
        foreach (var rawPart in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                throw new FormatException($"Cron field '{fieldName}' contains an empty entry.");

            int start, end, step;
            if (part.Contains('/'))
            {
                var slash = part.Split('/', 2);
                var basePart = slash[0];
                if (!int.TryParse(slash[1], out step) || step <= 0)
                    throw new FormatException($"Cron field '{fieldName}' has invalid step '{slash[1]}'.");
                if (basePart == "*")
                {
                    start = min; end = max;
                }
                else
                {
                    (start, end) = ParseRange(basePart, min, max, fieldName);
                }
            }
            else if (part == "*")
            {
                start = min; end = max; step = 1;
            }
            else if (part.Contains('-'))
            {
                (start, end) = ParseRange(part, min, max, fieldName);
                step = 1;
            }
            else
            {
                start = end = ParseValue(part, min, max, fieldName);
                step = 1;
            }

            for (var v = start; v <= end; v += step)
                result.Add(v);
        }

        if (result.Count == 0)
            throw new FormatException($"Cron field '{fieldName}' produced an empty set.");
        return result;
    }

    private static (int start, int end) ParseRange(string text, int min, int max, string fieldName)
    {
        var dash = text.Split('-', 2);
        var start = ParseValue(dash[0], min, max, fieldName);
        var end = ParseValue(dash[1], min, max, fieldName);
        if (start > end)
            throw new FormatException($"Cron field '{fieldName}' range '{text}' is reversed.");
        return (start, end);
    }

    private static int ParseValue(string text, int min, int max, string fieldName)
    {
        if (!int.TryParse(text, out var value))
            throw new FormatException($"Cron field '{fieldName}' has invalid value '{text}'.");
        if (value < min || value > max)
            throw new FormatException($"Cron field '{fieldName}' value '{text}' is outside {min}..{max}.");
        return value;
    }

    private bool Matches(DateTimeOffset t)
    {
        if (!_minutes.Contains(t.Minute)) return false;
        if (!_hours.Contains(t.Hour)) return false;
        if (!_months.Contains(t.Month)) return false;

        var domOk = !_domRestricted || _daysOfMonth.Contains(t.Day);
        var dowOk = !_dowRestricted || _daysOfWeek.Contains((int)t.DayOfWeek);
        // Standard cron union rule: when both day fields are restricted, either
        // matching wins; otherwise both must match.
        if (_domRestricted && _dowRestricted) return domOk || dowOk;
        return domOk && dowOk;
    }

    /// <summary>
    /// Returns the next occurrence strictly after <paramref name="after"/>, in
    /// the same timezone as <paramref name="after"/> (UTC when passed a UTC
    /// instant). Throws when no occurrence exists within the lookahead horizon.
    /// </summary>
    public DateTimeOffset Next(DateTimeOffset after)
    {
        var t = after.AddMinutes(1);
        var horizon = after.AddDays(MaxLookaheadDays);
        while (t <= horizon)
        {
            if (Matches(t)) return t;
            t = t.AddMinutes(1);
        }
        throw new InvalidOperationException($"Cron expression '{_expression}' has no occurrence within {MaxLookaheadDays} days of {after:o}.");
    }
}
