using Xunit;

// The secret store and the parental PIN file keep process-wide state. Running the tests one at a
// time keeps each test's temporary data folder isolated and makes failures reproducible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
