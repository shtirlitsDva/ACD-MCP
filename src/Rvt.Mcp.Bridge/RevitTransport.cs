namespace Rvt.Mcp.Bridge
{
    // Transport failure taxonomy + retry schedule + PID resolution outcome —
    // the Revit twins of the Acd.Mcp.Bridge types (those carry AutoCAD
    // wording in messages and pipe names, so a parameterized share isn't
    // free; extraction into a common transport lib is a known follow-up).

    public enum RvtTransportFailure
    {
        NoRevitFound,
        AmbiguousRevits,
        MultipleRevitPlugins,
        PinnedPidGone,
        PipeNotListening,
        PipeBroken,
    }

    public sealed class RvtTransportException : Exception
    {
        public RvtTransportFailure Reason { get; }

        public string ErrorCode => Reason switch
        {
            RvtTransportFailure.NoRevitFound        => "NO_REVIT_FOUND",
            RvtTransportFailure.AmbiguousRevits     => "AMBIGUOUS_REVITS",
            RvtTransportFailure.MultipleRevitPlugins => "MULTIPLE_REVIT_PLUGINS",
            RvtTransportFailure.PinnedPidGone       => "PINNED_PID_GONE",
            RvtTransportFailure.PipeNotListening    => "PIPE_NOT_LISTENING",
            RvtTransportFailure.PipeBroken          => "PIPE_BROKEN",
            _ => "UNKNOWN_TRANSPORT_ERROR",
        };

        public RvtTransportException(RvtTransportFailure reason, string message, Exception? inner = null)
            : base(message, inner)
        {
            Reason = reason;
        }
    }

    public sealed class ConnectRetryPolicy
    {
        public IReadOnlyList<int> AttemptTimeoutsMs { get; }

        public ConnectRetryPolicy(params int[] attemptTimeoutsMs)
        {
            if (attemptTimeoutsMs is null || attemptTimeoutsMs.Length == 0)
                throw new ArgumentException("At least one attempt timeout is required.", nameof(attemptTimeoutsMs));
            AttemptTimeoutsMs = attemptTimeoutsMs;
        }

        public static ConnectRetryPolicy Default { get; } = new(200, 800, 2000);
    }

    public enum PidResolutionReason
    {
        ExplicitPidVerified,
        ExplicitPidPipeNotReady,
        SoleRevitWithPlugin,
        SoleRevitPipeNotReady,
        DisambiguatedByPipe,
    }

    public readonly record struct PidResolution(int Pid, PidResolutionReason Reason)
    {
        // "Process exists but its pipe isn't accepting yet" — the caller
        // should wait and retry rather than fail hard.
        public bool IsTransient => Reason
            is PidResolutionReason.ExplicitPidPipeNotReady
            or PidResolutionReason.SoleRevitPipeNotReady;
    }
}
