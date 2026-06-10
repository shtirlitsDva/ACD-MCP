using System.Runtime.CompilerServices;

// Test project needs to construct internal types (BatchContext, BatchStateBag)
// without reflection so the tests stay readable and refactor-safe. The plugin
// (`Acd.Mcp`) also needs internal access so it can wire up the runner with
// the same internal types when constructing per-file contexts.
[assembly: InternalsVisibleTo("Acd.Mcp.Batch.Tests")]
[assembly: InternalsVisibleTo("Acd.Mcp")]
