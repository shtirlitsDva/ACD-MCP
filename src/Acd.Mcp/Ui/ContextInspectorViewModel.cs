using System.Collections.ObjectModel;
using Acd.Mcp.Scripting;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.CodeAnalysis.Scripting;

namespace Acd.Mcp.Ui
{
    // Owns the two lists shown in the palette's "Context" expander:
    //
    //   Globals   — the AcadGlobals surface (Doc/Db/Ed) re-read every refresh so
    //               the user sees the *current* active drawing, not whatever it
    //               was when the session was created.
    //
    //   Variables — every identifier the user has declared at script top-level
    //               across all submissions in the persistent ScriptState.
    //
    // Snapshot model: Refresh() rebuilds the two collections wholesale rather
    // than diffing. ScriptState.Variables is a small list (typically <20 entries
    // in an interactive REPL), so rebuild cost is negligible and the simpler
    // model avoids subtle ordering/equality bugs.
    //
    // All work runs synchronously on the WPF dispatcher thread — callers must
    // invoke Refresh() from the UI thread. Every value-rendering call is wrapped
    // in try/catch because AutoCAD DB-object ToString() can throw (disposed
    // database, transaction not open, missing active document, etc.).
    public sealed partial class ContextInspectorViewModel : ObservableObject
    {
        private const int MaxValueLength = 120;
        private const string Ellipsis = "…";

        private readonly ScriptSession _session;

        [ObservableProperty] private bool _isExpanded;

        public ObservableCollection<ContextItem> Globals { get; } = new();
        public ObservableCollection<ContextItem> Variables { get; } = new();

        public ContextInspectorViewModel(ScriptSession session)
        {
            _session = session;
        }

        public void Refresh() => SafeBoundary.Run("ContextInspector.Refresh", () =>
        {
            Globals.Clear();
            Variables.Clear();

            Globals.Add(MakeGlobal("Doc", () => _session.Globals.Doc));
            Globals.Add(MakeGlobal("Db",  () => _session.Globals.Db));
            Globals.Add(MakeGlobal("Ed",  () => _session.Globals.Ed));

            var state = _session.CurrentState;
            if (state is null) return;
            foreach (var v in state.Variables)
                Variables.Add(MakeVariable(v));
        });

        private static ContextItem MakeGlobal(string name, Func<object?> get)
        {
            try
            {
                var value = get();
                return new ContextItem
                {
                    Name = name,
                    TypeName = value?.GetType().Name ?? "<null>",
                    Value = Truncate(SafeToString(value)),
                };
            }
            catch (Exception ex)
            {
                return new ContextItem
                {
                    Name = name,
                    TypeName = "(unavailable)",
                    Value = Truncate($"<{ex.GetType().Name}: {ex.Message}>"),
                };
            }
        }

        private static ContextItem MakeVariable(ScriptVariable v) => new()
        {
            Name = v.Name,
            TypeName = v.Type?.Name ?? "?",
            Value = Truncate(SafeToString(v.Value)),
        };

        private static string SafeToString(object? value)
        {
            if (value is null) return "null";
            try { return value.ToString() ?? "<null-ToString>"; }
            catch (Exception ex) { return $"<{ex.GetType().Name} in ToString()>"; }
        }

        private static string Truncate(string s) =>
            s.Length <= MaxValueLength ? s : s.Substring(0, MaxValueLength) + Ellipsis;
    }
}
