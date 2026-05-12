using System.Collections.ObjectModel;
using System.Windows.Threading;
using Acd.Mcp.Pipe;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acd.Mcp.Ui
{
    // The palette's view-model. Owns:
    //   - the editor text (CurrentCode)
    //   - the list of log entries projected onto an ObservableCollection
    //   - the three commands the view binds to (Run / Reset / Clear)
    //
    // Every public entry point (command bodies, event handlers) is wrapped via
    // SafeBoundary so an exception from inside the executor, a binding error,
    // or a broken subscriber cannot crash AutoCAD's WPF dispatcher. The dispatcher's
    // own UnhandledException event is hooked here too as a last-line safety net.
    //
    // Does NOT own:
    //   - threading marshaling for snippet execution (AcadExecutor handles it)
    //   - WPF UI mechanics — the View binds via {Binding ...} only
    public sealed partial class ReplViewModel : ObservableObject, IDisposable
    {
        private readonly AcadExecutor _executor;
        private readonly ExecutionLog _log;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        [ObservableProperty] private string _currentCode = "";
        [ObservableProperty] private string _statusLine = "";
        [ObservableProperty] private bool _isRunning;

        public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

        public ReplViewModel(AcadExecutor executor, ExecutionLog log)
        {
            _executor = executor;
            _log = log;
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Last-line safety net for anything that bubbles up to the dispatcher
            // (binding errors, async-void unhandled exceptions, etc.).
            _dispatcher.UnhandledException += (_, e) =>
            {
                SafeBoundary.Report(e.Exception, "WPF Dispatcher.UnhandledException");
                e.Handled = true;
            };

            // Seed with anything that was already in the log when the palette opened
            // (e.g. MCP calls that happened before the user clicked ACDMCP_PALETTE).
            SafeBoundary.Run("ReplViewModel.ctor/seed", () =>
            {
                foreach (var entry in _log.Snapshot())
                    LogEntries.Add(new LogEntryViewModel(entry));
            });

            _log.EntryAdded += OnEntryAdded;
            UpdateStatus();
        }

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task RunAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentCode)) return;
            IsRunning = true;
            try
            {
                await SafeBoundary.RunAsync("ReplViewModel.Run", async () =>
                {
                    // AcadExecutor itself converts every script-level failure to
                    // an ExecuteResult; this wrapper catches the unexpected (the
                    // executor itself misbehaving, a logger throwing, etc.).
                    _ = await _executor.ExecuteAsync(
                        CurrentCode, timeoutMs: null, ExecutionSource.Repl, CancellationToken.None)
                        .ConfigureAwait(true);
                }).ConfigureAwait(true);
            }
            finally
            {
                IsRunning = false;
            }
        }

        private bool CanRun() => !IsRunning;

        partial void OnIsRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

        [RelayCommand]
        private void ResetSession() => SafeBoundary.Run("ReplViewModel.ResetSession", () =>
        {
            _executor.Reset();
        });

        [RelayCommand]
        private void ClearLog() => SafeBoundary.Run("ReplViewModel.ClearLog", () =>
        {
            LogEntries.Clear();
            UpdateStatus();
        });

        private void OnEntryAdded(object? sender, LogEntry entry)
        {
            // Fires on whichever thread called ExecutionLog.Add — usually the
            // AutoCAD main thread (the executor's Post handler) or, in some
            // edge cases during shutdown, a threadpool thread. Wrap the marshal
            // so a disposed dispatcher cannot escape.
            SafeBoundary.Run("ReplViewModel.OnEntryAdded", () =>
            {
                if (_dispatcher.CheckAccess())
                    AddEntryOnDispatcher(entry);
                else
                    _dispatcher.BeginInvoke(new Action(() => AddEntryOnDispatcher(entry)));
            });
        }

        private void AddEntryOnDispatcher(LogEntry entry)
        {
            SafeBoundary.Run("ReplViewModel.AddEntryOnDispatcher", () =>
            {
                LogEntries.Insert(0, new LogEntryViewModel(entry));
                // Keep the UI list bounded — ExecutionLog is already capped, but
                // the VM holds its own copy so we cap here too.
                const int maxUiEntries = 200;
                while (LogEntries.Count > maxUiEntries)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
                UpdateStatus();
            });
        }

        private void UpdateStatus()
        {
            int total = LogEntries.Count;
            int mcp = 0, repl = 0;
            foreach (var e in LogEntries)
            {
                if (e.Entry.Source == ExecutionSource.Mcp) mcp++;
                else if (e.Entry.Source == ExecutionSource.Repl) repl++;
            }
            StatusLine = $"{total} entries  ({mcp} MCP, {repl} REPL)";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SafeBoundary.Run("ReplViewModel.Dispose", () =>
            {
                _log.EntryAdded -= OnEntryAdded;
            });
        }
    }
}
