using Xunit;

// The eval kernel registry is a process-wide static and the C# kernel redirects the
// process console during execution; tests must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
