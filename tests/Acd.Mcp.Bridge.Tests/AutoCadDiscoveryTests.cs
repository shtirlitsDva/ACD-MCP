using Acd.Mcp.Bridge;
using Xunit;

namespace Acd.Mcp.Bridge.Tests
{
    // Covers the four-way branch in AutoCadDiscovery.ResolveAsync that
    // the source review called out as the fragility hotspot. Uses a fake
    // prober + a subclass-override of FindAutoCadPids so no real
    // NamedPipeClientStream or Process enumeration runs in CI.
    public class AutoCadDiscoveryTests
    {
        // --- helpers ----------------------------------------------------

        private sealed class FakeProber : PipeProber
        {
            private readonly HashSet<int> _listening;
            public FakeProber(params int[] listening) { _listening = new HashSet<int>(listening); }
            public override Task<bool> IsListeningAsync(int pid, TimeSpan timeout, CancellationToken ct = default)
                => Task.FromResult(_listening.Contains(pid));
        }

        private sealed class FakeDiscovery : AutoCadDiscovery
        {
            private readonly int[] _pids;
            public FakeDiscovery(PipeProber prober, params int[] pids) : base(prober) { _pids = pids; }
            public override int[] FindAutoCadPids() => _pids;
        }

        // --- single-instance --------------------------------------------

        [Fact]
        public async Task SoleAutoCad_WithPipe_ReturnsItDirectly()
        {
            var d = new FakeDiscovery(new FakeProber(1234), 1234);
            var r = await d.ResolveAsync(null);
            Assert.Equal(1234, r.Pid);
            Assert.Equal(PidResolutionReason.SoleAutoCadWithPlugin, r.Reason);
            Assert.False(r.IsTransient);
        }

        [Fact]
        public async Task SoleAutoCad_NoPipeYet_IsTransient()
        {
            var d = new FakeDiscovery(new FakeProber(/* nothing listening */), 1234);
            var r = await d.ResolveAsync(null);
            Assert.Equal(1234, r.Pid);
            Assert.Equal(PidResolutionReason.SoleAutoCadPipeNotReady, r.Reason);
            Assert.True(r.IsTransient);
        }

        // --- multi-instance ---------------------------------------------

        [Fact]
        public async Task MultiInstance_ExactlyOnePipe_PickedAutomatically()
        {
            var d = new FakeDiscovery(new FakeProber(5678), 1234, 5678, 9012);
            var r = await d.ResolveAsync(null);
            Assert.Equal(5678, r.Pid);
            Assert.Equal(PidResolutionReason.DisambiguatedByPipe, r.Reason);
        }

        [Fact]
        public async Task MultiInstance_NoPipes_ThrowsAmbiguous()
        {
            var d = new FakeDiscovery(new FakeProber(), 1234, 5678);
            var ex = await Assert.ThrowsAsync<AcadTransportException>(() => d.ResolveAsync(null));
            Assert.Equal(AcadTransportFailure.AmbiguousAutoCads, ex.Reason);
            Assert.Equal("AMBIGUOUS_AUTOCADS", ex.ErrorCode);
        }

        [Fact]
        public async Task MultiInstance_MultiplePipes_ThrowsMultiplePlugins()
        {
            var d = new FakeDiscovery(new FakeProber(1234, 5678), 1234, 5678);
            var ex = await Assert.ThrowsAsync<AcadTransportException>(() => d.ResolveAsync(null));
            Assert.Equal(AcadTransportFailure.MultipleAutoCadPlugins, ex.Reason);
            Assert.Equal("MULTIPLE_AUTOCAD_PLUGINS", ex.ErrorCode);
        }

        // --- no AutoCAD at all ------------------------------------------

        [Fact]
        public async Task NoAutoCad_Throws()
        {
            var d = new FakeDiscovery(new FakeProber());
            var ex = await Assert.ThrowsAsync<AcadTransportException>(() => d.ResolveAsync(null));
            Assert.Equal(AcadTransportFailure.NoAutoCadFound, ex.Reason);
            Assert.Equal("NO_AUTOCAD_FOUND", ex.ErrorCode);
        }

        // --- --pid preference -------------------------------------------
        // The "PID alive but pipe down" and "PID dead → fall through"
        // paths exercise ProcessExists, which hits the real OS. We test
        // them indirectly via the integration smoke (devreload). Pure-
        // unit tests here cover the in-memory branches that don't need
        // a live process handle.
    }
}
