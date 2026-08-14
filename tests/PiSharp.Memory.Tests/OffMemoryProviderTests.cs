using PiSharp.Memory.Abstractions;
using PiSharp.Memory.Backends.Off;
using Xunit;

namespace PiSharp.Memory.Tests;

public sealed class OffMemoryProviderTests
{
    private readonly OffMemoryProvider _provider = new();

    [Fact]
    public void Identity_IsOffAndNonSemantic()
    {
        Assert.Equal("off", _provider.Id);
        Assert.False(_provider.SupportsSemanticSearch);
    }

    [Fact]
    public async Task Get_ReturnsNull()
    {
        Assert.Null(await _provider.GetAsync(MemoryScope.Project, "facts/any"));
    }

    [Fact]
    public async Task Put_IsANoOp()
    {
        await _provider.PutAsync(MemoryScope.Project, Record("facts/any"));
        Assert.Null(await _provider.GetAsync(MemoryScope.Project, "facts/any"));
    }

    [Fact]
    public async Task Delete_ReturnsFalse()
    {
        Assert.False(await _provider.DeleteAsync(MemoryScope.Project, "facts/any"));
    }

    [Fact]
    public async Task Update_ReturnsNull()
    {
        Assert.Null(await _provider.UpdateAsync(MemoryScope.Project, "facts/any", record => record with { Title = "x" }));
    }

    [Fact]
    public async Task List_Search_Recall_ReturnEmpty()
    {
        Assert.Empty(await _provider.ListAsync(MemoryScope.Project, new MemoryQuery()));
        Assert.Empty(await _provider.SearchAsync(MemoryScope.Project, "query"));
        Assert.Empty(await _provider.RecallAsync(MemoryScope.Project, new MemoryQuery(Text: "query")));
    }

    private static MemoryRecord Record(string key)
        => new(key, MemoryKind.Fact, "Title", "Content", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
