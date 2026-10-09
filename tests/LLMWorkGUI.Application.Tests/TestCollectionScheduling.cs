using Xunit;

// These suites contain bounded race/deadline regressions. xUnit 2.5's collection
// scheduling can starve their continuations on small runners when unrelated
// collections start concurrently. Keep each test's explicit concurrent work and
// deadlines intact while executing independent collections sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
