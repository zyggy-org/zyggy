---
name: build-feature
description: "Use when implementing a deliverable or vertical slice in Zyggy after a _plans/<NN>-<Deliverable>.md (repo root) is approved. Executes plan steps using Red-Green-Refactor. Each plan step is a vertical behavior slice that may touch Core, Node, Cli, Hub and tests. Provides code templates as a reference catalog: seam interface + fake + real implementation, options/config binding, Node BackgroundService, CLI verb, Hub MCP tool, xUnit unit test, integration test against bare repo + fake claude, golden-file test."
metadata:
  argument-hint: "Which plan step to implement, e.g. 'Step 1 — Reject an envelope with a bad signature'"
---

# Build Feature — Implementation Skill

Execute approved `_plans/<NN>-<Deliverable>.md` steps using Red-Green-Refactor, running consecutive steps back-to-back and stopping **only** at 🛑 HUMAN GATE checkpoints, following the founding spec's §9 design rules and any existing pattern the plan names as reference.

Each plan step is a **vertical behavior slice** that may touch several projects in one cycle. The Templates below are a **reference catalog** — look up the pieces your current step needs, not a sequential order to follow.

## Vertical Slice Strategy

Plans decompose deliverables into vertical slices, not layers. Each slice follows a two-phase pattern:

1. **Fake phase** — implement the behavior against the seam interfaces (`IProcessRunner`, `IBusProvider`, `IModelRunner`, `ISecretStore`, `INotifier`, `IPolicySource`, `TimeProvider`) with substitutes, proven by xUnit tests in `tests/Zyggy.Core.Tests`.
2. **Wire phase** — replace the fake with the real edge (real `git` through `Process`, `HttpClient` over a mocked handler, the `tools/fake-claude` script, a local bare git repo), proven by an integration test in `tests/Zyggy.Integration`. Never the real `claude`, never real GitHub.

**Why this order?** The seams are the contracts between the spec's agents (§12). Proving behavior through them first surfaces mismatches in envelope fields, reason codes and state moves before any process or network code exists.

## Prerequisites

- A **`_plans/<NN>-<Deliverable>.md`** (repo root) must exist and be **approved by the user**.
- Read the plan step, its spec `_specs/<NN>-<Deliverable>.md` (Contracts section) and the founding-spec sections the plan cites.
- Read every existing file in the step's **Scope** and any reference feature the plan names.
- **Do not skip past a gate.** Prove every step (full RGR cycle, VERIFY green), then continue directly to the next step — stop only when the next plan item is a 🛑 HUMAN GATE.

## Progress Tracking

The plan file is a **living document**:

- **Before starting**: find the **first unchecked `[ ]`**. On a step's `Done` box → implement that step. On a 🛑 HUMAN GATE box → the preceding steps are done but not approved: stop and wait.
- **After a step's VERIFY passes**: change its `Done` checkbox to `[x]` and continue straight to the next step.
- **After the user approves at a gate**: change `[ ]` to `[x]` on every checkbox in that 🛑 HUMAN GATE block, then continue.

## Mandatory Workflow Per Step

Follow the **RGR-Proof loop** in `CLAUDE.md` — one cycle per step, RED before GREEN, steps run back-to-back, stop **only** at a 🛑 HUMAN GATE, delegate analyzer sweeps to `@code-analysis`.

Skill-specific notes:
- **READ** — read the plan step, the spec contracts, the existing code in Scope.
- **RED** — write the unit test (and integration test / golden file when the step says so) and run the failing-run command from the plan.
- **GREEN** — minimal code across all projects in Scope.
- **CODE ANALYSIS** — invoke `@code-analysis` for non-trivial sweeps.
- **PROVE** — `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx`, `dotnet format Zyggy.slnx --verify-no-changes`.
- **MARK DONE** — check the step's `Done` box.

> If packages are missing, run `dotnet restore Zyggy.slnx` first. If `Zyggy.slnx` is still empty (scaffolding not done), build and test per project.

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

Not sequential steps. Pick what the current plan step touches.

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

| When your step touches… | Use |
|-------------------------|-----|
| Envelope fields, signing, state moves, bus paths | **`bus-protocol`** skill — the §4/§5 rules in checklist form |
| Real git / fake claude / gate scenarios | **`integration-testing`** skill |
| A new public type in `Zyggy.Core` | **`add-public-api`** skill |
| Violation sweeps during CODE ANALYSIS | **`@code-analysis`** agent |
| A bug found mid-step (≤ 2 files) | **`@bugfix`** agent |
