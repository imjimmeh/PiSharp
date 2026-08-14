using System.Text.Json;

namespace PiSharp.Acp;

/// <summary>
/// ACP v1 content block in a <c>session/prompt</c> (plan §4.3). <c>text</c>, <c>image</c>
/// and <c>resource_link</c> are supported; <c>resource</c> and <c>audio</c> are rejected
/// because they are not advertised in <c>promptCapabilities</c>.
/// </summary>
public abstract record AcpContentBlock;

public sealed record AcpTextBlock(string Text) : AcpContentBlock;

public sealed record AcpImageBlock(string MimeType, string Data) : AcpContentBlock;

public sealed record AcpResourceLinkBlock(string Uri, string? Name = null) : AcpContentBlock;

public static class AcpContentBlockParser
{
    /// <summary>
    /// Parses a single ACP content-block JSON object. Returns <c>false</c> with an explanatory
    /// <paramref name="error"/> for malformed objects and for <c>resource</c>/<c>audio</c>/unknown
    /// block types (which produce a JSON-RPC <c>invalid_params</c>).
    /// </summary>
    public static bool TryParse(JsonElement block, out AcpContentBlock? parsed, out string? error)
    {
        parsed = null;
        error = null;
        if (block.ValueKind != JsonValueKind.Object)
        {
            error = "content block must be an object";
            return false;
        }

        if (!block.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
        {
            error = "content block must have a string 'type'";
            return false;
        }

        switch (typeProp.GetString())
        {
            case "text":
                if (!TryString(block, "text", out var text) || text is null)
                {
                    error = "text block requires a 'text' string";
                    return false;
                }
                parsed = new AcpTextBlock(text);
                return true;

            case "image":
                if (!TryString(block, "mimeType", out var mimeType) || mimeType is null
                    || !TryString(block, "data", out var data) || data is null)
                {
                    error = "image block requires 'mimeType' and 'data'";
                    return false;
                }
                parsed = new AcpImageBlock(mimeType, data);
                return true;

            case "resource_link":
                if (!TryString(block, "uri", out var uri) || uri is null)
                {
                    error = "resource_link block requires a 'uri' string";
                    return false;
                }
                TryString(block, "name", out var name);
                parsed = new AcpResourceLinkBlock(uri, name);
                return true;

            case "resource":
                error = "resource blocks are not supported (not advertised)";
                return false;

            case "audio":
                error = "audio blocks are not supported (not advertised)";
                return false;

            default:
                error = $"unsupported content block type '{typeProp.GetString()}'";
                return false;
        }
    }

    private static bool TryString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString();
        return true;
    }
}
