namespace Acd.Mcp.Bridge
{
    // Connect-retry schedule for the bridge → plugin pipe. Default
    // splits a ~3 s budget across three attempts at growing per-attempt
    // timeouts so brief launches (drawing-load, ACDMCP_START race)
    // succeed invisibly.
    //
    // Each attempt re-runs PID discovery (in AcadClient), so a listener
    // that comes up mid-retry is picked up on the next iteration. The
    // attempt timeouts are *connect* deadlines, not total budgets.
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
}
