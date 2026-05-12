using System.Diagnostics;
using System.IO;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Acd.Mcp
{
    // Single sink for every exception that would otherwise escape a process-level
    // boundary (an AutoCAD [CommandMethod], a WPF dispatcher continuation, a
    // background pipe task, an event subscriber). Catching an exception here is
    // ALWAYS preferable to crashing AutoCAD — diagnose by reading the log.
    //
    // Each report goes three places:
    //   1. AutoCAD editor (best-effort; may itself fail e.g. when no MdiActiveDocument)
    //   2. System.Diagnostics.Trace (DebugView etc.)
    //   3. Rolling-append file at %LOCALAPPDATA%\Acd.Mcp\log.txt
    //
    // The file is the source of truth — the editor is a convenience, and Trace is
    // for attached debuggers. The file lock serialises writers so the pipe's
    // threadpool tasks and the WPF dispatcher don't tear each other's lines.
    internal static class SafeBoundary
    {
        private static readonly object _fileLock = new();
        private static string? _logFile;
        private static bool _processHooksInstalled;

        public static string? LogFile => _logFile;

        // Idempotent. Safe to call from plugin Initialize even if a prior load
        // already installed the hooks (we'd just no-op).
        public static void EnsureInitialized()
        {
            if (_logFile is null)
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Acd.Mcp");
                    Directory.CreateDirectory(dir);
                    _logFile = Path.Combine(dir, "log.txt");
                }
                catch
                {
                    // Even logging setup must not throw. Without a file we still
                    // have Trace and the editor.
                }
            }

            if (!_processHooksInstalled)
            {
                _processHooksInstalled = true;

                // Unobserved Task exceptions (a Task GC'd before its exception was
                // observed) get logged instead of taking down the process on the
                // finalizer thread.
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    Report(e.Exception, "TaskScheduler.UnobservedTaskException");
                    e.SetObserved();
                };
            }
        }

        public static void Run(string context, Action body)
        {
            try { body(); }
            catch (Exception ex) { Report(ex, context); }
        }

        public static async Task RunAsync(string context, Func<Task> body)
        {
            try { await body().ConfigureAwait(false); }
            catch (Exception ex) { Report(ex, context); }
        }

        public static T? RunOrDefault<T>(string context, Func<T> body, T? fallback = default)
        {
            try { return body(); }
            catch (Exception ex) { Report(ex, context); return fallback; }
        }

        public static void Report(Exception ex, string context)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var msg = $"[ACD-MCP] EXCEPTION in {context}\n{ex}";

            try { Trace.WriteLine($"{stamp}  {msg}"); } catch { }

            TryAppendFile($"{stamp}  {msg}");

            try
            {
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage("\n" + msg + "\n");
            }
            catch
            {
                // Editor write itself failed (e.g. document was closing) — file
                // and Trace already captured the original, swallow.
            }
        }

        // Useful for non-exception diagnostics: "the listener started", "the palette
        // opened". Routed through the same sinks so everything's in one place.
        public static void Info(string context, string message)
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[ACD-MCP] {context}: {message}";
            try { Trace.WriteLine($"{stamp}  {line}"); } catch { }
            TryAppendFile($"{stamp}  {line}");
        }

        private static void TryAppendFile(string line)
        {
            var path = _logFile;
            if (path is null) return;
            try
            {
                lock (_fileLock)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging cannot itself crash us. If the disk is full or perms
                // are wrong, Trace and the editor remain.
            }
        }
    }
}
