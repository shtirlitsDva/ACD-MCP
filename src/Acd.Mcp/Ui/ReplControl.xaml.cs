using System.Windows.Controls;
using Acd.Mcp.Pipe;

namespace Acd.Mcp.Ui
{
    // Code-behind keeps one concern: bridging AvalonEdit's plain Text property
    // (which is not a DependencyProperty and so can't be data-bound from XAML)
    // to the VM's CurrentCode. Everything else is XAML binding.
    public partial class ReplControl : UserControl, IDisposable
    {
        private readonly ReplViewModel _vm;
        private bool _suppressSync;

        public ReplControl(AcadExecutor executor, ExecutionLog log)
        {
            InitializeComponent();

            _vm = new ReplViewModel(executor, log);
            DataContext = _vm;

            // Editor → VM
            Editor.TextChanged += (_, _) =>
            {
                if (_suppressSync) return;
                _vm.CurrentCode = Editor.Text;
            };

            // VM → Editor (e.g. if some future "load snippet" command sets it).
            // Guard the round-trip so we don't echo the user's typing back as an
            // edit that re-triggers OnTextChanged.
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ReplViewModel.CurrentCode)) return;
                if (Editor.Text == _vm.CurrentCode) return;
                _suppressSync = true;
                try { Editor.Text = _vm.CurrentCode; }
                finally { _suppressSync = false; }
            };
        }

        public void Dispose() => _vm.Dispose();
    }
}
