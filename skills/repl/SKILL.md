---
name: repl
description: |
  Stage REPL C# scripts in the ACD-MCP palette editor for the user to
  review and (optionally) edit before they run. Use when the user wants
  to see / approve / tweak the script before it executes — NOT for
  routine information gathering or one-shot edits (those go through
  `autocad_execute_csharp` directly). Covers the propose-to-editor
  workflow, the read-mirror-before-proposing rule, and the dirty-edit
  race contract.
when_to_use: User wants to review or edit a script before it runs against the active drawing; user is iterating on a longer REPL script they want to keep; agent decides to propose because the change is substantial and the user should sanity-check. For ad-hoc execute-and-report work, just call `autocad_execute_csharp` instead.
---

<what-this-skill-is-for>
A workflow for **staging** REPL C# scripts in the palette editor so the
user reviews them before pressing Run. There are two ways the agent can
interact with the REPL editor's flow:

1. **Direct execute** — `autocad_execute_csharp(code, timeout_ms?)`.
   Runs the code immediately against the active drawing. Returns the
   result. Does NOT touch the palette editor. The user doesn't see the
   script unless they look at the execution log. This is the default
   for everything: information gathering, one-shot edits, anything the
   user hasn't asked to review.

2. **Propose-to-editor** — `autocad_repl_propose_script(name,
   script_body, input_summary?)`. Saves the script to
   `%APPDATA%\Acd.Mcp\scripts\repl\<name>.csx` AND stages it in the
   REPL palette editor for the user. The user reviews, edits if they
   want, then clicks Run. The script is also kept on disk so the user
   can re-load it later via the Manage Scripts window.

The two tracks are independent. You can run direct-execute queries to
gather information (layer names, object types, …) **while** a proposed
script sits in the editor waiting for the user — direct execute does
not touch the editor, so the user's review is undisturbed.
</what-this-skill-is-for>

<when-to-propose-vs-execute-directly>
Default to direct execute. Propose only when one of these holds:

* The user asks you to "show me the script before running" / "let me
  see what you'd do" / "save this as a script".
* You're iterating on a non-trivial script (>~30 lines, or one the
  user has expressed interest in keeping) and the user should sanity-
  check before each run.
* The script does something irreversible enough that running it without
  review would be reckless even with the UI's Reset button (e.g. mass
  property edits across many entities, file-system writes).

Do NOT propose for: "what's the active layer?", "how many lines are on
layer FOO?", "what's the DTO shape for Civil Surface?". Those are
information gathering — direct execute.

Do NOT propose just because the script is "clever". The user is busy.
Propose when there's user-facing value in review, not when there's
agent-facing aesthetic value in saving.
</when-to-propose-vs-execute-directly>

<the-two-track-workflow>
A canonical session that uses both tracks:

1. **User asks for something substantial** ("set transparency to 50%
   on every entity on layer X-FOOBAR in this drawing, but only if the
   layer has more than 10 entities").

2. **Gather info via direct execute.** Confirm the layer exists, count
   entities on it, sanity-check the user's assumptions:
   ```
   autocad_execute_csharp("""
   using (var tx = Db.TransactionManager.StartTransaction())
   {
       var lt = (LayerTable)tx.GetObject(Db.LayerTableId, OpenMode.ForRead);
       if (!lt.Has("X-FOOBAR")) return new { exists = false };
       // count entities on X-FOOBAR in modelspace
       var bt = (BlockTable)tx.GetObject(Db.BlockTableId, OpenMode.ForRead);
       var ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
       int n = 0;
       foreach (ObjectId id in ms)
       {
           var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
           if (ent is not null && ent.Layer == "X-FOOBAR") n++;
       }
       return new { exists = true, count = n };
   }
   """)
   ```
   This DOES NOT touch the editor.

3. **Propose the script.** Read the REPL mirror first (see
   `<read-mirror-before-proposing>`), incorporate any user edits you
   find, then:
   ```
   autocad_repl_propose_script(
       name = "transparency-on-x-foobar",
       script_body = "...",
       input_summary = "set transparency=50 on layer X-FOOBAR (12 entities)")
   ```
   The response carries `replaced_dirty: true|false` — see
   `<replaced-dirty-contract>`.

4. **Tell the user it's staged.** "I've put the script in the REPL
   editor — review and click Run when you're ready."

5. **If the user asks for changes**, GO TO STEP 3 — read the mirror
   first (the user may have hand-edited), then propose again.

6. **Live execution is the user's click.** You don't need to ask. The
   propose tool's job is done once the script is staged.
</the-two-track-workflow>

<read-mirror-before-proposing>
Before EVERY call to `autocad_repl_propose_script`, read the live mirror
file:

```
%LOCALAPPDATA%\Acd.Mcp\repl-buffer.csx
```

This is what the user is currently looking at in the REPL editor.
Skipping this step means you may overwrite hand-edits the user has made
since your last proposal.

**Mirror write semantics** (see `<the-staging-model>` for the propose-time
side):

* When the **user is typing** in the WPF text-box, writes are debounced
  ~250 ms so a flurry of keystrokes doesn't translate into a flurry of
  disk writes.
* When an **agent propose** lands and is accepted (clean-editor inline
  promote, or user clicks Yes on the dirty-prompt), the mirror is
  **sync-flushed** — the file is on disk before `autocad_repl_propose_script`
  returns or before the prompt-accept callback completes.

So: if your read directly follows the user's typing, there may be ≤250 ms
of lag. If your read follows an agent propose, the mirror is already
durable.

The flow is:

1. **Read the mirror** with ordinary file tools.
2. **Compare** what's there to what you last proposed. Differences are
   user edits.
3. **Plan your update** against the user's current content, not against
   your own last proposal.
4. **Call `autocad_repl_propose_script`**.

If you can't read the mirror (file doesn't exist yet, permission error),
the REPL editor is empty / fresh — proceed with the proposal.
</read-mirror-before-proposing>

<response-shape>
`autocad_repl_propose_script` returns a **discriminated success-shape**.
Always check `ok` first.

```
{ ok: true,  saved_as: "<path>", name: "<name>", replaced_dirty: <bool|null>,
  error_code: null, error_message: null }                      # success

{ ok: false, error_code: "<numeric>", error_message: "<plugin text>",
  saved_as: null, name: null, replaced_dirty: null }           # failure
```

The bridge never throws on plugin-rejected failures — those would be
stripped to a generic "An error occurred invoking ..." by the MCP SDK
(see V2-G4 in `CRASH_TEST_V2_JOURNAL.md`). Instead the plugin's message
travels on the success path in `error_message`. Typical `ok: false`
case: REPL palette not open (`error_code: "-32603"`,
`error_message: "REPL palette is not open. Run ACDMCP_PALETTE inside AutoCAD first."`).

The same shape is shared with `autocad_batch_propose_script`, so the
agent's "check `ok` first" pattern transfers between flavours.
</response-shape>

<replaced-dirty-contract>
`replaced_dirty` (on the success path) is the agent's signal about what
the user is about to experience:

* **`false` or `null`** — the editor was clean. The proposal was
  inline-promoted (see `<the-staging-model>`) — the editor's visible
  text and the mirror file already reflect the new body when this RPC
  returns. Nothing further to say to the user.

* **`true`** — the editor had unsaved typed edits AND the new body
  differs from what the user is currently editing. The user is being
  prompted (async dialog: "Replace your unsaved changes? Yes/No"). The
  user's Yes/No is NOT reported here — the RPC returns before the
  dialog resolves.

When you see `replaced_dirty: true`, **warn the user proactively in
your reply**: "I'm proposing an updated version of the script — you'll
see a prompt to replace your in-progress edits. Click Yes to take my
version, or No to keep yours and I'll re-read it before my next
proposal."
</replaced-dirty-contract>

<the-staging-model>
The disposition of a proposal depends on whether the editor has unsaved
user edits at propose time (see V3-H3 in `CRASH_TEST_V3_JOURNAL.md`):

* **Editor is CLEAN (`IsDirty=false`)** — the common agent-iteration
  case. `ScriptEditor.ProposeFromAgent` **inline-promotes**: `CurrentText`
  becomes the saved body, the mirror file is sync-flushed (no debounce),
  no pending slot is set. The mirror is durable on disk **before** the
  RPC returns. `replaced_dirty: false`.

* **Editor is DIRTY (`IsDirty=true`)** — the user has typed edits we'd
  clobber. ProposeFromAgent **stages** the body in `PendingProposal`,
  CurrentText and the mirror stay at what the user is editing, and the
  UI prompts the user. On Yes → AcceptPending promotes pending →
  current (and sync-flushes the mirror). On No → DiscardPending leaves
  the user's text untouched. `replaced_dirty: true`.

**The mirror file always reflects what the user sees on screen.**
Reading the mirror is honest — you never read your own last-proposed
body back during the brief prompt window in the dirty case, and you
read your own proposal immediately in the clean case (because that IS
what the user sees now).
</the-staging-model>

<repl-script-conventions>
REPL scripts are NOT batch scripts. They run against the active
drawing's `Doc / Db / Ed / CivilDoc / Acd` globals, not against a
side-loaded `xDb / xTx / ctx`. There's no Step DSL, no Test/Live, no
Require/Apply. Read **`/acd-mcp:start` `<repl-conventions>`** for the
full surface — using-first, block-form `using(var tx = ...) { ... }`
for disposables, trailing-expression-return semantics, no `dynamic`,
etc.

A REPL script saved via propose has an auto-prepended header:

```
// @flavor: repl
// @name: telegram-style-name
// @summary: one-line description for the Manage Scripts window
```

You only supply the body to `autocad_repl_propose_script`; the header
is prepended by the saved-script store. The header survives in the
editor's display.
</repl-script-conventions>

<rules-the-agent-must-follow>
1. **Direct execute is the default.** Reach for `autocad_execute_csharp`
   first. Only escalate to `autocad_repl_propose_script` when the user
   has asked to review, or when iterating on a substantial script.

2. **Always read the REPL mirror before proposing.** Skipping this is
   how you trample user edits. The file is at
   `%LOCALAPPDATA%\Acd.Mcp\repl-buffer.csx`.

3. **Live execution is the user's click.** There is no
   `autocad_repl_run` tool. Don't ask for one.

4. **When `replaced_dirty: true`, warn the user.** They're about to see
   a dialog and need context.

5. **The two tracks are independent.** You can call
   `autocad_execute_csharp` to gather info while a proposed script sits
   in the editor — direct execute doesn't disturb the staged proposal.

6. **Telegram-style names.** Short, descriptive, hyphenated:
   `transparency-on-x-foobar`, not `SetTransparencyOnLayerXFoobar`.
</rules-the-agent-must-follow>

<file-locations>
REPL-specific paths (general folder layout is in `/acd-mcp:start`):

* Editor mirror (read before proposing):
  `%LOCALAPPDATA%\Acd.Mcp\repl-buffer.csx`

* Saved REPL scripts (also readable as plain files):
  `%APPDATA%\Acd.Mcp\scripts\repl\<name>.csx`
</file-locations>

<what-NOT-to-do>
* Don't propose for trivial one-shot queries. `autocad_execute_csharp`
  is right there and doesn't touch the user's editor.
* Don't propose without reading the mirror first.
* Don't call `autocad_repl_propose_script` and `autocad_execute_csharp`
  with the SAME body to "make sure it ran". Propose stages; execute
  runs. They're separate verbs.
* Don't try to "click Run for the user." That's their action.
* Don't write a script body with the `// @flavor: repl` header inside
  it — the tool prepends the header. If you include it manually you'll
  get a duplicated header in the saved file.
</what-NOT-to-do>
