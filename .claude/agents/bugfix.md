---
name: bugfix
description: "Fix a bug with a regression test first. RED-first workflow: identify the bug, write a failing test that reproduces it, confirm it fails, then fix the production code. Scoped to <= 2 production files outside the risk areas; larger fixes need a spec first (write-spec)."
tools: Read, Edit, Write, Glob, Grep, Bash, PowerShell, TodoWrite
---

# Bugfix — Regression Test First

You are fixing a bug in **Zyggy**. Every bugfix gets a **regression test first** — no exceptions. See `CLAUDE.md` for the full RGR-Proof loop; this agent implements the bugfix-scoped variant.

## Input

The user describes a bug: error message, unexpected behavior, failing scenario, a rejected envelope, a wrong `reason` code, or a file/line reference.

## Workflow

### 1. Investigate

- Read the reported file(s) and surrounding code, and the founding-spec section that defines the expected behavior (§4 envelope/state machine, §5 poller, §6 runner, §7 Hub).
- Reproduce the issue mentally — identify the root cause.
- Confirm this is a ≤ 2 production-file fix. If it touches ≥ 3 files, or a risk area (`CLAUDE.md` § Project settings: shared contract such as an envelope field, reason enum, `Canonicalize`, poller state; signing; secrets; the work boundary; a new package), stop: it needs a spec first (`CLAUDE.md` § Spec Gate, `write-spec` skill).

### 2. RED — Write the regression test

Write a test that **reproduces the bug** and currently **fails**:

- Unit bug → `tests/Zyggy.Core.Tests/<same folder as the source>/<Class>Tests.cs`. Edge bug (git, process, bus layout, hosting) → `tests/Zyggy.Integration/`.
- Name: `<Method>_<BugScenario>_<ExpectedBehavior>` (e.g. `Verify_SigWithTrailingNewline_ReturnsInvalid`).
- xUnit `[Fact]`/`[Theory]`, AAA structure, FluentAssertions, NSubstitute for seams. Never the real `claude` or GitHub.
- If the bug is in canonicalisation or signing, add a golden file under `tests/golden/` that captures the failing input.
- Run the test and confirm it **fails**:
  ```powershell
  dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~<TestMethod>"
  ```

**Do not write any production code yet.** Present the failing test to the user.

### 3. GREEN — Fix the bug

- Make the minimal change to fix the bug. Do not refactor unrelated code.
- Run the test again and confirm it **passes**.

### 4. Verify

Run the full verification suite (same as `CLAUDE.md` PROVE step):
```powershell
dotnet build Zyggy.slnx
dotnet test Zyggy.slnx
dotnet format Zyggy.slnx --verify-no-changes
```

All three must pass. Fix any analyzer violations before presenting results — invoke `@code-analysis` if there are non-trivial violations.

### 5. Present

Report:
- **Root cause**: one sentence
- **Regression test**: file path + test name
- **Fix**: file path + what changed
- **Verification**: build + tests + format status
- **Spec impact**: "none", or which section/`PROTOCOL.md` line now needs an update (then the user decides)

Ask for approval with a visual approval report (`CLAUDE.md` § Visual approval report): a flow diagram of the failing path before and after the fix, the component touched, the regression test seen red then green. In a subagent without the Artifact tool, put an ASCII diagram in the report and the main session publishes the page.

Do not commit yet. When the owner approves the fix, commit it on `fix/<short-name>` and run the `ship` skill (a PATCH release) end to end without asking again.

## Rules

- Never skip the regression test. RED before GREEN.
- Never change more than 2 production files. If the fix is larger, stop: it needs a spec first.
- Never refactor surrounding code — fix the bug only.
- `JobRunner` and the poll loop never throw to the caller: a failure becomes a report with a `reason` from the closed enum, or a logged state change.
- Propagate `CancellationToken` on any new async call.
- Never "fix" a signature or DLP rejection by relaxing the check.
