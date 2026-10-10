# gen/1 — CLAUDE.md corrected to match built code and established spec-amendment practice

Score: 2/2 (prev —)

Change 1: Fix the stale "Zyggy.Core stays empty" status line
  Files: CLAUDE.md
  Why: Verified directly against the worktree (`src/Zyggy.Core` has Brief/Dream/Envelope/Git/LinkedIn/M365/Media/Memory/Models/Processes/Runs/Secrets/Tenancy/Verbs; `src/Zyggy.Node` and `src/Zyggy.Hub` are still near-empty hosts) and matches evolution/journal/2026-10-10-1030.md ("CLAUDE.md describes the target architecture ... while the built code is mostly Central-side verbs ... a reader can easily mistake the plan for the code"; wished for "a short 'what is built today' section").
  Risk: Low — one factual sentence corrected, no rule changed; could go stale again as the roadmap advances.

Change 2: State the established practice for applying owner-approved founding-spec amendments
  Files: CLAUDE.md
  Why: The literal rule ("Only the user edits it; agents raise conflicts as Open Questions") contradicted actual practice on at least five sessions — evolution/journal/2026-09-28-2021.md turn 3 ("wished I had a repo rule stating who applies founding-spec amendments once the owner has decided them"), evolution/journal/2026-09-30-0815.md turn 6 ("CLAUDE.md says only the user edits the founding spec, while the roadmap's precedent has the orchestrator apply user-decided amendments; followed the precedent"), evolution/journal/2026-10-04-0832.md ("the CLAUDE.md 'only the user edits the founding spec' vs the established 'orchestrator applies owner-approved amendments' practice ... wished I had a stated rule for who pastes owner-accepted founding-spec wording"). Each time the agent had to re-justify departing from the written rule.
  Risk: Low-medium — slightly loosens a rule the owner wrote; scoped tightly to text the owner has already explicitly approved, and keeps technical-analyst/planner excluded.

Change 3: Fix the stale IPolicySource reference
  Files: CLAUDE.md
  Why: evolution/journal/2026-09-29-1600.md "Proposed genome changes (not made)" explicitly flags that CLAUDE.md §9 still says `IPolicySource` is "signed policy.yaml" while the spec (per the owner's MVP-scope decision the same day) now says `node.json` in v1; memory/short-term.md 2026-09-29 bullet records the same gap.
  Risk: Low — one phrase corrected to match an owner decision already on record; no behavior change.

Remembered:
- memory/long-term/bash-heredoc-limitations.md — new: multi-line Bash heredocs in this harness corrupt; use Write/Edit instead (recurring across 2026-09-28, 2026-10-07, 2026-10-09)
- memory/long-term/central-vm-operations.md — new: durable Central VM/systemd/run-command/SSH/concurrency facts (02/23/28/33/35)
- memory/long-term/ci-test-tooling-gotchas.md — new: CI/build/test-environment quirks (trx naming, mawk, podman Linux-proof, env vars in binary tests)
- memory/long-term/repo-public-hygiene.md — new: zyggy/zyggy-core are public — no real identifiers, secrets, or runbooks
- memory/long-term/central-integrations-facts.md — new: GitHub/Graph/M365/LinkedIn platform facts for Central (31/23/28/36)
- memory/long-term/evolve-propose-plugin-pin.md — new: /evolve:propose plugin-cache-version and safecrlf workaround
- memory/long-term/mvp-scope-decision.md — new: the 2026-09-29 MVP-scope keep/cut rule for specs and plans
- memory/long-term/spec-rework-concurrency-check.md — new: check git log on a spec file before reworking it

Retired:
- nothing

Declined to change:
- Adding a "where do instance docs live" line to CLAUDE.md (evolution/journal/2026-10-08.md, single citation) — folded into memory/long-term/repo-public-hygiene.md instead; declined as a genome edit to stay within the 3-change budget given the stronger, more-repeated evidence for Changes 1–3.
- Retiring any skill or sub-agent for disuse — the window is 12 days since gen/0 with active use of nearly every skill/agent (build-feature ×15, project-manager ×22, technical-analyst ×16, planner ×13, new-feature ×6, general-purpose ×17); no 30-day-idle candidate exists yet.

Recalled:
- nothing

Forgotten:
- nothing
