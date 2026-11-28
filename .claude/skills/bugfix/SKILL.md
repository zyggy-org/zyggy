---
name: bugfix
description: "Fix a bug with a regression test first: find the root cause, write a failing test that reproduces it, then make the minimal fix. For fixes of at most two production files outside risk areas; larger ones need a spec. Use when the user reports a bug, an error or wrong behavior."
argument-hint: "The bug: error message, wrong behavior, failing scenario, or a file and line"
---

# Bugfix: regression test first

Every bugfix gets a regression test first, no exceptions. This skill runs in the main conversation, so the user's description of the bug stays available and you can ask about it. The commands are in `CLAUDE.md` § Project settings.

## Guidelines

A guideline is a file under the project's `.claude/rules/` whose first line after the frontmatter is `<!-- guideline: <role> -->`, the role being testing, architecture or coding; find them by that line, not by file name. In step 1, read the **architecture** guideline to name the layer of the root cause: a fix that would cross one of its dependency rules is larger than a bugfix and needs a spec. In steps 2 and 3, follow the **testing** guidelines: their rule T-1 for the regression test, T-3 and T-9 for the fix (no weakened assertion, no skipped test), and T-11 for what the report says. When no file carries a role, skip what this paragraph says for it without remark.

## 1. Investigate

- On a protected branch, create a branch for the fix first (`.claude/rules/spec-driven.md` § One branch per spec), named like a spec branch; ask first only when the working tree holds changes.
- Reproduce the bug when it can be reproduced: run the failing scenario and keep the output.
- Read the reported code and what surrounds it, and identify the root cause, not the place where the symptom shows. When the expected behavior is unclear, ask the user; do not guess what "correct" means.
- Confirm that the fix changes at most two production files and touches no risk area (`CLAUDE.md` § Project settings). If it is larger or touches one, stop: the work needs a spec first (`investigate`, then `write-spec`).

## 2. RED: the regression test

- Write a test that reproduces the bug, at the level where the bug shows: an integration test when the fault is in how layers work together, a unit test when it is in one piece of logic.
- Place it where the existing tests of that code live, following their naming.
- Run it filtered and confirm it fails for the reason of the bug, not because of its own setup.

Write no production code yet.

## 3. GREEN: the fix

- Make the minimal change that fixes the root cause. Do not refactor surrounding code.
- Run the test again and confirm it passes.

## 4. Verify

Run the Verification Commands. All pass, zero warnings.

## 5. Report

- **Root cause**: one sentence.
- **Regression test**: its path and name, and what it failed on.
- **Fix**: the path and what changed.
- **Verification**: the result of each command.

When the bug is in a feature with a spec under `_specs/`, check whether the fix changes behavior the spec describes. If it does and the spec is not shipped, say so: the spec has to change through `amend-spec`.

Do not commit or push unless the user asks for it; the user reviews the diff and commits.
