<!--
Plugin distribution spec — extracted from the /acd-batch design pass
because it is a different concern with a different audience.

NOT for the batch-implementation agent. This describes how the entire
ACD-MCP product (existing MCP + future batch + future skills) ships to
end users across multiple AI clients.
-->

<status>idea / spec — not implemented, not scheduled, not assigned</status>

<scope>
How ACD-MCP ships to end users as **one installable thing** that supports
multiple AI clients (Claude Desktop, Claude Code, Codex, GitHub Copilot,
and ideally ChatGPT) plus the AutoCAD plugin side, with sensible defaults
and predictable upgrade behaviour.
</scope>

<precondition>
The MCP must actually be reachable from a real AI client first. The bridge
exists today; verify one successful round-trip from Claude (or any other
supported client) before designing the installer. Today's manual config
flow is documented in README.md.
</precondition>

<multi-client-support>
Each AI client that can host a local stdio MCP server uses its own config
file or registration mechanism. The installer must handle each one
gracefully — register where present, skip where absent, never fail because
one client isn't installed.

Known support landscape (verify before relying):

  Claude Desktop      `%APPDATA%\Claude\claude_desktop_config.json`
                      Add the "autocad" server under "mcpServers".

  Claude Code         `claude mcp add autocad <path-to-Acd.Mcp.Bridge.exe>`
                      Or a global config file the CLI manages.

  Codex (OpenAI CLI)  `~/.codex/config` (or platform equivalent). MCP
                      stdio support landed in late 2025 — confirm exact
                      config shape before implementing.

  GitHub Copilot (VS Code) — MCP stdio support added in late 2025 via
                      VS Code settings (`github.copilot.advanced.mcp.servers`
                      or similar — verify current key name).

  ChatGPT (web)       Cannot consume local stdio MCP servers. Connectors
                      are server-side only. Out of scope for v1.

  ChatGPT (desktop)   May gain local MCP support — needs research. Mark
                      as "investigate" not "support".

Installer strategy: probe each client's config location, register where it
exists, log "skipped (not installed)" otherwise. Idempotent — re-running
the installer updates existing entries rather than duplicating.
</multi-client-support>

<claude-plugin-shape>
Distribute as a Claude plugin so `/plugin install <git-or-local-path>` is
the single user-facing command. Layout:

  acd-mcp/
    .claude-plugin/
      plugin.json                       ← registers MCP server + skills
      skills/
        acd-batch/SKILL.md              ← drives the autonomous batch flow
        acd-mcp-add-dto/SKILL.md        ← teaches the agent how to add a DTO
      mcp/
        Acd.Mcp.Bridge/                 ← bridge binary + dependencies
    autocad-bundle/
      ACD-MCP.bundle/
        PackageContents.xml
        Contents/
          Acd.Mcp.dll
          ICSharpCode.AvalonEdit.dll
          CommunityToolkit.Mvvm.dll
          ...
    dto-system/                         ← shipped DTOs, see <dto-folders>
      circle.csx
      line.csx
      ...
    scripts-starter/                    ← optional starter examples
      batch/example-set-layer-color.csx
      repl/example-list-blocks.csx
    install-hooks/
      install.ps1                       ← runs on /plugin install
      uninstall.ps1

Reference for the AutoCAD bundle layout:
`C:\Users\MichailGolubjev\AppData\Roaming\Autodesk\ApplicationPlugins\DevReload.bundle\`
— `PackageContents.xml` at root, `Contents/` with every DLL.
</claude-plugin-shape>

<install-hook>
`install.ps1` does the heavy lifting that the Claude plugin host can't do
itself:

1. Locate AutoCAD's `ApplicationPlugins` folder
   (`%APPDATA%\Autodesk\ApplicationPlugins\`).
2. If `ACD-MCP.bundle` already exists, compare versions:
   - Newer or equal on disk → leave alone, log "already up to date".
   - Older on disk → check if AutoCAD is running.
     - Running → tell the user to close AutoCAD and re-run install.
     - Not running → copy `autocad-bundle/ACD-MCP.bundle/` over.
3. Seed DTO + scripts folders if missing (see `<dto-folders>` and
   `<scripts-folders>`). **Never overwrite** existing user content.
4. Register the MCP server with each supported client (probe per
   `<multi-client-support>`).
5. Log a summary of what was done so the user can audit the changes.

Uninstall is the inverse: remove the bundle (if AutoCAD closed),
deregister from each client, leave user-authored DTOs and scripts intact
unless `--purge` is passed.
</install-hook>

<dto-folders>
Two-tier DTO storage so installer updates never clobber user customisation:

  %LOCALAPPDATA%\Acd.Mcp\dto-system\        ← installer manages this folder
                                              completely. Wiped + repopulated
                                              on every plugin install.

  %APPDATA%\Acd.Mcp\dto-user\               ← user / agent writes here.
                                              Installer NEVER touches.
                                              Overrides any same-typed DTO in
                                              dto-system.

Resolution order at serializer time:
  1. Look in `dto-user/`. If a converter for type T exists, use it.
  2. Otherwise fall back to `dto-system/`.
  3. Otherwise emit the `{ "$unsupported": "TypeName" }` marker.

The `acd-mcp-add-dto` skill must teach the agent:
  - "system DTOs are shipped — do not edit them, they will be overwritten".
  - "user DTOs go in `dto-user/` and override system DTOs of the same type".
  - "name files after the type — one type per file (e.g. `circle.csx`)".
  - The verify-don't-guess rule for AutoCAD API property names.

One file per type — the implementation agent must NOT write a `entities.csx`
that registers ten DTOs. Per-file granularity lets users override individual
types and lets the installer overwrite system DTOs cleanly.
</dto-folders>

<scripts-folders>
Saved scripts live entirely under the user-writable area; the installer
seeds optional examples but never overwrites:

  %APPDATA%\Acd.Mcp\scripts\batch\         ← @flavor: batch
  %APPDATA%\Acd.Mcp\scripts\repl\          ← @flavor: repl

On install, if these folders are empty, copy contents of
`scripts-starter/` into them. If they already contain files, leave alone.
</scripts-folders>

<update-and-version-policy>
1. The plugin's `plugin.json` carries a semver. Each install writes the
   version into a state file (e.g. `%LOCALAPPDATA%\Acd.Mcp\version.json`).
2. The bundle's `PackageContents.xml` carries the same version, used by
   AutoCAD itself.
3. On upgrade: if AutoCAD is running, abort with a clear message.
   AutoCAD locks the bundle's DLLs once loaded.
4. On any "would overwrite user content" decision, the installer prefers
   the user's content. Loudly.
5. The bridge binary updates are safe regardless of AutoCAD state — it
   runs out-of-process.
</update-and-version-policy>

<open-research-items>
Before implementing the installer, confirm:

1. Exact Claude plugin manifest schema. `plugin.json` field names, install
   hook entry points, MCP-server registration syntax. The Claude docs
   site is authoritative.

2. Codex CLI's MCP config format and location (cross-platform — Linux vs
   Windows vs macOS). Verify before writing a probe.

3. GitHub Copilot for VS Code: exact settings key for stdio MCP servers
   and whether per-workspace or global settings are appropriate.

4. ChatGPT Desktop: does it support local MCP at all? If yes, what's the
   config? If no, document that ChatGPT users use the manual instructions
   in the README.

5. Whether the Claude plugin system can drive an external installer script
   directly, or whether the user has to run it separately after
   `/plugin install`. If the latter, the plugin's post-install message
   should tell them what to do.

6. Signing / smartscreen on Windows. An unsigned `Acd.Mcp.Bridge.exe` will
   trigger SmartScreen warnings. For an open-source release this needs
   thought — code-signing certificate or accept the warnings.

7. Open-source release plan. The user mentioned the project might go
   open-source. Some installer paths assume a private-distribution channel.
   Verify before publishing.
</open-research-items>

<not-now>
This entire document is deferred. Implementing it depends on the batch
feature being done, the DTO model being stable, and the bridge having
real-client verification under its belt. Revisit once `/acd-batch` ships.
</not-now>
