using System;
using System.IO;
using Xunit;

namespace Acd.Mcp.Batch.Tests
{
    public class ScriptEditorTests : IDisposable
    {
        private readonly string _root;
        private readonly string _mirrorPath;
        private readonly SavedScriptStore _store;
        private readonly EditorBuffer _mirror;
        private readonly ScriptEditor _editor;

        public ScriptEditorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "acd-mcp-editor-" + Guid.NewGuid().ToString("N"));
            _mirrorPath = Path.Combine(_root, "mirror.csx");
            _store = new SavedScriptStore(_root);
            // Short debounce so tests don't wait long for the mirror to land.
            _mirror = new EditorBuffer(_mirrorPath, TimeSpan.FromMilliseconds(10));
            _editor = new ScriptEditor(ScriptFlavor.Batch, _store, _mirror);
        }

        public void Dispose()
        {
            _editor.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void OnUserTyped_UpdatesCurrentText_AndSetsDirty()
        {
            Assert.Equal("", _editor.CurrentText);
            Assert.False(_editor.IsDirty);

            _editor.OnUserTyped("// hello");

            Assert.Equal("// hello", _editor.CurrentText);
            Assert.True(_editor.IsDirty);
        }

        [Fact]
        public void OnUserTyped_WritesToMirrorFile()
        {
            _editor.OnUserTyped("// mirrored body");
            _mirror.FlushNow();

            Assert.True(File.Exists(_mirrorPath));
            Assert.Equal("// mirrored body", File.ReadAllText(_mirrorPath));
        }

        [Fact]
        public void LoadFromSaved_ClearsDirty_ReplacesText_DropsPending()
        {
            _editor.OnUserTyped("// pending edits");
            Assert.True(_editor.IsDirty);
            _editor.ProposeFromAgent("ghost-proposal", "// ignore me", null);
            Assert.True(_editor.HasPendingProposal);

            var saved = _store.Save(ScriptFlavor.Batch, "loaded-script", "// from disk", summary: "");
            _editor.LoadFromSaved(saved);

            Assert.False(_editor.IsDirty);
            Assert.Contains("// from disk", _editor.CurrentText);
            Assert.False(_editor.HasPendingProposal);
        }

        [Fact]
        public void ProposeFromAgent_StagesPending_DoesNotTouchCurrentText_Mirror_OrDirty()
        {
            // Pretend the user has unsaved edits in the editor.
            _editor.OnUserTyped("// user-typed pending");
            _mirror.FlushNow();
            var originalMirrorContent = File.ReadAllText(_mirrorPath);
            Assert.True(_editor.IsDirty);

            ScriptProposedEvent? captured = null;
            _editor.ScriptProposed += (_, e) => captured = e;

            var saved = _editor.ProposeFromAgent(
                name: "agent-script",
                body: "// agent body",
                summary: "first proposal");

            // Event fired with the previous (user) text as PreviousText.
            Assert.NotNull(captured);
            Assert.Equal("agent-script", captured!.Saved.Name);
            Assert.Equal("// user-typed pending", captured.PreviousText);

            // CurrentText / IsDirty / mirror UNCHANGED — the proposal is
            // staged, not committed.
            Assert.Equal("// user-typed pending", _editor.CurrentText);
            Assert.True(_editor.IsDirty);
            _mirror.FlushNow();
            Assert.Equal(originalMirrorContent, File.ReadAllText(_mirrorPath));

            // PendingProposal exposes the agent's body for the UI to act on.
            Assert.True(_editor.HasPendingProposal);
            Assert.NotNull(_editor.PendingProposal);
            Assert.Contains("// agent body", _editor.PendingProposal!.Body);

            // The on-disk store record exists (proposals are persistent
            // even when not yet accepted — the agent saved it).
            var roundTripped = _store.TryGet(ScriptFlavor.Batch, "agent-script");
            Assert.NotNull(roundTripped);
            Assert.Contains("// agent body", roundTripped!.Body);
            Assert.Equal("first proposal", roundTripped.Summary);
        }

        [Fact]
        public void AcceptPending_PromotesPending_ClearsDirty_WritesMirror_DropsPending()
        {
            _editor.OnUserTyped("// user-typed pending");
            var staged = _editor.ProposeFromAgent("agent", "// agent body", null);
            Assert.True(_editor.HasPendingProposal);

            // SavedScript.Body is the on-disk content INCLUDING the auto-
            // prepended `// @flavor:` / `// @name:` header — the editor
            // displays the same string the store wrote, so that's what
            // we expect after AcceptPending.
            var expectedBody = staged.Body;
            Assert.Contains("// agent body", expectedBody);
            Assert.Contains("// @flavor:", expectedBody);

            _editor.AcceptPending();
            _mirror.FlushNow();

            Assert.Equal(expectedBody, _editor.CurrentText);
            Assert.False(_editor.IsDirty);
            Assert.False(_editor.HasPendingProposal);
            Assert.Equal(expectedBody, File.ReadAllText(_mirrorPath));
        }

        [Fact]
        public void DiscardPending_LeavesCurrentText_Mirror_AndDirty_Intact()
        {
            _editor.OnUserTyped("// user-typed pending");
            _mirror.FlushNow();
            _editor.ProposeFromAgent("agent", "// agent body", null);
            Assert.True(_editor.HasPendingProposal);

            _editor.DiscardPending();
            _mirror.FlushNow();

            Assert.False(_editor.HasPendingProposal);
            Assert.Equal("// user-typed pending", _editor.CurrentText);
            Assert.True(_editor.IsDirty);
            Assert.Equal("// user-typed pending", File.ReadAllText(_mirrorPath));
        }

        [Fact]
        public void ProposeFromAgent_OnCleanEditor_LeavesDirtyFalse()
        {
            Assert.False(_editor.IsDirty);
            _editor.ProposeFromAgent("clean-propose", "// body", summary: null);
            Assert.False(_editor.IsDirty);
        }

        [Fact]
        public void ProposeFromAgent_OnCleanEditor_InlinePromotes_AndSyncFlushesMirror()
        {
            // V3-H3 regression test: on a clean editor (no user edits to
            // clobber), ProposeFromAgent inline-promotes the proposal so
            // the mirror file is on disk BEFORE the call returns. No
            // FlushNow needed by the test, no debounce wait.
            Assert.False(_editor.IsDirty);
            Assert.False(File.Exists(_mirrorPath));

            var saved = _editor.ProposeFromAgent("clean-inline", "// auto-promoted", summary: null);

            // CurrentText IS the saved body now (no staging).
            Assert.Equal(saved.Body, _editor.CurrentText);
            // No pending proposal — already promoted.
            Assert.False(_editor.HasPendingProposal);
            Assert.Null(_editor.PendingProposal);
            // IsDirty stays false — promotion ≠ user typing.
            Assert.False(_editor.IsDirty);
            // Mirror is on disk synchronously — the whole point of H3.
            Assert.True(File.Exists(_mirrorPath));
            Assert.Equal(saved.Body, File.ReadAllText(_mirrorPath));
        }

        [Fact]
        public void AcceptPending_FlushesMirror_Synchronously()
        {
            // V3-H3 regression test: AcceptPending writes the mirror
            // file synchronously (FlushNow inside) — the test does NOT
            // call _mirror.FlushNow() and the assertion still holds.
            _editor.OnUserTyped("// user-typed");
            var staged = _editor.ProposeFromAgent("accept-sync", "// agent body", null);
            Assert.True(_editor.HasPendingProposal);

            _editor.AcceptPending();
            // No FlushNow here on purpose.

            Assert.True(File.Exists(_mirrorPath));
            Assert.Equal(staged.Body, File.ReadAllText(_mirrorPath));
        }

        [Fact]
        public void ProposeFromAgent_Twice_ReplacesPending()
        {
            _editor.OnUserTyped("// user");
            _editor.ProposeFromAgent("v1", "// first", null);
            _editor.ProposeFromAgent("v2", "// second", null);

            Assert.True(_editor.HasPendingProposal);
            Assert.Equal("v2", _editor.PendingProposal!.Name);
            Assert.Contains("// second", _editor.PendingProposal.Body);
        }

        [Fact]
        public void AcceptPending_WithNoPending_IsNoOp()
        {
            _editor.OnUserTyped("// user");
            var before = _editor.CurrentText;
            var beforeDirty = _editor.IsDirty;
            _editor.AcceptPending();
            Assert.Equal(before, _editor.CurrentText);
            Assert.Equal(beforeDirty, _editor.IsDirty);
        }

        [Fact]
        public void DiscardPending_WithNoPending_IsNoOp()
        {
            _editor.OnUserTyped("// user");
            var before = _editor.CurrentText;
            var beforeDirty = _editor.IsDirty;
            _editor.DiscardPending();
            Assert.Equal(before, _editor.CurrentText);
            Assert.Equal(beforeDirty, _editor.IsDirty);
        }

        [Fact]
        public void Constructor_Rejects_NullStore_Or_NullMirror()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ScriptEditor(ScriptFlavor.Batch, null!, _mirror));
            Assert.Throws<ArgumentNullException>(() =>
                new ScriptEditor(ScriptFlavor.Batch, _store, null!));
        }
    }
}
