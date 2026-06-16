using System.Text;
using PiSharp.Extensions;

namespace PiSharp.Tui.Interactive;

public static class TuiHotkeyText
{
    public static string RenderFromDescriptors(
        IReadOnlyList<TuiCommandDescriptor> descriptors,
        IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>> extensionShortcuts)
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
        foreach (var shortcut in extensionShortcuts.OrderBy(shortcut => shortcut.SourceId, StringComparer.Ordinal).ThenBy(shortcut => shortcut.Value.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var description = string.IsNullOrWhiteSpace(shortcut.Value.Description) ? "Extension shortcut" : shortcut.Value.Description;
            builder.AppendLine($"{shortcut.Value.Keys,-12} {description} ({shortcut.SourceId})");
        }

        return builder.ToString().TrimEnd();
    }

    public static string Render(
        IReadOnlyList<TuiKeybinding> builtIns,
        IReadOnlyList<OwnedExtensionRegistration<ExtensionShortcutRegistration>> extensionShortcuts)
        => RenderFromDescriptors(TuiKeybindings.CommandDescriptors, extensionShortcuts);
}
