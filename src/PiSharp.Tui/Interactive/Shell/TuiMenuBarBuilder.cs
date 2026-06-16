using System;
using System.Collections.Generic;
using System.Linq;
using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Shell;

/// <summary>Builds the MenuBar's MenuBarItem[] from built-in menus plus extension-registered entries.</summary>
public static class TuiMenuBarBuilder
{
    public static MenuBarItem[] Build(IReadOnlyList<TuiMenuEntry> customMenus, Action<string> invokeCommand)
    {
        var menus = new List<MenuBarItem>
        {
            new("_File", new MenuItem[]
            {
                new("_Quit", "Exit Pi", () => invokeCommand("quit")),
            }),
            new("_View", new MenuItem[]
            {
                new("Toggle _Left Sidebar", "F6", () => invokeCommand("toggle-left-sidebar")),
                new("Toggle _Right Sidebar", "F7", () => invokeCommand("toggle-right-sidebar")),
            }),
            new("_Help", new MenuItem[]
            {
                new("_Commands", "Show /help", () => invokeCommand("help")),
            }),
        };

        foreach (var group in customMenus.GroupBy(entry => entry.Menu, StringComparer.Ordinal))
        {
            var children = group
                .Select(entry => new MenuItem(entry.Label, entry.Shortcut ?? string.Empty, () => invokeCommand(entry.Command)))
                .ToArray();

            var existing = menus.FirstOrDefault(menu =>
                string.Equals(menu.Title.Replace("_", string.Empty), group.Key, StringComparison.Ordinal));

            if (existing is null)
            {
                menus.Add(new MenuBarItem(group.Key, children));
            }
            else
            {
                // Terminal.Gui v2.0.0: rebuild MenuBarItem with combined children
                var existingChildren = existing.Children?.OfType<MenuItem>().ToArray() ?? [];
                var newChildren = existingChildren.Concat(children).ToArray();
                var index = menus.IndexOf(existing);
                menus[index] = new MenuBarItem(existing.Title, newChildren);
            }
        }

        return menus.ToArray();
    }
}
