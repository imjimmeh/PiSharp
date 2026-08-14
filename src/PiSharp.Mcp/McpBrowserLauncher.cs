using System.Diagnostics;

namespace PiSharp.Mcp;

/// <summary>
/// Shell-opens a URL in the platform browser (win32: shell execute; macOS: <c>open</c>;
/// Linux: <c>xdg-open</c>). Mirrors the CLI's <c>OAuthBrowserLauncher</c> without referencing
/// <c>PiSharp.Cli</c> so the daemon can open the MCP OAuth consent page itself.
/// </summary>
public static class McpBrowserLauncher
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
            // The URL is surfaced to the operator through the status output; a browser launch
            // failure must not fail the OAuth flow (headless daemons report "auth required").
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
