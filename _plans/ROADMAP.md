# Zyggy Roadmap

> Owned by the project-manager agent. High-level deliverables only —
> detailed Red-Green-Refactor steps live in `_plans/<NN>-<Deliverable>.md` (planner),
> contracts and acceptance criteria in `_specs/<NN>-<Deliverable>.md` (technical-analyst),
> outcomes of the P0 spike in `_plans/decisions/0001-transport-and-vm.md` (written when deliverables 02, 04 and 05 execute).
>
> Status values: `Not started` · `Next` · `Planning` · `In progress` · `Blocked` · `Done`
>
> **Roadmap status: approved by the user at the HUMAN GATE on 2026-09-29.** The technical-analyst may be invoked for deliverable 01 (`_specs/01-solution-scaffolding.md`).

## How to read this file

- A **deliverable** is one working, verifiable capability: a vertical slice that ends in an observable round trip (a signed probe envelope carried from Central to a laptop and answered; a job claimed and reported against a local bare repo; the Hub answering `get_context`). It is never a project, a folder, a layer, or an RGR step.
- **Ordering principle (user decision, 2026-09-29): riskiest first, then a minimal product, then value order.** The two riskiest, most important unknowns are (a) whether the Central VM can run Claude Code unattended on the Max subscription within budget and (b) whether transport A — a git repository on GitHub reached over HTTPS (port 443) with ETag polling — actually passes the corporate proxy and firewall on the work laptop. P0 answers both with a spike. P1 is the walking skeleton (minimal product, memory and dream included). P2–P5 extend it in value order.
- **No throwaway code.** The P0 spike is built incrementally as real `Zyggy.*` code that survives into the product: the envelope model and signer are the probe payload, `GitHubBusProvider` is the product's one `IBusProvider` implementation, the probe listener loop is the same `Zyggy.Core` loop the Node hosts in P1, and `zyggy probe` remains a permanent diagnostics verb. The `IBusProvider` seam is proven with two implementations, but the second is an in-memory / local-bare-repo test double in the test project (kept honest by the shared provider contract suite), not a second product transport.
- **Transport decision (user, 2026-09-29).** Candidate A (git repository on GitHub, ETag polling, one repository per tenant) is confirmed, subject to one P0 test: GitHub over HTTPS (443) must pass the corporate proxy from the work laptop for (1) API polling to `api.github.com` with a bearer PAT, (2) `git fetch`/`pull` over HTTPS, (3) `git push` over HTTPS with the PAT via credential helper (SSH on 22 is expected to be blocked). Candidate C (an HTTPS API served by Central) is **not built**: everything it would need — TLS, auth, per-machine revocation, DDoS protection, availability, backups, an immutable audit log — GitHub already provides for A, and C would make Central an internet-facing single point of failure contrary to §8. C survives only as one line in the decision record: the fallback if A fails the corporate-proxy test. Enterprise friendliness comes from §14 pluggable edges (the customer's own GitHub Enterprise / Azure Repos / GitLab behind `IBusProvider`), not from a second transport.
- Deliverables are numbered `01..NN` in **build order**. A deliverable is schedulable only when every entry in its *Depends on* column is `Done`. One deliverable is in flight at a time.
- Every deliverable cites the founding-spec sections that are its contract (`_specs/00 - Personal Agent Platform — Technical Specification.md`, referred to below as "§n"), the §9/§14 design rules that bite, the phase gate it serves, and a Definition of Done.
- **Two kinds of gate evidence.** Automated: xUnit/FluentAssertions/NSubstitute tests, never the real `claude` or GitHub, phase scenarios under `tests/Zyggy.Integration/Gates/P<n>_*.cs`, every gate run with a non-default tenant id (§12). User-executed: the spike's real-machine measurements (VM soak, proxy passage, latency) and every physical round trip are **gate checklists the user executes and records**, not automated tests.
- Downstream file names follow the deliverable: `_specs/<NN>-<Deliverable>.md` and `_plans/<NN>-<Deliverable>.md`, where `<Deliverable>` is the kebab-case short name from the *File name* column.
- Status is reconciled against `src/`, `tests/`, and the plan-file checkboxes every session. `Done` is set only when the plan's final HUMAN GATE is checked **and** the Definition of Done holds; for a phase-closing deliverable the gate scenario must exist under `tests/Zyggy.Integration/Gates/P<n>_*.cs`.
- Naming: the spec uses working names `AgentBus.*`, `agentbus`, `agent-bus`, `agent-core`; code and this roadmap use the product names `Zyggy.*`, `zyggy`, `zyggy-bus`, `zyggy-core` (§9 Naming).
- Multi-tenancy shape from the first commit (§13, §14): `tenant` on every envelope, `tenants/<org>/` on every bus path, `key_id = <tenant>/<n>`, `memory/<tenant>/<user>/`, tenant-namespaced secrets, one bus repository per tenant with repo-scoped per-machine credentials. No deliverable may defer any of this.

## Phase map (risk-first — replaces the lost §12 diagram; confirmed by the user at the roadmap gate)

The §12 roadmap diagram did not survive the export. The phases below follow the user's ordering decision of 2026-09-29, not the original §12 sequence; in particular "P0 has no dependency on any machine" no longer holds — P0 needs all three machines (O14, decided; §12 sentence replaced).

| Phase | Theme | Machines needed | Gate (a working round trip, not a code review) | Deliverables |
|-------|-------|-----------------|------------------------------------------------|--------------|
| P0 | **De-risk: Central VM + transport A through the corporate proxy** | Central VM + home laptop + work laptop (probe only, foreground process, no `claude`, no product jobs) | VM: Claude Code stays authenticated unattended for 7 days, remote control resumes after a service restart (§13 Q4 wrapper), scheduled `claude -p` runs headless on the Max subscription, monthly cost within the §13 Q3 budget. Transport: one signed probe envelope travels Central → work laptop → Central over GitHub HTTPS through the real corporate proxy (API polling with PAT, `git fetch`/`pull`, `git push` with PAT via credential helper); the reachability matrix from all three machines is recorded; `_plans/decisions/0001-transport-and-vm.md` confirms A. Automated: `Gates/P0_ProbeRoundTrip.cs` green with tenant `acme`. | 01–05 |
| P1 | **Walking skeleton (minimal product)** | Central + home laptop | `zyggy submit` on Central → the home laptop runs the real `claude -p` in a project named by path → report on the bus → `zyggy status` shows it. A `context` envelope from the home laptop is ingested by the nightly dream pass into `memory/<tenant>/<user>/` and returned by the Hub's `get_context` in the next Central session, whose `SessionStart` digest is under 6k tokens. Both `/health` green, both nodes run as services. Automated: `Gates/P1_WalkingSkeleton.cs`. | 06–13 |
| P2 | **Routing and execution** | Central + home laptop | Central's `delegate` skill picks machine/project/agent from `registry/*.yaml`, the node runs in a worktree, the branch is pushed and the report carries `diff_ref`; a stale claim is re-queued by the hourly sweep and fails after 3 attempts. Automated: `Gates/P2_RegistryWorktreeSweep.cs`. | 14–17 |
| P3 | **Work laptop as a product node** | + work laptop | The work node runs as a service under a signed `policy.yaml` over transport A as proven in P0; a report containing mail-like content is replaced by `status: failed`, `reason: dlp_filter`; a tampered or stale policy stops the node. Automated: `Gates/P3_WorkBoundary.cs`. | 18–20 |
| P4 | **Hub bus tools, alerts, mail** | Central + both laptops | A laptop project gets context through `zyggy hub --proxy`; a Central session submits a job through the Hub; a stuck job and an identity-file dream diff reach Telegram; personal and work mail triage land as summaries and Drafts, nothing is sent. Automated: `Gates/P4_HubAlertsMail.cs`. | 21–24 |
| P5 | **Costs and reproducible install** | Central + a fresh machine | `zyggy status --costs 30d` sums the bus history; a job is refused with `budget_exceeded` when the monthly budget is hit; a fresh VM and a fresh laptop reach a green `/health` with one `zyggy init` command each. Automated: `Gates/P5_BudgetInstall.cs`. | 25–26 |

Not scheduled: `AgentSdkRunner` (§14) stays deferred unless the spec says otherwise; the §14 "Deferred to a commercial phase" list; §1 non-goals; candidate C (HTTPS API served by Central) — fallback only, per the decision record.

## Overview

| # | Deliverable | File name | Phase | Projects | Depends on | Status | Spec | Plan |
|---|-------------|-----------|-------|----------|------------|--------|------|------|
| 01 | Solution builds, tests, formats and publishes from a clean clone (thin harness) | `01-solution-scaffolding` | P0 | slnx, all csproj, `tools/fake-claude`, `tests/`, CI | — | **Next** | — | — |
| 02 | Central VM runs Claude Code unattended; 7-day soak starts | `02-central-vm-soak` | P0 | infra (Azure VM, systemd), `_plans/decisions/0001` | — | Not started | — | — |
| 03 | Envelope parse, canonicalise, sign and verify (the probe payload) | `03-envelope-signing` | P0 | Zyggy.Core, Core.Tests, `tests/golden` | 01 | Not started | — | — |
| 04 | Prove transport A through the corporate proxy: reachability matrix, HTTPS push with PAT on the work laptop, signed probe round trip from all three machines | `04-transport-a-proxy-proof` | P0 | Zyggy.Core, Zyggy.Cli, Core.Tests, Integration, `zyggy-bus` repo | 01, 02, 03 | Not started | — | — |
| 05 | Transport A confirmed and VM soak closed in one decision record; `Gates/P0_*` green (closes P0) | `05-transport-decision-record` | P0 | Zyggy.Cli, Integration (`Gates/P0_*`), `_plans/decisions/0001-transport-and-vm.md` | 02, 04 | Not started | — | — |
| 06 | Job runner executes a path-named job with the fake claude and always reports | `06-job-runner` | P1 | Zyggy.Core, Core.Tests, Integration, `tools/fake-claude` | 05 | Not started | — | — |
| 07 | Node service runs the poll → pull → claim → run → report loop with `/health` | `07-node-service-loop` | P1 | Zyggy.Node, Zyggy.Core, Integration (`Gates/P1_*` part 1) | 06 | Not started | — | — |
| 08 | `zyggy submit`, `status`, `verify` | `08-cli-submit-status-verify` | P1 | Zyggy.Cli, Zyggy.Core, Integration | 05 | Not started | — | — |
| 09 | HMAC keys and the GitHub PAT from the OS secret stores | `09-os-secret-stores` | P1 | Zyggy.Core, Core.Tests | 05 | Not started | — | — |
| 10 | Walking skeleton on real machines: services on Central and the home laptop, real `claude -p` | `10-skeleton-on-real-machines` | P1 | infra, `install.ps1`, systemd units, runbooks | 07, 08, 09 | Not started | — | — |
| 11 | Hub answers `get_context` and `remember` over stdio; `SessionStart` digest | `11-hub-context-remember` | P1 | Zyggy.Hub, Zyggy.Core, Zyggy.Cli, Core.Tests, Integration, `zyggy-core` (minimal, O22) | 05 | Not started | — | — |
| 12 | Nodes feed memory: `zyggy context` and the `Stop` hook emit signed context envelopes | `12-context-envelopes-from-nodes` | P1 | Zyggy.Cli, Zyggy.Core, Integration, `zyggy-core` | 08, 11 | Not started | — | — |
| 13 | Nightly dream pass consolidates bus and inbox into memory (closes P1) | `13-dream-pass` | P1 | Zyggy.Cli, Zyggy.Core, `zyggy-core`, infra (timer), Integration (`Gates/P1_*` part 2) | 10, 11, 12 | Not started | — | — |
| 14 | Discovery registry publishes `registry/<machine>.yaml` and routes jobs by project name | `14-discovery-registry-routing` | P2 | Zyggy.Core, Zyggy.Node, Zyggy.Cli, Integration | 13 | Not started | — | — |
| 15 | `zyggy-core` v1: `PROTOCOL.md`, prompt template, `bus`/`delegate`/`discover` skills, `PreToolUse` hook | `15-zyggy-core-skills-hooks` | P2 | `zyggy-core` repo, Zyggy.Cli (`report`, `discover --link`) | 14 | Not started | — | — |
| 16 | Worktree execution, pushed branch, `zyggy run` | `16-worktree-execution` | P2 | Zyggy.Core, Zyggy.Cli, Integration | 14 | Not started | — | — |
| 17 | Central sweep re-queues stale claims, retries and archives (closes P2) | `17-central-sweep` | P2 | Zyggy.Core, Zyggy.Cli, infra (timer), Integration (`Gates/P2_*`) | 15, 16 | Not started | — | — |
| 18 | Policy as data: signed `policy.yaml` governs every node | `18-policy-as-data` | P3 | Zyggy.Core, Zyggy.Node, Zyggy.Cli, Integration | 17 | Not started | — | — |
| 19 | DLP filter and hook enforcement on nodes with `dlp` enabled | `19-dlp-work-boundary` | P3 | Zyggy.Core, Zyggy.Node, `zyggy-core`, Integration | 18 | Not started | — | — |
| 20 | Work laptop joins as a product node under policy and DLP (closes P3) | `20-work-laptop-product-node` | P3 | infra (work laptop), Integration (`Gates/P3_*`) | 10, 19 | Not started | — | — |
| 21 | Hub bus tools (`list_nodes`, `submit_job`, `job_status`), HTTP transport, `zyggy hub --proxy` | `21-hub-bus-tools-proxy` | P4 | Zyggy.Hub, Zyggy.Cli, Zyggy.Core, Integration | 11, 14 | Not started | — | — |
| 22 | Telegram notifier, §11 alerts and the dream identity-diff message | `22-telegram-alerts` | P4 | Zyggy.Core, Zyggy.Cli, Core.Tests, Integration | 13, 17 | Not started | — | — |
| 23 | Personal mail triage on Central (Gmail + Outlook.com) | `23-personal-mail-triage` | P4 | `zyggy-core` repo, infra (Central `.mcp.json`) | 13, 22 | Not started | — | — |
| 24 | Work mail triage: metadata-only summaries, Drafts only (closes P4) | `24-work-mail-triage` | P4 | `zyggy-core` repo, infra (work laptop), Integration (`Gates/P4_*`) | 20, 21, 23 | Not started | — | — |
| 25 | Cost tracking and monthly budget enforcement | `25-costs-budget` | P5 | Zyggy.Core, Zyggy.Cli, Zyggy.Hub, Integration | 18, 22 | Not started | — | — |
| 26 | Reproducible install: `zyggy init`, join token, Central image + Bicep, versioned `zyggy-core`, `zyggy diagnose` (closes P5) | `26-reproducible-install` | P5 | Zyggy.Cli, infra, Integration (`Gates/P5_*`) | 25 | Not started | — | — |

## Deliverable details

### 01. Solution builds, tests, formats and publishes from a clean clone (thin harness)
- **Goal**: `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx` and `dotnet format Zyggy.slnx --verify-no-changes` are green from a clean clone and in CI; CI publishes the `zyggy` single-file binary for `win-x64` and `linux-x64` (the spike must run on three machines); the integration harness can create a bare bus repo plus clone; the fake `claude` emits one canned `stream-json` scenario and records its arguments — so 03–05 start from RED on a working loop and nothing more.
- **Scope**: project references (`Node`/`Cli`/`Hub` → `Core`; `Core.Tests` → `Core`; `Integration` → `Core`, `Node`, `Cli`); FluentAssertions and NSubstitute in both test projects; central package management; `.editorconfig` for `dotnet format`; removal of template placeholders (`Class1.cs`, `UnitTest1.cs`, hello-world `Program.cs` bodies); `tools/fake-claude/` (`fake-claude.ps1` + `fake-claude.sh`, `scenarios/done.jsonl` as a documented placeholder, argument capture file, `ZYGGY_FAKE_CLAUDE_SCENARIO`); `tests/golden/README.md` stating the golden-file contract; `tests/Zyggy.Integration/Infrastructure/BusRepoFixture` with one smoke test that pushes a commit to the bare repo and reads it back; CI workflow: build + test + format + `dotnet publish` of `Zyggy.Cli` for both RIDs as artefacts.
- **Defers (as thin as the spike needs)**: fake-claude scenarios `no-report`, `hang`, `error` and the real `stream-json` capture (06); §9 source folders (created by the first deliverable that needs each); per-project READMEs beyond removing template text; code coverage; publish of `Zyggy.Node` and `Zyggy.Hub` (07, 11).
- **Spec sections**: §9 solution structure and Packages, §9 Publish, §12 Definition of done (CI), CLAUDE.md "Build and test", `integration-testing` skill (harness names).
- **Depends on**: —
- **Design rules that apply**: `Directory.Build.props` stays the single place for TFM/LangVersion/nullable/`TreatWarningsAsErrors`; no test may invoke the real `claude`; packages limited to the §9 table plus xUnit/FluentAssertions/NSubstitute/coverlet; `git` on PATH is the only external prerequisite; no LibGit2Sharp.
- **Gate served**: prerequisite for P0.
- **Definition of done**: the three commands green locally and in CI; publish artefacts downloadable from CI for both RIDs; fake-claude `done` scenario runs on Windows and Linux and records its arguments; `BusRepoFixture` smoke test green; template placeholders gone; CLAUDE.md "Build and test" section matches the populated `Zyggy.slnx` (O13 decided; CLAUDE.md corrected 2026-09-29).
- **Risks**: new NuGet dependencies (FluentAssertions licence change in v8 — pin a compatible version or record the choice; NSubstitute); CI must need and hold no secrets.
- **Status**: Next

### 02. Central VM runs Claude Code unattended; 7-day soak starts
- **Goal**: the Azure VM from §13 Q3 exists, an unprivileged user has run `claude login` once, `claude-remote.service` runs the §13 Q4 resume wrapper, a systemd timer runs a headless `claude -p` job on the Max subscription and logs the result, restarting the service brings remote control back, an Azure cost alert is configured — and the 7-day soak clock has started with a dated day-0 entry in `_plans/decisions/0001-transport-and-vm.md`.
- **Scope**: VM provisioning (Standard B2as v2, Ubuntu 24.04, 64 GB Premium SSD, Node 22, Claude Code CLI, Tailscale, `/srv/agent` persistent volume, Azure Backup daily), unprivileged `zyggy` user with only its own directory, `claude-remote.service` + Q4 wrapper script, `claude-soak.timer` (headless `claude -p` at a fixed cadence writing to a log; this is the mechanism 13 later reuses for the dream job), Azure budget alert at the Q3 ceiling, runbook draft `restore-central.md`, the decision record opened with the checklist of the four VM checks and the transport-A checklist that 04 fills in. No Zyggy binaries are required; `zyggy-node.service` arrives in 10. No inbound port is opened on the VM.
- **Spec sections**: §10 Central, §13 Q3/Q4, §8 Isolation, §11 Runbooks, §3 Central agent instance.
- **Depends on**: — (no code dependency; scheduled after 01 only because one deliverable is in flight at a time and 01 is a one-day item — the soak clock must show 7 days before 05 can close)
- **Design rules that apply**: Central unprivileged with only its own directory mounted; `--permission-mode auto`, never `--dangerously-skip-permissions`; secrets via `LoadCredential=`, none in files under a repo; nothing installed that a later deliverable does not need.
- **Gate served**: P0 VM checks (user-executed).
- **Definition of done**: user-executed checklist recorded in the decision record: `claude` authenticated after a VM reboot; `systemctl restart claude-remote` followed by a remote-control session that resumes the previous conversation (Q4); three consecutive headless timer runs succeeded; Azure cost forecast for the month ≤ the Q3 budget; soak day-0 entry dated. The 7-day result is collected in 05.
- **Risks**: Claude Code auth token lifetime on a headless VM is the single biggest VM risk — a failed soak reopens §13 Q3; cloud cost; the VM is reachable only through Tailscale/SSH (outbound-only, §8).
- **Status**: Not started

### 03. Envelope parse, canonicalise, sign and verify (the probe payload)
- **Goal**: `Zyggy.Core` parses any §4 envelope (YAML front matter + body) into a typed model, produces the canonical signing input, signs with a per-tenant key from `ISecretStore`, and verifies — rejecting missing/unknown/invalid signatures, unsupported `schema`, a `tenant` that differs from the subtree the file was read from, and a `key_id` whose tenant part differs from `tenant` — with golden files proving the canonical bytes never drift. This is the probe payload that 04 carries over transport A.
- **Scope**: `Tenancy/` value types (`TenantId`, `UserId`, `Principal`); `Envelope`, `EnvelopeType`, `EnvelopeParser` (unknown fields preserved on round trip; `attempt` carried — O1), `EnvelopeSigner.Canonicalize` + HMAC-SHA256, `Ulid` file names, `schema: 1`, `key_id = <tenant>/<n>`, rotation (accept `<tenant>/1` and `<tenant>/2`); the mandatory-per-type field list for `job`, `report` and `context` (settled by this deliverable's technical-analyst — O1); `ISecretStore` seam with `InMemorySecretStore` (tests) and `FileSecretStore` (0600, the §8 Linux file option) — the file store is what all three spike machines use in P0 (O20); OS stores land in 09.
- **Spec sections**: §4 Envelope, §4 Signature, §9 `Tenancy/`, §9 Versioning (`schema: 1`, `schema_unsupported`), §14 Tenancy, §8 Secrets (abstraction and file option).
- **Depends on**: 01
- **Design rules that apply**: `EnvelopeSigner.Canonicalize` is the single source of truth with golden-file tests under `tests/golden/`; tenant checks enforced in `Zyggy.Core` (parser), not in callers; no `TenantId.Default` or equivalent in `src/`; `ISecretStore` lookups tenant-scoped, keys under `zyggy/<tenant>/hmac/<n>`; `YamlDotNet` with sorted keys; no static state.
- **Gate served**: P0 (payload for 04).
- **Definition of done**: unit tests for parse/round-trip/sign/verify/tenant mismatch/`key_id` mismatch/`schema` too high, all with a non-default tenant; golden files (input, canonical bytes, expected signature for a test key) for `job`, `report` and `context`; verification rejects tampering of any front-matter key or body byte; the mandatory-per-type field list written into `_specs/03-envelope-signing.md` and proposed as a §4 amendment; deviations from §4 raised as Open Questions in the spec.
- **Risks**: signing and canonical form are the protocol's shared contract between every machine — any later change is a protocol change; O1 decided 2026-09-29 (`attempt` in the §4 example; field list delegated here).
- **Status**: Not started

### 04. Prove transport A through the corporate proxy: reachability matrix, HTTPS push with PAT on the work laptop, signed probe round trip from all three machines
- **Goal**: on Central, `zyggy probe send --to work-laptop` writes a signed `job` envelope into `tenants/<org>/nodes/work-laptop/jobs/` of the tenant's `zyggy-bus` repository; `zyggy probe listen` on the laptop wakes on the ETag change, pulls over HTTPS, verifies, claims (`git mv`), writes a signed `report`, removes the job and pushes over HTTPS with the PAT via credential helper; the sender observes the report and `zyggy probe stats` prints round-trip p50/p95 and failure counts measured at the sender; `zyggy probe reach` records what each machine can actually reach through its system proxy — (1) `api.github.com:443` conditional polling with a bearer PAT, (2) `git fetch`/`pull` over `github.com:443`, (3) `git push` over `github.com:443` with the PAT via credential helper, and for the record (4) `github.com:22` SSH (expected blocked on the work laptop). The same run is executed from the home laptop and from the work laptop through the corporate proxy on day one.
- **Scope**: `IBusProvider` seam (cheap head check → fetch → publish over a local tenant checkout) with `GitHubBusProvider` as the product implementation and a second implementation that is an in-memory / local-bare-repo test double in the test project — both driven by the shared provider contract suite; `BusPaths` (tenant, machine, state, id, registry, policy, archive); `BusRepository` (list new jobs for me, claim, report + `git rm`, reject bad signature / wrong tenant / wrong `to` into `jobs/rejected/` with a `status: rejected` report, refuse a checkout with a second `tenants/<org>/` directory); `GitClient` over `IProcessRunner` (§5 write sequence, push-rejection retry with jitter — O4, start-up stash, HTTPS remote with credential helper or SSH remote selected per machine in `node.json`); `GitHubPoller` as a pure `PollState` state machine (ETag, active/idle interval with jitter, 403/429 back-off, network back-off; the 15-min safety fetch and work-hours evaluation move to 07); the listen loop as `Zyggy.Core` code hosted by the CLI verb in P0 and by `Zyggy.Node` in 07; `zyggy probe send|listen|stats|reach`; the tenant's `zyggy-bus` repository under `zyggy-org` created per `add-tenant.md` (force-push and deletion blocked on `main`, per-machine PATs scoped to that repository, deploy key or HTTPS push per machine — O19); probe binaries installed by hand on both laptops (foreground process, no service, no `claude`).
- **Spec sections**: §4 Repository layout, One repository per tenant, Write rules, Signature (rejection), State machine rows 1–3; §5 Poll loop, Git operations, Credentials per machine, Proxy; §9 design rules; §14 Tenancy, Pluggable edges; §11 Logging (once per state change), `add-tenant.md`; §1 work-laptop egress constraint (to be verified here, not assumed).
- **Depends on**: 01, 02, 03
- **Design rules that apply**: all paths via `BusPaths` (a `"tenants/"`, `"nodes/"` or `"jobs/"` literal outside it is a violation); git only through `IProcessRunner`; pure `PollState` with injected HTTP and `TimeProvider`; `GitHubBusProvider` is the only type that knows GitHub; `BusRepository`, `BusPaths` and the envelope layer are transport-agnostic so a customer's own git host (§14 pluggable edges) needs a new `IBusProvider` only; PAT read through `ISecretStore`, never options; corporate proxy honoured by both `HttpClient` (system proxy) and git (`http.proxy`); log once per state change, 304 silent; one in-flight request.
- **Gate served**: P0 transport test (the only candidate).
- **Definition of done**: unit tests: `PollState` transitions with a fake clock, `GitHubPoller` headers and rate-limit parsing against a mocked `HttpMessageHandler`, `BusPaths`, write rules; integration tests against `BusRepoFixture` with tenant `acme`: claim, report, reject (bad signature, wrong tenant, wrong `to`, `schema` too high), push rejected by a foreign commit then retried, three rejections leave the change uncommitted and log once, second tenant directory refused; both `GitHubBusProvider` (over the bare repo) and the test double pass the shared `IBusProvider` contract suite; **user-executed**: reachability matrix (3 machines × the four checks above) from `zyggy probe reach`, ≥ 10 probes Central → home laptop and ≥ 10 Central → work laptop through the corporate proxy with HTTPS push from the work laptop, appended to `_plans/decisions/0001-transport-and-vm.md`; runbook `bus-conflict.md` draft.
- **Risks**: §4/§5 shared contracts; PATs on three machines (secrets, repo-scoped); the corporate proxy must be honoured by both `HttpClient` and git; SSH on 22 is expected to be blocked on the work laptop — HTTPS push with the PAT is the path to prove (O19); three machines polling at 10 s against the GitHub rate limit (conditional 304s are documented as not counting — verify and record); latency measured at the sender only, no cross-machine clocks (O21); if the work laptop fails any of the three checks, candidate C becomes the recorded fallback (evaluated in 05, not built here).
- **Status**: Not started

### 05. Transport A confirmed and VM soak closed in one decision record; `Gates/P0_*` green (closes P0)
- **Goal**: `_plans/decisions/0001-transport-and-vm.md` (one record, merging the VM-viability and transport records) states: transport A confirmed against the corporate proxy with the per-machine git auth path (SSH vs HTTPS push with PAT) for Central, home laptop and work laptop; the 7-day VM soak result against the §13 Q3/Q4 checks; the latency of A alone measured at the sender (p50/p95 round trip) with a proposed target for the user to ratify (O21); and the one-line fallback — candidate C (HTTPS API served by Central) is evaluated only if A fails the proxy test, never built preemptively. `Gates/P0_ProbeRoundTrip.cs` is green with tenant `acme`.
- **Scope**: `zyggy probe stats --export` producing a Markdown/CSV table per machine: reachable (API / fetch / push / SSH), auth ok, p50/p95 round trip, failures, rate-limit hits; `Gates/P0_ProbeRoundTrip.cs` — signed probe claimed and answered, tampered probe ends in `jobs/rejected/`, tenant `acme`, second `tenants/<org>/` directory refused, a machine configured for `acme` never opens another tenant's repository; the decision record completed; §1 work-laptop egress constraint updated from "to be verified in P0" to the measured result (user amends).
- **Spec sections**: §12 Definition of done, §13 Q3/Q4 and the transport rows (Decided, condition "confirmed against the corporate proxy in P0"), §1 Constraints (verified, not assumed), §5 Credentials per machine, §14 Pluggable edges.
- **Depends on**: 02, 04
- **Design rules that apply**: decisions live in records, not in code; measurements are user-executed checklists, never automated tests against GitHub; the test-double `IBusProvider` stays in the test project only.
- **Gate served**: **closes P0**.
- **Definition of done**: decision record approved by the user; §1 egress constraint and §5 credentials table amended (or annotated by the orchestrator) from the record; `Gates/P0_*` green in CI; soak evidence: 7 consecutive days authenticated, remote control resumed after ≥ 2 service restarts, ≥ 7 headless `claude -p` runs, cost forecast within the Q3 budget; one signed probe round trip Central ↔ work laptop through the proxy recorded with timestamps.
- **Risks**: the highest-impact decision in the project; measurement bias (a single quiet day, proxy variance) — probes must include a working day inside the corporate network; if A fails the work-laptop proxy test, P3 (20, 24) is `Blocked` with the §8 `bus: disabled` fallback (O23) and the record opens the candidate-C evaluation; if the soak fails, §13 Q3 reopens.
- **Status**: Not started

### 06. Job runner executes a path-named job with the fake claude and always reports
- **Goal**: given a claimed job whose `project` names a local project path directly (no registry yet — O18), `JobRunner` runs pre-flight (path exists, is a git repository, has `.claude/`; `agent` resolved against `<path>/.claude/agents/*.md`; project lock), invokes `IModelRunner` (`ClaudeCodeCliRunner` over `IProcessRunner` with the exact §6 command line and prompt template), streams `stream-json`, enforces timeout and the 2 MB cap, stores the transcript under `runs/<tenant>/<id>.jsonl`, and always yields a report — `done`, or `failed`/`timeout`/`rejected` with a `reason` from the closed enum — never an exception to the caller.
- **Scope**: `IModelRunner` + `ClaudeCodeCliRunner`, `ClaudeProcess` (stream-json reader, `result` event → `cost_usd`, `duration_ms`, `num_turns`, tokens, model), `PromptTemplate` (body as data inside the `<<< >>>` fence), `ProjectLock` (`.claude/zyggy.lock`, `FileMode.CreateNew`, 2 h staleness — O10), `JobReason` enum with all nine members, `IPolicySource` seam declared with a default v1 stub (real policy in 18), report assembly (`summary` ≤ 2,000 chars fallback, `files_changed` empty, `diff_ref` null until 16; fresh ULID per report file with `in_reply_to` = job id — O3), fake-claude scenarios `no-report`, `hang`, `error` and the real `stream-json` sample captured once by the user from a real `claude -p` run outside the test suite. **Replacement path**: 14 makes `project` a registry name resolved to a path; a path value is then accepted only when it lies under a policy `dev_roots` entry, otherwise `unknown_project` (O18).
- **Spec sections**: §6 Pre-flight (steps 1–3), Invocation, Streaming and limits, Report assembly; §9 design rules (closed enum, never throws); §14 Model runtime.
- **Depends on**: 05
- **Design rules that apply**: `claude` only through `IProcessRunner` inside `ClaudeCodeCliRunner`; `JobRunner` never throws; envelope body is data; no test calls the real `claude`; transcript path carries the tenant.
- **Gate served**: P1.
- **Definition of done**: unit tests for pre-flight, lock, template rendering, stream parsing, cap and timeout with `FakeTimeProvider`; integration tests per fake-claude scenario (`done`, `no-report`, `hang` → `timeout`, `error` → `claude_error`) and for the invocation contract captured by the fake; runbook entries for `timeout` and `locked`; the path-to-registry replacement rule written into the spec.
- **Risks**: §6 runner I/O is a shared contract; prompt-injection surface; the fake must reflect the real `stream-json` shape.
- **Status**: Not started

### 07. Node service runs the poll → pull → claim → run → report loop with `/health`
- **Goal**: `Zyggy.Node` hosted as a service (Windows service / systemd selected at runtime) reads `node.json`, polls through `IBusProvider` (`GitHubBusProvider`), pulls, dispatches jobs (one per project, max 2 per machine, work hours unless `priority: high`), runs them through `JobRunner`, publishes reports, and exposes `http://localhost:4711/health` with the §11 fields — proven end-to-end against the bare repo, the fake claude and a mocked head check.
- **Scope**: `PollLoop` (BackgroundService reusing the 04 listen loop), `JobDispatcher`, `node.json` options model and validation (tenant required, no default, no secrets, refuse a checkout without `tenants/<tenant>/`), safety fetch every 15 min and work-hours evaluation (deferred from 04), Serilog rolling file enriched with `tenant`/`machine`/`jobId`/`phase`, health endpoint bound to localhost, `UseWindowsService()`/`UseSystemd()`, DI composition root whose production extension methods the tests reuse; telemetry off by default (O8); `Gates/P1_WalkingSkeleton.cs` part 1 (submit → node → fake claude → report on `main`).
- **Spec sections**: §3 `AgentBus.Node`, §5 Poll loop (integration), §6 Concurrency, §10 `node.json`, §11 Logging and Health endpoint, §9 Packages (Hosting, Serilog).
- **Depends on**: 06
- **Design rules that apply**: everything via DI, no static mutable state; log once per state change; secrets never in `appsettings.json`/`node.json`; only seam implementations and options are swapped in tests.
- **Gate served**: P1 (automated half; the physical round trip is 10).
- **Definition of done**: unit tests for dispatcher rules and options validation (including the tenant refusals); gate scenario part 1 green starting the real host through production DI with tenant `acme`; `node.json` schema documented; runbook entry "dirty working copy on start-up" links `bus-conflict.md`.
- **Risks**: new packages (Serilog, Hosting.WindowsServices/Systemd); health endpoint must bind to localhost only; telemetry is OFF by default in v1 (O8, decided) — no collector wired here.
- **Status**: Not started

### 08. `zyggy submit`, `status`, `verify`
- **Goal**: from any machine with a bus checkout and keys, `zyggy submit --to <machine> --project <path> [--agent] [--timeout] [--allowed-tools] <body>` builds, signs and publishes a job envelope; `zyggy status [--id]` reports bus state (jobs, claimed, reports) and the local `/health`; `zyggy verify` re-checks every signature in the machine's tenant subtree, the `tenant`-field-versus-path consistency of every file, and that exactly one `tenants/<org>/` directory exists — each with documented exit codes.
- **Scope**: System.CommandLine host (shared with `probe`), verbs `submit`, `status`, `verify`, shared options loading from `node.json`, in-process invocation harness for integration tests; `--costs` deferred to 25.
- **Spec sections**: §3 `AgentBus.Cli`, §4 Write rules and One repository per tenant, §8 Audit (`verify`), §11 Health endpoint (`status`), §12 DoD (repository-per-tenant rule refused by `verify`).
- **Depends on**: 05
- **Design rules that apply**: all bus access via `BusRepository`/`BusPaths`/`IBusProvider`; signing via `EnvelopeSigner` + `ISecretStore`; the CLI never handles git or transport credentials directly.
- **Gate served**: P1.
- **Definition of done**: integration tests invoking the CLI in-process for each verb, including `verify` detecting a tampered file, a tenant/path mismatch and a second tenant directory; `zyggy --version` works; publish for both RIDs still green in CI.
- **Risks**: `submit` is the human entry point to the signing path — argument validation must not allow a body to leak into front matter.
- **Status**: Not started

### 09. HMAC keys and the GitHub PAT from the OS secret stores
- **Goal**: `ISecretStore` resolves the bus HMAC keys (`zyggy/<tenant>/hmac/<n>`) and the GitHub PAT for the tenant's `zyggy-bus` repository from Windows Credential Manager, libsecret or the 0600 file on Linux, and systemd `LoadCredential` on Central — and the Node refuses to start when the signing key is absent. The spike's file store on Windows is retired and the spike keys rotated (O20).
- **Scope**: `WindowsCredentialManagerSecretStore` (`Meziantou.Framework.Win32.CredentialManager`), `LibSecretSecretStore`, `SystemdCredentialSecretStore`, store selection at runtime, `zyggy secret set|list` helper (values never echoed), git credential helper fed from `ISecretStore` for HTTPS push, runbook `rotate-hmac.md`.
- **Spec sections**: §8 Secrets table, §4 Signature (rotation), §5 Credentials per machine, §9 `Secrets/`, §14 Pluggable edges (`ISecretStore`), §11 `rotate-hmac.md`.
- **Depends on**: 05
- **Design rules that apply**: nothing outside the implementations references a secret store; lookups tenant-scoped; no secret in any file under a repo, in tests, or in logs.
- **Gate served**: P1.
- **Definition of done**: unit tests for selection and error mapping with a substituted backend; OS-conditional integration tests (`[Trait]` + skip elsewhere) that round-trip a value through the real store; start-up failure logged once with a runbook pointer.
- **Risks**: secrets and a new NuGet dependency; Windows tests need the interactive user's store.
- **Status**: Not started

### 10. Walking skeleton on real machines: services on Central and the home laptop, real `claude -p`
- **Goal**: `zyggy-node.service` runs on the Central VM from 02 (with `LoadCredential=`), `Zyggy.Node` runs as a Windows service on the home laptop installed by `install.ps1 -Tenant <org> -Machine home-laptop`, transport A is production-hardened (`zyggy-bus` branch protections, repo-scoped per-machine PATs, HTTPS push with PAT or deploy key per machine as recorded in 05), and `zyggy submit --to home-laptop --project <path>` on Central produces a real `claude -p` run on the laptop and a report visible in `zyggy status`.
- **Scope**: `install.ps1`, systemd units, Windows service registration as the interactive user, `node.json` on both machines (no secrets), runbooks `restore-central.md` (completed), `revoke-machine.md`, `add-tenant.md` (first execution recorded), transport hardening per the 05 decision record.
- **Spec sections**: §10 Central, Laptops, Upgrade path; §5 Credentials per machine; §8 Isolation; §11 Runbooks; §13 Q3/Q4.
- **Depends on**: 07, 08, 09
- **Design rules that apply**: Central unprivileged; `--permission-mode auto`; revoking one machine never affects the others; every credential scoped to one tenant repository.
- **Gate served**: P1 physical half.
- **Definition of done**: user-executed checklist in the plan's final HUMAN GATE: real round trip observed, both `/health` report the version and tenant, `node.json` on both machines contains no secret, runbooks exist, spec §10 updated for anything that changed.
- **Risks**: secrets provisioning; first real `claude -p` under the product's permission mode and allowlist on the user's own projects; cloud cost.
- **Status**: Not started

### 11. Hub answers `get_context` and `remember` over stdio; `SessionStart` digest
- **Goal**: a Claude Code session on Central with `Zyggy.Hub` in `.mcp.json` receives ranked memory for a topic (alias/description match, then recency, under `max_tokens`) from the principal's `memory/<tenant>/<user>/`, `remember` appends a tagged fact to that principal's `inbox/` while refusing secret patterns, and the `SessionStart` hook injects a digest (`profile.md`, `preferences.md`, `agents.md`, last 7 daily files, every other file's `description`) under 6k tokens produced by `zyggy memory digest`.
- **Scope**: `MemoryPaths` (per `Principal`), `MemoryStore`, `ContextRanker`, `Zyggy.Hub` over stdio with the `ModelContextProtocol` SDK, principal resolved once per connection from `node.json` `tenant`/`user`, secret-pattern refusal, `zyggy memory digest` verb, the memory repository initialised with the §7 layout for `geoffrey/geoffrey`, Central `.mcp.json`, and the **minimal `zyggy-core`** the skeleton needs: `remember` skill and `SessionStart` hook (O22). `list_nodes`/`submit_job`/`job_status`, HTTP transport and `hub --proxy` are 21.
- **Spec sections**: §7 Layout, File format, Context loading, Hub MCP surface and principal; §9 `Memory/`, Packages (MCP SDK); §14 Tenancy (memory path, principal); §3 Hooks (`SessionStart`).
- **Depends on**: 05
- **Design rules that apply**: `MemoryPaths` requires an explicit `Principal` on every call; the Hub never writes durable files other than `inbox/`; tools accept no `tenant`/`user` input and reject one if present; no static state.
- **Gate served**: P1.
- **Definition of done**: unit tests for ranking and token budget on a fixture memory tree with a non-default principal; integration test driving the Hub through an in-process MCP client for both tools including a refused secret and a rejected `tenant` input; digest size test; `.mcp.json` example for Central.
- **Risks**: new NuGet dependency (MCP SDK); the secret-pattern list is a data-protection control.
- **Status**: Not started

### 12. Nodes feed memory: `zyggy context` and the `Stop` hook emit signed context envelopes
- **Goal**: on the home laptop, `zyggy context --scope project:<name> "<fact>"` and the `Stop` hook write a signed `context` envelope (one fact per line, `scope` set) to `tenants/<org>/nodes/central/context/` on the bus, and Central's node receives it — so hand-run laptop sessions feed memory without any durable write on the node.
- **Scope**: `zyggy context` verb, context envelope writer, `Stop` hook script in `zyggy-core` (node variant), the node-side `remember` skill variant that calls `zyggy context`. `zyggy report` (skills writing reports by hand) is 15; `hub --proxy` is 21.
- **Spec sections**: §4 Envelope (context fields), Write rules (sender writes `nodes/central/context/`); §3 `remember` skill and `Stop` hook (node side); §7 (nodes never write durable memory).
- **Depends on**: 08, 11
- **Design rules that apply**: nodes never write durable memory; paths via `BusPaths`; body is data; signing via `ISecretStore`.
- **Gate served**: P1.
- **Definition of done**: integration tests: `zyggy context` lands a signed, verifiable context envelope on the bare repo with tenant `acme`; a `Stop` hook invocation does the same; `verify` accepts it.
- **Risks**: none beyond the shared envelope contract.
- **Status**: Not started

### 13. Nightly dream pass consolidates bus and inbox into memory (closes P1)
- **Goal**: `zyggy-dream.timer` on Central submits the dream as a job to `tenants/<org>/nodes/central/jobs/` (§6, one ledger entry per run); Central's node runs the `dream` skill in the memory directory; the mechanical steps are `zyggy` verbs the skill calls — `zyggy dream ingest` (fetch the bus, copy that tenant/user's new `reports/` and `context/` into `inbox/` one file each, move processed bus files to `tenants/<org>/archive/YYYY-MM/`, never ingest a file whose `tenant` differs from its subtree) and `zyggy dream rollup` (daily notes older than 30 days into monthly summaries); steps 2–4 (merge facts with provenance tags, rewrite files over 300 lines) are the model's work; step 7 commits `dream YYYY-MM-DD`. Step 5 (`agents.md` from the registry) is 14; the Telegram diff is 22.
- **Scope**: `dream` skill in `zyggy-core`, `zyggy dream ingest|rollup` verbs (operating on the bus clone through `BusRepository`), timer unit, `Gates/P1_WalkingSkeleton.cs` part 2 (context envelope → ingest → `get_context` returns it; digest < 6k tokens), one real nightly run observed on Central after a real job from the home laptop.
- **Spec sections**: §7 Dream pass steps 1–4, 6–7 and Rules; §6 Central executing its own jobs; §10 Central timers; §14 memory path; §3 `dream` skill.
- **Depends on**: 10, 11, 12
- **Design rules that apply**: only `[stated]`/`[observed]` tags; node facts are `[observed]`; never store secrets or mail bodies; only Central writes `archive/`; every move one commit via `BusPaths`.
- **Gate served**: **closes P1**.
- **Definition of done**: `Gates/P1_*` green (both parts) with tenant `acme`; one nightly run on Central reviewed by the user (git diff of the memory repo) at the plan gate; `PROTOCOL.md` placeholder and spec §7 updated where behaviour changed; runbook entry for a failed dream run.
- **Risks**: memory writes are the single durable state — until 22 adds the Telegram diff, the user reviews dream commits by hand; O6 (CLI verbs called by skills, dream submitted as a job) decided 2026-09-29.
- **Status**: Not started

### 14. Discovery registry publishes `registry/<machine>.yaml` and routes jobs by project name
- **Goal**: on service start and hourly, the node scans `devRoots` for Claude Code projects (`.claude/`, agents, skills, `CLAUDE.md` summary, default branch) and publishes `tenants/<org>/registry/<machine>.yaml` with the health subset, committing only when content differs (excluding `last_seen`, refreshed at most every 6 h); `zyggy discover` does the same on demand; `JobRunner` pre-flight now resolves `project` as a registry name and rejects unknown names with `unknown_project`, while a path value is accepted only under a `dev_roots` entry (O18); `zyggy dream agents` refreshes `agents.md` from the registry (dream step 5).
- **Scope**: `DiscoveryScanner`, `RegistryDocument`, `DiscoveryTimer`, `zyggy discover`, registry read in pre-flight, `zyggy dream agents`.
- **Spec sections**: §4 Registry file; §6 Pre-flight step 1; §7 Dream step 5; §3 `discover` skill; §11 Health (registry subset).
- **Depends on**: 13
- **Design rules that apply**: registry path via `BusPaths`; commits through `GitClient`/`IBusProvider`; log once per change; other tenants' registry files never read.
- **Gate served**: P2.
- **Definition of done**: unit tests for scanner and diff-excluding-`last_seen`; integration tests: one commit on first start, none on unchanged rescan, unknown project name → `unknown_project`, path outside `dev_roots` → `unknown_project`; spec §4/§6 amended for the routing rule.
- **Risks**: `dev_roots` becomes policy-governed in 18 — until then it is `node.json`.
- **Status**: Not started

### 15. `zyggy-core` v1: `PROTOCOL.md`, prompt template, `bus`/`delegate`/`discover` skills, `PreToolUse` hook
- **Goal**: the `zyggy-core` config repo holds `PROTOCOL.md` matching §4 verbatim (as amended after P0), `templates/job-prompt.md`, the `bus` (wrapping `zyggy submit/status/report`), `delegate` (machine/project/agent from the registry → job) and `discover` (`zyggy discover --link`) skills, the `PreToolUse` hook blocking tools outside `allowed_tools`, writes outside the project directory and `curl`/`wget`/`Invoke-WebRequest` unless allowed, and `CLAUDE.md` templates — pinned by SHA in each machine's `node.json` (O5).
- **Scope**: `zyggy-core` content (Markdown + hook scripts), `zyggy report` and `discover --link` verbs, pin handling in `node.json`, a scripted tool-call test of the hook on the home laptop.
- **Spec sections**: §3 `agent-core`, Skills, Hooks; §6 Prompt template; §8 Isolation and Injection; §9 Versioning (pin); §12 DoD (`PROTOCOL.md`).
- **Depends on**: 14
- **Design rules that apply**: envelope bodies are data in every prompt; `PROTOCOL.md` must match §4; the hook is a security control.
- **Gate served**: P2.
- **Definition of done**: `PROTOCOL.md` diffed against §4; `delegate` produces a valid signed job from a registry fixture; hook blocks a disallowed tool and an out-of-project write in a scripted run; SHA-pinned checkout per O5 (decided).
- **Risks**: shared contract (§4 verbatim); hook correctness on Windows and Linux shells.
- **Status**: Not started

### 16. Worktree execution, pushed branch, `zyggy run`
- **Goal**: a job with `worktree: true` runs in `../<project>-zyggy-<id>` on branch `zyggy/<id>` from the registry's `default_branch`; on completion commits are pushed, `diff_ref` carries the SHA, `files_changed` is populated, the worktree is removed; `worktree: false` runs in place and never commits. `zyggy run --job <file>` executes one job file for the optional Tailscale fast path while the report still lands on the bus.
- **Scope**: `WorktreeManager`, `files_changed` from `git status --porcelain`, `diff_ref`, `git_error` reason path, `zyggy run`, hook rule "writes only inside the worktree".
- **Spec sections**: §6 Pre-flight step 4, Report assembly; §5 Direct fast path; §3 `run`.
- **Depends on**: 14
- **Design rules that apply**: git via `GitClient`/`IProcessRunner`; `JobRunner` never throws (`git_error`); no test invokes the real `claude`.
- **Gate served**: P2.
- **Definition of done**: integration tests with a second bare repo as the project's origin: branch pushed and SHA in report, no-commit case → `diff_ref: null`, worktree removed on timeout; manual real-`claude` worktree run recorded at the plan gate.
- **Risks**: writes into the user's real project repos — limited to the worktree and the `zyggy/` branch namespace.
- **Status**: Not started

### 17. Central sweep re-queues stale claims, retries and archives (closes P2)
- **Goal**: an hourly sweep on Central (`zyggy-sweep.timer` → job to `nodes/central/jobs/`) moves claims older than `timeout_minutes` + 15 min back to `jobs/` with `attempt: n+1`, writes a `status: failed` report after 3 attempts, and moves `reports/` and `context/` older than 14 days to `archive/YYYY-MM/` — all auditable commits inside one tenant subtree.
- **Scope**: `zyggy sweep` verb (O6), sweep logic in `Zyggy.Core`, `attempt` handling, timer unit, alert *conditions* evaluated and logged (delivery is 22), `Gates/P2_RegistryWorktreeSweep.cs`.
- **Spec sections**: §4 State machine rows 4–5; §11 Alerts (evaluation), Runbooks; §10 timers.
- **Depends on**: 15, 16
- **Design rules that apply**: only Central writes `archive/`; every move one commit via `BusPaths`; `TimeProvider` for ages; idempotent.
- **Gate served**: **closes P2**.
- **Definition of done**: integration tests for re-queue, third-attempt failure and archive with a fake clock; `Gates/P2_*` green with tenant `acme`; runbook "job failed 3 times"; spec updated.
- **Risks**: `attempt` semantics — field added to the §4 example on 2026-09-29 (O1 decided); the sweep must respect the mandatory-field list from 03.
- **Status**: Not started

### 18. Policy as data: signed `policy.yaml` governs every node
- **Goal**: Central signs `tenants/<org>/policy.yaml`; every node loads it through `IPolicySource`, refuses jobs when the signature fails or the policy is older than 30 days, and takes `work_hours`, `max_concurrent_jobs`, default `allowed_tools`, `dev_roots` and `dlp` rules from it — `node.json` may only tighten, never loosen.
- **Scope**: `SignedPolicySource` replacing the 06 stub, policy model, `zyggy policy sign|show`, precedence rules, refusal logging, `policyVersion` in `/health`.
- **Spec sections**: §14 Policy as data; §4 layout (`policy.yaml`, only Central writes it); §9 five seams; §8 Work boundary (expressed as data); §11 Alerts (policy failure condition).
- **Depends on**: 17
- **Design rules that apply**: policy signing reuses `EnvelopeSigner.Canonicalize` semantics or a documented variant with golden files; nothing outside `SignedPolicySource` parses the policy.
- **Gate served**: P3.
- **Definition of done**: unit tests for tighten-only precedence and staleness with a fake clock; integration test: tampered policy → node refuses jobs and logs once; runbook "policy rejected".
- **Risks**: signing; a policy outage stops every node — the failure mode must be explicit.
- **Status**: Not started

### 19. DLP filter and hook enforcement on nodes with `dlp` enabled
- **Goal**: on a node whose policy enables DLP, any report body containing `Content-Type:`, a base64 blob > 1 KB, or more than 200 lines (values from policy, not code) is replaced by `status: failed`, `reason: dlp_filter`; the `PreToolUse` hook applies the policy's tool rules; telemetry stays off unless the policy enables collection/export (O8, decided).
- **Scope**: `DlpFilter` driven by policy rules, wiring in report assembly, hook rule source in `zyggy-core`, `dlp.enabled` in `node.json` as tighten-only override, `telemetry` policy keys (collection and export, both default off).
- **Spec sections**: §6 Report assembly (DLP paragraph); §8 Work boundary; §14 Policy as data; §11 Optional telemetry.
- **Depends on**: 18
- **Design rules that apply**: `JobRunner` never throws; rules are data; the filter is a hard requirement.
- **Gate served**: P3.
- **Definition of done**: unit tests per rule; integration test with a fake-claude scenario emitting mail-like content → `dlp_filter` report; runbook entry.
- **Risks**: work-laptop trust boundary (§8).
- **Status**: Not started

### 20. Work laptop joins as a product node under policy and DLP (closes P3)
- **Goal**: the work laptop runs `Zyggy.Node` as a Windows service over transport A as proven in P0 (GitHub HTTPS through the corporate proxy, HTTPS push with PAT), under a policy entry with `dlp` enabled and `devRoots` limited to work project folders; a job from Central runs in a work project and reports; a report with mail-like content is filtered to `dlp_filter`; no mail access yet.
- **Scope**: work-laptop install (`install.ps1`), `node.json` and policy entry, proxy configuration for `HttpClient` and git, `Gates/P3_WorkBoundary.cs` (policy + DLP automated part), user-executed round trip on the work laptop, runbook for the work node.
- **Spec sections**: §8 Work boundary; §10 Work laptop; §5 Proxy, Credentials per machine; §13 Q1; §14 Policy as data.
- **Depends on**: 10, 19
- **Design rules that apply**: nothing on the bus but reports and context envelopes that passed DLP; secrets in Credential Manager only.
- **Gate served**: **closes P3**.
- **Definition of done**: `Gates/P3_*` green with tenant `acme`; user-executed: work-laptop round trip through the corporate proxy, filtered report observed, tampered policy stops the node; spec §10 updated.
- **Risks**: work-laptop trust boundary; if P0 showed transport A does not pass the proxy, this deliverable is `Blocked` with the §8 `bus: disabled` fallback (O23) until the candidate-C fallback in the 05 record is evaluated.
- **Status**: Not started

### 21. Hub bus tools (`list_nodes`, `submit_job`, `job_status`), HTTP transport, `zyggy hub --proxy`
- **Goal**: a Central session lists nodes from `registry/*.yaml` plus health, submits a signed job through the Hub (same path as `zyggy submit`, `tenant` stamped from the principal) and reads a job's state and report; the Hub serves the same tools over HTTP with a bearer-token principal; on laptops, project `.mcp.json` files point to `zyggy hub --proxy`, which answers `get_context` from a cached copy of the machine's `context/` folder plus the project's `CLAUDE.md` and turns `remember` into a `context` envelope.
- **Scope**: three MCP tools over `BusRepository`/`EnvelopeSigner`, HTTP transport bound to localhost/Tailscale, `zyggy hub --proxy`, cache refresh on fetch, hop-count tracking for the 3-hop confirmation rule (§8).
- **Spec sections**: §7 Hub MCP surface and principal (HTTP bearer); §4 sender rules; §8 Injection (3 hops); §11 Health (`list_nodes`).
- **Depends on**: 11, 14
- **Design rules that apply**: bus access only via `BusRepository`; signing via `ISecretStore`; principal from transport only.
- **Gate served**: P4.
- **Definition of done**: integration tests for each tool against the bare repo; HTTP principal test (bad token rejected); proxy returns `CLAUDE.md` + cached context and lands a signed context envelope for `remember`.
- **Risks**: `submit_job` exposes the signing path to MCP callers — validation identical to the CLI.
- **Status**: Not started

### 22. Telegram notifier, §11 alerts and the dream identity-diff message
- **Goal**: `INotifier` with `TelegramNotifier` (token from the systemd credential) delivers the §11 alerts evaluated by the sweep — node silent, job stuck/re-queued, job failed 3×, signature failures, Claude auth expired, policy failure — one message per state change, every message naming the tenant; dream step 7 sends the diff when `profile.md` or `preferences.md` changed.
- **Scope**: `INotifier` seam + Telegram implementation, alert routing from the sweep's conditions (17), de-duplication, dream step 7 wiring, `zyggy notify` test verb.
- **Spec sections**: §11 Alerts; §7 Dream step 7; §8 Secrets (bot token); §14 Pluggable edges (`INotifier`).
- **Depends on**: 13, 17
- **Design rules that apply**: nothing outside `TelegramNotifier` references Telegram; alerts defined against the interface; messages never carry envelope bodies or mail content.
- **Gate served**: P4.
- **Definition of done**: unit tests per alert condition with a fake clock and a substituted `INotifier`; Telegram implementation against a mocked `HttpMessageHandler`; manual send and one real dream diff verified at the plan gate.
- **Risks**: secret (bot token); outbound channel — content discipline.
- **Status**: Not started

### 23. Personal mail triage on Central (Gmail + Outlook.com)
- **Goal**: Central's `.mcp.json` wires Gmail and Outlook.com MCP servers with refresh tokens from systemd credentials; a timer-submitted job runs `triage-mail`, merges both mailboxes into one summary that lands in `inbox/` via `remember`, drafts replies as Drafts, and never sends.
- **Scope**: MCP wiring and credentials on Central, `triage-mail` skill (personal profile), timer job, alert on auth failure via 22.
- **Spec sections**: §1 (no auto-send); §3 `triage-mail`; §8 Secrets (OAuth tokens); §13 Q5; §6 Central executing its own jobs.
- **Depends on**: 13, 22
- **Design rules that apply**: no automatic sending; mail content is untrusted data in prompts; secrets never in files under a repo.
- **Gate served**: P4.
- **Definition of done**: manual triage run reviewed at the plan gate (Drafts created, summary in memory, nothing sent); runbook for token refresh failure.
- **Risks**: OAuth secrets; third-party MCP servers (record provenance and version).
- **Status**: Not started

### 24. Work mail triage: metadata-only summaries, Drafts only (closes P4)
- **Goal**: on the work laptop, `triage-mail` (work profile) reads the inbox through the access the user already has (§13 Q2), classifies, creates Drafts only, keeps work facts in a separate `memory/<tenant>/<user>/work/` repo that never leaves the device, and emits a context envelope with only the §8 allowed fields, marked `share: true` where appropriate — and DLP proves it.
- **Scope**: `triage-mail` work profile, `work/` memory repo, scheduled job on the work node, `Gates/P4_HubAlertsMail.cs` (mechanical parts of 21–24: Hub tools, alert evaluation, allowed-field validation of the metadata envelope).
- **Spec sections**: §8 Work boundary; §7 Rules (`share: true`); §3 `triage-mail`; §10 Work laptop; §13 Q1/Q2.
- **Depends on**: 20, 21, 23
- **Design rules that apply**: no `mail.send`; no bodies, attachments, quoted text or recipient lists on the bus.
- **Gate served**: **closes P4**.
- **Definition of done**: `Gates/P4_*` green; user-executed: a triage run produces Drafts, a metadata-only envelope and nothing else on the bus; spec updated.
- **Risks**: work-laptop trust boundary.
- **Status**: Not started

### 25. Cost tracking and monthly budget enforcement
- **Goal**: `zyggy status --costs 30d` sums `cost_usd`, tokens and `num_turns` per tenant, machine and project from bus history; the sweep compares the tenant's month-to-date total against `policy.budget_usd_month`; `submit`, `submit_job` and the dispatcher refuse new jobs with `reason: budget_exceeded` when it is exceeded, and the alert fires.
- **Scope**: cost aggregation, budget check, alert condition, `--costs` output.
- **Spec sections**: §11 Cost tracking; §14 Model runtime (budget); §11 Alerts (`budget_exceeded`).
- **Depends on**: 18, 22
- **Design rules that apply**: `budget_exceeded` is a closed-enum reason; aggregation per tenant only.
- **Gate served**: P5.
- **Definition of done**: unit tests for aggregation and the threshold with fixtures; integration test: budget exceeded → refusal report and alert; `--costs` verified against a seeded history.
- **Risks**: none security-related.
- **Status**: Not started

### 26. Reproducible install: `zyggy init`, join token, Central image + Bicep, versioned `zyggy-core`, `zyggy diagnose` (closes P5)
- **Goal**: `zyggy init --role node --tenant <org> --join <token>` provisions a node (HMAC key, GitHub PAT, pinned `zyggy-core` version) from a short-lived Central-issued token; `zyggy init --role central --tenant <org>` bootstraps Central; Central ships as a Docker image plus a Bicep module and `docker compose`; `zyggy-core` is published as a versioned zip with checksum (O5); `zyggy diagnose` produces a redacted support bundle.
- **Scope**: §14 Reproducible install and Telemetry-and-support items, `Gates/P5_BudgetInstall.cs` (mechanical parts of 25–26), fresh-machine checklist.
- **Spec sections**: §14 Reproducible install, Telemetry and support; §10 Upgrade path.
- **Depends on**: 25
- **Design rules that apply**: secrets only through `ISecretStore`; bundle strips secrets and envelope bodies.
- **Gate served**: **closes P5**.
- **Definition of done**: `Gates/P5_*` green; user-executed: a fresh VM and a fresh laptop reach a green `/health` with one command each; bundle reviewed for leaks; docs.
- **Risks**: join token is a secret-distribution mechanism; infrastructure-as-code adds tooling.
- **Status**: Not started

## Open questions for the user (founding-spec conflicts and gaps found while building this roadmap)

Raised here so the technical-analyst does not have to rediscover them. Only the user amends the founding spec. Rows marked **Resolved 2026-09-28** reflect the tenancy review of that day; **on 2026-09-29 the user confirmed every remaining proposed resolution** (`Decided 2026-09-29`) and withdrew O15–O17 with candidate C. Where a founding-spec section was affected, the orchestrator amended it on 2026-09-29. "Affects" uses the 2026-09-29 numbering (after the drop of the candidate-C deliverable).

| # | Conflict / gap | Where | Affects | Proposed resolution | Status |
|---|----------------|-------|---------|---------------------|--------|
| O1 | The §4 envelope example lacked `schema`, `tenant`, `attempt`, `reason`, token/model fields and showed `key_id: k1`. | §4, §9, §14 | 03, 04, 15, 17 | **Resolved 2026-09-28** for `schema`, `tenant`, `key_id`, `reason`, tokens, `model`. **Decided 2026-09-29**: `attempt` added to the §4 example (founding spec amended by the orchestrator today); the mandatory-per-type field list is delegated to the technical-analyst of deliverable 03 (`_specs/03-envelope-signing.md`), who proposes it as a §4 amendment. | Decided 2026-09-29 |
| O2 | `archive/YYYY-MM/` appeared at repo root while §14 prefixes every path with `tenants/<org>/`. | §4, §14 | 04, 13, 17 | **Resolved 2026-09-28**: `archive/` is under `tenants/<org>/`. | Resolved 2026-09-28 |
| O3 | Report file naming: `reports/<id>.md` (job id) vs "file name = ULID" and `in_reply_to`. | §4 | 04, 06 | Fresh ULID per report/context file; `in_reply_to` carries the job id. Founding spec §4 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O4 | Claim push rejected: §4 "retry once, else skip" vs §5 "retry at most 3 times with jitter". | §4, §5 | 04 | §5 rule for all writes; a claim still failing after retries is skipped, not failed. Founding spec §4 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O5 | `zyggy-core` distribution: §9 submodule pinned by SHA vs §14 versioned zip, "no submodules"; `node.json` has `agentCore.pinnedSha`. | §9, §14 | 15, 26 | Checkout pinned by SHA in 15; versioned package in 26; `node.json` key renamed then. Founding spec §9/§14 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O6 | Sweep, dream ingestion, `agents.md` refresh and daily roll-up are mechanical git work described as skills/timers; §3 runs `claude -p "/dream"` directly while §6 says all scheduled work is a bus job to `nodes/central/jobs/`. | §3, §6, §7, §11 | 13, 14, 17, 25 | CLI verbs (`zyggy dream ingest|rollup|agents`, `zyggy sweep`) called by the skills; the dream submitted as a job for the ledger. Founding spec §3/§7 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O7 | §14 said the Hub takes tenant and user "from the caller's token" but v1 stdio has no token. | §14, §7 | 11, 21 | **Resolved 2026-09-28**: principal from the transport (stdio → `node.json`; HTTP → bearer). | Resolved 2026-09-28 |
| O8 | Telemetry default: §9/§10/§11 off by default vs §14 on by default with a local file exporter. | §9, §11, §14 | 07, 19 | Telemetry **OFF by default in v1**; collection and export are enabled only by policy (19). Founding spec §14 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O9 | §14 "Reproducible install" is in P0–P5 scope but no §12 phase owned it. | §12, §14 | 26 | Deliverable 26 closes P5 (user's value order); promote `zyggy init` earlier only if the work-laptop install in 20 would otherwise be too manual. | Decided 2026-09-29 |
| O10 | Lock file `.claude/agentbus.lock` (§6) vs §9 naming → `.claude/zyggy.lock`. | §6, §9 | 06 | `zyggy.lock` via the §9 naming map. Founding spec §6 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O11 | Memory layout `/srv/agent/central/memory/profile.md` vs `memory/<tenant>/<user>/`. | §7, §14 | 11, 13 | **Resolved 2026-09-28**: `memory/<tenant>/<user>/…` with `MemoryPaths`. | Resolved 2026-09-28 |
| O12 | `reason` enum listed with 7 members in §9 plus two elsewhere. | §9, §14 | 06 | **Resolved 2026-09-28**: closed 9-member enum in §9. | Resolved 2026-09-28 |
| O13 | CLAUDE.md stated `Zyggy.slnx` is empty; it already lists all six projects (only references and packages are missing). | CLAUDE.md | 01 | CLAUDE.md corrected by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O14 | The §13 rows "Git repository on GitHub as the only transport; ETag polling as doorbell" and "WebSocket / MQTT signalling … Deferred", the §1 non-goal "No WebSocket/MQTT signalling", the §1 constraint "Work laptop: only GitHub and Anthropic endpoints are reachable" (to be **tested**, never assumed), the §5 heading "One transport everywhere", and the §12 sentence "P0 has no dependency on any machine" (P0 needs all three). | §1, §5, §12, §13 | 04, 05, 07, 10 | **Decided 2026-09-29: transport A confirmed.** §13 rows restored to Decided with the condition "confirmed against the corporate proxy in P0"; §1 work-laptop egress constraint marked "to be verified in P0"; §12 "P0 has no machine dependency" replaced (P0 needs Central, home and work laptop). Candidate C dropped as a build candidate; kept as the one-line fallback in `_plans/decisions/0001-transport-and-vm.md`. Founding spec amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O15 | Candidate C would have made Central an internet-facing HTTPS server (TLS, DNS, firewall, rate limiting, ledger backup, single point of failure). | §5, §8, §10, §13 Q3 | — | Moot: C is not built; GitHub provides all of this for A. | Withdrawn 2026-09-29 (candidate C dropped) |
| O16 | Where a candidate-C server project would live in §3/§9. | §3, §9 | — | Moot: no server component; the second `IBusProvider` is a test double in the test project. | Withdrawn 2026-09-29 (candidate C dropped) |
| O17 | What memory and dream would need from a non-git transport (ledger location, `git pull` vs fetch). | §7, §6 | — | Moot: the ledger is the bus clone; `zyggy dream ingest` works on it through `BusRepository`. | Withdrawn 2026-09-29 (candidate C dropped) |
| O18 | The walking skeleton names the project path directly (no registry until 14): `project` carries an absolute path, contradicting the §4 comment "from registry" and §6 pre-flight step 1. After 14, is a path value still accepted? | §4, §6 | 06, 14 | P1: `project` = absolute path on the target machine, pre-flight checks it exists, is a git repo and has `.claude/`. From 14: registry name first; a path is accepted only when under a policy `dev_roots` entry, else `unknown_project`. Founding spec §4 comment and §6 step 1 amended by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O19 | §5 pushes with a deploy key over SSH (`github.com:22`); corporate proxies typically pass only 443. Transport A on the work laptop may need HTTPS push with the PAT via credential helper (§5 allows it), which changes the §5 credentials table and §8 deploy-key row for that machine. | §5, §8 | 04, 05, 20 | `zyggy probe reach` measures both; SSH on 22 is expected blocked on the work laptop, HTTPS push with the PAT via credential helper is the path to prove; the decision record states per machine which git auth path works and the §5/§8 tables are amended from it when 05 closes. Founding spec §5 annotated by the orchestrator 2026-09-29 (per-machine auth path, HTTPS push permitted). | Decided 2026-09-29 |
| O20 | Secret store during the spike: P0 runs on all three machines before the OS stores exist (09). §8 mandates Windows Credential Manager on laptops. | §8 | 03, 04, 09 | `FileSecretStore` (0600 / user-profile ACL) with spike-only keys on Windows during P0; keys rotated and the file store retired on Windows when 09 lands. Founding spec §8 annotated by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O21 | Latency measurement across machines needs a common clock; the spec sets no latency target. | §5, §13 | 04, 05 | Latency of A measured at the sender (send → report observed), no cross-machine clocks; the decision record proposes a target for the user to ratify. | Decided 2026-09-29 |
| O22 | The skeleton (P1) needs a minimal `zyggy-core` (`remember` and `dream` skills, `SessionStart` and `Stop` hooks, `PROTOCOL.md` placeholder) although the user listed "`zyggy-core` skills" under P2+. | §3 | 11, 12, 13, 15 | P1 ships only what memory and dream require; `bus`/`delegate`/`discover` skills, the `PreToolUse` hook and the verbatim `PROTOCOL.md` land in 15. Founding spec §3 annotated by the orchestrator 2026-09-29. | Decided 2026-09-29 |
| O23 | If transport A does not pass the corporate proxy in P0, the work laptop cannot be a product node. §8 has the `bus: disabled` fallback (local scheduled jobs only), written for a refused §13 Q1 that was later answered yes. | §8, §13 Q1 | 05, 20, 24 | `bus: disabled` stays the recorded fallback for a negative P0 result; 20 and 24 become `Blocked` with that reason while the candidate-C fallback line in the decision record is evaluated. Founding spec §8 annotated by the orchestrator 2026-09-29. | Decided 2026-09-29 |

## What to build next

**Deliverable 01 — Solution builds, tests, formats and publishes from a clean clone (thin harness)** (`_specs/01-solution-scaffolding.md`, then `_plans/01-solution-scaffolding.md`). The roadmap is approved (2026-09-29); the technical-analyst may start.

Why first: it is the only code deliverable with no dependencies, and the spike deliverables 03–04 need a solution that builds, test projects with FluentAssertions/NSubstitute, the bare-repo fixture, a format check and published `zyggy` binaries for three machines — none of which exist today (verified 2026-09-28: no project references, no `tools/`, no `tests/golden/`, no `.editorconfig`, no CI; `Class1.cs`/`UnitTest1.cs` placeholders still present). It is deliberately thin: everything the spike does not need is listed under *Defers*.

What follows it (P0 spike, in order): **02** Central VM soak (no code; starts the 7-day clock as early as possible) → **03** envelope + signer (the probe payload, full tenant shape) → **04** transport A proven through the corporate proxy: reachability matrix from Central, home and work laptop, HTTPS push with PAT on the work laptop, signed probe round trip → **05** soak result + transport confirmation + latency in `_plans/decisions/0001-transport-and-vm.md`, §1/§5 amendments, `Gates/P0_ProbeRoundTrip.cs`.

Hand-off brief for the technical-analyst (deliverable 01):

- **Founding-spec sections to read**: §9 solution structure, Packages table and Publish line; §9 design rules (no real `claude`, `IProcessRunner`, no LibGit2Sharp); §12 Definition of done; §6 Invocation (the command line the fake must accept); plus CLAUDE.md "Build and test" and the `integration-testing` skill (harness names: `BusRepoFixture`, `tools/fake-claude/{fake-claude.ps1,fake-claude.sh,scenarios/*.jsonl}`, `ZYGGY_FAKE_CLAUDE_SCENARIO`).
- **Design rules and decisions the spec must respect**: `Directory.Build.props` remains the single place for TFM/LangVersion/nullable/warnings-as-errors; packages restricted to the §9 table plus xUnit/FluentAssertions/NSubstitute/coverlet; `git` on PATH is the only external prerequisite; §13 decisions on .NET 10 and single-file self-contained binaries; nothing in 01 may pre-empt 03/04 (no GitHub-specific code, no `IBusProvider` yet, no envelope types).
- **Definition-of-done items the acceptance criteria must cover**: `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx`, `dotnet format Zyggy.slnx --verify-no-changes` green locally and in CI; CI publishes `Zyggy.Cli` single-file for `win-x64` and `linux-x64` as downloadable artefacts; fake-claude `done` scenario runs on both OSes and records its arguments; `BusRepoFixture` smoke test pushes and reads back a commit; template placeholders removed; `tests/golden/README.md` states the golden-file contract.
- **Explicit deferrals the spec must list as out of scope**: fake-claude `no-report`/`hang`/`error` scenarios and the real `stream-json` capture (06); §9 source folders; per-project READMEs beyond template removal; coverage; publish of Node/Hub (07/11); any production type from §4–§7.
- **Risk areas the spec must flag**: new NuGet dependencies (FluentAssertions version/licence choice, NSubstitute); CI holds no secrets and needs none.

Next action: invoke the `technical-analyst` subagent with the hand-off brief to produce `_specs/01-solution-scaffolding.md`; once that spec is approved with zero Open Questions, the `planner` turns it into `_plans/01-solution-scaffolding.md`.

## Change log

| Date | Change | Why |
|------|--------|-----|
| 2026-09-28 | Roadmap created: 25 deliverables across P0–P5 (+ one post-P5 §14 item); phase map proposed in place of the lost §12 diagram; 13 founding-spec conflicts logged as O1–O13; deliverable 01 marked Next. | First roadmap; repo is a `dotnet new` scaffold with no references, harness or CI. Awaiting user approval (HUMAN GATE). |
| 2026-09-28 | User-directed multi-tenancy review of the founding spec (§1–§14): tenant on every envelope, path, secret, log line; Hub principal model; `TenantId`/`Principal`/`MemoryPaths` in §9; closed 9-member reason enum. O1 (partly), O2, O7, O11, O12 marked resolved. | Make the spec consistent with §14 from day one. |
| 2026-09-29 | Decision: one bus repository per tenant; the repository is the isolation boundary, the `tenants/<org>/` prefix is uniformity plus defence in depth. Spec §4, §5, §11, §12, §13, §14 amended; infrastructure deliverable scope updated (repo per tenant, branch protection, repo-scoped credentials). | Git hosts authorise per repository, not per path; a shared repository would expose every tenant's ledger to every other tenant's machines. |
| 2026-09-29 | **Roadmap reordered risk-first (user decision).** P0 = de-risk: Central VM soak (7-day auth, Q4 resume, headless `claude -p`, Q3 cost) + a transport bake-off between two candidates measured from Central, home and work laptop through the real corporate proxy from day one, decision records in `_plans/decisions/`, §13 transport rows reopened (O14). P1 = walking skeleton including memory, Hub `get_context`/`remember`, context envelopes, nightly dream and `SessionStart` digest; project named by path until the registry (O18). P2–P5 extend in value order: registry + `zyggy-core` skills, worktrees, sweep; policy + DLP + work node; Hub bus tools + proxy, Telegram, personal and work mail; costs, reproducible install. 27 deliverables renumbered 01–27; 01 Next, all others Not started; O14–O23 added; `AgentSdkRunner` stays deferred. | The riskiest unknowns — VM viability and which protocol passes the corporate firewall — must be settled before the product is built on them; the spike is real, thin `Zyggy.*` code (no throwaway); the minimal product must include memory and dream. |
| 2026-09-29 | **Candidate C dropped; transport A confirmed subject to one P0 test; every open question confirmed; roadmap approved.** The HTTPS-API-served-by-Central candidate (and its server project) is removed as a build candidate and survives only as a one-line fallback in the decision record. Transport A (GitHub git, ETag polling, one repo per tenant) is confirmed subject to the corporate-proxy test from the work laptop: API polling with PAT, `git fetch`/`pull` over HTTPS, `git push` over HTTPS with PAT via credential helper (SSH on 22 expected blocked), measured from all three machines with a reachability matrix and one signed probe round trip Central → work laptop → Central. Old 05 removed; 06–27 renumbered 05–26 with every dependency and cross-reference fixed; 04 re-scoped to "Prove transport A through the corporate proxy" (second `IBusProvider` implementation is a test double, not a product transport); 05 becomes a single decision record `_plans/decisions/0001-transport-and-vm.md` (VM soak + transport + latency of A + C fallback line); P0 gate shrunk to VM soak checks + one signed probe round trip Central ↔ work laptop through the proxy + `Gates/P0_ProbeRoundTrip.cs` with tenant `acme`. Open questions: O1, O3–O6, O8–O10, O13, O14, O18–O23 Decided 2026-09-29 (founding spec amended by the orchestrator where applicable; O8 = telemetry off by default, policy-enabled; O10 = `zyggy.lock`; O21 = latency of A at the sender); O15–O17 withdrawn with C. Roadmap approved by the user at the HUMAN GATE; 01 stays Next. | Everything C would need (TLS, auth, per-machine revocation, DDoS protection, availability, backups, immutable audit log) GitHub already provides for A, and C would make Central an internet-facing single point of failure contrary to §8. Enterprise friendliness comes from §14 pluggable edges (customer's own GitHub Enterprise / Azure Repos / GitLab behind `IBusProvider`), not a second transport. Confirming all open questions unblocks the technical-analyst for 01. |
