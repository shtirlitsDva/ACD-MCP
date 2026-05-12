namespace Acd.Mcp.Batch
{
    // Two execution modes only. Test never commits; Live commits on success
    // after a prior Test pass has already returned green.
    public enum BatchMode
    {
        Test,
        Live,
    }

    // Two-phase sequencing: when the user picks Live, the runner first does a
    // complete Test pass, then a complete Live pass. Test-only runs do just
    // the Test phase.
    public enum BatchPhase
    {
        Test,
        Live,
    }

    // What the runner does when a per-file Failure occurs. Owned by the
    // BATCH palette UI (dropdown next to the Test/Live switch).
    //
    // Abort: stop the loop on the first failed file. The remaining files
    //        are NOT processed. This is the default — it prevents a script
    //        that's bad against the user's assumptions from silently
    //        churning through every drawing producing noise.
    //
    // Skip:  the failed file is recorded as Failure, then the loop moves
    //        on to the next file. Useful when the user knows some files
    //        legitimately don't match the script's preconditions and
    //        wants to see which.
    //
    // "File locked by another writer" is a structural failure that always
    // aborts the entire batch regardless of this setting — see
    // BatchAbortedException in BatchRunner.
    public enum BatchOnFailure
    {
        Abort,
        Skip,
    }
}
