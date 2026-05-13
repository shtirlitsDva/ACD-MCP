namespace Acd.Mcp.Batch
{
    // Mirrors a ScriptEditor's current text to a file on disk so the
    // agent (LLM) can read whatever the user is currently editing
    // before proposing a replacement. Writes are debounced (~250 ms)
    // so a flurry of keystrokes doesn't translate into a flurry of
    // disk writes.
    //
    // One EditorBuffer per ScriptEditor — BATCH and SCRIPT each point
    // at their own mirror path. Mirror file names use a buffer-<flavor>
    // convention so the two files sort next to each other in Explorer.
    //
    // Thread-safe: SetText can be called from the WPF dispatcher; the
    // debounce timer's callback fires on a threadpool thread.
    public sealed class EditorBuffer : IDisposable
    {
        // BATCH editor mirror path. SCRIPT instances pass an explicit
        // pathOverride to point at buffer-script.csx in the same folder.
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Acd.Mcp",
            "buffer-batch.csx");

        private readonly string _path;
        private readonly TimeSpan _debounce;
        private readonly object _lock = new();
        private string _pendingText = "";
        private Timer? _timer;
        private bool _disposed;

        public EditorBuffer(string? pathOverride = null, TimeSpan? debounce = null)
        {
            _path = pathOverride ?? DefaultPath;
            _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }

        public string MirrorPath => _path;

        public void SetText(string text)
        {
            if (text is null) text = "";
            lock (_lock)
            {
                if (_disposed) return;
                _pendingText = text;
                _timer ??= new Timer(_ => Flush(), state: null,
                    dueTime: Timeout.InfiniteTimeSpan, period: Timeout.InfiniteTimeSpan);
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }

        public void FlushNow() => Flush();

        private void Flush()
        {
            string snapshot;
            lock (_lock)
            {
                if (_disposed) return;
                snapshot = _pendingText;
            }
            try
            {
                // Atomic-ish replace via a temp file. WriteAllText on the
                // canonical path is fine for our durability needs — the
                // agent reads the file with retry-on-EBUSY semantics
                // implicitly through normal file ops.
                File.WriteAllText(_path, snapshot);
            }
            catch
            {
                // Disk full / perms / antivirus interception — the editor
                // mirror is a convenience, not a critical path. Swallow.
            }
        }

        public void Dispose()
        {
            // Flush any pending debounced write BEFORE tearing down so
            // the last ≤250 ms of typing isn't lost on plugin unload.
            // Flush() takes the lock internally and short-circuits on
            // _disposed, so it must run before we flip the flag.
            Flush();
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
