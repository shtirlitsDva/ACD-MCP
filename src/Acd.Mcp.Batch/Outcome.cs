using System;

namespace Acd.Mcp.Batch
{
    // The single closed discriminated union used across the codebase for
    // "this either yielded a value, was skipped with a reason, or failed".
    //
    // Three cases:
    //   Pass(value)              — operation produced T.
    //   Skip(reason)             — operation deliberately did not run, with a
    //                              reason that surfaces to the user. Used by
    //                              step Requires that short-circuit.
    //   Failure(message, ?cause) — operation tried and failed. `message` is
    //                              for display; `cause` is the underlying
    //                              exception when one exists (compile errors,
    //                              body throws). Callers that only care
    //                              "didn't yield a value" can collapse Skip +
    //                              Failure.
    //
    // Hand-rolled (no OneOf / LanguageExt). The base type's private protected
    // constructor seals the case list so no external assembly can extend it,
    // which lets the switch expressions stay exhaustive.
    //
    // Lives in Acd.Mcp.Batch because that's the pure-runtime project; both
    // Acd.Mcp (AutoCAD-bound) and Acd.Mcp.Bridge reference this assembly, so
    // every consumer in the system sees the same type.
    public abstract record Outcome<T>
    {
        private protected Outcome() { }

        public sealed record Pass(T Value) : Outcome<T>;
        public sealed record Skip(string Reason) : Outcome<T>;
        public sealed record Failure(string Message, Exception? Cause = null) : Outcome<T>;

        public static Outcome<T> Ok(T value) => new Pass(value);
        public static Outcome<T> Fail(string message, Exception? cause = null) => new Failure(message, cause);
        public static Outcome<T> Fail(Exception cause) => new Failure(cause.Message, cause);
        public static Outcome<T> Skipped(string reason) => new Skip(reason);

        public TResult Match<TResult>(
            Func<T, TResult> onPass,
            Func<string, TResult> onSkip,
            Func<string, Exception?, TResult> onFailure) => this switch
            {
                Pass p => onPass(p.Value),
                Skip s => onSkip(s.Reason),
                Failure f => onFailure(f.Message, f.Cause),
                _ => throw new InvalidOperationException(
                    $"Outcome<{typeof(T).Name}> union closed; this line is unreachable."),
            };

        public void Switch(
            Action<T> onPass,
            Action<string> onSkip,
            Action<string, Exception?> onFailure)
        {
            switch (this)
            {
                case Pass p: onPass(p.Value); return;
                case Skip s: onSkip(s.Reason); return;
                case Failure f: onFailure(f.Message, f.Cause); return;
                default:
                    throw new InvalidOperationException(
                        $"Outcome<{typeof(T).Name}> union closed; this line is unreachable.");
            }
        }

        public bool TryGet(out T value)
        {
            if (this is Pass p) { value = p.Value; return true; }
            value = default!;
            return false;
        }

        public bool IsPass => this is Pass;
        public bool IsSkip => this is Skip;
        public bool IsFailure => this is Failure;
    }

    // Non-generic helper for type-inferred construction at the call site:
    //   return Outcome.Pass(value);
    //   return Outcome.Failure<int>(ex);
    public static class Outcome
    {
        public static Outcome<T>.Pass Pass<T>(T value) => new(value);
        public static Outcome<T>.Skip Skip<T>(string reason) => new(reason);
        public static Outcome<T>.Failure Failure<T>(string message, Exception? cause = null) => new(message, cause);
        public static Outcome<T>.Failure Failure<T>(Exception cause) => new(cause.Message, cause);
    }
}
