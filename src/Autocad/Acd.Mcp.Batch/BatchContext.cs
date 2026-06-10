using System;
using System.Collections.Generic;
using System.Threading;

namespace Acd.Mcp.Batch
{
    // The runtime concrete that backs IBatchContext.
    //
    // Per-file lifetime: the runner constructs one BatchContext per file,
    // hands it to the compiled script delegate, then reads back Steps +
    // HasFailures to decide on commit.
    //
    // The BatchState bag is INJECTED — same instance for every file in a run.
    // The runner builds the bag once at run start and passes the same
    // reference into every per-file context. That gives us deterministic
    // cross-file state sharing without globals.
    internal sealed class BatchContext : IBatchContext
    {
        private readonly BatchStateBag _stateBag;
        private readonly List<StepOutcome> _steps = new();
        private bool _hasFailures;

        public BatchContext(BatchStateBag stateBag, BatchPhase phase, CancellationToken token)
        {
            _stateBag = stateBag;
            Phase = phase;
            Token = token;
        }

        public CancellationToken Token { get; }
        public BatchPhase Phase { get; }
        public bool HasFailures => _hasFailures;
        public IReadOnlyList<StepOutcome> Steps => _steps;

        public IStepBuilder Step(string name) => new StepBuilder(name, this);

        public T BatchState<T>() where T : new() => _stateBag.Get<T>();

        public void Fail(string reason)
        {
            _hasFailures = true;
            // Record the explicit Fail as a synthetic step so it shows up in
            // the per-file results without the runner having to special-case
            // "I called Fail but never built a step."
            _steps.Add(new StepOutcome.Failure(
                Name: "ctx.Fail",
                Requirements: Array.Empty<RequirementResult>(),
                Error: new InvalidOperationException(reason)));
        }

        // Used by StepBuilder when its chain terminates. Also flips the
        // failure flag if the recorded outcome is StepOutcome.Failure.
        internal void Record(StepOutcome outcome)
        {
            _steps.Add(outcome);
            if (outcome is StepOutcome.Failure) _hasFailures = true;
        }
    }

    // Thread-unsafe-by-design: the runner serialises files, so concurrent
    // access never happens. Tests can verify the same-instance guarantee.
    internal sealed class BatchStateBag
    {
        private readonly Dictionary<Type, object> _items = new();

        public T Get<T>() where T : new()
        {
            if (_items.TryGetValue(typeof(T), out var existing))
                return (T)existing;

            var fresh = new T();
            _items[typeof(T)] = fresh!;
            return fresh;
        }
    }
}
