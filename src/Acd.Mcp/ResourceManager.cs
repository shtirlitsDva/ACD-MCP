namespace Acd.Mcp
{
    // Centralises plugin tear-down. Two reasons it exists:
    //
    //  1. DevReload needs the collectible ALC to unload on Terminate. Any
    //     event subscription that captures plugin types but is held by a
    //     process-lifetime delegate root (TaskScheduler, AutoCAD's
    //     Application.Idle, AutoCAD's main Dispatcher) pins the ALC. Each
    //     pinner must be matched by an unsubscribe in Terminate, in
    //     reverse-of-subscribe order.
    //
    //  2. The teardown for IDisposable resources (PipeListener, PaletteSet,
    //     editors, etc.) was previously a flat sequence of SafeBoundary.Run
    //     calls in Terminate. As the plugin grew, the list got error-prone
    //     and ordering-fragile. Owning the list here keeps Terminate small
    //     and the registration co-located with the construction.
    //
    // Not thread-safe by design: Initialize/Terminate run single-threaded on
    // AutoCAD's main thread, and registration happens during construction
    // paths that are also main-thread-bound. A lock would just add cost.
    //
    // The safeRun delegate is injected so the plugin can wire it to
    // SafeBoundary.Run while unit tests can supply a plain try/catch. That
    // keeps the class itself free of AutoCAD types and trivially testable.
    internal sealed class ResourceManager : IDisposable
    {
        // One step in the teardown sequence. Context is the SafeBoundary
        // label used when the step throws. Action is the actual work.
        private readonly record struct Step(string Context, Action Action);

        private readonly Action<string, Action> _safeRun;
        private readonly List<Step> _steps = new();
        private bool _disposed;

        public ResourceManager(Action<string, Action> safeRun)
        {
            _safeRun = safeRun ?? throw new ArgumentNullException(nameof(safeRun));
        }

        // Disposes the resource on tear-down. Context is included in the
        // SafeBoundary label so a failure points at the right resource.
        public void Register(string context, IDisposable disposable)
        {
            if (disposable is null) throw new ArgumentNullException(nameof(disposable));
            _steps.Add(new Step(context, disposable.Dispose));
        }

        // Runs a custom tear-down action (e.g. a Close() that must precede
        // Dispose(), or a `Application.Idle -= handler`). Use this when the
        // resource is not IDisposable or when the cleanup is more than a
        // single Dispose() call.
        public void RegisterAction(string context, Action cleanup)
        {
            if (cleanup is null) throw new ArgumentNullException(nameof(cleanup));
            _steps.Add(new Step(context, cleanup));
        }

        // Subscribes immediately and queues the matching unsubscribe for
        // tear-down. The subscribe runs synchronously so the call site can
        // assume the handler is hooked by the time RegisterEvent returns.
        public void RegisterEvent(string context, Action subscribe, Action unsubscribe)
        {
            if (subscribe is null) throw new ArgumentNullException(nameof(subscribe));
            if (unsubscribe is null) throw new ArgumentNullException(nameof(unsubscribe));
            subscribe();
            _steps.Add(new Step(context, unsubscribe));
        }

        // LIFO so resources are torn down in the opposite order they were
        // wired up — typical destructor convention. Each step is isolated
        // via safeRun: one failing step does not skip the rest.
        //
        // Idempotent: a second call is a no-op (the list is cleared after
        // the first run).
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = _steps.Count - 1; i >= 0; i--)
            {
                var step = _steps[i];
                _safeRun($"ResourceManager.Dispose/{step.Context}", step.Action);
            }
            _steps.Clear();
        }
    }
}
