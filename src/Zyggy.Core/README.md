# Zyggy.Core

The shared class library. Every other project (`Zyggy.Node`, `Zyggy.Cli`, `Zyggy.Hub`) references it; it references nothing in the solution. If a rule about the bus, an envelope, a job, or a secret exists, it is implemented here once and consumed by the hosts.

## Responsibilities

| Folder | What lives here |
|--------|-----------------|
| `Envelope/` | The envelope model (YAML front matter + Markdown body), parser, ULID ids, and `EnvelopeSigner` — HMAC-SHA256 over the canonical form. `Canonicalize` is the single source of truth for what gets signed. |
| `Bus/` | `BusPaths` (the only place bus paths are built; tenant-prefixed), `BusRepository` (layout and the `jobs → claimed → reports / rejected` state moves), `GitClient` (system `git` through a process runner), `GitHubPoller` (ETag polling as a pure `PollState` machine). |
| `Registry/` | `RegistryDocument` and the `DiscoveryScanner` that finds Claude Code projects, subagents and skills under a machine's dev roots. |
| `Jobs/` | `JobRunner` (pre-flight, lock, worktree, run, report), `ClaudeProcess` (reads `claude -p` stream-json), `PromptTemplate`, `ProjectLock`, `WorktreeManager`. Never throws to the caller: every failure becomes a report with a closed-enum `reason`. |
| `Memory/` | Read-only helpers over the Markdown memory store and the `ContextRanker` used by the Hub's `get_context`. |
| `Secrets/` | `ISecretStore` and its per-OS implementations (Windows Credential Manager, libsecret, systemd credentials). |

## The five seams

The library defines the interfaces behind which every external system hides. Each has exactly one implementation in v1 and nothing outside the implementation may reference the external system directly.

| Interface | Hides |
|-----------|-------|
| `IBusProvider` | GitHub (poll endpoint + git remote) |
| `IModelRunner` | the Claude Code CLI |
| `ISecretStore` | the OS secret store |
| `INotifier` | Telegram |
| `IPolicySource` | the signed `policy.yaml` |

Plus `IProcessRunner`, the single path to the `git` and `claude` processes, so tests can substitute both.

## Where the rules come from

Founding spec `_specs/00 - Personal Agent Platform — Technical Specification.md`: §4 bus protocol, §5 transport, §6 job execution, §7 memory, §8 security, §9 design rules, §14 tenancy and versioning. Conventions for this project: `.claude/instructions/public-api.md`.

Tests: `tests/Zyggy.Core.Tests` (unit, mirrors these folders) and `tests/golden` (canonical envelopes and expected signatures).
