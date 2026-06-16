namespace PiSharp.TsBridge;

public sealed record TsBridgeOptions(
    string NodeExecutable = "node",
    string? RunnerPath = null,
    IReadOnlyList<string>? ExtensionPaths = null,
    string? WorkingDirectory = null,
    string? CacheDirectory = null,
    bool CacheEnabled = true)
{
    public string EffectiveRunnerPath(string baseDirectory)
        => RunnerPath ?? Path.Combine(baseDirectory, "Node", "TsBridgeRunner.mjs");
}
