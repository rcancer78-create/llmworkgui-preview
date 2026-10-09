using Xunit;

// WPF Application resources, windows and StaTestRunner's dispatcher are process-wide.
// A test pumping ContextIdle can otherwise execute another test's theme/window mutations
// inside its own layout assertions. Keep product concurrency tests explicit within one test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
