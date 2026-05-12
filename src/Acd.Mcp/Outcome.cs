namespace Acd.Mcp
{
    // Closed discriminated union: an operation either yielded a value (Success)
    // or did not (Failure carrying a message and optional cause). The base type
    // is abstract with a private protected constructor so the case list is
    // genuinely sealed — no third party can introduce a third state.
    //
    // Use Match for exhaustive handling; the compiler will warn if a new case is
    // added and a Match site forgets it (the switch expression is exhaustive
    // because the union is closed).
    public abstract record Outcome<T>
    {
        private protected Outcome() { }

        public sealed record Success(T Value) : Outcome<T>;
        public sealed record Failure(string Message, Exception? Cause = null) : Outcome<T>;

        public static Outcome<T> Ok(T value) => new Success(value);
        public static Outcome<T> Fail(string message, Exception? cause = null) => new Failure(message, cause);

        public TResult Match<TResult>(
            Func<T, TResult> onSuccess,
            Func<string, Exception?, TResult> onFailure) => this switch
        {
            Success s => onSuccess(s.Value),
            Failure f => onFailure(f.Message, f.Cause),
            _ => throw new InvalidOperationException("Outcome union closed; this line is unreachable."),
        };

        public void Switch(Action<T> onSuccess, Action<string, Exception?> onFailure)
        {
            switch (this)
            {
                case Success s: onSuccess(s.Value); return;
                case Failure f: onFailure(f.Message, f.Cause); return;
                default: throw new InvalidOperationException("Outcome union closed; this line is unreachable.");
            }
        }

        public bool TryGet(out T value)
        {
            if (this is Success s) { value = s.Value; return true; }
            value = default!;
            return false;
        }
    }
}
