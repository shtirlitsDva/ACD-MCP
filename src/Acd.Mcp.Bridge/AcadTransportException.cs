namespace Acd.Mcp.Bridge
{
    // Transport-level failure taxonomy. The bridge surfaces these to the
    // MCP tool wrappers so they can map to stable error_code strings the
    // agent's skill can branch on (see docs/design/lifecycle-and-discovery-v2.md
    // and the matching <error-codes> section of the skill docs).
    //
    // NOT to be confused with AcadRpcException — that one carries a reply
    // the plugin sent (protocol-level failure). AcadTransportException
    // means we never reached a working plugin in the first place.
    public enum AcadTransportFailure
    {
        // No acad.exe found in the process list. User probably hasn't
        // started AutoCAD yet.
        NoAutoCadFound,

        // Several acad.exe processes are running and none of them has
        // the acd-mcp-<pid> pipe listening. Plugin isn't loaded into any
        // of them, or none has run ACDMCP_START. Pass --pid.
        AmbiguousAutoCads,

        // Several acad.exe processes have the plugin pipe up. Genuine
        // multi-instance ambiguity — pass --pid to pick one.
        MultipleAutoCadPlugins,

        // --pid <N> was specified but PID N no longer exists.
        PinnedPidGone,

        // Discovery picked a PID, but the plugin pipe never accepted
        // a connection within the retry budget. AutoCAD is up, the
        // plugin is loaded, but ACDMCP_START hasn't run yet — or the
        // listener died.
        PipeNotListening,

        // The pipe accepted a connection, but the read/write that
        // followed failed (server closed mid-stream, etc.).
        PipeBroken,
    }

    public sealed class AcadTransportException : Exception
    {
        public AcadTransportFailure Reason { get; }

        // Stable error_code string for tool envelopes. Mirrors Reason
        // 1:1 but is the public name agents see — keep it stable across
        // refactors of the enum.
        public string ErrorCode => Reason switch
        {
            AcadTransportFailure.NoAutoCadFound        => "NO_AUTOCAD_FOUND",
            AcadTransportFailure.AmbiguousAutoCads     => "AMBIGUOUS_AUTOCADS",
            AcadTransportFailure.MultipleAutoCadPlugins => "MULTIPLE_AUTOCAD_PLUGINS",
            AcadTransportFailure.PinnedPidGone         => "PINNED_PID_GONE",
            AcadTransportFailure.PipeNotListening      => "PIPE_NOT_LISTENING",
            AcadTransportFailure.PipeBroken            => "PIPE_BROKEN",
            _ => "UNKNOWN_TRANSPORT_ERROR",
        };

        public AcadTransportException(AcadTransportFailure reason, string message, Exception? inner = null)
            : base(message, inner)
        {
            Reason = reason;
        }
    }
}
