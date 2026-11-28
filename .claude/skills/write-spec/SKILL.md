---
name: write-spec
description: "Use after the investigate skill, when a new feature, a risk-area change or work that spans sessions needs a spec before any code. Interviews the user from the investigation's questions, then completes _specs/<FeatureName>/spec.md: behavior, the What happens diagram, acceptance criteria with their checks, ordered vertical slices and the Scaffold. Writes no code."
argument-hint: "The feature, e.g. 'Subscriptions'"
---

# Write Spec

Complete `_specs/<FeatureName>/spec.md`, its Scaffold included, and stop for the user's one approval of the spec. The spec is the planning document (`.claude/rules/spec-driven.md`), so it has to be complete enough that a fresh session can build from it without this conversation.

This skill runs in the main conversation because it needs the user's answers. Change no file other than the spec.

## Before the interview

The spec file must already hold Findings and Open questions from the `investigate` skill. If it does not, run `investigate` first: an interview without findings asks the user what the code could have answered.

## Interview

Ask the user until you could write the spec without a single assumption.

- **Work in rounds.** Ask the questions whose answers decide the shape of the work first (who it is for, what they can do afterwards, which option from the Findings). Let the answers decide the next round. Stop when a new round would only confirm what you know.
- **Ask what only the user knows.** Never ask what the code or the Findings already answer. When the code suggests an answer, propose it and ask for a yes or a correction.
- **Offer choices** where the options are few and known, with the one you recommend first and the reason. Use the question tool for those; use plain questions for open ones.
- **Cover, as far as the feature needs them**: the user and the goal; data (fields, types, required, constraints, relations); the API (endpoints, auth, errors); pages (what the user sees at each step and where they go next); background work and messages; edge cases; what is out of scope; how each requirement will be checked; and what the user wants to see working first.
- **Record as you go.** After each round, move the answers into the spec and remove the questions they close.

Do not fill a gap with an assumption. A question the user cannot answer yet stays under Open questions, and the spec is not ready for approval while one remains that changes behavior.

## Writing

Use the sections of `.claude/templates/spec-template.md`; remove the ones the feature does not need. The What happens diagram, Slices and the Scaffold always stay.

- **Behavior** in the detail the feature needs: rules with their messages, data, API, pages, edge cases.
- **The What happens diagram**, in Mermaid, at component level, never at file or class level: the flow the user or the calling system goes through, with the outcome at each step. How it is built is in the Scaffold, not in a diagram. Mermaid labels (messages, notes, node and edge labels) never contain `;`, `#` or unbalanced quotes: a `;` ends a statement and the diagram does not render. Write the words instead (for example "semicolon") or Mermaid entity codes (`#59;` for `;`, `#quot;` for `"`), and keep labels short.
- **Security rules in full.** Never weaken a security rule for readability or convenience: state the full standard rule and handle readability another way (for example a text column format, never a skipped case).
- **Acceptance criteria** are numbered and each names its check: a test at a stated level, an API call and its response, or a page action and its result. "Code review" is not a check. Ask of each check: would it fail if the behavior were broken? The tests that prove a criterion will carry its id, so keep the ids stable once the spec is approved.
  - The outer test of a security criterion covers every case the rule names, including a dangerous variant of each (for CSV injection: a value starting with `=`, `+`, `-`, `@`, tab and CR, and a malicious `+`/`-` value such as `-2+3+cmd|...`).
- **Slices**: one row each, named by what the user or the system can do afterwards, ⚠️ on those that touch a risk area, with the acceptance criteria the slice satisfies, its two checks and the state "todo":
  - the **outer test**, written as a scenario with its expected outcome. Every slice's outer test is an integration test, at least one per slice, at the Integration test level `CLAUDE.md` § Project settings defines (for a browser-only feature a Presentation integration test on the real DI container, for an endpoint an API integration test against the real backend host); for a stub slice the same test with the stub as the faked backend client. Mocked view models do not count;
  - the **human check**, written as steps the user can carry out, or "none" with the reason. A stub slice always has one: being tried by the user is its purpose.
- **Leave out**, outside the Scaffold, file lists, class and test method names, line numbers and code. State a contract only when the contract is the requirement.

### The Scaffold

Write the spec's Scaffold section once the slices are cut: per slice, a table of the files to create, modify or delete, one line each saying what changes and why, plus one line for DI registrations and wiring. Check each row in the code: a file to modify or delete exists, a new one is marked create, and every new service or dependency has its registration on the wiring line. No steps and no code: the build works from each slice's rows. A file that no slice's rows name, and that a slice needs, is a gap in the Scaffold.

### Cutting the slices

- Slice by behavior, never by layer: "create entity, add repository, build controller, create page" leaves nothing observable until the last step.
- Cut each UI behavior into a **stub** slice (the page, form or component with its real interaction on fake data, for the user to try and judge before any backend exists) and the **wire** slice that follows it (API, logic and persistence from top to bottom; the stub is removed). Finish the stub and the wire of one behavior before the next.
- Do not cut a UI change that leaves the user nothing to judge (a column, a label, a field on an existing form): make it one slice with an integration test, and write "single slice: nothing to judge on a stub" in its row. When in doubt, ask the user whether they want to see it first.
- A slice is one increment the user can test: a functionality or an API call. Its size follows from that, not from a count of criteria; `build-slice` builds it from the slice's Scaffold rows.
- A security or personal-data criterion goes in the same slice as the functionality it protects (for example the CSV formula guard in the slice that produces the file).
- A slice that needs two scenarios with different outcomes for the user is two slices. A slice too large to prove in one session is split into smaller behaviors.
- Work that no outer test can prove (a doc edit, a rename, a helper) is not a slice: fold it into the slice whose behavior it supports.
- Order the slices so that the first one gives the user something to see as early as possible. No slice depends on a later one.
- In a project with no working code yet, slice 1 is the walking skeleton: the thinnest path from the entry point to persistence, together with the test harness that proves it.

## Guidelines

A guideline is a file under the project's `.claude/rules/` whose first line after the frontmatter is `<!-- guideline: <role> -->`, the role being testing, architecture or coding; find them by that line, not by file name. Read the **architecture** guidelines before writing Behavior, the What happens diagram and the Scaffold: the diagram's components and the Scaffold's files sit in the guideline's layers and hosts, its DI registrations and wiring follow the guideline, and a new component outside them is an open question for the user; pages, API and messages follow the guideline's contracts (route pattern, mapping of business errors to HTTP, event and message shapes). Read the **testing** guidelines before writing Acceptance criteria and Slices: each check names its level in their terms (integration through the real entry point, UI on the stub, unit); a criterion whose check would be a mocked unit test of wiring is rewritten; each outer test is an integration test, stated at the integration level their table of test levels gives for this project type. When no file carries a role, skip what this paragraph says for it without remark.

## Before presenting

1. Check the spec yourself and fix what fails:
   - every acceptance criterion has a check that someone can run and that would fail if the behavior were broken;
   - every acceptance criterion belongs to a slice, and every slice has its rows in the Scaffold;
   - every slice is one increment the user can test, a functionality or an API call; its size follows from that, not from a count of criteria;
   - every security or personal-data criterion goes in the same slice as the functionality it protects;
   - every slice has at least one integration test that tests its behavior: every slice's outer test is an integration test, at least one per slice; a security rule is stated in full and its outer test covers every case it names;
   - the What happens diagram agrees with Behavior;
   - every diagram renders (labels follow the rule above);
   - every risk area the feature touches is marked ⚠️ in Constraints and on its slice;
   - outside the Scaffold, no file list, test name or line number crept in;
   - every new service or dependency has its DI registration in the Scaffold table, on the wiring line of the slice that adds it;
   - every type, route, setting or file named in the Scaffold table exists in the code or is marked new (create); check it in the code, not from memory;
   - culture and locale (number, date, separator, encoding) are stated wherever output is formatted, in Behavior and on the Scaffold table's row of the file that formats it.
2. The critic is off by default. Start the `spec-critic` agent only when the user asks for it, with the path of the spec and nothing from this conversation, for one pass and never a second. It answers with one line, the verdict and the path of its report, `artifacts/<FeatureName>-critic-report.md`; read that file (it is ignored by git). Fix what it reports. Where you disagree with a finding, keep it for the summary; where a finding needs an answer only the user has, add it to Open questions. When it ends with no report, continue without it and say so in Status.
3. Add to Status: "<date>: spec written. Not approved.", with how the critic went: "critic not run", "critic run" or "critic asked for, no report".

Then present the spec and summarise in a few lines: the goal, the slices with what the user will see after each, the files and registrations the Scaffold adds or changes, the risk areas, the open questions, and, when the critic ran, any finding you did not follow, with the reason. Tell the user to open the spec in a Markdown preview to see the diagram, and ask for one approval of the spec, Scaffold included, or for changes.

When the user approves, add "<date>: spec approved by the user" to Status. Then offer to commit the approved spec as the first commit on the spec's branch, so a draft pull request can be opened; commit only when the user says so. The build starts in a fresh session with the `build-slice` skill.
