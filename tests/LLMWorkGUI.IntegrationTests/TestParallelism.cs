using Xunit;

// Process/barrier/deadline fixtures share host resources and xUnit's synchronization
// context. Isolate collection scheduling; each test retains its concurrent operations
// and original deadlines, so actual race/ownership assertions remain exercised.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
