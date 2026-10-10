---
name: central-vm-operations
description: Operational facts for running/administering the Central VM (systemd, run-command, SSH, concurrency) — read before touching Central from outside a live session.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
Central (the Linux VM) has these durable operating facts: `zyggy` has no sudo, so service restarts/installs are `<vm-admin>`-only, and every instruction handed to the owner must name which user runs it. `az vm run-command invoke --scripts` executes via `/bin/sh` (dash) fed through `base64 -d | bash`; any `claude -p` inside it needs `< /dev/null` or it swallows the rest of the script as its prompt, and output is cut at roughly 4 KB. The repo's auto-mode classifier refuses certain reads/writes on Central even with a Bash allow rule (production state files like `dream-runs.jsonl`, `~/.claude/projects/` transcripts, root-owned paths, unit installs) — hand those to the owner as `[vm/<user>]` commands rather than retrying piecemeal. Tailscale SSH runs in check mode and can hang printing a login.tailscale.com approval link; use `timeout` + `BatchMode`, or prefer `az vm run-command` for read-only checks. systemd builds a unit's `ReadWritePaths=` namespace before `ExecStartPre` runs, so a path that `ExecStartPre` itself creates needs a leading `-`. Claude Code connects HTTP MCP servers once at session start with no retry, so restarting `claude-remote` while a dependent MCP service (e.g. `zyggy-m365-mcp`) is down leaves the session without it until `/mcp` Reconnect or another restart — check the dependent service is active first. Another session can commit and push to `main` in the same checkout while you work; check `git log` before committing, and read the newest CI run that contains your own SHA.

**Why:** each of these cost a full troubleshooting turn at least once across the 02/23/28/33/35 deliverables.
**How to apply:** before giving the owner VM commands, or reasoning about a stuck Central session or service, check this list first.
