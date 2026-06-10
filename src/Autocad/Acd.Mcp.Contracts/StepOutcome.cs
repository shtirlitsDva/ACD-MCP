using System;
using System.Collections.Generic;

namespace Acd.Mcp.Batch
{
    // Per-step result, recorded once per Step.Apply chain in the script body.
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

    public sealed record RequirementResult(string Name, bool Passed, Exception? Error = null);
}
