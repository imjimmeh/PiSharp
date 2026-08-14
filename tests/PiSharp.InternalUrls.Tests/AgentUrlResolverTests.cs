using System.Text.Json;
using PiSharp.Extensions;
using PiSharp.InternalUrls.Resolvers;
using Xunit;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Covers <c>agent://&lt;id&gt;</c> and <c>agent://&lt;id&gt;/&lt;field.path&gt;</c>
/// (§5.6): JSON round-trip, dotted/index field extraction via
/// <see cref="InternalUrlFieldPath"/>, missing fields → <c>NotFound</c>, and the
/// id-segment guard (no separators / "..").
/// </summary>
public sealed class AgentUrlResolverTests
{
    private static readonly JsonElement StubResult = JsonSerializer.Deserialize<JsonElement>("""
        {
          "findings": [
            { "id": 1, "path": "src/a.cs", "severity": "high" },
            { "id": 2, "path": "src/b.cs", "severity": "low" }
          ],
          "summary": "two findings",
          "count": 2
        }
        """);

    private static AgentUrlResolver CreateResolver(Func<string, JsonElement?>? lookup = null)
        => new(lookup ?? (id => id == "abc" ? StubResult : null));

    [Fact]
    public async Task ResolveAsync_WholeResult_ReturnsRawJson()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", "abc", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.NotNull(result.Content);
        using var document = JsonDocument.Parse(result.Content!);
        Assert.Equal("two findings", document.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ResolveAsync_DottedField_ReturnsScalar()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", "abc/summary", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("\"two findings\"", result.Content);
    }

    [Fact]
    public async Task ResolveAsync_IndexedArrayElement_ReturnsElement()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", "abc/findings.1.path", null), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("\"src/b.cs\"", result.Content);
    }

    [Fact]
    public async Task ResolveAsync_MissingField_ReturnsNotFound()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", "abc/nope", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
        Assert.Contains("nope", result.Error!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_OutOfRangeIndex_ReturnsNotFound()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", "abc/findings.9.path", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task ResolveAsync_UnknownId_ReturnsNotFound()
    {
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", "zzz", null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.NotFound, result.Error!.Kind);
    }

    [Theory]
    // TryParseTarget rejects hard separators/".."/absolute forms; IsPlainName
    // additionally rejects ids with characters outside [A-Za-z0-9._-] (e.g. a
    // space) that survive target parsing.
    [InlineData("a/../b")]
    [InlineData("a b/c")]
    [InlineData("..")]
    [InlineData("/abs")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task ResolveAsync_HostileId_IsBlocked(string target)
    {
        var lookupCalls = 0;
        var resolver = new AgentUrlResolver(_ =>
        {
            lookupCalls++;
            return StubResult;
        });

        var result = await resolver.ResolveAsync(new InternalUrlRequest("agent", target, null), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(InternalUrlErrorKind.TraversalBlocked, result.Error!.Kind);
        Assert.Equal(0, lookupCalls);
    }
}
