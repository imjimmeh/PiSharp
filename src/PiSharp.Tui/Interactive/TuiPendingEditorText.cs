using PiSharp.Tui.Interactive.Components;

namespace PiSharp.Tui.Interactive;

internal static class TuiPendingEditorText
{
    public static TuiRenderState Apply(TuiRenderState state, PromptEditor prompt)
    {
        if (state.EditorText is null) return state;

        if (!string.Equals(prompt.PromptText, state.EditorText, StringComparison.Ordinal))
        {
            prompt.SetPromptText(state.EditorText);
        }

        return state.SetEditorText(null);
    }
}
