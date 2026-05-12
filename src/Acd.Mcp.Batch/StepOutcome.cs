using System;
using System.Collections.Generic;

namespace Acd.Mcp.Batch
{
    // Per-step result, recorded once per Step.Apply chain in the script body.
    //
    // Three cases:
    //   Pass     — every Require predicate returned true; Apply ran and produced
    //              a summary string.
    //   Skipped  — at least one Require predicate returned false. Apply was
    //              NOT invoked. Records the first failing requirement so the
    //              user knows which one caused the skip.
    //   Failure  — a Require predicate threw, or Apply threw. The file is
    //              treated as failed (no commit even in Live mode).
    //
    // Naming note: earlier drafts called the failure case "Crashed" — that
    // name conflicted with AutoCAD usage where "crash" means the process
    // died. Renamed to Failure.
    public abstract record StepOutcome
    {
        private protected StepOutcome(string name) { Name = name; }

        public string Name { get; }

        public sealed record Pass(string Name, IReadOnlyList<RequirementResult> Requirements, string Summary)
            : StepOutcome(Name);

        public sealed record Skipped(string Name, IReadOnlyList<RequirementResult> Requirements, string FailingRequirement)
            : StepOutcome(Name);

        public sealed record Failure(string Name, IReadOnlyList<RequirementResult> Requirements, Exception Error)
            : StepOutcome(Name);

        public TOut Match<TOut>(
            Func<Pass, TOut> onPass,
            Func<Skipped, TOut> onSkipped,
            Func<Failure, TOut> onFailure) => this switch
            {
                Pass p => onPass(p),
                Skipped s => onSkipped(s),
                Failure f => onFailure(f),
                _ => throw new InvalidOperationException(
                    $"Unhandled StepOutcome case: {GetType().Name}"),
            };
    }

    // A single Require predicate's result inside a step. The script can chain
    // multiple Requires; each one is recorded so the user can see which
    // predicate(s) passed and which one short-circuited the step.
    public sealed record RequirementResult(string Name, bool Passed, Exception? Error = null);
}
