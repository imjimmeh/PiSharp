namespace PiSharp.Git;

/// <summary>
/// Parser for <c>git status --porcelain=v1 -z --untracked-files=all</c> output.
///
/// NUL-delimited record layout (verified against git 2.45, porcelain v1 with -z):
/// <list type="bullet">
/// <item>plain entry: <c>XY PATH\0</c> — XY is the two status columns followed by a space;</item>
/// <item>rename/copy entry: <c>XY NEW\0OLD\0</c> — the destination path comes FIRST in
/// porcelain -z output (the human-readable <c>XY old -&gt; new</c> form and
/// <c>git diff --name-status -z</c> use the opposite order).</item>
/// </list>
/// The returned items carry raw status codes and paths; classification/scoring is applied
/// by <see cref="ChangeClassifier"/>.
/// </summary>
public static class PorcelainParser
{
    public static IReadOnlyList<ChangeItem> Parse(string output)
    {
        var items = new List<ChangeItem>();
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 3)
            {
                // Too short for "XY PATH" (porcelain XY is exactly two columns + a space).
                continue;
            }

            // Porcelain v1: XY is two fixed-width columns, then one space, then the path.
            // Codes with a leading/trailing space (e.g. " D" worktree delete, "M " staged)
            // must not be split with IndexOf(' '), which finds the status-column space.
            var code = token[..2];
            var path = token[3..];
            if (path.Length == 0)
            {
                continue;
            }

            if (code[0] is 'R' or 'C')
            {
                var renameSource = i + 1 < tokens.Length ? tokens[i + 1] : null;
                i++;
                items.Add(new ChangeItem(
                    Path: path,
                    Status: code,
                    Category: ChangeCategory.Other,
                    Score: 0,
                    IsRename: true,
                    RenameSource: string.IsNullOrEmpty(renameSource) ? null : renameSource));
            }
            else
            {
                items.Add(new ChangeItem(path, code, ChangeCategory.Other, 0, IsRename: false, RenameSource: null));
            }
        }

        return items;
    }

    /// <summary>
    /// True for porcelain codes that indicate an unresolved merge conflict.
    /// Unmerged entries have 'U' in X or Y, or are one of the seven two-way codes
    /// (DD, AU, UD, UA, DU, AA, UU).
    /// </summary>
    public static bool IsUnmerged(string code)
    {
        if (code.Length == 0)
        {
            return false;
        }

        if (code[0] == 'U' || (code.Length > 1 && code[1] == 'U'))
        {
            return true;
        }

        return code is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";
    }
}
