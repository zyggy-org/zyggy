---
paths:
  - "src/Test/**"
---
<!-- guideline: testing -->
# Testing for an AI agent

Applies whenever an AI agent writes, changes or judges a test in this solution: the builder of a slice, the builder of a bugfix, and the agent that reviews them. The spec-driven workflow's skills and agents read this file by its first line, at the steps that write or judge tests; it also loads when a file under `src/Test/` is opened. The test conventions themselves (frameworks, naming, structure) are the project's own, and the test levels are in `CLAUDE.md` § Project settings § Test levels; this file says in which order the tests are written, what an agent may and may not do to make them pass, and which tests are worth writing (Efficient tests, R-1 to R-7: their only home; the other testing texts point here).

## The order

The workflow's order leads; it is not a choice per slice.

1. **The slice test first.** A slice starts with its slice test, written before any production code: an integration test through the real entry point: for a page, through the page or component the user uses, rendered on the real Presentation DI container, never through the view model (R-3); a stub slice's test is the same Presentation integration test with the stub as the faked backend client, not a separate bUnit test. It is run, filtered, and must fail because the behavior is missing.
2. **A unit test before pure logic and its variants** (R-3). Such logic (a rule with several cases, a calculation, a mapping, an encoding, a guard, an error path) gets one data-driven unit test before the code, one row per distinct case, seen to fail for the expected reason; then the minimal code that makes it pass. Other logic is driven by the slice test (T-13).
3. **Wiring needs no unit test.** Wiring between layers is asked for by the slice test, which is the failing test for it.
4. **A bugfix starts with a regression test** at the level where the bug shows, failing for the bug's reason.
5. **No test over behavior that already exists is proven by breaking it.** A test is seen failing only before the behavior exists (T-4); production code is never modified, commented out or broken to see a test fail.

Where a project's older test instructions keep business-logic coverage "mainly in unit tests" and treat integration tests as a later check, this order wins: the integration test is written first and drives the slice; the unit tests follow the logic it asks for.

## The rules

Each rule has a number. A reviewer that finds a rule broken names it by its number (T-3, T-5).

| # | Rule |
|---|---|
| T-1 | **See it fail first, for the right reason.** Run the new test filtered before writing production code and read the failure. A missing type, a compile error, a harness or setup fault, a wrong route or a missing seed is not the right reason: fix it and run again until the failure names the missing behavior (the assertion on the outcome, a 404 on the route that does not exist yet, a `NotImplementedException` placed on purpose). |
| T-2 | **No test written to match the code after the fact.** When production code was written before its test (a slip, a generated file, a refactor), the test is still written and the build's record names it as not seen red, with why; the mutation check then shows whether it can fail; the code is not broken by hand to make it fail (T-4). A test that was green on its first run proves nothing until it has been red once. |
| T-3 | **No weakened assertion to make a test pass.** When a test fails on the outcome and the agent believes the outcome is right, the agent changes the code or raises the spec; never the expected value, the status code, the count or the strictness (`IsNotNull` in place of `AreEqual`, `IsTrue(result.IsSuccess \|\| …)`, a `try`/`catch` around the assertion). A slice test that had to change is reported in the build's record with the reason. |
| T-4 | **No deliberate breaks.** The proof is the tests written first and seen red before the behavior exists, then the slice gate green, then the mutation check (`scripts/mutation-check.mjs`, Stryker.NET on the changed lines of the slice's production files: the proof that the tests can fail), then the AC-to-test table. The build never modifies, comments out or breaks production code to see a test fail (the mutation check alone mutates it, in its own copy), and never weakens code or deletes a line to raise the mutation score; a survivor is killed by a new or stronger test (seen red first), or classified as `build-slice` says. |
| T-5 | **No mocking of the unit under test.** A unit test mocks only the interfaces at the unit's boundary (repository, service client, messaging, time); never the class under test, a value object, a DTO or a domain entity, and never a partial mock of the class under test. An integration test replaces only true external dependencies (messaging, third-party HTTP) and never the application's own handlers, repositories or authorization. |
| T-6 | **Assert the outcome, not the activity.** A test that only verifies that a method was called, passes on any error response, or asserts nothing about the result is weak and counts as no test. What to assert, and when a call may be verified: R-4. |
| T-7 | **One behavior per test, no logic in a test.** No `if`, loop or `switch`; a second scenario is a second test or a `[DataRow]`. |
| T-8 | **The slice test is left as written** once it has been red; adding a case to it is a new test. |
| T-9 | **Never skip, ignore or quarantine a failing test** to get green: no `[Ignore]`, no `[TestCategory]` filter that leaves it out of the Verification Commands, no commented-out assertion, no lowered analyzer rule. A test that cannot pass stops the slice, and the agent says why. *(New: no source states it; it follows from the workflow's "without weakening or disabling a rule".)* |
| T-10 | **Run the gate, not only the new tests**, before claiming the slice is proven: the slice gate (`node .claude/hooks/verify.mjs --slice <test>...`: the build, all unit tests, the UI tests, the integration tests written in the slice; no format check), zero warnings; the feature gate, every integration test and the format check included, runs once in the final review. A filtered run of a single test is for the red-green loop only. |
| T-11 | **Report what proves the slice.** In the conversation, at the end of the slice, as the build's final answer, the record: the slice test and the exact failure of its first run; each unit test and its first failure; each acceptance criterion against its test; the test list (R-1); the gate that ran, the integration tests it included and its last line; the mutation check's score line and each survivor with its classification (the committed `_specs/<Feature>/mutation-<slice>.md`); any test changed after it was written, and why; any test that could not be run, and why. The record does not repeat the spec and is written to no file. Never "all tests pass" without the command and its last line. |
| T-12 | **Test data is neutral and realistic**: plausible names and dates, never a real person, a national identification number, a real e-mail address or a real customer file; no secret in the `appsettings*.json` of a test host beyond the dummy values the host needs. |
| T-13 | **Test economy.** One outer integration test class per slice proves its criteria end to end, written first in the integration test location and seen red before any stub or production code; a criterion gets its own test method only when it needs its own setup, otherwise one scenario carries several AC ids. Which other tests are written, at which level, and which never (pass-through code among them): R-1 to R-7; variants are one data-driven test. A security rule keeps every case it names plus a dangerous variant of each. Each test is seen red once. The mutation check scores only the slice's changed lines: survivors outside the slice's changed lines are never a reason to add tests, and tests for existing behaviour are out of scope unless the spec asks for them (R-1 to R-7). |

## Efficient tests

The fewest tests that make a plausible bug fail. A reviewer names a broken rule by its number (R-2).

Sources: Google Testing Blog (test behavior, not implementation; change-detector tests; prefer public APIs), Khorikov's four pillars of a good test, Kent C. Dodds (mostly integration; tests that resemble real use), Microsoft Learn (unit testing best practices; mutation testing), bUnit docs (semantic markup comparison).

| # | Rule |
|---|---|
| R-1 | **Every test earns its place.** Before the slice is done, each new test has one line in the slice's test list: the acceptance criterion it protects and the plausible bug it catches (`AC-5: formula guard dropped for '+'`). A test without such a line is deleted, not kept. |
| R-2 | **Never write these:** a test that asserts a constant, literal or resource value against itself; tests of getters, setters, records, DTOs; DI registration or configuration with no behavior; framework or third-party behavior; an expected value computed by the code under test; a test whose only assertion is on a mock it configured itself when the outcome could be asserted instead; a test that still passes with the feature removed; a data row or test that repeats a case another test already covers. |
| R-3 | **One behavior, one level: the cheapest level that can catch its likely bug.** Unit: pure logic and its variants (encoding, formatting, guards, calculations); each data row is a distinct case or branch. UI (bUnit): what the page does for the user (which rows, which order, which labels, enabled or disabled state, what is shown after an action); assert what the user sees, compare markup semantically (`MarkupMatches`, or find by role or text), never brittle full-markup strings; a bUnit test never counts as the slice's integration test. Integration: at least one per slice; it enters through the page or component the user uses, never through the view model, so the page's own wiring (grid rows, sort, paging) is under test (rendered on the real DI container with the real view model and services, only the browser edge, the clock, auth and the backend client faked; a stub slice's test is this same test with the stub as the backend client; for an endpoint, the HTTP call through the real host), never builds its own input that the production code is supposed to produce, covers the main path plus at most one failure or edge, and does not repeat unit-level variants. |
| R-4 | **Assert outcomes:** bytes, state, file names, log content, rendered output. Verify a mock's calls only at the system boundary (the backend client, the browser download), and only when the call is the behavior. |
| R-5 | **Test through the public surface** (Google: prefer public APIs). An inner class gets its own tests only when its logic is too rich to reach through the public path. |
| R-6 | **Fewest tests that keep every behavior protected.** Tests that cover the same case are merged; parameterize variants (`[DataRow]`), never copy a test. Every behavior the slice is responsible for is caught by at least one test that would fail if it broke: for each acceptance criterion, name the test. |
| R-7 | **Line coverage is not the goal.** The question is whether a plausible bug makes a test fail. |

## For the agent that reviews

- A **weak check** is one that breaks T-3, T-5 or T-6. Report it with the rule's number and the line of the test.
- A **test not worth its place** breaks one of R-1 to R-7: report it with the rule's number; a criterion with no test that would fail if it broke (R-6) is a missing check.
- A check at the **wrong level** is judged against `CLAUDE.md` § Project settings § Test levels: a slice test of a wire slice that does not go through the real entry point, or a unit test that mocks what the slice test should exercise.
- **No deliberate breaks** (T-4): judge a check by whether it was seen red before the behavior existed, by what it asserts (T-3, T-5, T-6) and by the slice's committed mutation check, never by breaking the code by hand. A survivor classified 'equivalent' that a test could observe, or 'out of scope' code passed off as equivalent, is a finding; each 'accepted' survivor goes to the user.
- The mutation check reads the project's Pack values **Test projects** and **Mutation threshold**.
- A slice's record that lacks an item T-11 asks of it is reported as incomplete, naming the missing item.
