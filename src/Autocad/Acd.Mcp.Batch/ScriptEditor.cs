namespace Acd.Mcp.Batch
{
    // Shared deep module for the BATCH and REPL editor tabs.
    //
    // Owns:
    //   * the saved-scripts store (filtered by Flavor at call sites);
    //   * the live editor-buffer mirror file on disk (debounced writes);
    //   * the authoritative "current script text" slot (what the user
    //     sees in the editor);
    //   * the IsDirty flag (true after typing, cleared by LoadFromSaved
    //     / AcceptPending);
    //   * a separate "pending proposal" slot that holds the agent's
    //     last proposal until the UI accepts or discards it.
    //
    // <staging-model> An agent proposal's disposition depends on whether
    // the editor has unsaved user edits at propose time:
    //
    //  * Editor is CLEAN (IsDirty=false): no user state to clobber, so
    //    ProposeFromAgent inline-promotes the proposal — _currentText
    //    becomes the saved body, the mirror is synchronously written +
    //    flushed (NOT debounced), and PendingProposal is cleared before
    //    ScriptProposed is fired. This eliminates the V3-H3 race where
    //    the agent's next iteration could read stale mirror content.
    //
    //  * Editor is DIRTY (IsDirty=true): the user has unsaved typed
    //    edits. ProposeFromAgent writes the script to disk (the store),
    //    parks the body in PendingProposal, and fires ScriptProposed.
    //    The UI then prompts the user — accept (AcceptPending) or
    //    discard (DiscardPending). Until then, CurrentText + the mirror
    //    reflect what the USER is actually editing, so the agent's
    //    "read-the-mirror-before-re-proposing" convention is honest.
    //
    // The ScriptProposed event fires in BOTH cases — UI subscribers
    // need it to refresh their display from the accepted body and to
    // run the prompt path in the dirty case. UI subscribers' calls to
    // AcceptPending after the clean-editor inline promote are no-ops
    // (pending already cleared), which is the intentional convergence.
    // </staging-model>
    //
    // <dirty-semantics> IsDirty means "the user has unsaved typed edits".
    //   * OnUserTyped     → sets it (user is in the middle of editing).
    //   * LoadFromSaved   → clears it (text replaced from disk; no
    //                       unsaved edits remain).
    //   * AcceptPending   → clears it (user accepted the agent's body
    //                       as the new baseline).
    //   * DiscardPending  → unchanged (the rejection didn't change what
    //                       the user is editing).
    //   * ProposeFromAgent → unchanged (the proposal hasn't been
    //                       accepted yet).
    // </dirty-semantics>
    //
    // Narrow public surface: five behaviour entry points, five
    // observable properties, one event. Implementation hides thread
    // safety, mirror sequencing, and the relationship between the
    // slot / pending / store.
    //
    // One instance per editor (BATCH and REPL each get their own,
    // configured with their own flavor and mirror path).
    // SavedScriptStore is shared across both — the flavor parameter
    // routes calls to the correct subfolder.
    public sealed class ScriptEditor : IDisposable
    {
        private readonly object _lock = new();
        private readonly SavedScriptStore _store;
        private readonly EditorBuffer _mirror;
        private string _currentText = "";
        private SavedScript? _pendingProposal;
        private bool _isDirty;
        private bool _disposed;

        public ScriptFlavor Flavor { get; }
        public SavedScriptStore Store => _store;
        public string MirrorPath => _mirror.MirrorPath;

        // The text the user is currently editing. Authoritative — the
        // mirror file is kept in lock-step with this slot.
        public string CurrentText
        {
            get { lock (_lock) return _currentText; }
        }

        // The agent's last proposal that the UI has not yet accepted or
        // discarded. Null when no proposal is in flight.
        public SavedScript? PendingProposal
        {
            get { lock (_lock) return _pendingProposal; }
        }

        public bool HasPendingProposal
        {
            get { lock (_lock) return _pendingProposal is not null; }
        }

        public bool IsDirty
        {
            get { lock (_lock) return _isDirty; }
        }

        // Fires after a proposal has been staged in PendingProposal. The
        // UI subscribes, decides whether to prompt the user, and then
        // calls AcceptPending or DiscardPending. PreviousText on the
        // event is what CurrentText held at staging time so the consumer
        // can compare it against the proposed body.
        public event EventHandler<ScriptProposedEvent>? ScriptProposed;

        public ScriptEditor(ScriptFlavor flavor, SavedScriptStore store, EditorBuffer mirror)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _mirror = mirror ?? throw new ArgumentNullException(nameof(mirror));
            Flavor = flavor;
        }

        // Called from the WPF text-change handler. Updates the slot and
        // marks dirty. Mirror write is debounced inside EditorBuffer.
        //
        // <linearization> The mirror write is invoked WHILE holding
        // _lock so that two concurrent callers (possible if callers
        // ever come from non-UI threads) can't observe slot ≠ mirror
        // at rest. EditorBuffer.SetText just enqueues the text and
        // re-arms its debounce timer — no I/O happens here, so the
        // outer lock is held for microseconds. </linearization>
        public void OnUserTyped(string text)
        {
            text ??= "";
            lock (_lock)
            {
                if (_disposed) return;
                _currentText = text;
                _isDirty = true;
                _mirror.SetText(text);
            }
        }

        // Called when the user picks a saved script in Manage Scripts.
        // Replaces CurrentText, clears dirty, updates the mirror. Any
        // pending proposal is discarded (the user clearly chose a
        // different baseline). Mirror write is inside _lock — see
        // <linearization> on OnUserTyped.
        public void LoadFromSaved(SavedScript saved)
        {
            if (saved is null) throw new ArgumentNullException(nameof(saved));
            lock (_lock)
            {
                if (_disposed) return;
                _currentText = saved.Body;
                _isDirty = false;
                _pendingProposal = null;
                _mirror.SetText(saved.Body);
            }
        }

        // Agent path. Writes the body to the store, then either:
        //   * inline-promotes (clean editor — no user state to clobber), or
        //   * stages as a pending proposal for the UI to accept/discard
        //     (dirty editor — user has unsaved edits).
        // In both cases ScriptProposed is fired so UI subscribers can
        // refresh their display. A new proposal replaces any earlier
        // pending proposal in the dirty case (agent iterating). See
        // <staging-model> for the rationale.
        public SavedScript ProposeFromAgent(string name, string body, string? summary = null)
        {
            var saved = _store.Save(Flavor, name, body, summary);
            ScriptProposedEvent evt;
            lock (_lock)
            {
                if (_disposed) return saved;
                evt = new ScriptProposedEvent(saved, _currentText);
                if (_isDirty)
                {
                    // Stage for UI to prompt — preserve user edits in
                    // CurrentText + mirror until AcceptPending.
                    _pendingProposal = saved;
                }
                else
                {
                    // Inline promote — close V3-H3. The mirror is sync-
                    // flushed (FlushNow) so it's durable before the RPC
                    // call returns, eliminating the ~300 ms race where
                    // an iterating agent could read stale mirror content.
                    _currentText = saved.Body;
                    _pendingProposal = null;
                    _mirror.SetText(saved.Body);
                    _mirror.FlushNow();
                }
            }
            ScriptProposed?.Invoke(this, evt);
            return saved;
        }

        // UI accepted the pending proposal. Promote pending → current,
        // clear dirty, write the mirror, drop the pending slot. No-op
        // if there's no pending proposal (the call is harmless and
        // simplifies the VM's accept path — and is exactly the case
        // ProposeFromAgent's clean-editor inline-promote leaves behind).
        // Mirror is sync-flushed via FlushNow so the agent's
        // "read-mirror-before-propose" workflow doesn't race the
        // debounced write — see V3-H3 in the v3 crash-test journal.
        // Mirror write is inside _lock — see <linearization> on OnUserTyped.
        public void AcceptPending()
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_pendingProposal is null) return;
                _currentText = _pendingProposal.Body;
                _isDirty = false;
                _pendingProposal = null;
                _mirror.SetText(_currentText);
                _mirror.FlushNow();
            }
        }

        // UI rejected the pending proposal. CurrentText / mirror /
        // IsDirty all unchanged; the proposal stays on disk in the
        // store (the agent saved it intentionally) but is no longer
        // staged. No-op if there's no pending proposal.
        public void DiscardPending()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _pendingProposal = null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _mirror.Dispose();
        }
    }

    // Event payload for ScriptEditor.ScriptProposed. PreviousText is
    // CurrentText at the moment the proposal was staged — consumers
    // compare it to Saved.Body to decide whether to prompt the user
    // (e.g. only prompt when the proposal would visibly change the
    // editor).
    public sealed class ScriptProposedEvent : EventArgs
    {
        public SavedScript Saved { get; }
        public string PreviousText { get; }
        public ScriptProposedEvent(SavedScript saved, string previousText)
        {
            Saved = saved;
            PreviousText = previousText;
        }
    }
}
