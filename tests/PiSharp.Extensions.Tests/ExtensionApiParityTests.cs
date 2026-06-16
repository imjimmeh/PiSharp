using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class ExtensionApiParityTests
{
    [Fact]
    public void UnregisterBySourceRemovesAllParitySurfaces()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterCommand("extension:a", new ExtensionCommandRegistration("cmd", "desc", (_, _) => Task.CompletedTask));
        registry.RegisterShortcut("extension:a", new ExtensionShortcutRegistration("Ctrl+K", "desc", (_, _) => Task.CompletedTask));
        registry.RegisterFlag("extension:a", new ExtensionFlagRegistration("flag", "desc"));
        registry.RegisterMessageRenderer("extension:a", new ExtensionMessageRendererRegistration("renderer", ExtensionChatRowType.Assistant, _ => []));
        registry.RegisterMessageDecorator("extension:a", new ExtensionMessageDecoratorRegistration("decorator", ExtensionChatRowType.Assistant, (_, rows) => rows));

        registry.UnregisterBySource("extension:a");

        Assert.Empty(registry.Commands);
        Assert.Empty(registry.Shortcuts);
        Assert.Empty(registry.Flags);
        Assert.Empty(registry.Renderers);
        Assert.Empty(registry.Decorators);
    }

    [Fact]
    public void MessageRenderersAreKeyedByRowTypeWithOverridePolicy()
    {
        var registry = new ExtensionRegistry();
        registry.RegisterMessageRenderer("extension:a", new ExtensionMessageRendererRegistration("first", ExtensionChatRowType.Assistant, _ => [], ExtensionOverridePolicy.OverrideBuiltIn));

        Assert.Throws<InvalidOperationException>(() => registry.RegisterMessageRenderer("extension:b", new ExtensionMessageRendererRegistration("second", ExtensionChatRowType.Assistant, _ => [], ExtensionOverridePolicy.Reject)));

        registry.RegisterMessageRenderer("extension:b", new ExtensionMessageRendererRegistration("second", ExtensionChatRowType.Assistant, _ => [], ExtensionOverridePolicy.OverrideBuiltIn));

        var renderer = Assert.Single(registry.Renderers);
        Assert.Equal("second", renderer.Value.Name);
        Assert.Equal(ExtensionChatRowType.Assistant, renderer.Value.RowType);
    }

    [Fact]
    public void LegacyNameOnlyMessageRenderersRemainInertRegistrations()
    {
        var registry = new ExtensionRegistry();

        registry.RegisterMessageRenderer("extension:a", new ExtensionMessageRendererRegistration("legacy"));
        registry.RegisterMessageRenderer("extension:b", new ExtensionMessageRendererRegistration("legacy"));

        Assert.Single(registry.Renderers);
        Assert.Null(registry.Renderers[0].Value.Handler);
        Assert.Equal(ExtensionChatRowType.Unknown, registry.Renderers[0].Value.RowType);
    }

    [Fact]
    public void ExtensionApiExposesAllGoldenRegistrationSurfaces()
    {
        var members = typeof(IExtensionApi).GetMembers().Select(member => member.Name).ToArray();

        Assert.Contains("RegisterCommand", members);
        Assert.Contains("RegisterShortcut", members);
        Assert.Contains("RegisterFlag", members);
        Assert.Contains("RegisterMessageRenderer", members);
        Assert.Contains("RegisterMessageDecorator", members);
        Assert.Contains("RemoveProvider", members);
        Assert.Contains("Events", members);
        Assert.Contains("Session", members);
        Assert.Contains("Tools", members);
        Assert.Contains("Model", members);
        Assert.Contains("Prompt", members);
        Assert.Contains("Ui", members);
    }

    [Fact]
    public void ExtensionUiExposesVisibleTuiPrimitives()
    {
        var members = typeof(IExtensionUi).GetMembers().Select(member => member.Name).ToArray();

        Assert.Contains("SetStatusAsync", members);
        Assert.Contains("SetWidgetAsync", members);
        Assert.Contains("SetTitleAsync", members);
        Assert.Contains("GetEditorTextAsync", members);
        Assert.Contains("SetEditorTextAsync", members);
        Assert.Contains("SetFooterAsync", members);
        Assert.Contains("SetHeaderAsync", members);
        Assert.Equal("above-editor", new ExtensionWidgetState("text", "hello").Placement);
    }
}
