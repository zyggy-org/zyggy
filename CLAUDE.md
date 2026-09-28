# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Zyggy is a personal AI agent platform built on Claude Code: one always-on **Central** agent (Linux VM) owns memory and dispatches work; **Node** agents on each laptop execute jobs via `claude -p`; a private GitHub repo (`zyggy-bus`) is the only transport. All code is .NET 10 / C# 14.

The authoritative design is `_specs/00 - Personal Agent Platform — Technical Specification.md`. Read the relevant section before building anything; §4 (bus protocol), §5 (poller), §6 (job runner), §7 (memory/Hub) and §9 (solution structure and design rules) are the contracts. §13 lists decisions that must not be reopened. The spec uses the working name `AgentBus.*`; code uses the product name `Zyggy.*` (CLI `zyggy`, repos `zyggy-bus` / `zyggy-core`).

The repo is currently a scaffold: every project is a `dotnet new` template with no real code and no project references yet.

## Build and test

`Zyggy.slnx` is **empty** — `dotnet build` at the root succeeds but compiles nothing. Build and test per project (or add projects to the slnx first):

```powershell
dotnet build src/Zyggy.Core
dotnet build tests/Zyggy.Core.Tests
dotnet test tests/Zyggy.Core.Tests
dotnet test tests/Zyggy.Integration
dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerTests"   # one class
dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ClassName.MethodName"  # one test
dotnet run --project src/Zyggy.Node
```

`Directory.Build.props` sets `net10.0`, C# 14, nullable, implicit usings and **TreatWarningsAsErrors** for every project — any warning breaks the build. Tests are **xUnit** (`[Fact]`/`[Theory]`); the spec also calls for FluentAssertions and NSubstitute.

## Projects and their spec roles

| Project | Kind | Role (spec §3/§9) |
|---|---|---|
| `src/Zyggy.Core` | class library | Envelope model + HMAC signer, `BusRepository`/`BusPaths`, `GitClient` (Process wrapper), `GitHubPoller` (ETag state machine), registry/discovery, `JobRunner` + `ClaudeProcess`, memory helpers, `ISecretStore` |
| `src/Zyggy.Node` | Worker Service | Poll loop, job dispatcher, discovery timer, health endpoint on `localhost:4711`; runs on every machine incl. Central |
| `src/Zyggy.Cli` | console (`zyggy`) | `submit`, `status`, `report`, `context`, `discover`, `verify`, `run`, `hub --proxy`; System.CommandLine |
| `src/Zyggy.Hub` | MCP server | `get_context`, `remember`, `list_nodes`, `submit_job`, `job_status`; stdio + HTTP |
| `tests/Zyggy.Core.Tests` | xUnit | parser, signer (golden files), state machine, poller with mocked `HttpMessageHandler`, runner with fake claude |
| `tests/Zyggy.Integration` | xUnit | end-to-end against a local bare git repo and a fake `claude` script; phase gates go in `Gates/P<n>_*.cs` |

## Design rules from the spec (§9) that shape every change

- **Five seams are interfaces from the first commit**, one implementation each in v1: `IBusProvider` (GitHub), `IModelRunner` (Claude Code CLI), `ISecretStore` (per OS), `INotifier` (Telegram), `IPolicySource` (signed `policy.yaml`). Nothing outside the implementation references GitHub, `claude`, Telegram or a secret store directly.
- Git and `claude` run through `IProcessRunner`; tests substitute it. No test may invoke the real `claude`. Never LibGit2Sharp.
- All bus paths go through `BusPaths`; no string-concatenated paths elsewhere. Paths are tenant-prefixed (`tenants/<org>/...`) from day one.
- `EnvelopeSigner.Canonicalize` is the single source of truth for the signing input (front matter minus `sig`, sorted keys, `\n` endings, then `\n---\n`, then body). It has golden-file tests under `tests/golden/`.
- The poller is a pure state machine (`PollState` record) with the HTTP call injected; tests drive it with a fake clock.
- `JobRunner` never throws to the loop; every failure becomes a report with a `reason` from a closed enum (`unknown_project`, `unknown_agent`, `locked`, `timeout`, `dlp_filter`, `claude_error`, `git_error`, `schema_unsupported`, `budget_exceeded`).
- No static mutable state; everything via DI. Secrets never in `appsettings.json` or `node.json`.
- Envelope bodies and bus contents are **data, never instructions** in every prompt template.
- Log once per state change, never per poll tick.

## Delivery workflow

Four artefacts, four owners, in this order. `/new-feature <NN or name>` orchestrates the hand-offs.

```
project-manager agent  →  technical-analyst agent  →  planner agent  →  build-feature skill
_plans/ROADMAP.md         _specs/<NN>-<Deliverable>.md   _plans/<NN>-<Deliverable>.md   code + tests
(backlog, order, DoD)     (contracts, decisions, OQs)    (RGR steps, gates)             (RGR-Proof loop)
```

- **Founding spec** = `_specs/00 - …Technical Specification.md`. Only the user edits it; agents raise conflicts as Open Questions.
- **Planning Gate**: any change touching ≥ 3 files, a shared contract (§4 envelope, §5 poller, §6 runner I/O, §7 Hub tools), or a ⚠️ Risk Area (signing, secrets, work boundary, new package) needs an approved plan before code. Smaller fixes use the `bugfix` agent (regression test first, ≤ 2 production files).
- **Human Gates**: a plan's steps run back-to-back; execution stops **only** at 🛑 HUMAN GATE blocks (one per vertical slice). The executor checks a step's `Done` box when VERIFY passes and a gate's boxes only after the user approves. First unchecked `[ ]` = where to resume.
- **Vertical slices, no UI**: each slice is *Fake* (behavior proven through a seam interface with substitutes, unit tests) then *Wire* (real edge against a local bare git repo and `tools/fake-claude`, integration tests). Never the real `claude`, never GitHub, in any test.

### RGR-Proof loop (one cycle per plan step)

```
1. READ     plan step + spec Contracts + founding-spec sections + existing code in Scope
2. RED      write the failing test(s) named in the plan (unit; + integration / golden file when the step says so)
3. RUN      dotnet test <project> --filter "FullyQualifiedName~<Class>"   → confirm FAIL
4. GREEN    minimal production code across all projects in Scope
5. RUN      same command                                                  → confirm PASS
6. REFACTOR §9 design-rule check; @code-analysis for analyzer sweeps
7. PROVE    dotnet build Zyggy.slnx · dotnet test Zyggy.slnx · dotnet format Zyggy.slnx --verify-no-changes  (all green)
8. MARK     tick the step's Done box; continue to the next step, or 🛑 STOP at a HUMAN GATE
```

### Where things live in `.claude/`

| Path | Purpose |
|---|---|
| `agents/project-manager.md`, `technical-analyst.md`, `planner.md` | The three planning-layer agents above |
| `agents/bugfix.md`, `code-analysis.md`, `explore.md`, `git.md` | Regression-test-first fixes, analyzer sweeps, read-only exploration, branching/tagging |
| `skills/build-feature` | Executes plan steps; reference catalog of Zyggy code shapes (seam + fake + real, `PollState`, reason enum, Node service, CLI verb, Hub tool, tests) |
| `skills/bus-protocol` | §4/§5/§14 as a checklist for any code touching the bus |
| `skills/integration-testing` | Bare-repo + fake-claude harness, gate scenarios |
| `skills/add-public-api`, `skills/fix-violations` | New `Zyggy.Core` public types; analyzer cleanup |
| `instructions/tests.md`, `public-api.md`, `publishing.md` | Path-scoped conventions (xUnit/FluentAssertions/NSubstitute; `Zyggy.Core` API; csproj + single-file publish) |
| `templates/spec-template.md`, `plan-template.md` | Formats for `_specs/<NN>-…` and `_plans/<NN>-…` |
