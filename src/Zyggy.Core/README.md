# Zyggy.Core

The shared class library. Every other project (`Zyggy.Node`, `Zyggy.Cli`, `Zyggy.Hub`) references it; it references nothing in the solution. If a rule about the bus, an envelope, a job, or a secret exists, it is implemented here once and consumed by the hosts.

## Responsibilities

| Folder | What lives here |
|--------|-----------------|
| `Tenancy/` | `TenantId`, `UserId`, `MachineName` (validated lowercase labels, no default tenant) and `Principal`. Every tenant-scoped API takes one of these, never a raw string. |
| `Envelope/` | The envelope model (YAML front matter + Markdown body), `EnvelopeParser` (bytes in, typed envelope or a closed-enum rejection out, tenant checked first), `EnvelopeWriter`, ULID ids, and `EnvelopeSigner` — HMAC-SHA256 over the canonical form. `Canonicalize` is the single source of truth for what gets signed; `tests/golden` freezes its bytes. |
| `Bus/` | `BusPaths` (the only place a bus path is built or read; tenant-prefixed), `IBusProvider` with `GitHubBusProvider` (the only type that knows GitHub: the conditional head check through the system proxy, pull, push), `PollState` (the pure poller state machine), `BusRepository` (layout and the `jobs → claimed → reports / rejected` state moves with the §5 write sequence, retry and deferred commit), `BusListener` (the receiver's loop the probe verb hosts now and the Node service later), `Probe/` (the transport probe: handler, sender, ledger, stats, reachability matrix). |
| `Git/` | `GitClient`, the only place a git command line is built (spec 28), extended by the bus (move, pull --rebase, stash, ls-remote, dry-run push, config read, per-command commit identity). Runs through `IProcessRunner`; failures are results, never exceptions. |
| `Configuration/` | `NodeConfigurationReader`: the minimal `node.json` (spec 04) typed and validated — no default tenant, unknown keys ignored, any key naming a secret refused. |
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
| `IPolicySource` | the node's policy; v1 reads it from `node.json` (`NodeJsonPolicySource`, the MVP-scope decision), the signed `policy.yaml` comes later |

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

## Troubleshooting — Bus

The bus checkout is a plain git clone of the tenant's `zyggy-bus` repository; every change goes through the §5 write sequence (`pull --rebase` → change → one commit → `push`, retried after 1–3 s up to three times). Nothing here needs a reset. The entries below are the `bus-conflict.md` runbook draft until the runbook directory exists (deliverable 10). The founding-spec wording each entry implements is in parentheses (amendments A1–A5 of spec 04, pasted 2026-10-12).

| Situation | What you see | What to do |
|-----------|--------------|------------|
| Bus: dirty working copy at start-up | `BusStartupResult.Stashed`; one warning naming a stash `zyggy-<machine>-<timestamp>` | `git stash list` in the checkout, inspect with `git stash show -p`, then `git stash drop` or `git stash pop`. Zyggy never resets. |
| Bus: checkout | `WrongBranch` / `NotARepository` / `NoTenantDirectory` / `RebaseInProgress`; the probe verbs exit 3 (configuration) or 6 with the condition | Clone `bus.remote` to `bus.localPath`; `git switch main`; a missing `tenants/<tenant>/` means the repository was never seeded — run the create-the-repository steps (spec 04 AC-36). `RebaseInProgress`: `git rebase --abort`, then report which two files conflicted (by the write rules, two machines never touch the same file). |
| Bus: CRLF conversion (Windows) | Every job arrives as `invalid_signature`; the sender's probe times out although the receiver rejected it | Git for Windows ships `core.autocrlf=true`, which rewrites the envelope bytes on commit. The bus repository must carry a `.gitattributes` with `* -text`; until it does, `git -C <bus.localPath> config core.autocrlf false` on every Windows checkout, then `git checkout -- .`. |
| Bus: push deferred | `Deferred(4)`; one error "not pushed after 4 attempt(s); the commit stays local"; the probe records `push_failed`; the next write logs "pushed N deferred commit(s)" (A3/A4) | `git log origin/main..HEAD` shows the waiting commit(s); `zyggy probe reach` tells whether `push` is `ok` on this machine; check the credential helper state. Nothing to repair: the next successful write carries it. |
| Bus: pull failed | `WriteFailed("pull", …)`; one error; nothing changed; the next tick retries | Network or authentication: `zyggy probe reach`. If `.git/rebase-merge` exists, `git rebase --abort` and report the file pair — a content conflict is impossible by the write rules. |
| Bus: rate limit | `Throttled` with the instant polling resumes; one warning (A5: back-off until the later of 120 s, the host's reset time and `retry-after`) | All tokens of one GitHub account share 5,000 requests per hour; conditional `304` answers are free. Look for a runaway poller (the `User-Agent` is `zyggy/<version>`). |
| Bus: rejected envelopes | `jobs/rejected/<id>.md` and a `status: rejected` report whose `reason` is the rejection's wire name (A1: `malformed`, `tenant_mismatch`, `schema_unsupported`, `key_id_tenant_mismatch`, `missing_field`, `invalid_field`, `missing_signature`, `unknown_key`, `invalid_signature`, `wrong_recipient`); one warning with the file name and reason, never the body | `invalid_signature`/`unknown_key`: compare `hmac/<n>` across machines (same bytes, same `<n>`) and check `core.autocrlf`; `tenant_mismatch`/`wrong_recipient`: check `tenant` and `machine` in `node.json`; `schema_unsupported`: upgrade the binary. A file whose sender cannot be read is moved without a report. |

## Troubleshooting — Jobs

The job runner (deliverable 06) answers every claimed job with a signed report; it never leaves one unanswered and never throws to the poll loop. The log lines carry the scope `tenant`, `machine`, `phase=run`, `jobId` and never the prompt, the job body or the summary. The full `stream-json` of every run is kept on the executing machine at `<nodeDir>/runs/<tenant>/<job id>.jsonl` for 30 days — read it first. The entries below are the runbook draft until the runbook directory exists (deliverable 10); the wording is spec 06's Failure modes table.

| Situation | What you see | What to do |
|-----------|--------------|------------|
| Jobs: unknown_project / unknown_agent | `status: rejected`, `reason: unknown_project` (body `unknown_project: <check>`) or `unknown_agent`; one warning `job <id>: rejected at pre-flight: <reason> (<check>)`; the model never started | `unknown_project`: the job must name an absolute path on this machine that is a git repository with `.claude/`; registry names arrive with deliverable 14. `unknown_agent`: `<project>/.claude/agents/<name>.md` must exist and the name match `[A-Za-z0-9._-]+`. |
| Jobs: locked | `status: failed`, `reason: locked`, body `locked: <project>/.claude/zyggy.lock`; one warning | Another run (or a hand-run `zyggy run`, deliverable 16) holds the project. Read the lock file's `job`/`pid`/`acquired` lines. A lock whose process died opens again by itself (the operating system releases the handle) — there is no age rule; resubmit the job. |
| Jobs: timeout | `status: timeout`, `reason: timeout`, body `timeout after <n> min` and the partial text; the `claude` process tree killed; one `finished timeout` line | Raise `timeout_minutes` on the job or `claude.defaultTimeoutMinutes` in `node.json`; the transcript's tail shows where it stalled. An unauthenticated CLI can wait on a login prompt — check `claude` is logged in as the service user. |
| Jobs: claude_error | `status: failed`, `reason: claude_error`, body `claude_error: <detail>`: `not_found` / `start_failed` (the executable), `no_result` (the stream ended without a result — killed, crashed, exit 143), `error_max_turns`, `error_during_execution`, `auth`, `rate_limit`, `runner_exception <type>` (one error line) | `not_found`/`start_failed`: `claude.path` in `node.json`, then `claude --version` as the service user. `no_result`: the transcript tail. `error_max_turns`: raise `claude.maxTurns`. `auth`: `claude login` on the machine. `rate_limit`: wait. `runner_exception`: a bug — the log line names the type; file a bugfix. |
| Jobs: transcript not written | One warning `transcript not written: <detail>` from the model runner (scoped with the job id); the run and its report are unaffected | Permissions on `<nodeDir>/runs/`, or the disk is full. |
