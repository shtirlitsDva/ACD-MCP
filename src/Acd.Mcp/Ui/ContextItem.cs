namespace Acd.Mcp.Ui
{
    // One row in the Context inspector panel. Three columns: the identifier the
    // user can type into a snippet (Name), its declared/runtime type (TypeName),
    // and its current value rendered as a (possibly truncated) string.
    //
    // Plain immutable DTO — the inspector rebuilds its observable collections
    // on each refresh rather than mutating individual items, so per-item change
    // notification isn't needed.
    public sealed class ContextItem
    {
        public required string Name { get; init; }
        public required string TypeName { get; init; }
        public required string Value { get; init; }
    }
}
