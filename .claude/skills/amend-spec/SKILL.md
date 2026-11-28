---
name: amend-spec
description: "Use when an approved spec has to change: the build shows it is wrong or incomplete, or the user changes their mind about behavior. Updates behavior, criteria, the diagram, the slices and the Scaffold together, and stops for the user's approval before any more code is written."
argument-hint: "The spec and what has to change, e.g. 'Subscriptions - a subscription can be paused'"
---

# Amend Spec

Change an approved spec without letting it drift from the code or from itself. The spec describes the system as it will be, not the history of how it got there (`.claude/rules/spec-driven.md`). The spec's Scaffold changes with the rest of the spec, and the amended spec gets one approval. Change no file other than the spec, and change it on the spec's branch, named in its Status.

## When this applies

- The change alters behavior, a contract, an acceptance criterion, the Scaffold (a file, a registration, the wiring) or the slices: use this skill.
- The change is a decision the spec leaves to the builder (a name, a file, an internal structure): no amendment; the build's record names it.
- The spec is shipped: do not amend it. New work on a shipped feature starts with the `investigate` skill and a new spec.

## What to do

1. **State the change** in the conversation: what the spec says now, what it has to say, and what made that clear (the finding from the code, or the user's request). When the cause is a finding, say how it was verified.
2. **Work out what it touches**, and tell the user before editing:
   - the Behavior sections and acceptance criteria that change. Keep the ids of existing criteria; a new criterion gets a new id, a dropped one is removed and its id is not reused;
   - the What happens diagram;
   - the Scaffold: the rows of every slice whose files to create, modify or delete, DI registrations or wiring change;
   - the slices. A slice in state "todo" may be changed, split, added or removed. A slice that is "built" or "accepted" is not rewritten: when its behavior changes, add a new slice that changes it, with its own outer test;
   - tests that already carry the id of a criterion that changes.
3. **Ask** what only the user can decide, in the way the `write-spec` interview does. Do not fill a gap with an assumption.
4. **Edit the spec, its Scaffold included,** so that it reads as if it had always said this: no "changed from" notes in Behavior or in the Scaffold. Run the checks under "Before presenting" in the `write-spec` skill; start the `spec-critic` agent only when the user asks for it, for one pass; when it ends with no report, continue and say so in Status.
5. **Add to Status**: "<date>: amended: <one line on what changed and why>. Not approved."
6. **Stop** for the user's approval: one approval of the amended spec, Scaffold included. On approval, add "<date>: amendment approved by the user". No code that depends on the amendment is written before that.

When the amendment arrives in the middle of a slice, say what happens to the uncommitted work: it still fits and the slice goes on, or it has to be changed or discarded. The user decides; do not discard work unasked.

## Guidelines

A guideline is a file under the project's `.claude/rules/` whose first line after the frontmatter is `<!-- guideline: <role> -->`, the role being testing, architecture or coding; find them by that line, not by file name. When the amendment changes a diagram, Behavior's components or a check, read the **architecture** and **testing** guidelines at step 4 and apply them as the write-spec skill's Guidelines section says: components in the architecture guideline's layers and hosts, a new one outside them an open question; each changed check at the level their table of test levels gives. When no file carries a role, skip what this paragraph says for it without remark.
