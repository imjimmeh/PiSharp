using Xunit;

namespace PiSharp.Tui.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TuiIntegrationTestCollection
{
    public const string Name = "TUI integration tests";
}
