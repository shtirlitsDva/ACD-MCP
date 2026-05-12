using System;
using System.Collections.Generic;

namespace Acd.Mcp.Batch
{
    // Per-file outcome status — the only two flavours the UI/agent see.
    //
    // Pass: every step's StepOutcome was Pass AND no exception escaped the
    //       body. In Live mode this is the only state that commits.
    //
    // Failure: any StepOutcome.Failure (including a false Require predicate —
    //       Require is a hard precondition), any uncaught exception from the
    //       script body, or ctx.Fail() was called. The file's transaction is
    //       rolled back; the loop's behavior on this file follows the user's
    //       "On failure" palette choice (Abort | Skip).
    //
    // (Cancelled is a separate orthogonal flag — see BatchFileResult.Cancelled.
    //  A cancelled file is reported but the loop exits.)
    public enum FileOutcomeStatus
    {
        Pass,
        Failure,
    }

    // One per file processed in a phase. The Steps list is the per-step
    // breakdown; Status is the rolled-up outcome.
    public sealed record BatchFileResult(
        string Path,
        BatchPhase Phase,
        FileOutcomeStatus Status,
        IReadOnlyList<StepOutcome> Steps,
        bool Committed,
        bool Cancelled,
        Exception? Error,
        long ElapsedMs);

    // One per complete batch run (one or two phases). Newest-first when
    // listed; written to disk by BatchRunHistory.
    public sealed record BatchRunReport(
        string RunId,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        BatchMode RequestedMode,
        IReadOnlyList<string> Files,
        IReadOnlyList<BatchFileResult> Results,
        bool Cancelled,
        // Live runs that aborted because the internal Test pass failed will have
        // this set; the Results list then contains only the Test-phase entries.
        string? AbortedReason);
}
