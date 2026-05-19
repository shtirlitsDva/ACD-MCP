using System.IO.Pipes;
using Acd.Mcp.Bridge;
using Xunit;

namespace Acd.Mcp.Bridge.Tests
{
    // Real-pipe integration of PipeProber. Spins up a NamedPipeServerStream
    // with the acd-mcp-{pid} naming convention, asserts the prober returns
    // true; tears it down, asserts the prober returns false.
    //
    // Pseudo-PID is large + random to avoid colliding with any real
    // listener on the box (CI runners almost never have a 999999-PID
    // process, dev boxes don't either).
    public class PipeProberTests
    {
        // Pick a PID that won't collide with a real acad.exe; the prober
        // only uses it to construct the pipe name, so any int is fine.
        private static readonly int FakePid = 999900 + Random.Shared.Next(0, 99);

        [Fact]
        public async Task LiveListener_IsDetected()
        {
            var pipeName = PipeProber.PipeNameFor(FakePid);
            await using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var serverTask = server.WaitForConnectionAsync();
            try
            {
                var prober = new PipeProber();
                var detected = await prober.IsListeningAsync(FakePid, TimeSpan.FromMilliseconds(500));
                Assert.True(detected, "Prober should detect a live listener.");
            }
            finally
            {
                if (server.IsConnected) server.Disconnect();
                await serverTask.ContinueWith(_ => { }); // swallow
            }
        }

        [Fact]
        public async Task NoListener_ReturnsFalse_WithinTimeout()
        {
            var prober = new PipeProber();
            var start = DateTime.UtcNow;
            var detected = await prober.IsListeningAsync(FakePid + 1, TimeSpan.FromMilliseconds(150));
            var elapsed = DateTime.UtcNow - start;

            Assert.False(detected);
            // Allow generous slack — CI schedulers occasionally pause us.
            Assert.True(elapsed < TimeSpan.FromMilliseconds(1500),
                $"Prober should not block significantly past the timeout (took {elapsed.TotalMilliseconds}ms).");
        }
    }
}
