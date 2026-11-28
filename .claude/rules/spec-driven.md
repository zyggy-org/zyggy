<!-- spec-driven-kit 3.0.0 -->
# Spec-driven development

This file defines how work is done in this repository: when a spec is needed, what a spec and a slice are, and where Claude stops for the user. The procedures are in the skills and are not repeated here. The project's own values are in `CLAUDE.md` § Project settings.

The spec, with its Scaffold, is the planning document, approved by the user in one approval. Plan mode is not used, and no plan file is written. How a slice is built is `build-slice`'s.

**Precedence.** In this repository this file outranks any plugin or user-level skill that prescribes another planning workflow (brainstorm-then-plan, plan files, building from a task list of its own). Use such a skill only for what this workflow does not cover, such as debugging technique.

## The phases

| Phase | What happens | Skill or agent | Ends with |
|---|---|---|---|
| 1. Investigate | Read the code and what the request depends on; record verified findings and questions | `investigate` | Findings and questions in the spec file |
| 2. Interview | Ask the user, round by round, until the spec could be written without assumptions | `write-spec` | No open question that changes behavior |
| 3. Specify | Behavior, acceptance criteria, the What happens diagram, vertical slices, and the Scaffold (per slice the files to create, modify or delete, and the DI registrations and wiring); checked against the code; a context-free critic reads the spec only when the user asks, one pass | `write-spec`, `spec-critic` only when the user asks, one pass | The user approves the spec, in one approval |
| 4. Build | One slice per fresh session, on the user's go, test-driven, ending on the slice gate green and the record as the final answer | `build-slice` | The slice gate green |
| 5. Check | The user checks the slice from the record and accepts it; after the slice gate no agent reviews the slice | the user | The user accepts the slice |
| 6. Review | The final review, once after the last slice: the feature gate green (every Verification Command, every integration test), then the code analysis the project declares in Project settings, once per feature, never per slice (Analysis and Fix analysis, for .NET the built-in analyzers in the build and `dotnet format --verify-no-changes`, no server: its findings on the feature's changed code fixed, the feature gate again, the ones it cannot fix in the report for the user; skipped, and said so, when none is declared), then a context-free reviewer, `spec-reviewer`, checks every acceptance criterion against the full diff; when a slice is ⚠️, `security-review` runs once with it over the whole diff, never per slice; an end-to-end browser test is optional, at most once per feature | `spec-reviewer`, with `security-review` once when a slice is ⚠️ | The user pushes |

`new-feature` runs the phases in order. `amend-spec` changes an approved spec. `bugfix` handles small fixes.

## Spec Gate

Decide before the first change what the work needs.

| Situation | What to do |
|---|---|
| New feature or new behavior | Spec, approved by the user, before any code |
| Change in a risk area | Spec, approved by the user, before any code |
| Work that will not fit in one session | Spec, approved by the user, before any code |
| Contained change that fits in one session | No file. Say in the conversation what will be true afterwards and which test proves it, then build test first |
| Bugfix in one or two files | No spec; regression test first (`bugfix` skill) |
| Config correction, simple refactor | No spec |

## The spec

A spec lives in `_specs/<FeatureName>/spec.md` and follows `.claude/templates/spec-template.md`. It says what must be true when the work is done, how that is checked, and what it looks like.

- **Leaves out**, outside the Scaffold, file lists, class and test method names, line numbers and code, so it cannot go stale. Exception: a contract that is itself the requirement (a route, a DTO shape, a message name).
- **Has a Scaffold**, its last part: per slice a table of the files to create, modify or delete, one line each saying what changes and why, plus one line for DI registrations and wiring; checked in the code, no steps, no code. The spec, Scaffold included, is approved in one approval.
- **Stays true during the build.** When a decision changes behavior, a file of the Scaffold or its wiring, the spec and its Scaffold are changed first, through `amend-spec`, with one approval of the user.
- **Is history once shipped.** After the final review the spec is marked shipped. From then on the code is the truth; a later investigation verifies a shipped spec against the code before relying on it.

## One branch per spec

Every spec has a branch of its own, so its work can be followed while it is in progress and lands as one pull request.

- **Claude creates it** when a new spec starts, before the spec file is written: named as `CLAUDE.md` § Project settings says (Spec branch), from the up-to-date base branch (`git fetch`, then `git switch -c <branch> origin/<base>`; from the local base when the repository has no remote). The first line of the spec's Status records the branch and its base.
- **Creating, switching and fetching need no approval; committing does** (see Commit and push). When the working tree holds changes as the branch is created, ask first: they would move to the new branch.
- **Everything of the spec lives there**: the spec, its amendments and its slices. No other spec's work goes on it, and its slices are never built on another branch. A hook refuses spec files on a protected branch.
- **The approved spec is the first commit** on the branch (by the user, or by Claude when asked), so a draft pull request can be opened while the slices are built. After the final review the user pushes and completes the pull request.
- A fix without a spec (`bugfix`) also gets a branch of its own, created the same way.

## Slices

A slice is one increment the user can test: a functionality or an API call, across every layer it needs, named after what the user or the system can do afterwards. Its size follows from that, not from a count of criteria. Slice by behavior, never by layer. No slice depends on a later one.

- Every slice names an **outer test** and a **human check**, or the reason there is none. Every slice's outer test is an integration test, at least one per slice, as `CLAUDE.md` § Project settings § Test levels defines it (for a browser-only feature a Presentation integration test that enters through the page or component the user uses, not through the view model, rendered on the real DI container with the real view model and real services, faking only the browser edge, the clock, auth and the backend client, so the page's own wiring, such as grid rows, sort and paging, is under test; for an endpoint an API integration test against the real backend host; for a stub slice the same test with the stub as the faked backend client, not a separate bUnit test). Mocked view models do not count as integration. An end-to-end browser test is optional, at most once per feature, in the final review.
- A security or personal-data criterion goes in the same slice as the functionality it protects. A security rule is stated in full, never weakened for readability or convenience, and its outer test covers every case the rule names.
- Every slice, ⚠️ or not, starts only on the user's go for that slice; a build session never starts the next slice by itself.
- A UI behavior is cut into a **stub** slice (the page on fake data, for the user to try and judge) directly followed by its **wire** slice (the real backend from top to bottom; the stub is removed). Exception: a UI change with nothing for the user to judge (a column, a label, a field on an existing form) is one slice with an integration test, and the spec says so.
- Every test that proves an acceptance criterion carries the criterion's id (`AC-3`) in its name or in a trait, tag or comment, so the check can be found without guessing.

## Gates

- Two gates, both `node .claude/hooks/verify.mjs`. The slice gate (`--slice <IntegrationTestClass>`, the slice's integration test class) ends every slice: the build, all unit tests, the UI tests, only that slice's integration tests; a filter that matches zero tests is red (zero tests means the slice is not done). The slice gate runs no format check: the format check (`dotnet format --verify-no-changes`) runs once per feature, in the final review, like the analyzers. With the dotnet-shared pack the slice gate is followed by the mutation check (`node scripts/mutation-check.mjs --slice <n> --base <commit>`, Stryker.NET on the slice's changed production files), the proof that the slice's tests can fail: red below the project's threshold or with a mutant without coverage; its evidence, `_specs/<FeatureName>/mutation-<n>.md` with each survivor classified, is committed with the slice. The full integration suite runs only in the final review. The feature gate (`--feature`, or no argument) runs every Verification Command, every integration test and the format check included, once at the end of the feature in the final review, before the code analysis and `spec-reviewer`.
- The builder does not grade its own work. After the slice gate no agent reviews the slice: the slice gate and the user's check verify it, and `spec-reviewer` reviews the whole feature once, in the final review, and is not given the builder's account. A slice review agent left in a project by an older kit is optional and runs only when the user asks for it, never by the build on its own.

## Commit and push

Claude commits or pushes only when the user explicitly asks for it in the conversation. A request to commit applies to that commit only. Without one, the verified work stays uncommitted in the working tree, and Claude says so. The user checks each slice and accepts it; committing it is the user's choice. A build session that finds the working tree dirty asks before it builds. Creating or switching to a spec's branch and `git fetch` are not commits: Claude does them without asking. Merge, rebase, reset and deleting a branch only when the user asks.

The guard hook enforces the part of this that a hook can: in an attended session `git commit` and `git push` are put to the user for confirmation; in an unattended job (the environment variable `ZYGGY_JOB` is set, so nobody is there to answer) they pass to the job's own permissions, and the job's instructions stand for the user's request. On a protected branch (the project's list, and always `main`, `master`, `dev` and `release/*`) both are refused, attended or not. A force-push and a tag deletion are refused always.

## Where Claude stops

- after the investigation, when a finding makes the request impossible or changes its size;
- after writing a spec, its Scaffold included, before any code;
- before every slice, ⚠️ or not: no code until the user's go for that slice;
- after every slice, with the record and its Try-it steps: the user checks the slice and accepts it; Claude does not run the human check in the user's place and does not mark a slice accepted;
- when the slice gate is red at the end of a slice: the failing command and why;
- when the build shows the spec is wrong or incomplete in a way that changes behavior, a file of the Scaffold or its wiring.

## Tooling

- Skills: `new-feature`, `investigate`, `write-spec`, `build-slice`, `amend-spec`, `bugfix`.
- Agents: `spec-critic` (the spec, before approval, only when the user asks, one pass), `spec-reviewer` (the full diff, after the last slice). Each writes its full report to a file under `artifacts/` (ignored by git) and answers with one line, the verdict and the path; the skill that started it reads the file. After the slice gate no agent reviews the slice; a slice review agent left in a project by an older kit runs only when the user asks for it, never by the build on its own. For codebase search use the built-in Explore agent.
- Hooks (`.claude/hooks/`, configured in `.claude/spec-driven.json` when the kit was set up): no source edits and no spec files on a protected branch, no file-changing git commands inside the review agents and no file but its report from a read-only one, commit and push as § Commit and push says.
- Templates: `.claude/templates/spec-template.md`.
