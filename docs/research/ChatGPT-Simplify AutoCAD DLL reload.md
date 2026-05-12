# Simplify AutoCAD DLL reload

**User:** Anonymous  
**Created:**   
**Updated:**   
**Exported:** 5/12/2026 11:14:01  
**Link:** [https://chatgpt.com/share/699f02c0-2da8-8008-9a5f-4ca04dd75dcf](https://chatgpt.com/share/699f02c0-2da8-8008-9a5f-4ca04dd75dcf)  

## Response:
Yes — you don’t have to restrict yourself to single-line expressions like `CSharpScript.EvaluateAsync`. You can absolutely build a little “scratchpad REPL” that takes multiple user statements, wraps them into the body of a method, compiles them dynamically, and then executes that method.

Here’s the general pattern:

* * *

### 1\. Wrap user code in a static method

Suppose the user enters multiple lines in a textbox, e.g.:

```
var pt = new Autodesk.AutoCAD.Geometry.Point3d(1, 2, 3);
Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\nPoint: {pt}");
using(var tr = Application.DocumentManager.MdiActiveDocument.Database.TransactionManager.StartTransaction())
{
    // do work here
}
```

You can inject this into a static method:

```
public static class UserScript
{
    public static void Run()
    {
        // user code goes here
    }
}
```

* * *

### 2\. Compile dynamically with Roslyn

Use `Microsoft.CodeAnalysis.CSharp` to compile:

```
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

public static class SnippetRunner
{
    public static void RunSnippet(string userCode)
    {
        string source = $@"
using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;

public static class UserScript
{{
    public static void Run()
    {{
        {userCode}
    }}
}}";

        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Application).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Point3d).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Editor).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "UserSnippet",
            new[] { syntaxTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            throw new Exception(string.Join("\n", result.Diagnostics));
        }

        ms.Seek(0, SeekOrigin.Begin);
        var asm = Assembly.Load(ms.ToArray());
        var type = asm.GetType("UserScript");
        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        method.Invoke(null, null);
    }
}
```

* * *

### 3\. Integrating with AutoCAD

*   You’d wire this into your loader/REPL command.
*   A small docked palette (with a multiline textbox + Run button) could be the UI.
*   When the user clicks **Run**, take the textbox content, call `SnippetRunner.RunSnippet(code)`.
*   Since you’re referencing `AutoCAD.ApplicationServices`, `AutoCAD.EditorInput`, etc., the snippet can directly access them.

* * *

### 4\. Safety considerations

You probably want to sandbox:

*   Filter which assemblies are referenced.
*   Optionally wrap the user snippet in `try/catch` and redirect exceptions to AutoCAD’s `Editor.WriteMessage`.
*   Disallow `unsafe` code by default in the `CSharpCompilationOptions`.
*   You could further restrict the namespace imports if you don’t want `System.IO` or `System.Net` accessible.

* * *

✅ With this approach you get:

*   Multi-line snippets.
*   Variables, `using` statements, `try/catch`, etc.
*   Access to AutoCAD’s API in a safe and controlled way.

* * *

Would you like me to extend this with a **palette-based REPL UI** example (a small docked window in AutoCAD with a textbox + Run button), or would you prefer to first keep it command-line driven (`[CommandMethod]` that pops a prompt for input)?

## Prompt:
Yes, please implement this as a dockable PaletteSet/Palette with a RichTextBox control and a run button

## Response:
Perfect — a dockable palette is the most natural REPL host inside AutoCAD. You can host a **WinForms UserControl** in a `PaletteSet`, add a `RichTextBox` for code input, and a `Button` to execute the snippet through the Roslyn runner.

Here’s a working sketch:

* * *

### 1\. The Snippet Runner (from earlier)

Put this into a helper class:

```
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

public static class SnippetRunner
{
    public static void RunSnippet(string userCode)
    {
        string source = $@"
using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;

public static class UserScript
{{
    public static void Run()
    {{
        try
        {{
            {userCode}
        }}
        catch(Exception ex)
        {{
            Autodesk.AutoCAD.ApplicationServices.Application
                .DocumentManager.MdiActiveDocument.Editor
                .WriteMessage($""\nException: {{ex.Message}}"");
        }}
    }}
}}";

        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Application).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Point3d).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Editor).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "UserSnippet",
            new[] { syntaxTree },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var editor = Application.DocumentManager.MdiActiveDocument.Editor;
            foreach (var d in result.Diagnostics)
                editor.WriteMessage($"\n{d}");
            return;
        }

        ms.Seek(0, SeekOrigin.Begin);
        var asm = Assembly.Load(ms.ToArray());
        var type = asm.GetType("UserScript");
        var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        method.Invoke(null, null);
    }
}
```

* * *

### 2\. The Palette UI (UserControl)

Create a simple WinForms UserControl:

```
using System;
using System.Windows.Forms;

public class SnippetPalette : UserControl
{
    private readonly RichTextBox _editor;
    private readonly Button _runButton;

    public SnippetPalette()
    {
        Dock = DockStyle.Fill;

        _editor = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 10),
            WordWrap = false
        };

        _runButton = new Button
        {
            Text = "Run",
            Dock = DockStyle.Bottom,
            Height = 30
        };
        _runButton.Click += RunButton_Click;

        Controls.Add(_editor);
        Controls.Add(_runButton);
    }

    private void RunButton_Click(object? sender, EventArgs e)
    {
        SnippetRunner.RunSnippet(_editor.Text);
    }
}
```

* * *

### 3\. The PaletteSet Host

Register this with AutoCAD using a command:

```
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(YourNamespace.SnippetCommands))]

namespace YourNamespace
{
    public class SnippetCommands
    {
        private static PaletteSet? _palette;

        [CommandMethod("SNIPPET_REPL")]
        public void ShowSnippetPalette()
        {
            if (_palette == null)
            {
                _palette = new PaletteSet("C# REPL")
                {
                    Style = PaletteSetStyles.ShowCloseButton |
                            PaletteSetStyles.ShowPropertiesMenu |
                            PaletteSetStyles.Snappable
                };
                _palette.Size = new System.Drawing.Size(600, 400);
                _palette.Add("Editor", new SnippetPalette());
            }

            _palette.Visible = true;
        }
    }
}
```

* * *

### 4\. Usage

*   Load your DLL into AutoCAD.
*   Run the command:
    ```
    SNIPPET_REPL
    ```
*   A dockable window appears with a text editor + **Run** button.
*   Type multi-line code snippets (with variables, `using()`, etc.) and click **Run**.
*   Output or exceptions are written to AutoCAD’s command line.

* * *

✅ This gives you a **full multi-line REPL inside AutoCAD**, with:

*   Editable, scrollable RichTextBox.
*   Immediate compile + execution.
*   Access to AutoCAD API (through added references).

* * *

Would you like me to also add **intellisense-like features** (e.g., syntax highlighting or autocomplete) in the RichTextBox, or keep it as a plain editor for now?

## Prompt:
Yes, please add syntax highlight/autocomplete

## Response:
Great — let’s level up your REPL palette with **syntax highlighting** and a basic **autocomplete list**.

A plain `RichTextBox` won’t cut it here. For C#-aware editing inside AutoCAD you can use a lightweight text editor component that supports:

*   Coloring of C# keywords, strings, comments.
*   A popup autocomplete menu for common C# terms or AutoCAD API classes.

The most practical choice for in-process AutoCAD plugins is **FastColoredTextBox**. It’s a single WinForms control (just add the NuGet package) and works inside a `PaletteSet`.

* * *

1\. Install FastColoredTextBox
------------------------------

Add the NuGet package:

```
Install-Package FastColoredTextBox
```

* * *

2\. Syntax Highlight + Autocomplete Control
-------------------------------------------

```
using System;
using System.Drawing;
using System.Windows.Forms;
using FastColoredTextBoxNS;

public class SnippetPalette : UserControl
{
    private readonly FastColoredTextBox _editor;
    private readonly Button _runButton;
    private readonly AutocompleteMenu _autocomplete;

    public SnippetPalette()
    {
        Dock = DockStyle.Fill;

        _editor = new FastColoredTextBox
        {
            Dock = DockStyle.Fill,
            Language = Language.CSharp,
            Font = new Font("Consolas", 10),
            BackColor = Color.White,
            ForeColor = Color.Black,
            AutoIndent = true,
            AutoIndentExistingLines = true,
            WordWrap = false,
        };

        // autocomplete
        _autocomplete = new AutocompleteMenu(_editor)
        {
            SearchPattern = @"[\w\.:=!<>]"
        };

        // a starter dictionary (expand as you like)
        _autocomplete.Items.SetAutocompleteItems(new[]
        {
            "using", "public", "private", "class", "void",
            "var", "string", "int", "double",
            "Application", "DocumentManager", "Editor",
            "Point3d", "Line", "Circle",
            "foreach", "if", "else", "while", "for",
        });

        _runButton = new Button
        {
            Text = "Run",
            Dock = DockStyle.Bottom,
            Height = 30
        };
        _runButton.Click += RunButton_Click;

        Controls.Add(_editor);
        Controls.Add(_runButton);
    }

    private void RunButton_Click(object? sender, EventArgs e)
    {
        SnippetRunner.RunSnippet(_editor.Text);
    }
}
```

* * *

3\. Palette Host (same as before)
---------------------------------

```
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(YourNamespace.SnippetCommands))]

namespace YourNamespace
{
    public class SnippetCommands
    {
        private static PaletteSet? _palette;

        [CommandMethod("SNIPPET_REPL")]
        public void ShowSnippetPalette()
        {
            if (_palette == null)
            {
                _palette = new PaletteSet("C# REPL")
                {
                    Style = PaletteSetStyles.ShowCloseButton |
                            PaletteSetStyles.ShowPropertiesMenu |
                            PaletteSetStyles.Snappable
                };
                _palette.Size = new System.Drawing.Size(700, 500);
                _palette.Add("Editor", new SnippetPalette());
            }

            _palette.Visible = true;
        }
    }
}
```

* * *

4\. Behavior
------------

*   When you run `SNIPPET_REPL`, you’ll now see a **syntax-colored editor**.
*   Keywords, strings, comments are highlighted automatically.
*   Typing `App` will bring up **autocomplete suggestions** like `Application`.
*   Clicking **Run** still compiles + executes the snippet inside AutoCAD.

* * *

5\. Possible Enhancements
-------------------------

*   Add more AutoCAD types to the autocomplete list dynamically (`typeof(Editor).Assembly.GetTypes()` filtered by namespace).
*   Use different highlight colors for keywords vs. strings vs. comments.
*   Add keyboard shortcut (`Ctrl+Enter`) to run snippets without clicking the button.

* * *

Do you want me to show you how to **auto-populate the autocomplete list** from the AutoCAD assemblies (so that all public API classes show up), instead of manually listing them?



---
Powered by [ChatGPT Exporter](https://www.chatgptexporter.com)