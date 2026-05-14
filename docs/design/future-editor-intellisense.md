<!--
Future plans: add C# autocomplete / IntelliSense to the ScriptControl
and BatchControl AvalonEdit editors.
Status: research notes, not yet scheduled. No code committed.
Author: research session 2026-05-14.
-->

<context>
Today both code editors in the WPF plugin use AvalonEdit 6.3 with only the
embedded `CSharp-Dark.xshd` syntax highlighter loaded — no semantic features.

  - `src/Acd.Mcp/Ui/ScriptControl.xaml` + `.xaml.cs` (single-drawing script editor)
  - `src/Acd.Mcp/Batch/Ui/BatchControl.xaml` + `.xaml.cs` (batch Step DSL editor)

Roslyn is already a project dependency (`Microsoft.CodeAnalysis.CSharp.Scripting`
4.12.0) because `ScriptSession` compiles and runs the C# scripts. So the
compiler half of IntelliSense is already in the build; what is missing is the
editor-side wiring (completion list, signature help, diagnostics).
</context>

<goal>
Bring per-keystroke C# completion, signature help and live diagnostics to both
editors. Completion must reflect the **actual** runtime surface — the
`Doc/Db/Ed/CivilDoc` globals, the imports and the assembly references that
`ScriptSession` (single-drawing) and the batch runtime expose. Anything less
will lie to the user and undermine trust in the tool.
</goal>

<options-considered>

<option-a-roslynpad-editor>
**RoslynPad.Editor.Windows** (current 4.12.1, net8.0-windows7.0).

A drop-in `RoslynCodeEditor` control that subclasses AvalonEdit's `TextEditor`
and ships pre-wired completion, signature help, squigglies and quick-fixes,
driven by a Roslyn workspace.

Why it fits us:
  - Same AvalonEdit 6.3.0.90 we already use — no editor upgrade.
  - Same Microsoft.CodeAnalysis 4.12 line — no Roslyn version drift.
  - Native support for `SourceCodeKind.Script` and a custom globals type, which
    is exactly the model `ScriptSession` already uses.
  - Setup is `editor.InitializeAsync(host, ..., SourceCodeKind.Script,
    globalsType: typeof(ScriptGlobals))`.

Cost:
  - 14 transitive packages (System.Composition / MEF, VS Threading,
    Microsoft.CodeAnalysis.*.Features, RoslynPad.Roslyn, RoslynPad.Themes).
  - Meaningful bin/ growth.
</option-a-roslynpad-editor>

<option-b-roll-own-workspace>
Bare Roslyn: `AdhocWorkspace` + `CompletionService.GetService(document)` + feed
results into AvalonEdit's existing `CompletionWindow` API.

Maximum control, no MEF, no extra packages beyond what we already pull in. But
we'd be re-implementing diagnostics, signature help and quick-info from
scratch. Only worth it if Option A breaks inside AutoCAD.
</option-b-roll-own-workspace>

<option-c-snippets-only>
Static, trigger-based snippet completion using AvalonEdit's built-in
`ICompletionData` API — no Roslyn on the editor side. Member list comes from a
hand-curated catalog of the globals (`Doc`, `Db`, `Ed`, `CivilDoc`) and a few
common AutoCAD types.

Tiny effort, useful as a stopgap or fallback if A and B both fail in the
hosted ALC. Doesn't understand user-defined variables or method overloads.
</option-c-snippets-only>

</options-considered>

<caveats>

These have to be answered before we commit to Option A.

  1. **Assembly Load Context.** The plugin loads inside an isolated,
     collectible ALC via DevReload. RoslynPad.Editor.Windows is WPF, so the
     existing rule ("WPF-shared assemblies live in the default ALC") would
     apply to the editor control itself — but it pulls in MEF
     (`System.Composition`), VS Threading and the Features assemblies, none of
     which are loaded today. Risk: MEF compose failures or type-identity
     conflicts when those assemblies resolve in the plugin ALC while WPF
     lives in the default ALC. Spike in one editor first and watch the
     command-line output.

  2. **Workspace vs. scripting reference drift.** The editor uses a Roslyn
     `Workspace`; execution uses `CSharpScript`. If their reference lists
     and import lists drift, completion will hallucinate. Treat the existing
     `ScriptOptions` builder inside `ScriptSession` as the single source of
     truth and feed `RoslynHostReferences` from it — don't maintain two
     parallel lists.

  3. **AutoCAD process collisions.** AutoCAD already loads a lot of
     `Microsoft.*` assemblies. We need to verify nothing RoslynPad pulls in
     collides with versions AutoCAD ships.

  4. **Batch UI lives in the WPF plugin, runtime is AutoCAD-free.** The
     Batch editor sits in the AutoCAD-hosted assembly, so the same ALC story
     applies to it. The batch runtime (net8.0-only) is unaffected.

</caveats>

<recommended-path>

  1. **Spike Option A in `ScriptControl` only**, on a branch.
  2. Build a single `RoslynHost` whose `MetadataReferences` and `Imports` are
     derived from the same code path that builds the runtime `ScriptOptions`
     in `ScriptSession` (do not duplicate).
  3. Replace the `<avalonedit:TextEditor>` instance in `ScriptControl.xaml`
     with `RoslynCodeEditor`; remove the manual `.xshd` load (the new control
     does its own highlighting).
  4. Load the plugin in AutoCAD, exercise the editor, watch for ALC / MEF /
     XAML composition errors.
  5. If clean → roll out to `BatchControl` with the batch runtime's reference
     list as the workspace input.
  6. If A blows up in AutoCAD → fall back to **Option B** (workspace, no
     Features assemblies) or **Option C** (snippets only) as a stopgap.

</recommended-path>

<out-of-scope-for-now>

  - Cross-file navigation (Go to Definition for user-authored scripts).
  - Refactorings beyond what RoslynPad's default quick-actions provide.
  - Sharing one Roslyn workspace across both editors — start with two
    independent hosts, consolidate later if cheap.
  - Replacing the embedded `CSharp-Dark.xshd` with a Roslyn-driven semantic
    classifier for tabs that don't get a `RoslynCodeEditor`.

</out-of-scope-for-now>

<references>

  - AvalonEdit code-completion docs:
    http://avalonedit.net/documentation/html/47c58b63-f30c-4290-a2f2-881d21227446.htm
  - RoslynPad project: https://github.com/roslynpad/roslynpad
  - RoslynPad.Editor.Windows on NuGet:
    https://www.nuget.org/packages/RoslynPad.Editor.Windows/
  - RoslynPad packages overview:
    https://github.com/roslynpad/roslynpad/blob/main/docs/packages/README.md
  - MS Learn — "RoslynPad, or how to build a C# editor with Roslyn":
    https://learn.microsoft.com/en-us/shows/open-at-microsoft/roslynpad-or-how-to-build-a-csharp-editor-with-roslyn
  - CDS.CSharpScripting (WinForms wrapper; unmaintained, .NET Framework only;
    listed for completeness):
    https://github.com/nooogle/CDS.CSharpScripting

</references>
