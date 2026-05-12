namespace Acd.Mcp.Serialization
{
    // The converter calls this when it encounters an unregistered AutoCAD type.
    // Slice 5's implementation rescans both DTO folders and recompiles any
    // .csx files whose mtime changed since the last scan, populating the
    // registry. The converter then retries the lookup.
    //
    // A no-op implementation is provided so the converter factory is usable
    // before the loader is wired up (slice 3 ships the skeleton; slices 4–5
    // attach the live reload).
    public interface IDtoReloadTrigger
    {
        void NotifyMiss(Type runtimeType);
    }

    internal sealed class NoopReloadTrigger : IDtoReloadTrigger
    {
        public static readonly NoopReloadTrigger Instance = new();
        public void NotifyMiss(Type runtimeType) { }
    }
}
