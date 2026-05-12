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
}
