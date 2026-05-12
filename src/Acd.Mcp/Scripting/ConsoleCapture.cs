namespace Acd.Mcp.Scripting
{
    // Per-call redirection of Console.Out / Console.Error to StringWriters so
    // anything a snippet prints lands in the response instead of disappearing
    // into AutoCAD's stdout (which is invisible to the MCP client anyway).
    //
    // Console.SetOut / SetError are process-global. AcadExecutor serialises
    // snippet execution onto the main thread, so concurrent calls cannot
    // overlap. Any unrelated background thread that writes to Console during
    // a snippet will bleed into the capture — acceptable v1 tradeoff;
    // documented in the architecture doc.
    internal sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _origOut;
        private readonly TextWriter _origErr;
        private readonly StringWriter _outBuf = new();
        private readonly StringWriter _errBuf = new();
        private bool _disposed;

        public ConsoleCapture()
        {
            _origOut = Console.Out;
            _origErr = Console.Error;
            Console.SetOut(_outBuf);
            Console.SetError(_errBuf);
        }

        public string Stdout => _outBuf.ToString();
        public string Stderr => _errBuf.ToString();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }
}
