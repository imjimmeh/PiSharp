namespace PiSharp.Extensions.Testing;

public static class FakeExtensionDescriptor
{
    public static ExtensionDescriptor Default { get; } = new ExtensionDescriptor(
        Id: "test.extension",
        Name: "Test Extension",
        Version: "0.0.0",
        Path: typeof(FakeExtensionDescriptor).Assembly.Location);
}
