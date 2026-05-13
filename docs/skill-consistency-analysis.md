<analysis>

This doc analyzes the three skills `/acd-mcp:start`, `/acd-mcp:repl`, `/acd-mcp:batch` and proposes a restructure that (a) forces the agent to load either /repl-or-renamed-to-/script OR /batch before doing any plugin work, (b) makes the two flavor skills structurally consistent, and (c) briefs the agent deeply enough that it stops exploring system internals on the fly.

Open questions are at the bottom — please mark answers inline.

</analysis>

<inconsistencies-found>

A pairwise audit of the three skill files. Each row is something that exists in one form on the REPL side and a different form on the BATCH side, or where /start carries content that should live in a flavor sibling.

<1-frontmatter-shape>
| Skill | `name` | `description` | `when_to_use` |
|---|---|---|---|
| `/start` | ✓ | ✓ | ✓ |
| `/repl`  | ✓ | ✓ | ✓ |
| `/batch` | ✓ | ✓ | **missing** — guidance is embedded inside `description` |

`/batch` should have an explicit `when_to_use:` line matching /repl's pattern. Without it, Claude Code's skill auto-loader has thinner signal for "is this skill relevant?"
</1-frontmatter-shape>

<2-mirror-file-naming>
The on-disk mirror file names are asymmetric:

* REPL editor mirror → `%LOCALAPPDATA%\Acd.Mcp\repl-buffer.csx`
* BATCH editor mirror → `%LOCALAPPDATA%\Acd.Mcp\editor-buffer.csx`

`editor-buffer.csx` is leftover from when BATCH was the only editor. After the introduction of the REPL palette tab, the BATCH file should have been renamed to `batch-buffer.csx`. This shows up in /start's file-locations table, in /repl's `<read-mirror-before-proposing>`, in /batch's step 1 of `<workflow>`, and (most importantly) in plugin code.

This is a real code smell at the system level, not just docs.
</2-mirror-file-naming>

<3-mcp-tool-naming>
| Tool | Pattern |
|---|---|
| `autocad_execute_csharp` | no flavor prefix — outlier |
| `autocad_repl_propose_script` | `autocad_<flavor>_propose_script` |
| `autocad_batch_propose_script` | `autocad_<flavor>_propose_script` |
| `autocad_batch_run_test` | `autocad_<flavor>_run_<phase>` |
| `autocad_batch_get_selection` | `autocad_<flavor>_get_<thing>` |

`autocad_execute_csharp` should be `autocad_repl_execute` (or post-rename `autocad_script_execute`) so the flavor prefix is universal. Today's outlier name actively hides which flavor the call belongs to.
</3-mcp-tool-naming>

<4-rules-and-section-placement>
Both flavor skills carry "always read the mirror before proposing" as a hard rule, but the location is asymmetric:

* `/repl` has a top-level `<read-mirror-before-proposing>` section that any agent skimming the skill cannot miss.
* `/batch` embeds the same rule inside step 1 of `<workflow>` — easier to miss when an agent reads only the rule list.

Same asymmetry for the staging model:

* `/repl` has a top-level `<the-staging-model>` section explaining clean-vs-dirty inline-promote semantics.
* `/batch` doesn't describe the staging model at all — it just mentions `replaced_dirty` in step 4. The model is the SAME on both sides (V3-H3 fixed both), but the doc only spells it out for REPL.
</4-rules-and-section-placement>

<5-response-shape-coverage>
* `/repl` `<response-shape>` documents ONE tool: `autocad_repl_propose_script`.
* `/batch` `<response-shape>` documents THREE tools: `autocad_batch_propose_script`, `autocad_batch_run_test`, `autocad_batch_get_selection`.

Asymmetry. After symmetrization both skills should have one `<response-shape>` per tool the flavor exposes.
</5-response-shape-coverage>

<6-start-is-fat-not-thin>
`/start` currently carries:

* `<what-this-plugin-is>` — correct location.
* `<two-modes>` — correct location.
* `<which-skill-when>` — correct location.
* `<repl-conventions>` — REPL-only content. Should live in /repl.
* `<serialization-etiquette>` — affects REPL primarily; less relevant to batch return values (batch scripts don't have a `returnValueJson`). Could move to /repl, or split if any of it applies to /batch's Step.Apply return strings.
* `<hard-rules>` — currently two rules (guessing-forbidden, one-type-per-dto). The first applies to both flavors. The second is /add-dto territory.
* `<file-locations>` — global table, correct location.
* `<sibling-skills>` — correct location.
* `<initial-checks>` — pipe + palette + AECC. Correct location.

Net: `/start` has ~140 lines, ~50% of which are REPL-specific reference material. /repl then refers back to /start for the conventions. That's a circular load — the agent loads /start, /start says "see /repl for proposing," /repl says "for the conventions, see /start." Either skill alone is incomplete.

The right structure (proposal): /start carries ONLY content that's true for both flavors, plus the hard MUST-LOAD rule. All REPL-specific reference moves into /repl (or /script after rename). /batch already owns its flavor reference; no change there.
</6-start-is-fat-not-thin>

<7-no-must-load-rule>
`/start`'s `<which-skill-when>` is *guidance*: "If the user is ambiguous, ask before loading a sibling skill — pulling in the wrong one wastes context." This describes WHEN to load but does NOT require loading. An agent reading /start and deciding the task is "small enough to skip /batch" gets no pushback from the skill text.

Effect we've seen: agents reach for `autocad_execute_csharp` without /repl loaded, then trip over the auto-return asymmetry, the `replaced_dirty` contract, or the `Acd.DataProvider` vs raw-XData distinction — all of which /repl would have told them.

Proposal: replace `<which-skill-when>` with a hard MUST-LOAD rule. Wording sketch:

> Before ANY MCP call against the plugin, you MUST load the matching flavor skill:
> * Single drawing operation → `/acd-mcp:script` (renamed from /repl).
> * Multi-drawing operation → `/acd-mcp:batch`.
>
> Loading is not optional. The skills carry rules that prevent silent failures (auto-return semantics, mirror-before-propose, response-shape discrimination, replaced_dirty UX). If the user's intent is ambiguous, ASK before calling any plugin tool — do NOT default to one flavor.

This is still soft (Claude Code has no hard load-gate), but it's strong enough that an agent following the rule list cannot rationalize skipping the sibling.
</7-no-must-load-rule>

<8-auto-return-gotcha-not-documented>
The asymmetry we just diagnosed (`new int[] {1,2,3};` returns; `new List<int> {1,2,3};` discards) is invisible in the skills. Adding a small note in /repl's `<repl-script-conventions>` or in a sibling `<auto-return-gotchas>` section prevents future agents from rediscovering this and proposing a fix in the wrong file.
</8-auto-return-gotcha-not-documented>

<9-saved-script-folder-rename-cascade>
* `%APPDATA%\Acd.Mcp\scripts\repl\<name>.csx` — disk path.
* `// @flavor: repl` — header line that the saved-script store parses.

If we rename /repl → /script at the skill level, do we also rename the disk folder and the header value? Or do we leave the on-disk concept "repl flavor" intact and rename only the skill name?

Two cohesive choices, no middle ground:

* **Skill-only rename**: skill becomes /acd-mcp:script. The on-disk folder, mirror file, header `@flavor: repl`, palette tab, all stay the same. Less work, but a new contributor reading both the skill and the code will see two names for the same thing.
* **Full rename**: skill /script, folder `scripts\script\`, mirror `script-buffer.csx` (or `script-mirror.csx`), header `// @flavor: script`, palette tab labelled "SCRIPT" instead of "REPL", MCP tool `autocad_script_propose`. Bigger churn, but the two names collapse into one.

(Note: the word "REPL" technically means "read-eval-print loop." A non-interactive proposed-and-run script isn't really a REPL submission. "Script" is more accurate. So full rename has merit beyond cosmetics.)
</9-saved-script-folder-rename-cascade>

<10-sibling-skill-links>
* `/start` has `<sibling-skills>` linking to /repl, /batch, /add-dto.
* `/repl` mentions /acd-mcp:start in prose but no `<sibling-skills>` section.
* `/batch` has no `<sibling-skills>` section either.

Symmetric fix: each skill carries a small `<sibling-skills>` block linking to the others, so any single skill load surfaces the full map.
</10-sibling-skill-links>

</inconsistencies-found>

<proposed-restructure>

Aiming for the smallest restructure that hits all the goals: must-load enforcement, structural symmetry, deep briefing.

<a-start-thin>
`/start` collapses to ~50 lines of plugin-only briefing:
* `<what-this-plugin-is>` — keep.
* `<two-modes>` — keep, but shrink the descriptions to one line each + pointer to flavor skill.
* `<MUST-LOAD-a-flavor>` — NEW hard rule (replaces `<which-skill-when>`).
* `<initial-checks>` — keep.
* `<file-locations>` — keep (single source of truth for paths).
* `<hard-rules>` — keep just the guessing-forbidden rule (universal).
* `<sibling-skills>` — keep.

Everything else moves out.
</a-start-thin>

<b-script-fat>
`/script` (renamed /repl) owns the entire REPL/script flavor surface:

* `<what-this-skill-is-for>` — existing.
* `<two-execution-paths>` — direct execute vs propose-to-editor.
* `<repl-conventions>` — MOVED from /start: globals, namespaces, using-first, block-form-using, trailing-expression-return (with the **new** auto-return gotcha for ObjectCreation).
* `<serialization-etiquette>` — MOVED from /start.
* `<when-to-propose-vs-execute-directly>` — existing.
* `<read-mirror-before-proposing>` — existing.
* `<response-shape>` — extended to cover BOTH `autocad_script_execute` and `autocad_script_propose`.
* `<replaced-dirty-contract>` — existing.
* `<the-staging-model>` — existing.
* `<rules-the-agent-must-follow>` — existing.
* `<what-NOT-to-do>` — existing.
* `<file-locations>` — kept thin (refer to /start).
* `<sibling-skills>` — NEW.
</b-script-fat>

<c-batch-symmetric>
`/batch` is restructured to mirror `/script` section-for-section where it makes sense:

* Add explicit `when_to_use:` frontmatter line.
* Add a top-level `<read-mirror-before-proposing>` section (extract from workflow step 1).
* Add a top-level `<the-staging-model>` section (parallel to /script's — replaced_dirty contract belongs here).
* Keep `<response-shape>` (already covers three tools).
* Keep `<workflow>`, `<step-dsl>`, `<cross-file-state>`, `<example-full-script>`, `<diagnosing-failures>` — these are batch-flavor-only.
* Keep `<rules-the-agent-must-follow>`, `<what-NOT-to-do>`.
* `<file-locations>` — kept thin.
* `<sibling-skills>` — NEW.
</c-batch-symmetric>

<d-rename-cascade-if-yes>
If full rename:
* Skill folder: `skills/repl/` → `skills/script/`.
* Frontmatter `name: repl` → `name: script`.
* Mirror file: `repl-buffer.csx` → `script-buffer.csx` (or `script-mirror.csx`; pair with `batch-buffer.csx` → `batch-mirror.csx` for symmetry).
* On-disk folder: `scripts/repl/` → `scripts/script/`.
* Header: `// @flavor: repl` → `// @flavor: script` (saved-script store parser updated, old files migrated or back-compat-aliased for one release).
* MCP tool: `autocad_execute_csharp` → `autocad_script_execute`; `autocad_repl_propose_script` → `autocad_script_propose`.
* Palette tab label "REPL" → "SCRIPT".
* DevReload command? "ACDMCP_PALETTE" stays.

Code touchpoints (rough estimate): ~30–50 files. Tests need fixture renames. Plugin v0.2.0 → v0.3.0 (breaking).
</d-rename-cascade-if-yes>

</proposed-restructure>

<open-decisions>

<q1-rename-scope>
**How far should the /repl → /script rename go?**

* **A. Skill-only.** Rename `skills/repl/` → `skills/script/`, frontmatter, sibling links. NOTHING else changes. Mirror file stays `repl-buffer.csx`, palette tab stays "REPL", MCP tool stays `autocad_execute_csharp`. Smallest change; preserves the two-names-for-one-concept smell.

* **B. Full rename.** All call-sites renamed: mirror file, on-disk folder, header value, MCP tool names, palette tab label. Larger PR, breaking for anyone with saved scripts (migration path needed), but the system speaks one language afterward.

* **C. Hybrid.** Rename skill + MCP tools + palette tab (user-visible surface), but leave mirror file name / on-disk folder / header alone (internal smell, but no migration concern).

ANSWER:
</q1-rename-scope>

<q2-start-thin-vs-fat>
**Should /start become a thin redirector (push REPL conventions out to /script)?**

* **A. Thin.** /start is ~50 lines of plugin-level briefing only. To use REPL or batch, the agent MUST load the flavor sibling — and loading a sibling gives the agent everything it needs without circling back to /start. Symmetric, deep, but `/start` alone is no longer "enough to do basic REPL work."

* **B. Keep current fat.** /start still carries REPL conventions, serialization etiquette, hard rules, initial checks. The agent can do REPL work with only /start loaded. Inconsistent (no equivalent batch content in /start), but lower migration risk.

ANSWER:
</q2-start-thin-vs-fat>

<q3-must-load-strictness>
**How strict should the MUST-LOAD rule be?**

* **A. Strict.** "Before ANY MCP call, you MUST load the matching flavor skill. Loading is not optional." (My preference — agents follow rules when they're stated as rules.)

* **B. Strong-default.** "Load the matching flavor skill before non-trivial work; the trivial probe (`Doc.Name`, `typeof(T).GetProperties()...`) is exempt." More lenient but introduces a judgment call.

ANSWER:
</q3-must-load-strictness>

<q4-mirror-naming-if-renaming>
**(Only relevant if Q1 = B or C and we rename the mirror files.)** Preferred mirror file names?

* **A.** `script-buffer.csx` / `batch-buffer.csx`.
* **B.** `script-mirror.csx` / `batch-mirror.csx`.
* **C.** `script.csx` / `batch.csx` (drops the suffix entirely — they're already in `%LOCALAPPDATA%\Acd.Mcp\`).

ANSWER:
</q4-mirror-naming-if-renaming>

</open-decisions>

<implementation-order-once-decided>

For reference, in case you want to see what the work looks like before answering:

1. Skill restructure (docs only). Land first, isolated. ~3 files touched.
2. /start MUST-LOAD rule added. Same PR as (1) or follow-up.
3. (If renaming) code/UI rename pass. Separate PR; touches plugin + bridge + tests + scripts. Plugin version bump.
4. (Independent) the auto-return ObjectCreation fix from the prior analysis. Drop the two syntax kinds from `IsValidExpressionStatement`. ~5 LOC + 2 xUnit tests.

Items 1–2 carry no migration risk and could land today. Item 3 should land as one atomic PR with a migration note in the release notes. Item 4 is independent.

</implementation-order-once-decided>
