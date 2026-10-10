---
name: evolve-propose-plugin-pin
description: /evolve:propose may load a stale cached plugin version; run the matching Invoke-Evolver.ps1 directly if it errors on MEMORY.md.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
The `evolve` plugin is cached per-version under `~/.claude/plugins/cache/claude-evolve-plugin/evolve/<version>`; `/evolve:propose` can load an older cached version (0.1.0, expects a root `MEMORY.md`) than the one that actually initialised this project (0.2.0, `memory/short-term.md` + `memory/long-term/`), which fails immediately on `Get-Content MEMORY.md`. The runner also separately failed twice on a false "changed path" violation caused by git printing a CRLF-normalization warning on stderr during its diff check; `git config core.safecrlf false` (local, reversible) worked around it.

**Why:** hit on 2026-09-29 and again on 2026-10-09, each costing a full proposal run.
**How to apply:** if `/evolve:propose` errors on a missing `MEMORY.md`, or refuses with no real path change, check the plugin cache version and the git `safecrlf` setting before assuming the repo state itself is wrong.
