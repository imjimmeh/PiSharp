using Xunit;

// The memory plugin tests share the app-base MemoryServices static registries, so
// they must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
