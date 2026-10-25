using Xunit;

// Keep local, CI and release collection scheduling consistent. Explicit concurrency
// inside tests remains enabled; this does not diagnose native fixture I/O failures.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
