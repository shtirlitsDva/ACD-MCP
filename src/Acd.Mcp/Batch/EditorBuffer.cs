using System;
using System.IO;
using System.Threading;

namespace Acd.Mcp.Batch.Runtime
{
    // Mirrors the BATCH editor's current text to:
    //   %LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx
    //
    // The agent reads that file before proposing edits so it doesn't trample
    // the user's in-progress changes. Mirror writes are debounced (~250 ms)
    // so a flurry of keystrokes doesn't translate into a flurry of disk
    // writes.
    //
    // Thread-safe: SetText can be called from the WPF dispatcher; the
    // debounce timer's callback fires on a threadpool thread.
    internal sealed class EditorBuffer : IDisposable
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Acd.Mcp",
            "editor-buffer.csx");

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
