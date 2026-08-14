namespace PiSharp.DeclarativeTools;

/// <summary>
/// The file kinds the scanner accepts inside a tool directory.
/// </summary>
public enum DeclarativeToolKind
{
    Markdown,
    Json,
    Script
}

/// <summary>
/// The two accepted discovery shapes (plan §5.1):
/// file form (<c>tools/foo.ext</c>) and directory form (<c>tools/&lt;name&gt;/index.ext</c>).
/// </summary>
public enum ToolFileShape
{
    /// <summary><c>tools/foo.ext</c> — the tool name derives from the file name.</summary>
    File,

    /// <summary><c>tools/&lt;name&gt;/index.ext</c> — the tool name derives from the directory name.</summary>
    Index
}

/// <summary>
/// A candidate tool file found by <see cref="ToolDirectoryScanner"/>.
/// </summary>
public sealed record ToolFile(string Path, DeclarativeToolKind Kind, ToolFileShape Shape)
{
    /// <summary>
    /// Name derived from the file path before frontmatter overrides are applied
    /// (<see cref="ToolFileParser"/>): directory name for the index form,
    /// file name without extension for the file form.
    /// </summary>
    public string DefaultName
    {
        get
        {
            if (Shape == ToolFileShape.Index)
                return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) ?? string.Empty;
            return System.IO.Path.GetFileNameWithoutExtension(Path);
        }
    }
}
