# Zyggy.Core.Tests

Unit tests for `Zyggy.Core`. Fast, isolated, run on every build. Folders mirror the library (`Envelope/`, `Bus/`, `Registry/`, `Jobs/`, `Memory/`, `Secrets/`).

## What is tested here

- **Pure logic and state machines**: envelope parsing, `EnvelopeSigner.Canonicalize` and `Verify` (with the golden files in `tests/golden`), `BusPaths`, the poller's `PollState` transitions driven by a fake clock, report assembly and REPORT-section extraction, the DLP filter, context ranking, the `node.json` / `policy.yaml` merge.
- **Orchestration through seams**: `BusRepository`, `JobRunner` and the poll-loop body with substituted `IProcessRunner`, `IModelRunner`, `IBusProvider`, `ISecretStore`, `INotifier`, `IPolicySource`.
- **Every failure path**: each closed-enum `reason` code has at least one test that produces it.

Nothing here runs `git`, `claude`, or touches the network. Real edges belong in `Zyggy.Integration`.

## Stack and conventions

xUnit, FluentAssertions, NSubstitute, `FakeTimeProvider`. Test names follow `<Method>_<Scenario>_<Expected>`, Arrange-Act-Assert, one act per test. Full conventions: `.claude/instructions/tests.md`.

```powershell
dotnet test tests/Zyggy.Core.Tests
dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerTests"
```
