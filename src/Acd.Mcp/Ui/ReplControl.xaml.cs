using System.Windows.Controls;
using System.Windows.Media;
using Acd.Mcp.Pipe;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Acd.Mcp.Ui
{
    // Code-behind keeps two concerns:
    //
    //  1. Bridge AvalonEdit's plain Text property (not a DependencyProperty) to the
    //     VM's CurrentCode in both directions, with a re-entrancy guard.
    //
    //  2. Apply dark-mode colours to AvalonEdit's syntax-highlighting table.
    //     AvalonEdit owns its own per-token colour model (HighlightingColor with
    //     HighlightingBrush); these are NOT WPF brushes resolved from a Style or
    //     ResourceDictionary, so XAML cannot reach them. The memory entry on WPF
    //     theming calls this out specifically: SDK-themed controls bypass the
    //     style system for their internals.
    //
    //  Everything visual is otherwise in Theme.xaml — no inline colours here.
    public partial class ReplControl : UserControl, IDisposable
    {
        private readonly ReplViewModel _vm;
        private bool _suppressSync;

        public ReplControl(AcadExecutor executor, ExecutionLog log)
        {
            InitializeComponent();

            _vm = new ReplViewModel(executor, log);
            DataContext = _vm;

            ApplyDarkSyntaxColors();

            Editor.TextChanged += OnEditorTextChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        // Recolour the default "C#" HighlightingDefinition. The named colour list
        // ("Keyword", "String", etc.) is defined in AvalonEdit's built-in CSharp-Mode.xshd.
        // We override only the Foreground so font weights / italics stay default.
        private void ApplyDarkSyntaxColors()
        {
            var def = Editor.SyntaxHighlighting;
            if (def is null) return;

            SetColor(def, "Keyword",       FromHex("#FF569CD6")); // VS Code dark "blue"
            SetColor(def, "String",        FromHex("#FFCE9178")); // soft salmon
            SetColor(def, "Char",          FromHex("#FFCE9178"));
            SetColor(def, "Comment",       FromHex("#FF6A9955")); // muted green
            SetColor(def, "Punctuation",   FromHex("#FFE1E1E1"));
            SetColor(def, "ReferenceTypes", FromHex("#FF4EC9B0")); // teal for types
            SetColor(def, "MethodCall",    FromHex("#FFDCDCAA")); // warm yellow
            SetColor(def, "NumberLiteral", FromHex("#FFB5CEA8"));
            SetColor(def, "Digits",        FromHex("#FFB5CEA8"));
            SetColor(def, "ValueTypes",    FromHex("#FF569CD6"));
            SetColor(def, "GetSetAddRemove", FromHex("#FF569CD6"));
            SetColor(def, "TrueFalse",     FromHex("#FF569CD6"));
            SetColor(def, "TypeKeywords",  FromHex("#FF569CD6"));
        }

        private static void SetColor(IHighlightingDefinition def, string name, Color foreground)
        {
            // Not every C# highlighting build has every name — guard each one so
            // an SDK upgrade that drops a category doesn't crash plugin load.
            var hc = def.GetNamedColor(name);
            if (hc is null) return;
            hc.Foreground = new SimpleHighlightingBrush(foreground);
        }

        private static Color FromHex(string hex) =>
            (Color)ColorConverter.ConvertFromString(hex);

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
