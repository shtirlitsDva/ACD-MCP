namespace Acd.Mcp.Serialization
{
    // Concrete reload trigger. When the converter encounters an unregistered
    // AutoCAD type it calls NotifyMiss; we delegate to DtoLoader.Refresh which
    // does an mtime-incremental scan. If a file changed since the last scan
    // its registration is now in place, and the converter's retry succeeds.
    //
    // Debounce: a stream of "Circle is unknown" misses inside one
    // serialization shouldn't trigger N folder scans. We rate-limit the
    // refresh to once per minInterval. The miss path is still O(1) when
    // rate-limited.
    //
    // The trigger is intentionally stateless about which types have already
    // been "tried" — that knowledge belongs to the registry, and an
    // mtime-based incremental refresh is the right primitive to ask whether
    // the disk has anything new for us.
    public sealed class DtoReloadTrigger : IDtoReloadTrigger
    {
        private readonly DtoLoader _loader;
        private readonly TimeSpan _minInterval;
        private readonly object _gate = new();
        private DateTime _lastScanUtc = DateTime.MinValue;

        public DtoReloadTrigger(DtoLoader loader, TimeSpan? minInterval = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _minInterval = minInterval ?? TimeSpan.FromMilliseconds(500);
        }

        public void NotifyMiss(Type runtimeType)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (now - _lastScanUtc < _minInterval) return;
                _lastScanUtc = now;
            }

            _loader.Refresh();
        }
    }
}
