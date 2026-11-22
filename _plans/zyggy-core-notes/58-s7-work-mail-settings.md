# Spec 58 slice 7 — the work-mail session's Claude Code settings (Q24)

Q24 was accepted by the owner on 2026-11-22: keep the work-mail session under prompts. Central widened it to every tool: **in the
session that may send or move work mail, any tool use beyond Read and the `zyggy outlook` read verbs needs the owner's answer to a
prompt.** This file holds the exact settings for the work laptop; this job did not write them there.

## 1. The project settings — `.claude/settings.json` in the folder the work-mail skill runs in

The folder the owner opens Claude Code in for the work mail (the work-mail project on the work laptop; one folder, used for nothing
else). Commit it with that project, or write it by hand there:

```json
{
  "permissions": {
    "defaultMode": "default",
    "disableBypassPermissionsMode": "disable",
    "allow": [
      "Read",
      "Bash(zyggy outlook policy)",
      "Bash(zyggy outlook policy *)",
      "Bash(zyggy outlook count)",
      "Bash(zyggy outlook count *)",
      "Bash(zyggy outlook list)",
      "Bash(zyggy outlook list *)",
      "Bash(zyggy outlook read *)",
      "Bash(zyggy outlook brief)",
      "Bash(zyggy outlook brief *)",
      "Bash(zyggy outlook brief-done *)",
      "Bash(zyggy outlook drafts)",
      "Bash(zyggy outlook drafts *)",
      "Bash(zyggy outlook share)",
      "Bash(zyggy outlook share *)"
    ],
    "deny": [
      "Write",
      "Edit",
      "MultiEdit",
      "NotebookEdit",
      "mcp__plugin_playwright_playwright__browser_run_code_unsafe",
      "mcp__plugin_playwright_playwright__browser_evaluate"
    ]
  },
  "hooks": {
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "zyggy outlook guard" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "zyggy outlook settle" } ] }
    ]
  }
}
```

- `allow` holds only `Read` and the read verbs, each a simple command. Claude Code checks every part of a compound command on its
  own, so `zyggy outlook list && <anything>` still prompts for `<anything>`. Nothing else is allowed: Bash, PowerShell, WebFetch,
  every MCP tool and every other command prompts (`defaultMode` `default`).
- `deny` takes `Write`, `Edit`, `MultiEdit`, `NotebookEdit` and the MCP tools that run code away entirely (no prompt to click
  through). Add to `deny` any other MCP tool on the work laptop that runs code or a shell (an `*_unsafe`, `evaluate`, `exec` or
  `run_*` tool), by its full name.
- `disableBypassPermissionsMode` keeps the session out of bypass mode (`--dangerously-skip-permissions`, Shift+Tab).
- `zyggy outlook send` and `move` are not allowed: the guard answers `ask` for each, with the whole message, so they always prompt.
- `brief-done` and `share` read stdin; a here-document (`zyggy outlook share <<'EOF' … EOF`) stays one allowed command, a pipe
  (`echo … | zyggy outlook share`) prompts for the `echo`.
- The hooks block is the one in `58-s7-outlook-actions.md` §3; if the node profile already wires it in the user settings, leave it
  out here (two registrations run the guard twice, harmless but noisy).

## 2. The user settings on the work laptop — nothing that allows more

Claude Code adds the allow rules of every settings file the session reads: `%USERPROFILE%\.claude\settings.json` (or
`%CLAUDE_CONFIG_DIR%\settings.json`), the project's `.claude\settings.json` and `.claude\settings.local.json`, and the managed file
(`%ProgramFiles%\ClaudeCode\managed-settings.json`). For the guarantee, none of them may allow anything beyond `Read`, `Grep`,
`Glob` and the read verbs above, unless the project's `deny` takes it back (deny wins). Check them once:

```powershell
Get-Content "$env:USERPROFILE\.claude\settings.json"
Get-Content ".claude\settings.local.json"
```

A broad user allow rule (for example `Bash(git *)` for other projects) blocks every send and move of the work-mail session until it
is removed or moved into those other projects' own settings.

## 3. What the guard checks (zyggy, built in this job)

Before it passes a send or move to the prompt, `zyggy outlook guard` refuses it (`outlook-guard: refused: …`, one `refused` row) when:

- the hook's `permission_mode` is not `default` (`acceptEdits`, `bypassPermissions`, `dontAsk`, `plan`, or missing in an older
  client): `the session's permission mode is …`;
- a settings file above auto-allows anything beyond `Read`/`Grep`/`Glob` (bare or with a path) and
  `Bash(zyggy outlook <read verb>)`, `Bash(zyggy outlook <read verb> *)`, `Bash(zyggy outlook <read verb>:*)` (also `zyggy.exe`),
  and no deny rule written the same, or a bare deny of its tool, takes it back: `<file> auto-allows <rule> (…)`. Bare `Bash`,
  `Bash(*)`, `PowerShell`, `Write`, `Edit`, any MCP tool, `WebFetch`, `Bash(git *)`, `Bash(zyggy outlook *)` all count;
- a settings file exists but cannot be read, or is not valid JSON with string `allow`/`deny` lists: `cannot read the settings file …`.

It reads the project files of `CLAUDE_PROJECT_DIR` and of the hook's `cwd`. Read verbs are not checked: they read and print only.

Every other tool call on the work laptop goes through the screen as before, now with full paths: a path in a command or in
`file_path`/`path`/`notebook_path` is resolved (`~` and environment variables, `\\?\`/`\\.\`, `\\<host>\<drive>$\…`, `..`/`.`, then
the file system's own final path — 8.3 names, junctions, symbolic links) and refused when it lies in the outlook state folder; a
pipe or redirect into a shell or interpreter (`| bash`, `| pwsh`, `| python`, `| iex`, `bash < file`, `python3 <<EOF`,
`bash <(…)`) is refused.

## 4. What remains (the residual of Q24)

- The guard cannot see a `--allowedTools` flag on the `claude` command line, nor another hook that answers `allow`. Start the
  work-mail session as plain `claude` in that folder, and register no other allowing hook there.
- A hard link or a share pointing at the state folder, made earlier, is not followed (the file system gives the name it was opened
  by). Making one needs a command, which prompts under these settings.
- A same-user process the owner let run at a prompt can still read the approval key or drive Outlook over COM; the prompt is the
  control. (b) of Q24 — the approval out of the user's reach — is not built.
