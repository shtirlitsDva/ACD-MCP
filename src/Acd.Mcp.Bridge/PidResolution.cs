namespace Acd.Mcp.Bridge
{
    // Structured result of AutoCadDiscovery.ResolveAsync. Reason is
    // recorded so logs / status surface can explain *why* a particular
    // PID was chosen (or why none could be).
    public enum PidResolutionReason
    {
        // Caller supplied --pid <N> and we verified the process exists
        // AND its plugin pipe is listening.
        ExplicitPidVerified,

        // Caller supplied --pid <N>, the process exists, but the plugin
        // pipe isn't listening yet. Worth retrying — the listener may
        // come up shortly (DEBUG-auto-start race / user typing
        // ACDMCP_START / drawing-load).
        ExplicitPidPipeNotReady,

        // Exactly one acad.exe found AND its plugin pipe is listening.
        // The happy single-instance path.
        SoleAutoCadWithPlugin,

        // Exactly one acad.exe found but no plugin pipe yet. Same
        // retry semantics as ExplicitPidPipeNotReady.
        SoleAutoCadPipeNotReady,

        // Multiple acad.exe, exactly one has the plugin pipe — that's
        // ours. Civil 3D + plain AutoCAD coexistence, or zombie acad.exe
        // alongside the live one.
        DisambiguatedByPipe,
    }

    public readonly record struct PidResolution(int Pid, PidResolutionReason Reason)
    {
        // True for the "wait a bit and try again" reasons. Used by the
        // connect retry loop to decide whether to re-resolve and retry
        // versus surface the failure immediately.
        public bool IsTransient =>
            Reason is PidResolutionReason.ExplicitPidPipeNotReady
                   or PidResolutionReason.SoleAutoCadPipeNotReady;
    }
}
