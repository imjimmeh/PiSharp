using PiSharp.Coordination;

if (args.Length < 2 || !TryParseArgs(args, out var repoRoot, out var pipeName))
{
    Console.Error.WriteLine("Usage: PiSharp.Coordination.Daemon --repo-root <path> --pipe-name <name>");
    return 1;
}

Directory.CreateDirectory(repoRoot);

var coordinationDir = Path.Combine(repoRoot, ".pi", "coordination");
await using var daemon = await CoordinationDaemon.StartAsync(coordinationDir, pipeName);

Console.WriteLine($"Coordination daemon started on pipe '{pipeName}' for repo '{repoRoot}'.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}

Console.WriteLine("Coordination daemon stopped.");
return 0;

static bool TryParseArgs(string[] args, out string repoRoot, out string pipeName)
{
    repoRoot = string.Empty;
    pipeName = string.Empty;

    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--repo-root" && !string.IsNullOrWhiteSpace(args[i + 1]))
            repoRoot = args[i + 1];
        else if (args[i] == "--pipe-name" && !string.IsNullOrWhiteSpace(args[i + 1]))
            pipeName = args[i + 1];
    }

    return !string.IsNullOrWhiteSpace(repoRoot) && !string.IsNullOrWhiteSpace(pipeName);
}
