---
name: write-spec
description: "Use when a new Zyggy deliverable, a risk-area change, or work that will span sessions needs a spec before any code (the Spec Gate in CLAUDE.md). Reads the founding-spec sections and the reference feature, challenges each founding-spec item, interviews the owner, and writes _specs/<NN>-<short-name>.md as a brief: goal, constraints, behavior, decisions, acceptance criteria with their checks, and an ordered list of Fake/Wire slices. Writes no code and no plan file."
argument-hint: "The feature or change, e.g. 'discovery registry — nodes publish what they can run'"
---

# Write Spec

Produce `_specs/<NN>-<short-name>.md` and stop for the owner's approval. The spec is the only planning document (`CLAUDE.md` § The spec is the brief), so it must be complete enough that a fresh session can build from it without this conversation.

This skill runs in the main conversation because it needs the owner's answers. Change no file other than the spec. Never edit the founding spec here; a conflict with it is an Open question (the main session may paste owner-approved wording afterwards, `CLAUDE.md` § Founding spec).

## Before writing

1. **Look for an existing spec** with the same subject under `_specs/`. If one exists, read it and its Status, and ask whether the request updates it or is a new spec. Do not overwrite work in progress. When converting an older spec to this format, verify each statement it makes about the code before keeping it.
2. **Read the roadmap entry** in `_plans/ROADMAP.md` (number, goal, dependencies, definition of done, risks) or the project-manager's hand-off brief.
3. **Read the founding-spec sections** the entry names, plus always §9 design rules, §13 decisions and §14 constraints. §4 is normative: `PROTOCOL.md` in `zyggy-core` must match it, so any envelope or state-machine change is an Open question.
4. **Read the reference feature** (`CLAUDE.md` § Project settings) across the projects the request touches: Core seam, Node service, CLI verb, integration test, gate. Use another feature when it is clearly closer, and say which and why.
5. **Read what the request depends on**: the seams and options it extends, the bus paths (`BusPaths`), the CLI verbs, the Hub tools, the package documentation when a library capability is added.
6. **Identify risk areas** (`CLAUDE.md` § Project settings). They become ⚠️ constraints and ⚠️ slices.

## Challenge before specifying

The founding spec was written before the code. For every item it assigns to this deliverable, decide **keep / reshape / library / defer** with a one-line why; that table goes under Decisions.

- Does it serve this deliverable's observable round trip or a §12 gate? "The founding spec mentions it" is not value on its own.
- Would you design it this way today? Prefer `Process` via `IProcessRunner`, `HttpClient`, `System.Text.Json`, `TimeProvider`, `IOptions<T>`, `IHostedService` over hand-rolled code; no abstraction with one implementation unless it is one of the five §9 seams.
- A library must reduce the code Zyggy owns: check licence (MIT/Apache-2.0 preferred, copyleft is an Open question), maintenance, footprint, .NET 10 and single-file self-contained publish. Cite what you checked.
- Never soften §8 (signature verification before any model call, no `--dangerously-skip-permissions`, work-laptop data only as metadata, no secrets in files). Never silently reopen a §13 decision; raise it as an Open question with evidence.

## Interview

Ask only what the request, the founding spec and the code do not already answer, in one batch. Typical gaps:

- Which machine role (owner, Central, a Node, the work node) can do what afterwards that it cannot do now?
- Envelope fields, bus paths, state moves, reason codes added or interpreted.
- CLI verbs and options, exit codes, output; Hub tools and their input/output.
- Config keys: where (`node.json`, options), default, override.
- Failure modes and their observable outcome (report `reason`, rejection move, log line); the runbook entry each one needs.
- Owner-run checks on the real machines.
- Edge cases and what is explicitly out of scope. How each requirement is checked.

Do not fill a gap with an assumption. An unanswered question goes under Open questions, with what you found, why it matters, the options and your recommendation. The spec is not ready for approval while one remains that changes behavior.

## Writing

Use `.claude/templates/spec-template.md`. Remove the sections the feature does not need.

- **Acceptance criteria** are numbered and each names its check: a unit or integration test (say which level), a gate scenario, a `zyggy` command against a local bare repo and its output, or an owner-run check on a real machine. "Code review" is not a check. Ask of each: would it fail if the behavior were broken? §12 definition-of-done items and §8/§14 rules that apply are ACs, not remarks.
- **Slices** follow `CLAUDE.md` § Vertical slices: one row each, named by what the owner or system can do afterwards, Fake before its Wire, ⚠️ on slices that touch a risk area, the ACs each slice satisfies, and its one review check (a test filter, a command or a gate). A phase-closing deliverable ends with the gate slice (`tests/Zyggy.Integration/Gates/P<n>_<Name>.cs`).
- **Leave out** file lists, class and test method names, line numbers and code. State a contract only when it is the requirement: an envelope field, a reason code, a CLI verb and options, an MCP tool signature, a config key, a bus path.
- **Diagrams.** Add Mermaid diagrams (` ```mermaid ` blocks, rendered by GitHub and VS Code) wherever a picture explains faster than prose; at least: a component diagram of the projects, seams, machines and bus paths the feature touches (new parts highlighted), and a sequence diagram of the round trip across owner, Central, the bus and the Node. Add a state diagram for any state move, a flowchart for a decision with branches (e.g. a refusal path ending in a `reason`), and the slice map (Fake → Wire per behavior). Diagrams name contracts and roles, not classes or files; keep the text authoritative — a diagram never says something the Behavior section does not.
- **State facts you verified.** If a statement about the code, Claude Code CLI behavior or an environment matters to the build, say how you know it (file read, command run, docs checked) or put it under Open questions.
- **Public repo**: placeholders only (`acme`, `home-laptop`, `/home/alice/src`), no employer name, no real ids.

## Before presenting

Check the spec against these points and fix what fails:

1. Every acceptance criterion has a check someone can run, and that would fail if the behavior were broken.
2. Every slice is vertical, has exactly one review check, and depends on no later slice. Work without its own review check (a doc edit, a runbook row, a rename) is folded into the slice whose behavior it supports.
3. Every risk area the feature touches is marked ⚠️ in Constraints and on its slice.
4. Every founding-spec item in scope appears under Decisions; nothing deferred appears in a slice.
5. No file list, test name or line number crept in.
6. The component and sequence diagrams exist, render (valid Mermaid), and agree with Behavior and Slices.
7. Status says "spec written, not approved" with today's date.

Then present it as a visual approval report (`CLAUDE.md` § Visual approval report): a page with the spec's diagrams rendered, the slice map, the risk areas and the Open questions with your recommendation for each; in the chat a few plain lines, an ASCII sketch of the round trip and the link. Ask the owner to approve or change the spec. When the owner approves, add "<date>: approved by the owner" to Status, create `feature/<NN>-<short-name>` from an up-to-date `main`, and commit the spec as its first commit so the approval visibly precedes the code. The build starts in a fresh session with the `build-feature` skill.
