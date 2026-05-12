using System;
using System.Collections.Generic;

namespace Acd.Mcp.Batch
{
    // Fluent step. Evaluates predicates lazily — they only run inside Apply
    // so that the chain can be built without side-effects until the user
    // (script body) commits to executing it.
    //
    // Predicates run in declared order, short-circuiting on the first false.
    // Each predicate's result is recorded so the per-file report can show
    // which Requirement passed and which short-circuited the step.
    internal sealed class StepBuilder : IStepBuilder
    {
        private readonly string _name;
        private readonly BatchContext _ctx;
        private readonly List<(string Name, Func<bool> Predicate)> _requirements = new();

        public StepBuilder(string name, BatchContext ctx)
        {
            _name = name;
            _ctx = ctx;
        }

        public IStepBuilder Require(string name, Func<bool> predicate)
        {
            _requirements.Add((name, predicate));
            return this;
        }

        public StepOutcome Apply(Func<string> action) => RunApply(action);

        public StepOutcome Apply(Action action) => RunApply(() =>
        {
            action();
            return "";
        });

        private StepOutcome RunApply(Func<string> action)
        {
            var results = new List<RequirementResult>(_requirements.Count);
            foreach (var (rname, predicate) in _requirements)
            {
                bool passed;
                try
                {
                    passed = predicate();
                }
                catch (Exception ex)
                {
                    // Predicate threw → step Failure. We still record the
                    // requirement so the user knows which one blew up.
                    results.Add(new RequirementResult(rname, false, ex));
                    var outcome = new StepOutcome.Failure(_name, results, ex);
                    _ctx.Record(outcome);
                    return outcome;
                }

                results.Add(new RequirementResult(rname, passed));

                if (!passed)
                {
                    // First failing requirement short-circuits the chain.
                    var outcome = new StepOutcome.Skipped(_name, results, rname);
                    _ctx.Record(outcome);
                    return outcome;
                }
            }

            try
            {
                var summary = action();
                var passOutcome = new StepOutcome.Pass(_name, results, summary ?? "");
                _ctx.Record(passOutcome);
                return passOutcome;
            }
            catch (Exception ex)
            {
                var failOutcome = new StepOutcome.Failure(_name, results, ex);
                _ctx.Record(failOutcome);
                return failOutcome;
            }
        }
    }
}
