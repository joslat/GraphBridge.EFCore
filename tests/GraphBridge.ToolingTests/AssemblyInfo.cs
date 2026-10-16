using Xunit;

// Roslyn and EF model construction are serialized on the shared developer machine.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
