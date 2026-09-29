---
description: "Use when writing, reviewing, or generating unit or integration tests for Zyggy. Covers xUnit + FluentAssertions + NSubstitute conventions, AAA structure, folder layout, golden-file tests, the fake-clock rule, and the bare-repo + fake-claude integration harness."
applyTo: "tests/**"
---
# Test Conventions — Zyggy

## Frameworks & Tooling
- **xUnit** (`[Fact]` / `[Theory]` + `[InlineData]` / `[MemberData]`) with **FluentAssertions** for assertions and **NSubstitute** for substitutes at seams — as the founding spec §9 prescribes. No MSTest, no NUnit, no Moq.
- Test projects are plain `Microsoft.NET.Test.Sdk` projects: run with `dotnet test`, filter with `--filter "FullyQualifiedName~<Class>"` or `--filter "FullyQualifiedName~<Class>.<Method>"`.
- Time comes from `TimeProvider`; tests use `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`). Never `DateTime.UtcNow`/`Task.Delay` with real durations in a test.
- Prioritize meaningful tests over chasing a coverage number.

## Project Layout

| Project | Contains |
|---|---|
| `tests/Zyggy.Core.Tests/` | Unit tests; folders mirror `src/Zyggy.Core/` (`Envelope/`, `Bus/`, `Registry/`, `Jobs/`, `Memory/`, `Secrets/`) |
| `tests/Zyggy.Integration/` | Real edges against a local bare git repo and `tools/fake-claude`; `Infrastructure/` fixtures, `Gates/P<n>_*.cs` phase scenarios |
| `tests/golden/` | Canonical envelopes with expected canonical bytes and signatures |
| `tools/fake-claude/` | Script emitting canned `stream-json` scenarios |

Name test files `<ClassUnderTest>Tests.cs`; test classes `sealed`.

## Naming
`<Method>_<Scenario>_<Expected>` — e.g. `Verify_TamperedBody_ReturnsInvalid`, `Claim_PushRejectedTwice_SkipsJob`.

A failing test name should tell you *what broke* without reading the code.

## Structure — Arrange-Act-Assert

Every test follows AAA with **one Act and one logical assertion** per test.

```csharp
[Fact]
public void OnResponse_NotModified_KeepsEtagAndInterval()
{
    // Arrange
    var state = new PollState("abc", TimeSpan.FromSeconds(10), null);
    var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

    // Act
    var next = PollPolicy.OnResponse(state, PollResponse.NotModified, now, PollOptions.Default);

    // Assert
    next.Should().Be(state);
}
```

Avoid logic (`if`, `for`, `switch`) inside tests. For multiple inputs use `[Theory]` with `[InlineData]`; for reusable data sets use `[MemberData]`.

---

## What to unit-test

- **Pure functions and state machines** — `EnvelopeParser`, `EnvelopeSigner.Canonicalize`/`Verify`, `PollPolicy`, `BusPaths`, policy/node.json merge, report assembly, REPORT-section extraction, DLP filter, context ranking.
- **Orchestration through seams** — `BusRepository`, `JobRunner`, `PollLoop` body with substituted `IProcessRunner` / `IModelRunner` / `IBusProvider` / `ISecretStore` / `INotifier` / `IPolicySource`.
- **Every failure path** — each closed-enum `reason` has at least one test that produces it.

Do **not** unit-test trivial records, DI wiring beyond one resolution test per extension method, or the real `git`/`claude` edges (those are integration tests).

### Reference pattern

```csharp
public sealed class JobRunnerTests
{
    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly IProcessRunner _process = Substitute.For<IProcessRunner>();
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public async Task RunAsync_UnknownProject_ReportsRejectedWithReason()
    {
        // Arrange
        var sut = new JobRunner(_model, _process, _clock, registry: RegistryDocument.Empty, NullLogger<JobRunner>.Instance);
        var job = TestEnvelopes.Job(project: "does-not-exist");

        // Act
        var report = await sut.RunAsync(job, CancellationToken.None);

        // Assert
        report.Status.Should().Be(ReportStatus.Rejected);
        report.Reason.Should().Be(JobFailureReason.UnknownProject);
        await _model.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }
}
```

### Key rules
- **Substitute only at seams** (interfaces). No real process, network, file-system bus, or secret store in a unit test. A temp directory for a project lock or a registry scan is fine.
- Prefer small builder helpers (`TestEnvelopes.Job(...)`) over big constructor setups.
- Every bugfix starts with a **failing regression test**.
- **One Act per test.** If a test needs multiple acts, split it.
- Never assert on log text as the only evidence; assert on the returned state, report, or file.

---

## Golden-file tests (`tests/golden/`)

The canonical form and signature are the protocol. Each golden case is:

```
tests/golden/<case>.md          # the envelope as it would sit on the bus
tests/golden/<case>.canonical   # exact bytes Canonicalize must produce
tests/golden/<case>.sig         # expected HMAC (64 lowercase hex, no newline) for key ids acme/1 and acme/2 and the two test secrets fixed by _specs/03-envelope-signing.md
```

A `[Theory]` with `[MemberData]` enumerates the folder; a new case is added in RED for any canonicalisation or field change. Golden files are copied to output (`<None Include="../golden/**" CopyToOutputDirectory="PreserveNewest" />`).

---

## Integration tests — `tests/Zyggy.Integration/`

Test the **real edges**: `GitClient` over `Process` against a local bare repo, `ClaudeProcess` running `tools/fake-claude`, the Node poll loop round trip, CLI verbs, Hub tools over stdio. Business-logic coverage stays in unit tests; integration tests verify that the layers work together and that the bus on disk matches §4.

### Principles
- **Keep the host as real as possible.** Build services through the production DI extension methods; swap only the `claude` path, the bus remote (bare repo) and the secret store (in-memory).
- **Never the real `claude`, never GitHub.** Any test needing network is wrong.
- **Assert on the bare repo**, not the clone: `git show main:<path>`, `git log -1 --format=%s`.
- **Per-test isolation**: one bare repo + clone per test class via `IClassFixture<BusRepoFixture>` / `IAsyncLifetime`; delete on dispose; git identity set in the fixture, never from the machine's global config.
- Tag with `[Trait("Category", "Integration")]` so `--filter "Category!=Integration"` runs units only.

Full harness description and examples: `.claude/skills/integration-testing/SKILL.md`.

### Gate scenarios
`Gates/P<n>_<Name>.cs` — one file per §12 phase, one `[Fact]` per gate criterion, a working round trip from empty bus to a report on `origin/main`. Required for a phase-closing deliverable's definition of done.

---

## Testing pyramid

| Layer | Description | Automation |
|---|---|---|
| **Unit** (base) | Pure functions, state machines, orchestration through seams | Every build |
| **Golden** | Canonical form + signature fixtures | Every build |
| **Integration** | Real git + fake claude against a bare repo | Every build (slower; filterable) |
| **Gate** | One scripted round trip per §12 phase | Every build once the phase ships |
| **Manual** | Real Central VM / laptop install, real `claude` | Phase gate demo by the user |

## Run commands

```powershell
dotnet test Zyggy.slnx
dotnet test Zyggy.slnx --filter "Category!=Integration"
dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerTests"
dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_"
```
