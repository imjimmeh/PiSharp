using Terminal.Gui;

namespace PiSharp.Tui.Interactive.Input;

internal sealed class ExtensionCustomUiInputCapture(ExtensionUiBridgeHost bridge) : ITuiInputCapture
{
    public string Name => "extension-custom-ui";

    public bool TryHandleKey(Key key)
        => bridge.TryHandleCustomUiKey(key);
}
