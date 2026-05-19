using System.IO.Pipes;

namespace Acd.Mcp.Bridge
{
    // Cheap "is the plugin pipe listening?" probe. Opens a
    // NamedPipeClientStream and awaits ConnectAsync with a tight
    // timeout. Returns true iff the connect succeeded (the connection
    // is closed immediately — this is a liveness check, not a session).
    //
    // Used in two places:
    //   1. AutoCadDiscovery — disambiguate between multiple acad.exe
    //      processes by asking which one owns acd-mcp-{pid}.
    //   2. PipeClient retry loop — same primitive backs the connect
    //      attempts, so they share semantics.
    //
    // Instance-based + virtual so tests can substitute an in-memory
    // implementation without spinning up real pipes.
    public class PipeProber
    {
        public static string PipeNameFor(int pid) => $"acd-mcp-{pid}";

        public virtual async Task<bool> IsListeningAsync(
            int pid, TimeSpan timeout, CancellationToken ct = default)
        {
            await using var client = new NamedPipeClientStream(
                ".", PipeNameFor(pid), PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                // Pipe was created then torn down between Process enum and
                // ConnectAsync — treat as "not listening" rather than fatal.
                return false;
            }
            // OperationCanceledException intentionally propagates — caller's
            // CT is authoritative.
        }
    }
}
