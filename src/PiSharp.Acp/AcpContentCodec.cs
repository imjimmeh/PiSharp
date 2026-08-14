using System.Text;
using System.Text.Json;
using PiSharp.Abstractions.Messages;

namespace PiSharp.Acp;

/// <summary>Decoded prompt inputs ready for <c>SubmitPromptAsync</c> (plan §5.7).</summary>
public sealed record AcpPromptInput(string Text, IReadOnlyList<ImageContent>? Images);

/// <summary>
/// Maps ACP prompt content blocks onto PiSharp prompt text and <see cref="ImageContent"/>
/// (§5.7). <c>text</c> blocks are joined with newlines; <c>image</c> blocks become
/// <see cref="ImageContent"/>; <c>resource_link</c> blocks are flattened to a text reference line.
/// </summary>
public static class AcpContentCodec
{
    /// <summary>
    /// Parses the <c>params.prompt</c> array into typed content blocks. Throws
    /// <see cref="AcpRpcException"/> (<c>invalid_params</c>) for malformed or unsupported blocks.
    /// </summary>
    public static IReadOnlyList<AcpContentBlock> Parse(JsonElement promptArray)
    {
        if (promptArray.ValueKind != JsonValueKind.Array)
            throw AcpRpcException.InvalidParams("params.prompt must be an array of content blocks");

        var blocks = new List<AcpContentBlock>();
        foreach (var element in promptArray.EnumerateArray())
        {
            if (!AcpContentBlockParser.TryParse(element, out var parsed, out var error) || parsed is null)
                throw AcpRpcException.InvalidParams(error ?? "invalid content block");
            blocks.Add(parsed);
        }

        return blocks;
    }

    /// <summary>Decodes parsed content blocks into prompt text and images.</summary>
    public static AcpPromptInput Decode(IReadOnlyList<AcpContentBlock> blocks)
    {
        var text = new StringBuilder();
        var images = new List<ImageContent>();

        foreach (var block in blocks)
        {
            switch (block)
            {
                case AcpTextBlock textBlock:
                    if (text.Length > 0) text.Append('\n');
                    text.Append(textBlock.Text);
                    break;

                case AcpImageBlock imageBlock:
                    images.Add(new ImageContent(imageBlock.MimeType, imageBlock.Data));
                    break;

                case AcpResourceLinkBlock link:
                    if (text.Length > 0) text.Append('\n');
                    text.Append($"[Attached resource: {link.Uri}]");
                    break;
            }
        }

        return new AcpPromptInput(text.ToString(), images.Count == 0 ? null : images);
    }
}
