using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Abstractions.Messages;
using PiSharp.Tools.Shared;

namespace PiSharp.Cli.Files;

public sealed record ProcessedFileReferences(string Text, IReadOnlyList<ImageContent> Images);

public static class FileReferenceProcessor
{
    public static async Task<ProcessedFileReferences> ProcessInlineReferencesAsync(string text, string workingDirectory, CancellationToken cancellationToken = default, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        logger.LogDebug("FileReferenceProcessor.ProcessInlineReferencesAsync entry textLength={TextLength}", text.Length);
        var references = ExtractReferences(text).ToArray();
        if (references.Length == 0)
        {
            logger.LogDebug("FileReferenceProcessor.ProcessInlineReferencesAsync exit (no references)");
            return new ProcessedFileReferences(text, []);
        }
        logger.LogDebug("FileReferenceProcessor.ProcessInlineReferencesAsync found {ReferenceCount} references", references.Length);

        var context = new StringBuilder();
        var images = new List<ImageContent>();
        foreach (var reference in references)
        {
            var fullPath = Path.GetFullPath(reference.Path, workingDirectory);
            if (!IsUnderWorkingDirectory(fullPath, workingDirectory)) continue;
            if (!File.Exists(fullPath)) continue;
            if (await IsGitIgnoredAsync(fullPath, workingDirectory, cancellationToken).ConfigureAwait(false)) continue;
            await AppendFileContextAsync(context, images, fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (context.Length == 0)
        {
            logger.LogDebug("FileReferenceProcessor.ProcessInlineReferencesAsync exit (no usable references)");
            return new ProcessedFileReferences(text, []);
        }
        context.Append(text);
        logger.LogDebug("FileReferenceProcessor.ProcessInlineReferencesAsync exit with {ImageCount} images", images.Count);
        return new ProcessedFileReferences(context.ToString(), images);
    }

    public static async Task<ProcessedFileReferences> ProcessFileArgumentsAsync(IEnumerable<string> fileArgs, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var context = new StringBuilder();
        var images = new List<ImageContent>();
        foreach (var fileArg in fileArgs)
        {
            var fullPath = Path.GetFullPath(fileArg, workingDirectory);
            if (!File.Exists(fullPath)) throw new FileNotFoundException($"File not found: {fullPath}", fullPath);
            await AppendFileContextAsync(context, images, fullPath, cancellationToken).ConfigureAwait(false);
        }
        return new ProcessedFileReferences(context.ToString(), images);
    }

    private static bool IsUnderWorkingDirectory(string path, string workingDirectory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullPath, fullWorkingDirectory, comparison)) return true;
        return fullPath.StartsWith(fullWorkingDirectory + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(fullWorkingDirectory + Path.AltDirectorySeparatorChar, comparison);
    }

    private static async Task AppendFileContextAsync(StringBuilder context, List<ImageContent> images, string fullPath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0) return;

        var mimeType = ImageUtilities.DetectSupportedImageMimeType(fullPath, bytes);
        if (mimeType is not null)
        {
            try
            {
                var image = await ImageUtilities.ResizeIfNeededAsync(bytes, mimeType, cancellationToken: cancellationToken).ConfigureAwait(false);
                images.Add(new ImageContent(image.MimeType, Convert.ToBase64String(image.Data)));
                var note = image.DimensionNote ?? string.Empty;
                context.Append("<file name=\"").Append(fullPath).Append("\">").Append(note).AppendLine("</file>");
            }
            catch
            {
                context.Append("<file name=\"").Append(fullPath).AppendLine("\">[Image omitted: could not be resized below the inline image size limit.]</file>");
            }
            return;
        }

        var content = Encoding.UTF8.GetString(bytes);
        context.Append("<file name=\"").Append(fullPath).AppendLine("\">")
            .Append(content)
            .AppendLine()
            .AppendLine("</file>");
    }

    private static IEnumerable<FileReferenceToken> ExtractReferences(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@' || !IsTokenStart(text, i)) continue;
            if (i + 1 >= text.Length) continue;
            if (text[i + 1] == '"')
            {
                var end = text.IndexOf('"', i + 2);
                if (end < 0) continue;
                var path = text[(i + 2)..end];
                if (!string.IsNullOrWhiteSpace(path)) yield return new FileReferenceToken(path);
                i = end;
                continue;
            }

            var start = i + 1;
            var endIndex = start;
            while (endIndex < text.Length && !char.IsWhiteSpace(text[endIndex])) endIndex++;
            var raw = text[start..endIndex].TrimEnd('.', ',', ';', ':', ')', ']');
            if (raw.Length > 0 && LooksLikePath(raw)) yield return new FileReferenceToken(raw);
            i = endIndex;
        }
    }

    private static bool IsTokenStart(string text, int index)
        => index == 0 || char.IsWhiteSpace(text[index - 1]);

    private static bool LooksLikePath(string value)
        => value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('.', StringComparison.Ordinal)
            || value.StartsWith("~", StringComparison.Ordinal);

    private static async Task<bool> IsGitIgnoredAsync(string fullPath, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var relativePath = Path.GetRelativePath(workingDirectory, fullPath);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };
            process.StartInfo.ArgumentList.Add("check-ignore");
            process.StartInfo.ArgumentList.Add("--quiet");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(relativePath);

            if (!process.Start()) return false;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record FileReferenceToken(string Path);
}
