using System.Linq;
using PiSharp.Tui.Interactive;
using Terminal.Gui;
using Xunit;

namespace PiSharp.Tui.Tests;

public sealed class SidebarShortcutTests
{
    [Fact]
    public void Catalog_RegistersF6AndF7_AsGlobalShortcuts()
    {
        var keys = TuiBuiltInShortcutCatalog.Bindings.SelectMany(b => b.Keys).ToArray();

        Assert.Contains(Key.F6, keys);
        Assert.Contains(Key.F7, keys);
    }
}
