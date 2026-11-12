---
name: build-feature
description: "Use when implementing a slice of an approved Zyggy spec (_specs/<NN>-<short-name>.md). Reads the spec and the current code, builds the slice test-first across Core, Node, Cli, Hub and tests (Fake through the seams, then Wire to the real edge), proves it with the Verification Commands and the slice's acceptance criteria, records one Status line, and stops for the owner to review and commit. Also a reference catalog of Zyggy code shapes: seam + fake + real, PollState, reason enum, options, Node BackgroundService, CLI verb, Hub MCP tool, unit, golden-file and integration tests."
argument-hint: "The spec and the slice, e.g. 'discovery registry slice 3' or 'next slice of the discovery registry'"
---

# Build Feature

Build **one slice** of an approved spec, following `CLAUDE.md` § Delivery workflow (Workflow rules, Red-Green-Refactor-Proof loop, Human Review Gates) and the founding spec's §9 design rules. This skill adds what is specific to building from a spec; the Templates below are a **reference catalog** — look up the pieces the slice needs.

## Before changing files

1. **Read the spec** `_specs/<NN>-<short-name>.md`. Its Status must show it approved by the owner and Open questions must hold nothing that changes behavior; if not, stop and say so. The next slice is the first one in Slices without a "done" line in Status.
2. **Read the founding-spec sections** the spec's Context names, the code the slice touches, and the reference feature (the spec's Context, else `CLAUDE.md` § Project settings) in every project involved. Path-scoped rules in `.claude/instructions/` apply as you open matching files.
3. **State the approach** in a few lines: which projects and seams, which tests at which level (unit, golden file, integration, gate), anything the spec did not foresee. For a ⚠️ slice, present it in plan mode and wait for the owner.
4. **Check the working tree**: on `feature/<NN>-<short-name>` (not `main`) and clean. If no feature branch exists yet, create it from an up-to-date `main` and commit the approved spec as its first commit. Uncommitted changes of an earlier slice mean the owner has not approved it yet: ask, do not build on top of it.

If the spec turns out to be wrong or incomplete in a way that changes behavior, stop and ask; then edit the spec so it stays true. Do not build around a gap silently.

## Building

- **Fake slice**: the behavior against the seam interfaces (`IProcessRunner`, `IBusProvider`, `IModelRunner`, `ISecretStore`, `INotifier`, `IPolicySource`, `TimeProvider`) with substitutes, proven by unit tests in `tests/Zyggy.Core.Tests` (golden files when canonical forms are touched).
- **Wire slice**: replace the fake with the real edge (real `git` through `IProcessRunner`, `HttpClient` over a mocked handler, `tools/fake-claude`, a local bare git repo), with integration tests in `tests/Zyggy.Integration` for the happy path and one failure path ending in a `reason` code or rejection move. Never the real `claude`, never real GitHub. Use the `integration-testing` skill.
- **Gate slice**: the phase scenario in `tests/Zyggy.Integration/Gates/P<n>_<Name>.cs`, through the real `Zyggy.Node` services, plus `PROTOCOL.md` / `node.json` schema / runbook rows where behavior changed.
- **Test-only slice** (adds checks to existing behavior): see each new test fail by breaking the behavior on purpose or asserting the opposite, then restore it.
- Mirror the reference feature's structure and naming. Delegate non-trivial analyzer sweeps to `@code-analysis`. If packages are missing, `dotnet restore Zyggy.slnx` first.

## Done

A slice is done when:

1. the Verification Commands (`CLAUDE.md` § Build and test) are green with zero warnings — a clean build after any analyzer, package or props change;
2. each acceptance criterion the slice lists has been checked the way the spec says, and the result is stated (for an owner-run check, quote the evidence in the Status line once the owner gives it);
3. the spec's Status has a new line: slice, date, proof result (test counts), decisions taken.

Stop after every slice and report as a **visual approval report** (`CLAUDE.md` § Visual approval report — the Artifact page plus a short chat summary with an ASCII sketch): the diagram of what changed and how it now behaves, the proof result, the files changed, a proposed commit message, and the slice's review check worded as steps the owner can carry out now (which command to run, what to expect). A ⚠️ approach presented in plan mode gets the same diagrams of what will change. When the owner approves, commit the slice with that message (do not push yet); that approval is the acceptance. Build the next slice when the owner asks for it and the tree is clean.

## After the last slice

Start a fresh reviewer agent with no context from this session. Give it the spec path and the diff range (`main` to `HEAD`), and ask it to report each acceptance criterion as met or not met, with the file, the test or command, and the evidence, and to say whether each check would fail if the behavior were broken. Relay its report to the owner as a visual approval report for the whole feature (component diagram of everything touched, the end-to-end sequence, every acceptance criterion with its verdict and evidence, the release chain to come with the proposed version) and ask for the release approval. On approval, run the `ship` skill end to end without asking again.

---

## Design rules to honour in every step (founding spec §9)

- Git and `claude` are invoked only through `IProcessRunner`; tests substitute it. No LibGit2Sharp.
- All bus paths come from `BusPaths`. No path string concatenation elsewhere. Paths are tenant-prefixed.
- `EnvelopeSigner.Canonicalize` is the single source of truth for the signing input; changes go with golden files.
- The poller is a pure state machine (`PollState` record) with the HTTP call injected; tests drive it with a fake clock.
- `JobRunner` never throws to the loop: every failure becomes a report with a `reason` from the closed enum.
- No static mutable state; everything through DI. No code outside a seam implementation references GitHub, `claude`, Telegram or a secret store.
- Log once per state change, never per poll tick. Every log line carries `machine`, `jobId`, `phase`.
- Secrets never in `appsettings.json` or `node.json`.

---

## Templates — Reference Catalog

Not sequential steps. Pick what the current slice touches.

### Seam interface + fake + real implementation (`Zyggy.Core`)

Interface in `src/Zyggy.Core/<Folder>/I<Seam>.cs`:

```csharp
namespace Zyggy.Core.Bus;

/// <summary>Runs an external process. The only path by which Zyggy invokes git or claude.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> in <paramref name="workingDirectory"/>.</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

Real implementation, `internal sealed`, in the same folder; registered in the host's DI (`services.AddSingleton<IProcessRunner, ProcessRunner>()`). In tests use NSubstitute (`Substitute.For<IProcessRunner>()`) or a small hand-written fake that records calls when ordering matters (the git write sequence `pull --rebase → change → commit → push`).

### Pure state machine (poller)

```csharp
namespace Zyggy.Core.Bus;

public sealed record PollState(string? ETag, TimeSpan Interval, DateTimeOffset? BackoffUntil);

public static class PollPolicy
{
    public static PollState OnResponse(PollState state, PollResponse response, DateTimeOffset now, PollOptions options) => /* pure */ ...;
}
```

Test with a `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) and a `Theory` over status codes / rate-limit headers. The HTTP call lives in a thin `GitHubPoller` that takes `HttpClient` — test it with a mocked `HttpMessageHandler`.

### Closed reason enum + report assembly

```csharp
public enum JobFailureReason
{
    UnknownProject, UnknownAgent, Locked, Timeout, DlpFilter, ClaudeError, GitError, SchemaUnsupported, BudgetExceeded
}
```

Serialise as snake_case strings exactly as §6/§9/§14 name them (`unknown_project`, …). A test asserts the string of every member.

### Options / config binding

`node.json` binds to an options record in `Zyggy.Core` (validated with `IValidateOptions<T>`); `policy.yaml` is read through `IPolicySource`. Policy may only tighten `node.json`; write the merge as a pure function and test it.

### Node hosted service (`Zyggy.Node`)

```csharp
public sealed class PollLoop(IBusProvider bus, JobDispatcher dispatcher, TimeProvider clock, ILogger<PollLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) { /* poll → pull → process → sleep */ }
}
```

Keep the loop body in a testable method that takes the current `PollState` and returns the next; the `BackgroundService` only schedules it.

### CLI verb (`Zyggy.Cli`, System.CommandLine)

One file per verb under `src/Zyggy.Cli/Commands/<Verb>Command.cs`; the verb builds an envelope or reads the bus through `Zyggy.Core` types only. Exit code 0 on success, non-zero with a one-line error otherwise. Test the handler method directly with fakes; one integration test invokes the built CLI against the bare repo.

### Hub MCP tool (`Zyggy.Hub`, ModelContextProtocol SDK)

One tool per method, attributes from the official C# SDK; input validated; `remember` refuses secret patterns before touching disk. Verify the SDK's current attribute names and hosting API with the context7 docs before writing — do not code from memory.

### xUnit unit test (`tests/Zyggy.Core.Tests`, mirrors the source folder)

```csharp
namespace Zyggy.Core.Tests.Envelope;

public sealed class EnvelopeSignerTests
{
    [Fact]
    public void Verify_TamperedBody_ReturnsInvalid()
    {
        // Arrange
        var signer = new EnvelopeSigner(new FakeSecretStore("k1", "secret"));
        var envelope = Golden.Load("job-minimal.md") with { Body = "changed" };

        // Act
        var result = signer.Verify(envelope);

        // Assert
        result.Should().Be(SignatureResult.Invalid);
    }
}
```

Naming `<Method>_<Scenario>_<Expected>`, AAA, one Act, FluentAssertions, NSubstitute at seams only.

### Golden-file test (`tests/golden/`)

Each golden case is an envelope `.md` plus a sidecar with the expected canonical form and signature for a known key. A `[Theory]` with `[MemberData]` enumerates the folder. Adding a case is part of RED for any canonicalisation change.

### Integration test (`tests/Zyggy.Integration`)

Use the harness in `.claude/skills/integration-testing/SKILL.md`: a bare repo + working clone per test, `tools/fake-claude` on the path, real `GitClient`. Assert on files and commits in the bare repo, never on log text alone.

---

## Related Skills & Agents

| When your slice touches… | Use |
|-------------------------|-----|
| Envelope fields, signing, state moves, bus paths | **`bus-protocol`** skill — the §4/§5 rules in checklist form |
| Real git / fake claude / gate scenarios | **`integration-testing`** skill |
| A new public type in `Zyggy.Core` | **`add-public-api`** skill |
| Violation sweeps during CODE ANALYSIS | **`@code-analysis`** agent |
| A bug found mid-slice (≤ 2 files) | **`@bugfix`** agent |
