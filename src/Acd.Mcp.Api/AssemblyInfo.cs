using System.Runtime.CompilerServices;

// Acd.Mcp hosts the converter / loader / providers that consume the registry's
// internal TryGet and IDtoProjection; Acd.Mcp.Tests links the registry source
// directly for pure-logic tests and needs the same access. Both relationships
// are by design and stay narrow.
[assembly: InternalsVisibleTo("Acd.Mcp")]
[assembly: InternalsVisibleTo("Acd.Mcp.Tests")]
