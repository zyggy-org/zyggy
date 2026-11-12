---
name: new-feature
description: "Take a Zyggy roadmap deliverable from backlog to a deployed release: project-manager hand-off, the write-spec skill for _specs/<NN>-<short-name>.md, the build-feature skill slice by slice (commit on the owner's approval), then the ship skill after the final approval."
argument-hint: "Feature name plus any initial context, e.g. 'discovery registry — nodes publish what they can run'"
---

# New Feature

A roadmap deliverable goes from a backlog entry to a spec to working slices. The workflow is defined in `CLAUDE.md` § Delivery workflow; this file only orders the hand-offs. In conversation, name the feature by what it does, not by its number.

## Input

A feature name and, optionally, context. Resolve where it stands:

- No entry in `_plans/ROADMAP.md` → step 1.
- Entry exists, no `_specs/<NN>-<short-name>.md` → step 2.
- Spec exists, not approved or with Open questions → step 2 (update it).
- Spec approved → step 3; the next slice is the first one in its Slices without a "done" line in Status.
- Old-style spec with a `_plans/<NN>-*.md` plan from before 2026-11-12 → finish it with that plan as history, or convert the spec with `write-spec` first if work remains; ask the owner which.

## Workflow

Before step 1, say in plain words what the next feature is and confirm with the owner before starting the chain.

1. **Backlog** — invoke the `project-manager` subagent. It reconciles `_plans/ROADMAP.md` with the repo, confirms the deliverable is schedulable, and returns a hand-off brief (founding-spec sections, design rules, definition of done, ⚠️ risk areas). Stop for the owner's roadmap approval when the roadmap changed.
2. **Spec** — use the `write-spec` skill with the hand-off brief: interview the owner, read the reference feature and the founding-spec sections, write `_specs/<NN>-<short-name>.md`, and stop for approval. No code, no plan file.
3. **Build** — once the spec is approved, the owner starts a fresh session (`/clear`) and the `build-feature` skill builds the next slice: test first, proven by the Verification Commands and the slice's acceptance criteria, one Status line, then it stops with the slice's review check and a proposed commit message.
4. **Approve and repeat** — the owner tries the review check, reviews the diff and approves; Claude commits; then the next slice. Other stops are in `CLAUDE.md` § Human Review Gates: before a ⚠️ slice and when the spec proves wrong.
5. **Review** — after the last slice, a fresh reviewer agent (`general-purpose`, `model: sonnet`) compares the full diff with the spec and reports each acceptance criterion as met or not met. The owner reads that report and approves the release.
6. **Release** — on that approval, the `ship` skill ships it without further questions: merge, tag, GitHub release, instance pin, Central and laptop upgraded, live checks, roadmap entry to Done. Only a red step or a step Claude cannot do comes back to the owner.

## See also

- `.claude/templates/spec-template.md` — format of a spec
- `CLAUDE.md` — Project settings, Spec Gate, Workflow rules, Human Review Gates (Vertical slices: `write-spec` skill)
