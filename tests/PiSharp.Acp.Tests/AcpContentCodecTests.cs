using System.Text.Json;
using Xunit;

namespace PiSharp.Acp.Tests;

/// <summary>
/// Codec round-trips and rejection rules (plan §5.7 / §10). Parses <c>params.prompt</c>
/// content blocks into typed records then decodes them into prompt text + images.
/// </summary>
public sealed class AcpContentCodecTests
{
    private static IReadOnlyList<AcpContentBlock> Parse(string json) => AcpContentCodec.Parse(AcpTestRuntime.JsonEl(json));

    [Fact]
    public void Parse_TextBlock_DecodesToTextOnly()
    {
        var blocks = Parse("""[{"type":"text","text":"hello world"}]""");
        var decoded = AcpContentCodec.Decode(blocks);
        Assert.Equal("hello world", decoded.Text);
        Assert.Null(decoded.Images);
        Assert.IsType<AcpTextBlock>(blocks[0]);
    }

    [Fact]
    public void Parse_ImageBlock_DecodesToImageContent()
    {
        var blocks = Parse("""[{"type":"image","mimeType":"image/png","data":"BASE64"}]""");
        var decoded = AcpContentCodec.Decode(blocks);
        Assert.Equal(string.Empty, decoded.Text);
        var image = Assert.Single(decoded.Images);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("BASE64", image.Data);
    }

    [Fact]
    public void Parse_ResourceLinkBlock_FlattensToTextReference()
    {
        var blocks = Parse("""[{"type":"resource_link","uri":"file://x.txt","name":"x"}]""");
        var decoded = AcpContentCodec.Decode(blocks);
        Assert.Equal("[Attached resource: file://x.txt]", decoded.Text);
        Assert.Null(decoded.Images);
    }

    [Fact]
    public void Decode_MixedBlocks_JoinsTextAndCollectsImages()
    {
        var blocks = Parse(
            """[{"type":"text","text":"hi"},{"type":"image","mimeType":"image/png","data":"BB"},{"type":"resource_link","uri":"file://y"}]""");
        var decoded = AcpContentCodec.Decode(blocks);
        Assert.Equal("hi\n[Attached resource: file://y]", decoded.Text);
        var image = Assert.Single(decoded.Images);
        Assert.Equal("image/png", image.MediaType);
    }

    [Fact]
    public void Parse_ResourceBlock_RejectedAsInvalidParams()
    {
        var ex = Assert.Throws<AcpRpcException>(() => Parse("""[{"type":"resource","uri":"file://x"}]"""));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_AudioBlock_RejectedAsInvalidParams()
    {
        var ex = Assert.Throws<AcpRpcException>(() => Parse("""[{"type":"audio","mimeType":"audio/wav","data":"AA"}]"""));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_UnknownBlockType_RejectedAsInvalidParams()
    {
        var ex = Assert.Throws<AcpRpcException>(() => Parse("""[{"type":"mystery"}]"""));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_MissingTextField_Rejected()
    {
        var ex = Assert.Throws<AcpRpcException>(() => Parse("""[{"type":"text"}]"""));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_ImageMissingData_Rejected()
    {
        var ex = Assert.Throws<AcpRpcException>(() => Parse("""[{"type":"image","mimeType":"image/png"}]"""));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_NonObjectBlock_Rejected()
    {
        var ex = Assert.Throws<AcpRpcException>(() => AcpContentCodec.Parse(AcpTestRuntime.JsonEl("""["just-a-string"]""")));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_NonArrayPrompt_RejectedAsInvalidParams()
    {
        var ex = Assert.Throws<AcpRpcException>(() => AcpContentCodec.Parse(AcpTestRuntime.JsonEl(""""{"type":"text","text":"x"}"""")));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public void Parse_BlockMissingType_Rejected()
    {
        var ex = Assert.Throws<AcpRpcException>(() => Parse("""[{"text":"x"}]"""));
        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
    }
}
