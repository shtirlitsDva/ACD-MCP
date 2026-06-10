using System.Diagnostics;

namespace Rvt.Mcp.Bridge
{
    // Resolves which Revit.exe the bridge talks to — same three-step model
    // as AutoCadDiscovery: process enumeration, pipe liveness, --pid hint
    // (a hint, not a pin, so a Revit restart never wedges the bridge).
    public class RevitDiscovery
    {
        private readonly RevitPipeProber _prober;

        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(150);

        public RevitDiscovery(RevitPipeProber? prober = null)
        {
            _prober = prober ?? new RevitPipeProber();
        }

        public static RevitDiscovery Default { get; } = new();

        public virtual int[] FindRevitPids()
        {
            return Process.GetProcessesByName("Revit")
                .Select(p => p.Id)
                .OrderBy(id => id)
                .ToArray();
        }

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
                // Pinned PID is dead — fall through to discovery.
            }

            var pids = FindRevitPids();
            if (pids.Length == 0)
            {
                throw new RvtTransportException(
                    RvtTransportFailure.NoRevitFound,
                    "No Revit instance found. Start Revit with the Rvt.Mcp add-in deployed, " +
                    "or pass --pid <PID> explicitly.");
            }

            if (pids.Length == 1)
            {
                var only = pids[0];
                var listening = await _prober.IsListeningAsync(only, DefaultProbeTimeout, ct).ConfigureAwait(false);
                return new PidResolution(
                    only,
                    listening
                        ? PidResolutionReason.SoleRevitWithPlugin
                        : PidResolutionReason.SoleRevitPipeNotReady);
            }

            var probes = await Task.WhenAll(pids.Select(async pid =>
                (Pid: pid, Listening: await _prober.IsListeningAsync(pid, DefaultProbeTimeout, ct).ConfigureAwait(false))
            )).ConfigureAwait(false);

            var listeners = probes.Where(t => t.Listening).Select(t => t.Pid).ToArray();
            return listeners.Length switch
            {
                1 => new PidResolution(listeners[0], PidResolutionReason.DisambiguatedByPipe),
                0 => throw new RvtTransportException(
                        RvtTransportFailure.AmbiguousRevits,
                        $"Multiple Revit instances found (PIDs: {string.Join(", ", pids)}) " +
                        "but none has the Rvt.Mcp add-in pipe listening. " +
                        "Deploy the add-in to the target Revit, or pass --pid <PID>."),
                _ => throw new RvtTransportException(
                        RvtTransportFailure.MultipleRevitPlugins,
                        $"Multiple Revit instances with the Rvt.Mcp add-in found (PIDs: {string.Join(", ", listeners)}). " +
                        "Pass --pid <PID> to pick one."),
            };
        }

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
