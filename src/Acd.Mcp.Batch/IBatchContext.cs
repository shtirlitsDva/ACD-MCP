using System;
using System.Threading;

namespace Acd.Mcp.Batch
{
    // What a script body sees as `ctx`.
    //
    // Step(name): start a fluent step builder. The chain terminates when
    //             Apply is called (or when the chain is otherwise discarded);
    //             at termination a StepOutcome is recorded on this context.
    //
    // BatchState<T>(): returns the same instance of T for every file in this
    //                  run. First call default-constructs T. Different T's
    //                  coexist (one Counter, one ErrorList, etc.).
    //
    // Token: the CancellationToken that propagates the user's Cancel click.
    //        Long inner loops in a script body should observe it.
    //
    // Fail(reason): mark the current file as failed without throwing. The
    //               runner notices via HasFailures and refuses to commit.
    //
    // Phase: tells the body whether it's running in the Test or Live phase.
    //        Most bodies don't care, but some genuinely Live-only side effects
    //        (writing to an external system) might want to skip in Test.
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
    //
    // Multiple Require predicates can be chained; they evaluate in order at
    // Apply time, short-circuiting on the first false. If any predicate
    // throws, the step is Failure (file fails). If Apply throws, same.
    public interface IStepBuilder
    {
        IStepBuilder Require(string name, Func<bool> predicate);

        StepOutcome Apply(Func<string> action);

        // Convenience for steps that don't produce a meaningful summary. The
        // recorded summary is an empty string. The action's return type is
        // unconstrained to keep call sites natural.
        StepOutcome Apply(Action action);
    }
}
