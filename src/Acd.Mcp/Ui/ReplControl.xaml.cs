using System.Windows.Controls;
using System.Xml;
using Acd.Mcp.Pipe;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Acd.Mcp.Ui
{
    // Code-behind keeps two concerns:
    //
    //  1. Bridge AvalonEdit's plain Text property (not a DependencyProperty) to the
    //     VM's CurrentCode in both directions, with a re-entrancy guard.
    //
    //  2. Load our own dark-themed C# syntax-highlighting definition from the
    //     embedded CSharp-Dark.xshd resource. We do NOT mutate
    //     HighlightingManager.Instance's shared "C#" definition — that would
    //     leak our colours into any other AvalonEdit instance running in the
    //     same AutoCAD process. This is the SDK-themed-control carve-out from
    //     the WPF theming memory: token colours bypass the WPF style system,
    //     so they ship as a separate resource.
    //
    //  Everything else is in Theme.xaml — no inline colours here.
    public partial class ReplControl : UserControl, IDisposable
    {
        private const string DarkSyntaxResourceName = "Acd.Mcp.Ui.Themes.CSharp-Dark.xshd";

        private readonly ReplViewModel _vm;
        private bool _suppressSync;

        public ReplControl(AcadExecutor executor, ExecutionLog log)
        {
            InitializeComponent();

            _vm = new ReplViewModel(executor, log);
            DataContext = _vm;

            ApplyDarkSyntax();

            Editor.TextChanged += OnEditorTextChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        // Load CSharp-Dark.xshd from this assembly's embedded resources and
        // assign it to the editor. Swallows on failure so the palette still
        // opens with default colours rather than crashing.
        private void ApplyDarkSyntax() => SafeBoundary.Run("ReplControl.ApplyDarkSyntax", () =>
        {
            var asm = typeof(ReplControl).Assembly;
            using var stream = asm.GetManifestResourceStream(DarkSyntaxResourceName);
            if (stream is null)
            {
                SafeBoundary.Info("ApplyDarkSyntax",
                    $"Embedded resource '{DarkSyntaxResourceName}' not found — falling back to default C# highlighting.");
                return;
            }

            using var reader = XmlReader.Create(stream);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            Editor.SyntaxHighlighting = definition;
        });

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
