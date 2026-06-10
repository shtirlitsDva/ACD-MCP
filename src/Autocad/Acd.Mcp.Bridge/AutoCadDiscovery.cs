using System.Diagnostics;

namespace Acd.Mcp.Bridge
{
    // Resolves which acad.exe the bridge should talk to. Three pieces:
    //   - Process enumeration: who claims to be AutoCAD?
    //   - Liveness check: of those, which actually owns the plugin pipe?
    //   - Preference handling: --pid <N> is a *hint*, not a pin (so an
    //     AutoCAD restart doesn't permanently disable the bridge).
    //
    // PipeProber injection lets tests substitute an in-memory liveness
    // check without spinning up real pipes.
    public class AutoCadDiscovery
    {
        private readonly PipeProber _prober;

        // Tight probe timeout: just enough for a live listener to
        // accept on a healthy box; short enough that probing 3-4
        // candidates in parallel stays well under one second.
        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(150);

        public AutoCadDiscovery(PipeProber? prober = null)
        {
            _prober = prober ?? new PipeProber();
        }

        // Shared default instance for production code paths that need a
        // discovery service but have nothing to inject. Tests construct
        // their own instance with a fake prober.
        public static AutoCadDiscovery Default { get; } = new();

        // Returns the PIDs of every acad.exe on the box, sorted for
        // determinism. Virtual so tests can substitute the enumeration.
        public virtual int[] FindAutoCadPids()
        {
            return Process.GetProcessesByName("acad")
                .Select(p => p.Id)
                .OrderBy(id => id)
                .ToArray();
        }

        // Resolve the AutoCAD PID, honoring an optional --pid preference.
        // Throws AcadTransportException on hard failure (with a Reason
        // the tool wrappers can map to a stable error_code).
        public async Task<PidResolution> ResolveAsync(int? explicitPid, CancellationToken ct = default)
        {
            if (explicitPid is int pinned)
            {
                if (ProcessExists(pinned))
                {
                    var listening = await _prober.IsListeningAsync(pinned, DefaultProbeTimeout, ct).ConfigureAwait(false);
                    return new PidResolution(
                        pinned,
                        listening
                            ? PidResolutionReason.ExplicitPidVerified
                            : PidResolutionReason.ExplicitPidPipeNotReady);
                }
                // Pinned PID is dead — fall through to discovery rather
                // than welding the bridge to a now-defunct process. The
                // status surface records this transition via the Reason.
            }

            var pids = FindAutoCadPids();
            if (pids.Length == 0)
            {
                throw new AcadTransportException(
                    AcadTransportFailure.NoAutoCadFound,
                    "No AutoCAD instance found. Start AutoCAD and load the Acd.Mcp plugin, " +
                    "or pass --pid <PID> explicitly.");
            }

            if (pids.Length == 1)
            {
                var only = pids[0];
                var listening = await _prober.IsListeningAsync(only, DefaultProbeTimeout, ct).ConfigureAwait(false);
                return new PidResolution(
                    only,
                    listening
                        ? PidResolutionReason.SoleAutoCadWithPlugin
                        : PidResolutionReason.SoleAutoCadPipeNotReady);
            }

            // Multi-instance: probe each candidate in parallel and pick
            // the one(s) that actually own the plugin pipe. That's the
            // only signal that meaningfully says "this is *my* AutoCAD."
            var probes = await Task.WhenAll(pids.Select(async pid =>
                (Pid: pid, Listening: await _prober.IsListeningAsync(pid, DefaultProbeTimeout, ct).ConfigureAwait(false))
            )).ConfigureAwait(false);

            var listeners = probes.Where(t => t.Listening).Select(t => t.Pid).ToArray();
            return listeners.Length switch
            {
                1 => new PidResolution(listeners[0], PidResolutionReason.DisambiguatedByPipe),
                0 => throw new AcadTransportException(
                        AcadTransportFailure.AmbiguousAutoCads,
                        $"Multiple AutoCAD instances found (PIDs: {string.Join(", ", pids)}) " +
                        "but none has the Acd.Mcp plugin pipe listening. " +
                        "Run ACDMCP_START in the target AutoCAD, or pass --pid <PID>."),
                _ => throw new AcadTransportException(
                        AcadTransportFailure.MultipleAutoCadPlugins,
                        $"Multiple AutoCAD instances with the Acd.Mcp plugin found (PIDs: {string.Join(", ", listeners)}). " +
                        "Pass --pid <PID> to pick one."),
            };
        }

        // Process.GetProcessById throws ArgumentException for "no such PID";
        // HasExited can also be true for already-terminated PIDs (and the
        // getter itself can throw InvalidOperationException in racy unloads).
        // Squash all three into a clean bool.
        private static bool ProcessExists(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (ArgumentException)         { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}
