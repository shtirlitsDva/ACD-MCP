namespace Acd.Mcp
{
    public enum ExecutionSource
    {
        Mcp,    // came in over the named pipe
        Repl,   // ran from the in-process REPL palette
    }

    public sealed record LogEntry(
        DateTimeOffset Timestamp,
        ExecutionSource Source,
        string Code,
        ExecuteResult Result);

    // Single observable buffer of recent executions. Both the MCP pipe and the
    // in-process REPL palette feed this through AcadExecutor; the palette VM
    // subscribes to EntryAdded and projects entries onto its WPF collection.
    //
    // Thread-safe: Add can be called from the pipe's threadpool task or from the
    // WPF dispatcher thread. The class itself owns the locking; consumers are
    // responsible for marshaling Their own UI updates onto the correct thread
    // (the palette VM does this via Dispatcher.BeginInvoke).
    public sealed class ExecutionLog
    {
        private readonly object _lock = new();
        private readonly LinkedList<LogEntry> _entries = new();

        public int Capacity { get; }

        public event EventHandler<LogEntry>? EntryAdded;

        public ExecutionLog(int capacity = 200)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
        }

        public void Add(LogEntry entry)
        {
            lock (_lock)
            {
                _entries.AddFirst(entry);
                while (_entries.Count > Capacity) _entries.RemoveLast();
            }
            EntryAdded?.Invoke(this, entry);
        }

        public IReadOnlyList<LogEntry> Snapshot()
        {
            lock (_lock) return _entries.ToArray();
        }
    }
}
