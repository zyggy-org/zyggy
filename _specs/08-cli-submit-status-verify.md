# Spec: 08 — `zyggy submit`, `status`, `verify`

> Founding-spec sections: §3 Components (`AgentBus.Cli`: `submit`, `status`, `verify`, `--version`; "Central's node … also used for cron-style jobs Central submits to itself"); §4 Bus protocol (Repository layout, One repository per tenant, Write rules — "a sender writes only into `nodes/<target>/jobs/` … inside its own tenant's prefix", Envelope, Mandatory fields per type, Signature, Canonical form, State machine row 1 "sender: add + commit + push"); §5 Git operations (the write sequence every submission runs); §8 Audit ("`agentbus verify` re-checks all signatures in the machine's own tenant subtree (and the `tenant`-field-versus-path consistency of every file) and reports drift"), Isolation and Injection (bodies are data; signature before anything else); §9 solution structure (`AgentBus.Cli` verb list, design rules: `BusPaths`, seams, `IProcessRunner`, no static state), Packages (System.CommandLine); §11 Health endpoint ("Used by `agentbus status`"), Cost tracking (`status --costs`, deferred to 25), Runbooks (`restore-central.md` runs `verify`); §12 Definition of done; §13 decisions (one bus repository per tenant, HMAC envelopes, GitHub as the only transport, MVP scope, tenancy shape); §14 (repository-per-tenant enforcement, `diagnose`, budget). Roadmap entry: `_plans/ROADMAP.md` #08 (overview row line 106; entry lines 269–280 incl. "What 07 leaves to 08" and the hand-off brief; "What to build next" line 803). Project inputs honoured: `_specs/07-node-service-loop.md` (ex-Q8 `AddNodeServices` as the CLI's composition root; ex-Q9 no verdict field in `/health` — 08 defines "green"; `Gates/P1_WalkingSkeleton.cs` part 1b appended by 08), `_specs/04-transport-a-proxy-proof.md` (the System.CommandLine host, `BusHost`, exit codes, the `ZYGGY_BUS_API_BASE` test hook, ex-OQ4 "a foreign `tenants/<org>/` directory is ignored, not refused"), `memory/long-term/mvp-scope-decision.md`, `memory/long-term/repo-public-hygiene.md`, roadmap rule R1 (tools are .NET), `.claude/skills/build-feature/SKILL.md` (CLI verb shape), `.claude/instructions/public-api.md`.
>
> Status: **draft — ten Open Questions at the end; not planner-ready until the owner answers them.** Nothing in this file edits the founding spec; the wording it would need is listed under "Founding-spec amendments to propose".

## Current state (verified against the repo on 2026-10-24, branch `feature/07-node-service-loop`)

| Roadmap item | State today | Decision for 08 |
|---|---|---|
| CLI host | `src/Zyggy.Cli/CliApplication.RunAsync(args, CliEnvironment)` builds one `RootCommand` (`memory`, `dream`, `m365`, `brief`, `linkedin`, `probe`, `secret`); `RawVerbs` dispatches the script-replacement verbs to Core "verb hosts" with an injected `VerbIo`; parse errors → stderr + exit 2; `--version` is System.CommandLine's root option (proven by `tests/Zyggy.Integration/Cli/ZyggyCliTests.cs`, binary only). `ExitCodes` (`Ok 0, NoRun 1, Usage 2, Configuration 3, Locked 4, Aborted 5, Failed 6, PushDeferred 7`). The probe verbs write to `Console.Out/Error` directly; `Zyggy.Cli` has **no** `InternalsVisibleTo`. | `submit`, `status`, `verify` are three new System.CommandLine commands on the same root (one file per verb); the verb logic lives in `Zyggy.Core` and is rendered by thin commands; `CliApplication` gains an in-process entry point with injected `VerbIo` (Q9). `--version` unchanged. |
| Shared bus-verb plumbing (04/07) | `BusHost`: `--config`/`--verbose`, `LoadConfiguration` (exit 3, one stderr line), `BuildServices` = console logging to stderr + **`AddNodeServices(configuration)`** (Core's one composition root, 07 ex-Q8) + the `ZYGGY_BUS_API_BASE` test hook + an `extra` registration callback. `ProbeCommand.CheckCheckoutAsync` (private): not a repository / wrong branch → exit 3, touching nothing. | Reused as is; `CheckCheckoutAsync` moves to `BusHost` (shared by `probe`, `submit`, `status`, `verify`) and additionally refuses a checkout without `tenants/<tenant>/` (exit 3). No second DI tree (roadmap (b)). |
| The sender's write | `BusRepository.SubmitAsync(signedJob)` exists (04, used by `ProbeSender`): refuses another tenant's envelope, builds the path with `BusPaths.Jobs(me.Tenant, job.To)`, and calls `AssertMayWrite(path, BusFolder.Jobs, submission: true)` — the `submission` flag is exactly the §4 sender rule (own tenant, the `jobs/` folder, **any** machine incl. its own); every other write stays machine-bound. The write runs the §5 sequence under the cross-process checkout lock (pull → add → commit `<tenant> job <id> <from>→<to>` → push with the retry rule → `Pushed`/`Deferred`/`WriteFailed`). | **No change to the write rule and no weakening**: `submit` is a second caller of the existing `SubmitAsync`. The hand-off's concern is answered by the code: the listener's rule and the sender's rule are already two branches of one assertion. |
| Envelope construction | `Envelope.CreateJob(EnvelopeHeader, JobFields, body)` writes `schema: 1`, `priority`, `attempt`, `worktree` always, omits null optionals, and **interprets its own front matter through `EnvelopeParser` before returning** (a field that breaks §4 throws). `EnvelopeSigner.SignAsync`, `EnvelopeWriter.Serialize` (refuses an unsigned envelope), `FrontMatterEmitter` quotes scalars per the canonical-form rules. `JobFields(Project, Agent, Worktree, AllowedTools, ReportBack, TimeoutMinutes, Deadline, Attempt)`; `EnvelopePriority { Low, Normal, High }`. | Consumed as is. The ⚠️ "body must not leak into front matter" risk is closed by (1) a typed request with field validation in Core and (2) a round-trip self-check (serialise → parse-and-verify → compare) before any write. |
| Reading the bus | `BusRepository` lists only this machine's `jobs/` and `jobs/claimed/`; `FindReportsAsync(reporter, inReplyTo)` reads another machine's `reports/` (verified, addressed to me); `HeadShaAsync`; `BusPaths.TryParse` reads a path back to `(tenant, machine, folder, id)` strictly. No tenant-wide listing, no audit walk. `BusCheckoutLock` (internal): `WriteLockName`, `ListenLockName`, 60 s wait. | Two new read-only Core classes: `BusInventory` (tenant-wide counts, recent reports, one envelope's trace — `status`) and `BusAuditor` (`verify`). Both read the working tree under the checkout write lock so they never see a half-applied `git mv`; neither writes. |
| `/health` (07) | `Zyggy.Node.NodeHealth` (fourteen §11 fields, camelCase, nulls written, `UtcSecondsConverter`, source-generated `NodeHealthJsonContext`), served by `HealthServer` (Kestrel, loopback, `Cache-Control: no-store`, 404/405 elsewhere); **no verdict field** (07 ex-Q9). `NodeHostOptions.HealthPort` 4711, `0` in tests (ephemeral). `Zyggy.Cli` references `Zyggy.Core` only — a reference to `Zyggy.Node` would drag the `Microsoft.AspNetCore.App` framework reference (+26 MB single-file) into `zyggy`. | The record, converter and JSON context move to `Zyggy.Core.Health` (Q8); `Zyggy.Node` keeps the server. `status` reads it with a loopback `HttpClient` (no proxy) and `HealthRules.Evaluate` (pure, fake-clock-testable) defines green/amber/red/down. |
| `node.json` reader | `NodeConfigurationReader`: `tenant` required, `machine`, `signWith` optional (required by the verbs that sign), `bus.*`, `poll.*`, `secrets.root`, `claude.*`, `devRoots`, `nodeDir`, `workHours`, `maxConcurrentJobs`, `log.level`; secret-looking keys refused; `--config` / `ZYGGY_NODE_JSON` / per-user default. Documented in `src/Zyggy.Cli/README.md`. | **No new key.** `submit` requires `signWith`; `status` and `verify` do not (they only verify). README rows for the three verbs rewritten; one line for the two test-only environment hooks. |
| Integration harness | `ZyggyCli.RunAsync` runs the **built binary** as a child process; `M365InProcess`/`BriefInProcess` run Core verb hosts **in process** with `VerbConsole`; `NodeHostHarness` builds the production node host in process with an ephemeral health port, `BusRepoFixture` (bare repo, `SenderDir` for `central`, `CloneDir` for `home-laptop`), `SeedSignedJobAsync`, `SeedRawFileAsync`; `Gates/P1_WalkingSkeleton.cs` part 1 ends with `#endregion` and the header says "append a `#region Part 1b — 08`". | In-process invocation of the whole CLI (`ZyggyCliInProcess`, Q9) with injected environment, `VerbIo` and the same test registrations as the host; the gate's part 1b appended. |
| F3 `unverified_report` (roadmap precondition "bugfix first") | `src/Zyggy.Core/Bus/Probe/ProbeOutcome.cs` has `UnverifiedReport` (`unverified_report`, "decision 0001 F3") and `ProbeStatsTests` asserts its column — the bugfix is **in the code**; the roadmap line 803 still says "still open" and the Cli README `probe stats` row lacks the column. | Not 08's work; listed for the project-manager under "Notes". |
| Packages | `System.CommandLine` 2.0.11, `Microsoft.Extensions.*` 10.0.12, `Ulid`, `YamlDotNet`; no HTTP client package needed (BCL). | **No new package.** |

## User Story

**As** the owner at a terminal on any machine that has a bus checkout and the tenant's signing key (Central first, the home laptop after 10), **I want** `zyggy submit` to turn a prompt and a target into a signed job on the bus without my touching git or YAML, `zyggy status` to tell me in one screen whether this machine's node is green and what sits on the bus for my tenant (queued, claimed, rejected, reported — and the fate of one job by id), and `zyggy verify` to re-check every signature and every `tenant`-versus-path pairing in my tenant's subtree and name any drift, each with an exit code a script can read — **so that** the P1 walking skeleton has its human entry point and its human read-out (submit → node → `claude -p` → report → `status`), deliverable 10 can prove the real machines with the same three verbs, and the §8 audit promise ("reports drift") is a command rather than a sentence.

---

## Acceptance Criteria

Tenant `acme`, machines `central` (sender, `BusRepoFixture.SenderDir`) and `home-laptop` (node, `CloneDir`), key `acme/1`. **U** = unit (`tests/Zyggy.Core.Tests`, `FakeTimeProvider`, substitutes, scratch directories — no git), **I** = integration (`tests/Zyggy.Integration`, the CLI **in process** against the bare repository, real `git`, the fake `claude` where a node runs; never the real `claude`, never GitHub), **G** = `Gates/P1_WalkingSkeleton.cs` part 1b, **D** = document, **CI** = pipeline.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 (U) | `JobRequest.TryCreate` with: `--to "Home Laptop"`; `--project ""` / `"x\nsig: hmac-sha256:00"` / a 1,025-character string / `"../x"`; `--agent "a: b"` / `"a\n"` / 65 characters; `--timeout 0` / `1441`; `--allowed-tools "Read,,Grep"` / `"Read\n---"` / an item of 129 characters; `--priority urgent`; body `""` / `"   \n"` / 64 KiB + 1 byte | Validation | A `JobRequestError(Field, Detail)` naming `to`, `project`, `agent`, `timeout_minutes`, `allowed_tools[1]`, `priority` or `body`; no envelope is built. Accepted: `--project C:/src/calizr`, `/home/u/src/calizr`, `C:\src\calizr`, `calizr`; `--allowed-tools "Read, Grep, Bash(dotnet *)"` → `["Read","Grep","Bash(dotnet *)"]`; `--priority high`; a body `"---\nnot front matter\n"`. |
| AC-2 (U) | A valid `JobRequest`, `FakeTimeProvider`, a substituted `ISecretStore` holding `acme/1` | `JobSubmitter.BuildAsync` (the step before the write) | The bytes serialise with `EnvelopeWriter`; `EnvelopeSigner.ParseAndVerifyAsync(bytes, acme)` accepts them; the parsed `Header.To/From/Priority`, `Job.Project/Agent/TimeoutMinutes/AllowedTools` equal the request; `Job.Worktree == false`, `Job.Attempt == 1`, `Job.ReportBack == null`, `Job.Deadline == null`; `Body` equals the normalised body byte for byte (CRLF → LF, exactly one trailing `\n`); the front matter has **no** key other than the §4 names; a body beginning with `---\n` is entirely body. A substituted emitter that drops a quote (test double) makes the self-check fail → `SubmitRefused("project", …)`, and `BusRepository.SubmitAsync` is **never** called. |
| AC-3 (U) | `HealthRules.Evaluate(health, now, PollOptions.Default, cliVersion)` | The matrix: `health == null` (unreachable); all fields fresh, `claudeAuthOk true`, same version; `lastPollUtc` 181 s old with `backoffUntilUtc null`; `lastPollUtc` 181 s old with `backoffUntilUtc` in the future; `claudeAuthOk false`; `claudeAuthOk null`; `rateLimitRemaining 99`; `lastPullUtc null`; node version ≠ CLI version; `jobsRunning 2` | `Down` ("no node service answering"); `Green` (no reason); `Red` ("not polling: last poll 181 s ago"); `Amber` ("backing off until …"); `Red` ("claude not logged in"); `Amber` ("claude auth not yet probed"); `Amber` ("rate limit low: 99"); `Amber` ("never pulled"); `Amber` ("node 0.9.0, cli 0.10.0"); `Green` with the informational line "2 job(s) running". The level is the worst reason; reasons are listed in the order above; the function reads no clock but `now`. |
| AC-4 (U) | A scratch tree `tenants/acme/…` written with `SeedSignedJobAsync`-style helpers (no git), a substituted `ISecretStore` with `acme/1` | `BusAuditor.RunAsync` over: a valid job, report, context and archived report; a report with one signature hex flipped; a file under `nodes/home-laptop/reports/` whose `tenant` is `other-org`; a file with `key_id: other-org/1`; a file signed with `acme/2` (absent from the store); a file whose `id` differs from its name; a `report` envelope in `jobs/`; a report in `nodes/home-laptop/reports/` with `from: central`; a job in `nodes/home-laptop/jobs/` with `to: central`; `jobs/notes.txt`; `jobs/README.md`; `registry/home-laptop.yaml`; `tenants/acme/policy.yaml`; a directory `tenants/other-org/` containing a malformed `.md`; a directory `tenants/Bad Name/` | Entries in path order: `ok` ×4; `invalid_signature`; `tenant_mismatch`; `key_id_tenant_mismatch`; `unknown_key` (detail names `acme/2`); `id_differs`; `type_folder_mismatch`; `writer_mismatch` (`from`); `writer_mismatch` (`to`); `skipped` (not an envelope name); `malformed` (an `.md` whose name is not a ULID is judged, not skipped); `skipped` (registry, unsigned in v1); `unexpected_path`; **no entry for any file under `tenants/other-org/`** and `ForeignTenants == ["other-org"]`; `unexpected_path` for `tenants/Bad Name/`. `Ok 4, Failed 9, Skipped 2`; `IsClean == false`. The auditor opened no file outside `tenants/acme/` (asserted with a recording file reader or by a file inside `other-org/` whose read would throw). |
| AC-5 (U) | A scratch tree with `nodes/central/` (0 queued, 2 reports), `nodes/home-laptop/` (1 queued, 1 claimed, 1 rejected, 3 reports of which one unverifiable), `archive/2026-09/` | `BusInventory.ListAsync(recent: 10)` and `TraceAsync(id)` for a queued, a claimed, a rejected, a reported (`done`, `cost_usd` set) and an unknown id | `Machines` in ordinal order with the exact counts; `RecentReports` = the 5 reports by ULID descending, the unverifiable one carrying `Verified == false` and its reason; `TraceAsync` → `State` `queued` / `claimed` / `rejected` with the machine, the job's summary and every report answering it (status, reason, cost, the body text); `null` for the unknown id. |
| AC-6 (U) | `src/` | Source hygiene (extends 07 AC-12) | `Zyggy.Cli.csproj` references `Zyggy.Core` only (no `Zyggy.Node`, no `FrameworkReference`); the new Core classes contain no `Console.`, no `DateTime.UtcNow`/`TimeProvider.System`, no static mutable field, no string-built bus path (every path from `BusPaths`); `HealthClient` builds its handler with `UseProxy = false` and accepts only loopback hosts; nothing in `src/Zyggy.Cli` references `Process`, `HttpClient` (other than through `HealthClient`), GitHub, `claude` or a secret store directly. |
| AC-7 (I) | `central`'s `node.json` (`signWith: acme/1`, `bus.localPath` = `SenderDir`), the secret store seeded | `zyggy submit --to home-laptop --project <scratch project path> --timeout 5 --allowed-tools Read,Grep --priority high "Read README.md and finish with a REPORT section.\n"` in process | Exit 0; stdout exactly `submitted <ULID> to home-laptop (tenants/acme/nodes/home-laptop/jobs/<ULID>.md)`; the file exists on `main` at `BusPaths.EnvelopeFile(BusPaths.Jobs(acme, home-laptop), id)`; the commit subject is `acme job <id> central→home-laptop`; the file verifies with `acme/1`; its body bytes equal the normalised argument; `created` is within 5 s of now; `git status --porcelain` of the sender checkout is empty afterwards; `SecretSweep` over stdout, stderr, the file and `git log -p`. The same with no positional body and the body on stdin behaves identically. |
| AC-8 (I) | As AC-7 | `submit --to central …` (a self-addressed job, §3) / `--to "Home Laptop"` / without `signWith` in `node.json` / with `acme/1` absent from the store / `bus.localPath` not a repository / checkout on another branch / checkout without `tenants/acme/` / a dirty sender checkout (an untracked file under `tenants/acme/`) / a remote that refuses every push | Exit 0 and the file under `nodes/central/jobs/` / exit 2, one stderr line naming `to`, **nothing written, no commit** / exit 3 `signWith missing` / exit 6 `signing key acme/1 is not in the secret store; run: zyggy secret set hmac/1 --hex`, nothing committed / exit 3 / exit 3 / exit 3 / exit 6 `pull: …` (04's rule: a sender never stashes), working copy restored / exit 7 `submitted <id> … (committed locally, not yet pushed: the next bus write on this machine pushes it)` with the commit present locally and absent on `main`. |
| AC-9 (I) | `home-laptop`'s `node.json`; no node running | `zyggy status` in process with `ZYGGY_HEALTH_URL` pointing at a closed loopback port | Exit 6; the first line `node home-laptop (acme) — health: down — no node service answering on <url>`; the bus block is still printed (machine table and recent reports from the checkout); nothing written; no connection attempt to any non-loopback address. |
| AC-10 (I) | The checkout holds: a queued job for `home-laptop`, a claimed job, a rejected job with its `status: rejected` report, a `done` report with `cost_usd` | `zyggy status` / `zyggy status --id <queued>` / `--id <claimed>` / `--id <rejected>` / `--id <reported job>` / `--id <unknown>` / `--id not-a-ulid` | The machine table row `home-laptop  queued 1  claimed 1  rejected 1  reports 2` and the two reports under `recent reports`, the rejected one with its reason / exit 0 and `queued at home-laptop since <age>` / `claimed by home-laptop since <age>` / `rejected by home-laptop: <reason>` plus the report line / exit 0, the job line, the report line `done … cost_usd 0.0123 …` and the body after a blank line (Q6) / exit 1 `not found in tenant acme` / exit 2. In `--id` mode the health block is not printed and the exit code is 0 found / 1 not found. |
| AC-11 (I) | A clean checkout with verified files for both machines | `zyggy verify` / then one report tampered on disk (a signature byte flipped, committed to `main`) / then a report rewritten with `tenant: other-org` inside `tenants/acme/…` / then a directory `tenants/other-org/nodes/home-laptop/jobs/<ulid>.md` seeded (ex-OQ4 content) | Exit 0, summary `verified <n> ok, 0 failed, <k> skipped; 1 tenant directory; HEAD <sha12>` / exit 6 with `FAIL tenants/acme/nodes/home-laptop/reports/<ulid>.md invalid_signature` / exit 6 with `FAIL … tenant_mismatch (tenant)` / exit 6 with `WARN tenants/other-org/ second tenant directory (§13: one bus repository per tenant); not read` and `2 tenant directories` in the summary (Q1). In every case `git status --porcelain` is unchanged, no commit is made, no file under `tenants/other-org/` is opened, and the listener lock is not taken (a running node is not disturbed). |
| AC-12 (G) | `P1_WalkingSkeleton`, `#region Part 1b — 08`, the host of part 1d running over `CloneDir` (ephemeral health port) | (a) `zyggy submit` in process from `central`'s `node.json` over `SenderDir` naming a scratch project (scenario `done`); (b) wait until one report is on `main` and `jobsRunning` is 0; (c) `zyggy status` in process with `home-laptop`'s `node.json` and `ZYGGY_HEALTH_URL` = the bound port; (d) `zyggy status --id <id>`; (e) `zyggy verify` | (a) exit 0 and the `submitted` line; (b) the report is `done`, `in_reply_to` the submitted id; (c) exit 0, first line ends `health: green`, the table shows `home-laptop … reports 1`, `recent reports` lists the report; (d) exit 0, `done`, the REPORT body text; (e) exit 0, `0 failed`, `1 tenant directory`. Part 1 is not restructured; part 1b reuses the bottom helpers; `SecretSweep` over every stdout/stderr. |
| AC-13 (I) | The built binary and the in-process entry point | `zyggy --version` | Exit 0, the informational version on stdout (the existing binary test stays; an in-process twin is added). |
| AC-14 (D) | Documents | Review | `src/Zyggy.Cli/README.md`: the `submit`, `status`, `verify` rows rewritten with options, output and exit codes, `--costs` marked "deliverable 25", the `probe stats` row gains the `unverified_report` column, a "Test hooks" line for `ZYGGY_BUS_API_BASE` and `ZYGGY_HEALTH_URL`, "Troubleshooting — Submit / Status / Verify" rows for every failure mode below; `src/Zyggy.Core/README.md` (the runbook home until 10) gains "Verify: drift found", "Verify: second tenant directory", "Verify: unknown key after rotation", "Status: down", "Status: red/amber", "Submit: deferred push"; `PROTOCOL.md`/§4 unchanged (no envelope field added or reinterpreted); the founding-spec amendment A8-1 below pasted by the orchestrating session or recorded as pending in the journal. |
| AC-15 (CI) | Clean clone, Windows and Linux | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`; `dotnet publish src/Zyggy.Cli -c Release -r win-x64|linux-x64 --self-contained -p:PublishSingleFile=true` and the smoke run (`zyggy --version`) | Green on both runners; the published `zyggy` single-file size recorded at the gate and **not** grown by a framework reference (76.9 / 76.0 MB at 07's gate is the baseline); `Directory.Packages.props` unchanged. |
| AC-16 (U/I) | §8 | Every test above | `status` and `verify` call `ParseAndVerifyAsync` before showing a file as verified, and tag anything else `(unverified: <reason>)`; a body printed by `status --id` is printed verbatim as data under a `body:` line and never interpreted; the PAT and the HMAC key appear in no output, no commit, no log (`SecretSweep`); `--dangerously-skip-permissions` appears nowhere in `src/`; `HealthClient` never leaves loopback; no test resolves the real `claude` (`Tests_NeverResolveRealExternalPrograms` stays green). |

---

## Decision Table

| Founding-spec / brief item | Verdict | Target type / library | Justification |
|----------------------------|---------|-----------------------|---------------|
| §3/§9 `submit`: build, sign and publish a job envelope from the command line | Keep | `Zyggy.Cli.Commands.SubmitCommand` (thin) → `Zyggy.Core.Bus.JobRequest.TryCreate` + `JobSubmitter` → the existing `Envelope.CreateJob` / `EnvelopeSigner.SignAsync` / `BusRepository.SubmitAsync` | The P1 gate's human entry point and the `delegate` skill's (15) and the Hub's (21) future back end. Every piece below the request exists since 03/04; 08 adds the typed request, the self-check and the verb. |
| §4 Write rules: "a sender writes only into `nodes/<target>/jobs/` … inside its own tenant's prefix" vs the node's own-folder rule (hand-off: "decide how a sender's write is permitted without weakening the node's rule") | Keep (already built) | `BusRepository.AssertMayWrite(path, BusFolder.Jobs, submission: true)` inside `SubmitAsync` | 04 already models both rules as one assertion with a `submission` branch: the folder must be `jobs/` and the tenant mine; only then may the machine differ. `submit` adds no path and no flag; the listener's claim/report/reject calls keep `submission: false`. A self-addressed job (Central's cron-style jobs, §3) passes the same branch. |
| ⚠️ "argument validation must not let a body leak into front matter" | Reshape — closed in Core, not in the verb | `JobRequest` (typed, validated: labels, a `ProjectReference`, bounded lengths, no control characters) + `JobSubmitter`'s round-trip self-check (serialise → `ParseAndVerifyAsync` → field and body equality) before `SubmitAsync` | A leak can only happen through an option value carrying a line break or a YAML-significant sequence, or through the emitter; the first is refused at validation (exit 2, nothing built), the second is caught by re-parsing the exact bytes that would be pushed. Both are unit-testable without git. The body cannot leak the other way: it is appended after the closing `---` and the parser stops the front matter at the first `---` line, which the emitter never writes inside the mapping. |
| §4 optional job fields `worktree`, `report_back`, `deadline`, `attempt` as `submit` options | Defer | `JobFields` defaults (`false`, `null`, `null`, `1`) | `worktree` is 16, `report_back` has no consumer before 15/16, `deadline` has no consumer in v1, `attempt` is the sweep's (17). An option without a consumer is a knob to document and test for nothing; the roadmap's option list omits them. |
| `submit` waiting for the report (as `probe send` does) | Defer | — | A job runs up to `timeout_minutes` (30 by default); a blocking verb would be the wrong shape; `status --id` is the read-out. 21's `job_status` is the programmatic one. |
| §3/§11 `status`: this node's health from the local endpoint + the state of jobs on the bus; 07 ex-Q9: "`zyggy status` defines what green means" | Keep + Reshape (the verdict rule is 08's) | `StatusCommand` → `Zyggy.Core.Health.HealthClient` (loopback `HttpClient`), `HealthRules.Evaluate` (pure), `Zyggy.Core.Bus.BusInventory` (counts per machine, recent reports, `TraceAsync(id)`) | P1's read-out and 10's proof ("a report visible in `zyggy status`"). The verdict is a pure function of the fourteen fields, `now`, the `poll` intervals and the CLI's own version, so the rule has a unit test matrix and the Node stays verdict-free (07 ex-Q9). |
| §11 `status --costs 30d` | Defer to 25 | — | Roadmap; the README row says so. |
| `status --json` | Defer | — | No gate or deliverable before 21/22 consumes it; 21's `job_status` and 22's alerts read `BusInventory`/`HealthRules` directly, not the text. |
| 07 `Zyggy.Node.NodeHealth` + `UtcSecondsConverter` + `NodeHealthJsonContext` | Reshape (Q8) | Moved to `Zyggy.Core.Health` (same shape, same JSON); `Zyggy.Node.HealthServer` serialises the Core type | The §11 body is a shared contract between the Node (07), `status` (08), the registry subset (14), `list_nodes` (21) and the alerts (22); one type in Core is the only way the CLI can read it without referencing `Zyggy.Node` (and Kestrel). Behaviour-preserving; 07's `NodeHealthJsonTests` move with it. |
| §8 Audit: "`verify` re-checks all signatures in the machine's own tenant subtree (and the `tenant`-field-versus-path consistency of every file) and reports drift" | Keep | `VerifyCommand` → `Zyggy.Core.Bus.BusAuditor` (walks `tenants/<tenant>/` only, `BusPaths.TryParse` + `EnvelopeSigner.ParseAndVerifyAsync(bytes, tenantFromPath)` per file) | §8 names it; §11 `restore-central.md` runs it; the P1 DoD lists its three detections. The parser already yields `tenant_mismatch` and `key_id_tenant_mismatch` when given the path's tenant, so "field-versus-path" costs no new code. |
| `verify`'s consistency checks beyond the §8 sentence: `id` vs file name, `type` vs folder, writer (`from`/`to`) vs folder (Q2) | Reshape (pending Q2) | `AuditOutcome` members `id_differs`, `type_folder_mismatch`, `writer_mismatch` | Each is one comparison over fields the auditor has already parsed and is a §4 rule ("file name = ULID", the write rules); drift there is exactly what an audit exists to show. They are verify's vocabulary, not new §4 rejection reasons. |
| §4/§13/§14 "exactly one `tenants/<org>/` directory": `verify` reporting a second one | Keep as **reported, never read, never refused** (Q1) | `AuditReport.ForeignTenants`, a `WARN` line, counted as drift | The roadmap DoD requires the detection; §14 calls it "would have to be added for a second tenant"; counting directory names under `tenants/` is a handful of lines that protects the one tenant from a protocol violation, and 04's ex-OQ4 decided the node ignores such a directory. A conflict with §12/§14's wording — Q1 and amendment A8-1. |
| §4 "every file HMAC-signed" vs the unsigned `registry/<machine>.yaml` of §4's example | Defer the policy to 14; `verify` skips | `skipped` entry, shown with `--verbose` | No registry file exists before 14; whether it is signed is 14's design. Flagging it now would make every future checkout "drifted" by design. |
| `verify` over git history (every state change is a commit) | Defer | — | §8's sentence is about files; a history audit (who moved what) is a different tool and no gate needs it. |
| `verify` of other tenants' files | Out (§4 "no machine reads outside its prefix") | never opened | ex-OQ4 of 04; AC-4/AC-11 assert it. |
| Roadmap: "shared options loading from `node.json`" | Keep (already built) | `BusHost.LoadConfiguration`, `BusHost.CheckCheckoutAsync` (moved from `ProbeCommand`, + the `tenants/<tenant>/` check) | 04 built it; 08 shares it among four verbs instead of one. |
| Roadmap: "in-process invocation harness for integration tests" | Reshape (Q9) | `CliApplication.RunAsync(args, CliEnvironment, VerbIo, Action<IServiceCollection>? configure)`; `InternalsVisibleTo("Zyggy.Integration")` on `Zyggy.Cli`; test helper `ZyggyCliInProcess` returning `(exit, VerbConsole)`; the `ZYGGY_HEALTH_URL` hook beside `ZYGGY_BUS_API_BASE` | The gate's host runs in process on an ephemeral port, so `status` must be invoked in process with the port injected; the binary tests stay for `--version` and the probe verbs. Same rule as 07: only seam implementations and options are swapped, through the same `extra` callback `BusHost.BuildServices` already has. |
| Roadmap: `zyggy --version` works | Keep (already built) | System.CommandLine's root `--version` | Proven by `ZyggyCliTests`; AC-13 adds the in-process twin. |
| §9 CLI through System.CommandLine 2.0.11 | Library (already) | — | Argument parsing, `--help`, typed options; no bespoke parsing. |
| Reading `/health` | Library (BCL) | `HttpClient` with a `SocketsHttpHandler { UseProxy = false }`, 2 s timeout, `System.Text.Json` source-generated context | No package; the proxy is switched off so a corporate `HTTPS_PROXY` on the work laptop can never capture `localhost`. |
| 07's design rule "every bus access via `BusRepository`/`BusPaths`/`IBusProvider`; signing via `EnvelopeSigner` + `ISecretStore`; the CLI never handles git or transport credentials" | Keep | AC-6 | `submit` pushes through `BusRepository` → `IBusProvider.PushAsync`; `status`/`verify` read the working tree through `BusPaths`; `HeadShaAsync` is the only git call and goes through `GitClient`. |
| 10's install, services, URL/port runbook; 14 registry; 15 `report`, `discover`; 16 `run`; 17 sweep; 21 Hub tools; 25 `--costs`; §14 `init`, `diagnose` | Defer / Out | — | Roadmap and §14. |

---

## Contracts

### Namespaces and visibility (public-api.md)

| Namespace | Public | Internal (visible to the test projects) |
|-----------|--------|------------------------------------------|
| `Zyggy.Core.Bus` | `JobRequest` (+ `TryCreate`, `JobRequestError`), `ProjectReference`, `JobSubmitter`, `SubmitResult` (closed: `Submitted`, `SubmitDeferred`, `SubmitFailed`, `SubmitRefused`), `BusInventory`, `InventoryReport`, `MachineCounts`, `EnvelopeSummary`, `EnvelopeTrace`, `TraceState`, `BusAuditor`, `AuditReport`, `AuditEntry`, `AuditOutcome` | the checkout lock use (`BusCheckoutLock`, existing internal) |
| `Zyggy.Core.Health` | `NodeHealth` (moved from `Zyggy.Node`, Q8), `HealthClient`, `HealthProbe` (closed: `Reachable(NodeHealth)`, `Unreachable(string Detail)`), `HealthRules`, `HealthVerdict`, `HealthLevel` | `UtcSecondsConverter`, `NodeHealthJsonContext` (moved) |
| `Zyggy.Cli` | — (console project) | `CliApplication.RunAsync(args, env, io, configure)`, `SubmitCommand`, `StatusCommand`, `VerifyCommand`, `BusHost.CheckCheckoutAsync` — `InternalsVisibleTo("Zyggy.Integration")` added |
| `Zyggy.Node` | `HealthServer` unchanged in behaviour; `NodeHealth` no longer declared here | — |

### `submit` — request, self-check, result

```csharp
namespace Zyggy.Core.Bus;

/// A project named the §4 way: a registry label ([a-z0-9-], 1–63) or an absolute path in POSIX (/…) or Windows drive (X:\… or X:/…) form,
/// judged by shape on every OS (Central sends Windows paths to the laptops). ≤ 1024 chars, no control characters, no "..".
public readonly record struct ProjectReference(string Value, bool IsPath) { public static bool TryParse(string? text, out ProjectReference result, out string? detail); }

public sealed record JobRequestError(string Field, string Detail);          // Field: to | project | agent | timeout_minutes | allowed_tools[i] | priority | body

public sealed record JobRequest(
    MachineName To,
    ProjectReference Project,
    string? Agent,                       // 1–64 chars of [A-Za-z0-9._-]
    int? TimeoutMinutes,                 // 1–1440
    IReadOnlyList<string>? AllowedTools, // each 1–128 chars, trimmed, no control characters; null when the option is absent (policy default applies, §4)
    EnvelopePriority Priority,           // low | normal | high; Normal when absent
    ReadOnlyMemory<byte> Body)           // normalised: \r\n → \n, exactly one trailing \n, non-blank, ≤ 65 536 bytes (Q7)
{
    public static JobRequestError? TryCreate(string to, string project, string? agent, int? timeoutMinutes, IReadOnlyList<string>? allowedToolsRaw, string? priority, string body, out JobRequest? request);
}

public abstract record SubmitResult;
public sealed record Submitted(EnvelopeId Id, string RelativePath, int PushAttempts) : SubmitResult;
public sealed record SubmitDeferred(EnvelopeId Id, string RelativePath, int PushAttempts, string Detail) : SubmitResult;   // committed locally, not pushed (§5)
public sealed record SubmitFailed(string Stage, string Detail) : SubmitResult;        // lock | pull | sign | add | commit (BusRepository's stages)
public sealed record SubmitRefused(string Field, string Detail) : SubmitResult;       // the round-trip self-check disagreed; nothing written

public abstract record BuildResult;
public sealed record Built(Envelope Envelope, byte[] Bytes) : BuildResult;           // the exact bytes that will be written
public sealed record BuildRefused(string Field, string Detail) : BuildResult;         // the self-check disagreed; becomes SubmitRefused

public sealed class JobSubmitter
{
    // DI: BusRepository, EnvelopeSigner, BusIdentity (me), KeyId (signWith), TimeProvider, ILogger<JobSubmitter>
    /// CreateJob(header(me.Tenant, EnvelopeId.New(now), me.Machine, request.To, now, request.Priority), JobFields(project, agent, false, allowedTools, null, timeout, null, 1), body)
    /// → SignAsync(signWith) → EnvelopeWriter.Serialize → ParseAndVerifyAsync(bytes, me.Tenant) must accept, and the parsed header/job fields and body bytes must equal the request.
    public Task<BuildResult> BuildAsync(JobRequest request, CancellationToken ct);
    public Task<SubmitResult> SubmitAsync(JobRequest request, CancellationToken ct);  // BuildAsync, then BusRepository.SubmitAsync(envelope); maps WriteOutcome → SubmitResult; a signing failure surfaces as SubmitFailed("sign", …)
}
```

The `AllowedTools` option is given as a comma-separated list and may be repeated; items are trimmed; an empty item is a refusal (`allowed_tools[i]`), never silently dropped. `Priority` text is matched case-sensitively against the §4 wire words.

### `status` — inventory, trace, health verdict

```csharp
namespace Zyggy.Core.Bus;

public sealed record MachineCounts(MachineName Machine, int Queued, int Claimed, int Rejected, int Reports, int Context);
public sealed record EnvelopeSummary(
    string RelativePath, EnvelopeId Id, EnvelopeType? Type, MachineName? From, MachineName? To, DateTimeOffset? Created, EnvelopeId? InReplyTo,
    ReportStatus? Status, string? Reason, decimal? CostUsd, bool Verified, string? Problem);   // Problem = the §4 reason wire name when !Verified; header fields read leniently then
public enum TraceState { Queued, Claimed, Rejected, Reported, Unknown }
public sealed record EnvelopeTrace(EnvelopeId Id, TraceState State, MachineName? Machine, EnvelopeSummary? Job, IReadOnlyList<EnvelopeSummary> Reports, string? ReportBody);
public sealed record InventoryReport(string? HeadSha, IReadOnlyList<MachineCounts> Machines, IReadOnlyList<EnvelopeSummary> RecentReports);

public sealed class BusInventory
{
    // DI: BusIdentity (me), checkout root (GitHubBusOptions.CheckoutRoot), EnvelopeSigner, BusRepository (HeadShaAsync), TimeProvider, ILogger<BusInventory>
    /// Machines = the directory names under tenants/<me.Tenant>/nodes/ that parse as MachineName, ordinal order; counts = *.md files per folder (no parsing);
    /// RecentReports = the newest `recent` files across every machine's reports/ by ULID descending, each parsed and verified with me.Tenant.
    public Task<InventoryReport> ListAsync(int recent, CancellationToken ct);
    /// Looks for <id>.md under every machine's jobs/, jobs/claimed/, jobs/rejected/ (first hit wins; a duplicate is logged), and for reports whose in_reply_to == id under every reports/ (and archive/*/).
    /// State: Reported when a verified non-rejected report exists; else Rejected when a rejected/ file or a status: rejected report exists; else Claimed; else Queued; Unknown when nothing matches.
    public Task<EnvelopeTrace?> TraceAsync(EnvelopeId id, CancellationToken ct);   // null when Unknown
}
```

Both methods take the checkout **write** lock (`BusCheckoutLock.WriteLockName`, the existing 60 s wait) for the duration of the directory walk so the listing is never torn by a concurrent `git mv`; they never take the listen lock and never write. Only `tenants/<me.Tenant>/` is walked.

```csharp
namespace Zyggy.Core.Health;

public sealed record NodeHealth(/* the fourteen §11 fields exactly as 07 declared them */);   // moved; JSON unchanged

public abstract record HealthProbe;
public sealed record Reachable(NodeHealth Health) : HealthProbe;
public sealed record Unreachable(string Detail) : HealthProbe;          // connection refused | timeout | HTTP <code> | unparsable body

public sealed class HealthClient
{
    public HealthClient(Uri healthUrl, TimeProvider clock);            // default http://localhost:4711/health; SocketsHttpHandler { UseProxy = false }, 2 s timeout; a non-loopback host is an ArgumentException
    public Task<HealthProbe> ReadAsync(CancellationToken ct);
}

public enum HealthLevel { Green, Amber, Red, Down }                      // worst wins
public sealed record HealthVerdict(HealthLevel Level, IReadOnlyList<string> Reasons, IReadOnlyList<string> Notes);

public static class HealthRules
{
    /// The one place "green" is defined (07 ex-Q9). Pure. Rules in this order (Q4):
    ///   health null                                               → Down  "no node service answering on <url>"
    ///   claudeAuthOk == false                                     → Red   "claude not logged in"
    ///   lastPollUtc null, or now - lastPollUtc > 3 × poll.IdleInterval, and backoffUntilUtc is null or past
    ///                                                             → Red   "not polling: last poll <age> ago" / "never polled"
    ///   backoffUntilUtc > now                                     → Amber "backing off until <utc>"
    ///   claudeAuthOk null                                         → Amber "claude auth not yet probed"
    ///   rateLimitRemaining < poll.RateLimitFloor                  → Amber "rate limit low: <n>"
    ///   lastPullUtc null                                          → Amber "never pulled"
    ///   version != cliVersion                                     → Amber "node <v>, cli <v>"
    ///   jobsRunning > 0 / jobsClaimedByMe > 0                     → Notes (no colour)
    public static HealthVerdict Evaluate(NodeHealth? health, DateTimeOffset now, PollOptions poll, string cliVersion, string url);
}
```

### `verify` — audit

```csharp
namespace Zyggy.Core.Bus;

/// Closed. The first ten are the §4 rejection reasons (wire names via EnvelopeWire); the rest are verify's own vocabulary — not a PROTOCOL.md change.
public enum AuditOutcome
{
    Ok,
    Malformed, TenantMismatch, SchemaUnsupported, KeyIdTenantMismatch, MissingField, InvalidField, MissingSignature, UnknownKey, InvalidSignature,
    IdDiffers,              // id_differs: front-matter id ≠ file name
    TypeFolderMismatch,     // type_folder_mismatch: a report in jobs/, a job in reports/, …
    WriterMismatch,         // writer_mismatch: reports/ or context/ whose `from` ≠ the folder's machine; jobs/* whose `to` ≠ the folder's machine
    UnexpectedPath,         // unexpected_path: a path BusPaths.TryParse refuses (policy.yaml, a stray folder, an invalid label)
    Skipped,                // skipped: a non-.md file, a registry .yaml (unsigned in v1, 14)
}

public sealed record AuditEntry(string RelativePath, AuditOutcome Outcome, string? Field, string? Detail);
public sealed record AuditReport(string? HeadSha, IReadOnlyList<AuditEntry> Entries, IReadOnlyList<string> ForeignTenants, IReadOnlyList<string> UnexpectedTopLevel)
{
    public int Ok { get; } public int Failed { get; } public int Skipped { get; }
    public bool IsClean => Failed == 0 && ForeignTenants.Count == 0 && UnexpectedTopLevel.Count == 0;   // Q1
}

public sealed class BusAuditor
{
    // DI: BusIdentity (me), checkout root, EnvelopeSigner, BusRepository (HeadShaAsync), ILogger<BusAuditor>
    /// Lists the directory names under tenants/: me.Tenant is walked; another valid tenant label → ForeignTenants (never opened); anything else → UnexpectedTopLevel.
    /// Walks tenants/<me.Tenant>/ recursively in ordinal path order; per file: BusPaths.TryParse → else UnexpectedPath (registry .yaml → Skipped; non-.md → Skipped);
    /// .md under nodes/** or archive/**: ParseAndVerifyAsync(bytes, location.Tenant) → the §4 outcome; then IdDiffers, TypeFolderMismatch, WriterMismatch in that order; else Ok.
    public Task<AuditReport> RunAsync(CancellationToken ct);        // under the checkout write lock; reads only
}
```

### Envelope / file formats

No field is added, removed or reinterpreted; `PROTOCOL.md` and §4 stay as 03/04 left them. `submit` writes exactly what `Envelope.CreateJob` + `EnvelopeSigner` produce (the 03 golden form); the only state transition used is §4 row 1 (sender: add + commit + push) through the existing `BusRepository.SubmitAsync`. `status` and `verify` write nothing.

### CLI surface

| Verb | Options and arguments | Output (stdout) | Exit codes |
|------|-----------------------|-----------------|------------|
| `zyggy submit --to <machine> --project <path\|name> [--agent <name>] [--timeout <minutes>] [--allowed-tools <a,b,…>]… [--priority low\|normal\|high] [<body>] [--config <path>] [--verbose]` | `<body>` positional; when absent the body is read from stdin (Q7). `--allowed-tools` may repeat. | `submitted <ULID> to <machine> (<repository-relative path>)`; on deferral the same line followed by ` (committed locally, not yet pushed: the next bus write on this machine pushes it)` | 0 pushed · 2 argument refused / self-check refused (one stderr line `zyggy: <field>: <detail>`) · 3 configuration (`node.json`, `signWith` missing, checkout not a repository / wrong branch / no `tenants/<tenant>/`) · 6 write failed (`lock`, `pull`, `sign` with the `zyggy secret set` hint, `add`, `commit`) · 7 committed locally, push deferred |
| `zyggy status [--id <ulid>] [--config <path>] [--verbose]` | Without `--id`: the node block then the bus block. With `--id`: the trace of one envelope only. | See "Output formats" | Without `--id`: 0 green · 1 amber · 6 red or down · 3 configuration (also checkout unreadable). With `--id`: 0 found · 1 not found · 2 not a ULID · 3 configuration |
| `zyggy verify [--config <path>] [--verbose]` | `--verbose` also prints `SKIP` lines and `OK` counts per folder. | One line per finding, then the summary | 0 clean · 6 drift found (any `FAIL`, any `WARN`) · 3 configuration |
| `zyggy --version` | — | the informational version | 0 |

Every verb resolves `node.json` as 04 defined (`--config`, `ZYGGY_NODE_JSON`, the per-user default) and logs to stderr through the console logger (`--verbose` = Debug). stdout carries only the verb's result; a human and a script read the same text.

**Output formats** (stable; the integration tests assert them line by line; ages are `<n> s|min|h ago` from the registered `TimeProvider`; timestamps `YYYY-MM-DDTHH:MM:SSZ`):

```text
# zyggy status
node    home-laptop (acme) · zyggy-node 0.10.0 · health: green
        last poll 4 s ago · last pull 12 s ago · head 1a2b3c4d5e6f · running 0 · claimed 0 · last report 2026-10-24T08:00:00Z · claude auth ok
bus     head 1a2b3c4d5e6f · 1 tenant directory
        machine       queued  claimed  rejected  reports  context
        central            0        0         0        2        0
        home-laptop        1        0         1        5        0
recent reports (5 of 7)
        01K…  home-laptop→central  done     in_reply_to 01K…  cost_usd 0.0123  2 min ago
        01K…  home-laptop→central  rejected invalid_signature  in_reply_to 01K…  9 min ago
        01K…  central→home-laptop  (unverified: unknown_key)  1 h ago
```

When the verdict is not green, the first line ends `health: amber — <reason>; <reason>` (or `red`, `down`), and the second node line is omitted when the endpoint is unreachable.

```text
# zyggy status --id 01K…
job     01K…  central→home-laptop  project C:/src/calizr  priority high  created 2026-10-24T07:58:00Z
state   reported by home-laptop            # or: queued at home-laptop since 3 min ago | claimed by home-laptop since 1 min ago | rejected by home-laptop: invalid_signature
report  01K…  done  started … finished … duration_seconds 42  cost_usd 0.0123  num_turns 3  model <model>
body:
<the report body, verbatim, data>
```

```text
# zyggy verify
FAIL tenants/acme/nodes/home-laptop/reports/01K….md invalid_signature
FAIL tenants/acme/nodes/home-laptop/reports/01K….md tenant_mismatch (tenant)
FAIL tenants/acme/nodes/home-laptop/jobs/01K….md writer_mismatch (to)
WARN tenants/other-org/ second tenant directory (§13: one bus repository per tenant); not read
verified 41 ok, 3 failed, 2 skipped; 2 tenant directories; HEAD 1a2b3c4d5e6f
```

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `tenant`, `machine`, `bus.*`, `secrets.root`, `poll.*` | `node.json` (04) | as 04 | unchanged; `poll.activeSeconds`/`idleSeconds` feed the green rule's staleness threshold (3 × idle) |
| `signWith` | `node.json` (04) | — | **required by `submit`** (exit 3 when absent, as `probe send`); not required by `status`/`verify` |
| health URL | code: `http://localhost:4711/health` (§11) | 4711 | **no production override** (07: the port never changes); `ZYGGY_HEALTH_URL` is a test hook, loopback only, documented beside `ZYGGY_BUS_API_BASE` |
| health timeout, staleness factor (3 × idle), recent-reports count (10), body cap (64 KiB), label/length limits | code (constants in the Core classes) | as stated | none in v1 — every one is a convention a later deliverable may lift into options; none is a `node.json` key (roadmap (d): no key without a README row, and no key is needed) |
| `ZYGGY_NODE_JSON`, `--config` | process (04) | per-user path | unchanged |

---

## Behaviors & Conventions

- **`submit` order**: `node.json` (exit 3) → checkout pre-flight (exit 3, touches nothing) → `JobRequest.TryCreate` (exit 2) → build + round-trip self-check (exit 2) → the write (0 / 7 / 6). A configuration error therefore wins over an argument error when both are wrong, and nothing touches the checkout before the self-check has passed. One stdout line on success; stderr only for errors and `--verbose` logs.
- **`submit` identity**: `from` = `node.json` `machine`, `tenant` = `node.json` `tenant`, `key_id` = `signWith`, `id` = `EnvelopeId.New(now)`, `created` = now truncated to seconds — never an option (a sender cannot impersonate; §4). `--to` may equal `machine` (Central's self-jobs, Q10).
- **`submit` body**: positional argument or, when absent, stdin read to the end (a terminal without redirection and no positional → exit 2 `body: give the body as the last argument or on stdin`); `\r\n` → `\n`; one trailing `\n` ensured; blank → exit 2; > 64 KiB → exit 2 (Q7). The body is never logged, never echoed.
- **Deferred push** (`SubmitDeferred`): the job is committed locally in the sender's checkout and is pushed by the next bus write on this machine — on a machine with a running node that is the node's next claim/report; on Central the CLI's own next `submit`. Exit 7 so a script notices; the line names the id.
- **`status` reads, never pulls** (Q5): the bus block shows the checkout as it is, with `HEAD` and the node's `lastPullUtc` as the freshness indicator; a stale checkout on a machine whose node is down is visible as `health: down`. Both reads take the checkout write lock for the walk so a concurrent node write is serialised; a lock held > 60 s → exit 3 `the bus checkout is busy`.
- **"Green"** is computed only in `HealthRules.Evaluate` and printed only by `status`; the Node, the registry (14), `list_nodes` (21) and the alerts (22) reuse the function rather than re-deriving a verdict.
- **`status` verifies before it trusts**: every report line comes from `ParseAndVerifyAsync`; an unverifiable file is listed with `(unverified: <reason>)` and its header read leniently (`EnvelopeParser.PeekFrom`-style), never its body. Counts per folder count files, not verified files — the table answers "what is there", the recent list answers "what is trustworthy".
- **`status --id` prints the body as data**: under a `body:` line, verbatim, after every header line; nothing in it is interpreted (§8).
- **`verify` is read-only and quiet on success**: `OK` lines only with `--verbose`; one `FAIL`/`WARN` line per finding; the summary line always; it never stashes, never pulls, never writes, never opens a file outside `tenants/<tenant>/`, never takes the listen lock. A directory name under `tenants/` is the only thing it learns about another tenant.
- **Drift definition** (Q1/Q2): a `FAIL` is any non-`ok`, non-`skipped` outcome; a `WARN` is a second tenant directory or an unexpected top-level entry; exit 6 when either exists.
- **Rotation**: during the 7-day overlap both keys are in the store and every file verifies; after the old key is dropped, files signed with it fail `unknown_key` and are counted as drift (Q3 — 09 decides whether retired keys stay available for verification).
- **Logging**: `ILogger<T>` through the CLI's stderr console logger; `Information` lines `submitted job {Id} to {To}`, `status: health {Level}`, `verify: {Failed} failed, {Foreign} foreign tenant director(ies)`; `Debug` per file with `--verbose`; never a body, a key, a PAT, a header value of the health request.
- **Time**: every instant from the registered `TimeProvider`; `HealthRules` takes `now` as a parameter; ages in the output are computed once per run.
- **Rule R1**: everything is .NET; no shell.
- **Public hygiene**: fixtures use tenant `acme`, machines `central`/`home-laptop`; no owner, employer, host or id anywhere in code, fixtures, README rows or this spec.

---

## Failure modes

| Situation | Observable outcome | Runbook entry (Cli README "Troubleshooting — Submit / Status / Verify"; Core README until 10) |
|-----------|--------------------|-----------------------------------------------------------------------------------------------|
| `node.json` missing / invalid / secret-looking key | `zyggy: configuration error: <key>: <detail>`, exit 3 (04) | existing `node.json` row |
| `submit` without `signWith` | `zyggy: configuration error: signWith missing`, exit 3 | "Submit: signWith" — add `signWith` (the sender must sign) |
| Checkout not a repository / wrong branch / no `tenants/<tenant>/` | exit 3, one line, nothing touched | existing "Bus: checkout" rows; "no tenants/<tenant>/ directory" → wrong `bus.localPath` or an unseeded repository |
| An argument refused (`to`, `project`, `agent`, `timeout_minutes`, `allowed_tools[i]`, `priority`, `body`) | `zyggy: <field>: <detail>`, exit 2, nothing built | "Submit: argument refused" — the field rules table |
| Round-trip self-check disagrees (an emitter or parser regression) | `zyggy: refusing to submit: the envelope does not round-trip (<field>)`, exit 2, nothing written | "Submit: does not round-trip" — file a bugfix with the arguments (never the body); this line should never appear |
| Signing key absent from the store | `zyggy: signing key <id> is not in the secret store; run: zyggy secret set <name> --hex`, exit 6 | existing "Probe: keys" row, renamed "Bus: keys" |
| Sender checkout dirty or behind so the pull fails | `zyggy: submit failed at pull: <git detail>`, exit 6, working copy restored | existing "`push_failed pull: …`" row, generalised |
| Checkout lock busy > 60 s | exit 6 (`submit`) / exit 3 (`status`, `verify`) `the bus checkout is busy: another zyggy process held its lock` | "Bus: checkout busy" — a hung `zyggy` or node holds `.git/zyggy-write.lock`; `ps`/Task Manager |
| Push rejected after the retry rule | `SubmitDeferred`, exit 7, the line names the id; the commit is local | "Submit: deferred push" — nothing to do on a machine with a running node; on Central run `submit` again later or `git -C <bus.localPath> push` by hand; 04's "Bus: push deferred" |
| `status`: no node answering (not installed yet, stopped, port in use by something else) | `health: down — no node service answering on <url>`, exit 6, bus block still printed | "Status: down" — `zyggy-node` service state (10's unit), 07's "Node: refused to start" |
| `status`: red (`claude` logged out, not polling) | `health: red — <reason>`, exit 6 | "Status: red" — `claude login` as the service user (07 "Node: claude auth"); not polling → the node log, 07 "Node: safety fetch", network |
| `status`: amber (back-off, rate limit, version skew, never pulled, auth not yet probed) | `health: amber — <reason>`, exit 1 | "Status: amber" — back-off clears by itself; version skew → install the same release on node and CLI (10) |
| `status --id` unknown | `not found in tenant <tenant>`, exit 1 | "Status: not found" — the id is from another tenant's bus, or the file was archived/removed by hand |
| `verify`: a tampered or unverifiable file | `FAIL <path> <reason>`, exit 6 | "Verify: drift found" — a file edited after signing or a key mismatch; compare with `git log -p -- <path>`; a job in `jobs/` that fails will be rejected by its node anyway |
| `verify`: `unknown_key` after a rotation | `FAIL <path> unknown_key (acme/1)`, exit 6 | "Verify: unknown key after rotation" — expected for files older than the overlap window; see `rotate-hmac.md` (09) |
| `verify`: second tenant directory | `WARN tenants/<other>/ …`, exit 6 | "Verify: second tenant directory" — a protocol violation (§13): someone pushed another tenant's tree into this repository; the node ignores it (04); remove it with the bus owner |
| `verify`: unexpected top-level entry or path | `WARN`/`FAIL … unexpected_path` | "Verify: unexpected path" — a hand-made file or folder; `policy.yaml` is not expected before 18 |
| Health endpoint answers non-JSON or a non-200 | `down — HTTP <code>` / `unparsable body` | "Status: down" footnote — another program on 4711; `netstat`/`ss` |

---

## Dependencies

| Package | License | Why (what bespoke code it removes) |
|---------|---------|------------------------------------|
| none new | — | `System.CommandLine` 2.0.11 (existing) parses the verbs; `HttpClient`/`SocketsHttpHandler` and `System.Text.Json` source generation are BCL; signing, writing, locking, git and the composition root exist since 03/04/07. `Directory.Packages.props` is unchanged (AC-15). |

---

## Deliberate deviations from the founding spec

- §4 "the enforcement (`agentbus verify` reporting a second directory, nodes refusing to start against it) is a §14 item" and §14 "Would have to be added for a second tenant: … `agentbus verify` reporting a second `tenants/<org>/` directory": `verify` **reports** (never reads, never refuses) a second directory from 08 because the roadmap's definition of done requires it — pending Q1, amendment A8-1. Nodes refusing to start stays §14 (04 ex-OQ4).
- §8 Audit names signatures and tenant-versus-path only; `verify` also reports `id_differs`, `type_folder_mismatch` and `writer_mismatch` — pending Q2, amendment A8-2 (one clause).
- Spec 07 placed `NodeHealth` in `Zyggy.Node`; it moves to `Zyggy.Core.Health` — pending Q8; a spec-07 contract, no founding-spec text.
- §3/§9 verb lists are unchanged; `status --costs` (§11) is deferred to 25 by the roadmap, not by this spec.

### Founding-spec amendments to propose (exact wording; only the owner or the orchestrating session applies them, after the gate)

- **A8-1 — §4 "One repository per tenant (rule)"**, last sentence: replace "the enforcement (`agentbus verify` reporting a second directory, nodes refusing to start against it) is a §14 item" with "`agentbus verify` reports a second directory as drift and never reads inside it (deliverable 08); nodes ignore such a directory (deliverable 04) and refusing to start against it is a §14 item". **§14 "Would have to be added for a second tenant"**, first bullet: replace "`agentbus verify` reporting a second `tenants/<org>/` directory, nodes refusing to start against it" with "nodes refusing to start against a second `tenants/<org>/` directory (`verify` already reports it)".
- **A8-2 — §8 Audit**, second bullet: append "and the §4 write-rule consistency of every file (file name = `id`, type matches its folder, `from`/`to` match the folder's machine)".

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| `--project` is a Windows path given on Linux (Central → laptop) | Accepted by shape (`X:\…` or `X:/…`); never checked for existence locally — the target's pre-flight (06) decides |
| `--project` is a registry name | Accepted (label); the node answers `unknown_project` until 14 (as the gate's part 1a) |
| `--allowed-tools "Bash(dotnet *)"` | One item with spaces and parentheses; emitted double-quoted by the canonical emitter (§4 example); round-trips |
| `--allowed-tools` given twice | Items concatenated in order; duplicates kept (the node's allowlist is a set anyway) |
| Body given both positionally and on stdin | The positional wins; stdin is not read |
| Body with a UTF-8 BOM or invalid UTF-8 from a file redirect | BOM stripped; invalid UTF-8 → exit 2 `body: not valid UTF-8` |
| Body is exactly `---` | Valid body `---\n`; the parser's front matter already closed; round-trips |
| `--to` equals `machine` | Allowed (Q10); the node claims its own inbox like any other |
| `submit` while this machine's node is mid-write | The write sequence waits on the checkout lock (≤ 60 s), then pulls and pushes; no conflict |
| `status` while the node pulls | The walk waits for the lock; the table reflects the post-pull tree |
| `status` on a checkout with no `nodes/` directories yet | Empty table, `recent reports (0 of 0)`; exit by health only |
| `status` health reachable but `version` null/empty | `Amber "node version unknown"` |
| `status` when the node is in `Idle` mode (60 s polls) | Threshold is 3 × idle = 180 s regardless of mode, so an idle node is green |
| `--id` matches a job and two reports (a sweep re-queue, 17) | All reports listed, newest last; state from the newest verified one |
| `verify` on a tenant subtree with thousands of files | Linear; one lock hold; a progress line every 500 files with `--verbose` |
| `verify` finds a `.md` whose name is a ULID but whose `id` is another ULID | `id_differs` (the node only logs this at intake; the audit flags it) |
| `verify` finds `tenants/acme/archive/2026-09/<ulid>.md` signed with a key still in the store | `ok`; archive files are audited like any other |
| `verify` with `tenants/` missing entirely | exit 3 (`no tenants/<tenant>/ directory`), the shared pre-flight |
| `ZYGGY_HEALTH_URL` set to a non-loopback host | `HealthClient` refuses it (`ArgumentException` → exit 3 `health url must be loopback`); never a request |
| `--version` with a broken `node.json` on disk | Prints the version, exit 0; `node.json` is never read |

---

## Out of Scope

- `--costs` (25); `--json` output; `submit --wait`; `--worktree`, `--report-back`, `--deadline`, `--attempt` options; `zyggy report`, `discover`, `context` (12/15); `zyggy run` (16); the sweep and stale-claim detection in `status` (17); signed registry files and their audit (14); Hub tools (21); alerts from the verdict (22); `init`, `diagnose` (§14).
- Any pull, fetch, stash or write by `status` or `verify`; auditing git history; verifying another tenant's files; nodes refusing to start against a second tenant directory (§14).
- A production override of the health URL/port; authentication on `/health`; a `node.json` key of any kind.
- Any change to the §4 envelope, the state machine, `PROTOCOL.md`, `EnvelopeSigner.Canonicalize` or the golden files.
- Rewriting the probe verbs onto `VerbIo` (they keep writing to the console; their binary tests stay).

---

## Open Questions _(remove when resolved)_

- [ ] **Q1 — `verify` and a second `tenants/<org>/` directory: report, refuse, or leave to §14?** Found: the roadmap's definition of done (line 276) and brief (line 279) require `verify` to detect "a second `tenants/<org>/` directory (reported, not refused — ex-OQ4 of 04)", while the roadmap's own "Spec sections" line (272) says "repository-per-tenant rule refused by `verify`"; the founding spec §4 says that enforcement "is a §14 item", §12 says "no gate is required to … prove the repository-per-tenant rule", §14 lists "`agentbus verify` reporting a second `tenants/<org>/` directory" under "would have to be added for a second tenant", and `memory/long-term/mvp-scope-decision.md` says to cut code that exists only for a second tenant. Why it matters: it decides whether `AuditReport.ForeignTenants` exists, whether it affects the exit code, and whether the founding spec needs amending. Options: (a) report as `WARN` counted as drift (exit 6), never read inside, never refuse — A8-1; (b) report as information only (exit unaffected); (c) do not detect it at all (strict §14 reading). Recommendation: **(a)**. The detection is a directory listing (a handful of lines, no second-tenant fixture beyond a seeded folder the gate already uses), §13 calls the situation a protocol violation, and an audit that stays silent on a violation is not an audit; the node keeps ignoring the directory (04), so nothing is refused. The gate (§12) still does not test it — the verb's own integration test does.
- [ ] **Q2 — `verify`'s check list beyond §8's sentence.** Found: §8 names "all signatures" and "the `tenant`-field-versus-path consistency"; §4 also states "file name = ULID", the per-folder write rules (`reports/` written only by `<machine>`, jobs addressed `to` the folder's machine) and the type-per-folder layout; the parser gives the first two for free, the other three are one comparison each over fields already parsed. Why it matters: new wire words (`id_differs`, `type_folder_mismatch`, `writer_mismatch`) in `verify`'s output and a one-clause §8 amendment (A8-2). Options: (a) include the three checks; (b) signatures and tenant-versus-path only. Recommendation: **(a)** — drift in exactly these fields is what a compromised or mis-configured machine would produce, and the cost is negligible; the words are verify's vocabulary, not §4 rejection reasons.
- [ ] **Q3 — Retired keys make old files fail `verify` with `unknown_key`.** Found: §4 rotation ("add `<tenant>/2`, accept both for 7 days, drop `<tenant>/1`") vs §8 audit over the whole subtree including `archive/` (kept beyond 7 days by §4's sweep). After a rotation every older file reports `unknown_key` forever. Why it matters: `verify`'s exit code becomes permanently 6 after the first rotation unless the rule is decided. Options: (a) 08 counts `unknown_key` as drift and names the key; 09's `rotate-hmac.md` keeps retired keys in the store as verify-only (a `signWith` points at the current one; verification tries any `<tenant>/<n>` present); (b) `verify` treats `unknown_key` as `skipped` (hides a real key mismatch); (c) the sweep re-signs archived files on rotation (a write to the audit trail). Recommendation: **(a)** for 08 (nothing to build here beyond the detail), and the retention of retired keys is recorded as a question for 09's spec.
- [ ] **Q4 — The "green" rule and the exit-code mapping of `status`.** Found: 07 ex-Q9 leaves the verdict to 08; the fourteen fields, `PollOptions` (idle 60 s, rate-limit floor 100) and the CLI version are the only inputs; nothing in the founding spec defines thresholds. Why it matters: 10's smoke ("green"), 22's alert source, and scripts read the exit code. Options: (a) the rule as drafted in `HealthRules.Evaluate` (red: auth false or no poll for > 3 × idle; amber: back-off, auth unknown, rate limit low, never pulled, version skew; down: unreachable) with exits 0 green / 1 amber / 6 red or down; (b) two levels only (green / not green) with 0 / 1; (c) three colours but every non-green exits 1. Recommendation: **(a)** — amber is "look later", red/down is "act now", and the distinction is free once the reasons exist; the thresholds are constants in one class and can be tightened by 22 without a contract change.
- [ ] **Q5 — Should `status` and `verify` pull before reading?** Found: `BusRepository.PullAsync` exists; a machine has one checkout shared by its node and its CLI; the node pulls on every doorbell and at least every 15 min; `probe send` never stashes a dirty checkout. Why it matters: a pull needs the network and the PAT/deploy key and can fail on a dirty tree, turning a read-only verb into a write path; without it the answer can be up to one safety-fetch period stale on a machine whose node is down. Options: (a) no pull; show `HEAD` and the node's `lastPullUtc` as freshness; (b) pull first, `--no-pull` to skip; (c) pull only when the node is down. Recommendation: **(a)** — the verbs stay read-only (easier to reason about in an audit tool), the staleness is visible in the same output, and `verify` on a restore (`restore-central.md`) audits the checkout as it stands, which is what one wants.
- [ ] **Q6 — `status --id` prints the report body.** Found: §3 has a separate `report` verb ("Read a report by job id", 15); 10's definition of done wants "a report visible in `zyggy status`"; until 15 there is no other way to read the result from the command line. Why it matters: scope overlap with 15 and the §8 "body is data" rule in a terminal. Options: (a) print the body verbatim under a `body:` line; 15 makes `zyggy report <id>` an alias of the trace's report part; (b) print header fields only. Recommendation: **(a)** — the walking skeleton is not proven until a human can read the answer; the body is printed as data and never interpreted.
- [ ] **Q7 — `submit` body source and normalisation.** Found: the roadmap gives `<body>` as a positional argument; multi-line prompts on a command line are awkward on both shells; `probe send` uses a fixed body ending in `\n`; §4 says the body is bytes "unchanged" (for signing) but a writer chooses what it writes; no size rule exists. Options: (a) positional or stdin (when the positional is absent), CRLF → LF, one trailing `\n`, blank refused, 64 KiB cap; (b) positional only, bytes untouched; (c) add `--body-file <path>`. Recommendation: **(a)** — stdin is what skills and shells have; LF normalisation keeps the canonical form identical across Windows and Linux senders; the cap is a sanity check against a pasted document, not a protocol rule (constant, no key).
- [ ] **Q8 — Move `NodeHealth` (07) from `Zyggy.Node` to `Zyggy.Core.Health`.** Found: `Zyggy.Cli` references `Zyggy.Core` only; `Zyggy.Node` carries the `Microsoft.AspNetCore.App` framework reference (+26 MB single-file at 07's gate); the §11 body is read by 08, 14, 21, 22. Options: (a) move the record, converter and JSON context to Core (07's tests move with them; `HealthServer` unchanged); (b) a second, CLI-side record with the same fourteen properties. Recommendation: **(a)** — one definition of a shared contract; (b) is the duplication that makes two deliverables drift.
- [ ] **Q9 — In-process invocation shape.** Found: `CliApplication.RunAsync(args, CliEnvironment)` is internal with no `InternalsVisibleTo`; the probe verbs write to `Console.*`; the Core verb hosts (`M365VerbHost`, `BriefVerbHost`) take `VerbIo` and are invoked in process by tests; the gate's host runs in process on an ephemeral port. Options: (a) `CliApplication.RunAsync(args, environment, VerbIo io, Action<IServiceCollection>? configure = null)` + `InternalsVisibleTo("Zyggy.Integration")`; the three new verbs write through `io`; `configure` is applied through `BusHost.BuildServices`'s existing `extra` (the 07 rule: only seam implementations and options are swapped); a `ZYGGY_HEALTH_URL` loopback-only hook for the port; (b) move the three verbs into a Core `BusVerbHost` like `M365VerbHost` and bypass System.CommandLine for them (contradicts the roadmap's "System.CommandLine host shared with `probe`"). Recommendation: **(a)**.
- [ ] **Q10 — A self-addressed `submit` (`--to` = own `machine`).** Found: §3 says Central's node is "also used for cron-style jobs Central submits to itself"; `AssertMayWrite(…, submission: true)` allows it; 13's dream timer will submit a `dream` job to Central. Options: (a) allowed, no flag; (b) refused unless `--self`. Recommendation: **(a)** — it is the spec's own use case and the write rule already permits it.

---

## Notes for the planner and the project-manager

1. **F3 is already in the code**: `ProbeOutcome.UnverifiedReport` (`unverified_report`, "decision 0001 F3") and its `ProbeStatsTests` column exist on this branch, but `_plans/ROADMAP.md` line 803 says the bugfix is "still open" and the Cli README's `probe stats` row still lists the columns without it. Reconcile the roadmap; 08's AC-14 fixes the README row in passing.
2. **Roadmap self-contradiction** in the 08 entry: "Spec sections … repository-per-tenant rule **refused** by `verify`" (line 272) vs the brief's "reported, not refused" (line 279). Q1 recommends "reported"; the roadmap line should follow the answer.
3. **Gate file**: part 1b is appended as `#region Part 1b — 08` in `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`, reusing the bottom helpers; part 1 is not restructured; 13 appends part 2 after it.
4. **No owner-run item needs a real machine**; everything runs against the bare repository, the fake `claude` and the in-process host. The first real `submit` → `status` on Central and the home laptop is 10's.
5. **`CLAUDE.md`** still says `src/Zyggy.Node` is "the empty host"; it is not since 07 — a one-line fix for the orchestrating session, not for this spec.
