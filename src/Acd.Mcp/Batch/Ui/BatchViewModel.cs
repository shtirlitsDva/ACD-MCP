using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Acd.Mcp.Batch;
using Acd.Mcp.Batch.Runtime;
using Acd.Mcp.Ui.ManageScripts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acd.Mcp.Batch.Ui
{
    // The BATCH palette's view-model. Lives entirely on the WPF dispatcher
    // thread for property mutation; background-thread callbacks marshal in.
    //
    // Owns:
    //   - Folder / Mask / Recurse selection.
    //   - The list of matching files (refreshed on demand).
    //   - The Test/Live slide-switch state.
    //   - The "is a run in progress" gate.
    //   - The per-file results collection (one row per file as they complete).
    //
    // Reacts to:
    //   - BatchExecutor.ScriptProposed → editor text update with the
    //     unsaved-edits prompt when the editor has dirty changes.
    //   - BatchExecutor.FileCompleted → append a row.
    //   - BatchExecutor.RunCompleted  → flip IsRunning off, update status.
    //
    // Implements IBatchUiState so the pipe RPC handler can read the
    // current selection without coupling to WPF.
    public sealed partial class BatchViewModel : ObservableObject, IDisposable, IBatchUiState, IManageScriptsTarget
    {
        private readonly BatchExecutor _executor;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        [ObservableProperty] private string _folder = "";
        [ObservableProperty] private string _mask = "*.dwg";
        [ObservableProperty] private bool _recurse = false;
        [ObservableProperty] private string _matchedSummary = "(no folder selected)";

        // CurrentScript is hand-coded (not [ObservableProperty]) because we
        // need a "set without flipping IsDirty" path for agent-pushed updates
        // and Manage-Scripts loads.
        private string _currentScript = "";
        public string CurrentScript
        {
            get => _currentScript;
            set
            {
                if (SetProperty(ref _currentScript, value))
                {
                    if (!_disposed)
                    {
                        // Propagate to the editor FIRST so editor.IsDirty
                        // flips true before any consumer (e.g. an RPC
                        // arriving on a thread-pool thread reading
                        // _executor.IsDirty for replaced_dirty) can
                        // observe a stale-clean state.
                        _executor.OnEditorTextChanged(value);
                        IsDirty = true;
                    }
                }
            }
        }

        [ObservableProperty] private bool _isDirty;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private bool _liveSelected;
        [ObservableProperty] private string _statusLine = "";

        // On-failure policy — the user picks Abort | Skip from the palette
        // ComboBox. Defaults to Abort: a script wrong against the user's
        // assumptions should stop the loop, not silently fail N drawings.
        [ObservableProperty] private BatchOnFailure _onFailure = BatchOnFailure.Abort;

        // ComboBox ItemsSource. Computed once; the enum values never change.
        public IReadOnlyList<BatchOnFailure> OnFailureOptions { get; } =
            (BatchOnFailure[])Enum.GetValues(typeof(BatchOnFailure));

        public ObservableCollection<BatchFileResultViewModel> Results { get; } = new();
        public ObservableCollection<string> Files { get; } = new();

        // IBatchUiState surface — the pipe RPC handler reads these to forward
        // the agent's "what files am I about to run on?" query.
        string IBatchUiState.CurrentFolder => Folder;
        string IBatchUiState.CurrentMask => Mask;
        bool IBatchUiState.Recurse => Recurse;
        IReadOnlyList<string> IBatchUiState.CurrentSelection => Files.ToArray();
        BatchOnFailure IBatchUiState.OnFailure => OnFailure;

        // IManageScriptsTarget — the shared Manage Scripts window reads
        // CurrentScriptText for Save-As and calls LoadSavedScript when
        // the user clicks Load.
        string IManageScriptsTarget.CurrentScriptText => CurrentScript;
        bool IManageScriptsTarget.LoadSavedScript(SavedScript saved) => LoadSavedScript(saved);

        public BatchViewModel(BatchExecutor executor)
        {
            _executor = executor;
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Seed editor text with whatever the executor currently has
            // (could be non-empty if the agent proposed before the palette opened).
            _currentScript = executor.CurrentScript;

            executor.ScriptProposed += OnScriptProposed;
            executor.FileCompleted += OnFileCompleted;
            executor.RunCompleted += OnRunCompleted;

            UpdateStatus();
        }

        // Sync the VM's display text to a value that came from a load
        // (agent proposal accepted, or Manage-Scripts Load), WITHOUT
        // re-entering the user-typed path that would flip the editor's
        // IsDirty back to true. The ScriptEditor side has already been
        // updated by the caller (via ProposeFromAgent / LoadFromSaved),
        // so this is purely a VM-side display refresh.
        private void SetVmTextFromLoad(string value)
        {
            SetProperty(ref _currentScript, value, nameof(CurrentScript));
        }

        [RelayCommand]
        private void Refresh() => SafeBoundary.Run("BatchVm.Refresh", () =>
        {
            Files.Clear();
            if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
            {
                MatchedSummary = "(no folder selected)";
                return;
            }
            var opt = Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var found = Directory.EnumerateFiles(Folder, string.IsNullOrWhiteSpace(Mask) ? "*.dwg" : Mask, opt)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var f in found) Files.Add(f);
            MatchedSummary = $"{found.Length} file{(found.Length == 1 ? "" : "s")} matched.";
        });

        [RelayCommand]
        private void Browse() => SafeBoundary.Run("BatchVm.Browse", () =>
        {
            // WPF doesn't have a built-in folder picker; the standard
            // approach for plugin-internal use is Win32's OpenFolderDialog
            // (System.Windows.Forms.FolderBrowserDialog). We avoid the
            // WinForms dep by using Windows API CodePack or by accepting
            // a typed path. Keep it simple here: prompt via input box.
            // Real-world hosts can swap in a richer picker.
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select drawings folder",
                InitialDirectory = Directory.Exists(Folder) ? Folder : "",
            };
            if (dlg.ShowDialog() == true)
            {
                Folder = dlg.FolderName;
                Refresh();
            }
        });

        [RelayCommand(CanExecute = nameof(CanRun))]
        private void Run() => SafeBoundary.Run("BatchVm.Run", () =>
        {
            if (Files.Count == 0) { StatusLine = "No files matched."; return; }
            Results.Clear();
            IsRunning = true;
            var mode = LiveSelected ? BatchMode.Live : BatchMode.Test;
            _executor.StartRunFromUi(mode, Files.ToArray(), OnFailure);
        });

        private bool CanRun() => !IsRunning;
        partial void OnIsRunningChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

        [RelayCommand]
        private void Cancel() => SafeBoundary.Run("BatchVm.Cancel", () => _executor.Cancel());

        [RelayCommand]
        private void ManageScripts() => SafeBoundary.Run("BatchVm.ManageScripts", () =>
        {
            var win = new ManageScriptsWindow(_executor.ScriptEditor, this);
            // Modal — see <open-decisions> #4. Closes on Load / Cancel.
            win.Owner = Application.Current?.MainWindow;
            win.ShowDialog();
        });

        // Called when the user picks "Load" in the Manage Scripts window.
        // Behaves the same as an agent proposal, with the dirty-prompt
        // path. Returns true if the load was applied; false if the user
        // refused at the unsaved-changes prompt (so the window can stay
        // open for another pick).
        public bool LoadSavedScript(SavedScript saved)
        {
            bool applied = false;
            SafeBoundary.Run("BatchVm.LoadSavedScript", () =>
            {
                if (IsDirty && CurrentScript != saved.Body)
                {
                    var choice = MessageBox.Show(
                        "Replace your unsaved editor changes with the saved script?",
                        "Unsaved changes",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (choice != MessageBoxResult.Yes) return;
                }
                // Drive the ScriptEditor's clean-load path (clears its
                // IsDirty + syncs slot/mirror); then refresh the VM's
                // display field without re-entering the typed setter.
                _executor.ScriptEditor.LoadFromSaved(saved);
                SetVmTextFromLoad(saved.Body);
                IsDirty = false;
                applied = true;
            });
            return applied;
        }

        private void OnScriptProposed(object? sender, ScriptProposedEvent evt)
            => Marshal(() =>
            {
                // Unsaved-edits race resolution per spec, option (a): prompt.
                // The proposal is staged in ScriptEditor.PendingProposal — the
                // editor's CurrentText + mirror still hold what the user is
                // editing, so we can compare against the VM's display safely.
                if (IsDirty && CurrentScript != evt.Saved.Body)
                {
                    var choice = MessageBox.Show(
                        $"The agent proposed an updated script '{evt.Saved.Name}'.\n\nReplace your unsaved changes?",
                        "Script proposed by agent",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (choice != MessageBoxResult.Yes)
                    {
                        _executor.ScriptEditor.DiscardPending();
                        return;
                    }
                }
                // Promote pending → current inside the ScriptEditor; this
                // is the one place where the editor's slot + mirror are
                // updated by an agent proposal, and only after the user
                // (or the no-prompt path) has accepted.
                _executor.ScriptEditor.AcceptPending();
                SetVmTextFromLoad(evt.Saved.Body);
                IsDirty = false;
            });

        private void OnFileCompleted(object? sender, BatchFileResult r) =>
            Marshal(() =>
            {
                Results.Add(new BatchFileResultViewModel(r));
                UpdateStatus();
            });

        private void OnRunCompleted(object? sender, BatchRunReport report) =>
            Marshal(() =>
            {
                IsRunning = false;
                if (!string.IsNullOrEmpty(report.AbortedReason))
                    StatusLine = $"Aborted: {report.AbortedReason}";
                else if (report.Cancelled)
                    StatusLine = $"Cancelled. {Results.Count} file(s) reported.";
                else
                    UpdateStatus();
            });

        private void UpdateStatus()
        {
            int pass = 0, fail = 0;
            foreach (var r in Results)
            {
                if (r.Result.Status == FileOutcomeStatus.Pass) pass++;
                else fail++;
            }
            StatusLine = IsRunning
                ? $"Running… {Results.Count} / {Files.Count}  (Pass {pass}, Fail {fail})"
                : (Results.Count > 0
                    ? $"Done. {Results.Count} file(s).  Pass {pass}, Fail {fail}."
                    : MatchedSummary);
        }

        private void Marshal(Action action)
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SafeBoundary.Run("BatchVm.Dispose", () =>
            {
                _executor.ScriptProposed -= OnScriptProposed;
                _executor.FileCompleted -= OnFileCompleted;
                _executor.RunCompleted -= OnRunCompleted;
            });
        }
    }

    // Per-row view-model for the results list. Computes the display text
    // and a status glyph; nothing else.
    public sealed class BatchFileResultViewModel
    {
        public BatchFileResult Result { get; }
        public BatchFileResultViewModel(BatchFileResult r) { Result = r; }

        public string Glyph => Result.Status == FileOutcomeStatus.Pass ? "✓" : "✗";
        public string FileName => Path.GetFileName(Result.Path);
        public string StatusText => Result.Status == FileOutcomeStatus.Pass ? "PASS" : "FAIL";
        public string Phase => Result.Phase.ToString();
        public string Detail
        {
            get
            {
                if (Result.Error is not null) return Result.Error.Message;
                var lastStep = Result.Steps.LastOrDefault();
                return lastStep switch
                {
                    StepOutcome.Pass p => p.Summary,
                    StepOutcome.Failure f => f.Error.Message,
                    _ => "",
                };
            }
        }
        public bool IsFailure => Result.Status == FileOutcomeStatus.Failure;
    }
}
