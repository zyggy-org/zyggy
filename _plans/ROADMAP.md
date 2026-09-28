# Zyggy Roadmap

> Owned by the project-manager agent. High-level deliverables only —
> detailed Red-Green-Refactor steps live in `_plans/<NN>-<Deliverable>.md` (planner),
> contracts and acceptance criteria in `_specs/<NN>-<Deliverable>.md` (technical-analyst).
>
> Status values: `Not started` · `Next` · `Planning` · `In progress` · `Blocked` · `Done`

## How to read this file

- A **deliverable** is one working, verifiable capability: a vertical slice that ends in an observable round trip (an envelope signed and verified; a job claimed and reported against a local bare repo; the Hub answering `get_context`). It is never a project, a folder, a layer, or an RGR step.
- Deliverables are numbered `01..NN` in **build order**. A deliverable is schedulable only when every entry in its *Depends on* column is `Done`. One deliverable is in flight at a time.
- Every deliverable cites the founding-spec sections that are its contract (`_specs/00 - Personal Agent Platform — Technical Specification.md`, referred to below as "§n"), the §9/§14 design rules that bite, the §12 gate it serves, and a Definition of Done.
- Downstream file names follow the deliverable: `_specs/<NN>-<Deliverable>.md` and `_plans/<NN>-<Deliverable>.md`, where `<Deliverable>` is the kebab-case short name from the *File name* column.
- Status is reconciled against `src/`, `tests/`, and the plan-file checkboxes every session. `Done` is set only when the plan's final HUMAN GATE is checked **and** the Definition of Done holds; for a phase-closing deliverable the gate scenario must exist under `tests/Zyggy.Integration/Gates/P<n>_*.cs`.
- Naming: the spec uses working names `AgentBus.*`, `agentbus`, `agent-bus`, `agent-core`; code and this roadmap use the product names `Zyggy.*`, `zyggy`, `zyggy-bus`, `zyggy-core` (§9 Naming).

## Phase map (proposed — §12 diagram did not survive the export)

The §12 roadmap diagram is missing from the Markdown export. The phase contents below are derived from the §12 text ("P0 has no dependency on any machine; P1–P3 can run on Central plus the home laptop alone; P4 waits on the security answer and can slip without blocking P5"), the §3 component list and the §13 answers. **The user confirms or corrects this map at the roadmap gate.**

| Phase | Theme | Machines needed | Gate (a working round trip, not a code review) | Deliverables |
|-------|-------|-----------------|------------------------------------------------|--------------|
| P0 | Core contracts | none | A signed job dropped into a local bare bus repo is claimed, executed by the fake `claude`, and reported — entirely through `Zyggy.Core`, no host process. A tampered envelope ends in `jobs/rejected/` with a `status: rejected` report. | 01–06 |
| P1 | Node loop on real machines | Central VM + home laptop | `zyggy submit` on Central produces a report from `home-laptop` in `reports/` on `zyggy-bus`; `/health` on both machines is green; the Node runs as a service. | 07–10 |
| P2 | Registry and real execution | Central + home laptop | Central's `delegate` skill picks a project from `registry/home-laptop.yaml`, the node runs the real `claude -p` in a worktree, the branch is pushed and the report carries `diff_ref`; a stale claim is re-queued by the hourly sweep. | 11–14 |
| P3 | Memory and Hub | Central + home laptop | A `context` envelope from the home laptop reaches durable memory after a dream pass; a Claude Code session on Central receives it through `get_context`; a laptop project gets context through `zyggy hub --proxy`. | 15–18 |
| P4 | Work node | + work laptop | The work node runs under a signed `policy.yaml`, a report containing mail content is replaced by `status: failed`, `reason: dlp_filter`, and the metadata-only mail summary reaches Central. | 19–21 |
| P5 | Personal mail, notifications, dream | Central | Nightly dream consolidates inbox and bus into memory and sends a Telegram diff when identity files change; personal mail triage (Gmail + Outlook.com) lands as a summary; alerts from §11 reach Telegram. | 22–25 |

Infra deliverables (Central VM, systemd units, `install.ps1`, `zyggy-bus` repo) are slotted where a phase gate first needs a real machine (P1, deliverable 10).

## Overview

| # | Deliverable | File name | Phase | Projects | Depends on | Status | Spec | Plan |
|---|-------------|-----------|-------|----------|------------|--------|------|------|
| 01 | Solution builds and tests end-to-end with the test harness | `01-solution-scaffolding` | P0 | slnx, all csproj, `tools/fake-claude`, `tests/`, CI | — | **Next** | — | — |
| 02 | Envelope parse, canonicalise, sign and verify | `02-envelope-signing` | P0 | Zyggy.Core, Core.Tests, `tests/golden` | 01 | Not started | — | — |
| 03 | Git write sequence against a local bare repo | `03-git-client` | P0 | Zyggy.Core, Core.Tests, Integration | 01 | Not started | — | — |
| 04 | Bus layout and job state moves (claim, report, reject) | `04-bus-state-moves` | P0 | Zyggy.Core, Core.Tests, Integration | 02, 03 | Not started | — | — |
| 05 | Poller decides when to pull (ETag state machine) | `05-poller-state-machine` | P0 | Zyggy.Core, Core.Tests | 01 | Not started | — | — |
| 06 | Job runner executes a job with the fake claude and reports (closes P0) | `06-job-runner` | P0 | Zyggy.Core, Core.Tests, Integration (`Gates/P0_*`) | 04, 05 | Not started | — | — |
| 07 | Node service runs the poll → claim → run → report → push loop | `07-node-poll-loop` | P1 | Zyggy.Node, Zyggy.Core, Integration (`Gates/P1_*`) | 06 | Not started | — | — |
| 08 | `zyggy submit`, `status`, `verify` | `08-cli-submit-status-verify` | P1 | Zyggy.Cli, Zyggy.Core, Integration | 06 | Not started | — | — |
| 09 | HMAC keys and PAT from the OS secret store | `09-os-secret-stores` | P1 | Zyggy.Core, Core.Tests | 02 | Not started | — | — |
| 10 | Central VM, `zyggy-bus` repo, services on both machines (closes P1) | `10-p1-infrastructure` | P1 | infra, `install.ps1`, systemd units, runbooks | 07, 08, 09 | Not started | — | — |
| 11 | Discovery publishes `registry/<machine>.yaml` | `11-discovery-registry` | P2 | Zyggy.Core, Zyggy.Node, Zyggy.Cli, Integration | 07, 08 | Not started | — | — |
| 12 | Worktree execution, pushed branch, `zyggy run` | `12-worktree-execution` | P2 | Zyggy.Core, Zyggy.Cli, Integration | 11 | Not started | — | — |
| 13 | Central sweep re-queues stale claims and archives | `13-central-sweep` | P2 | Zyggy.Core, Zyggy.Cli, Integration | 07 | Not started | — | — |
| 14 | `zyggy-core` v1: PROTOCOL.md, prompt template, bus/delegate/discover skills, PreToolUse hook (closes P2) | `14-zyggy-core-skills` | P2 | `zyggy-core` repo, Integration (`Gates/P2_*`) | 12, 13 | Not started | — | — |
| 15 | Hub answers `get_context` and `remember` over stdio | `15-hub-context-remember` | P3 | Zyggy.Hub, Zyggy.Core, Core.Tests, Integration | 06 | Not started | — | — |
| 16 | Hub answers `list_nodes`, `submit_job`, `job_status`; HTTP transport | `16-hub-bus-tools` | P3 | Zyggy.Hub, Zyggy.Core, Integration | 15, 11 | Not started | — | — |
| 17 | Laptop projects get context via `zyggy hub --proxy`; `zyggy context` emits context envelopes | `17-hub-proxy-context-envelopes` | P3 | Zyggy.Cli, Zyggy.Core, Integration | 16 | Not started | — | — |
| 18 | Memory skills and hooks: dream, remember, SessionStart, Stop (closes P3) | `18-memory-skills-hooks` | P3 | `zyggy-core` repo, Zyggy.Cli, Integration (`Gates/P3_*`) | 17, 14 | Not started | — | — |
| 19 | Policy as data: signed `policy.yaml` governs every node | `19-policy-as-data` | P4 | Zyggy.Core, Zyggy.Node, Zyggy.Cli, Integration | 10, 13 | Not started | — | — |
| 20 | DLP filter and hook enforcement on the work node | `20-dlp-work-boundary` | P4 | Zyggy.Core, Zyggy.Node, `zyggy-core`, Integration | 19 | Not started | — | — |
| 21 | Work mail triage: metadata-only summaries, drafts only (closes P4) | `21-work-mail-triage` | P4 | `zyggy-core` repo, infra (work laptop), Integration (`Gates/P4_*`) | 20, 18 | Not started | — | — |
| 22 | Telegram notifier and §11 alerts | `22-telegram-alerts` | P5 | Zyggy.Core, Zyggy.Cli, Core.Tests, Integration | 13 | Not started | — | — |
| 23 | Personal mail triage on Central (Gmail + Outlook.com) | `23-personal-mail-triage` | P5 | `zyggy-core` repo, infra (Central `.mcp.json`) | 18, 22 | Not started | — | — |
| 24 | Nightly dream, cost tracking and budget enforcement (closes P5) | `24-dream-costs-budget` | P5 | Zyggy.Core, Zyggy.Cli, infra, Integration (`Gates/P5_*`) | 22, 23, 19 | Not started | — | — |
| 25 | Reproducible install: `zyggy init`, join token, Central image, `zyggy diagnose` | `25-reproducible-install` | post-P5 (§14) | Zyggy.Cli, infra | 24 | Not started (priority to confirm) | — | — |

## Deliverable details

### 01. Solution builds and tests end-to-end with the test harness
- **Goal**: `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx` and `dotnet format Zyggy.slnx --verify-no-changes` are green from a clean clone; the integration harness can create a bare bus repo plus clone and the fake `claude` emits a canned `stream-json` scenario — so every later deliverable starts from RED on a working loop.
- **Scope**: project references (`Node`/`Cli`/`Hub` → `Core`; `Core.Tests` → `Core`; `Integration` → `Core`, `Node`, `Cli`); FluentAssertions and NSubstitute in both test projects; central package management; `.editorconfig` for `dotnet format`; removal of template placeholders (`Class1.cs`, `UnitTest1.cs`, hello-world `Program.cs` bodies); `tools/fake-claude/` (`.ps1` + `.sh`, `scenarios/done|no-report|hang|error.jsonl`, argument capture file); `tests/golden/` folder with a README stating the golden-file contract; `tests/Zyggy.Integration/Infrastructure/BusRepoFixture` with one smoke test that pushes a commit to the bare repo and reads it back; CI workflow running build + test + format. Source layout folders per §9 (`Envelope/`, `Bus/`, `Registry/`, `Jobs/`, `Memory/`, `Secrets/`) may be created empty.
- **Spec sections**: §9 solution structure, §9 Packages, §12 Definition of done (CI), CLAUDE.md build/test commands, `integration-testing` skill (harness shape).
- **Depends on**: —
- **Design rules that apply**: `TreatWarningsAsErrors` stays on for every project; no test may invoke the real `claude` (the fake is the only runner); packages limited to the §9 table plus test tooling.
- **Gate served**: prerequisite for the P0 gate.
- **Definition of done**: three commands green locally and in CI; fake-claude `done` scenario runs on Windows and Linux and records its arguments; smoke integration test green; `README` per project no longer describes the template; CLAUDE.md statement "Zyggy.slnx is empty" flagged to the user for correction (the slnx already lists all six projects; only references and packages are missing).
- **Risks**: new NuGet dependencies (FluentAssertions license change in v8 — pin a compatible version or record the choice; NSubstitute); the real `stream-json` shape must be captured once from a real `claude -p` run outside the test suite by the user and checked in as `scenarios/done.jsonl` — until then the fixture uses a documented placeholder and 06 depends on the capture.
- **Status**: Next

### 02. Envelope parse, canonicalise, sign and verify
- **Goal**: `Zyggy.Core` parses any §4 envelope (YAML front matter + body) into a typed model, produces the canonical signing input, signs with a key from `ISecretStore`, and verifies — rejecting missing, unknown-key, invalid signatures and unsupported `schema` — with golden files proving the canonical bytes never drift.
- **Scope**: `Envelope`, `EnvelopeType`, `EnvelopeParser` (unknown fields preserved on round trip), `EnvelopeSigner.Canonicalize` + HMAC-SHA256, `Ulid` file names, `schema: 1`, `tenant`, `key_id = <tenant>/<n>`, key rotation (accept `k1` and `k2`), `ISecretStore` seam with an in-memory implementation for tests. Real OS stores are 09.
- **Spec sections**: §4 Envelope, §4 Signature, §9 Versioning (`schema: 1`, `schema_unsupported`), §14 Tenancy (tenant field, per-tenant keys), §8 Secrets (abstraction only).
- **Depends on**: 01
- **Design rules that apply**: `EnvelopeSigner.Canonicalize` is the single source of truth with golden-file tests under `tests/golden/`; `ISecretStore` seam from the first commit — nothing outside its implementation touches a key store; `YamlDotNet` with sorted keys; no static state.
- **Gate served**: P0.
- **Definition of done**: unit tests for parse/round-trip/sign/verify; golden files (input envelope, canonical bytes, expected signature for a test key) for job, report and context envelopes; verification rejects tampering of any front-matter key or body byte; `PROTOCOL.md` field list drafted from the implemented model (lands in 14) and any deviation from §4 raised as an Open Question in the spec.
- **Risks**: signing and canonical form are a shared contract between every machine — any later change is a protocol change; §4 example, §9 and §14 disagree on `key_id` format and on which fields are mandatory (see Open questions O1).
- **Status**: Not started

### 03. Git write sequence against a local bare repo
- **Goal**: `GitClient` performs the §5 write sequence (`pull --rebase` → change → `commit` → `push`, retry up to 3 times with 1–3 s jitter on push rejection, then leave uncommitted and log once) and the start-up dirty-tree stash, against a real bare repo, with the system `git` invoked only through `IProcessRunner`.
- **Scope**: `IProcessRunner` + `ProcessRunner`, `GitClient` (status, stash, pull --rebase, add, mv, rm, commit with per-machine identity, push, fetch, rev-parse), push-rejection retry, commit message contract `<type> <id> <from>→<to>`.
- **Spec sections**: §5 Git operations, §9 design rules (Process, never LibGit2Sharp), §11 Logging (once per state change).
- **Depends on**: 01
- **Design rules that apply**: git through `IProcessRunner` only; no credentials handled in code; commit identity `zyggy (<machine>) <machine>@zyggy.org`; log once per state change.
- **Gate served**: P0.
- **Definition of done**: unit tests with a substituted `IProcessRunner` for the retry/stash decision logic; integration tests against `BusRepoFixture` for happy push, rejected push with a foreign commit (retry succeeds), three rejections (change left uncommitted, error logged once), dirty tree stashed on start-up; runbook draft `bus-conflict.md`.
- **Risks**: none security-related; the retry behaviour is a shared contract with the state machine in 04.
- **Status**: Not started

### 04. Bus layout and job state moves (claim, report, reject)
- **Goal**: `BusRepository` claims a job addressed to this machine (`git mv` to `jobs/claimed/`), writes a report and deletes the job, and rejects a mis-addressed or badly signed envelope into `jobs/rejected/` with a `status: rejected` report — every path produced by tenant-prefixed `BusPaths`, every move one commit on the bare repo's `main`.
- **Scope**: `BusPaths` (tenant, machine, state, id, registry, policy, archive), `BusRepository` (list new jobs for me, claim with the "retry once, else skip" rule, report + `git rm`, reject, write context, read reports), write-rule enforcement (a machine writes only its own folders), signature verification before anything else.
- **Spec sections**: §4 Repository layout, Write rules, State machine, §14 Tenancy (`tenants/<org>/` prefix), §8 Injection (unsigned/mis-addressed rejected before any model call).
- **Depends on**: 02, 03
- **Design rules that apply**: all paths via `BusPaths` (grep for `"nodes/"`, `"jobs/"`, `"tenants/"` outside it is a violation); one `git mv`/add+rm per commit; two writers never touch the same file.
- **Gate served**: P0.
- **Definition of done**: unit tests for `BusPaths` and write-rule checks; integration tests seeding the layout with production `BusPaths` and asserting on the bare repo (`git show main:<path>`, commit subject) for claim, report, reject-bad-signature, reject-wrong-`to`, reject-`schema` too high, skipped claim after push rejection; `bus-protocol` skill checklist passes.
- **Risks**: shared §4 contract; `archive/` location and report file naming are ambiguous in §4 (Open questions O2, O3).
- **Status**: Not started

### 05. Poller decides when to pull (ETag state machine)
- **Goal**: given a sequence of HTTP outcomes and a fake clock, the poller transitions a `PollState` record exactly as §5 prescribes — 10 s active / 60 s idle with ±2 s jitter, 120 s back-off until `x-ratelimit-reset` on 403/429 or low remaining, exponential 10 s → 5 min on network error, an unconditional safety fetch every 15 min — and signals "pull now" only on a 200 with a new ETag.
- **Scope**: `PollState` record (etag, interval, backoffUntil, lastSafetyFetch), pure transition function, `GitHubPoller` over an injected `HttpMessageHandler` with the §5 headers, `IBusProvider` seam (cheap head check + fetch) with `GitHubBusProvider` as v1 implementation, work-hours evaluation from options, system-proxy honouring.
- **Spec sections**: §5 Poll loop, §5 Credentials (read PAT via `HttpClient` only), §9 design rules (pure state machine), §14 Pluggable edges (`IBusProvider`), §11 Logging.
- **Depends on**: 01
- **Design rules that apply**: pure `PollState` + injected HTTP; `TimeProvider`/fake clock; `IBusProvider` is the only place that knows GitHub; log once per state change, 304 silent; one in-flight request.
- **Gate served**: P0.
- **Definition of done**: unit tests driving the transition function through every §5 rule with a fake clock; `GitHubPoller` tests with a mocked `HttpMessageHandler` asserting headers (`Authorization`, `If-None-Match`, `User-Agent: zyggy/<version>`) and rate-limit parsing; no network in any test.
- **Risks**: `IBusProvider` is a shared seam (§5 poller contract); PAT is obtained through `ISecretStore` (02) — never from options.
- **Status**: Not started

### 06. Job runner executes a job with the fake claude and reports (closes P0)
- **Goal**: given a claimed job, `JobRunner` runs pre-flight (project and agent resolved against `registry/<me>.yaml`, project lock), invokes `IModelRunner` (v1 `ClaudeCodeCliRunner` over `IProcessRunner` with the exact §6 command line and prompt template), streams `stream-json`, enforces timeout and the 2 MB cap, stores the transcript under `runs/<id>.jsonl`, and always yields a report — `done`, or `failed`/`timeout`/`rejected` with a `reason` from the closed enum — never an exception to the caller. The P0 gate scenario ties 02–06 together.
- **Scope**: `IModelRunner` seam + `ClaudeCodeCliRunner`, `ClaudeProcess` (stream-json reader, `result` event → `cost_usd`, `duration_ms`, `num_turns`, tokens, model), `PromptTemplate` (body as data, `<<< >>>` fence), `ProjectLock` (`FileMode.CreateNew`, 2 h staleness), `RegistryDocument` read model (minimal, for pre-flight), report assembly (`summary` ≤ 2,000 chars fallback, `files_changed`, `diff_ref` null until 12), `JobReason` enum with all nine members (`unknown_project`, `unknown_agent`, `locked`, `timeout`, `dlp_filter`, `claude_error`, `git_error`, `schema_unsupported`, `budget_exceeded`), `IPolicySource` seam declared with a static/default v1 stub (real policy in 19). Worktrees are 12. Gate: `tests/Zyggy.Integration/Gates/P0_CoreRoundTrip.cs`.
- **Spec sections**: §6 Pre-flight, Invocation, Streaming and limits, Report assembly, §9 design rules (closed enum, never throws), §14 Model runtime (`IModelRunner`, token/model fields on reports), §12 Definition of done.
- **Depends on**: 04, 05
- **Design rules that apply**: `claude` only through `IProcessRunner` inside `ClaudeCodeCliRunner`; `JobRunner` never throws; envelope body is data in the prompt template; no test calls the real `claude`; lock file named `zyggy.lock` per §9 naming.
- **Gate served**: **closes P0** — round trip through `Zyggy.Core` only: seed signed job in bare repo → claim → fake claude `done` → report on `origin/main`; second fact: tampered job → `jobs/rejected/` + `status: rejected`.
- **Definition of done**: unit tests for pre-flight, lock, template rendering, stream parsing, cap and timeout with `FakeTimeProvider`; integration tests for each fake-claude scenario (`done`, `no-report`, `hang` → `timeout`, `error` → `claude_error`) and for the invocation contract captured by the fake; `Gates/P0_CoreRoundTrip.cs` passing; runbook entries for `timeout` and `locked`; spec/PROTOCOL notes for report fields.
- **Risks**: §6 runner I/O is a shared contract between `core-dev` and `node-dev`; prompt-injection surface (template must treat body as data); the fake-claude `done.jsonl` must reflect the real `stream-json` shape (captured by the user in 01).
- **Status**: Not started

### 07. Node service runs the poll → claim → run → report → push loop
- **Goal**: `Zyggy.Node` hosted as a service (Windows service / systemd selected at runtime) reads `node.json`, polls through the poller, pulls, dispatches jobs (one per project, max 2 per machine, work hours unless `priority: high`), runs them through `JobRunner`, pushes reports, and exposes `http://localhost:4711/health` with the §11 fields — proven end-to-end against the bare repo, fake claude and a mocked GitHub head check.
- **Scope**: `PollLoop` (BackgroundService), `JobDispatcher`, `node.json` options model and validation (secrets forbidden), Serilog rolling file with `machine`/`jobId`/`phase` enrichment, health endpoint, `UseWindowsService()`/`UseSystemd()`, DI composition root exposing production extension methods the tests reuse. Gate scenario `Gates/P1_NodeRoundTrip.cs` (automated part of the P1 gate).
- **Spec sections**: §3 `AgentBus.Node`, §5 Poll loop (integration), §6 Concurrency, §10 `node.json`, §11 Logging and Health endpoint, §9 Packages (Hosting, Serilog).
- **Depends on**: 06
- **Design rules that apply**: everything via DI, no static mutable state; log once per state change; secrets never in `appsettings.json`/`node.json`; only `IProcessRunner`-visible paths and options are swapped in tests.
- **Gate served**: P1 (automated half; the physical round trip is 10).
- **Definition of done**: unit tests for dispatcher rules and options validation; `Gates/P1_NodeRoundTrip.cs` starts the real host through production DI against the bare repo + fake claude and ends with a report on `main` and a green `/health`; `node.json` schema documented; runbook entry for "dirty working copy on start-up" links to `bus-conflict.md`.
- **Risks**: new packages (Serilog, Hosting.WindowsServices/Systemd); health endpoint must bind to localhost only.
- **Status**: Not started

### 08. `zyggy submit`, `status`, `verify`
- **Goal**: from any machine with a bus checkout and keys, `zyggy submit` builds, signs and pushes a job envelope; `zyggy status [--id]` reports bus state (jobs, claimed, reports) and the local `/health`; `zyggy verify` re-checks every signature in the bus and reports drift — each with documented exit codes.
- **Scope**: System.CommandLine host, single-file publish profile (`zyggy`), verbs `submit`, `status`, `verify` (with `--costs` deferred to 24), shared options loading from `node.json`, in-process invocation harness for integration tests.
- **Spec sections**: §3 `AgentBus.Cli`, §8 Audit (`verify`), §9 Publish, §11 Health endpoint (`status`), §4 (sender writes only `nodes/<target>/jobs/`).
- **Depends on**: 06
- **Design rules that apply**: all bus access via `BusRepository`/`BusPaths`; signing via `EnvelopeSigner` + `ISecretStore`; CLI never handles git credentials.
- **Gate served**: P1.
- **Definition of done**: integration tests invoking the CLI in-process against the bare repo for each verb, including `verify` detecting a tampered file; `zyggy --version` works; publish for `win-x64` and `linux-x64` succeeds in CI.
- **Risks**: `submit` is the human entry point to the signing path — argument validation must not allow a body to leak into front matter.
- **Status**: Not started

### 09. HMAC keys and PAT from the OS secret store
- **Goal**: `ISecretStore` resolves the bus HMAC keys (`<tenant>/k1`, `<tenant>/k2`) and the GitHub PAT from Windows Credential Manager, libsecret or a 0600 file on Linux, and systemd `LoadCredential` on Central — and the Node refuses to start when the signing key is absent.
- **Scope**: `WindowsCredentialManagerSecretStore` (`Meziantou.Framework.Win32.CredentialManager`), `LibSecretSecretStore` / `FileSecretStore` (0600), `SystemdCredentialSecretStore`, store selection at runtime, `zyggy` helper to write a key into the store (or documented manual step), runbook `rotate-hmac.md`.
- **Spec sections**: §8 Secrets table, §4 Signature (rotation), §9 `Secrets/` folder and package, §14 Pluggable edges (`ISecretStore`).
- **Depends on**: 02
- **Design rules that apply**: nothing outside the implementations references a secret store; no secret in any file under the repo, in tests, or in logs.
- **Gate served**: P1.
- **Definition of done**: unit tests for selection and error mapping with a substituted backend; OS-conditional integration tests (`[Trait]` + skip on other OS) that round-trip a value through the real store; start-up failure mode logged once with a runbook pointer.
- **Risks**: secrets and a new NuGet dependency; Windows tests need the interactive user's store.
- **Status**: Not started

### 10. Central VM, `zyggy-bus` repo, services on both machines (closes P1)
- **Goal**: the Azure VM from §13 Q3 runs `zyggy-node.service` and `claude-remote.service` (with the Q4 resume wrapper); the home laptop runs `Zyggy.Node` as a Windows service installed by `install.ps1`; the private `zyggy-bus` repo under `zyggy-org` has per-machine PATs and deploy keys; and a job submitted on Central is reported by `home-laptop` on the real bus.
- **Scope**: `zyggy-bus` repo for tenant `geoffrey` (one repository per tenant, §4/§13 decision of 2026-09-29) with `tenants/geoffrey/` layout, `PROTOCOL.md` placeholder, and force-push/deletion blocked on `main`; per-machine PATs and deploy keys scoped to that repository only; VM provisioning (Ubuntu 24.04, Node 22, Claude Code CLI, Tailscale, `/srv/agent` volume, Azure Backup); systemd units with `LoadCredential=`; `install.ps1`; runbooks `restore-central.md`, `revoke-machine.md`; Q4 resume wrapper verified.
- **Spec sections**: §10 Central, Laptops, Upgrade path, §5 Credentials per machine, §8 Isolation, §13 Q3/Q4, §11 Runbooks.
- **Depends on**: 07, 08, 09
- **Design rules that apply**: Central unprivileged with only its own directory; `--permission-mode auto`, never `--dangerously-skip-permissions`; revoking one machine never affects the others; the repository is the tenant isolation boundary, so no credential on any machine may reach another tenant's repository.
- **Gate served**: **closes P1** — physical round trip Central → `home-laptop` → `reports/`; `Gates/P1_*` (07) green in CI.
- **Definition of done**: gate checklist executed and recorded in the plan's final HUMAN GATE; both `/health` endpoints report the version; runbooks exist; `node.json` on both machines contains no secret; spec §10 updated for anything that changed.
- **Risks**: secrets provisioning; first real exposure of the signing path across machines; cloud cost (§13 Q3).
- **Status**: Not started

### 11. Discovery publishes `registry/<machine>.yaml`
- **Goal**: on service start and hourly, the node scans `devRoots` for Claude Code projects (`.claude/`, agents, skills, `CLAUDE.md` summary, default branch) and publishes `tenants/<org>/registry/<machine>.yaml`, committing only when content differs (excluding `last_seen`, refreshed at most every 6 h) and including the health subset; `zyggy discover` does the same on demand.
- **Scope**: `DiscoveryScanner`, `RegistryDocument` writer, `DiscoveryTimer`, `zyggy discover` (and `--link` for shared skills), registry read by `JobRunner` pre-flight (replaces the minimal reader from 06 if needed).
- **Spec sections**: §4 Registry file, §3 `discover` skill, §10 Claude Code side (`discover --link`), §11 Health (registry subset).
- **Depends on**: 07, 08
- **Design rules that apply**: registry path via `BusPaths`; commit through `GitClient`; log once per change.
- **Gate served**: P2.
- **Definition of done**: unit tests for scanner and diff-excluding-`last_seen`; integration test showing one commit on first start and none on unchanged rescan; `zyggy discover` in-process test.
- **Risks**: none security-related; `dev_roots` later becomes policy-governed (19).
- **Status**: Not started

### 12. Worktree execution, pushed branch, `zyggy run`
- **Goal**: a job with `worktree: true` runs in `../<project>-zyggy-<id>` on branch `zyggy/<id>` from the default branch; on completion commits are pushed, `diff_ref` carries the SHA, `files_changed` is populated, and the worktree is removed; `worktree: false` runs in place and never commits. `zyggy run --job <file>` executes one job file for the optional Tailscale fast path while still landing the report on the bus. The real `claude -p` is exercised manually on the home laptop.
- **Scope**: `WorktreeManager`, `files_changed` from `git status --porcelain`, `diff_ref`, `zyggy run`, `git_error` reason path.
- **Spec sections**: §6 Pre-flight step 4, Report assembly, §5 Direct fast path, §3 `agentbus run`.
- **Depends on**: 11
- **Design rules that apply**: git via `GitClient`/`IProcessRunner`; `JobRunner` never throws (`git_error`); no test invokes the real `claude`.
- **Gate served**: P2.
- **Definition of done**: integration tests with a second bare repo playing the project's origin: branch pushed and SHA in report, no-commit case yields `diff_ref: null`, worktree removed on timeout; manual real-`claude` run recorded at the plan gate.
- **Risks**: writes into the user's real project repos — must be limited to the worktree and branch namespace `zyggy/`.
- **Status**: Not started

### 13. Central sweep re-queues stale claims and archives
- **Goal**: an hourly sweep on Central moves claims older than `timeout_minutes` + 15 min back to `jobs/` with `attempt: n+1`, writes a `status: failed` report after 3 attempts, and moves `reports/` and `context/` older than 14 days to `archive/YYYY-MM/` — all as auditable commits.
- **Scope**: sweep logic in `Zyggy.Core`, `zyggy sweep` verb (or equivalent entry point; see Open question O6), `attempt` field handling in 02's model, systemd `zyggy-sweep.timer` definition for 10's units.
- **Spec sections**: §4 State machine (rows 4–5), §7 Dream pass step 1 (archive), §11 Alerts "Job stuck" (evaluation only; delivery is 22), §10 systemd timers.
- **Depends on**: 07
- **Design rules that apply**: only Central writes `archive/`; every move one commit via `BusPaths`/`GitClient`; `TimeProvider` for age calculations.
- **Gate served**: P2.
- **Definition of done**: integration tests for re-queue, third-attempt failure, and archive with a fake clock; sweep is idempotent; runbook entry for "job failed 3 times".
- **Risks**: `archive/` path ambiguity (O2); `attempt` field absent from §4 example (O1).
- **Status**: Not started

### 14. `zyggy-core` v1: PROTOCOL.md, prompt template, bus/delegate/discover skills, PreToolUse hook (closes P2)
- **Goal**: the `zyggy-core` config repo exists with `PROTOCOL.md` matching §4 verbatim, `templates/job-prompt.md`, the `bus`, `delegate` and `discover` skills wrapping the CLI, the `PreToolUse` hook blocking tools outside `allowed_tools` and writes outside the worktree, and `CLAUDE.md` templates — pinned by SHA in each machine's `node.json`.
- **Scope**: `zyggy-core` repository content (Markdown + hook scripts), pin handling in `node.json`, `Gates/P2_DelegateWorktree.cs` (fake-claude automated part), manual P2 gate on real machines.
- **Spec sections**: §3 `agent-core`, Skills, Hooks, §6 Prompt template, §8 Isolation and Injection (hook rules), §9 Versioning (pin), §12 DoD (PROTOCOL.md).
- **Depends on**: 12, 13
- **Design rules that apply**: envelope bodies are data in every prompt; `PROTOCOL.md` must match §4; hook blocks `curl`/`wget`/`Invoke-WebRequest` unless allowed.
- **Gate served**: **closes P2** — `delegate` picks project from registry → worktree run → branch pushed → report with `diff_ref`; stale claim re-queued by sweep.
- **Definition of done**: `Gates/P2_*` green; `PROTOCOL.md` diffed against §4; hook tested with a scripted tool call on the home laptop; spec updated where behaviour changed; distribution mechanism decided (O5).
- **Risks**: shared contract (§4 verbatim copy); hook is a security control on the laptops.
- **Status**: Not started

### 15. Hub answers `get_context` and `remember` over stdio
- **Goal**: a Claude Code session on Central with `Zyggy.Hub` in `.mcp.json` receives ranked memory (alias/description match, then recency, under `max_tokens`) for a topic, and `remember` appends a tagged fact to `memory/<tenant>/<user>/inbox/` while refusing secret patterns.
- **Scope**: `MemoryStore` (read helpers over the §7 layout and front matter), `ContextRanker`, `Zyggy.Hub` with the `ModelContextProtocol` SDK over stdio, secret-pattern refusal, memory repo layout per §14.
- **Spec sections**: §7 Layout, File format, Hub MCP surface, Rules (secret refusal), §14 Tenancy (memory path), §9 Packages (MCP SDK).
- **Depends on**: 06
- **Design rules that apply**: Hub never writes durable files other than `inbox/`; tenant/user resolution rule (O7) respected; no static state.
- **Gate served**: P3.
- **Definition of done**: unit tests for ranking and token budget on a fixture memory tree; integration test driving the Hub through an in-process MCP client for both tools including a refused secret; `.mcp.json` example for Central.
- **Risks**: new NuGet dependency (MCP SDK); secret-pattern list is a data-protection control.
- **Status**: Not started

### 16. Hub answers `list_nodes`, `submit_job`, `job_status`; HTTP transport
- **Goal**: through the Hub, a session lists nodes from `registry/*.yaml` plus health, submits a signed job (same path as `zyggy submit`) and reads a job's state and report; the Hub also serves the same tools over HTTP for the laptop proxy.
- **Scope**: three MCP tools over `BusRepository`/`EnvelopeSigner`, HTTP transport bound to localhost/Tailscale only, shared composition with the CLI.
- **Spec sections**: §7 Hub MCP surface, §4 (sender rules), §11 Health (for `list_nodes`).
- **Depends on**: 15, 11
- **Design rules that apply**: bus access only via `BusRepository`; signing via `ISecretStore`; a chain longer than 3 hops requires confirmation (§8 Injection — record how the Hub tracks hop count).
- **Gate served**: P3.
- **Definition of done**: integration tests for each tool against the bare repo; HTTP transport smoke test; tool schemas documented for `zyggy-core` skills.
- **Risks**: `submit_job` exposes the signing path to MCP callers — input validation identical to the CLI.
- **Status**: Not started

### 17. Laptop projects get context via `zyggy hub --proxy`; `zyggy context` emits context envelopes
- **Goal**: on a laptop, a project `.mcp.json` pointing at `zyggy hub --proxy` answers `get_context` from a cached copy of the bus `context/` folder plus the project's `CLAUDE.md`, and `remember` (and `zyggy context`) emits a signed `context` envelope to `nodes/central/context/` instead of writing memory.
- **Scope**: `zyggy hub --proxy`, `zyggy context` and `zyggy report` verbs, context envelope writer (`scope`, one fact per line), cache refresh on pull.
- **Spec sections**: §7 Hub MCP (laptop paragraph), §4 Envelope (context fields), §3 `remember` skill (node side), §3 Stop hook (node side).
- **Depends on**: 16
- **Design rules that apply**: nodes never write durable memory; context path via `BusPaths`; body is data.
- **Gate served**: P3.
- **Definition of done**: integration tests: proxy returns project `CLAUDE.md` + cached context; `remember` via proxy lands a signed context envelope on the bare repo.
- **Risks**: none beyond the shared envelope contract.
- **Status**: Not started

### 18. Memory skills and hooks: dream, remember, SessionStart, Stop (closes P3)
- **Goal**: `zyggy-core` gains the `dream` and `remember` skills and the `SessionStart` (digest < 6k tokens) and `Stop` hooks, so a context envelope from the home laptop is ingested into `inbox/`, consolidated into durable files with provenance tags by the dream pass, and visible to the next session — the P3 round trip. Telegram diff review and the nightly timer are 24.
- **Scope**: skill/hook Markdown and scripts in `zyggy-core`; mechanical dream steps (bus ingestion into `inbox/`, `agents.md` refresh from registry, daily roll-up) as a testable `zyggy` verb if O6 is decided that way; `Gates/P3_MemoryRoundTrip.cs` for the mechanical parts.
- **Spec sections**: §7 Context loading, Dream pass steps 1–6, Rules, §3 Skills/Hooks, §14 memory path.
- **Depends on**: 17, 14
- **Design rules that apply**: only `[stated]`/`[observed]` tags; node facts are `[observed]`; never store secrets or mail bodies; work facts stay on the work node.
- **Gate served**: **closes P3**.
- **Definition of done**: `Gates/P3_*` green for ingestion and digest size; manual dream run on Central reviewed at the plan gate; spec §7 updated for any change.
- **Risks**: memory writes are the single durable state — dream must be reviewable (git diff) before 24 automates it.
- **Status**: Not started

### 19. Policy as data: signed `policy.yaml` governs every node
- **Goal**: Central signs `tenants/<org>/policy.yaml`; every node loads it through `IPolicySource`, refuses to run jobs when the signature fails or the policy is older than 30 days, and takes `work_hours`, `max_concurrent_jobs`, default `allowed_tools`, `dev_roots` and `dlp` rules from it — `node.json` may only tighten, never loosen.
- **Scope**: `IPolicySource` real implementation (`SignedPolicySource`), policy model, `zyggy policy sign|show` (Central), precedence rules over `node.json`, refusal logging.
- **Spec sections**: §14 Policy as data, §4 layout (`policy.yaml`), §9 five seams (`IPolicySource`), §8 Work boundary (expressed as data).
- **Depends on**: 10, 13
- **Design rules that apply**: policy signing reuses `EnvelopeSigner.Canonicalize` semantics or a documented variant (golden files if new); nothing outside `SignedPolicySource` parses the policy file.
- **Gate served**: P4.
- **Definition of done**: unit tests for precedence (tighten-only) and staleness with a fake clock; integration test: tampered policy → node refuses jobs and logs once; runbook entry "policy rejected".
- **Risks**: signing; a policy outage stops all nodes — failure mode must be explicit.
- **Status**: Not started

### 20. DLP filter and hook enforcement on the work node
- **Goal**: on a node whose policy enables DLP, any report body containing `Content-Type:`, a base64 blob > 1 KB, or more than 200 lines is replaced by `status: failed`, `reason: dlp_filter`; the `PreToolUse` hook applies the policy's tool rules; telemetry export is disabled by policy while collection remains.
- **Scope**: `DlpFilter` in `Zyggy.Core` driven by policy rules, wiring in `JobRunner` report assembly, hook rule source in `zyggy-core`, `dlp.enabled` in `node.json` as tighten-only override.
- **Spec sections**: §6 Report assembly (work laptop), §8 Work boundary, §14 Policy as data (`dlp` rules), §11 Optional telemetry.
- **Depends on**: 19
- **Design rules that apply**: `JobRunner` never throws; failure becomes a `dlp_filter` report; rules are data.
- **Gate served**: P4.
- **Definition of done**: unit tests per rule and for policy-driven configuration; integration test with a fake-claude scenario emitting mail-like content → `dlp_filter` report on the bare repo; runbook entry.
- **Risks**: work-laptop trust boundary (§8) — the filter is a hard requirement, not best effort.
- **Status**: Not started

### 21. Work mail triage: metadata-only summaries, drafts only (closes P4)
- **Goal**: on the work laptop, the `triage-mail` skill reads the inbox through the mail access the user already has (§13 Q2), classifies, creates Drafts only, keeps work facts in a separate `memory/work/` repo that never leaves the device, and emits a context envelope with only the §8 allowed fields (sender display name, subject, received date, one-line summary, requested action, deadline) marked `share: true` where appropriate.
- **Scope**: `triage-mail` skill (work profile), `memory/work/` repo setup, work-node `node.json` and policy, scheduled job submission via the local timer/task, `Gates/P4_WorkBoundary.cs` (DLP + policy automated part), manual gate on the work laptop.
- **Spec sections**: §8 Work boundary, §7 Rules (`share: true`), §3 `triage-mail`, §10 Work laptop, §13 Q1/Q2.
- **Depends on**: 20, 18
- **Design rules that apply**: no `mail.send`; no bodies, attachments, quoted text or recipient lists on the bus; tokens in Credential Manager only.
- **Gate served**: **closes P4**.
- **Definition of done**: `Gates/P4_*` green; manual check that a triage run produces Drafts, a metadata-only envelope, and nothing else on the bus; runbook for the work node; spec updated.
- **Risks**: work-laptop trust boundary; corporate proxy for both `HttpClient` and git (§5.7).
- **Status**: Not started

### 22. Telegram notifier and §11 alerts
- **Goal**: `INotifier` with `TelegramNotifier` (bot token from the systemd credential) delivers the §11 alerts — node silent, job stuck/re-queued, job failed 3×, signature failures, Claude auth expired — evaluated by the Central sweep from the bus, registry and health data.
- **Scope**: `INotifier` seam + Telegram implementation, alert evaluation in the sweep (13), de-duplication (one message per state change), `zyggy notify` test verb.
- **Spec sections**: §11 Alerts, §8 Secrets (Telegram token), §14 Pluggable edges (`INotifier`), §9 five seams.
- **Depends on**: 13
- **Design rules that apply**: nothing outside `TelegramNotifier` references Telegram; alerts defined against the interface; log once per state change.
- **Gate served**: P5.
- **Definition of done**: unit tests for each alert condition with a fake clock and a substituted `INotifier`; Telegram implementation tested against a mocked `HttpMessageHandler`; manual send verified at the plan gate.
- **Risks**: secret (bot token); outbound messaging must never carry envelope bodies or mail content.
- **Status**: Not started

### 23. Personal mail triage on Central (Gmail + Outlook.com)
- **Goal**: Central's `.mcp.json` wires Gmail and Outlook.com (personal Graph) MCP servers with refresh tokens from systemd credentials; a timer-submitted job runs `triage-mail`, merges both mailboxes into one summary, drafts replies as Drafts, and never sends.
- **Scope**: MCP server wiring and credentials on Central, `triage-mail` skill (personal profile), timer job definition, summary lands in `inbox/` via `remember`.
- **Spec sections**: §1 (no auto-send), §3 `triage-mail`, §8 Secrets (OAuth tokens), §13 Q5, §6 Central executing its own jobs.
- **Depends on**: 18, 22
- **Design rules that apply**: no automatic sending; mail content is untrusted data in prompts; secrets never in files under the repo.
- **Gate served**: P5.
- **Definition of done**: manual triage run reviewed at the plan gate (Drafts created, summary in memory, nothing sent); runbook for token refresh failure; alert on auth failure via 22.
- **Risks**: OAuth secrets; third-party MCP servers (record provenance and version).
- **Status**: Not started

### 24. Nightly dream, cost tracking and budget enforcement (closes P5)
- **Goal**: `zyggy-dream.timer` runs the dream nightly at 03:00 Europe/Brussels and sends a Telegram diff when `profile.md` or `preferences.md` changed; `zyggy status --costs 30d` sums `cost_usd` per machine and project from bus history; Central refuses new jobs with `reason: budget_exceeded` when `policy.budget_usd_month` is exceeded.
- **Scope**: timer units, dream step 7 wiring to `INotifier`, cost aggregation, budget check in `submit`/`submit_job`, `Gates/P5_DreamAndAlerts.cs` for the mechanical parts.
- **Spec sections**: §7 Dream pass step 7, §10 Central timers, §11 Cost tracking, §14 Model runtime (budget), §6 Central executing its own jobs.
- **Depends on**: 22, 23, 19
- **Design rules that apply**: dream commits are auditable; `budget_exceeded` is a closed-enum reason; only Central writes memory.
- **Gate served**: **closes P5**.
- **Definition of done**: `Gates/P5_*` green; one real nightly run observed with a Telegram diff; `--costs` output verified against bus history; spec and runbooks updated.
- **Risks**: automated memory rewrite without a human in the loop — the Telegram diff is the control.
- **Status**: Not started

### 25. Reproducible install: `zyggy init`, join token, Central image, `zyggy diagnose`
- **Goal**: `zyggy init --role node --tenant <org> --join <token>` provisions a node (HMAC key, bus credentials, pinned `zyggy-core` version) from a short-lived Central-issued token; `zyggy init --role central` bootstraps Central; Central ships as a Docker image plus a Bicep module and `docker compose`; `zyggy diagnose` produces a redacted support bundle; `zyggy-core` is published as a versioned zip with checksum.
- **Scope**: §14 Reproducible install and Telemetry-and-support items not already delivered. Scheduled after P5 unless the user promotes parts (e.g. `zyggy init` for the work laptop in P4).
- **Spec sections**: §14 Reproducible install, Telemetry and support, §10 Upgrade path.
- **Depends on**: 24
- **Design rules that apply**: secrets only through `ISecretStore`; bundle strips secrets and envelope bodies.
- **Gate served**: none in §12 — productisation shape (§14).
- **Definition of done**: fresh VM and fresh laptop reach a green `/health` with one command each; bundle reviewed for leaks; docs.
- **Risks**: join token is a secret-distribution mechanism (signing keys travel through it); infrastructure-as-code adds new tooling.
- **Status**: Not started (priority to confirm — see O9)

## Open questions for the user (founding-spec conflicts found while building this roadmap)

These are raised here so the technical-analyst does not have to rediscover them. Only the user amends the founding spec. The multi-tenancy review of 2026-09-28 (user-directed) amended §1–§14 for §14 compatibility; rows marked **Resolved** reflect that.

| # | Conflict / gap | Where | Affects | Proposed resolution |
|---|----------------|-------|---------|---------------------|
| O1 | The §4 envelope example lacks `schema`, `tenant`, `attempt`, `reason`, `input_tokens`, `output_tokens`, `model`, and shows `key_id: k1`, while §9 mandates `schema: 1`, §4 state machine uses `attempt`, and §14 mandates `tenant` and `key_id = <tenant>/<n>`. §4 must match `PROTOCOL.md` verbatim. | §4, §9, §14 | 02, 04, 13, 14 | **Resolved 2026-09-28 (tenancy review):** §4 example now carries `schema: 1`, `tenant`, `key_id: geoffrey/1`; report fields gained `reason`, `input_tokens`, `output_tokens`, `model`. Still open: `attempt` is not in the example; mandatory-per-type field list. |
| O2 | `archive/YYYY-MM/` appears at repo root in the §4 layout, but §14 says every bus path is prefixed `tenants/<org>/`. | §4, §14 | 04, 13 | **Resolved 2026-09-28:** §4 layout fixed, `archive/` is under `tenants/<org>/`; no cross-tenant move exists. |
| O3 | Report file naming: the state-machine row says `reports/<id>.md` (the job id?), while the write rule "file name = ULID" and `in_reply_to` suggest a fresh ULID per report. | §4 | 04, 06 | Fresh ULID per report/context file; `in_reply_to` carries the job id. |
| O4 | Claim push rejected: §4 says "pull, retry once, else skip"; §5 says retry at most 3 times with jitter for every write sequence. | §4, §5 | 03, 04 | §5 rule for all writes; a claim that still fails after retries is skipped (not failed) as §4 intends. |
| O5 | `zyggy-core` distribution: §9 says git submodule pinned by SHA; §14 says versioned zip + checksum, "no submodules". `node.json` has `agentCore.pinnedSha`. | §9, §14 | 14, 25 | Checkout pinned by SHA in P2 (simple), versioned package in 25; `node.json` key renamed when 25 lands. |
| O6 | The sweep, dream ingestion (step 1), `agents.md` refresh (step 5) and daily roll-up (step 6) are mechanical git work described as skills/timers; testability and the §12 gate suggest CLI verbs (`zyggy sweep`, `zyggy dream-ingest`) the skill calls. §3 also runs `claude -p "/dream"` directly while §6 says all scheduled work is a bus job to `nodes/central/jobs/`. | §3, §6, §7, §11 | 13, 18, 24 | Add `sweep` and an ingestion verb to the CLI surface; dream submitted as a job to Central for the ledger, the skill calls the verbs. |
| O7 | §14 says the Hub takes tenant and user "from the caller's token", but the v1 Hub runs over local stdio with no token. | §14, §7 | 15, 16 | **Resolved 2026-09-28:** §7 defines the Hub principal `(tenant, user)` resolved from the transport (stdio → `node.json` `tenant`/`user`; HTTP → bearer token); tools never accept tenant/user inputs. Logged in §13. |
| O8 | Telemetry default: §9/§10/§11 say optional and off by default; §14 says on by default with a local file exporter, work laptop may disable export but not collection. | §9, §11, §14 | 07, 20 | Decide one default; roadmap assumes off in P1 and revisits with 20. |
| O9 | §14 "Reproducible install" (`zyggy init`, join token, Docker image, Bicep, versioned `zyggy-core`, `zyggy diagnose`) is not in the §14 "deferred to a commercial phase" list, so it is in P0–P5 scope, yet no §12 phase owns it. | §12, §14 | 25 | Deliverable 25 after P5; promote `zyggy init` earlier if the work-laptop install (P4) would otherwise be manual. |
| O10 | Lock file is named `.claude/agentbus.lock` in §6; the §9 naming rule implies `.claude/zyggy.lock`. | §6, §9 | 06 | `zyggy.lock`. |
| O11 | Memory layout: §7 shows `/srv/agent/central/memory/profile.md`; §14 requires `memory/<tenant>/<user>/…` (`memory/geoffrey/geoffrey/`). | §7, §14 | 15, 18 | **Resolved 2026-09-28:** §7 layout is now `memory/<tenant>/<user>/…` with `MemoryPaths` in §9. |
| O12 | The `reason` enum is listed with 7 members in §9, plus `schema_unsupported` (§9 Versioning) and `budget_exceeded` (§14). CLAUDE.md lists all 9. | §9, §14 | 06 | **Resolved 2026-09-28:** §9 lists the closed 9-member enum. |
| O13 | CLAUDE.md states `Zyggy.slnx` is empty; it already lists all six projects (only references and packages are missing). | CLAUDE.md | 01 | Correct CLAUDE.md when 01 lands. |

## What to build next

**Deliverable 01 — Solution builds and tests end-to-end with the test harness** (`_specs/01-solution-scaffolding.md`, then `_plans/01-solution-scaffolding.md`).

Why first: it is the only deliverable with no dependencies, and every later deliverable's RED step needs a solution that builds, test projects with FluentAssertions/NSubstitute, the fake `claude`, the bare-repo fixture and a format check — none of which exist today (verified this session: no project references, no `tools/`, no `tests/golden/`, no `.editorconfig`, no CI).

Hand-off brief for the technical-analyst:

- **Founding-spec sections to read**: §9 solution structure and Packages table, §9 design rules (no real `claude`, `IProcessRunner`), §12 Definition of done, §6 Invocation (the command line the fake must accept), plus CLAUDE.md "Build and test" and the `integration-testing` skill (harness names: `BusRepoFixture`, `tools/fake-claude/{fake-claude.ps1,fake-claude.sh,scenarios/*.jsonl}`, `ZYGGY_FAKE_CLAUDE_SCENARIO`).
- **Design rules and decisions the spec must respect**: `Directory.Build.props` remains the single place for TFM/LangVersion/nullable/warnings-as-errors; packages restricted to the §9 table plus xUnit/FluentAssertions/NSubstitute/coverlet; no LibGit2Sharp; `git` on PATH is the only external prerequisite; §13 decisions on .NET 10 and single-file binaries.
- **Definition-of-done items the acceptance criteria must cover**: `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx`, `dotnet format Zyggy.slnx --verify-no-changes` green locally and in CI; fake-claude emits each scenario and records its arguments on both OSes; `BusRepoFixture` smoke test pushes and reads back a commit; template placeholders removed; per-project READMEs corrected.
- **Risk areas the spec must flag**: new NuGet dependencies (FluentAssertions version/licence choice, NSubstitute); the real `stream-json` sample must be captured by the user from a real `claude -p` run outside the test suite and checked in as `scenarios/done.jsonl` (an Open Question until captured); CI secrets: none needed and none allowed.
- **Out of scope for 01**: any production type from §4–§7 (envelope, signer, paths, poller, runner) — those are 02–06.

Next action: approve the roadmap (or request changes). Then invoke the `technical-analyst` subagent with the hand-off brief to produce `_specs/01-solution-scaffolding.md`; once that spec is approved with zero Open Questions, the `planner` turns it into `_plans/01-solution-scaffolding.md`.

## Change log

| Date | Change | Why |
|------|--------|-----|
| 2026-09-28 | Roadmap created: 25 deliverables across P0–P5 (+ one post-P5 §14 item); phase map proposed in place of the lost §12 diagram; 13 founding-spec conflicts logged as O1–O13; deliverable 01 marked Next. | First roadmap; repo is a `dotnet new` scaffold with no references, harness or CI. Awaiting user approval (HUMAN GATE). |
| 2026-09-28 | User-directed multi-tenancy review of the founding spec (§1–§14): tenant on every envelope, path, secret, log line; Hub principal model; `TenantId`/`Principal`/`MemoryPaths` in §9; closed 9-member reason enum. O1 (partly), O2, O7, O11, O12 marked resolved. | Make the spec consistent with §14 from day one. |
| 2026-09-29 | Decision: one bus repository per tenant; the repository is the isolation boundary, the `tenants/<org>/` prefix is uniformity plus defence in depth. Spec §4, §5, §11, §12, §13, §14 amended; deliverable 10 scope updated (repo per tenant, branch protection, repo-scoped credentials). | Git hosts authorise per repository, not per path; a shared repository would expose every tenant's ledger to every other tenant's machines. |
