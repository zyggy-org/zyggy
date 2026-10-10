---
name: spec-rework-concurrency-check
description: Check git log for a spec file before reworking it — two same-day sessions can silently lose a decision.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
Two sessions editing the same `_specs/<NN>-*.md` on the same day once lost an owner decision (D6, consent-gated send/move/delete in spec 23) because the second session started from a stale copy.

**Why:** specs are edited directly by agents across multiple turns and sessions in the same day during an active deliverable, with no lock between them.
**How to apply:** before reworking or reopening an existing spec file, run `git log -- _specs/<NN>*` first to check for commits from another session since you last read it.
