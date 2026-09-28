# gen/0 — Inventory of the host genome

Date: 2026-09-28
Score: —
Status: settled (baseline; nothing to revert to)

This generation makes no behavioural change. It records what the working agent is made of at the moment the evolve plugin was initialised, adds the owner's protected constraints to `CLAUDE.md`, and creates the agent's memory: `memory/short-term.md` (working notes, consolidated at every generation) and `memory/long-term.md` with `memory/long-term/` (durable memories that strengthen when recalled and are forgotten when unused).

## Genome files

### Instructions

| File | Purpose |
| --- | --- |
| `CLAUDE.md` | CLAUDE.md |
| `memory/long-term.md` | Long-term memory |
| `memory/short-term.md` | Short-term memory |
| `public-api.md` | Public API design conventions for Zyggy.Core, the class library consumed by Zyggy.Node, Zyggy.Cli and Zyggy.Hub: XML documentation, internal-and-sealed by default, guard clauses, CancellationToken, the five seam interfaces, closed enums, records for values. Activates when editing Zyggy.Core source files. |
| `publishing.md` | Project-file and publishing conventions for Zyggy: shared settings live in Directory.Build.props, hosts publish as single-file self-contained binaries (zyggy-node, zyggy, zyggy-hub) for win-x64 and linux-x64, versions come from git tags. Activates when editing .csproj files. |
| `tests.md` | Use when writing, reviewing, or generating unit or integration tests for Zyggy. Covers xUnit + FluentAssertions + NSubstitute conventions, AAA structure, folder layout, golden-file tests, the fake-clock rule, and the bare-repo + fake-claude integration harness. |

### Sub-agents

| File | Purpose |
| --- | --- |
| `bugfix.md` | Fix a bug with a regression test first. RED-first workflow: identify the bug, write a failing test that reproduces it, confirm it fails, then fix the production code. Scoped to <= 2 file changes; escalate larger fixes to the planner agent. |
| `code-analysis.md` | Fix compiler diagnostics and analyzer findings with modern .NET 10 / C# 14 best practices. Prefer the native compiler, SDK analyzers, and idiomatic code over older StyleCop-centric workflows. |
| `explore.md` | Fast read-only codebase exploration and Q&A for Zyggy. Use when you need to find files, trace a behavior across Core/Node/Cli/Hub, understand how a bus operation or job run works, or answer questions about the codebase and the founding spec. Never modifies files. |
| `git.md` | Git workflow specialist for the Zyggy repository. Handles branches, PRs, tagging releases, hotfixes, resolving merge/rebase conflicts, and the branching/versioning strategy on GitHub. |
| `planner.md` | Planning specialist for Zyggy. Creates _plans/<NN>-<Deliverable>.md at the repo root with step-by-step Red-Green-Refactor vertical-slice cycles from an approved _specs/<NN>-<Deliverable>.md. Use for any change triggering the planning gate (>= 3 files, new deliverable, Risk Area). NEVER writes production code. |
| `project-manager.md` | Project manager for Zyggy. Owns the backlog in _plans/ROADMAP.md: derives deliverables from the founding spec's components and delivery phases (P0–P5), orders them by dependency, tracks status, decides what to build next, and frames the hand-off to the technical-analyst. Coordinates high-level planning only — never writes production code, specs, or detailed step plans. |
| `technical-analyst.md` | Technical analyst for Zyggy. Turns one roadmap deliverable (project-manager hand-off) into a buildable specification at _specs/<NN>-<Deliverable>.md for the planner. Reads the founding spec's contract sections, challenges each feature's added value and design, prefers well-maintained libraries over bespoke code, and records an Open Question whenever in doubt instead of assuming. Never writes code, plans, or roadmaps. |

### Skills

| File | Purpose |
| --- | --- |
| `add-public-api/SKILL.md` | Add a new public type, interface, or method to Zyggy.Core (the library consumed by Zyggy.Node, Zyggy.Cli and Zyggy.Hub) with full test coverage and XML documentation. Follows Red-Green-Refactor per step with HUMAN GATE. Use when adding a new seam interface, implementation, options record, or extension method to the library's API surface. |
| `build-feature/SKILL.md` | Use when implementing a deliverable or vertical slice in Zyggy after a _plans/<NN>-<Deliverable>.md (repo root) is approved. Executes plan steps using Red-Green-Refactor. Each plan step is a vertical behavior slice that may touch Core, Node, Cli, Hub and tests. Provides code templates as a reference catalog: seam interface + fake + real implementation, options/config binding, Node BackgroundService, CLI verb, Hub MCP tool, xUnit unit test, integration test against bare repo + fake claude, golden-file test. |
| `bus-protocol/SKILL.md` | Use when creating, modifying, or reviewing anything that reads or writes the Zyggy bus: envelope fields, canonical form and HMAC signing, ULID file names, tenant-prefixed paths via BusPaths, the job state machine (jobs → claimed → reports / rejected), write rules per machine, the git write sequence and push-retry rule, the registry file, and schema versioning. Checklist form of founding spec §4/§5/§14. Use for: adding an envelope field, a new state move, a new CLI verb that submits or reads, a Hub tool that touches the bus, or reviewing a PR for protocol drift. |
| `fix-violations/SKILL.md` | Use when fixing compiler diagnostics and analyzer findings in Zyggy with modern .NET 10 / C# 14 best practices. Prefer the native compiler, SDK analyzers, and idiomatic code — this repo uses no StyleCop. |
| `integration-testing/SKILL.md` | Use when writing or running Zyggy integration tests in tests/Zyggy.Integration: the local bare-git-repo bus harness, the tools/fake-claude script, real GitClient/ClaudeProcess over Process, Node poll-loop round trips, CLI verb invocations, and the phase gate scenarios under Gates/P<n>_*.cs. Use for: proving a wire step, scripting a §12 gate, debugging a failing round trip, asserting on bus files and commits. |

### Commands

| File | Purpose |
| --- | --- |
| `new-feature.md` | Build a roadmap deliverable end-to-end: project-manager hand-off → technical-analyst spec → planner plan → build-feature implementation gate to gate, stopping only at 🛑 HUMAN GATEs. |

### Templates

| File | Purpose |
| --- | --- |
| `plan-template.md` | Plan: <NN> — <Deliverable Name> — <User Story> |
| `spec-template.md` | Spec: <NN> — <Deliverable Name> |

### Tools

(none)

Retired:
- nothing

Declined to change:
- nothing
