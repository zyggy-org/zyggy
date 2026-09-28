---
description: "Build a roadmap deliverable end-to-end: project-manager hand-off → technical-analyst spec → planner plan → build-feature implementation gate to gate, stopping only at 🛑 HUMAN GATEs."
argument-hint: "<deliverable number or name> [— short description], e.g. '01 — Envelope parse, sign and verify'"
---

# New Deliverable — Vertical-Slice Workflow

You are building a deliverable of **Zyggy**. The workflow is codified across four artefacts — this command only orchestrates the hand-off between them.

```
project-manager (_plans/ROADMAP.md)  →  technical-analyst (_specs/<NN>-<Deliverable>.md)  →  planner (_plans/<NN>-<Deliverable>.md)  →  build-feature skill (code)
```

**User argument**: $ARGUMENTS

## Input

The user names a deliverable (number or name) and, optionally, extra context. Resolve where it stands:

- No entry in `_plans/ROADMAP.md` (or no roadmap) → start at step 1.
- Entry exists, no `_specs/<NN>-<Deliverable>.md` → start at step 2.
- Spec exists with Open Questions → ask the user to answer them, then step 2 folds them in.
- Spec approved, no plan → step 3.
- Plan exists → step 4 at the first unchecked `[ ]`.

## Workflow

1. **Backlog** — invoke the `project-manager` subagent (via the Task tool). It reconciles `_plans/ROADMAP.md` with the repo, confirms the deliverable is schedulable (dependencies ✅), and produces the hand-off brief. **Stop at its 🛑 gate** for roadmap approval.

2. **Spec** — invoke the `technical-analyst` subagent with the hand-off brief. It writes `_specs/<NN>-<Deliverable>.md` with a Decision Table, Contracts, Failure modes and Open Questions. **Stop at its 🛑 gate**; the planner is never invoked while Open Questions remain.

3. **Plan** — invoke the `planner` subagent with the approved spec. It reads the spec, the founding-spec sections and the existing code, interviews the user on anything missing, and produces `_plans/<NN>-<Deliverable>.md` from `.claude/templates/plan-template.md`. **Stop and wait for user approval.** No code is written.

4. **Implement gate to gate** — once the plan is approved, invoke the `build-feature` skill starting at the **first unchecked `[ ]`** in `_plans/<NN>-<Deliverable>.md`. The skill executes the RGR-Proof loop from `CLAUDE.md` for each step, runs consecutive steps back-to-back (checking each step's `Done` box as its VERIFY passes), stops at the next 🛑 HUMAN GATE, and marks the gate's checkboxes `[x]` after user approval.

5. **Repeat step 4** for each subsequent gate until the plan is complete. Then tell the user the deliverable is done so the `project-manager` can flip it to ✅ (it verifies the definition of done, including the gate scenario for a phase-closing deliverable).

## Rules

- **Never skip a layer.** A deliverable always has a roadmap entry, a spec with zero Open Questions, and an approved plan before any code is written (Planning Gate in `CLAUDE.md`).
- **Never blend plan steps into one RGR cycle, and never skip a gate.** Steps run continuously between gates; stopping at every 🛑 HUMAN GATE is non-negotiable.
- **Never re-decide the workflow here.** Backlog rules live in `.claude/agents/project-manager.md`, spec rules in `.claude/agents/technical-analyst.md`, planning rules in `.claude/agents/planner.md`, execution rules in `CLAUDE.md` and `.claude/skills/build-feature/SKILL.md`.
- **Small changes (< 3 files, no contract change) do not need this command** — use the `bugfix` agent or work directly under the RGR-Proof loop.

## See Also

- `.claude/templates/spec-template.md` — format for `_specs/<NN>-<Deliverable>.md`
- `.claude/templates/plan-template.md` — format for `_plans/<NN>-<Deliverable>.md`
- `CLAUDE.md` — Planning Gate, RGR-Proof loop, Human Gates, build/test commands
