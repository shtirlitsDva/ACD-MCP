using System;
using System.Threading;

namespace Acd.Mcp.Batch
{
    // What a script body sees as `ctx`.
    public interface IBatchContext
    {
        IStepBuilder Step(string name);

        T BatchState<T>() where T : new();

        CancellationToken Token { get; }

        BatchPhase Phase { get; }

        void Fail(string reason);

        bool HasFailures { get; }
    }

    // Fluent builder returned from ctx.Step("..."). The chain is consumed
    // either by Apply (which runs the body) or by being discarded.
    public interface IStepBuilder
    {
        IStepBuilder Require(string name, Func<bool> predicate);

        StepOutcome Apply(Func<string> action);

        StepOutcome Apply(Action action);
    }
}
