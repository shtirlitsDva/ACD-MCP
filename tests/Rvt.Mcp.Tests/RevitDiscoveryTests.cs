using Rvt.Mcp.Bridge;
using Xunit;

namespace Rvt.Mcp.Tests
{
    // Mirrors Acd.Mcp.Bridge.Tests.AutoCadDiscoveryTests: fake process
    // enumeration + fake pipe prober, no real pipes.
    public class RevitDiscoveryTests
    {
        private sealed class FakeProber : RevitPipeProber
        {
            private readonly HashSet<int> _listening;
            public FakeProber(params int[] listening) => _listening = new(listening);

            public override Task<bool> IsListeningAsync(
                int pid, TimeSpan timeout, CancellationToken ct = default) =>
                Task.FromResult(_listening.Contains(pid));
        }

        private sealed class FakeDiscovery : RevitDiscovery
        {
            private readonly int[] _pids;
            public FakeDiscovery(int[] pids, RevitPipeProber prober) : base(prober) => _pids = pids;
            public override int[] FindRevitPids() => _pids;
        }

        [Fact]
        public async Task NoRevit_ThrowsNoRevitFound()
        {
            var discovery = new FakeDiscovery([], new FakeProber());
            var ex = await Assert.ThrowsAsync<RvtTransportException>(
                () => discovery.ResolveAsync(null));
            Assert.Equal(RvtTransportFailure.NoRevitFound, ex.Reason);
            Assert.Equal("NO_REVIT_FOUND", ex.ErrorCode);
        }

        [Fact]
        public async Task SingleRevitWithPipe_Resolves()
        {
            var discovery = new FakeDiscovery([100], new FakeProber(100));
            var res = await discovery.ResolveAsync(null);
            Assert.Equal(100, res.Pid);
            Assert.Equal(PidResolutionReason.SoleRevitWithPlugin, res.Reason);
            Assert.False(res.IsTransient);
        }

        [Fact]
        public async Task SingleRevitWithoutPipe_IsTransient()
        {
            var discovery = new FakeDiscovery([100], new FakeProber());
            var res = await discovery.ResolveAsync(null);
            Assert.Equal(100, res.Pid);
            Assert.True(res.IsTransient);
        }

        [Fact]
        public async Task MultipleRevits_OnePipe_Disambiguates()
        {
            var discovery = new FakeDiscovery([100, 200, 300], new FakeProber(200));
            var res = await discovery.ResolveAsync(null);
            Assert.Equal(200, res.Pid);
            Assert.Equal(PidResolutionReason.DisambiguatedByPipe, res.Reason);
        }

        [Fact]
        public async Task MultipleRevits_NoPipes_Throws()
        {
            var discovery = new FakeDiscovery([100, 200], new FakeProber());
            var ex = await Assert.ThrowsAsync<RvtTransportException>(
                () => discovery.ResolveAsync(null));
            Assert.Equal(RvtTransportFailure.AmbiguousRevits, ex.Reason);
        }

        [Fact]
        public async Task MultipleRevits_MultiplePipes_Throws()
        {
            var discovery = new FakeDiscovery([100, 200], new FakeProber(100, 200));
            var ex = await Assert.ThrowsAsync<RvtTransportException>(
                () => discovery.ResolveAsync(null));
            Assert.Equal(RvtTransportFailure.MultipleRevitPlugins, ex.Reason);
        }

        [Fact]
        public void PipeName_UsesRvtPrefix()
        {
            Assert.Equal("rvt-mcp-1234", RevitPipeProber.PipeNameFor(1234));
        }
    }
}
