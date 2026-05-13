using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Acd.Mcp.Batch;
using Acd.Mcp.Pipe;
using Acd.Mcp.Scripting;
using Acd.Mcp.Ui.ManageScripts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acd.Mcp.Ui
{
    // The REPL palette's view-model. Owns:
    //   - the editor text (CurrentCode), wired through the shared
    //     ScriptEditor deep module so the agent's propose / saved-store /
    //     mirror-file mechanics are identical to the BATCH side;
    //   - the list of log entries projected onto an ObservableCollection;
    //   - the IsDirty flag (true after user typing, cleared on Load /
    //     accept-proposal);
    //   - the four toolbar commands (Run / Reset / Clear / Scripts).
    //
    // Implements IManageScriptsTarget so the shared Manage Scripts window
    // can fetch the current editor text (for Save-As) and ask us to load
    // a saved script (with dirty-prompt).
    //
    // Every public entry point (command bodies, event handlers) is
    // wrapped via SafeBoundary so an exception from inside the executor,
    // a binding error, or a broken subscriber cannot crash AutoCAD's
    // WPF dispatcher. The dispatcher's own UnhandledException event is
    // hooked here too as a last-line safety net.
    public sealed partial class ReplViewModel : ObservableObject, IDisposable, IManageScriptsTarget
    {
        private readonly AcadExecutor _executor;
        private readonly ExecutionLog _log;
        private readonly ScriptEditor _scriptEditor;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        // CurrentCode is hand-coded (not [ObservableProperty]) because
        // we need a "set without flipping IsDirty" path for agent-pushed
        // updates and Manage-Scripts loads. Same pattern as BatchViewModel.
        private string _currentCode = "";
        public string CurrentCode
        {
            get => _currentCode;
            set
            {
                if (SetProperty(ref _currentCode, value))
                {
                    if (!_disposed)
                    {
                        // Propagate to the editor FIRST so editor.IsDirty
                        // flips true before any consumer (e.g. an RPC
                        // reading _scriptEditor.IsDirty for
                        // replaced_dirty) can observe a stale-clean
                        // state. Mirrors BatchViewModel's ordering.
                        _scriptEditor.OnUserTyped(value);
                        IsDirty = true;
                    }
                }
            }
        }

        [ObservableProperty] private string _statusLine = "";
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private bool _isDirty;

        public ObservableCollection<LogEntryViewModel> LogEntries { get; } = new();
        public ContextInspectorViewModel ContextInspector { get; }

        public ScriptEditor ScriptEditor => _scriptEditor;

        // IManageScriptsTarget — the shared Manage Scripts window calls
        // these. CurrentCode reflects what the user is editing
        // (authoritative); LoadSavedScript runs the user-load path.
        string IManageScriptsTarget.CurrentScriptText => CurrentCode;
        bool IManageScriptsTarget.LoadSavedScript(SavedScript saved) => LoadSavedScript(saved);

        public ReplViewModel(AcadExecutor executor, ScriptSession session, ExecutionLog log, ScriptEditor scriptEditor)
        {
            _executor = executor;
            _log = log;
            _scriptEditor = scriptEditor ?? throw new ArgumentNullException(nameof(scriptEditor));
            _dispatcher = Dispatcher.CurrentDispatcher;
            ContextInspector = new ContextInspectorViewModel(session);
            ContextInspector.Refresh();

            // Seed the editor display from any pre-existing text in the
            // ScriptEditor slot (e.g. agent proposed before the palette
            // was opened, then user accepted via another path).
            _currentCode = _scriptEditor.CurrentText;

            // Last-line safety net for anything that bubbles up to the
            // dispatcher (binding errors, async-void unhandled
            // exceptions, etc.).
            _dispatcher.UnhandledException += (_, e) =>
            {
                SafeBoundary.Report(e.Exception, "WPF Dispatcher.UnhandledException");
                e.Handled = true;
            };

            // Seed with anything that was already in the log when the
            // palette opened (e.g. MCP calls that happened before the
            // user clicked ACDMCP_PALETTE).
            SafeBoundary.Run("ReplViewModel.ctor/seed", () =>
            {
                foreach (var entry in _log.Snapshot())
                    LogEntries.Add(new LogEntryViewModel(entry));
            });

            _log.EntryAdded += OnEntryAdded;
            _scriptEditor.ScriptProposed += OnScriptProposed;
            UpdateStatus();

            // Palette-reopen recovery: if a proposal was staged in the
            // ScriptEditor before this VM existed (e.g. user closed the
            // palette while a prompt was unanswered, agent re-proposed,
            // user reopens), replay the prompt now via the same handler.
            // The dispatcher is current here so we can synthesise the
            // event from the editor's PendingProposal.
            if (_scriptEditor.PendingProposal is { } pending)
            {
                var evt = new ScriptProposedEvent(pending, _currentCode);
                _dispatcher.BeginInvoke(new Action(() => OnScriptProposed(this, evt)));
            }
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
                    // AcadExecutor itself converts every script-level
                    // failure to an ExecuteResult; this wrapper catches
                    // the unexpected (the executor itself misbehaving,
                    // a logger throwing, etc.).
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
            ContextInspector.Refresh();
        });

        [RelayCommand]
        private void ClearLog() => SafeBoundary.Run("ReplViewModel.ClearLog", () =>
        {
            LogEntries.Clear();
            UpdateStatus();
        });

        [RelayCommand]
        private void Scripts() => SafeBoundary.Run("ReplViewModel.Scripts", () =>
        {
            var win = new ManageScriptsWindow(_scriptEditor, this);
            win.Owner = Application.Current?.MainWindow;
            win.ShowDialog();
        });

        // Called by the shared Manage Scripts window when the user clicks
        // Load. Prompts if the editor has unsaved typed edits that would
        // be visibly replaced. Returns true if the load was applied;
        // false if the user refused so the window can stay open.
        public bool LoadSavedScript(SavedScript saved)
        {
            bool applied = false;
            SafeBoundary.Run("ReplViewModel.LoadSavedScript", () =>
            {
                if (IsDirty && CurrentCode != saved.Body)
                {
                    var choice = MessageBox.Show(
                        "Replace your unsaved editor changes with the saved script?",
                        "Unsaved changes",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (choice != MessageBoxResult.Yes) return;
                }
                _scriptEditor.LoadFromSaved(saved);
                SetVmTextFromLoad(saved.Body);
                IsDirty = false;
                applied = true;
            });
            return applied;
        }

        // Sync the VM's display text to a value that came from a load
        // (agent proposal accepted, or Manage-Scripts Load), WITHOUT
        // re-entering the user-typed path that would flip the editor's
        // IsDirty back to true. The ScriptEditor side is already at
        // saved.Body when this is called.
        private void SetVmTextFromLoad(string value)
        {
            SetProperty(ref _currentCode, value, nameof(CurrentCode));
        }

        private void OnScriptProposed(object? sender, ScriptProposedEvent evt) =>
            Marshal(() =>
            {
                // Staged proposal — CurrentCode + the editor slot still
                // hold what the user is editing. Prompt only if accepting
                // would visibly change the editor.
                if (IsDirty && CurrentCode != evt.Saved.Body)
                {
                    var choice = MessageBox.Show(
                        $"The agent proposed an updated script '{evt.Saved.Name}'.\n\nReplace your unsaved changes?",
                        "Script proposed by agent",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (choice != MessageBoxResult.Yes)
                    {
                        _scriptEditor.DiscardPending();
                        return;
                    }
                }
                // Clean accept path (no dirty edits to clobber, or user
                // confirmed). Promote pending → current and sync the VM.
                _scriptEditor.AcceptPending();
                SetVmTextFromLoad(evt.Saved.Body);
                IsDirty = false;
            });

        private void OnEntryAdded(object? sender, LogEntry entry)
        {
            // Fires on whichever thread called ExecutionLog.Add — usually
            // the AutoCAD main thread (the executor's Post handler) or,
            // in some edge cases during shutdown, a threadpool thread.
            // Wrap the marshal so a disposed dispatcher cannot escape.
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
                // Keep the UI list bounded — ExecutionLog is already
                // capped, but the VM holds its own copy so we cap here
                // too.
                const int maxUiEntries = 200;
                while (LogEntries.Count > maxUiEntries)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
                UpdateStatus();
                ContextInspector.Refresh();
            });
        }

        private void Marshal(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
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
                _scriptEditor.ScriptProposed -= OnScriptProposed;
            });
        }
    }
}
