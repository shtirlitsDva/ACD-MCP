using System.Windows.Controls;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Ui
{
    // Code-behind keeps one concern: bridging AvalonEdit's plain Text property
    // (which is not a DependencyProperty and so can't be data-bound from XAML)
    // to the VM's CurrentCode. Everything else is XAML binding.
    //
    // Both directions of the sync are wrapped via SafeBoundary so a glitch in
    // AvalonEdit's text events or the VM property setter can't crash the
    // dispatcher.
    public partial class ReplControl : UserControl, IDisposable
    {
        private readonly ReplViewModel _vm;
        private bool _suppressSync;

        public ReplControl(AcadExecutor executor, ExecutionLog log)
        {
            InitializeComponent();

            _vm = new ReplViewModel(executor, log);
            DataContext = _vm;

            Editor.TextChanged += OnEditorTextChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        private void OnEditorTextChanged(object? sender, EventArgs e) =>
            SafeBoundary.Run("ReplControl.Editor.TextChanged", () =>
            {
                if (_suppressSync) return;
                _vm.CurrentCode = Editor.Text;
            });

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            SafeBoundary.Run("ReplControl.VmPropertyChanged", () =>
            {
                if (e.PropertyName != nameof(ReplViewModel.CurrentCode)) return;
                if (Editor.Text == _vm.CurrentCode) return;
                _suppressSync = true;
                try { Editor.Text = _vm.CurrentCode; }
                finally { _suppressSync = false; }
            });

        public void Dispose() =>
            SafeBoundary.Run("ReplControl.Dispose", () =>
            {
                Editor.TextChanged -= OnEditorTextChanged;
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.Dispose();
            });
    }
}
