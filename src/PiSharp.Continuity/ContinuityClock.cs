using PiSharp.Continuity.Contracts;

namespace PiSharp.Continuity;

/// <summary>
/// Injectable time source for the continuity services — the fake-clock seam
/// that the scheduler unit tests drive.
/// </summary>
public sealed class ContinuityClock
{
    private readonly Func<DateTimeOffset> _now;

    public ContinuityClock(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public DateTimeOffset UtcNow => _now().ToUniversalTime();
}
