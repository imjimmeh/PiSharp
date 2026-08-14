using System.Text.Json;
using PiSharp.InternalUrls;
using Xunit;

namespace PiSharp.InternalUrls.Tests;

/// <summary>
/// Dotted/index path navigation over a <see cref="JsonElement"/> (§5.6
/// <c>agent://&lt;id&gt;/&lt;field.path&gt;</c>), including array-index and
/// bracket-suffix forms.
/// </summary>
public sealed class InternalUrlFieldPathTests
{
    private static readonly JsonElement Doc = JsonSerializer.Deserialize<JsonElement>("""
        {
          "name": "agent-a",
          "tags": ["x", "y"],
          "findings": [
            { "id": 1, "path": "src/a.cs" }
          ],
          "nested": { "deep": { "value": 42 } }
        }
        """);

    [Fact]
    public void TrySelect_EmptyPath_ReturnsRoot()
    {
        Assert.True(InternalUrlFieldPath.TrySelect(Doc, "", out var selected));
        Assert.Equal(JsonValueKind.Object, selected.ValueKind);
    }

    [Fact]
    public void TrySelect_DottedObjectPath_ReturnsValue()
    {
        Assert.True(InternalUrlFieldPath.TrySelect(Doc, "nested.deep.value", out var selected));
        Assert.Equal(42, selected.GetInt32());
    }

    [Fact]
    public void TrySelect_ArrayIndex_ReturnsElement()
    {
        Assert.True(InternalUrlFieldPath.TrySelect(Doc, "findings.0.path", out var selected));
        Assert.Equal("src/a.cs", selected.GetString());
    }

    [Fact]
    public void TrySelect_BracketSuffix_ReturnsElement()
    {
        Assert.True(InternalUrlFieldPath.TrySelect(Doc, "tags[1]", out var selected));
        Assert.Equal("y", selected.GetString());
    }

    [Fact]
    public void TrySelect_MissingProperty_ReturnsFalse()
    {
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "nope", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "nested.deep.nope", out _));
    }

    [Fact]
    public void TrySelect_OutOfRangeOrNegativeIndex_ReturnsFalse()
    {
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "findings.5", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "findings.-1", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "tags[9]", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "tags[-1]", out _));
    }

    [Fact]
    public void TrySelect_MalformedPath_ReturnsFalse()
    {
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "tags[]", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "tags[abc]", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "a..b", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, ".leading", out _));
    }

    [Fact]
    public void TrySelect_IndexOnNonArrayOrNonObject_ReturnsFalse()
    {
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "name.0", out _));
        Assert.False(InternalUrlFieldPath.TrySelect(Doc, "tags.0.path", out _));
    }
}
