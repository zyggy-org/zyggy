---
name: build-slice
description: "Use when implementing a slice of an approved spec (_specs/<FeatureName>/spec.md), on the user's go for that slice. Builds exactly that slice from its Scaffold rows, test-driven without exception, ends on the slice gate green and answers with the record: each acceptance criterion with its test and the failure seen when it was red; commits only when the user asks."
argument-hint: "The spec and the slice, e.g. 'Subscriptions slice 1'"
---

# Build Slice

Build exactly the approved slice of `_specs/<Feature>/spec.md` the user gave the go for, using its Scaffold rows. One slice per session; the tree must be clean and on the spec's branch, otherwise ask. The values in `CLAUDE.md` § Project settings apply.

If the spec is wrong or incomplete in a way that changes behavior, a Scaffold file or its wiring, stop and say so (`amend-spec`); a different file name or place only goes in the record.

## Guidelines

The guidelines apply to every line: the project's rules under `.claude/rules/`, the testing, architecture and coding guidelines among them (each found by its first line `<!-- guideline: <role> -->`).

## Test-driven without exception

Note HEAD as the slice's base. See each new test fail first, filtered: it runs and fails on its assertion because the behavior is missing; a build error, a missing type, an exception or a skipped test is not that failure, so new code compiles and returns a default before its test runs. A data-driven test is seen failing with each row.
1. **Integration test first.** Write the slice's integration test of its behavior in the Integration test project, entering through the page or component the user uses, not through the view model, so the page's own wiring (grid rows, sort, paging) is under test (rendered on the real DI container, real view model and services; only the browser edge, the clock, auth and the backend client faked; never in the UI test project); a stub slice's test is this same integration test with the stub as the faked backend client, not a separate bUnit test. Add only the stubs the Scaffold rows name so it compiles, then run it: it is seen failing for the right reason: the behavior is missing, not the harness or the setup.
2. **Unit tests.** For each rule or calculation, a unit test seen failing, then the smallest code to pass, then refactor with every test green.
3. **Page tests.** A page test for rendering is written before its code and seen red too.
4. Repeat until the integration test is green. Each test carries its criterion id (`AC-3`). Which tests are worth writing, at which level, and what they assert: R-1 to R-7 of the testing guideline; a new test without its line in the test list is deleted.

A test is seen failing only before the behavior exists; the build never modifies, comments out or breaks production code to see a test fail.
An existing test is never changed or weakened unless the spec's criteria require it; then the record says which and why. Security and personal-data criteria are tested like any other: every case the rule names.
Read each file once (Grep or Glob first, then the range you need); use quiet build and test commands.

## Verification is the gate

Run `node .claude/hooks/verify.mjs --slice <IntegrationTestClass>` until it is green, fixing what it reports (a filter that matches zero tests is red). Quote its last line. It runs no format check: `dotnet format --verify-no-changes` runs once per feature, like the analyzers, never per slice.
Then the mutation check, the proof that the slice's tests can fail (with the dotnet-shared pack): `node scripts/mutation-check.mjs --slice <n> --base <commit>` (the slice's base) until it is green. It mutates and scores only the slice's changed lines: a survivor outside the slice's changed lines is never a reason to add a test, and tests for existing behaviour are out of scope unless the spec asks for them (R-1 to R-7). It writes `_specs/<Feature>/mutation-<n>.md`; classify each survivor there with one line why. A survivor is 'equivalent' only if no test could ever observe the change (same output, state and side effects for every input). Code that is merely untested or 'out of scope' is not equivalent: add or strengthen a test (seen red first), or list it as 'accepted' for the reviewer to approve. Never weaken code or delete a line to raise the score.

## The record is the final answer

Your final answer in the conversation, no file:
- one line per acceptance criterion: the test that proves it and the failure message seen when that test was red;
- the test list (R-1): one line for each new test, `test name -> AC -> the bug it catches`;
- the slice gate's last line, quoted;
- the mutation check's score line, and each survivor with its classification and why;
- anything that differs from the spec (an existing test changed, and why);
- how the user tries it: the steps, commands and addresses of this project.

No slice plan file, no recap file, no spec edits, no reviewing agent per slice. Then stop: the user checks the slice and gives the go for the next. Commit only when the user asks, `_specs/<Feature>/mutation-<n>.md` with the slice.
