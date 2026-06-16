using System.Threading.Tasks;
using PiSharp.Extensions;
using Xunit;

namespace PiSharp.Extensions.Tests;

public sealed class NoExtensionUiTests
{
    [Fact]
    public async Task RegisterMenuItemAsync_OnNoExtensionUi_IsNoOpAndDoesNotThrow()
    {
        IExtensionUi ui = NoExtensionUi.Instance;
        var item = new ExtensionMenuItem("View", "Toggle Left Sidebar", "toggle-left-sidebar");

        await ui.RegisterMenuItemAsync("ext-1", item);
    }
}
