namespace PiSharp.Extensions;

public sealed record ExtensionDescriptor(
    string Id,
    string Name,
    string Version,
    string? Path = null,
    string? Description = null,
    string? SourceId = null)
{
    public string EffectiveSourceId => string.IsNullOrWhiteSpace(SourceId) ? CreateSourceId(Id) : SourceId;

    public static string CreateSourceId(string id)
        => $"extension:{NormalizeId(id)}";

    public static ExtensionDescriptor FromMetadata(ExtensionMetadataAttribute metadata, string? path = null)
        => new(metadata.Id, metadata.Name ?? metadata.Id, metadata.Version, path, metadata.Description, metadata.SourceId);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidOperationException("Extension id is required.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException($"Extension '{Id}' must have a name.");
        if (string.IsNullOrWhiteSpace(Version)) throw new InvalidOperationException($"Extension '{Id}' must have a version.");
    }

    private static string NormalizeId(string id)
        => string.Concat((id ?? string.Empty).Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')).ToLowerInvariant();
}
