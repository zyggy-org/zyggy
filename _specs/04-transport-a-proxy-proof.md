# Spec: 04 — Prove transport A through the corporate proxy (the transport proof)

> Founding-spec sections: §1 Constraints (work-laptop egress "verified in P0"); §4 Repository layout, One repository per tenant, Write rules, Signature (receiver rejections incl. `to`), State machine rows 1–3; §5 Poll loop items 1–7, Git operations, Credentials per machine, `main` protection; §8 Secrets (HMAC keys, GitHub PAT, deploy key / write PAT, the P0 file-store paragraph), Isolation, Injection, Audit; §9 naming map, `Bus/` folder, packages, every design rule; §10 `node.json`, Central; §11 Logging, runbooks `bus-conflict.md`, `add-tenant.md`; §12 Definition of done; §13 transport row ("confirmed against the corporate proxy in P0"), tenancy shape, one bus repository per tenant, WebSocket/MQTT deferred; §14 Tenancy, Pluggable edges. Roadmap entry: `_plans/ROADMAP.md` #04 and its hand-off brief of 2026-10-12. Decision record: `_plans/decisions/0001-transport-and-vm.md` (transport section). Project inputs honoured: `_specs/03-envelope-signing.md` ("Defer to 04" rows), `_specs/28-central-dream-local.md` (`GitClient` contract), `.claude/skills/bus-protocol/SKILL.md`, `.claude/skills/integration-testing/SKILL.md`, `.claude/instructions/public-api.md`.
>
> Status: **approved by the owner on 2026-10-12 — zero Open Questions; planner-ready.** The ten questions of the draft (the brief's OQ1–OQ8, plus OQ9 "what does *leave the change uncommitted* mean after three failed pushes" and OQ10 "how is a rejection report addressed when the job's header is unreadable") were all decided as recommended ("approved"). Every decision is folded into the tables below and marked *decided 2026-10-12 (ex-OQn)*; the founding-spec wording the owner still has to paste is listed under "Founding-spec amendments to apply". Earlier owner decisions of 2026-10-12 stand: the bus repository `zyggy-bus` is separate from the memory repository; transport A is decided; candidate C is evaluated in the transport decision record (05), never built here.

## Current state (verified against the repo on 2026-10-12)

| Item | State today |
|------|-------------|
| Bus code | None: no `Bus/` folder, no `IBusProvider`, `BusPaths`, `BusRepository`, `GitHubPoller`/`PollState`, no `zyggy probe`, no `node.json` reader, no `tests/Zyggy.Integration/Gates/`. `Zyggy.Node` and `Zyggy.Hub` are empty hosts. |
| Reusable as is | `Zyggy.Core.Envelope` (parser, writer, `EnvelopeSigner`, `EnvelopeId`, `KeyId`, `ReportStatus.Rejected`, `EnvelopeRejectionReason`), `Tenancy` (`TenantId`, `MachineName`), `Secrets` (`ISecretStore`, `InMemorySecretStore`, `FileSecretStore` + `AddFileSecretStore`, `SecretName`), `Processes` (`IProcessRunner`, `ProcessSpec`, `ProcessResult`, `AddProcessRunner` registering `TimeProvider.System`), the CLI host (`CliApplication`, `RawVerbs`, `ExitCodes` 0/1/2/3/4/5/6/7, `CliEnvironment`), test infrastructure (`BusRepoFixture` without tenant/seeding, `RecordingProcessRunner`, `ZyggyCli` out-of-process harness, `FakeTimeProvider`). |
| `Zyggy.Core.Git.GitClient` (`internal sealed`, spec 28) | status/add/rm/commit `--only`/push `HEAD:<branch>`/fetch/rebase/abort/rev-parse/rev-list/symbolic-ref, index.lock retry with `TimeProvider`, `GitClientOptions { Executable, Timeout, Environment }`, `GIT_TERMINAL_PROMPT=0` always. **Missing for the bus**: `mv`, `pull --rebase`, `stash push`, `ls-remote`, `push --dry-run`, `config --get`, per-machine commit identity. Used live on Central by the dream, brief and offload paths — every change here is additive. |
| `CredentialFileSecretStore` (spec 33/36) | Serves only the fixed names `m365-app-key`, `m365-app-key/new`, `linkedin/*`; refuses any other name (`"key: no credential named …"`). It cannot serve `hmac/1` or `github-pat` without changes (OQ2). |
| Product `HttpClient`s | `GraphHttp`, `LinkedInHttp`, `McpProbe` all set `UseProxy = false`. The bus client must not copy this (§5 item 7). |
| Packages | `YamlDotNet`, `Ulid`, `Microsoft.Extensions.{DependencyInjection.Abstractions, Options, Logging.Abstractions}`, `ModelContextProtocol` in Core; `System.CommandLine`, `Microsoft.Extensions.Hosting` in Cli (console logging is transitive). No new package is needed for 04. |
| Live estate | Central: Azure VM, Ubuntu 24.04, user `zyggy`, `zyggy` v0.7.0 pinned under `/opt/zyggy/<version>/`, deploy keys per repository via `~zyggy/.ssh/config` host aliases `github.com-zyggy-<repo>` (0002). Laptops: no Zyggy binary. GitHub org `zyggy-org`: `zyggy-core`, `zyggy-geoffrey`, `zyggy-geoffrey-memory`; **no `zyggy-bus` yet**. |
| Conventions inherited | Tenant `acme` in every test and fixture; `geoffrey` only in live configuration. Runbook entries live in the project READMEs' Troubleshooting sections until the runbook directory exists (10) — same choice as specs 01 and 03. |

## Context — the three machines (from the brief; the spec's constraints, not its scope)

| Machine | Egress | Runs the probe as | Secret store in 04 | Git auth to prove (OQ3) |
|---------|--------|-------------------|--------------------|-------------------------|
| Central (`central`) | unrestricted; inbound only Tailscale SSH | foreground `runuser -u zyggy -- zyggy probe …` (no unit) | `FileSecretStore`, default root `~zyggy/.local/share/zyggy/secrets` (decided, ex-OQ2) | API: fine-grained PAT (read); push: deploy key over SSH, host alias `github.com-zyggy-bus`, like the memory repository |
| Home laptop (`home-laptop`) | unrestricted | foreground, owner's interactive account; `win-x64` binary copied by hand | `FileSecretStore`, `%LOCALAPPDATA%\zyggy\secrets` | API: own PAT; push: HTTPS with the same PAT via Git Credential Manager (deviation from §5's "deploy key", decided ex-OQ3; 05 amends the §5 table from the measurement) |
| Work laptop (`work-laptop`) | corporate proxy; GitHub/Anthropic over 443 *believed* reachable; SSH 22 expected blocked; possible TLS inspection; owner-only access | foreground, owner's account; same binary | `FileSecretStore` | API: own PAT; push: HTTPS with the same PAT via Git Credential Manager (§5 O19 path); one documented git-settings pass after a failing `reach`, then the result goes to 05 (decided, ex-OQ7) |

Everything the work laptop writes to the bus is a signed `report` with the probe's fixed text; no `claude`, no work data, no product job, nothing beyond GitHub endpoints (§8 work boundary, respected in the spike).

## User Story

**As** Central, **I want** `zyggy probe send --to <machine>` to write a signed probe job into the tenant's `zyggy-bus` repository and to tell me, per probe, how long it took until a verified report from that machine was on `main`, **so that** the P0 transport gate has a measured, sender-side round-trip time per machine (O21) and the transport decision record (05) can confirm transport A or open the fallback.

**As** a laptop (home or work), **I want** `zyggy probe listen` to wake on GitHub's conditional poll, pull over HTTPS, verify the envelope before anything else, claim it, answer with a signed report and push through whatever proxy and credential helper my machine has, **so that** the same `Zyggy.Core` loop later hosted by the Node service (07) is proven on the hardest network path before any product code depends on it.

**As** the owner, **I want** `zyggy probe reach` to tell me per machine which of the four GitHub paths (API over 443 with a PAT, `git fetch` over 443, `git push` over 443 with the PAT, SSH on 22) actually works through the system proxy, with the error class when one does not, **so that** the 3 × 4 reachability matrix in `0001-transport-and-vm.md` is measured, not assumed (§1).

---

## Acceptance Criteria

Tenant `acme`, machines `central`, `home-laptop`, `work-laptop`, keys `acme/1`/`acme/2` (03's test key material) in every automated test; `geoffrey` appears only in the live `node.json` files. **U** = unit (`tests/Zyggy.Core.Tests`), **I** = integration (`tests/Zyggy.Integration`, bare repo, real `git`, no network), **O** = owner-executed, recorded in `_plans/decisions/0001-transport-and-vm.md`, **D** = document.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 (U) | `BusPaths`, tenant `acme`, machine `home-laptop`, id `01J8Y3N7Q2X9Z4A5B6C7D8E9F0` | Every path function | Exactly: `tenants/acme`, `tenants/acme/registry/home-laptop.yaml`, `tenants/acme/nodes/home-laptop/jobs`, `…/jobs/claimed`, `…/jobs/rejected`, `…/reports`, `…/context`, `tenants/acme/archive/2026-09`, `<folder>/01J8Y3N7Q2X9Z4A5B6C7D8E9F0.md`, `PROTOCOL.md`; always `/` separators; `TryParse` of each produced path yields the same `(tenant, machine, folder, id)`; `TryParse("tenants/globex/nodes/x/jobs/<id>.md")` yields tenant `globex` (parsing never assumes a tenant); `TryParse("tenants/acme/policy.yaml")`, `"../x"`, `"tenants/Acme/…"` → false. |
| AC-2 (U) | `src/` | Source hygiene test | No literal `"tenants/"`, `"nodes/"`, `"jobs/"`, `"reports/"`, `"context/"`, `"archive/"`, `"registry/"` outside `BusPaths`; no `api.github.com`, `github.com` or `x-ratelimit` outside `GitHubBusProvider`; no `UseProxy = false` under `Bus/`; no `TenantId.Default`/parameterless overloads; no `static` mutable field; no `DateTime.UtcNow`/`TimeProvider.System` outside DI registration. |
| AC-3 (U) | `PollState.Initial(now)` in `Active` mode, `PollOptions` defaults, a fake clock | `Apply(Unchanged)` ×n with `jitterUnit` ∈ {−1, 0, +1} | `NextDueUtc − now` ∈ [8 s, 12 s] exactly matching `10 s + jitterUnit × 2 s`; `Pull == false`; `PhaseChanged == null` (a 304 is silent). In `Idle` mode the base is 60 s. |
| AC-4 (U) | Same | `Apply(Changed(etag: "W/\"abc\"", remaining: 4999))` | `State.ETag == "W/\"abc\""`, `Pull == true`, `RateLimitRemaining == 4999`, next due = interval as AC-3. A following `Unchanged` keeps the ETag. |
| AC-5 (U) | Same, at `T` | `Apply(Throttled(resetUtc: T+7 min, retryAfter: null))` / `Apply(Unchanged(remaining: 99))` / `Apply(Throttled(reset: null, retryAfter: 30 s))` / `Apply(Throttled(reset: T+10 s, retryAfter: null))` / `Apply(Throttled(reset: null, retryAfter: 300 s))` | `Phase == Throttled`, `BackoffUntilUtc` = `T+7 min` / `T+120 s` / `T+120 s` (floor) / `T+120 s` (never shorter than 120 s) / `T+300 s` (`retry-after` longer than the floor wins); `NextDueUtc == BackoffUntilUtc` (+ jitter); `PhaseChanged == Throttled` on the first transition only, `null` on a repeated throttle. After `BackoffUntilUtc`, an `Unchanged` returns to `Polling` with `PhaseChanged == Polling`. `Apply(Unchanged(remaining: 4000, minimumInterval: 30 s))` in `Active` mode → next due `now + 30 s + jitter` (`x-poll-interval` honoured, decided ex-OQ6). |
| AC-6 (U) | Same | `Apply(Unreachable)` ×7 | `Phase == NetworkBackoff`, delays 10, 20, 40, 80, 160, 300, 300 s (capped at 5 min); `ConsecutiveFailures` counts; `PhaseChanged` only on the first failure; the first `Unchanged`/`Changed` resets to `Polling` with `ConsecutiveFailures == 0` and `PhaseChanged == Polling`. |
| AC-7 (U) | A `PollState` in any phase | Any `Apply` | Pure: the input state is unchanged (records), no I/O, no clock read (the `now` parameter is the only time source); `jitterUnit` outside [−1, 1] → `ArgumentOutOfRangeException`. |
| AC-8 (U) | `GitHubBusProvider` over a mocked `HttpMessageHandler`, `BusRemote(owner "acme-org", repo "acme-bus", branch "main")`, an `InMemorySecretStore` holding `github-pat` for `acme` | `ProbeHeadAsync(etag: "W/\"old\"")` | One request: `GET https://api.github.com/repos/acme-org/acme-bus/commits?sha=main&per_page=1`; headers `Authorization: Bearer <the stored token>`, `If-None-Match: W/"old"`, `User-Agent: zyggy/<informational version>`, `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`; no `If-None-Match` when `etag` is null. |
| AC-9 (U) | Same | Handler answers `304` (headers `x-ratelimit-remaining: 4321`) / `200` with `ETag: W/"new"` and `x-ratelimit-remaining: 4000` / `403` with `x-ratelimit-remaining: 0`, `x-ratelimit-reset: <epoch>` / `429` with `retry-after: 60` / `401` / `500` / `HttpRequestException` / `TaskCanceledException` from the handler (timeout) | `Unchanged(remaining 4321)` / `Changed("W/\"new\"", 4000)` / `Throttled(reset = epoch as `DateTimeOffset`, retryAfter null)` / `Throttled(reset null, retryAfter 60 s)` / `Unreachable(detail "unauthorized")` / `Unreachable("http 500")` / `Unreachable(…)` / `Unreachable(…)`; a `304` or `200` carrying `x-poll-interval: 30` sets `MinimumInterval == 30 s`; the method never throws; no `Detail` contains the token or any header value other than status codes and the rate-limit numbers. |
| AC-10 (U) | Same, store lacks `github-pat` | `ProbeHeadAsync` | `Unreachable("no github-pat for tenant acme")`; no HTTP request was sent. |
| AC-11 (U) | `GitHubBusProvider` constructed through `AddGitHubBus()` | Inspect the handler it builds | `SocketsHttpHandler.UseProxy == true` (default), `DefaultProxyCredentials == CredentialCache.DefaultCredentials`, `AllowAutoRedirect == false`, timeout 30 s; the provider reads the PAT from `ISecretStore` on every request (two requests → two `GetAsync` calls on a substitute), never from options or environment. |
| AC-12 (U) | `GitClient` over `RecordingProcessRunner`, `GitClientOptions.Identity = ("zyggy (home-laptop)", "home-laptop@zyggy.org")` | `MoveAsync`, `PullRebaseAsync("main")`, `StashPushAsync(message)`, `CommitOnlyAsync`, `LsRemoteAsync(url, "main", env)`, `PushDryRunAsync(url, refspec)`, `ConfigGetAsync("http.proxy")` | Argument vectors exactly: `mv -- <from> <to>`; `pull --rebase origin main`; `stash push --include-untracked -m <message>`; `-c user.name=zyggy (home-laptop) -c user.email=home-laptop@zyggy.org commit --only -F - -- <paths>`; `ls-remote --exit-code <url> refs/heads/main` with the given env additions and `GIT_TERMINAL_PROMPT=0`; `push --dry-run <url> <refspec>`; `config --get http.proxy`. Every spec has `FileName == "git"`, no shell. Without `Identity`, `commit` carries no `-c user.*` (28's call sites unchanged). |
| AC-13 (U) | `BusRepository` over `RecordingProcessRunner` with a scripted `push` that fails twice with `! [rejected] … (fetch first)` then succeeds; fake clock; a jitter source returning 0.5 | `ClaimAsync` | Command order: `pull --rebase origin main`, `mv`, `commit`, `push`, `pull --rebase`, `push`, `pull --rebase`, `push`; the two delays are 2 s each (`1 s + 0.5 × 2 s`); outcome `Pushed(attempts: 3)`. With a jitter source returning 0 and 1 the delays are 1 s and 3 s. |
| AC-14 (U) | Same, `push` fails four times | `ClaimAsync` | Exactly three pushes after the first (four in total), then outcome `Deferred(attempts: 4)` — no fifth push, no `reset`; the recorded `ILogger` received exactly one `Error` entry for the deferral (decided, ex-OQ9). |
| AC-15 (U) | `BusRepository.EnsureCleanAsync` with scripted `status --porcelain` output non-empty / empty | Start-up | Non-empty: `stash push --include-untracked -m zyggy-home-laptop-<ISO timestamp>` is run and one `Warning` is logged; empty: no stash, no log. Also: `symbolic-ref` ≠ configured branch → `BusStartupResult.WrongBranch`, nothing stashed. |
| AC-16 (U) | Write-rule guard | `BusRepository` asked to write to `nodes/<other>/reports/`, to another tenant's prefix, or to `archive/` | `InvalidOperationException` ("write rule") — a caller bug, never a protocol outcome; `SubmitAsync` only ever writes `nodes/<to>/jobs/`, `ClaimAsync`/`ReportAsync`/`RejectAsync` only inside `nodes/<me>/`. |
| AC-17 (I) | `BusRepoFixture(tenant: "acme")` seeded by `SeedTenantAsync("acme", ["central", "home-laptop"])` through the production `BusPaths` | A signed job to `home-laptop` pushed from a second clone (the sender); `BusRepository` for `home-laptop` pulls, verifies, claims | On the bare repo's `main`: the file is at `tenants/acme/nodes/home-laptop/jobs/claimed/<id>.md`, byte-identical, absent from `jobs/`; last commit subject `acme claim <id> central→home-laptop` (decided, ex-OQ9); one commit for the move. |
| AC-18 (I) | AC-17's claimed job | `ReportAsync(status done, fixed body, signWith acme/1)` | On `main`: `tenants/acme/nodes/home-laptop/reports/<new ulid>.md` exists, parses and verifies with `acme/1`, `in_reply_to == <job id>`, `to == central`, `from == home-laptop`, `status: done`, `started`/`finished`/`duration_seconds` present; the claimed job is gone (`git rm`); one commit `acme report <new ulid> home-laptop→central`. |
| AC-19 (I) | Four pushed jobs addressed to `home-laptop`: (a) one byte of the body flipped after signing, (b) `tenant: globex` + `key_id: globex/1`, (c) `to: central` (file sits in `home-laptop/jobs/`), (d) `schema: 2` | `ProcessOnceAsync` | Each ends at `jobs/rejected/<id>.md` on `main` with a `reports/<ulid>.md` `status: rejected`, `reason` = `invalid_signature` / `tenant_mismatch` / `wrong_recipient` / `schema_unsupported` (decided, ex-OQ1; `EnvelopeWire.ToWire(EnvelopeRejectionReason.WrongRecipient) == "wrong_recipient"`), `in_reply_to` = the file-name ULID, `to` = the job's `from` (decided, ex-OQ10); for (b) the `ISecretStore` substitute received no `GetAsync` for tenant `globex`; the reports are signed by `home-laptop`; nothing was ever passed to the job handler (a substitute asserts zero calls); one `Warning` log per rejection. |
| AC-20 (I) | A file in `jobs/` that is not an envelope (no front matter) | `ProcessOnceAsync` | Moved to `jobs/rejected/`, **no** report (header unreadable — decided, ex-OQ10), one `Warning` naming the file name and `malformed`. A second file with valid front matter but `from: Not-A-Label` and a bad signature → moved, no report, `Warning` `invalid_signature` (the peeked `from` is invalid). |
| AC-21 (I) | The bare repo advanced by the sender clone between the SUT's `pull --rebase` and its `push` (hook through an `IProcessRunner` decorator) | `ClaimAsync` | Push rejected once, retried after `pull --rebase`, pushed on the second attempt; both commits are on `main`; the SUT's commit sits on top of the foreign one. |
| AC-22 (I) | The decorator makes every `push` fail | `ClaimAsync` | After four attempts: `Deferred`; the clone has exactly one unpushed commit (`rev-list origin/main..HEAD` = 1), the working tree is clean (`status --porcelain` empty), the bare repo is unchanged, one `Error` logged. A following `ProcessOnceAsync` with pushes working pushes that commit without a new claim and logs `Information` "pushed 1 deferred commit" (decided, ex-OQ9). |
| AC-23 (I) | `BusRepoFixture` seeded with `tenants/acme/…` **and** `tenants/globex/nodes/home-laptop/jobs/<signed job>.md` | A `BusRepository` for `acme`/`home-laptop` lists jobs | Only `acme` jobs are listed; the `globex` file is untouched on `main` after processing; no log line mentions it (ignored, never processed — §4 write rules; no refusal — decided, ex-OQ4). |
| AC-24 (I) | The `IBusProvider` contract suite (abstract test class, one `[Fact]` per contract rule) | Run against `GitHubBusProvider` (bare repo as remote, head check through a mocked handler that answers from the bare repo's `rev-parse main` as the ETag) **and** against the test double `LocalBareRepoBusProvider` | Both pass every rule: `ProbeHeadAsync(null)` → `Changed`; a second call with the returned ETag → `Unchanged`; after a foreign push → `Changed` with a new ETag; `PullAsync` brings the new commit into the clone; `PushAsync` of a local commit lands on `main`; `PushAsync` with the remote ahead → `Rejected` (not an exception); any failure is a result, never a thrown exception. |
| AC-25 (I) | `BusListener` with the probe handler over the bare repo, a `FakeTimeProvider`-driven loop | A job is pushed by the sender; the listener runs one tick | The listener probes the head, pulls, claims, reports, pushes; `Changed` then `Unchanged` ticks; log lines: one `Information` per state change (`claim`, `report`), none for the `Unchanged` tick; every log scope carries `tenant`, `machine`, `phase`. |
| AC-26 (I) | `ZyggyCli` out-of-process harness, a `node.json` pointing at the fixture clone, `FileSecretStore` root in a temp dir seeded with `acme/1` | `zyggy probe send --to home-laptop --timeout 5` with no listener | Exit 6; the job is on `main` in `tenants/acme/nodes/home-laptop/jobs/`; the ledger line has `outcome: timeout`; stdout prints the id and `timeout`. With a listener process (`zyggy probe listen` started by the test against the same bare repo, second `node.json` for `home-laptop`): exit 0, ledger `outcome: done`, `roundTripMs > 0`, stdout shows the round trip; `zyggy probe stats` then prints one row for `home-laptop` with count 2, done 1, timeout 1 and p50/p95 in ms; `zyggy probe stats` on an empty ledger exits 1 with "no probes". |
| AC-27 (I) | `zyggy probe listen` with a `node.json` lacking `tenant` / with `tenant: Acme` / with a `bus.localPath` that is not a git checkout / whose checkout is on branch `dev` | Start | Exit 3 with one stderr line naming the key or condition (`tenant missing`, `tenant invalid`, `bus.localPath is not a git repository`, `checkout is on 'dev', expected 'main'`); nothing is stashed, nothing pulled. |
| AC-28 (I) | `zyggy probe reach` with the fixture's bare repo as `bus.remote` (file URL) and a handler-less run (the API check hits a loopback `HttpListener` that answers 304 / connection refused) | Run | Prints a four-row Markdown table (`api`, `fetch`, `push`, `ssh`) with columns `check`, `endpoint`, `result`, `detail`, `proxy`; `fetch` and `push` are `ok` (push via `--dry-run` left the bare repo unchanged — `rev-parse main` identical before and after); `api` is `ok` / `unreachable (connection refused)`; `ssh` is `skipped (no ssh remote configured)` or a result — never affects the exit code; exit 0 when `api`, `fetch` and `push` are `ok`, else 6. The `proxy` column shows host:port only (userinfo stripped) or `none`. |
| AC-29 (U+I) | Every `ProcessSpec` recorded in AC-12–AC-28, every log entry, every exception message, every stdout/stderr line of the CLI tests | Searched for the test PAT bytes and the test HMAC key bytes (hex and raw) | Zero occurrences. The PAT is passed only as the `Authorization` header of `HttpClient`; git never receives it from Zyggy (no `-c credential.*`, no `http.extraHeader`, no URL userinfo, no `GIT_ASKPASS` set by Zyggy). |
| AC-30 (U) | `NodeConfigurationReader` | `Read` of the documented minimal file / a file with extra unknown keys (07's) / missing file / invalid JSON / `signWith: "globex/1"` with `tenant: acme` / `bus.branch` absent | Typed `NodeConfiguration` with `Tenant`, `Machine`, `SignWith`, `Bus{Owner, Repo, Branch, LocalPath, Remote}`, `Poll{Active, Idle}`, `Secrets.Root?` / same, unknown keys ignored / `FileMissing(path)` / `Malformed(detail)` / `Invalid("signWith", "tenant differs")` / `Branch == "main"` default. No secret-looking key is accepted: a `pat`, `token` or `hmac` key anywhere → `Invalid(<key>, "secrets never in node.json")`. |
| AC-31 (U) | `zyggy secret set hmac/1` (decided, ex-OQ2) with stdin `<hex or raw bytes>` | Run with a temp `FileSecretStore` root | The file `<root>/acme/hmac/1` holds the hex of the bytes read from stdin (raw input hex-encoded; `--hex` input decoded then re-encoded), permissions per 03 AC-27; nothing echoed to stdout except `stored acme/hmac/1 (<n> bytes)`; the value never appears in argv (a `zyggy secret set hmac/1 <value>` form does not exist → exit 2). |
| AC-32 (I) | `AddGitHubBus(...)` + `AddFileSecretStore` + `AddEnvelopeSigning` + `AddProcessRunner` on a `ServiceCollection` | Build provider | `IBusProvider` resolves to `GitHubBusProvider` (singleton); `BusRepository`, `BusListener`, `GitClient` resolve; `AddGitHubBus` without an `ISecretStore` → the DI's `InvalidOperationException` on resolve (no hidden default). Existing dream/offload registrations and their tests stay green (additive change to `GitClient`). |
| AC-33 (O) | All three machines, `zyggy probe reach`, on a working day, the work laptop inside the corporate network | Owner runs and pastes the tables | The 3 × 4 matrix is appended to 0001's transport section with timestamps; each cell `ok` / `auth failed` / `unreachable (<class>)` / `tls failed`; the four checklist rows of 0001 are ticked or marked failed per machine. |
| AC-34 (O) | Central with `zyggy probe listen` on `home-laptop` | `zyggy probe send --to home-laptop --count 10 --interval 30` | ≥ 10 probes `done`; `zyggy probe stats` output (count, done, failures, p50, p95, min, max) appended to 0001. |
| AC-35 (O) | Central with `zyggy probe listen` on `work-laptop` through the real corporate proxy, HTTPS push with the PAT via Git Credential Manager | `zyggy probe send --to work-laptop --count 10 --interval 30` | ≥ 10 probes attempted, the number `done` and the stats appended to 0001 with timestamps; any failure's error class recorded. A failing check is **not** worked around in 04: the record goes to 05 (candidate C / `bus: disabled` evaluation). |
| AC-36 (O) | The `zyggy-bus` repository | Owner follows the `add-tenant.md` steps listed in this spec | Private repository `zyggy-org/zyggy-bus`; ruleset on `main`: force-push and deletion blocked; `PROTOCOL.md` placeholder and `tenants/geoffrey/nodes/{central,home-laptop,work-laptop}/…` with `.gitkeep` files committed; three fine-grained PATs scoped to that one repository (Contents read on Central, Contents read+write on each laptop); one write deploy key on Central; no secret value in any repository or in 0001. |
| AC-37 (D) | Documents | Review | `src/Zyggy.Core/README.md` gains the `Bus/` troubleshooting entries of the Failure-modes table (dirty checkout at start-up, push deferred after four attempts, rate-limit back-off, proxy/TLS refusal, wrong branch, missing PAT/key); `src/Zyggy.Cli/README.md` gains the `probe` verbs, exit codes, the `node.json` minimal schema and the per-machine setup (key seeding, PAT, credential helper, proxy settings) — this is the `bus-conflict.md` draft and the probe runbook; `_plans/decisions/0001-transport-and-vm.md` transport section filled (AC-33–35); the founding-spec amendments under "Founding-spec amendments to apply" have been pasted by the owner (or the orchestrating session at the owner's instruction) into the founding spec, and `src/Zyggy.Core/README.md` names them where the `Bus/` code implements them. |
| AC-38 (D/CI) | Clean clone, Windows and Linux | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on `Zyggy.slnx`; publish `win-x64` and `linux-x64` single-file | All green; no new package in `Directory.Packages.props`; the published `zyggy` on Central is installed as a new pinned version under `/opt/zyggy/<version>/` per the 28/33 install path and the laptops receive the `win-x64` file from the CI artifact. |

---

## Decision Table

| Founding-spec / brief item | Verdict | Target type / library | Justification |
|----------------------------|---------|-----------------------|---------------|
| §9 seam `IBusProvider` (GitHub: poll + git remote); §14 pluggable edges | Keep (shape fixed below) | `Zyggy.Core.Bus.IBusProvider { ProbeHeadAsync, PullAsync, PushAsync, Remote }`; `GitHubBusProvider` (`internal sealed`, `AddGitHubBus`) | The seam is the §5 poller contract: the doorbell (REST, host-specific) plus pull/push of the local checkout (the remote URL and credential are the host's business). Everything else (`BusPaths`, `BusRepository`, `PollState`) stays host-agnostic so Azure Repos/GitLab need one new class. |
| Brief: second implementation = in-memory / local-bare-repo test double under a shared contract suite | Keep (bare-repo double, not in-memory) | `tests/Zyggy.Integration/Infrastructure/LocalBareRepoBusProvider` (head = `rev-parse main` of the bare dir; pull/push via `GitClient`) + abstract `BusProviderContractTests` | An in-memory double would need `BusRepository` to abstract the file system — a sixth abstraction with no product use. The bare-repo double is ~60 lines, exercises the same `GitClient`, and the contract suite keeps both honest. |
| §5 poll loop items 1–5; §9 "pure `PollState` record (etag, interval, backoffUntil), HTTP injected, fake clock" | Keep | `PollState`, `PollOptions`, `PollMode`, `PollPhase`, `HeadProbe` (closed), `PollTransition` — pure `Apply(outcome, mode, now, jitterUnit, options)` | Shared contract 07 hosts unchanged. Jitter enters as a parameter so the machine stays pure and the ±2 s rule is testable; `PhaseChanged` makes "log once per state change" a property of the machine, not of the host. |
| §5 item 3 work-hours evaluation; item 6 fifteen-minute safety fetch | Defer to 07 (brief) | `PollMode` input only | The probe runs in `Active` mode; who decides the mode is the Node service's job (needs `node.json` `workHours`, timezone). The safety fetch needs a long-running host; the probe listener is interactive. |
| §5 item 4 `403/429` or `x-ratelimit-remaining < 100` → 120 s until `x-ratelimit-reset` | Keep + Reshape (floor, `retry-after`, `x-poll-interval`) — **decided 2026-10-12 (ex-OQ6)** | `PollState.Apply(Throttled…)`: until `max(now+120 s, reset, now+retry-after)`; `HeadProbe.MinimumInterval` from `x-poll-interval` raises the next interval | GitHub documents `retry-after` for secondary limits, "wait at least one minute" when neither header is present, and `x-poll-interval` as a minimum (docs checked 2026-10-12); §5's numbers stand and are measured in P0. Recorded in 0001: `304`s do not count, all PATs of one account share 5,000/h (worst case 1,080/h). |
| §5 item 5 network error: exponential 10 s → 5 min | Keep | `PollState` doubling from `NetworkBackoffMin` to `NetworkBackoffMax` | As written. |
| §5 item 3 "one in-flight request" | Keep (structural) | `BusListener` is one sequential loop; AC-25 asserts no overlapping request on the mocked handler | A flag in `PollState` would add API to guard something the sequential loop already guarantees. |
| §5 item 1 request shape (`commits?sha=main&per_page=1`, `Authorization: Bearer`, `If-None-Match`, `User-Agent: zyggy/<version>`) | Keep + `Accept`/`X-GitHub-Api-Version` | `GitHubBusProvider` | GitHub recommends both headers (docs checked 2026-10-12); cost nil. |
| 304 responses and the primary rate limit | Keep (verified) | — | GitHub best-practices page: a conditional request that returns `304` "does not count against your primary rate limit" when authorised (checked 2026-10-12). All PATs of one account share one 5,000/h budget; worst case 3 machines × 360 polls/h = 1,080 if every poll were a `200`. Recorded in 0001 (OQ6). |
| `git ls-remote` as the doorbell instead of REST | Rejected (not specced) | — | §13 decides ETag polling; `ls-remote` costs a TLS handshake plus auth per tick and has no free `304`. |
| §5 item 7 proxy: `HttpClient` system proxy; git `http.proxy` | Keep + Reshape (proxy credentials) | `SocketsHttpHandler` with `UseProxy` default (true), `DefaultProxyCredentials = CredentialCache.DefaultCredentials`, `AllowAutoRedirect = false` | `HttpClient.DefaultProxy` reads WinHTTP/WinINet settings on Windows and `HTTPS_PROXY` on Linux (learn.microsoft.com, checked 2026-10-12); default credentials let an NTLM/Kerberos proxy authenticate the interactive user without any Zyggy configuration. Git's proxy and TLS backend are the owner's `git config`; Zyggy only *reports* them in `probe reach` and never writes `git config` — **decided 2026-10-12 (ex-OQ7)**: one documented settings pass (`http.proxy`, `http.sslBackend schannel`, `credential.helper manager`) after a failing `reach`, `reach` again, both results in 0001, a still-failing required check goes to 05. |
| §5 git operations: system `git` via `Process`, never LibGit2Sharp; `GitClient` "the only place a git command line is built" (28) | Keep (extend 28's client) | `GitClient` + `MoveAsync`, `PullRebaseAsync`, `StashPushAsync`, `LsRemoteAsync`, `PushDryRunAsync`, `ConfigGetAsync`; `GitClientOptions.Identity` | Additive; the live dream/offload paths keep their behaviour (AC-12 last clause, AC-32). |
| §5 commit identity `zyggy (<machine>) <machine>@zyggy.org` | Reshape (per-command `-c`, not repo config) | `GitClientOptions.Identity` → `-c user.name=… -c user.email=…` on `commit` | No state written into the checkout; deterministic in `RecordingProcessRunner`; the identity follows the configured `machine`. |
| §5 write sequence `pull --rebase` → change → `commit` → `push`; push rejected → `pull --rebase`, ≤ 3 retries, 1–3 s jitter (O4) | Keep | `BusRepository.WriteAsync` (private sequence) with `TimeProvider` + injected jitter source | Shared contract. |
| §5 "then leave the change uncommitted and log an error; the next poll retries" | Reshape — **decided 2026-10-12 (ex-OQ9)** | `WriteOutcome.Deferred`: the commit stays local and unpushed, tree clean; the next write's `pull --rebase` + `push` carries it; §5 and §4 row 2 amended (see "Founding-spec amendments to apply") | "Uncommitted" conflicts with the next sentence's "never left dirty across iterations" and would need bespoke resume logic; a local unpushed commit is exactly what 28 already does (`PushDeferred`, exit 7) and git rebases it for free. Nothing is lost. |
| §5 start-up `git status --porcelain` non-empty → named stash + warning | Keep | `BusRepository.EnsureCleanAsync` → `stash push --include-untracked -m zyggy-<machine>-<timestamp>` | `--include-untracked`, because a half-written envelope is untracked. |
| §5 commit message `<tenant> <type> <id> <from>→<to>` (bus-protocol skill shows it without `<tenant>`) | Keep §5 + clarify `<type>` | `acme claim <id> central→home-laptop`, `acme report <ulid> home-laptop→central`, `acme reject <id> …`, `acme job <id> central→home-laptop` | The founding spec wins over the skill text (finding forwarded). A claim commit that read `job` would be indistinguishable from the submission in `git log`; `<type>` is read as the action word — **decided 2026-10-12 (ex-OQ9)**, §5 amended. |
| §4 repository layout, `BusPaths` tenant-prefixed, `registry`, `archive`, `PROTOCOL.md` | Keep | `BusPaths` (static, `TenantId`/`MachineName`/`EnvelopeId` typed inputs, `TryParse`) | Design rule; typed inputs make a path without a tenant unexpressible. |
| `policy.yaml` path | Defer (§14, brief) | — | Signed policy is a §14 item; `IPolicySource` v1 is `node.json`. |
| §4 "refuse a checkout with a second `tenants/<org>/` directory" (04 entry, DoD) vs §4/§14 "nothing enforces this beyond `BusPaths` reading only its own prefix" | Defer (§14) — **decided 2026-10-12 (ex-OQ4)** | `BusRepository` lists only `tenants/<own>/nodes/<me>/jobs/*.md` (AC-23); no refusal logic | The founding spec moved enforcement to §14 on 2026-09-29; the roadmap entry predates that. Refusal logic is code that exists only for a second tenant (§1 non-goal). The project-manager trims the 04 DoD line and 05's gate line. |
| §4 write rules | Keep (guarded in Core) | `BusRepository` throws on a write outside its allowed folders (AC-16) | "Tenant checks in Core, not callers" (§9). |
| §4 receiver rejections: signature, tenant, `key_id` tenant, `to` ≠ own machine → `jobs/rejected/` + `status: rejected` report | Keep | `BusRepository.VerifyIntake` (03's `ParseAndVerifyAsync` with the tenant of the subtree, then `Header.To == me`), `RejectAsync` (one commit: `mv` + report) | §8 "rejected before any model call" — the handler is never invoked for a rejected file (AC-19). |
| `reason` of a `status: rejected` report (03 finding 1) | Keep (value set fixed) — **decided 2026-10-12 (ex-OQ1)** | `reason` = the `EnvelopeRejectionReason` wire name; `EnvelopeRejectionReason` gains `WrongRecipient` (`wrong_recipient`); the nine runner reasons stay for `failed`/`timeout`; §4 and §9 amended | One enum per failure family, no duplicated members, 03's wire names reused unchanged; `schema_unsupported` keeps the same string in both enums. Adding `WrongRecipient` is the protocol change public-api.md requires a decision for — this is it. |
| Addressing a rejection report when `id`/`from` cannot be read | Reshape — **decided 2026-10-12 (ex-OQ10)** | `in_reply_to` = the file-name ULID; `to` = `from` peeked leniently from the raw front matter by the internal `EnvelopeParser.PeekFrom(bytes)`; unreadable → move only, one `Warning`, no report; §4 amended | `EnvelopeResult` carries no header on rejection (03); the file name is by §4 the id the sender knows; the move itself is the audit record in `git log`. No public API change to 03. |
| §4 state machine rows 1–3 (submit, claim, report + `git rm`) | Keep | `BusRepository.SubmitAsync`, `ClaimAsync`, `ReportAsync` | Rows 4–5 (sweep, archive) are 17. |
| §4 "tenant ≠ `<org>` of the path" (03 defer) | Keep | `BusRepository` passes the tenant of the subtree it listed to `ParseAndVerifyAsync` | The only tenant it ever lists is its own, so `TenantMismatch` covers both checks. |
| Listen loop as `Zyggy.Core` code hosted by the CLI now and by the Node service in 07 | Keep | `BusListener` + `JobHandler` delegate | Delegate, not an interface: public-api.md forbids a sixth single-implementation abstraction; a delegate has two implementations by 07 (probe reply, `JobRunner` dispatch) without being an abstraction of an external system. |
| `zyggy probe send \| listen \| stats \| reach` | Keep (shapes below) | `Zyggy.Cli` System.CommandLine verbs over `Zyggy.Core.Bus.Probe` (`ProbeSender`, `ProbeLedger`, `ProbeStats`, `ReachabilityCheck`) | Permanent diagnostics verb per the roadmap ("no throwaway code"). |
| Latency measured at the sender, no cross-machine clocks (O21) | Keep | `probe send` waits for the verified report (default 120 s) and writes `sentUtc`/`observedUtc` to a local JSONL ledger; `stats` aggregates the ledger | One clock. The measured time includes the receiver's and the sender's own poll intervals; 05 interprets. |
| `probe reach` checks (1) API, (2) fetch, (3) push, (4) SSH 22 | Keep + Reshape (side-effect-free push) | API: one conditional GET; fetch: `ls-remote --exit-code <https url> refs/heads/<branch>`; push: `push --dry-run <https url> HEAD:refs/heads/<branch>`; SSH: `ls-remote <ssh url>` with `GIT_SSH_COMMAND=ssh -o BatchMode=yes -o ConnectTimeout=10` | `push --dry-run` still negotiates with `git-receive-pack`, so it proves write authentication without writing (AC-28 asserts the remote is unchanged). `ls-remote` through git honours the owner's proxy, TLS backend and credential helper, which is what the matrix must measure. |
| Minimal configuration source (`node.json` vs options) | Keep (§10 shape, minimal keys) — **decided 2026-10-12 (ex-OQ5)** | `Zyggy.Core.Configuration.NodeConfiguration`/`NodeConfigurationReader` (System.Text.Json source-generated), `--config` / `ZYGGY_NODE_JSON` / default path; §10 example amended | Four verbs × three machines × six values makes options error-prone; 07 needs the reader anyway (no throwaway code); the §10 rule "refuses to start when `tenant` is missing" applies to the probe. |
| §8 Secrets: HMAC key and `github-pat` through `ISecretStore`; P0 file store (O20) | Keep | `FileSecretStore` on all three machines; names `hmac/<n>`, `github-pat` | One store, one seeding procedure (OQ2). `CredentialFileSecretStore` is name-locked to m365/LinkedIn. |
| Seeding by hand (`openssl rand -hex 32`) vs a verb | Reshape (09's verb, first slice) — **decided 2026-10-12 (ex-OQ2)** | `zyggy secret set <name>` reading the value from stdin (`--hex` for the HMAC key) over `ISecretStore.SetAsync`; `FileSecretStore` on all three machines; `CredentialFileSecretStore` stays m365/LinkedIn-only; the bus key is a new secret | Six seeds across three machines, two of them a PAT that the owner would otherwise hex-encode by hand; one store, one procedure; 09 adds the OS stores behind the same verb and retires the Windows file store. |
| §5 credentials per machine (deploy key SSH for Central and home, PAT over HTTPS for work if 22 blocked) | Reshape (home laptop) — **decided 2026-10-12 (ex-OQ3)** | Central: write deploy key over SSH (`Host github.com-zyggy-bus`, remote `git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git`); both laptops: one fine-grained PAT each (Contents read+write, `zyggy-bus` only) used by `HttpClient` from `FileSecretStore` and by git through Git Credential Manager, remote `https://github.com/zyggy-org/zyggy-bus.git` | One Windows procedure proven twice (the home laptop rehearses the work laptop's path); Central unchanged from its existing pattern; GCM is Git for Windows' default helper with the Windows Credential Manager store (GCM docs, checked 2026-10-12). `probe reach` still measures SSH on all three so 0001 records both paths; 05 amends the §5 table from the measurement. |
| The git credential is never handled by Zyggy code | Keep (asserted) | AC-29 | §5/§8; the 32/34 askpass lesson. |
| §11 logging once per state change; `tenant`, `machine`, `phase` on every line | Keep (abstractions only) | `ILogger<T>` from `Microsoft.Extensions.Logging.Abstractions` (already referenced); the CLI wires the console logger that `Microsoft.Extensions.Hosting` already brings | Serilog and the rolling file are 07. |
| §11 health endpoint fields `lastEtag`, `rateLimitRemaining`, `backoffUntilUtc` | Keep as `PollState` properties only | `PollState.ETag`, `RateLimitRemaining`, `BackoffUntilUtc` | The endpoint is 07; exposing the values now costs nothing and avoids a 07 API change. |
| Brief: `BusRepoFixture` gains a tenant parameter (no default) and `tenants/<org>/` seeding | Keep | `BusRepoFixture(TenantId tenant)`, `SeedTenantAsync(tenant, machines)` via production `BusPaths`, `SeedSignedJobAsync(...)` from a second clone | integration-testing skill rules. |
| Brief: probe binaries installed by hand on both laptops; Central install as a pinned version | Keep (owner steps, documented) | `src/Zyggy.Cli/README.md` | No installer before 26. |
| Brief: `add-tenant.md` steps for `zyggy-bus` (ruleset, `PROTOCOL.md` placeholder, per-machine PATs, deploy key) | Keep (owner-executed, listed below) | AC-36 | §11 runbook followed once by hand; no code. |
| `Gates/P0_ProbeRoundTrip.cs`, `probe stats --export`, latency target, §1/§5 amendments from measurements | Defer to 05 (brief) | — | — |
| `claude` on laptops, `JobRunner`, services, `verify`, OS secret stores, registry, sweep/archive, Hub, policy/DLP, repository-per-tenant enforcement | Defer (brief) | — | 06, 07, 08, 09, 14, 17, 11/21, 18–20, §14. |
| `IEnvelopeSigner` example in public-api.md | Not a contract (03 finding 3) | — | Left as is. |

---

## Contracts

### Namespaces and visibility (public-api.md)

| Namespace | Public | Internal (visible to the two test projects) |
|-----------|--------|---------------------------------------------|
| `Zyggy.Core.Bus` | `IBusProvider` (seam), `BusRemote`, `HeadProbe` (+ `Unchanged`, `Changed`, `Throttled`, `Unreachable`), `BusSyncResult`, `PushResult`, `PollState`, `PollTransition`, `PollOptions`, `PollMode`, `PollPhase`, `BusPaths`, `BusFolder`, `BusLocation`, `BusIdentity`, `BusRepository`, `BusFile`, `JobIntake`, `WriteOutcome`, `BusStartupResult`, `BusListener`, `JobHandler`, `JobReply`, `ServiceCollectionExtensions.AddGitHubBus` | `GitHubBusProvider`, `RateLimitHeaders` |
| `Zyggy.Core.Bus.Probe` | `ProbeSender`, `ProbeLedger`, `ProbeRecord`, `ProbeOutcome`, `ProbeStats`, `ReachabilityCheck`, `ReachResult`, `ReachStatus` | — |
| `Zyggy.Core.Configuration` | `NodeConfiguration`, `BusSettings`, `PollSettings`, `SecretsSettings`, `NodeConfigurationReader`, `NodeConfigurationResult`, `NodeConfigurationError` | JSON source-generation context |
| `Zyggy.Core.Git` | unchanged (`GitClient` stays `internal sealed`) | `GitClient` + new members, `GitClientOptions.Identity`, `GitIdentity` |
| `Zyggy.Core.Envelope` | `EnvelopeRejectionReason.WrongRecipient` (wire `wrong_recipient`) added to the closed enum and to `EnvelopeWire` (decided, ex-OQ1); everything else unchanged | `EnvelopeParser.PeekFrom(ReadOnlyMemory<byte>) : MachineName?` — lenient read of the top-level `from` scalar, null when the file, mapping or label is unreadable (decided, ex-OQ10) |

`BusRepository` and `BusListener` are public because the Node service (07) and the CLI (08) host them; `GitClient` stays internal because no host builds git command lines; `JobHandler` is a delegate, not an interface (decided 2026-10-12, ex-OQ8). All public members XML-documented citing the §4/§5 rule they implement. No static mutable state; everything through DI.

### `IBusProvider` — the seam (hides the git host)

```csharp
public sealed record BusRemote(string Owner, string Repository, string Branch, Uri HttpsUrl, Uri? SshUrl);   // description only; never a credential

public abstract record HeadProbe { public TimeSpan? MinimumInterval { get; init; } }   // closed; MinimumInterval = GitHub's x-poll-interval when sent (ex-OQ6)
public sealed record Unchanged(int? RateLimitRemaining) : HeadProbe;              // 304 (or same marker)
public sealed record Changed(string ETag, int? RateLimitRemaining) : HeadProbe;   // 200 with a new validator
public sealed record Throttled(DateTimeOffset? ResetUtc, TimeSpan? RetryAfter, int? RateLimitRemaining) : HeadProbe;  // 403/429
public sealed record Unreachable(string Detail) : HeadProbe;                      // network, timeout, 401, 5xx, missing PAT; Detail never carries a secret

public sealed record BusSyncResult(bool Succeeded, string Detail);                // pull --rebase outcome
public abstract record PushResult;                                                // closed
public sealed record Pushed : PushResult;
public sealed record Rejected(string Detail) : PushResult;                        // non-fast-forward: the §5 retry rule applies
public sealed record PushFailed(string Detail) : PushResult;                      // auth, network, other

/// §9 seam. The only type that knows the host's REST API, URL shapes and headers. Never throws for an expected failure.
public interface IBusProvider
{
    BusRemote Remote { get; }
    Task<HeadProbe> ProbeHeadAsync(string? etag, CancellationToken ct);           // the doorbell (§5 items 1–2)
    Task<BusSyncResult> PullAsync(CancellationToken ct);                          // `pull --rebase origin <branch>` on the local checkout
    Task<PushResult> PushAsync(CancellationToken ct);                             // one `push origin HEAD:<branch>`; the retry rule is BusRepository's
}
```

`GitHubBusProvider(HttpMessageHandler, GitClient, BusRemote, ISecretStore, BusIdentity, string checkoutRoot, string version)`:

| Aspect | Contract |
|--------|----------|
| Request | `GET https://api.github.com/repos/{owner}/{repo}/commits?sha={branch}&per_page=1`; `Authorization: Bearer <pat>`; `If-None-Match: <etag>` when known; `User-Agent: zyggy/<informational version>`; `Accept: application/vnd.github+json`; `X-GitHub-Api-Version: 2022-11-28`; timeout 30 s. |
| PAT | `ISecretStore.GetAsync(tenant, SecretName "github-pat")` **on every request** (UTF-8 bytes of the token; no cache, so a rotated token is used on the next tick); absent → `Unreachable("no github-pat for tenant <t>")` and no request. Never in options, environment, argv, URL, logs, exception messages. |
| Mapping | `304` → `Unchanged`; `200` → `Changed(ETag header)` (a `200` without `ETag` → `Changed(etag: "")`, logged once); `403`/`429` → `Throttled(x-ratelimit-reset as epoch seconds, retry-after seconds, x-ratelimit-remaining)`; anything else, `HttpRequestException`, timeout → `Unreachable(status or exception type)`. `x-ratelimit-remaining` and `x-poll-interval` (→ `MinimumInterval`) parsed on every response. |
| Handler | `SocketsHttpHandler { UseProxy = true (default), DefaultProxyCredentials = CredentialCache.DefaultCredentials, AllowAutoRedirect = false }`; TLS trust = the OS store (corporate root CA on the work laptop works without configuration). Tests inject a mocked handler. |
| Pull / push | Delegated to `GitClient.PullRebaseAsync(checkoutRoot, branch)` / `PushAsync(checkoutRoot, branch)`; `Rejected` when stderr contains `[rejected]` or `non-fast-forward`/`fetch first`; `PushFailed` otherwise. The remote URL and credential are the checkout's `origin` and git's helper — the provider never sets either. |
| Registration | `AddGitHubBus(this IServiceCollection, Action<GitHubBusOptions>? configure)`; requires `ISecretStore`, `IProcessRunner`, `TimeProvider`, `NodeConfiguration` (or explicit `BusRemote`/`BusIdentity`/checkout root). |

`LocalBareRepoBusProvider` (test project only): `ProbeHeadAsync` = `rev-parse <branch>` in the bare directory compared with `etag`; pull/push via the same `GitClient`. `BusProviderContractTests` is abstract with one `[Fact]` per rule in AC-24; two sealed subclasses bind the two providers.

### `PollState` — the pure poller state machine (§5, §9)

```csharp
public enum PollMode  { Active, Idle }                      // who decides: the host (07 work hours); the probe is always Active
public enum PollPhase { Polling, Throttled, NetworkBackoff }

public sealed record PollOptions(
    TimeSpan ActiveInterval,      // 10 s
    TimeSpan IdleInterval,        // 60 s
    TimeSpan Jitter,              // ±2 s
    TimeSpan ThrottleInterval,    // 120 s floor
    int RateLimitFloor,           // 100
    TimeSpan NetworkBackoffMin,   // 10 s
    TimeSpan NetworkBackoffMax)   // 5 min
{ public static PollOptions Default { get; } }

public sealed record PollState(
    string? ETag, PollPhase Phase, DateTimeOffset NextDueUtc, DateTimeOffset? BackoffUntilUtc,
    int ConsecutiveFailures, int? RateLimitRemaining)
{
    public static PollState Initial(DateTimeOffset now);                                            // due immediately, Polling
    public PollTransition Apply(HeadProbe outcome, PollMode mode, DateTimeOffset now, double jitterUnit, PollOptions options);  // pure
}

public sealed record PollTransition(PollState State, bool Pull, PollPhase? PhaseChanged);         // PhaseChanged != null ⇒ log once
```

Transition rules (first match wins; `interval(mode)` = `max(10 s / 60 s, outcome.MinimumInterval)`; `jitter` = `jitterUnit × Jitter`, `jitterUnit ∈ [−1, 1]` from the host's `Random`):

| Outcome | New phase | `BackoffUntilUtc` | `NextDueUtc` | `Pull` | `ETag` |
|---------|-----------|-------------------|--------------|--------|--------|
| `Unchanged` with `RateLimitRemaining ≥ floor` (or null) | `Polling` | null | `now + interval + jitter` | false | kept |
| `Changed` with remaining ≥ floor | `Polling` | null | `now + interval + jitter` | **true** | new |
| `Unchanged`/`Changed` with `RateLimitRemaining < floor` | `Throttled` | `now + ThrottleInterval` | `BackoffUntilUtc + jitter` | as above | as above |
| `Throttled(reset, retryAfter)` | `Throttled` | `max(now + ThrottleInterval, reset, now + retryAfter)` | `BackoffUntilUtc + jitter` | false | kept |
| `Unreachable` | `NetworkBackoff` | `now + min(Max, Min × 2^failures)` | `BackoffUntilUtc + jitter` | false | kept |

`PhaseChanged` is set only when the phase differs from the previous state's. `ConsecutiveFailures` resets on any non-`Unreachable` outcome. One in-flight request is structural: `BusListener` awaits each probe before computing the next.

### `BusPaths` — the only place bus paths are built (§4, §9)

All results are repository-relative with `/` separators; typed inputs; no default tenant; `Resolve(checkoutRoot, relative)` joins for the file system.

| Member | Result |
|--------|--------|
| `Tenant(TenantId t)` | `tenants/<t>` |
| `Registry(t, MachineName m)` | `tenants/<t>/registry/<m>.yaml` |
| `Jobs(t, m)` | `tenants/<t>/nodes/<m>/jobs` |
| `Claimed(t, m)` | `tenants/<t>/nodes/<m>/jobs/claimed` |
| `Rejected(t, m)` | `tenants/<t>/nodes/<m>/jobs/rejected` |
| `Reports(t, m)` | `tenants/<t>/nodes/<m>/reports` |
| `Context(t, m)` | `tenants/<t>/nodes/<m>/context` |
| `Archive(t, int year, int month)` | `tenants/<t>/archive/YYYY-MM` |
| `EnvelopeFile(string folder, EnvelopeId id)` | `<folder>/<ULID>.md` |
| `Protocol` | `PROTOCOL.md` (the only file outside the prefix) |
| `TryParse(string relative, out BusLocation)` | `BusLocation(TenantId Tenant, MachineName? Machine, BusFolder Folder, EnvelopeId? Id)`; `BusFolder { Jobs, Claimed, Rejected, Reports, Context, Registry, Archive }`; false for anything else (incl. `policy.yaml`, `..`, uppercase). |

### `BusRepository` — layout and state moves (§4 rows 1–3, write rules)

```csharp
public sealed record BusIdentity(TenantId Tenant, MachineName Machine);
public sealed record BusFile(string RelativePath, EnvelopeId Id);                           // a `jobs/<ulid>.md` of mine

public abstract record JobIntake;                                                           // closed
public sealed record AcceptedJob(BusFile File, Envelope Job) : JobIntake;
public sealed record RejectedJob(BusFile File, EnvelopeRejectionReason Reason, string? Field, string Detail, MachineName? From) : JobIntake;

public abstract record WriteOutcome;                                                        // closed
public sealed record Pushed(int Attempts) : WriteOutcome;
public sealed record Deferred(int Attempts, string Detail) : WriteOutcome;                  // commit kept local and unpushed, tree clean (ex-OQ9)
public sealed record WriteFailed(string Stage, string Detail) : WriteOutcome;               // pull/mv/commit failed; tree restored

public enum BusStartupResult { Clean, Stashed, WrongBranch, NotARepository, NoTenantDirectory, RebaseInProgress }

public sealed class BusRepository(GitClient git, IBusProvider provider, BusIdentity me, string checkoutRoot,
                                  EnvelopeSigner signer, TimeProvider clock, Func<double> jitter, ILogger<BusRepository> log)
{
    Task<BusStartupResult> EnsureCleanAsync(CancellationToken ct);                          // §5 start-up rule; branch == Remote.Branch
    Task<IReadOnlyList<BusFile>> ListNewJobsAsync(CancellationToken ct);                    // tenants/<me.Tenant>/nodes/<me.Machine>/jobs/*.md only
    Task<JobIntake> VerifyIntakeAsync(BusFile file, CancellationToken ct);                  // ParseAndVerifyAsync(bytes, me.Tenant) then Header.To == me.Machine
    Task<WriteOutcome> ClaimAsync(AcceptedJob job, CancellationToken ct);                   // mv jobs/<id>.md → jobs/claimed/<id>.md
    Task<WriteOutcome> ReportAsync(AcceptedJob claimed, ReportFields report, ReadOnlyMemory<byte> body, KeyId signWith, CancellationToken ct); // add reports/<new>.md + rm claimed job
    Task<WriteOutcome> RejectAsync(RejectedJob job, KeyId signWith, CancellationToken ct);  // mv → jobs/rejected/<id>.md (+ report when From != null, ex-OQ10)
    Task<WriteOutcome> SubmitAsync(Envelope signedJob, CancellationToken ct);               // sender: add nodes/<to>/jobs/<id>.md (write rule: own tenant only)
    Task<IReadOnlyList<Envelope>> FindReportsAsync(MachineName reporter, EnvelopeId inReplyTo, CancellationToken ct); // sender side; verified with me.Tenant
}
```

**Write sequence** (every `ClaimAsync`/`ReportAsync`/`RejectAsync`/`SubmitAsync`):

1. `provider.PullAsync()` — failure → `WriteFailed("pull", …)`, nothing changed.
2. Apply the change with `git mv` / `git add` / `git rm` (one logical move, never copy + delete).
3. `commit --only -F - -- <paths>` with the machine identity; message `<tenant> <action> <id> <from>→<to>` where `<action>` ∈ `job | claim | report | reject` (`context` from 12) — decided 2026-10-12 (ex-OQ9).
4. `provider.PushAsync()`. `Pushed` → `Pushed(attempts)`. `Rejected` → wait `1 s + jitter() × 2 s` (jitter ∈ [0,1]), `PullAsync` (rebases the local commit), push again; at most 3 retries. `PushFailed` (auth/network) → no retry loop beyond the same 3 attempts.
5. Still not pushed → `Deferred` (decided 2026-10-12, ex-OQ9): the commit stays on local `HEAD`, the tree is clean, one `Error` log; the next write's step 1 rebases it and step 4 pushes it together with the new change; the listener logs `Information` "pushed N deferred commit(s)" when that happens. No `reset` of any kind.

**Intake order** (per file in `ListNewJobsAsync`): `ParseAndVerifyAsync(bytes, me.Tenant)` (03's order: malformed → tenant → schema → type → key_id → fields → sig → key → digest) → `Header.To != me.Machine` → `RejectedJob(WrongRecipient)` → else `AcceptedJob`. The handler is invoked only for `AcceptedJob` **after** `ClaimAsync` returned `Pushed` or `Deferred`.

**Rejection report** (decided 2026-10-12, ex-OQ1/ex-OQ10): `status: rejected`, `reason` = the `EnvelopeRejectionReason` wire name (`malformed`, `tenant_mismatch`, `schema_unsupported`, `key_id_tenant_mismatch`, `missing_field`, `invalid_field`, `missing_signature`, `unknown_key`, `invalid_signature`, `wrong_recipient`), `in_reply_to` = the file-name ULID, `to` = `EnvelopeParser.PeekFrom(bytes)`, `from` = me, body = one line `rejected: <reason> (<field>)`; signed with `signWith`; written in the **same commit** as the move. When `PeekFrom` returns null: move only, `Warning` log, no report.

### `GitClient` additions (internal, additive)

| Member | Command line |
|--------|--------------|
| `MoveAsync(repo, from, to)` | `mv -- <from> <to>` |
| `PullRebaseAsync(repo, branch)` | `pull --rebase origin <branch>` |
| `StashPushAsync(repo, message)` | `stash push --include-untracked -m <message>` |
| `LsRemoteAsync(repo, url, branch, environment)` | `ls-remote --exit-code <url> refs/heads/<branch>` (+ env, e.g. `GIT_SSH_COMMAND`) |
| `PushDryRunAsync(repo, url, refspec)` | `push --dry-run <url> <refspec>` |
| `ConfigGetAsync(repo, key)` | `config --get <key>` → value or null |
| `GitClientOptions.Identity : GitIdentity?` (`Name`, `Email`) | when set, every `commit` is prefixed `-c user.name=<Name> -c user.email=<Email>` |

Existing members, the index.lock retry and `GIT_TERMINAL_PROMPT=0` are unchanged. Nothing outside `GitClient` builds a git command line (28).

### `BusListener` — the loop the CLI hosts now and the Node service hosts in 07

```csharp
public delegate Task<JobReply> JobHandler(Envelope job, CancellationToken ct);              // the probe: fixed reply; 07: JobRunner
public sealed record JobReply(ReportStatus Status, string? Reason, ReadOnlyMemory<byte> Body, DateTimeOffset Started, DateTimeOffset Finished);

public sealed class BusListener(IBusProvider provider, BusRepository bus, PollOptions options, KeyId signWith,
                                TimeProvider clock, Random jitter, JobHandler handler, ILogger<BusListener> log)
{
    Task<BusStartupResult> StartAsync(CancellationToken ct);                                // EnsureClean, initial Pull, ProcessOnce
    Task<int> ProcessOnceAsync(CancellationToken ct);                                        // intake → claim → handler → report (or reject); returns files handled
    Task RunAsync(PollMode mode, CancellationToken ct);                                      // loop: wait until NextDue (clock), ProbeHead, Apply, Pull when told, ProcessOnce; ends on cancellation
    PollState State { get; }                                                                 // for health (07) and the probe's status line
}
```

Logging (§11): `ILogger` scopes `tenant`, `machine`, `phase ∈ {poll, pull, claim, report, push, reject}`; `Information` on each state change (`PhaseChanged`, claim, report, reject, deferred push recovered); `Warning` on stash, rejection, deferred push; `Error` on `WriteFailed`; nothing on an `Unchanged` tick.

### `NodeConfiguration` — the minimum `node.json` (decided 2026-10-12, ex-OQ5)

```json
{
  "tenant": "geoffrey",
  "machine": "home-laptop",
  "signWith": "geoffrey/1",
  "bus": {
    "owner": "zyggy-org",
    "repo": "zyggy-bus",
    "branch": "main",
    "localPath": "C:/agents/bus",
    "remote": "https://github.com/zyggy-org/zyggy-bus.git"
  },
  "poll": { "activeSeconds": 10, "idleSeconds": 60 },
  "secrets": { "root": null }
}
```

| Key | Type / rule | Default |
|-----|-------------|---------|
| `tenant` | `TenantId`; **required, no default** (§10) | — |
| `machine` | `MachineName`; required | — |
| `signWith` | `KeyId`; tenant part must equal `tenant` | — (required by `listen`/`send`; not by `reach`) |
| `bus.owner`, `bus.repo` | non-empty strings (GitHub owner and repository) | — |
| `bus.branch` | non-empty | `main` |
| `bus.localPath` | absolute path of the checkout | — |
| `bus.remote` | the checkout's expected `origin` URL (HTTPS or SSH form); `reach` derives the HTTPS form from `owner`/`repo` and the SSH form `git@github.com:<owner>/<repo>.git` unless `remote` is SSH | — |
| `poll.activeSeconds`, `poll.idleSeconds` | positive integers | 10, 60 |
| `secrets.root` | `FileSecretStoreOptions.RootDirectory` | the 03 default per OS |
| anything else | ignored (07 adds `role`, `user`, `workHours`, `claude`, …) | — |
| any key named `pat`, `token`, `hmac`, `secret`, `password` (case-insensitive, any depth) | **refused** (`Invalid`) — secrets never in `node.json` (§10) | — |

File location precedence: `--config <path>` → `ZYGGY_NODE_JSON` → `<LocalApplicationData>/zyggy/node.json` (`%LOCALAPPDATA%\zyggy\node.json`; `~/.config/zyggy/node.json` on Linux — the directory Central already uses). `NodeConfigurationResult` is `Ok(NodeConfiguration)` or `Error(NodeConfigurationError { FileMissing, Malformed, MissingField, InvalidField }, string Field?, string Detail)`.

### CLI surface

Common options: `--config <path>`; exit codes follow `ExitCodes` (0 ok, 1 nothing to do, 2 usage, 3 configuration, 6 failed). Every verb loads `node.json`, builds the DI host (`AddProcessRunner`, `AddFileSecretStore(root)`, `AddEnvelopeSigning`, `AddGitHubBus`), and logs to stderr via the console logger (Information; `--verbose` → Debug).

| Verb | Behaviour | Output | Exit |
|------|-----------|--------|------|
| `zyggy probe send --to <machine> [--count N=1] [--interval <s>=30] [--timeout <s>=120]` | For each probe: build `job` (`project: probe`, `to`, `from: me`, `priority: normal`, `timeout_minutes: 2`, body `zyggy transport probe` + newline), sign with `signWith`, `SubmitAsync`; then poll the head (same `PollState`, `Active`) and pull until a report from `<machine>` with `in_reply_to == id` verifies (`ParseAndVerifyAsync`, `to == me`) or the timeout elapses; append a `ProbeRecord` to the ledger. | One line per probe: `<id> <to> done <ms> ms` / `rejected <reason>` / `timeout` / `push_failed`; a summary line. | 0 all `done`; 6 any other outcome; 3 configuration |
| `zyggy probe listen` | `BusListener.StartAsync` then `RunAsync(Active)` with the probe handler (reply `done`, body `zyggy transport probe acknowledged by <machine>` + newline) until Ctrl+C. | One stderr line per state change; a stdout line per handled file. | 0 on cancellation; 3 configuration; 6 `StartAsync` ≠ `Clean`/`Stashed` |
| `zyggy probe stats [--to <machine>]` | Aggregate the ledger per target machine: count, done, rejected, timeout, push_failed, p50, p95, min, max (ms, nearest-rank percentiles over `done` records). | Markdown table (pasted into 0001). | 0; 1 when the ledger is empty |
| `zyggy probe reach` | The four checks below against `bus.owner`/`bus.repo`; each with a 30 s process/HTTP timeout; results classified. | Markdown table `check \| endpoint \| result \| detail \| proxy`; a header line with `http.proxy`, `http.sslBackend`, `credential.helper` as git reports them (values redacted of userinfo) and the `HttpClient.DefaultProxy` for `https://api.github.com` (host:port). | 0 when `api`, `fetch`, `push` are `ok`; 6 otherwise; `ssh` never affects the exit code |
| `zyggy secret set <name>` (decided, ex-OQ2) | Reads the value from stdin (raw bytes, trailing newline stripped; `--hex` decodes hex input), `ISecretStore.SetAsync(tenant, name, bytes)`. | `stored <tenant>/<name> (<n> bytes)` | 0; 2 when a value is given as an argument; 3 configuration |

`probe reach` checks and classification (`ReachStatus { Ok, AuthFailed, Unreachable, TlsFailed, Skipped }`):

| Check | How | `Ok` | `AuthFailed` (reached) | `Unreachable` / `TlsFailed` |
|-------|-----|------|------------------------|-----------------------------|
| `api` | `GitHubBusProvider.ProbeHeadAsync(null)` | `Changed`/`Unchanged` | `Unreachable("unauthorized")` (401/403 without rate-limit headers) | any other `Unreachable` (detail: exception type / status); TLS: `AuthenticationException` inner |
| `fetch` | `ls-remote --exit-code https://github.com/<owner>/<repo>.git refs/heads/<branch>` | exit 0 | stderr `Authentication failed` / `could not read Username` (reached, no helper) | `Could not resolve` / `Failed to connect` / `Connection timed out` / proxy errors → `Unreachable`; `SSL certificate problem` / `schannel` → `TlsFailed` |
| `push` | `push --dry-run https://github.com/<owner>/<repo>.git HEAD:refs/heads/<branch>` from the checkout | exit 0 (remote unchanged) | `Authentication failed` / `403` / `Permission … denied` | as above |
| `ssh` | `ls-remote --exit-code git@github.com:<owner>/<repo>.git refs/heads/<branch>` with `GIT_SSH_COMMAND=ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new` | exit 0 | `Permission denied (publickey)` (port reached) | timeout / `Connection refused` / `Network is unreachable` → `Unreachable` (expected on the work laptop) |

Classification uses stderr patterns; the raw stderr (userinfo-redacted, first 300 chars) is always in `detail`, so a misclassified line is still evidence.

### Probe ledger

`<LocalApplicationData>/zyggy/probe/<tenant>/sent.jsonl` (override `--ledger <path>`); one JSON object per line (System.Text.Json source-generated): `{ "id", "to", "sentUtc", "observedUtc"?, "outcome": "done|rejected|timeout|push_failed", "reason"?, "roundTripMs"? }`. Append-only; `stats` reads it. Sender-side clock only (O21).

### Secret names (via `ISecretStore`, tenant-scoped, §8)

| Name | Holder | Content | Used by |
|------|--------|---------|---------|
| `hmac/1` | all three machines | 32 random bytes (hex on disk, 03) | `EnvelopeSigner` |
| `github-pat` | all three machines | UTF-8 bytes of the machine's fine-grained PAT | `GitHubBusProvider` (`HttpClient` only) |
| deploy key (Central) | `~zyggy/.ssh/zyggy_zyggy-bus_ed25519`, 0600 | — | git/ssh only, via `~zyggy/.ssh/config` `Host github.com-zyggy-bus` |
| PAT for git (laptops) | Git Credential Manager → Windows Credential Manager | — | git only; stored by the owner at the first HTTPS push prompt |

### Configuration summary

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| Poll intervals, jitter, throttle floor, back-off bounds | `PollOptions` (bound from `node.json` `poll.*` for the two intervals) | 10 s / 60 s / ±2 s / 120 s / 100 / 10 s → 5 min | `node.json` may change the two intervals only; the rest is protocol (§5), changed by spec amendment. |
| Push retries and jitter | constants in `BusRepository` | 3 retries, 1–3 s | Protocol (§5). |
| Probe timeout / count / interval | CLI options | 120 s / 1 / 30 s | Per run. |
| Probe body text, `project: probe`, `timeout_minutes: 2` | constants in `ProbeSender` | — | Fixed; owner-visible; never work data. |
| Secret store root | `node.json` `secrets.root` → `FileSecretStoreOptions.RootDirectory` | 03 default per OS | Any absolute path. |
| HTTP proxy / TLS for the API | the OS (WinHTTP, `HTTPS_PROXY`) | system | Never in Zyggy configuration. |
| Git proxy / TLS backend / credential helper | the owner's `git config` (`http.proxy`, `http.sslBackend`, `credential.helper`) | git defaults | Never written by Zyggy; reported by `probe reach`. |

---

## Behaviors & Conventions

- The receiver verifies before anything else: no handler, no claim, no log of body content before `ParseAndVerifyAsync` accepted the file and `to` matched (§8). Override: none.
- `BusRepository` lists only its own `tenants/<tenant>/nodes/<machine>/jobs/*.md` (top level; `claimed/` and `rejected/` are never re-listed in 04 — resuming claimed jobs after a crash is 07/17). Files under any other prefix are never read (AC-23). Override: none.
- Every write follows the §5 sequence and produces one commit per state move; two writers never touch the same file, so `pull --rebase` never conflicts on content. Override: none.
- A `304` is silent; a phase change, a claim, a report, a rejection, a stash, a deferred push are logged once (§11). Override: `--verbose` adds Debug lines per tick for diagnosis only.
- The probe body is a fixed string; the report body is a fixed string; neither carries machine data beyond the machine name already in the header. Override: none (work boundary, §8).
- The PAT is read at use and travels only as an HTTP header; the git credential is git's (`credential.helper` / ssh). Zyggy never writes `git config`. Override: none.
- `HttpClient` uses the system proxy with default credentials; TLS trust is the OS store. Override: the OS/environment (`HTTPS_PROXY`, WinHTTP), never `node.json`.
- The ledger is the sender's only record; `stats` never reads the bus. Override: `--ledger`.
- `probe reach` never writes to the remote (`--dry-run`, `ls-remote`, conditional `GET`).
- Tenant and machine come from `node.json`; nothing in `src/` names `geoffrey`, `zyggy-org` or `zyggy-bus` (AC-2; the README examples do).

---

## Failure modes

| Situation | Observable outcome | Runbook entry (README Troubleshooting until 10) |
|-----------|--------------------|--------------------------------------------------|
| Dirty checkout at start-up | `BusStartupResult.Stashed`; stash `zyggy-<machine>-<timestamp>`; one `Warning` | Core README "Bus: dirty working copy" — `git stash list`, inspect, drop or pop; this is the `bus-conflict.md` draft (§11) |
| Checkout on the wrong branch / not a repository / no `tenants/<tenant>/` directory | `WrongBranch` / `NotARepository` / `NoTenantDirectory`; CLI exit 3 with the condition | Core README "Bus: checkout" — clone `bus.remote` to `bus.localPath`, `git switch main`; `NoTenantDirectory` → run `add-tenant.md` seeding |
| Push rejected (foreign commit) | Retry per §5; `Information` "push retried (n)" | none needed (normal) |
| Push still rejected/failing after four attempts | `Deferred` (ex-OQ9): commit local and unpushed, tree clean; one `Error`; `probe send` records `push_failed`; the next write carries the commit | Core README "Bus: push deferred" — check `git log origin/main..HEAD`, `probe reach` for the `push` cell, credential helper state |
| `pull --rebase` fails (network, auth, conflict) | `WriteFailed("pull")`; one `Error`; nothing changed; next tick retries | Core README "Bus: pull failed" — conflict is impossible by the write rules; if `rebase-merge` exists, `git rebase --abort` and report the file pair |
| API `403/429` or `x-ratelimit-remaining < 100` | `Throttled`; one `Warning` with `backoffUntilUtc`; polling resumes after | Core README "Bus: rate limit" — all PATs of one account share 5,000/h; check for a runaway poller (`User-Agent` in GitHub's rate-limit response) |
| API unreachable (DNS, proxy refusal, TLS, timeout, 5xx) | `NetworkBackoff` 10 s → 5 min; one `Warning` on entering, one `Information` on recovery; `probe reach` `api` cell `unreachable (<class>)` | Cli README "Probe: proxy and TLS" — `HTTPS_PROXY`/WinHTTP settings, corporate root CA in the OS store, `probe reach --verbose` detail |
| API 401 | `Unreachable("unauthorized")` → `NetworkBackoff`; `reach` `api` = `auth failed` | Cli README "Probe: PAT" — token expired/revoked or wrong repository scope; `zyggy secret set github-pat` |
| `github-pat` absent from the store | `Unreachable("no github-pat…")`; no request; `reach` `api` = `auth failed (no token)` | same |
| `hmac/<n>` absent | `SignAsync` → `UnknownKey`; `listen` cannot report → exit 6 at start-up (key checked in `StartAsync`); `send` exit 3 | Cli README "Probe: keys" — seed with `zyggy secret set hmac/1` (same bytes on every machine) |
| Git fetch/push over HTTPS fails on the work laptop (proxy, TLS inspection, helper) | `reach` `fetch`/`push` cells `unreachable`/`tls failed`/`auth failed` with git's stderr | Cli README "Probe: git through the proxy" — `git config --global http.proxy <proxy>`, `http.sslBackend schannel` (Git for Windows), GCM first-use sign-in; one documented settings pass, `reach` again, then the result goes to 05 (ex-OQ7) |
| SSH 22 blocked | `reach` `ssh` = `unreachable (timeout)`; exit code unaffected | recorded in 0001; expected on the work laptop (O19) |
| Rejected envelope (any `EnvelopeRejectionReason`, incl. `WrongRecipient`) | `jobs/rejected/<id>.md` + `status: rejected` report with the rejection's wire name as `reason` (ex-OQ1); one `Warning` with id and reason, never the body | Core README "Bus: rejected envelopes" — compare keys across machines (`invalid_signature`/`unknown_key`), check `node.json` `tenant`/`machine` (`tenant_mismatch`/`wrong_recipient`), upgrade the binary (`schema_unsupported`) |
| Non-envelope file in `jobs/` (or `from` unreadable) | moved to `rejected/`, no report (ex-OQ10); one `Warning` with the file name | same |
| Report never observed by the sender | ledger `timeout`; `send` exit 6 | Cli README "Probe: no report" — is `listen` running on the target, what does its stderr say, `reach` on the target |
| `node.json` missing/invalid | exit 3, one stderr line naming the key | Cli README "node.json" — the minimal schema above |
| Secret-looking key in `node.json` | exit 3 `secrets never in node.json` | same |
| Rebase or merge in progress left by a crash | `EnsureCleanAsync` detects it through `OperationInProgressAsync` → `BusStartupResult.RebaseInProgress`; nothing stashed; CLI exit 6 | Core README "Bus: pull failed" (`git rebase --abort`, then restart) |

---

## Dependencies

No new NuGet package. Verified in `Directory.Packages.props` and the two csproj files on 2026-10-12.

| Package | Status | Why |
|---------|--------|-----|
| `Microsoft.Extensions.Logging.Abstractions` 10.0.12 (MIT) | already in `Zyggy.Core` | `ILogger<T>` in `BusRepository`/`BusListener`/`GitHubBusProvider` |
| `Microsoft.Extensions.Hosting` 10.0.12 (MIT) | already in `Zyggy.Cli` | brings `Microsoft.Extensions.Logging.Console` for the verbs' stderr logging and `ServiceCollection` |
| `System.CommandLine` 2.0.11 (MIT) | already in `Zyggy.Cli` | `probe` and `secret` verbs |
| `YamlDotNet`, `Ulid`, `Options`, `DI.Abstractions` | already in `Zyggy.Core` | envelope layer (03) |
| BCL | — | `HttpClient`/`SocketsHttpHandler` (proxy, TLS), `System.Text.Json` source generation (`node.json`, ledger), `TimeProvider`, `Random` |

Considered and not added: `Microsoft.Extensions.Http` (typed clients) — one handler constructed in `AddGitHubBus` is smaller; `Octokit` — one conditional GET does not justify a client library and its headers/proxy behaviour would be outside Zyggy's control; `Polly` — the back-off is the §5 state machine and must stay pure and fake-clock testable.

---

## Deliberate deviations from the founding spec and the brief (all decided by the owner on 2026-10-12)

- **Push-failure semantics**: a local unpushed commit instead of "leave the change uncommitted" (ex-OQ9; §5 and §4 row 2 amended).
- **Commit message `<type>`** read as an action word (`job`, `claim`, `report`, `reject`, `context`) rather than the envelope type (ex-OQ9; §5 amended).
- **Rejection reports carry the §4 rejection reason** and `EnvelopeRejectionReason` gains `WrongRecipient` (ex-OQ1; §4 and §9 amended).
- **A rejection whose `from` cannot be read gets no report**, only the move (ex-OQ10; §4 amended).
- **Home laptop pushes over HTTPS with the PAT via Git Credential Manager**, not with a deploy key over SSH (§5 table) — one Windows procedure; `probe reach` still measures SSH and 05 amends the table from the measurement (ex-OQ3).
- **No refusal of a second tenant directory** (04 roadmap entry/DoD) — §4/§14 already moved it (ex-OQ4; the project-manager trims the roadmap lines).
- **`zyggy secret set`** pulled forward from 09 as a thin slice (ex-OQ2).
- **`node.json` read by `Zyggy.Core` now**, minimal keys, `signWith` and `secrets.root` added to the §10 example (ex-OQ5; §10 amended).
- **Rate-limit floor, `retry-after` and `x-poll-interval`** added to §5 item 4 (ex-OQ6; §5 amended).
- **Zyggy never writes `git config`**; proxy and TLS for git are the owner's settings pass (ex-OQ7).
- Runbook entries as README Troubleshooting sections (as 01 and 03) until the runbook directory exists (10).

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| `200` on the head probe caused by a commit to another machine's folder | `Changed` → pull → `ListNewJobsAsync` empty → no work, no log (§5 item 2). |
| ETag `W/"…"` weak validator | Stored and sent back verbatim; never normalised. |
| The job file name ULID ≠ front-matter `id` | 03 verifies the signature over the front matter; the move keeps the file name; the report's `in_reply_to` is the file name (what the sender knows). Logged as `Warning` "id differs from file name". |
| Two jobs for me in one pull | Processed in file-name (ULID = time) order; each its own claim and report commits. |
| A job for me whose `from` is my own machine name | Accepted (self-addressed jobs are how Central's own node works later). |
| Listener started while a job is already in `jobs/` | `StartAsync` processes it before the first probe. |
| `Ctrl+C` during a write sequence | The `CancellationToken` kills the git child; `EnsureCleanAsync` at the next start stashes any half-applied move (AC-15). |
| Clock skew between machines | Irrelevant: latency is sender-side; `created` is informational. |
| The sender's own poll interval | Adds up to 10 s to the observed round trip; `stats` documents it; 05 interprets. |
| `probe send --count 10` with one `timeout` | Continues; summary shows 9 done, 1 timeout; exit 6. |
| `x-ratelimit-remaining` header missing on `304` | `Unchanged(null)`; no throttle. |
| `retry-after` as an HTTP date | Parsed via `RetryConditionHeaderValue`; `Date` form converted to a delay. |
| Proxy requires authentication, interactive user logged in | `DefaultProxyCredentials` answers NTLM/Kerberos; no prompt. A proxy needing an explicit username/password is `unreachable (407)` → runbook (`HTTPS_PROXY` with userinfo is the owner's call; never `node.json`). |
| Git for Windows with OpenSSL backend behind TLS inspection | `fetch`/`push` cells `tls failed`; runbook `http.sslBackend schannel`. |
| `bus.remote` is SSH on a laptop | `reach` still runs `fetch`/`push` over the derived HTTPS URL and `ssh` over the configured one; `listen`/`send` use whatever `origin` is. |
| A second tenant's directory in the checkout | Ignored (AC-23). |

---

## Out of Scope

- `claude` on any laptop; any product job; `JobRunner`, `IModelRunner` dispatch (06). Services/units on laptops; Central's probe as a unit (10).
- `node.json` beyond the minimal keys; `role`, `user`, `workHours`, `claude`, `devRoots`, `dlp`; work-hours evaluation; the 15-minute safety fetch; `/health`; Serilog (07).
- `zyggy submit`, `status`, `verify`; `--costs` (08, 25). The full `zyggy secret` surface, OS secret stores, `rotate-hmac.md` execution, retiring the Windows file store (09).
- Registry/discovery (14); sweep, `attempt` re-signing, archive moves, resuming `claimed/` after a crash (17); Hub, `hub --proxy` (11, 21); `zyggy-core` skills and the verbatim `PROTOCOL.md` (15); policy, DLP (18–20).
- Repository-per-tenant enforcement (§14; decided ex-OQ4); a second product transport; WebSocket/MQTT; candidate C (05 evaluates); the latency target; `probe stats --export`; `Gates/P0_ProbeRoundTrip.cs`; §1/§5 amendments from the measurements (05).
- Any change to the §4 canonical form or envelope fields (03's golden files stay as they are).

---

## Risk Areas

- **§4/§5 shared contracts** — `BusPaths`, the commit message, the state moves, the write sequence and `PollState` are the protocol 07, 08, 12, 15 and 17 build on; after 04 ships, any change is a `PROTOCOL.md` change. The decisions ex-OQ1, ex-OQ9 and ex-OQ10 shape them and are founding-spec amendments A1–A5.
- **Secrets on three machines** — three repo-scoped PATs, three copies of `hmac/1`, one deploy key; asserted absent from argv/env/git config/logs/output (AC-29); fixtures hold only 03's synthetic keys.
- **Work-laptop trust boundary (§8)** — first Zyggy code on the employer's machine: fixed body, no `claude`, GitHub endpoints only; the proxy may inspect metadata; a failing check is recorded, not worked around (AC-35).
- **Corporate proxy honoured by both clients** — the product's `UseProxy = false` pattern must not leak (AC-2, AC-11); git's settings are the owner's and are only reported.
- **GitHub rate limit** — one account's 5,000/h shared by all PATs; conditional `304`s free; back-off correctness (AC-5, AC-9).
- **Measurement bias** — a quiet day is not evidence; AC-35 requires a working day inside the corporate network.
- **New public API and an additive `GitClient` change** used by the live dream/brief/offload paths on Central — AC-12 (no identity → unchanged `commit`), AC-32 (existing registrations green).
- **A repository and credentials created by hand** (`add-tenant.md` steps) — the owner performs them; this spec lists them (AC-36); no secret value in any file.

### `add-tenant.md` steps for v1 (owner-executed, AC-36; the §11 runbook's first execution)

1. Create the private repository `zyggy-org/zyggy-bus`; ruleset on `main`: block force pushes and deletion; no required reviews (every machine pushes directly).
2. Commit `PROTOCOL.md` (placeholder: "mirrors §4 of the technical specification; verbatim copy lands in deliverable 15") and `tenants/geoffrey/nodes/{central,home-laptop,work-laptop}/{jobs,jobs/claimed,jobs/rejected,reports,context}/.gitkeep`, `tenants/geoffrey/registry/.gitkeep`, `tenants/geoffrey/archive/.gitkeep`.
3. Fine-grained PATs, resource owner `zyggy-org` (the organisation must allow fine-grained PATs for its repositories), repository access **only** `zyggy-bus`: `central-bus` (Contents: read), `home-laptop-bus` (Contents: read and write), `work-laptop-bus` (Contents: read and write); 90-day expiry noted in 0001. Each stored with `zyggy secret set github-pat` on its machine (and, on the laptops, into Git Credential Manager at the first HTTPS push prompt).
4. Deploy key on Central: `ssh-keygen -t ed25519 -f ~zyggy/.ssh/zyggy_zyggy-bus_ed25519 -N ''`, add to `zyggy-bus` with write access, `Host github.com-zyggy-bus` block in `~zyggy/.ssh/config` (as for the memory repository), `origin` = `git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git`.
5. HMAC key: `openssl rand -hex 32` once; `zyggy secret set hmac/1 --hex` on each of the three machines with the same value (transferred over a channel the owner trusts, never through a repository or chat log).
6. `node.json` on each machine (minimal schema above); clone `zyggy-bus` to `bus.localPath`; `zyggy probe reach` on each machine; results into 0001.

---

## Decisions taken at the spec gate (2026-10-12, owner: "approved")

| Ex-OQ | Decision |
|-------|----------|
| OQ1 | A `status: rejected` report's `reason` is the §4 rejection reason (03's `EnvelopeRejectionReason` wire names); `EnvelopeRejectionReason` gains `WrongRecipient` (`wrong_recipient`); the nine runner reasons stay for `failed`/`timeout`. |
| OQ2 | `FileSecretStore` on all three machines (03's default roots; `secrets.root` overrides) plus `zyggy secret set <name>` reading the value from stdin (`--hex` for the HMAC key). `CredentialFileSecretStore` stays m365/LinkedIn-only. The bus key is a new secret. |
| OQ3 | Central: write deploy key over SSH (`Host github.com-zyggy-bus`). Both laptops: one fine-grained PAT each (Contents read+write, `zyggy-bus` only) used by `HttpClient` from the store and by git through Git Credential Manager. `probe reach` measures SSH everywhere; 05 amends the §5 table. Zyggy never carries the PAT to git. |
| OQ4 | Read only the own `tenants/<tenant>/` prefix, ignore the rest; no second-tenant refusal (§14). The project-manager trims the 04 DoD line and 05's gate line. |
| OQ5 | Minimal `node.json` read by `Zyggy.Core.Configuration` (`tenant`, `machine`, `signWith`, `bus.*`, `poll.*`, `secrets.root`), located by `--config` → `ZYGGY_NODE_JSON` → `<LocalApplicationData>/zyggy/node.json`; unknown keys ignored; secret-looking keys refused; no `tenant` → refuse to start. |
| OQ6 | §5's 10 s / 60 s / 120 s / < 100 stand; back-off is the later of 120 s, `x-ratelimit-reset` and `retry-after`; `x-poll-interval` honoured when sent. 0001 records that `304`s do not count and that all PATs of one account share 5,000/h. |
| OQ7 | `HttpClient` with the system proxy and `DefaultProxyCredentials = CredentialCache.DefaultCredentials`; git settings are the owner's — one documented settings pass (`http.proxy`, `http.sslBackend schannel`, `credential.helper manager`) after a failing `reach`, `reach` again, both results in 0001; a still-failing required check goes to 05. Zyggy never writes `git config`. |
| OQ8 | Public: `IBusProvider` + records, the `PollState` family, the `BusPaths` family, `BusRepository` + records, `BusListener`/`JobHandler` (delegate)/`JobReply`, `NodeConfiguration*`, `Probe*`/`Reach*`, `AddGitHubBus`. Internal: `GitHubBusProvider`, `GitClient` and its additions, `RateLimitHeaders`, `EnvelopeParser.PeekFrom`. |
| OQ9 | After the push retries fail the commit stays local and unpushed with a clean tree; the next write's `pull --rebase` + `push` carries it; one `Error` log. Commit message `<tenant> <action> <id> <from>→<to>`, `<action>` ∈ `job \| claim \| report \| reject \| context`. |
| OQ10 | Rejection report: `in_reply_to` = the file-name ULID, `to` = `from` peeked leniently from the raw front matter (internal `EnvelopeParser.PeekFrom`); unreadable → move only, one `Warning`, no report. No public API change to 03. |

---

## Founding-spec amendments to apply (exact replacement wording; only the owner — or the orchestrating session at the owner's instruction — edits the founding spec)

**A1 (ex-OQ1, ex-OQ10) — §4 Signature, third bullet.** Replace

> A receiver rejects (moves to `jobs/rejected/` with a `reports/` entry `status: rejected`) any envelope with a missing, unknown or invalid signature, with `tenant` different from its own tenant (which, because `BusPaths` only ever reads the receiver's own prefix, is also the `<org>` of the path the file sits in), with a `key_id` whose tenant part differs from `tenant`, or with `to` not equal to its own machine name. The tenant check runs before the signature check, so a key from another tenant is never even looked up (built in deliverable 03).

with

> A receiver rejects (moves to `jobs/rejected/`) any envelope with a missing, unknown or invalid signature, with `tenant` different from its own tenant (which, because `BusPaths` only ever reads the receiver's own prefix, is also the `<org>` of the path the file sits in), with a `key_id` whose tenant part differs from `tenant`, or with `to` not equal to its own machine name. The tenant check runs before the signature check, so a key from another tenant is never even looked up (built in deliverable 03). The move is accompanied, in the same commit, by a `reports/` entry `status: rejected` whose `in_reply_to` is the rejected file's name (its ULID), whose `to` is the rejected file's `from`, and whose `reason` is the rejection reason: `malformed`, `tenant_mismatch`, `schema_unsupported`, `key_id_tenant_mismatch`, `missing_field`, `invalid_field`, `missing_signature`, `unknown_key`, `invalid_signature` or `wrong_recipient` (`to` not the receiver). When the file's `from` cannot be read, the move alone records the rejection and no report is written (deliverable 04).

**A2 (ex-OQ1) — §9 Design rules, the `JobRunner` bullet.** Replace

> `JobRunner` never throws to the loop: every failure becomes a report with a `reason` code from a closed enum (`unknown_project`, `unknown_agent`, `locked`, `timeout`, `dlp_filter`, `claude_error`, `git_error`, `schema_unsupported`, `budget_exceeded`). Tenant mismatches and signature failures are rejections at parse time (§4) and never reach the runner.

with

> `JobRunner` never throws to the loop: every `failed` or `timeout` report carries a `reason` code from a closed enum (`unknown_project`, `unknown_agent`, `locked`, `timeout`, `dlp_filter`, `claude_error`, `git_error`, `schema_unsupported`, `budget_exceeded`). Tenant mismatches, signature failures and mis-addressed envelopes are rejections at parse time (§4) and never reach the runner; a `rejected` report carries one of the §4 rejection reasons instead.

**A3 (ex-OQ9) — §4 State machine, row 2.** Replace the Git-operation cell

> `git mv` + commit + push; push rejected → the §5 retry rule (pull --rebase, up to 3 attempts with jitter); still rejected → leave the job unclaimed and skip until the next poll

with

> `git mv` + commit + push; push rejected → the §5 retry rule (pull --rebase, up to 3 attempts with jitter); still rejected → the claim stays a local, unpushed commit on the node and is pushed with the node's next write (§5)

**A4 (ex-OQ9) — §5 Git operations, third bullet.** Replace

> Every write sequence is: `pull --rebase` → local change → `commit -m "<tenant> <type> <id> <from>→<to>"` → `push`. On push rejection: `pull --rebase`, retry at most 3 times with 1–3 s jitter, then leave the change uncommitted and log an error; the next poll retries.

with

> Every write sequence is: `pull --rebase` → local change → `commit -m "<tenant> <action> <id> <from>→<to>"` → `push`, where `<action>` is `job` (a submission), `claim`, `report`, `reject` or `context`. On push rejection: `pull --rebase`, retry at most 3 times with 1–3 s jitter, then leave the commit local and unpushed with a clean working copy and log an error once; the next write sequence rebases and pushes it together with its own change.

**A5 (ex-OQ6) — §5 Poll loop, item 4.** Replace

> On HTTP 403/429 or `x-ratelimit-remaining < 100`: back off to 120 s until the reset time in `x-ratelimit-reset`.

with

> On HTTP 403/429 or `x-ratelimit-remaining < 100`: back off until the later of 120 s from now, the reset time in `x-ratelimit-reset` and `retry-after`. When a response carries `x-poll-interval`, the next interval is at least that many seconds. Conditional requests answered `304` do not count against GitHub's primary limit; all personal access tokens of one account share that limit.

**A6 (ex-OQ5) — §10 `node.json` example.** Replace the `bus` line

> `"bus": { "remote": "git@github.com:gvandiest/agent-bus.git", "owner": "gvandiest", "repo": "agent-bus", "branch": "main", "localPath": "C:/agents/bus" },`

with

> `"signWith": "geoffrey/1",                // the key id this machine signs with (§4 rotation, §11 rotate-hmac.md)`
> `"bus": { "remote": "https://github.com/zyggy-org/zyggy-bus.git", "owner": "zyggy-org", "repo": "zyggy-bus", "branch": "main", "localPath": "C:/agents/bus" },   // Central: "remote": "git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git"`

and add after the `dlp` line

> `"secrets": { "root": null }              // FileSecretStore root during P0 (§8); null = the per-OS default`

and append to the paragraph below the example: "The probe verbs of deliverable 04 read `tenant`, `machine`, `signWith`, `bus`, `poll` and `secrets`; every other key belongs to the Node service (07). A key that names a secret (`pat`, `token`, `hmac`, `secret`, `password`) is refused: secrets are never in `node.json`."

**A7 (ex-OQ3) — §5 Credentials per machine.** No 04 text: the home-laptop row ("own deploy key over SSH") is amended by the transport decision record (05) from the measured result; 04 runs the home laptop with HTTPS push via Git Credential Manager and records both paths.

## Findings forwarded (not blocking 04)

1. `.claude/skills/bus-protocol/SKILL.md` shows the commit message without `<tenant>` and the §4 "retry once" rule that O4 superseded; align with §5 as amended by A3/A4.
2. `.claude/skills/integration-testing/SKILL.md` shows `BusRepoFixture` with `Tenant = "geoffrey"`; the fixture takes the tenant as a required parameter and tests use `acme`.
3. `src/Zyggy.Core/README.md` describes `Bus/` as containing `GitClient`; the client lives in `Git/` (28). Update the folder table when `Bus/` lands.
4. 07 inherits: `PollMode` evaluation (work hours), the safety fetch, resuming `claimed/` after a crash, health fields from `BusListener.State`.
5. 05 inherits: the `304`/shared-budget facts and the per-machine auth paths recorded in 0001; `probe stats --export`; the gate scenario built from AC-17–AC-25's fixtures.
