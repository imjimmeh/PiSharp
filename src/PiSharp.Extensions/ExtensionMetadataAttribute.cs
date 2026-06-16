namespace PiSharp.Extensions;

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ExtensionMetadataAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public string? Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? Description { get; init; }
    public string? SourceId { get; init; }
}
