using PiSharp.InternalUrls.Services;
using Xunit;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Covers the <c>diff://</c> backing store (§5.7): per-file + last-diff
/// semantics, LRU capacity bound and eviction, and reset.
/// </summary>
public sealed class DiffLedgerTests
{
    [Fact]
    public void Record_ThenTryGetLatest_ReturnsMostRecent()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff-a");
        ledger.Record(@"C:\repo\b.cs", "diff-b");

        Assert.True(ledger.TryGetLatest(out var path, out var diff));
        Assert.Equal(@"C:\repo\b.cs", path);
        Assert.Equal("diff-b", diff);
    }

    [Fact]
    public void Record_ThenTryGetForPath_ReturnsThatFileDiff()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff-a");
        ledger.Record(@"C:\repo\b.cs", "diff-b");

        Assert.True(ledger.TryGetForPath(@"C:\repo\a.cs", out var diff));
        Assert.Equal("diff-a", diff);
        Assert.False(ledger.TryGetForPath(@"C:\repo\nope.cs", out _));
    }

    [Fact]
    public void Record_ReRecordSamePath_UpdatesInPlaceAndBecomesLatest()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff-a-v1");
        ledger.Record(@"C:\repo\b.cs", "diff-b");
        ledger.Record(@"C:\repo\a.cs", "diff-a-v2");

        Assert.Equal(2, ledger.Count);
        Assert.True(ledger.TryGetLatest(out var path, out var diff));
        Assert.Equal(@"C:\repo\a.cs", path);
        Assert.Equal("diff-a-v2", diff);
    }

    [Fact]
    public void Record_ExceedingCapacity_EvictsLeastRecentlyUsed()
    {
        var ledger = new DiffLedger(capacity: 2);
        ledger.Record(@"C:\repo\a.cs", "diff-a");
        ledger.Record(@"C:\repo\b.cs", "diff-b");
        ledger.Record(@"C:\repo\c.cs", "diff-c"); // evicts a.cs

        Assert.Equal(2, ledger.Count);
        Assert.False(ledger.TryGetForPath(@"C:\repo\a.cs", out _));
        Assert.True(ledger.TryGetForPath(@"C:\repo\b.cs", out _));
        Assert.True(ledger.TryGetForPath(@"C:\repo\c.cs", out _));
    }

    [Fact]
    public void Record_ReRecordPromotesLruOrder()
    {
        var ledger = new DiffLedger(capacity: 2);
        ledger.Record(@"C:\repo\a.cs", "diff-a");
        ledger.Record(@"C:\repo\b.cs", "diff-b");
        ledger.Record(@"C:\repo\a.cs", "diff-a-v2"); // a.cs promoted; b.cs is now LRU

        ledger.Record(@"C:\repo\c.cs", "diff-c"); // evicts b.cs

        Assert.True(ledger.TryGetForPath(@"C:\repo\a.cs", out _));
        Assert.False(ledger.TryGetForPath(@"C:\repo\b.cs", out _));
    }

    [Fact]
    public void Clear_RemovesAllDiffs()
    {
        var ledger = new DiffLedger();
        ledger.Record(@"C:\repo\a.cs", "diff-a");

        ledger.Clear();

        Assert.Equal(0, ledger.Count);
        Assert.False(ledger.TryGetLatest(out _, out _));
        Assert.False(ledger.TryGetForPath(@"C:\repo\a.cs", out _));
    }

    [Fact]
    public void Record_RejectsNullOrWhitespacePath()
    {
        var ledger = new DiffLedger();
        Assert.Throws<ArgumentException>(() => ledger.Record("", "diff"));
        Assert.Throws<ArgumentNullException>(() => ledger.Record(null!, "diff"));
    }
}
