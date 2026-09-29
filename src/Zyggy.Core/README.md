# Zyggy.Core

The shared class library. Every other project (`Zyggy.Node`, `Zyggy.Cli`, `Zyggy.Hub`) references it; it references nothing in the solution. If a rule about the bus, an envelope, a job, or a secret exists, it is implemented here once and consumed by the hosts.

## Responsibilities

| Folder | What lives here |
|--------|-----------------|
| `Tenancy/` | `TenantId`, `UserId`, `MachineName` (validated lowercase labels, no default tenant) and `Principal`. Every tenant-scoped API takes one of these, never a raw string. |
| `Envelope/` | The envelope model (YAML front matter + Markdown body), `EnvelopeParser` (bytes in, typed envelope or a closed-enum rejection out, tenant checked first), `EnvelopeWriter`, ULID ids, and `EnvelopeSigner` — HMAC-SHA256 over the canonical form. `Canonicalize` is the single source of truth for what gets signed; `tests/golden` freezes its bytes. |
| `Bus/` | `BusPaths` (the only place bus paths are built; tenant-prefixed), `BusRepository` (layout and the `jobs → claimed → reports / rejected` state moves), `GitClient` (system `git` through a process runner), `GitHubPoller` (ETag polling as a pure `PollState` machine). |
| `Registry/` | `RegistryDocument` and the `DiscoveryScanner` that finds Claude Code projects, subagents and skills under a machine's dev roots. |
| `Jobs/` | `JobRunner` (pre-flight, lock, worktree, run, report), `ClaudeProcess` (reads `claude -p` stream-json), `PromptTemplate`, `ProjectLock`, `WorktreeManager`. Never throws to the caller: every failure becomes a report with a closed-enum `reason`. |
| `Memory/` | Read-only helpers over the Markdown memory store and the `ContextRanker` used by the Hub's `get_context`. |
| `Secrets/` | `ISecretStore` (tenant-scoped, names `zyggy/<tenant>/<name>`), `InMemorySecretStore` for tests, and `FileSecretStore` — the P0 store on every machine, registered with `AddFileSecretStore()`. The OS stores (Windows Credential Manager, libsecret, systemd credentials) arrive in deliverable 09. |

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

## Troubleshooting — Secrets: file store

`AddFileSecretStore()` keeps each key as one file. Signing and verification read it on every call; nothing is cached.

| Question | Answer |
|----------|--------|
| Where is a key? | `<root>/<tenant>/hmac/<n>`, e.g. `<root>/acme/hmac/1`. The default root is `%LOCALAPPDATA%\zyggy\secrets` on Windows and `$XDG_DATA_HOME/zyggy/secrets` or `~/.local/share/zyggy/secrets` on Linux; `FileSecretStoreOptions.RootDirectory` overrides it. |
| What is in the file? | The key as hexadecimal text on one line; surrounding whitespace and either case are accepted. Keys shorter than 32 bytes are refused as `unknown_key`. |
| How do I seed or reseed a key? | Linux: `mkdir -p -m 700 <root>/<tenant>/hmac && (umask 077; openssl rand -hex 32 > <root>/<tenant>/hmac/1)`. Windows: create the file with the same content, then restrict it to yourself: `icacls <file> /inheritance:r /grant:r "%USERNAME%:F"`. Every machine of the tenant needs the same bytes for the same `<n>`. |
| What permissions does Zyggy set? | Linux: file `0600`, directories it creates `0700`. Windows: a protected DACL with one entry, the current user, full control (`icacls <file>` shows a single `(F)` line and no `(I)` entries). Writes go to a temporary file with those permissions and are renamed over the key, so the key never exists with wider permissions. |
| `InvalidDataException: Secret file '<path>' does not contain ... hexadecimal characters` | The file is corrupt or was written in another format. Reseed it. The message names the path, never the content. |
| `InvalidOperationException: FileSecretStoreOptions.RootDirectory must be set` | The process has no user profile directory (a service without `HOME`). Set `RootDirectory` explicitly. |
| `UnauthorizedAccessException` / `IOException` | The root is not writable by the service account, or the file belongs to another user. Fix ownership; do not widen the permissions. |
