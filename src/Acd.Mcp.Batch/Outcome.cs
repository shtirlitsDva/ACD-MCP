using System;

namespace Acd.Mcp.Batch
{
    // Hand-rolled discriminated union used in three places:
    //   - the per-file body outcome (Pass | Skip(reason) | Failure(error))
    //   - any auxiliary computation that wants to return success-or-failure
    //     without an exception
    //   - the StepOutcome hierarchy below mirrors this same shape but carries
    //     domain-specific payloads, so it lives in its own file.
    //
    // Why hand-rolled (no OneOf / LanguageExt):
    //   - The user explicitly forbade third-party DU libraries for this
    //     codebase. We want exhaustive Match on a sealed hierarchy with no
    //     surprises.
    //   - Sealed records give us value-equality, a clean ToString, and
    //     pattern-matching ergonomics.
    //   - The Match overloads encode exhaustiveness at the call site: if a
    //     new case were ever added, every caller would fail to compile.
    public abstract record Outcome<T>
    {
        // Locked constructor — only the nested derived records can extend it.
        // This makes the hierarchy effectively closed for pattern matching.
        private protected Outcome() { }

        public sealed record Pass(T Value) : Outcome<T>;
        public sealed record Skip(string Reason) : Outcome<T>;
        public sealed record Failure(Exception Error) : Outcome<T>;

        public TOut Match<TOut>(
            Func<T, TOut> onPass,
            Func<string, TOut> onSkip,
            Func<Exception, TOut> onFailure) => this switch
            {
                Pass p => onPass(p.Value),
                Skip s => onSkip(s.Reason),
                Failure f => onFailure(f.Error),
                _ => throw new InvalidOperationException(
                    $"Unhandled Outcome<{typeof(T).Name}> case: {GetType().Name}"),
            };

        public void Match(
            Action<T> onPass,
            Action<string> onSkip,
            Action<Exception> onFailure)
        {
            switch (this)
            {
                case Pass p: onPass(p.Value); break;
                case Skip s: onSkip(s.Reason); break;
                case Failure f: onFailure(f.Error); break;
                default:
                    throw new InvalidOperationException(
                        $"Unhandled Outcome<{typeof(T).Name}> case: {GetType().Name}");
            }
        }

        public bool IsPass => this is Pass;
        public bool IsSkip => this is Skip;
        public bool IsFailure => this is Failure;
    }

    // Convenience constructors on a non-generic type so call sites can write
    // Outcome.Pass(value) without naming T (the compiler infers it). Keeps
    // the consuming code symmetrical with `return Outcome.Pass(x);` style.
    public static class Outcome
    {
        public static Outcome<T>.Pass Pass<T>(T value) => new(value);
        public static Outcome<T>.Skip Skip<T>(string reason) => new(reason);
        public static Outcome<T>.Failure Failure<T>(Exception error) => new(error);
    }
}
