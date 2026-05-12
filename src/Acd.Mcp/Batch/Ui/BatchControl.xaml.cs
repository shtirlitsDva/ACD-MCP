using System;
using System.Windows.Controls;
using System.Xml;
using Acd.Mcp.Batch.Runtime;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Acd.Mcp.Batch.Ui
{
    // Mirrors the REPL palette's pattern: AvalonEdit's Text isn't a DP, so
    // we two-way sync it to the VM's CurrentScript with a re-entrancy
    // guard, and we load the same dark XSHD definition.
    public partial class BatchControl : UserControl, IDisposable
    {
        private const string DarkSyntaxResourceName = "Acd.Mcp.Ui.Themes.CSharp-Dark.xshd";

        private readonly BatchViewModel _vm;
        private bool _suppressSync;

        public BatchControl(BatchExecutor executor)
        {
            InitializeComponent();
            _vm = new BatchViewModel(executor);
            DataContext = _vm;

            ApplyDarkSyntax();
            Editor.Text = _vm.CurrentScript;
            Editor.TextChanged += OnEditorTextChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        // Expose the VM so the palette can publish it as the BatchUiState
        // implementation the pipe handler reads from.
        public BatchViewModel ViewModel => _vm;

        private void ApplyDarkSyntax() => SafeBoundary.Run("BatchControl.ApplyDarkSyntax", () =>
        {
            var asm = typeof(BatchControl).Assembly;
            using var stream = asm.GetManifestResourceStream(DarkSyntaxResourceName);
            if (stream is null) return;
            using var reader = XmlReader.Create(stream);
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        });

        private void OnEditorTextChanged(object? sender, EventArgs e) =>
            SafeBoundary.Run("BatchControl.OnEditorTextChanged", () =>
            {
                if (_suppressSync) return;
                _vm.CurrentScript = Editor.Text;
            });

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            SafeBoundary.Run("BatchControl.OnVmPropertyChanged", () =>
            {
                if (e.PropertyName != nameof(BatchViewModel.CurrentScript)) return;
                if (Editor.Text == _vm.CurrentScript) return;
                _suppressSync = true;
                try { Editor.Text = _vm.CurrentScript; }
                finally { _suppressSync = false; }
            });

        public void Dispose() => SafeBoundary.Run("BatchControl.Dispose", () =>
        {
            Editor.TextChanged -= OnEditorTextChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Dispose();
        });
    }
}
