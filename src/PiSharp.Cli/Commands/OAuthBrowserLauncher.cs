using System.Diagnostics;

namespace PiSharp.Cli.Commands;

public static class OAuthBrowserLauncher
{
    public static Task OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Task.CompletedTask;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        try
        {
            using var process = Start(uri.ToString());
        }
        catch (Exception)
        {
            // The URL is still printed to the terminal, so browser launch failures should not fail login.
        }

        return Task.CompletedTask;
    }

    private static Process? Start(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            return Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }

        if (OperatingSystem.IsMacOS())
            return Process.Start(CreateCommand("open", url));

        return Process.Start(CreateCommand("xdg-open", url));
    }

    private static ProcessStartInfo CreateCommand(string fileName, string url)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }
}
