---
name: integration-testing
description: "Use when writing or running Zyggy integration tests in tests/Zyggy.Integration: the local bare-git-repo bus harness, the tools/fake-claude script, real GitClient/ClaudeProcess over Process, Node poll-loop round trips, CLI verb invocations, and the phase gate scenarios under Gates/P<n>_*.cs. Use for: proving a wire step, scripting a §12 gate, debugging a failing round trip, asserting on bus files and commits."
metadata:
  argument-hint: "Describe the scenario, e.g. 'claim a job from the bare repo and assert the git mv commit' or 'script the P1 gate'"
---

# Integration Testing — bare repo + fake claude

Integration tests prove a **real edge** (git via `Process`, the `claude` process contract, bus file layout, hosted services, CLI verbs) without touching the real world. Two fixtures make that possible; both are mandated by the founding spec §9 (`tests/AgentBus.Integration`: "end-to-end against a local bare git repo and a fake claude script").

## Quick decision

| Situation | Approach |
|-----------|----------|
| Pure logic through a seam (`IProcessRunner`, `IModelRunner`, fake clock) | Unit test in `tests/Zyggy.Core.Tests` — not this skill |
| Real `git` commands, bus layout on disk, commits on `origin/main` | **Bare repo harness** below |
| Real `ClaudeProcess` stream-json parsing, timeouts, REPORT extraction | **fake-claude** below |
| `Zyggy.Node` poll → claim → run → report → push | Both, driven by a fake `IBusProvider` HEAD check or the real poller against a mocked `HttpMessageHandler` |
| `zyggy <verb>` | Invoke the CLI entry point in-process with a bare repo |
| A §12 phase gate | `tests/Zyggy.Integration/Gates/P<n>_<Name>.cs`, both fixtures, one scenario per gate |

Never call the real `claude`. Never call GitHub. If a test needs a network, it is wrong.

---

## Harness 1 — bare repo bus

One bare repository plus one working clone per test, created under a temp directory and deleted on dispose. The bare repo plays `origin` (what GitHub would be); the clone is the node's `bus.localPath`.

```csharp
namespace Zyggy.Integration.Infrastructure;

public sealed class BusRepoFixture : IAsyncLifetime
{
    public string BareDir { get; private set; } = "";
    public string CloneDir { get; private set; } = "";
    public string Tenant { get; } = "geoffrey";

    public async ValueTask InitializeAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"));
        BareDir = Path.Combine(root, "bus.git");
        CloneDir = Path.Combine(root, "bus");
        await Git(root, "init", "--bare", "--initial-branch=main", BareDir);
        await Git(root, "clone", BareDir, CloneDir);
        await Git(CloneDir, "config", "user.name", "zyggy (test)");
        await Git(CloneDir, "config", "user.email", "test@zyggy.org");
        // seed layout: tenants/<tenant>/nodes/<machine>/{jobs,jobs/claimed,jobs/rejected,reports,context}
    }

    public Task<string> HeadSha() => GitOut(BareDir, "rev-parse", "main");
    public Task<string> Show(string path) => GitOut(BareDir, "show", $"main:{path}");
    public async ValueTask DisposeAsync() { /* delete root, ignore IO errors */ }
}
```

Rules:
- Seed the layout with the production `BusPaths` so the test breaks if the layout changes.
- Drop envelopes into the clone with the production `EnvelopeSigner` and a test key; commit and push them as "the sender" from a **second** clone when the scenario needs a foreign writer (simulates Central pushing a job).
- Assert on the **bare repo**: `git show main:<path>` for content, `git log -1 --format=%s` for the commit message contract `<type> <id> <from>→<to>`, absence of a path after a `git mv`.
- Use `IProcessRunner`'s real implementation for the code under test, and the fixture's own helper for setup/assertions, so a bug in `GitClient` cannot mask itself.
- Push-rejection scenarios: advance `main` in the bare repo from the second clone between the SUT's `pull --rebase` and its `push` (inject a hook through `IProcessRunner` decoration, or pre-stage the conflicting commit and assert the retry path).

---

## Harness 2 — fake claude

`tools/fake-claude/` is a script that emits canned `stream-json` lines and exits. The test sets `claude.path` in the node options to that script so `ClaudeProcess` runs it exactly like the real CLI.

Layout:

```
tools/fake-claude/
  fake-claude.ps1        # Windows
  fake-claude.sh         # Linux/macOS
  scenarios/
    done.jsonl           # assistant text with a REPORT section + result event {cost_usd, duration_ms, num_turns}
    no-report.jsonl      # assistant text without REPORT → summary falls back to last text block, truncated
    hang.jsonl           # never emits result → timeout path
    error.jsonl          # non-zero exit → claude_error
```

The script picks a scenario from an environment variable (`ZYGGY_FAKE_CLAUDE_SCENARIO`) or the first positional argument the test controls; it also writes the arguments it received to a file so tests can assert the invocation contract from §6 (`-p`, `--cwd`, `--output-format stream-json`, `--permission-mode auto`, `--allowedTools`, `--max-turns`).

Rules:
- Every scenario file is real `stream-json` shape as documented for Claude Code; when the shape is unknown, capture it once from a real `claude -p` run **outside** the test suite and check the capture in.
- Timeout tests use a short `timeout_minutes` override and a `FakeTimeProvider` where the runner supports it; never `Thread.Sleep` for minutes.
- Assert the report: `status`, `reason`, `summary` ≤ 2,000 chars, `files_changed`, `cost_usd`, and that the transcript landed under `<node dir>/runs/<id>.jsonl`.

---

## Writing a test

```csharp
namespace Zyggy.Integration.Bus;

public sealed class ClaimJobTests(BusRepoFixture bus) : IClassFixture<BusRepoFixture>
{
    [Fact]
    public async Task Claim_JobAddressedToMe_MovesToClaimedOnOrigin()
    {
        // Arrange
        var jobPath = await bus.SeedSignedJobAsync(to: "home-laptop", body: "hello");
        var sut = TestHost.BuildBusRepository(bus.CloneDir, machine: "home-laptop");

        // Act
        await sut.ClaimAsync(jobPath, CancellationToken.None);

        // Assert
        (await bus.Show($"tenants/geoffrey/nodes/home-laptop/jobs/claimed/{jobPath.FileName}")).Should().Contain("hello");
        (await bus.LastCommitSubject()).Should().Be($"job {jobPath.Id} central→home-laptop");
    }
}
```

- xUnit `IClassFixture<T>` / `IAsyncLifetime`, AAA, FluentAssertions, one Act.
- Name: `<Operation>_<Scenario>_<ObservableOutcome>`.
- Always include one **failure path** per edge: bad signature → `jobs/rejected/` + `status: rejected` report; push rejected → retry once, then skipped; timeout → `status: timeout`.
- Tests must not depend on run order or on a machine-wide `git config`; set identity in the fixture.
- Mark slow ones `[Trait("Category", "Integration")]` so `dotnet test --filter "Category!=Integration"` gives a fast unit-only run.

---

## Gate scenarios (`tests/Zyggy.Integration/Gates/`)

One file per phase, `P<n>_<Name>.cs`, one `[Fact]` per gate criterion from the spec/plan, using both harnesses and the **real** `Zyggy.Node` services built through the production DI extension methods (only `IProcessRunner`-visible paths and options are swapped). The gate is "a working round trip, not a code review": the test starts from an empty bus and ends with a report in `reports/` on the bare repo's `main`.

## Run commands

```powershell
dotnet test tests/Zyggy.Integration
dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ClaimJobTests"
dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_"
dotnet test Zyggy.slnx --filter "Category!=Integration"      # fast unit-only run
```

Prerequisite: `git` on PATH (the harness and `GitClient` both shell out to it).
