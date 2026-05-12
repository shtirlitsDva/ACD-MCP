using System;
using System.Collections.Generic;

namespace Acd.Mcp.Batch
{
    // Per-step result, recorded once per Step.Apply chain in the script body.
    //
    // Two cases:
    //   Pass     — every Require predicate returned true; Apply ran and produced
    //              a summary string.
    //   Failure  — a Require predicate returned false OR threw, OR Apply threw.
    //              The file is treated as failed (no commit even in Live mode).
    //              Require is a HARD precondition (see /acd-mcp:batch skill
    //              <step-dsl>): branching belongs in `if` inside Apply, not in
    //              Require. There is no Skipped case anymore — the Test pass
    //              exists to catch a false Require before Live.
    //
    // Backward compat: persisted history with Kind="Skipped" is rehydrated
    // as Failure (see BatchRunHistory.StepOutcomeEnvelope.ToOutcome).
    public abstract record StepOutcome
    {
        private protected StepOutcome(string name) { Name = name; }

        public string Name { get; }

        public sealed record Pass(string Name, IReadOnlyList<RequirementResult> Requirements, string Summary)
            : StepOutcome(Name);

        public sealed record Failure(string Name, IReadOnlyList<RequirementResult> Requirements, Exception Error)
            : StepOutcome(Name);

        public TOut Match<TOut>(
            Func<Pass, TOut> onPass,
            Func<Failure, TOut> onFailure) => this switch
            {
                Pass p => onPass(p),
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
