# Spec: 07 — Node service runs the poll → pull → claim → run → report loop with `/health`

> Founding-spec sections: §3 Components (`AgentBus.Node`, Node instance); §5 Poll loop (items 2–3 work hours, item 6 safety fetch, item 7 proxy) and Git operations (start-up rule); §6 Streaming and limits — Concurrency ("one job per project, at most `maxConcurrentJobs` per machine, only inside `workHours` unless `priority: high`"), Pre-flight step 3 (held lock → "the dispatcher leaves the job in `jobs/`"); §8 Isolation (`--permission-mode auto`, bodies are data), Secrets (never in `node.json`); §9 Packages (Hosting, WindowsServices, Systemd, Serilog), solution structure (`AgentBus.Node`: `PollLoop`, `JobDispatcher`, health endpoint), design rules (five seams, DI, no static state, log once per state change); §10 `node.json` (`poll.safetyFetchMinutes`, `workHours`, `maxConcurrentJobs`, `nodeDir`, the start-up refusals), Upgrade path (health reports the version); §11 Logging, Health endpoint, Runbooks (`bus-conflict.md`); §12 Definition of done; §13 decisions (Claude Code runtime, GitHub transport, .NET 10 single-file, MVP scope); §14 (telemetry off — O8; `policyVersion`, HTTP Hub, repository-per-tenant enforcement stay out). Roadmap entry: `_plans/ROADMAP.md` #07 (hand-off brief, lines 255–266), overview row line 105, P1 phase row line 37. Project inputs honoured: `_specs/06-job-runner.md` (items forwarded to 07: `Gates/P1_WalkingSkeleton.cs` opens with the probe round trip's content, the `ProjectLock.IsHeld` pre-claim check, the `nodeDir`/`claude.path` binding; `NodePolicy` to gain `MaxConcurrentJobs`/`WorkHours`), `_plans/06-job-runner.md` line 488, `_specs/04-transport-a-proxy-proof.md` (the listen loop 07 reuses; work-hours evaluation and the 15-minute safety fetch deferred from 04 to 07; "07 inherits `PollMode` evaluation, the safety fetch, resuming `claimed/` after a crash, health fields from `BusListener.State`"), `memory/long-term/mvp-scope-decision.md`, `memory/long-term/repo-public-hygiene.md`, roadmap rule R1, `.claude/instructions/publishing.md`, `.claude/skills/build-feature/SKILL.md` (Node hosted-service shape).
>
> Status: **approved by the owner on 2026-10-17 — zero Open Questions; planner-ready.** Owner, verbatim: "fix all inconsistencies, and I approve all your recommendations". The nine questions of the draft (Q1–Q9) were all decided as recommended; each decision is folded into the tables below and marked *decided 2026-10-17 (ex-Qn)*; the "Decisions" block at the end records them, and the founding-spec wording the orchestrating session is applying is under "Founding-spec amendments to apply". Nothing in this file edits the founding spec.

## Current state (verified against the repo on 2026-10-17, branch `feature/06-job-runner`)

| Roadmap item | State today | Decision for 07 |
|---|---|---|
| `Zyggy.Node` | `Program.cs` is one line (`Host.CreateApplicationBuilder(args).Build().Run()`); `Zyggy.Node.csproj` (Worker SDK, `AssemblyName` `zyggy-node`) references `Zyggy.Core` and `Microsoft.Extensions.Hosting` only; `appsettings.json` holds console log levels; `README.md` describes the intended loop (incl. a `DiscoveryTimer` and "`policy.yaml` may tighten", both later). No `BackgroundService`, no health endpoint, no service hosting, no Serilog. | **Built here**: `PollLoop` (BackgroundService), `NodeHost` composition root, health endpoint, Serilog, service hosting. README rewritten to what 07 ships (discovery → 14, policy → 18). |
| The listen loop (04) | `Zyggy.Core.Bus.BusListener` (public): `StartAsync` (§5 start-up rule via `BusRepository.EnsureCleanAsync` → `Clean`/`Stashed`/`WrongBranch`/`NotARepository`/`NoTenantDirectory`/`RebaseInProgress`, then `KeyMissing`, then the one-listener-per-checkout lock → `ListenerRunning`, then one pull and one `ProcessOnceAsync`), `RunAsync(PollMode mode, ct)` (wait until `State.NextDueUtc`, `IBusProvider.ProbeHeadAsync`, `PollState.Apply`, pull when `Changed`, `ProcessOnceAsync`), `ProcessOnceAsync` (list → `VerifyIntakeAsync` → reject or `ClaimAsync` → **awaits the `JobHandler` inline** → `ReportAsync`), `State : PollState`, scopes `tenant`/`machine`/`phase` on every line, one line per state change. `PollMode` is fixed for the whole run; there is no pre-claim gate; `ProcessOnceAsync` runs only after a pull; no safety fetch; `claimed/` is never re-listed. | **Reuse and extend additively** (decided 2026-10-17, ex-Q7): a `JobGate` delegate consulted before `ClaimAsync`; a `PollModeSelector` delegate instead of the fixed `PollMode`; the run+report of an admitted job tracked instead of awaited so the loop keeps polling and up to `maxConcurrentJobs` run; the §5 item 6 safety fetch inside `RunAsync`; a `Status` snapshot for `/health`; `ResumeClaimedAsync` (ex-Q4). The probe verbs keep their behaviour (gate admits all, mode always `Active`). |
| Poller state machine (04) | `PollState`/`PollTransition`/`PollOptions`/`PollMode`/`PollPhase`/`HeadProbe` — pure, tested with a fake clock; `PollOptions` has no safety-fetch interval; `PollSettings` (node.json `poll`) reads `activeSeconds`/`idleSeconds` only. | Unchanged machine. `PollOptions.SafetyFetchInterval` (15 min) and `PollSettings.SafetyFetchMinutes` added. |
| `IBusProvider` / `GitHubBusProvider` (04) | `Remote`, `ApiBaseUri`, `ProbeHeadAsync`, `PullAsync`, `PushAsync`; the PAT read from `ISecretStore` per request; the system proxy handler built in one place (`CreateHandler`). | **Unchanged** (the seam is a shared contract). The safety fetch is a `BusRepository` operation over the existing internal `GitClient.FetchAsync`/`RevParseAsync`, so the provider contract suite stays as it is. |
| `BusRepository` (04) | `EnsureCleanAsync`, `ListNewJobsAsync` (top-level `jobs/*.md` only), `VerifyIntakeAsync`, `ClaimAsync`, `ReportAsync`, `RejectAsync`, `PullAsync` (under the cross-process checkout lock), `TryAcquireListenLockAsync`, `UnpushedCommitCountAsync`, `Me`. No fetch/compare, no `claimed/` listing, no HEAD sha. | Gains `SafetyFetchAsync`, `ListClaimedAsync`, `HeadShaAsync`. Bus writes from two concurrent runs are already serialised by `BusCheckoutLock` (`FileShare.None`, polled 200 ms, 60 s timeout) — no new lock. |
| `JobRunner` + `AddJobRunner` (06) | `JobRunner.RunAsync(Envelope, ct)` is the `JobHandler`; never throws; `ProjectLock.IsHeld(path)` exposed for the pre-claim check; `NodeDir` falls back to a registered `NodeConfiguration`; `ClaudeCodeOptions.Path` bound by the host ("07 binds it"). `IModelRunner` has one method, `RunAsync`. | **Consumed as is**; `claude.path` → `ClaudeCodeOptions.Path` bound in the composition root. `IModelRunner.CheckAuthAsync` added for `claudeAuthOk` (decided 2026-10-17, ex-Q3). |
| `IPolicySource` / `NodePolicy` (06) | `NodePolicy(DefaultAllowedTools, DefaultTimeoutMinutes, MaxTurns, MaxBudgetUsd, DevRoots)`; `NodeJsonPolicySource` projects `NodeConfiguration`; 06 wrote "07 extends `NodePolicy` with `MaxConcurrentJobs` and `WorkHours`". | Extended exactly so. |
| `node.json` reader (04/06) | `NodeConfigurationReader`: `tenant` required (no default), `machine`, `signWith` (optional; tenant must match), `bus` (owner/repo/branch/localPath/remote), `poll` (two intervals), `secrets.root`, `claude.*`, `devRoots`, `nodeDir`; unknown keys ignored; secret-looking keys refused at any depth; `--config` / `ZYGGY_NODE_JSON` / per-user default path. | Extended with `poll.safetyFetchMinutes`, `workHours`, `maxConcurrentJobs`, `log.level` (ex-Q5). The Node additionally requires `signWith` (it must sign reports). `role`/`user`/`agentCore`/`dlp` stay ignored (11/15/19). |
| Composition root | `src/Zyggy.Cli/Commands/BusHost.BuildServices` (CLI): `AddLogging(console)`, `AddProcessRunner`, `AddFileSecretStore(secrets.root)`, `AddEnvelopeSigning`, `PollOptions` from `poll`, `KeyId`, `AddGitHubBus` with `BusRemoteFrom(bus)`; `tests/Zyggy.Integration/Infrastructure/TestHost.BuildRunnerListener` rebuilds the same chain by hand plus `AddClaudeCodeModelRunner`/`AddNodeJsonPolicy`/`AddJobRunner`. Two copies of the mapping `NodeConfiguration` → services. | **Reshape** (decided 2026-10-17, ex-Q8): the `NodeConfiguration` → bus/signing/runner mapping moves into `Zyggy.Core` (`AddNodeServices(NodeConfiguration)`); `Zyggy.Node.NodeHost` adds the host-only parts (Serilog, health, `PollLoop`, service hosting); the CLI's `BusHost` and `TestHost` call the Core method (behaviour unchanged, existing tests prove it). The gate starts the real host through `NodeHost`. |
| Integration harness | `BusRepoFixture` (tenant required, `SeedSignedJobAsync` with `JobFields`, foreign-prefix seeding via `SeedRawFileAsync`), `BareRepoHeadHandler` (the mocked head check, through the internal `GitHubBusOptions.HandlerFactory`), `FakeClaude` (+ scenarios `done`, `no-report`, `no-result`, `error`), `ScratchProject`, `RecordingLoggerProvider`, `HookedProcessRunner`, `SecretSweep`. `tests/Zyggy.Integration/Gates/` does not exist. | Reused; `Gates/P1_WalkingSkeleton.cs` created (part 1). |
| Packages (`Directory.Packages.props`) | `Microsoft.Extensions.Hosting` 10.0.12, `Options`/`Logging.Abstractions`/`DependencyInjection(.Abstractions)` 10.0.12, `Ulid`, `YamlDotNet`, `System.CommandLine`, `ModelContextProtocol`, test packages. No Serilog, no WindowsServices/Systemd, no ASP.NET Core framework reference. `Directory.Build.props` sets **`InvariantGlobalization=true`** for every project. | New: `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12, `Microsoft.Extensions.Hosting.Systemd` 10.0.12, `Serilog` 4.4.0, `Serilog.Extensions.Hosting` 10.0.0, `Serilog.Sinks.File` 7.0.0, `Serilog.Sinks.Console` 6.1.1; `TimeZoneConverter` 8.0.0 (ex-Q2); the `Microsoft.AspNetCore.App` framework reference for Kestrel (ex-Q1). See Dependencies. |
| Claude Code facts | `claude auth status` exists in the official CLI reference (exit 0 when logged in, 1 when not; JSON by default, `--text` for humans). Not yet exercised by any Zyggy code; `tools/fake-claude` knows only `-p`. | Used for `claudeAuthOk` (ex-Q3); the fake gains an `auth status` mode. |

## User Story

**As** the home laptop (and Central's own node), **I want** `zyggy-node` to run as a service that reads my `node.json`, refuses to start without a tenant, a signing key or a bus checkout carrying `tenants/<tenant>/`, polls the bus through the GitHub doorbell at the work-hours or idle interval, pulls when the branch moved (and every 15 minutes regardless), claims only the jobs I may run now (inside work hours unless `priority: high`, a free slot, the project not locked), runs them through the job runner with at most two at a time while the loop keeps polling, publishes every signed report, logs one structured line per state change into a rolling file, and answers `http://localhost:4711/health` with the §11 fields — **so that** the P1 walking skeleton's automated half (a job submitted on the bus → the node → `claude -p` → a report on `main`, with a real host started through production DI) is proven against the bare repository and the fake `claude`, and deliverable 10 only has to install the same binary on the real machines.

---

## Acceptance Criteria

Tenant `acme`, machines `central` → `home-laptop`, key `acme/1` in every automated test. **U** = unit (`tests/Zyggy.Core.Tests`, `FakeTimeProvider`, substitutes), **I** = integration (`tests/Zyggy.Integration`, bare repo, real `git`, `tools/fake-claude`, the mocked head check; never the real `claude`, never GitHub), **G** = the gate scenario `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`, **D** = document, **CI** = pipeline.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 (U) | `node.json` without `tenant` / with `tenant: ""` / without `signWith` / with a key named `githubToken` anywhere / with `workHours.to` ≤ `workHours.from` / `workHours.days: ["Monday"]` / `workHours.timezone: "Mars/Olympus"` / `maxConcurrentJobs: 0` / `poll.safetyFetchMinutes: 0` / `log.level: "chatty"` | `NodeConfigurationReader.Read` | A refusal naming the key (`tenant`, `signWith`, the dotted secret key, `workHours.to`, `workHours.days[0]`, `workHours.timezone`, `maxConcurrentJobs`, `poll.safetyFetchMinutes`, `log.level`); never a default tenant. `signWith` is required for the Node (the reader keeps it optional for the probe verbs: the Node host refuses `SignWith == null` with the same exit code 3). |
| AC-2 (U) | `node.json` with the §10 example's `workHours`, `maxConcurrentJobs: 2`, `poll.safetyFetchMinutes: 15` / without those keys | `Read` | `NodeConfiguration.WorkHours` = (`Europe/Brussels`, Mon–Fri, 08:00–19:00), `MaxConcurrentJobs` 2, `Poll.SafetyFetchMinutes` 15 / `WorkHours == null` (always inside), 2, 15. `NodeJsonPolicySource` projects `NodePolicy.MaxConcurrentJobs` and `NodePolicy.WorkHours` from them. |
| AC-3 (U) | `WorkHours` (`Europe/Brussels`, Mon–Fri, 08:00–19:00) | `Contains(now)` for Monday 07:59:59 UTC+2, 08:00:00, 18:59:59, 19:00:00, Saturday 12:00, and the UTC instants across a DST change (last Sunday of March and October) | `false`, `true`, `true`, `false`, `false`; the DST cases evaluate in local wall-clock time; the same test passes on Windows and Linux under `InvariantGlobalization=true` (the IANA id resolves on both through `TimeZoneConverter`, ex-Q2). |
| AC-4 (U) | `JobDispatcher` over `FakeTimeProvider`, policy `MaxConcurrentJobs 2`, work hours as AC-3 | `Admit(job, now)` for: a `normal` job inside work hours with 0 running; the same outside work hours; a `high` job outside work hours; a `normal` job inside work hours with 2 running; a job whose project is already in flight in this process; a job whose project's `ProjectLock` is held by another `ProjectLock` instance | `Admitted`; `Deferred(OutsideWorkHours)`; `Admitted`; `Deferred(NoSlot)`; `Deferred(ProjectBusy)`; `Deferred(ProjectBusy)`. The rules are applied in that order; the decision is a pure function of (job header, project path, now, running set, policy, lock state). |
| AC-5 (U) | `JobDispatcher` | A job is deferred on three consecutive passes, then admitted | Exactly one `Information` line `job {Id} deferred: {Reason}` (first pass) and one `job {Id} admitted after deferral` — log once per state change, never per tick (§11). |
| AC-6 (U) | `PollModeSelector` built from `WorkHours` | Evaluated inside / outside work hours / with `WorkHours == null` | `Active` / `Idle` / `Active`. `BusListener.RunAsync` logs `poll mode {Mode}` once per transition and runs `ProcessOnceAsync` on an `Idle → Active` transition without waiting for the branch to move (deferred jobs become due). |
| AC-7 (U) | `BusListener.RunAsync` with a substituted `IBusProvider` answering `Unchanged` forever, `PollOptions.SafetyFetchInterval = 15 min`, `FakeTimeProvider` | The clock is advanced 14:59 then 15:00, then 15 more minutes | No fetch before 15:00; at 15:00 `BusRepository.SafetyFetchAsync` runs once; again at 30:00. When the fetch reports `origin/<branch>` ahead of the local branch: one `Warning` `safety fetch: branch moved while the doorbell said unchanged; resynchronising`, a pull, `ProcessOnceAsync`, and `State.ETag` reset to `null` so the next probe re-synchronises; when nothing moved: no log line. |
| AC-8 (U) | `BusListener` with a `JobGate` substitute | `ProcessOnceAsync` over a verified job the gate defers | `ClaimAsync` is **not** called, the file stays in `jobs/`, the handler is never invoked, `handled` does not count it; a job the gate admits is claimed as in 04 and its run + report is tracked (`Status.JobsRunning == 1` while the handler's task is pending) while `ProcessOnceAsync` returns. Rejected files bypass the gate (verification first, §4/§8). |
| AC-9 (U) | `BusListener` with two admitted jobs whose handlers block on a `TaskCompletionSource` | Both are claimed | `Status.JobsRunning == 2`; `RunAsync`'s loop ticks again while they run (a substituted provider sees further `ProbeHeadAsync` calls); completing the first handler produces its report (`ReportAsync`) before the second finishes; cancelling the host token during a run propagates `OperationCanceledException` to the handler, writes no report, and `Status.JobsRunning` returns to 0 after `StopAsync`. |
| AC-10 (U) | `BusListener.ResumeClaimedAsync` (ex-Q4) over a checkout holding two files in this machine's `jobs/claimed/`, one verifying, one with a bad signature | After `StartAsync` | The verifying one is run through the gate → handler → report like a fresh claim (no second `claim` commit); the other is left in `claimed/` with one `Warning` (`claimed job {Id} does not verify; left for the sweep`); one `Information` `resuming {Count} claimed job(s) from a previous run`. |
| AC-11 (U) | `NodeHealth` snapshot serialised with the source-generated JSON context | Serialise | Exactly the §11 field names in camelCase: `version`, `tenant`, `machine`, `lastPollUtc`, `lastEtag`, `lastPullUtc`, `busHeadSha`, `jobsRunning`, `jobsClaimedByMe`, `lastReportUtc`, `rateLimitRemaining`, `backoffUntilUtc`, `claudeAuthOk`, `agentCoreSha`; absent values serialised as `null` (never omitted); timestamps `YYYY-MM-DDTHH:MM:SSZ`; `agentCoreSha` is `null` until 15. |
| AC-12 (U) | `src/` | Source hygiene (extends 04 AC-2 / 06 AC-16) | Under `src/Zyggy.Node/`: no `DateTime.UtcNow`/`DateTimeOffset.UtcNow`/`TimeProvider.System` outside the composition root, no static mutable field, no `Process`/`HttpClient`/GitHub/`claude`/secret-store reference; the only Kestrel listen call is `ListenLocalhost` (no `ListenAnyIP`, `UseUrls`, `ASPNETCORE_URLS`, `0.0.0.0`, `*`); no `OpenTelemetry` package or namespace anywhere in `src/` (O8). |
| AC-13 (I) | A `node.json` file on disk for `home-laptop` (tenant `acme`, `signWith: acme/1`, `bus.localPath` = the fixture clone, `secrets.root` = a scratch file store seeded with `acme/1` and the test PAT, `claude.path` = `FakeClaude.ExecutablePath`, `nodeDir` = scratch, `poll` 1 s / 1 s / 1 min, no `workHours`, `log.level: Debug`) | `NodeHost.Build(args: ["--config", path], configure: test hooks)` → `IHost.StartAsync` | The host starts through the **production** composition root; `IBusProvider` is `GitHubBusProvider` (its head check pointed at the bare repository through the test's `HandlerFactory`), `ISecretStore` is `FileSecretStore`, `IModelRunner` is `ClaudeCodeCliRunner` with `Path == FakeClaude.ExecutablePath`, `JobHandler` is `JobRunner.RunAsync`, `IPolicySource` is `NodeJsonPolicySource`; `BusListener.StartAsync` returned `Clean`; Serilog is the logger factory (the rolling file `<nodeDir>/logs/zyggy-<date>.log` exists after the first line). |
| AC-14 (I) | AC-13's host running | `GET http://localhost:<port>/health` (the test's port) | `200`, `Content-Type: application/json`, the AC-11 shape with `tenant: acme`, `machine: home-laptop`, `version` = the informational version, `lastPollUtc` non-null within 5 s of a tick, `jobsRunning: 0`; `GET /` and `GET /health/x` → `404`; `POST /health` → `405`; the server's bound addresses are loopback only (`127.0.0.1` / `::1`). |
| AC-15 (I) | AC-13 with `node.json` lacking `tenant` / `signWith` / naming a `bus.localPath` that is not a repository / whose checkout has no `tenants/acme/` / whose secret store lacks `acme/1` / while another listener holds the checkout's listen lock | `IHost.RunAsync` | The host stops by itself: exit code 3 (`tenant`, `signWith`) or 6 (`NotARepository`, `NoTenantDirectory`, `KeyMissing`, `ListenerRunning`); exactly one `Error` line naming the condition and the Core README entry; no poll, no pull, no claim; `/health` was never bound (connection refused). |
| AC-16 (I) | AC-13 with a dirty checkout (an untracked file under `tenants/acme/`) | Start | `BusStartupResult.Stashed`: one `Warning` naming the stash, the host continues, polls and claims normally (runbook "dirty working copy on start-up" → `bus-conflict.md`). |
| AC-17 (I) | AC-13's host, two jobs from `central` to two different scratch projects, scenario `done` with `ZYGGY_FAKE_CLAUDE_DELAY_MS=3000`, `maxConcurrentJobs: 2`; then a third job to the first project while it runs | Pushed from the sender, the host left running | Both first jobs are claimed within two ticks and run concurrently (`/health` shows `jobsRunning: 2`, two `fake-claude` processes exist at once); the third stays in `jobs/` until the first finishes (`Deferred(ProjectBusy)` logged once), then runs; three verified `status: done` reports on `main`, each `in_reply_to` its job; `jobsClaimedByMe` returns to 0; no `fake-claude` process remains. |
| AC-18 (I) | AC-13 with `workHours` set so that now is outside them (`FakeTimeProvider` is not available to a real host: the test writes a window that excludes the current wall-clock hour), one `normal` job and one `priority: high` job | Two ticks | The `high` job is claimed and reported; the `normal` job stays in `jobs/` (`Deferred(OutsideWorkHours)` once); the poll interval is the idle one (`/health` `lastPollUtc` advances ~60 s apart — asserted on `PollState.NextDueUtc - lastPoll` through the status, not by waiting a minute). |
| AC-19 (I) | AC-13's host with the sender pushing a commit that the head check does not report (the test's `BareRepoHeadHandler` is told to keep answering `Unchanged`), `poll.safetyFetchMinutes: 1` (the test's lowest allowed value is 1; the test waits ≤ 75 s) | — | After the safety fetch: the `Warning` of AC-7, the pull, the job claimed and reported. |
| AC-20 (I) | AC-13's host running one `hang` job | `IHost.StopAsync` (service stop / SIGTERM) | The host stops within `HostOptions.ShutdownTimeout` (20 s); the `fake-claude` tree is killed; the claim stays in `jobs/claimed/`; no report; the lock file is released; the listen lock file is gone; one `Information` `stopping: {Count} job(s) interrupted; their claims stay for the next start`. A new host over the same checkout resumes the claim (AC-10). |
| AC-21 (I) | AC-13's host | Log inspection after one job | Every line in `<nodeDir>/logs/zyggy-<date>.log` carries `tenant=acme machine=home-laptop`; lines emitted inside a run carry `phase=run jobId=<id>`; the listener's lines carry `phase=poll|pull|claim|report|reject`; no line contains the job body, the prompt, the PAT or a key (`SecretSweep` over the file); a `304` tick wrote nothing; `log.level: Debug` yields `Debug` lines, the default does not. |
| AC-22 (I) | `Program.Main` of `zyggy-node` started as a plain process (not a service) with `--config` | Run under the test for 3 s, then SIGINT/`StopAsync` | Exits 0; the `UseWindowsService()`/`UseSystemd()` calls are present and inert outside a service (the host still runs as a console process on both OSes); `--version` prints the informational version and exits 0. |
| AC-23 (G) | `Gates/P1_WalkingSkeleton.cs`, `[Trait("Category","Integration")]` (assembly-level), tenant `acme` | Part 1 | (a) **probe round trip** (ex-05 content): a signed job from `central` is claimed and answered by the real host; (b) a tampered job ends in `jobs/rejected/` with a `status: rejected` report, the model never started (no fake capture file); (c) a foreign `tenants/other-org/nodes/home-laptop/jobs/<ulid>.md` seeded in the same checkout is ignored and untouched (the node reads only its own prefix — ex-OQ4 of 04); (d) **submit → node → fake claude → report on `main`**: a job naming a scratch project (`.git`, `.claude/`), scenario `done`, through the host started by `NodeHost` with production DI, yields a verified `status: done` report with `cost_usd`, `num_turns`, `model`, the REPORT body, `runs/acme/<id>.jsonl` under `nodeDir`, and `/health` showing `lastReportUtc` set and `jobsRunning: 0`. The scenario is written so that 08 appends `zyggy status` (part 1b) and 13 appends part 2 without restructuring. |
| AC-24 (D) | Documents | Review | `src/Zyggy.Node/README.md` rewritten (what runs, `node.json` keys the service reads, exit codes, `/health` fields, log file, how to run as a console process); `src/Zyggy.Cli/README.md` `node.json` table gains `poll.safetyFetchMinutes`, `workHours`, `maxConcurrentJobs`, `log.level` ("read by the Node service"); `src/Zyggy.Core/README.md` Troubleshooting gains "Node: refused to start" (the exit-code table) and "Node: dirty working copy on start-up" linking the `bus-conflict.md` draft, "Node: job deferred", "Node: safety fetch resynchronised", "Node: claude auth"; `tools/fake-claude/README.md` documents the `auth status` mode; `src/Zyggy.Node/appsettings.json` and `appsettings.Development.json` no longer exist (ex-Q6); the founding-spec amendments A7-1..A7-6 listed below have been pasted by the orchestrating session (listed in the journal) or are recorded there as pending. |
| AC-25 (CI) | Clean clone, Windows and Linux | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`; `dotnet publish src/Zyggy.Node -c Release -r win-x64|linux-x64 --self-contained -p:PublishSingleFile=true` and the smoke run (`zyggy-node --version`) | Green on both runners; the published single-file `zyggy-node` size is recorded in the plan's gate (the cost of the Kestrel framework reference made visible, ex-Q1); `Directory.Packages.props` carries exactly the packages of the Dependencies table, exact versions. |
| AC-26 (U/I) | §8 | Every test above | No test resolves the real `claude` (`Tests_NeverResolveRealExternalPrograms` stays green); a job that fails intake never starts the model (AC-23 b); `--dangerously-skip-permissions` appears nowhere in `src/` (06's fact, re-asserted); the PAT and the HMAC key appear in no log file, no health response, no process argument (`SecretSweep`). |

---

## Decision Table

| Founding-spec / brief item | Verdict | Target type / library | Justification |
|----------------------------|---------|-----------------------|---------------|
| §3/§9 `AgentBus.Node` as a .NET Worker Service; `PollLoop` (BackgroundService) | Keep (thin) | `Zyggy.Node.PollLoop : BackgroundService` — start-up check → exit code or `BusListener.RunAsync(...)`; nothing else | The loop body already lives in `Zyggy.Core.BusListener` (roadmap: "no throwaway code — the probe listener loop is the same loop the Node hosts"). The Worker SDK and `Microsoft.Extensions.Hosting` are already referenced. |
| §9 `JobDispatcher`; §6 concurrency: one job per project, at most `maxConcurrentJobs` (2), only inside `workHours` unless `priority: high`; §6 step 3 "held → the dispatcher leaves the job in `jobs/`" | Keep — as a pure admission rule plus tracked runs | `Zyggy.Core.Jobs.JobDispatcher` (`Admit(...) → AdmissionDecision`, in-flight project set, `JobsRunning`); `BusListener` consults it through the `JobGate` delegate **before** `ClaimAsync` and tracks admitted runs instead of awaiting them | §6 wants an un-admitted job left unclaimed, which only a pre-claim gate can do; a run that blocks the loop would stop the doorbell for the job's whole duration (30 min of no polling, stale `lastPollUtc`, no second job), so admitted runs are tracked tasks and the loop keeps ticking. The decision is a pure function so the rules are unit-tested with a fake clock; the OS-level `ProjectLock.IsHeld` covers other processes (a hand-run `zyggy run`, 16), the in-flight set covers this process (a lock is only taken inside the run, after the claim). |
| §5 item 3 "10 s inside `work_hours`, 60 s outside" (deferred from 04) | Keep | `WorkHours` record (`Zyggy.Core.Policy`) + `PollModeSelector` delegate on `BusListener.RunAsync`; `PollMode` evaluated each tick | 04 built `PollMode` as an input precisely for this. The listener re-runs `ProcessOnceAsync` on `Idle → Active` because a `304` quiet night would otherwise leave a deferred job waiting for the next commit. |
| §5 item 6 safety net: unconditional `git fetch` + compare `origin/main` every 15 min regardless of ETag state (deferred from 04) | Keep | `PollOptions.SafetyFetchInterval` (15 min; `node.json` `poll.safetyFetchMinutes`), `BusRepository.SafetyFetchAsync` over `GitClient.FetchAsync` + `RevParseAsync(origin/<branch>)`, scheduled inside `BusListener.RunAsync` | It is the only defence against a wrong/missing ETag (the provider already warns when GitHub answers 200 without one) and against a proxy caching the conditional request. It lives on `BusRepository`, not on `IBusProvider`, so the seam and the provider contract suite stay untouched (the provider's `PullAsync` is the host's "bring the branch in"; the comparison is pure git). |
| §5 item 7 proxy (`HttpClient` system proxy, git `http.proxy`) | Keep (already built) | `AddGitHubBus` → `CreateHandler()` (`UseProxy`, default credentials) | Nothing to add; AC-12 keeps the Node from building a second handler. |
| §5 Git operations start-up rule (dirty tree → named stash, warning) and §10 start-up refusals (no `tenant`, no `tenants/<tenant>/`; secrets never in `node.json`) | Keep (already built) + exit codes | `BusListener.StartAsync` results mapped to `NodeExitCodes` (3 configuration, 6 bus/start-up condition — the CLI's numbers); `signWith` required by the host | 04 built every check; 07 only decides what the service does with the result: stop with a code the service manager and the runbook can read. A node that cannot sign cannot report (§4), so `signWith` is mandatory here although the probe verbs read without it. |
| 04 "resuming `claimed/` after a crash is 07/17" | Reshape — **decided 2026-10-17 (ex-Q4)** | `BusRepository.ListClaimedAsync`, `BusListener.ResumeClaimedAsync` after the start-up pull | A service is restarted by every upgrade (10); until the sweep exists (17, phase P2) an interrupted claim would be stuck for weeks. The node is the sole writer of its own `claimed/`, the files are still signed, and the sweep (17) re-queues only claims older than `timeout_minutes + 15 min`, so re-running one's own claims on start is consistent with the §4 state machine (no new transition: the report step is the same). ~40 lines over existing members; closes a real P1 gap (A7-5). |
| §11 health endpoint `http://localhost:4711/health` with the fourteen fields | Keep — Kestrel, **decided 2026-10-17 (ex-Q1)** | `NodeHealth` (immutable snapshot, `Zyggy.Node`), source-generated `System.Text.Json` context, Kestrel minimal endpoint `ListenLocalhost(4711)` (test override: port 0) via the `Microsoft.AspNetCore.App` framework reference | Consumed by `zyggy status` (08), `list_nodes` (21), the registry subset (14) and the alerts (22); the P1 gate names it. Kestrel removes every line of bespoke HTTP (request parsing, status codes, keep-alive) and has no URL-ACL requirement; `HttpListener` on Windows refuses `http://localhost:4711/` for a non-administrator unless `netsh http add urlacl` was run for that exact prefix and user (verified: Microsoft/Octopus/Austin Wise write-ups) — the service runs as the interactive user (§10). The cost is binary size, paid only by the service binary (the CLI stays slim) and recorded at the gate (AC-25); a raw-socket responder (~80 bespoke lines) was rejected. |
| §11 `claudeAuthOk` health field; §11 alert "Claude auth expired" (22) | Reshape — **decided 2026-10-17 (ex-Q3)** | `IModelRunner.CheckAuthAsync(ct) → bool?`; `ClaudeCodeCliRunner` runs `claude auth status` (exit 0/1) through `IProcessRunner`; probed at start, hourly (`NodeHostOptions.AuthProbeInterval`), and after a run that failed with `auth`; fake-claude gains an `auth status` mode (exit 0, or 1 under `ZYGGY_FAKE_CLAUDE_AUTH=0`) | Nothing outside `ClaudeCodeCliRunner` may reference `claude` (§9), so the probe must sit behind the seam; `claude auth status` is the documented, scriptable check (exit code). Deriving the value from the last job would leave a freshly installed node without the field, and 22's alert needs a proactive source (A7-4). |
| §11 `agentCoreSha`, `policyVersion` (§14) | Defer | `agentCoreSha: null` until 15; no `policyVersion` field (§14) | The pin arrives with `zyggy-core` (15); policy versions are §14. |
| §11 Logging: Serilog, one rolling file per machine `<nodeDir>/logs/zyggy-.log`, 14-day retention, Information, Debug on demand via `node.json`; every line carries `tenant`, `machine`, `jobId`, `phase` | Library (§9 names it) | `Serilog` 4.4.0 + `Serilog.Extensions.Hosting` 10.0.0 (`AddSerilog`), `Serilog.Sinks.File` 7.0.0 (`rollingInterval: Day`, `retainedFileCountLimit: 14`, `shared: false`), `Serilog.Sinks.Console` 6.1.1 (journald on Central; inert without a console); `Enrich.WithProperty("tenant"/"machine")`; `jobId`/`phase` arrive from the existing `ILogger` dictionary scopes (Serilog.Extensions.Logging maps them to properties); level from `node.json` `log.level` (ex-Q5); both `appsettings*.json` files removed from `Zyggy.Node` (decided 2026-10-17, ex-Q6) | `Microsoft.Extensions.Logging` has no rolling-file provider; a hand-written one (rotation, retention, shared write) is exactly the code §9 chose Serilog to avoid. No `Serilog.Settings.*`, no `Serilog.Enrichers.*`: the two static properties are set in code. The console sink keeps journald on Central informed and is inert under a Windows service. Default `Information`; a `304` tick is silent by construction (04). |
| §11 "log once per state change" for the new states (mode, deferral, safety fetch, auth) | Keep | Lines listed under Behaviors; the dispatcher remembers deferred ids | §11; AC-5/AC-7. |
| §9 `UseWindowsService()` / `UseSystemd()` selected at runtime | Library | `Microsoft.Extensions.Hosting.WindowsServices` 10.0.12, `Microsoft.Extensions.Hosting.Systemd` 10.0.12; both called unconditionally | Each is a no-op unless the process is actually hosted by that service manager (`WindowsServiceHelpers.IsWindowsService()` / `SystemdHelpers.IsSystemdService()`), which is what "selected at runtime" means — no detection code of our own. Versions match the `Microsoft.Extensions.Hosting` 10.0.12 already pinned (10.0.13 exists; bumping the whole `Microsoft.Extensions.*` set is a separate hygiene change, not 07's). Unit files and `install.ps1` are 10. |
| §10 `node.json`: `workHours`, `maxConcurrentJobs`, `poll.safetyFetchMinutes`; §11 "Debug on demand via `node.json`" | Keep (reader extended); `log.level` **decided 2026-10-17 (ex-Q5)** | `NodeConfigurationReader` (04's hand-validated System.Text.Json reader, not `IOptions` binding), `WorkHoursSettings`, `PollSettings.SafetyFetchMinutes`, `NodeConfiguration.MaxConcurrentJobs`, `LogSettings.Level` | The reader is the shipped pattern (04 ex-OQ5); every new key gets the same dotted-key refusal. `role`, `user`, `agentCore`, `dlp` stay ignored (11, 15, 19). |
| `workHours.timezone` as an IANA id (§10 example `Europe/Brussels`) under `InvariantGlobalization=true` | Library — **decided 2026-10-17 (ex-Q2)** | `TimeZoneConverter` 8.0.0 (`TZConvert.GetTimeZoneInfo`), MIT, no dependencies, embedded mapping | Microsoft's ICU page states IANA ids in `FindSystemTimeZoneById` are "not available when using NLS mode or globalization invariant mode" on Windows; `Directory.Build.props` sets invariant mode for every project (01, for the single-file smoke). Without a mapping the §10 example would throw `TimeZoneNotFoundException` on every laptop. Turning invariant mode off for the Node alone (ICU dependency on Central) and Windows ids in `node.json` (unportable) were rejected (A7-6). |
| §9/§3 "all logic in `Zyggy.Core`; the Node wires it into DI"; brief: "DI composition root whose production extension methods the tests reuse" | Reshape | `Zyggy.Core.Configuration.ServiceCollectionExtensions.AddNodeServices(NodeConfiguration)` (process runner, file secret store, signing, `PollOptions`, `KeyId`, `AddGitHubBus` from `bus`, `AddClaudeCodeModelRunner` from `claude.path`, `AddNodeJsonPolicy`, `AddJobRunner`, the `NodeConfiguration` itself); `Zyggy.Node.NodeHost.Build(args, configure)` adds Serilog, health, `PollLoop`, service hosting; the CLI's `BusHost` and the tests' `TestHost` call `AddNodeServices` | Today the CLI and the test harness each re-type the mapping (`BusRemoteFrom` lives only in the CLI). One Core method is what "production DI reused by tests" requires; the gate then starts the genuine host. The CLI switch is a behaviour-preserving refactor covered by its existing tests. |
| Test hooks for the gate (mocked head check, fake `claude`, scratch secret root, free port) | Keep (existing shapes) | `GitHubBusOptions.HandlerFactory` (internal, 04) through `services.Configure<GitHubBusOptions>` after `AddNodeServices`; `claude.path` and `secrets.root` and `nodeDir` through the test's `node.json`; `NodeHostOptions.HealthPort = 0` and `JobRunnerOptions.ModelEnvironment` through `NodeHost.Build(configure:)` | Only seam implementations and options are swapped (brief's design rule); no test-only branch in production code. |
| Telemetry (OpenTelemetry) | Defer (O8 decided, §14) | nothing wired; AC-12 forbids the package | Roadmap risk line; 19 adds policy keys. |
| §9 `DiscoveryTimer`, registry subset in health | Defer to 14 | — | Roadmap. |
| `zyggy status` reading `/health` | Defer to 08 | — | 08 extends the gate (part 1b). |
| Sweep, `attempt`, stale-claim re-queue | Defer to 17 | — | Only the node's own interrupted claims are resumed (ex-Q4); nothing is moved back to `jobs/`. |
| Signed `policy.yaml`, tighten-only merge, `policyVersion`; repository-per-tenant enforcement; HTTP Hub; `zyggy init` | Out (§14, MVP-scope decision) | — | The `IPolicySource` shape is kept; nothing is built for a second source. |
| `role: central` differences (`user`, memory directory requirement, §10) | Defer to 11 | — | The Node runs the same loop on Central; the Hub needs `user`. |
| §5 direct fast path (`zyggy run`), worktrees | Defer to 16 | — | Roadmap. |
| DLP, `PreToolUse` hook | Defer to 19 | — | Roadmap. |

---

## Contracts

### Namespaces and visibility (public-api.md)

| Namespace | Public | Internal (visible to the test projects) |
|-----------|--------|------------------------------------------|
| `Zyggy.Core.Bus` | `BusListener`: `RunAsync(PollModeSelector, ct)` (new overload; the `PollMode` overload stays and delegates), `JobGate` and `PollModeSelector` delegates, `Status : ListenerStatus`, `ResumeClaimedAsync(ct)`; `BusRepository`: `SafetyFetchAsync`, `ListClaimedAsync`, `HeadShaAsync`; `PollOptions.SafetyFetchInterval`; `SafetyFetchResult` | — |
| `Zyggy.Core.Jobs` | `JobDispatcher` (sealed), `AdmissionDecision` (closed), `DeferralReason` (closed) | — |
| `Zyggy.Core.Policy` | `NodePolicy.MaxConcurrentJobs`, `NodePolicy.WorkHours`, `WorkHours` record | — |
| `Zyggy.Core.Configuration` | `NodeConfiguration.WorkHours`, `.MaxConcurrentJobs`, `.Log`; `PollSettings.SafetyFetchMinutes`; `WorkHoursSettings`, `LogSettings`; `ServiceCollectionExtensions.AddNodeServices(NodeConfiguration)` | — |
| `Zyggy.Core.Models` | `IModelRunner.CheckAuthAsync(ct)` (ex-Q3) | `ClaudeCodeCliRunner` runs `auth status` |
| `Zyggy.Node` | `NodeHost` (`Build`, `Run`), `NodeHostOptions`, `NodeHealth`, `NodeExitCodes`, `PollLoop` | `HealthEndpoint` mapping, `NodeHealthJsonContext` |

### `node.json` additions (reader contract)

```jsonc
{
  "poll": { "activeSeconds": 10, "idleSeconds": 60, "safetyFetchMinutes": 15 },
  "workHours": { "timezone": "Europe/Brussels", "days": ["Mon","Tue","Wed","Thu","Fri"], "from": "08:00", "to": "19:00" },
  "maxConcurrentJobs": 2,
  "log": { "level": "Information" }          // Information | Debug (also Warning, Error); 14-day retention is fixed (ex-Q5)
}
```

| Key | Type / rule | Default when absent | Refusal (`Invalid(<key>, …)`) |
|---|---|---|---|
| `poll.safetyFetchMinutes` | positive integer | 15 | `must be a positive integer` |
| `workHours` | object or absent | **absent = always inside work hours** (`Active` polling, every job admitted; Central's expected setting) | `must be an object` |
| `workHours.timezone` | IANA id resolvable by `TimeZoneConverter` | required when the object is present | `unknown time zone` |
| `workHours.days` | non-empty array of `Mon Tue Wed Thu Fri Sat Sun` (case-sensitive), no duplicates | required | `workHours.days[i]: must be Mon..Sun` |
| `workHours.from`, `workHours.to` | `HH:mm` 24-hour, `to` > `from` (no overnight windows in v1) | required | `must be HH:mm` / `to must be after from` |
| `maxConcurrentJobs` | positive integer | 2 | `must be a positive integer` |
| `log.level` | `Debug`, `Information`, `Warning`, `Error` | `Information` | `must be Debug, Information, Warning or Error` |
| `signWith` | as 04 | — | the **host** (not the reader) refuses `null`: `signWith missing: the node must sign its reports` (exit 3) |

Every other §10 key is read later (`role`, `user` → 11; `agentCore` → 15; `dlp` → 19) and stays ignored now; a key naming a secret is refused as in 04.

### `WorkHours` and `PollModeSelector`

```csharp
namespace Zyggy.Core.Policy;

/// §5 item 3 / §6 concurrency: the machine's working window; null on NodePolicy means "always inside".
public sealed record WorkHours(TimeZoneInfo Zone, IReadOnlySet<DayOfWeek> Days, TimeOnly From, TimeOnly To)
{
    public bool Contains(DateTimeOffset nowUtc);        // convert to Zone's wall clock; day ∈ Days && From <= time < To
    public static WorkHours? FromSettings(WorkHoursSettings? settings);   // resolves the zone through TimeZoneConverter (TZConvert.GetTimeZoneInfo)
}

public sealed record NodePolicy(
    IReadOnlyList<string> DefaultAllowedTools, int DefaultTimeoutMinutes, int MaxTurns, decimal? MaxBudgetUsd, IReadOnlyList<string> DevRoots,
    int MaxConcurrentJobs,          // 2
    WorkHours? WorkHours);          // null = always inside

namespace Zyggy.Core.Bus;
public delegate PollMode PollModeSelector(DateTimeOffset nowUtc);   // the Node: WorkHours?.Contains(now) == false ? Idle : Active; the probe: _ => Active
```

### `JobDispatcher` and the `JobGate`

```csharp
namespace Zyggy.Core.Jobs;

public enum DeferralReason { OutsideWorkHours, NoSlot, ProjectBusy }
public abstract record AdmissionDecision;                       // closed
public sealed record Admitted : AdmissionDecision;
public sealed record Deferred(DeferralReason Reason) : AdmissionDecision;

public sealed class JobDispatcher
{
    // DI: IPolicySource, TimeProvider, ILogger<JobDispatcher>
    /// Pure rules in order: priority/work hours → free slot (JobsRunning < MaxConcurrentJobs) → project not in flight here and ProjectLock.IsHeld(project) false.
    public Task<AdmissionDecision> AdmitAsync(Envelope.Envelope job, CancellationToken ct);
    /// Marks a project in flight / released; JobsRunning for health. Thread-safe (two tracked runs).
    public IDisposable Track(Envelope.Envelope job);
    public int JobsRunning { get; }
    public JobGate AsGate();                                      // what BusListener consumes
}

namespace Zyggy.Core.Bus;
/// Consulted after verification and before the claim (§6 step 3 "leave the job in jobs/"). Rejected files never reach it.
public delegate Task<AdmissionDecision> JobGate(Envelope.Envelope job, CancellationToken ct);
```

The project path for the busy check is `JobFields.Project` normalised with `Path.GetFullPath` when it is rooted; a non-rooted value (a registry name) is admitted (the runner then answers `unknown_project`, 06). `priority: high` skips only the work-hours rule, never the slot or project rules (§6). A `Deferred` job is left exactly where it is; nothing is written to the bus.

### `BusListener` additions (04's loop, additive — decided 2026-10-17, ex-Q7)

```csharp
public sealed record ListenerStatus(
    PollState Poll, DateTimeOffset? LastPollUtc, DateTimeOffset? LastPullUtc, DateTimeOffset? LastReportUtc,
    string? BusHeadSha, int JobsRunning, int JobsClaimedByMe, PollMode Mode);

public sealed partial class BusListener
{
    public ListenerStatus Status { get; }                                   // immutable snapshot swapped on each change (thread-safe read)
    public Task RunAsync(PollModeSelector mode, CancellationToken ct);      // the PollMode overload = RunAsync(_ => mode, ct)
    public Task<int> ResumeClaimedAsync(CancellationToken ct);              // this machine's claimed/*.md → verify → gate → run+report; no claim commit (ex-Q4)
}
// constructor gains `JobGate? gate = null` (null = admit all) — AddGitHubBus resolves `sp.GetService<JobGate>()`.
```

Loop rules added to `RunAsync` (behaviour, not build order): each wake also checks `now >= nextSafetyFetchUtc` (then `BusRepository.SafetyFetchAsync`; when it reports `Moved`: warn, pull, `ProcessOnceAsync`, `State = State with { ETag = null }`); the mode is re-evaluated each tick (`Idle → Active` ⇒ `ProcessOnceAsync` without a pull); the wait is `min(State.NextDueUtc, nextSafetyFetchUtc) - now`. `ProcessOnceAsync` for an `AcceptedJob`: `gate` → `Deferred` ⇒ skip (dispatcher logs once per id); `Admitted` ⇒ `ClaimAsync` inline (so the next listing cannot see the file twice) ⇒ `Track` ⇒ run + `ReportAsync` as a tracked task (`Task.Run`-free continuation stored in a concurrent set; removed when done) — `ProcessOnceAsync` returns without awaiting it. `StopAsync`/cancellation: the tracked tasks observe the token (the handler kills the model process, 06), their claims stay, `Status.JobsRunning` drains; the listen lock is released on `Dispose`.

### `BusRepository` additions

```csharp
public enum SafetyFetchOutcome { Unchanged, Moved, Failed }
public sealed record SafetyFetchResult(SafetyFetchOutcome Outcome, string Detail);   // Detail redacted like BusSyncResult
public Task<SafetyFetchResult> SafetyFetchAsync(CancellationToken ct);  // git fetch origin <branch>; Moved when rev-parse origin/<branch> != HEAD; under the checkout write lock
public Task<IReadOnlyList<BusFile>> ListClaimedAsync(CancellationToken ct);  // tenants/<t>/nodes/<me>/jobs/claimed/*.md (own prefix only)
public Task<string?> HeadShaAsync(CancellationToken ct);                // for /health busHeadSha
```

### `/health` (`Zyggy.Node`)

```csharp
public sealed record NodeHealth(
    string Version, string Tenant, string Machine,
    DateTimeOffset? LastPollUtc, string? LastEtag, DateTimeOffset? LastPullUtc, string? BusHeadSha,
    int JobsRunning, int JobsClaimedByMe, DateTimeOffset? LastReportUtc,
    int? RateLimitRemaining, DateTimeOffset? BackoffUntilUtc, bool? ClaudeAuthOk, string? AgentCoreSha);
```

- `GET /health` → `200 application/json`, the record serialised with a `[JsonSerializable]` context, camelCase, nulls written, timestamps `YYYY-MM-DDTHH:MM:SSZ`; any other path `404`; any other method `405`. No authentication (loopback only, §11). `Cache-Control: no-store`.
- Bound with Kestrel `ListenLocalhost(NodeHostOptions.HealthPort)` (default **4711**; `0` = ephemeral, tests only, the bound port read back from `IServerAddressesFeature`). Never `ListenAnyIP`, never `UseUrls`, never `ASPNETCORE_URLS` (AC-12).
- Field sources: `Version` = informational version (same as `zyggy --version`); `Tenant`/`Machine` = `node.json`; `LastPollUtc`, `LastEtag`, `LastPullUtc`, `BusHeadSha`, `JobsRunning`, `JobsClaimedByMe`, `LastReportUtc`, `RateLimitRemaining`, `BackoffUntilUtc` = `BusListener.Status`; `ClaudeAuthOk` = the auth probe (`null` = not yet known); `AgentCoreSha` = `null` (15).
- "Green" is not computed by the endpoint (decided 2026-10-17, ex-Q9): there is no `status` field; the P1 gate asserts the fields directly; `zyggy status` (08) defines the human verdict from them.

### Hosting (`Zyggy.Node`)

```csharp
public sealed class NodeHostOptions
{
    public int HealthPort { get; set; } = 4711;                       // 0 in tests
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan AuthProbeInterval { get; set; } = TimeSpan.FromHours(1);   // claude auth status re-probe (ex-Q3)
}

public static class NodeHost
{
    /// Parses --config/--version, reads node.json (exit 3 on refusal), builds the host: AddNodeServices(configuration)
    /// + Serilog + JobDispatcher/JobGate + PollModeSelector + health + PollLoop + UseWindowsService() + UseSystemd().
    public static IHost Build(string[] args, Action<IServiceCollection>? configure = null);
    public static Task<int> RunAsync(string[] args, CancellationToken ct);  // Program.Main
}

public static class NodeExitCodes { public const int Ok = 0, Configuration = 3, Startup = 6, Faulted = 1; }
```

`PollLoop.ExecuteAsync`: `listener.StartAsync` → not `Clean`/`Stashed` ⇒ `Error` line + `NodeExitCodes.Startup` ⇒ `IHostApplicationLifetime.StopApplication()` (the health server is started only after the check passes); else `ResumeClaimedAsync` → `RunAsync(modeSelector, stoppingToken)`. An unexpected exception ends the host (`BackgroundServiceExceptionBehavior.StopHost`, exit 1, one `Error` line). `UseSystemd()` sends `READY=1` when the host has started; a failed start-up check then stops the unit with the exit code (systemd `Restart=`/`RestartSec=` and Windows recovery settings are 10's unit files; the runbook says what each code means).

### Serilog configuration (in `NodeHost`, code only — no `appsettings.json` keys)

- `MinimumLevel` from `log.level`; `Microsoft.*`/`System.*` overridden to `Warning`.
- `Enrich.WithProperty("tenant", …)`, `Enrich.WithProperty("machine", …)`; scope properties (`phase`, `jobId`) come from the existing `ILogger` scopes through `Serilog.Extensions.Logging`.
- `WriteTo.File(Path.Join(nodeDir, "logs", "zyggy-.log"), rollingInterval: Day, retainedFileCountLimit: 14, shared: false, outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ssZ} [{Level:u3}] {tenant} {machine} {phase} {jobId} {SourceContext}: {Message:lj}{NewLine}{Exception}")`; `WriteTo.Console(...)` same template (journald on Central).
- `nodeDir` default per 06 (`%LOCALAPPDATA%\zyggy` / `~/.local/share/zyggy`); the directory is created; a log directory that cannot be created stops the host with exit 6 and the reason on stderr (the only line the console sink carries in that case).
- `appsettings.json` and `appsettings.Development.json` are removed from `Zyggy.Node` (the §10 single configuration file is `node.json`; secrets never in `appsettings.json` becomes trivially true).

### Envelope / file formats

No envelope field is added, removed or reinterpreted; no `PROTOCOL.md` change. `priority` (§4, existing) is read by the dispatcher. Bus moves are the 04 ones; a resumed claim uses the existing `claimed/ → reports/ + rm` transition only.

### CLI surface

None (`zyggy status` is 08). `zyggy-node` accepts `--config <path>` (then `ZYGGY_NODE_JSON`, then the per-user default) and `--version`.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `poll.activeSeconds`, `poll.idleSeconds` | `node.json` (04) | 10 / 60 | `node.json` only |
| `poll.safetyFetchMinutes` | `node.json` → `PollOptions.SafetyFetchInterval` | 15 | `node.json` only; tests may set 1 |
| `workHours` | `node.json` → `NodePolicy.WorkHours` | absent = always inside | `node.json` only in v1 (`policy.yaml` would tighten it, §14) |
| `maxConcurrentJobs` | `node.json` → `NodePolicy.MaxConcurrentJobs` | 2 | `node.json` only in v1 (`policy.yaml` could only lower it, §14) |
| `log.level` | `node.json` → Serilog minimum level | `Information` | `node.json` only; a restart applies it |
| log retention | code | 14 files (days) | none in v1 (§11 fixes it) |
| `claude.path` | `node.json` → `ClaudeCodeOptions.Path` (06: "07 binds it") | `claude` on `PATH` | `node.json` only (the CLI's `ZYGGY_CLAUDE_PATH` stays for `zyggy dream`) |
| `nodeDir` | `node.json` (06) → transcripts `runs/`, logs `logs/` | per-OS default | `node.json` only |
| `secrets.root` | `node.json` (04) → `FileSecretStoreOptions.RootDirectory` | per-OS default | until 09 replaces the file store |
| health port | `NodeHostOptions.HealthPort` | 4711 (§11) | options only — tests use 0; production never changes it (`zyggy status`, `list_nodes` depend on it) |
| shutdown timeout, auth probe interval | `NodeHostOptions` | 20 s, 1 h | options only |
| `ZYGGY_NODE_JSON`, `--config` | process | per-user default path (04) | the unit file / service registration (10) sets one of them |

---

## Behaviors & Conventions

- **Start-up order**: read `node.json` (refusal ⇒ exit 3, one stderr line, nothing else started) → build the host → Serilog opens the log file → `PollLoop`: `BusListener.StartAsync` (stash/warn, refuse ⇒ exit 6) → auth probe → health server bound on loopback → `ResumeClaimedAsync` → `RunAsync`. The health server is never reachable while a start-up refusal is possible.
- **Tick**: wait until the earlier of the poller's next due instant and the next safety fetch; ring the doorbell (`304` silent); on `Changed` pull and process; re-evaluate the mode each tick; `Idle → Active` processes the inbox once without a pull.
- **Admission before claim**: verification first (04), then the gate (work hours unless `high` → slot → project), then the claim. A deferred job is logged once (`Information`, with the reason) and silently re-examined on later passes; its admission is logged once. Nothing is written to the bus for a deferred job.
- **Runs do not block the loop**: an admitted job is claimed inline and run+reported as a tracked task; the loop keeps polling; at most `maxConcurrentJobs` run; bus writes from concurrent runs queue on the existing checkout lock.
- **Safety fetch** every `poll.safetyFetchMinutes`: `Unchanged` ⇒ silent; `Moved` ⇒ one `Warning`, pull, process, ETag reset; `Failed` ⇒ one `Warning` once per failure streak (the network back-off of the doorbell is unaffected).
- **Stop**: cancellation ends the loop, kills running model processes (06), leaves their claims, releases the project and listen locks, flushes Serilog (`CloseAndFlush` through `AddSerilog`'s disposal); exit 0.
- **Resume**: own `claimed/` files that verify are run again through the gate (a `normal` job outside work hours waits until inside; the claim is already made); files that do not verify are left with one `Warning` for the sweep (17).
- **Health** is a snapshot of in-memory state; it never touches git or the network on request (the sha and claimed count are refreshed by the loop after each pull/write). `claudeAuthOk` is `null` until the first probe answers.
- **Logging** (`ILogger<T>` everywhere; Serilog only in `NodeHost`): new lines, once per state change —
  - `Information` `poll mode {Mode}` (listener, on change), `job {Id} deferred: {Reason}` / `job {Id} admitted after deferral` (dispatcher), `resuming {Count} claimed job(s) from a previous run`, `stopping: {Count} job(s) interrupted; their claims stay for the next start`, `health endpoint listening on http://localhost:{Port}/health`, `claude auth ok` (first `true`), `node started: tenant {Tenant}, machine {Machine}, version {Version}` (once)
  - `Warning` `safety fetch: branch moved while the doorbell said unchanged; resynchronising`, `safety fetch failed: {Detail}` (once per streak), `claimed job {Id} does not verify; left for the sweep`, `claude auth expired` (on `true → false`), `maxConcurrentJobs {N} > 4: more than four parallel claude runs is unlikely to be intended` (advisory, once)
  - `Error` `refusing to start: {Condition} — see README "Node: refused to start"`, `node faulted: {ExceptionType}`
  - never the body, the prompt, the PAT, a key, a header value.
- **Exit codes**: 0 normal stop; 3 configuration (`node.json`); 6 start-up condition (`NotARepository`, `WrongBranch`, `NoTenantDirectory`, `RebaseInProgress`, `KeyMissing`, `ListenerRunning`, log directory not writable, health port in use); 1 unexpected fault.
- **Time**: every instant from the registered `TimeProvider` (`TimeProvider.System` in production); the dispatcher, `WorkHours`, the safety-fetch schedule and the auth-probe schedule are testable with `FakeTimeProvider`.
- **Rule R1**: everything here is .NET; the only shell is 10's launcher (`ExecStart=` / service registration), which runs the binary.
- **Public hygiene**: fixtures use tenant `acme`, machines `central`/`home-laptop`, zone `Europe/Brussels` as in §10; no owner, employer, host or id anywhere.

---

## Failure modes

| Situation | Observable outcome | Runbook entry (Core README until 10) |
|-----------|--------------------|--------------------------------------|
| `node.json` missing / invalid / no `tenant` / no `signWith` / secret-looking key / bad `workHours` | stderr `zyggy-node: configuration error: <key>: <detail>`; exit 3; nothing started | "Node: refused to start" — exit-code table; the dotted key names the fix; `signWith` is mandatory for the service |
| Checkout not a repository / wrong branch / no `tenants/<tenant>/` / rebase in progress / signing key absent / another listener | one `Error` with the condition; exit 6; `/health` never bound | "Node: refused to start" → the existing "Bus: checkout" rows; `ListenerRunning`: a `zyggy probe listen` or a second service shares the checkout — stop it or give the service its own `bus.localPath` |
| Dirty working copy at start-up | `Stashed`: one `Warning` naming `zyggy-<machine>-<timestamp>`; the service runs | "Node: dirty working copy on start-up" → `bus-conflict.md` draft ("Bus: dirty working copy at start-up"): inspect/drop the stash; Zyggy never resets |
| Log directory not creatable / health port 4711 in use | exit 6 with the reason (stderr + console sink) | "Node: refused to start" — `nodeDir` permissions; `netstat`/`ss` for 4711 (a second node on the machine is not supported: one checkout, one service) |
| Job outside work hours (`normal`) / no free slot / project busy | left in `jobs/`; one `Information` per job per deferral episode; `/health` unaffected | "Node: job deferred" — resubmit with `priority: high` for an out-of-hours run; `maxConcurrentJobs`; the lock file of the project |
| Doorbell lies (ETag never changes) or proxy caches the conditional request | job delayed ≤ `safetyFetchMinutes`; one `Warning` at resynchronisation | "Node: safety fetch resynchronised" — if it repeats, the host is answering `200` without an ETag (04's provider warning) or the proxy strips it; raise the issue with the proxy or lower `safetyFetchMinutes` |
| Safety fetch fails (network) | one `Warning` per streak; polling continues with its own back-off | "Bus: pull failed" |
| Service stopped / crashed during a run | claim stays; model tree killed; on the next start the claim is resumed and run again | "Node: interrupted run" — a second transcript file overwrites the first (06 edge case); until 17, a claim that never verifies again must be moved back to `jobs/` by hand |
| `claude auth status` reports not logged in | `claudeAuthOk: false`; one `Warning` on the transition; jobs still run and fail with `claude_error: auth` (06) | "Node: claude auth" — `claude login` as the service user; the alert is 22 |
| Unexpected exception in the loop | `node faulted: <type>`; exit 1; the service manager restarts per its unit | "Node: refused to start" footnote; file a bugfix with the log |
| Two concurrent runs write reports at once | second write waits on the checkout lock (≤ 60 s), then pulls and pushes; no conflict (two files) | — (04 "Bus: push deferred" if the push fails) |

---

## Dependencies

| Package | Version | License | Why (what bespoke code it removes) |
|---------|---------|---------|------------------------------------|
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.12 (matches the pinned `Microsoft.Extensions.Hosting`; 10.0.13 is current on nuget.org, 2026-10-13) | MIT | `UseWindowsService()`: SCM lifetime, no-op outside a service. §9 package table. |
| `Microsoft.Extensions.Hosting.Systemd` | 10.0.12 (same) | MIT | `UseSystemd()`: `sd_notify` READY/STOPPING, no-op outside systemd. §9. |
| `Serilog` | 4.4.0 (nuget.org, 2026-07-10) | Apache-2.0 | Structured pipeline, enrichment, levels. §9. |
| `Serilog.Extensions.Hosting` | 10.0.0 (2025-11-28; depends on `Serilog ≥ 4.3.0`, `Serilog.Extensions.Logging 10.0.0`, `Microsoft.Extensions.*.Abstractions ≥ 10.0.0`) | Apache-2.0 | `AddSerilog` as the `ILoggerFactory`; maps `ILogger` scopes (`tenant`/`machine`/`phase`/`jobId` dictionaries already emitted by 04/06) to properties; flush on host shutdown. |
| `Serilog.Sinks.File` | 7.0.0 (2025-04-28) | Apache-2.0 | Daily rolling file with retention — the code §9 chose not to write. |
| `Serilog.Sinks.Console` | 6.1.1 (2025-11-02) | Apache-2.0 | stdout for journald on Central and for console runs; inert under a Windows service. §9. |
| `TimeZoneConverter` | 8.0.0 (2026-10-12; no dependencies on net8/9/10) | MIT | IANA → `TimeZoneInfo` without ICU (embedded mapping), so `workHours.timezone: "Europe/Brussels"` works on Windows under `InvariantGlobalization=true`. Replaces nothing bespoke; prevents a hand-kept mapping table. |
| `Microsoft.AspNetCore.App` | framework reference (ships with the .NET 10 SDK/runtime; not a NuGet package, not in `Directory.Packages.props`) | MIT | Kestrel + minimal routing for `/health`: no hand-written HTTP parsing, no URL ACL. Cost: single-file size (recorded at the gate, AC-25). |
| none for telemetry | — | — | O8. |

All verified on nuget.org on 2026-10-17 (versions, licenses, dependency lists); each is a managed-only package usable in a single-file self-contained publish (no native assets). The planner pins exact versions in `Directory.Packages.props` and the CI publish + smoke step is the proof (AC-25).

---

## Deliberate deviations from the founding spec

All decided by the owner on 2026-10-17 ("fix all inconsistencies, and I approve all your recommendations"); the exact wording is under "Founding-spec amendments to apply".

- §3 "Node instance: `C:\agents\node` or `~/agents/node` … `node.json`" vs the shipped reader's default path (`%LOCALAPPDATA%\zyggy\node.json`, `~/.config/zyggy/node.json`, `ZYGGY_NODE_JSON`, `--config`; 04 ex-OQ5): the service follows the reader — A7-1 (a 04 leftover, not a 07 design choice).
- §10 `workHours` absent ⇒ always inside: the spec is silent; Central's node must run and poll at the active interval at any hour — A7-2.
- §10 `log` section for the §11 "Debug on demand via `node.json`" (ex-Q5) — A7-3.
- §11 `claudeAuthOk` obtained from `claude auth status` through the new `IModelRunner.CheckAuthAsync` (ex-Q3); §6 "Runner inputs/outputs" is a shared contract, so this additive member is recorded as a deviation — A7-4.
- §4 state machine unchanged, but a node re-runs its own `claimed/` files on start (ex-Q4) — A7-5.
- §9 package table: `TimeZoneConverter` (ex-Q2) and the Kestrel framework reference (ex-Q1) — A7-6.
- §9 names `GitHubPoller`, `PollLoop`, `JobDispatcher`: `PollLoop` and `JobDispatcher` are kept as names; the loop body stays `BusListener` (04, ex-Q7) — no founding-spec text.
- 04's `BusHost` composition root moves into `Zyggy.Core.AddNodeServices` (ex-Q8) — a `Zyggy.Core` contract, no founding-spec text.

### Founding-spec amendments to apply (exact final wording, approved by the owner 2026-10-17; the orchestrating session is applying them to the founding spec and lists every edit in the journal — never the technical-analyst or the planner)

- **A7-1 — §3 "Node instance (laptops)"**: replace "`node.json` (tenant, machine name, dev roots, work hours)" with "`node.json` (tenant, machine name, dev roots, work hours) at the per-user path of §10 (`%LOCALAPPDATA%\zyggy\node.json` on Windows, `~/.config/zyggy/node.json` elsewhere) or the path given by `ZYGGY_NODE_JSON` / `--config`".
- **A7-2 — §10 example**: after the `workHours` line add the comment `// absent = always inside work hours (Central): active polling and jobs at any hour`.
- **A7-3 — §10 example** (ex-Q5): after `"maxConcurrentJobs": 2,` insert `"log": { "level": "Information" },        // Debug on demand (§11); 14-day retention is fixed`; in §11 "Logging" first bullet replace "Debug on demand via `node.json`" with "Debug on demand via `node.json` `log.level`".
- **A7-4 — §11 "Health endpoint"** (ex-Q3): append "`claudeAuthOk` is the result of `claude auth status` (exit code) run through `IModelRunner.CheckAuthAsync` at start, hourly and after a run that failed with an authentication error; `null` until the first probe."
- **A7-5 — §5 "Git operations"** (ex-Q4): append to the start-up bullet "On start-up, after the first pull, the node re-runs the jobs still in its own `jobs/claimed/` that verify (its previous run was interrupted); a claimed file that no longer verifies is left for the sweep."
- **A7-6 — §9 Packages table**: add the row "Time zones | `TimeZoneConverter` | IANA ids for `workHours.timezone` under invariant globalization" (ex-Q2) and the note "Health endpoint: Kestrel via the `Microsoft.AspNetCore.App` framework reference, loopback only" (ex-Q1).

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| `workHours` crosses midnight (`from: 22:00, to: 06:00`) | Refused by the reader (`to must be after from`); v1 has no overnight windows |
| DST transition inside the window | Evaluated in the zone's wall clock through `TimeZoneInfo`; a skipped/repeated hour follows .NET's conversion; no job is wrongly deferred across the change (AC-3) |
| `maxConcurrentJobs: 1` | Strictly sequential; the loop still polls during the run |
| `maxConcurrentJobs` larger than 4 | Accepted with one advisory `Warning` |
| Two jobs for the same project arrive in one commit | First admitted and claimed; second `Deferred(ProjectBusy)` until the first's lock is released and its run tracked task has ended |
| A job is deferred for work hours, then the window opens while the branch is quiet | The `Idle → Active` transition triggers `ProcessOnceAsync`; the job is admitted at most one tick after the window opens |
| Deferred job removed by Central before admission (not possible by the write rules; a future sweep) | Simply disappears from the listing; the dispatcher forgets the id |
| Safety fetch while a tracked run writes its report | Both take the checkout lock in turn; the fetch may see the report's commit as "moved" only if the push raced ahead of the local branch — `HEAD` vs `origin/<branch>` compares shas, so a pushed local commit counts as unchanged |
| Safety fetch and the doorbell fire in the same tick | One pull, one `ProcessOnceAsync` |
| Health request during start-up | Connection refused (not yet bound); after a refusal, never bound |
| Health request while a pull runs | Served from the last snapshot; no git call on the request path |
| `claude auth status` itself hangs | 30 s timeout through `IProcessRunner`; `claudeAuthOk` stays as it was; one `Warning` per streak |
| The sweep (17) re-queues a job this node is still running | The run's `ReportAsync` fails at `git rm` of the claimed file (`WriteFailed`) and is logged (04); the re-queued copy runs again — accepted duplicate until 17 defines `attempt` handling |
| `node.json` changed while running | Not reloaded; a restart applies it (documented) |
| Windows service account without `LOCALAPPDATA` | `nodeDir`/`secrets.root` defaults fail with the existing `RootDirectory must be set` message → set `nodeDir` and `secrets.root` explicitly (10's install writes them) |
| `--version` | Prints the informational version, exit 0, reads no `node.json` |

---

## Out of Scope

- `install.ps1`, systemd unit and Windows service registration, `LoadCredential=`, real machines, runbook directory (10); OS secret stores (09); `zyggy status` and the gate's part 1b (08).
- `DiscoveryTimer`, registry subset of health, `registry/<machine>.yaml` `last_seen`/`health` (14); `agentCoreSha` value (15); worktrees, `zyggy run` (16); the sweep, `attempt`, stale-claim re-queue, archive (17); signed `policy.yaml`, tighten-only merge, `policyVersion` (18/§14); DLP, `PreToolUse`, telemetry policy keys (19); alerts, `INotifier` (22); Hub, `user`/`role` handling (11, 21).
- Concurrency beyond one process per checkout (a second service on the same machine), hot reload of `node.json`, a configurable health port in production, authentication on `/health`, HTTPS, any non-loopback binding.
- OpenTelemetry, metrics, traces (O8, §14); `zyggy diagnose`, `zyggy init` (§14); a second `IBusProvider`/`IModelRunner` implementation.
- Any change to the §4 envelope, the state-machine transitions or `PROTOCOL.md`.
- Overnight work-hour windows; per-day windows; holidays.

---

## Decisions taken at the spec gate (2026-10-17, owner: "fix all inconsistencies, and I approve all your recommendations")

| Q | Decision | Reason |
|---|----------|--------|
| Q1 Health endpoint host | Kestrel through the `Microsoft.AspNetCore.App` framework reference, `ListenLocalhost(4711)` only (A7-6). | Zero bespoke HTTP code and no http.sys URL ACL: `HttpListener` on Windows refuses `http://localhost:4711/` for the non-administrator interactive user the service runs as unless `netsh http add urlacl` was run for that exact prefix; a raw-socket responder would be ~80 lines Zyggy owns. The size cost falls on `zyggy-node` only and is recorded at the gate (AC-25). |
| Q2 `workHours.timezone` under invariant globalization | `TimeZoneConverter` 8.0.0 (MIT, no dependencies) resolves the IANA id on both OSes (A7-6). | Microsoft documents that IANA ids in `FindSystemTimeZoneById` are unavailable on Windows in globalization-invariant mode, which `Directory.Build.props` sets for every project; disabling invariant mode for the Node would make Central depend on `libicu`, and Windows ids would make `node.json` unportable. |
| Q3 Source of `claudeAuthOk` | `IModelRunner.CheckAuthAsync(ct) → bool?`, implemented in `ClaudeCodeCliRunner` with `claude auth status` (exit 0/1); probed at start, every `AuthProbeInterval` (1 h) and after a run failing with `auth`; `tools/fake-claude` answers `auth status` (exit 0, or 1 under `ZYGGY_FAKE_CLAUDE_AUTH=0`) (A7-4). | The field means what §11 says on a freshly installed node and 22's alert gets a proactive source; deriving it from the last job would leave a node that never ran a job without a value; `claude` may be referenced only inside the seam implementation (§9). |
| Q4 Own `jobs/claimed/` after a restart | Resumed after the start-up pull: verify → gate → run → report, no second claim commit; a file that no longer verifies is left for the sweep (A7-5). | Every upgrade restarts the service and a stop kills the running model process without a report; until the sweep (17, P2) an interrupted claim would be stuck indefinitely. The node is the sole writer of its own `claimed/`; the §4 transitions are unchanged. |
| Q5 Log level key | `node.json` `"log": { "level": "Information" }`; retention fixed at 14 days (A7-3). | §11 promises "Debug on demand via `node.json`" and §10 had no key; a section leaves room for later keys; a restart applies it. |
| Q6 Console sink and `appsettings*.json` | File + console sinks; `appsettings.json` and `appsettings.Development.json` deleted from `Zyggy.Node`. | journald on Central reads stdout; the console sink is inert under a Windows service; dead configuration files are a hygiene risk and §10's single configuration file is `node.json`. |
| Q7 One loop | 04's `BusListener` is extended additively (`JobGate`, `PollModeSelector`, tracked runs, safety fetch, `Status`, `ResumeClaimedAsync`); the probe path keeps its defaults and 04's tests stay unchanged. | The roadmap forbids a second copy of the bus loop; a Node-only loop would duplicate ~300 lines of verified §4/§5 logic. |
| Q8 Composition root | `Zyggy.Core.Configuration.ServiceCollectionExtensions.AddNodeServices(NodeConfiguration)`; the CLI's `BusHost` and the tests' `TestHost` switch to it; `Zyggy.Node.NodeHost` adds only host concerns. | One mapping instead of two (soon three); "production DI reused by the tests" requires it; 08 builds `submit`/`status` on the same method. |
| Q9 "`/health` green" | No verdict field in the endpoint; the P1 gate asserts the §11 fields; `zyggy status` (08) defines green from them. | Keeps §11's shape verbatim and the human policy in the one human-facing tool. |

---

## Note for the planner and the project-manager

1. **Gate file ownership**: `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` is created here with part 1 (probe round trip content + submit → node → fake claude → report); 08 appends `zyggy status` (part 1b), 13 appends part 2. The roadmap entries of 08 and 13 should say so.
2. **Roadmap reconciliation observed while reading** (for the project-manager, not changed here): the overview row for 06 (line 104) still reads "**Next** … spec not started" although `_specs/06-job-runner.md` (approved 2026-10-15) and `_plans/06-job-runner.md` exist and Steps 1–11 are checked on `feature/06-job-runner` (only the final definition-of-done gate and the release step 12 are open); 07's entry should carry the forwarded items from `_plans/06-job-runner.md` line 488 (`Gates/P1_WalkingSkeleton.cs`, the `IsHeld` pre-claim check, the `nodeDir`/`claude.path` binding) and the ex-05 gate content (06 ex-Q10); `CLAUDE.md`'s "`src/Zyggy.Node` … still the empty host" remains true until 07 ships.
3. **Owner-run items in this deliverable**: none that need a real machine — everything runs against the bare repository and the fake; the first real service is 10. The single-file size of `zyggy-node` with the Kestrel framework reference is recorded at the gate.
4. **06's forwarded security note** (the model process could read the P0 file secret store) belongs to 09's spec, not here; 07 does not change where the key lives.
