using Xunit;

namespace Acd.Mcp.Tests;

public class ResourceManagerTests
{
    // Tests use a try/catch safeRun so we exercise the same error-isolation
    // contract the plugin sees from SafeBoundary.Run, without dragging
    // SafeBoundary (and its AutoCAD references) into the test project.
    private static readonly Action<string, Action> SafeRun = (_, body) =>
    {
        try { body(); } catch { /* swallow — matches SafeBoundary's no-escape contract */ }
    };

    [Fact]
    public void Registered_disposable_is_disposed_on_Dispose()
    {
        var rm = new ResourceManager(SafeRun);
        var d = new RecordingDisposable();
        rm.Register("d", d);

        rm.Dispose();

        Assert.Equal(1, d.DisposeCount);
    }

    [Fact]
    public void Registered_event_subscribe_runs_immediately_and_unsubscribe_runs_on_Dispose()
    {
        var rm = new ResourceManager(SafeRun);
        bool subscribed = false;
        bool unsubscribed = false;

        rm.RegisterEvent("ev",
            subscribe: () => subscribed = true,
            unsubscribe: () => unsubscribed = true);

        Assert.True(subscribed);
        Assert.False(unsubscribed);

        rm.Dispose();
        Assert.True(unsubscribed);
    }

    [Fact]
    public void Steps_run_in_LIFO_order()
    {
        var rm = new ResourceManager(SafeRun);
        var order = new List<string>();

        rm.RegisterAction("A", () => order.Add("A"));
        rm.RegisterAction("B", () => order.Add("B"));

        rm.Dispose();

        Assert.Equal(new[] { "B", "A" }, order);
    }

    [Fact]
    public void One_failing_step_does_not_skip_the_rest()
    {
        var rm = new ResourceManager(SafeRun);
        var order = new List<string>();

        // Order of registration: first, throws, last.
        // Expected reverse-execution: last, throws (swallowed), first.
        rm.RegisterAction("first", () => order.Add("first"));
        rm.RegisterEvent("throws",
            subscribe: () => { },
            unsubscribe: () => throw new InvalidOperationException("boom"));
        rm.RegisterAction("last", () => order.Add("last"));

        rm.Dispose();

        Assert.Equal(new[] { "last", "first" }, order);
    }

    [Fact]
    public void Second_Dispose_is_a_no_op()
    {
        var rm = new ResourceManager(SafeRun);
        var d = new RecordingDisposable();
        rm.Register("d", d);

        rm.Dispose();
        rm.Dispose();

        Assert.Equal(1, d.DisposeCount);
    }

    [Fact]
    public void Mixed_registration_types_preserve_LIFO()
    {
        var rm = new ResourceManager(SafeRun);
        var order = new List<string>();

        rm.RegisterAction("a", () => order.Add("a"));
        rm.Register("b", new ActionDisposable(() => order.Add("b")));
        rm.RegisterEvent("c",
            subscribe: () => { },
            unsubscribe: () => order.Add("c"));
        rm.RegisterAction("d", () => order.Add("d"));

        rm.Dispose();

        Assert.Equal(new[] { "d", "c", "b", "a" }, order);
    }

    [Fact]
    public void Reentrant_Dispose_inside_a_step_is_safe()
    {
        var rm = new ResourceManager(SafeRun);
        var order = new List<string>();

        // The step calls Dispose on its own manager while iteration is in
        // progress. The _disposed guard makes the re-entry a no-op, and the
        // outer loop should still complete the remaining steps.
        rm.RegisterAction("first", () => order.Add("first"));
        rm.RegisterAction("reentrant", () => { order.Add("reentrant"); rm.Dispose(); });
        rm.RegisterAction("last", () => order.Add("last"));

        rm.Dispose();

        Assert.Equal(new[] { "last", "reentrant", "first" }, order);
    }

    [Fact]
    public void Subscribe_that_throws_propagates_and_skips_registration()
    {
        var rm = new ResourceManager(SafeRun);
        bool unsubscribeCalled = false;

        Assert.Throws<InvalidOperationException>(() =>
            rm.RegisterEvent("ev",
                subscribe: () => throw new InvalidOperationException("boom"),
                unsubscribe: () => unsubscribeCalled = true));

        // The exception escapes registration — the lambda failed to wire
        // up, so we MUST NOT queue the unsubscribe (it has nothing to undo).
        rm.Dispose();
        Assert.False(unsubscribeCalled);
    }

    private sealed class RecordingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _action;
        public ActionDisposable(Action action) => _action = action;
        public void Dispose() => _action();
    }
}
