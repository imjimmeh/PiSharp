using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tools.Edit;

public static class EditDiff
{
    public static string DetectLineEnding(string content)
    {
        var crlf = content.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = content.IndexOf('\n');
        if (lf == -1 || crlf == -1) return "\n";
        return crlf < lf ? "\r\n" : "\n";
    }

    public static string NormalizeToLf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string RestoreLineEndings(string text, string ending) => ending == "\r\n" ? text.Replace("\n", "\r\n") : text;

    public static BomStrippedContent StripBom(string content) => content.StartsWith('\ufeff') ? new BomStrippedContent("\ufeff", content[1..]) : new BomStrippedContent(string.Empty, content);

    public static string NormalizeForFuzzyMatch(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC)
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201A', '\'').Replace('\u201B', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u201E', '"').Replace('\u201F', '"')
            .Replace('\u2010', '-').Replace('\u2011', '-').Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2015', '-').Replace('\u2212', '-');
        normalized = Regex.Replace(normalized, "[\u00A0\u2002-\u200A\u202F\u205F\u3000]", " ");
        return string.Join("\n", normalized.Split('\n').Select(line => line.TrimEnd()));
    }

    public static AppliedEditsResult ApplyEditsToNormalizedContent(string normalizedContent, IReadOnlyList<EditReplacement> edits, string path)
    {
        if (edits.Count == 0) throw new InvalidOperationException("Edit tool input is invalid. edits must contain at least one replacement.");
        var normalizedEdits = edits.Select(edit => new EditReplacement(NormalizeToLf(edit.OldText), NormalizeToLf(edit.NewText))).ToArray();
        for (var i = 0; i < normalizedEdits.Length; i++)
        {
            if (normalizedEdits[i].OldText.Length == 0) throw new InvalidOperationException(normalizedEdits.Length == 1 ? $"oldText must not be empty in {path}." : $"edits[{i}].oldText must not be empty in {path}.");
        }

        var baseContent = normalizedContent;
        var matched = new List<MatchedEdit>();
        for (var i = 0; i < normalizedEdits.Length; i++)
        {
            var edit = normalizedEdits[i];
            var match = FindText(baseContent, edit.OldText);
            if (!match.Found) throw new InvalidOperationException(NotFoundMessage(path, i, normalizedEdits.Length));
            var occurrences = CountOccurrences(baseContent, edit.OldText);
            if (occurrences > 1) throw new InvalidOperationException(DuplicateMessage(path, i, normalizedEdits.Length, occurrences));
            matched.Add(new MatchedEdit(i, match.Index, match.MatchLength, edit.NewText));
        }

        matched.Sort((a, b) => a.MatchIndex.CompareTo(b.MatchIndex));
        for (var i = 1; i < matched.Count; i++)
        {
            var previous = matched[i - 1];
            var current = matched[i];
            if (previous.MatchIndex + previous.MatchLength > current.MatchIndex)
            {
                throw new InvalidOperationException($"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
            }
        }

        var newContent = baseContent;
        for (var i = matched.Count - 1; i >= 0; i--)
        {
            var edit = matched[i];
            newContent = newContent[..edit.MatchIndex] + edit.NewText + newContent[(edit.MatchIndex + edit.MatchLength)..];
        }

        if (baseContent == newContent) throw new InvalidOperationException(normalizedEdits.Length == 1
            ? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
            : $"No changes made to {path}. The replacements produced identical content.");

        return new AppliedEditsResult(baseContent, newContent);
    }

    public static GeneratedDiff GenerateDiffString(string oldContent, string newContent, int contextLines = 4)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var oldSuffix = oldLines.Length - 1;
        var newSuffix = newLines.Length - 1;
        while (oldSuffix >= prefix && newSuffix >= prefix && oldLines[oldSuffix] == newLines[newSuffix]) { oldSuffix--; newSuffix--; }
        var width = Math.Max(oldLines.Length, newLines.Length).ToString(CultureInfo.InvariantCulture).Length;
        var output = new List<string>();
        var contextStart = Math.Max(0, prefix - contextLines);
        for (var i = contextStart; i < prefix; i++) output.Add($" {Pad(i + 1, width)} {oldLines[i]}");
        for (var i = prefix; i <= oldSuffix; i++) output.Add($"-{Pad(i + 1, width)} {oldLines[i]}");
        for (var i = prefix; i <= newSuffix; i++) output.Add($"+{Pad(i + 1, width)} {newLines[i]}");
        var contextEnd = Math.Min(oldLines.Length - 1, oldSuffix + contextLines);
        for (var i = oldSuffix + 1; i <= contextEnd; i++) output.Add($" {Pad(i + 1, width)} {oldLines[i]}");
        return new GeneratedDiff(string.Join("\n", output), prefix + 1);
    }

    private static FuzzyMatch FindText(string content, string oldText)
    {
        var exact = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exact != -1) return new FuzzyMatch(true, exact, oldText.Length, false);
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOld = NormalizeForFuzzyMatch(oldText);
        var fuzzy = fuzzyContent.IndexOf(fuzzyOld, StringComparison.Ordinal);
        return fuzzy == -1 ? new FuzzyMatch(false, -1, 0, false) : new FuzzyMatch(true, fuzzy, fuzzyOld.Length, true);
    }

    private static int CountOccurrences(string content, string oldText)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOld = NormalizeForFuzzyMatch(oldText);
        if (fuzzyOld.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = fuzzyContent.IndexOf(fuzzyOld, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += fuzzyOld.Length;
        }
        return count;
    }

    private static string NotFoundMessage(string path, int editIndex, int totalEdits) => totalEdits == 1
        ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
        : $"Could not find edits[{editIndex}] in {path}. The oldText must match exactly including all whitespace and newlines.";

    private static string DuplicateMessage(string path, int editIndex, int totalEdits, int occurrences) => totalEdits == 1
        ? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
        : $"Found {occurrences} occurrences of edits[{editIndex}] in {path}. Each oldText must be unique. Please provide more context to make it unique.";

    private static string Pad(int value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width);
}

public sealed record EditReplacement(
    [property: Description("Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call.")]
    string OldText,

    [property: Description("Replacement text for this targeted edit.")]
    string NewText);
public sealed record AppliedEditsResult(string BaseContent, string NewContent);
public sealed record GeneratedDiff(string Diff, int FirstChangedLine);
public sealed record BomStrippedContent(string Bom, string Text);
internal sealed record FuzzyMatch(bool Found, int Index, int MatchLength, bool UsedFuzzyMatch);
internal sealed record MatchedEdit(int EditIndex, int MatchIndex, int MatchLength, string NewText);
