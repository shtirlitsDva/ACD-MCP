using System.Collections.ObjectModel;
using System.Windows;
using Acd.Mcp.Batch;
using Acd.Mcp.Batch.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acd.Mcp.Batch.Ui
{
    public partial class ManageScriptsWindow : Window
    {
        private readonly ManageScriptsViewModel _vm;

        public ManageScriptsWindow(BatchExecutor executor, BatchViewModel paletteVm)
        {
            InitializeComponent();
            _vm = new ManageScriptsViewModel(executor, paletteVm, this);
            DataContext = _vm;
        }
    }

    // Lives next to the window because it's tightly coupled to it: Load
    // dismisses the window, Save-as / Rename pop input dialogs, etc.
    internal sealed partial class ManageScriptsViewModel : ObservableObject
    {
        private readonly BatchExecutor _executor;
        private readonly BatchViewModel _paletteVm;
        private readonly Window _window;

        public ObservableCollection<SavedScript> Items { get; } = new();

        [ObservableProperty] private SavedScript? _selected;

        public ManageScriptsViewModel(BatchExecutor executor, BatchViewModel paletteVm, Window window)
        {
            _executor = executor;
            _paletteVm = paletteVm;
            _window = window;
            Refresh();
            // If the editor's current script matches a saved entry, preselect it.
            foreach (var s in Items)
                if (s.Body == executor.CurrentScript) { Selected = s; break; }
        }

        private void Refresh()
        {
            Items.Clear();
            foreach (var s in _executor.Scripts.List(ScriptFlavor.Batch, limit: 200, offset: 0))
                Items.Add(s);
        }

        [RelayCommand]
        private void Load() => SafeBoundary.Run("ManageScripts.Load", () =>
        {
            if (Selected is null) return;
            _paletteVm.LoadSavedScript(Selected);
            _window.DialogResult = true;
            _window.Close();
        });

        [RelayCommand]
        private void SaveAs() => SafeBoundary.Run("ManageScripts.SaveAs", () =>
        {
            var name = Prompts.AskForString(_window, "Save as new", "Telegram-style name (e.g. set-layer-transparency):");
            if (string.IsNullOrWhiteSpace(name)) return;
            _executor.Scripts.Save(ScriptFlavor.Batch, name, _executor.CurrentScript);
            Refresh();
        });

        [RelayCommand]
        private void Rename() => SafeBoundary.Run("ManageScripts.Rename", () =>
        {
            if (Selected is null) return;
            var name = Prompts.AskForString(_window, "Rename", $"New name for '{Selected.Name}':");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { _executor.Scripts.Rename(ScriptFlavor.Batch, Selected.Name, name); }
            catch (System.IO.IOException ex)
            {
                MessageBox.Show(_window, ex.Message, "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Refresh();
        });

        [RelayCommand]
        private void Delete() => SafeBoundary.Run("ManageScripts.Delete", () =>
        {
            if (Selected is null) return;
            var ok = MessageBox.Show(_window,
                $"Delete saved script '{Selected.Name}'?",
                "Confirm delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;
            _executor.Scripts.Delete(ScriptFlavor.Batch, Selected.Name);
            Refresh();
        });

        [RelayCommand]
        private void Close()
        {
            _window.DialogResult = false;
            _window.Close();
        }
    }

    // Tiny inline prompt helper. WPF doesn't ship Microsoft.VisualBasic's
    // InputBox; we wire up a one-off Window with a TextBox.
    internal static class Prompts
    {
        public static string? AskForString(Window owner, string title, string prompt)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 140,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)owner.FindResource("Brush.Bg.Window"),
            };
            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = (System.Windows.Media.Brush)owner.FindResource("Brush.Fg.Primary"),
            });
            var tb = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 12) };
            stack.Children.Add(tb);
            var row = new System.Windows.Controls.DockPanel();
            var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 70 };
            System.Windows.Controls.DockPanel.SetDock(ok, System.Windows.Controls.Dock.Right);
            row.Children.Add(cancel);
            row.Children.Add(ok);
            stack.Children.Add(row);
            win.Content = stack;
            string? result = null;
            ok.Click += (_, _) => { result = tb.Text; win.DialogResult = true; };
            tb.Focus();
            win.ShowDialog();
            return result;
        }
    }
}
