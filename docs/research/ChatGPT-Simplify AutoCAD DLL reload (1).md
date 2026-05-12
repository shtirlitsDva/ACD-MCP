# Simplify AutoCAD DLL reload

**User:** Anonymous  
**Created:**   
**Updated:**   
**Exported:** 5/12/2026 11:16:45  
**Link:** [https://chatgpt.com/share/699f02c0-2da8-8008-9a5f-4ca04dd75dcf](https://chatgpt.com/share/699f02c0-2da8-8008-9a5f-4ca04dd75dcf)  

## Response:
Got it ✅ — if the loader is the permanent extension and the reloadable implementation is just a backend that conforms to an interface, then the framework doesn’t need `IExtensionApplication` or `IReloadableExtensionApplication`. The loader owns lifecycle and AutoCAD integration. The backend assembly just needs to implement an interface defined in the loader.

Here’s a **basic skeleton** of what that framework looks like:

```
// --------------------
// Loader project
// --------------------

using Autodesk.AutoCAD.Runtime;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

[assembly: CommandClass(typeof(MyPlugin.Loader))]

namespace MyPlugin
{
    // Define the contract that reloadable assemblies must implement
    public interface IMyPlugin
    {
        void DoSomething();  // example command handler
    }

    public class Loader : IExtensionApplication
    {
        private AssemblyLoadContext? _alc;
        private IMyPlugin? _plugin;

        public void Initialize()
        {
            LoadPlugin();
        }

        public void Terminate()
        {
            UnloadPlugin();
        }

        [CommandMethod("MYCMD")]
        public void MyCommand()
        {
            _plugin?.DoSomething();
        }

        private void LoadPlugin()
        {
            UnloadPlugin();

            string pluginPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
                "MyPlugin.Reloadable.dll");

            _alc = new AssemblyLoadContext("ReloadablePluginContext", isCollectible: true);
            using var stream = new FileStream(pluginPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var assembly = _alc.LoadFromStream(stream);

            var pluginType = assembly.GetType("MyPlugin.Reloadable.PluginImpl");
            if (pluginType == null)
                throw new InvalidOperationException("PluginImpl not found in reloadable assembly");

            _plugin = (IMyPlugin)Activator.CreateInstance(pluginType)!;
        }

        private void UnloadPlugin()
        {
            _plugin = null;
            if (_alc != null)
            {
                _alc.Unload();
                _alc = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        [CommandMethod("RELOAD_MYPLUGIN")]
        public void ReloadCommand()
        {
            LoadPlugin();
        }
    }
}
```

```
// --------------------
// Reloadable project
// --------------------

namespace MyPlugin.Reloadable
{
    using MyPlugin; // references the Loader assembly

    public class PluginImpl : IMyPlugin
    {
        public void DoSomething()
        {
            Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPluginImpl.DoSomething() called.");
        }
    }
}
```

### Key points:

*   The **loader** assembly is always present in AutoCAD and registers commands (`MYCMD`, `RELOAD_MYPLUGIN`).
*   The **reloadable** assembly implements `IMyPlugin`.
*   When you rebuild `MyPlugin.Reloadable.dll`, you just run `RELOAD_MYPLUGIN` in AutoCAD — the loader unloads the old ALC, reloads the new assembly, and updates `_plugin`.
*   AutoCAD never holds a reference to code in the reloadable assembly. It only knows about the loader.

This way, you can iterate on the reloadable component without restarting AutoCAD.

* * *

Would you like me to extend this so that **multiple reloadable plugins** can coexist (i.e. the loader manages several interfaces/assemblies at once), or do you just want the single-plugin pattern for now?

## Prompt:
I would like to verify that the plugin is being unloaded and reloaded, via its module handle:

   public class PlugInImpl : IReloadablePlugIn
   {
      public void DoSomething()
      {
         Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nPluginImpl.DoSomething() called.");
         Debug.WriteLine("DoSomething");
         Assembly asm = this.GetType().Assembly;
         /// Get BaseAddress of the main module of this assembly
         /// and display it in call to Debug.WriteLine:
      }
   }

## Response:
Perfect way to check unloading 👍 — you can verify whether the reloadable DLL actually leaves memory by logging its module base address on each reload.

In .NET, there’s no direct managed API for the base address, but you can P/Invoke `GetModuleHandle` / `GetModuleInformation` from Win32. Here’s a working snippet you can drop into your reloadable `PlugInImpl` class:

```
using Autodesk.AutoCAD.ApplicationServices.Core;
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MyPlugin.Reloadable
{
    public class PlugInImpl : IMyPlugin
    {
        public void DoSomething()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\nPluginImpl.DoSomething() called.");
            Debug.WriteLine("DoSomething");

            Assembly asm = GetType().Assembly;
            string location = asm.Location;

            IntPtr hModule = GetModuleHandle(location);
            if (hModule == IntPtr.Zero)
            {
                Debug.WriteLine($"GetModuleHandle failed for {location}, error={Marshal.GetLastWin32Error()}");
                return;
            }

            MODULEINFO info = new MODULEINFO();
            if (!GetModuleInformation(Process.GetCurrentProcess().Handle, hModule, out info, (uint)Marshal.SizeOf<MODULEINFO>()))
            {
                Debug.WriteLine($"GetModuleInformation failed, error={Marshal.GetLastWin32Error()}");
                return;
            }

            Debug.WriteLine($"Assembly {asm.GetName().Name} base address = 0x{info.lpBaseOfDll.ToInt64():X}");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetModuleInformation(
            IntPtr hProcess,
            IntPtr hModule,
            out MODULEINFO lpmodinfo,
            uint cb);

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }
    }
}
```

### How it works

*   `asm.Location` gives you the file path of the reloadable DLL.
*   `GetModuleHandle(location)` resolves the module handle for that DLL in the current process.
*   `GetModuleInformation` provides `lpBaseOfDll` (base address), which you can log.

### Verification

*   Run `MYCMD` → note the base address in the **Output window** (Debug).
*   Rebuild `MyPlugin.Reloadable.dll`, then run `RELOAD_MYPLUGIN`.
*   Run `MYCMD` again → the base address should change.
*   If the base address stays the same, AutoCAD is still holding onto the old assembly.

* * *

Do you want me to also add **a forced GC check** in the loader that will log whether the old `AssemblyLoadContext` was actually collected (so you can be sure the unload worked)?

## Prompt:
Is there a cleaner way to do this using System.ProcessModule ?

## Response:
Yes 👍 — you can do this much more cleanly using the managed API without going into `psapi.dll`.  
Every `Process` has a `Modules` collection of `ProcessModule` objects, each of which exposes a `BaseAddress` property.

Here’s a cleaner version of your logging inside `PlugInImpl`:

```
using Autodesk.AutoCAD.ApplicationServices.Core;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace MyPlugin.Reloadable
{
    public class PlugInImpl : IMyPlugin
    {
        public void DoSomething()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage("\nPluginImpl.DoSomething() called.");
            Debug.WriteLine("DoSomething");

            Assembly asm = GetType().Assembly;
            string moduleName = System.IO.Path.GetFileName(asm.Location);

            // Find the matching module in the current process
            var currentProcess = Process.GetCurrentProcess();
            ProcessModule? module = currentProcess.Modules
                .Cast<ProcessModule>()
                .FirstOrDefault(m =>
                    string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));

            if (module != null)
            {
                Debug.WriteLine(
                    $"Assembly {asm.GetName().Name} base address = 0x{module.BaseAddress.ToInt64():X}");
            }
            else
            {
                Debug.WriteLine($"Could not find module for {moduleName}");
            }
        }
    }
}
```

### Advantages

*   No `DllImport`, no unmanaged interop.
*   Uses `ProcessModule.BaseAddress` directly.
*   The `Modules` collection is safe and automatically gives you the information you want.

### Caveat

`Process.Modules` walks the process module list, which means **only native modules (DLLs, EXEs)**.  
Managed assemblies _are_ backed by a native image when loaded (PE file mapped into memory), so this works fine for verifying the reload/unload cycle.

* * *

Would you like me to also show how to log the **`AssemblyLoadContext` identity** alongside the base address, so you can clearly see that a _new_ ALC was created and old one collected? That way you’d have both native (base address) and managed (ALC hash) confirmation.

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