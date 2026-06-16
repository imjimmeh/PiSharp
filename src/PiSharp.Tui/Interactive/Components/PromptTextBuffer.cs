namespace PiSharp.Tui.Interactive.Components;

internal static class PromptTextBuffer
{
    public static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static PromptTextMutation Insert(string text, int cursorOffset, string insertion)
    {
        text = Normalize(text);
        insertion = Normalize(insertion);
        var clampedCursorOffset = Math.Clamp(cursorOffset, 0, text.Length);
        return new PromptTextMutation(text.Insert(clampedCursorOffset, insertion), clampedCursorOffset + insertion.Length);
    }

    public static PromptTextMutation DeleteLeft(string text, int cursorOffset)
    {
        text = Normalize(text);
        var clampedCursorOffset = Math.Clamp(cursorOffset, 0, text.Length);
        return clampedCursorOffset == 0
            ? new PromptTextMutation(text, clampedCursorOffset)
            : new PromptTextMutation(text.Remove(clampedCursorOffset - 1, 1), clampedCursorOffset - 1);
    }

    public static PromptTextMutation DeleteRight(string text, int cursorOffset)
    {
        text = Normalize(text);
        var clampedCursorOffset = Math.Clamp(cursorOffset, 0, text.Length);
        return clampedCursorOffset >= text.Length
            ? new PromptTextMutation(text, clampedCursorOffset)
            : new PromptTextMutation(text.Remove(clampedCursorOffset, 1), clampedCursorOffset);
    }
}

internal readonly record struct PromptTextMutation(string Text, int CursorOffset);
