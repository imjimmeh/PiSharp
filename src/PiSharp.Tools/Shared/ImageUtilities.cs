using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace PiSharp.Tools.Shared;

public static class ImageUtilities
{
    public const int MaxImageBytes = 25 * 1024 * 1024;
    public const long MaxImagePixels = 40_000_000;

    public static string? DetectSupportedImageMimeType(string path, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G') return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F') return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P') return "image/webp";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null
        };
    }

    public static async Task<ProcessedImage> ResizeIfNeededAsync(byte[] bytes, string mimeType, int maxDimension = 2000, CancellationToken cancellationToken = default)
    {
        if (bytes.Length > MaxImageBytes) throw new InvalidOperationException($"Image exceeds maximum supported size of {MaxImageBytes} bytes.");
        var format = Image.DetectFormat(bytes);
        using var image = Image.Load(bytes);
        if ((long)image.Width * image.Height > MaxImagePixels) throw new InvalidOperationException($"Image exceeds maximum supported pixel count of {MaxImagePixels}.");
        var originalWidth = image.Width;
        var originalHeight = image.Height;
        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(maxDimension, maxDimension),
                Mode = ResizeMode.Max
            }));
        }

        await using var output = new MemoryStream();
        await SaveAsync(image, output, mimeType, format, cancellationToken).ConfigureAwait(false);
        var note = originalWidth == image.Width && originalHeight == image.Height ? null : $"[Resized from {originalWidth}x{originalHeight} to {image.Width}x{image.Height}]";
        return new ProcessedImage(mimeType, output.ToArray(), note);
    }

    private static Task SaveAsync(Image image, Stream output, string mimeType, IImageFormat fallbackFormat, CancellationToken cancellationToken)
        => mimeType switch
        {
            "image/png" => image.SaveAsync(output, new PngEncoder(), cancellationToken),
            "image/jpeg" => image.SaveAsync(output, new JpegEncoder(), cancellationToken),
            "image/gif" => image.SaveAsync(output, new GifEncoder(), cancellationToken),
            "image/webp" => image.SaveAsync(output, new WebpEncoder(), cancellationToken),
            _ => image.SaveAsync(output, fallbackFormat, cancellationToken)
        };
}

public sealed record ProcessedImage(string MimeType, byte[] Data, string? DimensionNote = null);
