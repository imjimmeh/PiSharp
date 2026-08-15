using System.Text;
using PiSharp.Extensions;
using PiSharp.Tui.Interactive;

namespace PiSharp.Tui.Interactive;

public static class TuiHotkeyText
{
    private readonly record struct ExtensionShortcutEntry(string SourceId, string Keys, string Description);

    public static string RenderFromDescriptors(
        IReadOnlyList<TuiCommandDescriptor> descriptors,
        IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>> extensionShortcuts)
        => RenderCore(descriptors, extensionShortcuts.Select(static shortcut =>
            new ExtensionShortcutEntry(shortcut.SourceId, shortcut.Value.Keys, shortcut.Value.Description)).ToList());

    /// <summary>
    /// Renders the hotkey help from already-parsed extension shortcut bindings, so callers that
    /// keep a non-blocking cache of bindings (see <see cref="TuiShortcutController"/>) never need to
    /// re-read the remote shortcut source on the UI thread just to render help text.
    /// </summary>
    public static string RenderFromBindings(
        IReadOnlyList<TuiCommandDescriptor> descriptors,
        IReadOnlyList<TuiExtensionShortcutBinding> bindings)
        => RenderCore(descriptors, bindings.Select(static binding =>
            new ExtensionShortcutEntry(binding.SourceId, binding.Keys, binding.Description)).ToList());

    private static string RenderCore(
        IReadOnlyList<TuiCommandDescriptor> descriptors,
        IReadOnlyList<ExtensionShortcutEntry> extensionShortcuts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Built-in shortcuts:");
        foreach (var descriptor in descriptors)
        {
            var slashHint = descriptor.SlashCommand is not null ? $" ({descriptor.SlashCommand})" : string.Empty;
            builder.AppendLine($"{descriptor.DefaultKeys,-12} {descriptor.Description}{slashHint}");
        }

        if (extensionShortcuts.Count == 0) return builder.ToString().TrimEnd();

        builder.AppendLine();
        builder.AppendLine("Extension shortcuts:");
        foreach (var shortcut in extensionShortcuts
            .OrderBy(shortcut => shortcut.SourceId, StringComparer.Ordinal)
            .ThenBy(shortcut => shortcut.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var description = string.IsNullOrWhiteSpace(shortcut.Description) ? "Extension shortcut" : shortcut.Description;
            builder.AppendLine($"{shortcut.Keys,-12} {description} ({shortcut.SourceId})");
        }

        return builder.ToString().TrimEnd();
    }

    public static string Render(
        IReadOnlyList<TuiKeybinding> builtIns,
        IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>> extensionShortcuts)
        => RenderFromDescriptors(TuiKeybindings.CommandDescriptors, extensionShortcuts);
}
