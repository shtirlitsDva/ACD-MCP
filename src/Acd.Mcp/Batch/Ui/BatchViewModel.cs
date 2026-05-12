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
    public sealed partial class BatchViewModel : ObservableObject, IDisposable, IBatchUiState
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
                        IsDirty = true;
                        _executor.OnEditorTextChanged(value);
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
        bool IBatchUiState.IsDirty => IsDirty;

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

        // Bypass IsDirty side-effect: agent push + Manage-Scripts Load.
        private void SetScriptWithoutDirtyFlag(string value)
        {
            if (SetProperty(ref _currentScript, value, nameof(CurrentScript)))
                _executor.OnEditorTextChanged(value);
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
            var win = new ManageScriptsWindow(_executor, this);
            // Modal — see <open-decisions> #4. Closes on Load / Cancel.
            win.Owner = Application.Current?.MainWindow;
            win.ShowDialog();
        });

        // Called when the user picks "Load" in the Manage Scripts window.
        // Behaves the same as an agent proposal, with the dirty-prompt path.
        public void LoadSavedScript(SavedScript saved) =>
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
                SetScriptWithoutDirtyFlag(saved.Body);
                IsDirty = false;
            });

        private void OnScriptProposed(object? sender, BatchScriptProposed evt)
            => Marshal(() =>
            {
                // Unsaved-edits race resolution per spec, option (a): prompt.
                if (IsDirty && CurrentScript != evt.Saved.Body)
                {
                    var choice = MessageBox.Show(
                        $"The agent proposed an updated script '{evt.Saved.Name}'.\n\nReplace your unsaved changes?",
                        "Script proposed by agent",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (choice != MessageBoxResult.Yes) return;
                }
                SetScriptWithoutDirtyFlag(evt.Saved.Body);
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
