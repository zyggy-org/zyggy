# Plan: 04 — Prove transport A through the corporate proxy — "On Central I type `zyggy probe send --to work-laptop` and a signed probe job lands in the tenant's `zyggy-bus` repository; on the laptop `zyggy probe listen` wakes on GitHub's ETag change, pulls over HTTPS, verifies the envelope before anything else, claims it, answers with a signed report and pushes through the corporate proxy with the PAT git already holds; Central sees the report and tells me how long the round trip took; `zyggy probe reach` tells me per machine which of the four GitHub paths actually works"

> **Scheduling note.** 04 starts beside 39's waiting final gate (owner exception, roadmap 2026-10-12). Steps 1–15 are code, tests and repository text built on the branch `feature/04-transport-a-proxy-proof`, left uncommitted for the owner's review unless he says otherwise at a gate (auto-memory "Owner reviews before commit"; never pushed by the agent). Steps 16–21 are owner-run or live steps with the exact commands the owner types; the agent reaches Central read-only (`az vm run-command`) and never reaches a laptop.

## Overview

After this deliverable the product carries its one bus transport, proven on the hardest network path before any product job depends on it: `Zyggy.Core.Bus` holds `BusPaths` (the only place a bus path is built, tenant-prefixed), the `IBusProvider` seam with `GitHubBusProvider` (the only type that knows GitHub: one conditional `GET` per tick through the system proxy with default proxy credentials, `pull --rebase` and `push` delegated to `GitClient`), the pure `PollState` machine (10 s / 60 s ± 2 s, 120 s rate-limit floor with `retry-after` and `x-poll-interval`, 10 s → 5 min network back-off, a `304` silent), `BusRepository` (list only my own `tenants/<t>/nodes/<me>/jobs/`, verify before anything else, `claim` by `git mv`, `report` + `git rm`, `reject` into `jobs/rejected/` with a `status: rejected` report whose `reason` is the §4 rejection wire name, the §5 write sequence with three jittered push retries and a *deferred* local commit afterwards, the start-up stash) and `BusListener` (the loop the CLI hosts now and the Node service hosts in 07). `Zyggy.Core.Configuration` reads the minimal `node.json`; `Zyggy.Cli` gains `zyggy probe send|listen|stats|reach` and `zyggy secret set`. The owner creates `zyggy-bus` per `add-tenant.md`, seeds `hmac/1` and `github-pat` on three machines, measures the 3 × 4 reachability matrix and runs ≥ 10 + ≥ 10 probes; the results fill the transport section of `_plans/decisions/0001-transport-and-vm.md` for 05 to decide on.

Implements `_specs/04-transport-a-proxy-proof.md` (approved 2026-10-12, zero Open Questions; ex-OQ1..ex-OQ10 decided; its Decision Table, Contracts and AC-1..AC-38 are binding). Founding-spec sections: §1 (work-laptop egress "verified in P0"), §4 (layout, write rules, signature rejections incl. `to`, state machine rows 1–3), §5 (poll loop 1–7, git operations, credentials per machine), §8 (secrets, work boundary, injection), §9 (naming map, `Bus/`, every design rule), §10 (`node.json`), §11 (logging once per state change, runbooks), §12, §13 (transport row), §14. **This plan never edits `_specs/00 …`**: the amendments A1–A6 are pasted by the orchestrating session with the owner's approval (Step 15 confirms them). The project-manager trims the 04 DoD line and 05's gate line (ex-OQ4) — not this plan.

**Reference patterns** (read across all layers; mirrored here):

- **Seam + fake + real, DI registration, "no hidden default"**: `src/Zyggy.Core/Secrets/*` (`ISecretStore`, `InMemorySecretStore`, `internal sealed FileSecretStore`, `AddFileSecretStore`), `src/Zyggy.Core/Envelope/ServiceCollectionExtensions.cs` (`AddEnvelopeSigning` registers no store), `src/Zyggy.Core/Processes/*` (`IProcessRunner`, `ProcessSpec`, `ProcessResult`, `AddProcessRunner` with `TryAddSingleton(TimeProvider.System)`), plan 03 Step 8 (registration tests + integration over the production DI).
- **Git through a process runner**: `src/Zyggy.Core/Git/GitClient.cs` (spec 28: the only place a git command line is built; `RunAsync` with index.lock retry; `GIT_TERMINAL_PROMPT=0`), `tests/Zyggy.Core.Tests/Git/GitClientTests.cs`, `tests/Zyggy.Core.Tests/Infrastructure/RecordingProcessRunner.cs` (`On(sub, results…)`, `Hook`, `CallsOf`), `tests/Zyggy.Core.Tests/Dream/DreamCommitterTests.cs` (write sequence over the recorder).
- **Envelope layer (03)**: `EnvelopeParser.Parse(bytes, expectedTenant)`, `EnvelopeSigner.ParseAndVerifyAsync`/`SignAsync`, `Envelope.CreateJob`/`CreateReport`, `EnvelopeWriter.Serialize`, `EnvelopeRejectionReason` + `EnvelopeWire`, `FrontMatterReader` (the lenient `PeekFrom` reuses its delimiter split); test helpers `TestKeys` (`acme/1`, `acme/2`), `Golden` (`job.md`, `report.md`), `EnvelopeText` (mutations without the code under test).
- **Closed enums with wire tests**: `tests/Zyggy.Core.Tests/Envelope/EnvelopeWireTests.cs`, `Runs/RunFailureReasonWireTests.cs`.
- **Source hygiene**: `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` (`Src_ServiceLiteral_OnlyInItsAdapter` is the model for the AC-2 facts).
- **CLI shape**: `src/Zyggy.Cli/CliApplication.cs` (root command), `Commands/DreamCommand.cs` (System.CommandLine verb; `ServiceCollection` + `AddLogging`; `ConfigurationError` → exit 3; `InformationalVersion()`), `ExitCodes.cs`, `CliEnvironment.cs`; out-of-process harness `tests/Zyggy.Integration/Infrastructure/ZyggyCli.cs` (`RunAsync`, `Start`), `Cli/ZyggyCliTests.cs`.
- **Integration harness**: `tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs` (bare + clone, isolated `GIT_CONFIG_GLOBAL`, `RunGitAsync`, `ShowAsync`, `LastCommitSubjectAsync`), `MemoryRepoFixture.cs` (`SeedAsync` pattern), `Bus/BusRepoFixtureSmokeTests.cs`, `Secrets/FileSecretStoreTests.cs` (temp root), `.claude/skills/integration-testing/SKILL.md`.
- **Mocked HTTP**: `tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs` (an `HttpMessageHandler` scripted per request — the model for `StubGitHubHandler`). **Anti-pattern to avoid**: `GraphHttp`, `LinkedInHttp`, `McpProbe` set `UseProxy = false`; the bus handler must not.
- **Owner-run / Central / 0002-style evidence steps**: `_plans/39-archive-office-formats.md` Steps 7–9 (release → `/opt/zyggy/<v>/` → pin; evidence rows dated and sourced; "any AC not met is reported at the gate, never fixed in the step"). For 04 the evidence record is `_plans/decisions/0001-transport-and-vm.md` (transport section), not 0002.

**Phase**: 04 serves P0 but does not close it — 05 closes P0 with `Gates/P0_ProbeRoundTrip.cs` (spec Decision Table: deferred to 05). There is therefore **no `Gates/P<n>_*.cs` slice** in this plan; the last 🛑 HUMAN GATE is the definition-of-done check against `ROADMAP.md` #04 (as trimmed by the project-manager for ex-OQ4) and AC-1..AC-38.

**What this plan deliberately is not** (spec Out of Scope / Defer / Rejected): no `claude` anywhere, no `JobRunner`, no service or unit on any machine (Central's probe is a foreground `runuser -u zyggy -- zyggy probe …`); no `node.json` key beyond `tenant`, `machine`, `signWith`, `bus.*`, `poll.{activeSeconds,idleSeconds}`, `secrets.root`; no work-hours evaluation, no 15-minute safety fetch, no `/health`, no Serilog; no `zyggy submit|status|verify`, no OS secret store, no `rotate-hmac.md` execution; no registry, sweep, archive, resume of `claimed/`; no second-tenant refusal (ex-OQ4); no `ls-remote` doorbell; no `Microsoft.Extensions.Http`, `Octokit`, `Polly` — **no new NuGet package**; no change to the §4 canonical form or to 03's golden files; Zyggy never writes `git config`; the latency target, `probe stats --export` and the P0 gate scenario are 05's.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — Every bus path comes from one place and a test bus has the §4 layout | 1, 2 | `BusPaths` builds and parses every tenant-prefixed path; `wrong_recipient` exists; `PeekFrom` reads `from` leniently; `BusRepoFixture(tenant)` seeds `tenants/acme/…` through the production `BusPaths` and a sender clone pushes a signed job | 🛑 after Step 2 (⚠️ §4 protocol change `WrongRecipient`) |
| B — The poller wakes on an ETag change, stays silent on a 304, backs off on rate limits and network errors, and talks to GitHub through the system proxy | 3, 4, 5 | pure `PollState` transitions on a fake clock; `GitHubBusProvider` sends the §5 request with the PAT from the store and maps every answer to a `HeadProbe`; `pull`/`push` through the real `git` against the bare repo; both providers pass the contract suite | 🛑 after Step 5 (⚠️ §5 shared contract, PAT handling, proxy) |
| C — A node claims a job addressed to it, answers with a signed report, retries a rejected push and keeps a deferred commit; anything else is rejected with a reason | 6, 7, 8 | `GitClient` additions; `BusRepository` write sequence, start-up stash, write-rule guard, intake, submit, reports lookup — first over the recorder, then on the bare repo (claim, report, foreign push, deferral, four rejections, malformed file, other tenant ignored) | 🛑 after Step 8 (⚠️ §4/§5 shared contracts: commit message, state moves, deferral) |
| D — The listen loop runs the whole receiver: probe → pull → intake → claim → handler → report, logging once per state change | 9, 10 | `BusListener.StartAsync`/`ProcessOnceAsync`/`RunAsync` over substitutes and a fake clock; the integration round trip on the bare repo incl. rejection via the loop, the deferred commit recovered, scopes `tenant`/`machine`/`phase` | 🛑 after Step 10 |
| E — The `zyggy` verbs: `secret set`, `probe listen`, `probe send`, `probe stats`, `probe reach`, all configured by `node.json` | 11, 12, 13, 14 | `node.json` read with the §10 refusals; DI composition `AddGitHubBus`; `secret set` seeds a key from stdin; `probe listen` refuses bad configuration before touching git; the out-of-process round trip `send` ↔ `listen` with a ledger and `stats`; `reach` prints the four-row matrix with a side-effect-free push | 🛑 after Step 12 (⚠️ secrets verb, public API, DI) · 🛑 after Step 14 (⚠️ PAT never reaches git; AC-29 sweep) |
| F — Documents | 15 | Core and Cli READMEs carry the runbook entries and the per-machine setup; the owner confirms A1–A6 pasted | 🛑 after Step 15 |
| G — Live: the bus repository, the binaries, the secrets, the matrix, the probes | 16, 17, 18, 19, 20, 21 | `zyggy-bus` exists with its ruleset, PATs and deploy key; the release is pinned on Central and copied to both laptops; keys seeded; `reach` on three machines; ≥ 10 + ≥ 10 probes; 0001 filled; roadmap status | 🛑 after Step 18 (⚠️ secrets on three machines, work-laptop boundary) · 🛑 after Step 21 (definition of done) |

Every code step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally (Windows); Linux by CI on both runners at each gate (`.github/workflows/ci.yml`, incl. the publish + smoke step for both RIDs — AC-38).

<!--
Decomposition strategy: vertical slices. Fake = pure records/state machine, RecordingProcessRunner for git, StubGitHubHandler for HTTP,
InMemorySecretStore, FakeTimeProvider, NSubstitute for IBusProvider/ISecretStore; unit tests in tests/Zyggy.Core.Tests/Bus etc.
Wire = the real GitClient over ProcessRunner against BusRepoFixture (tenant acme), a loopback HttpListener standing in for api.github.com
out of process, the built zyggy binary; integration tests in tests/Zyggy.Integration. Never the real claude, never GitHub.
Steps are named by what the system can do. One gate per slice (E and G carry two: secrets/public API, and live). No Gates/P<n> slice: 05 closes P0.
-->

---

## Shared rules for this plan

- **Tenant and machines in tests**: tenant `acme`, machines `central`, `home-laptop`, `work-laptop`, keys `acme/1`/`acme/2` from `TestKeys`; the foreign tenant is `globex`. Nothing in `src/` names `geoffrey`, `zyggy-org` or `zyggy-bus` (`SourceHygieneTests.Src_ContainsNoTenantLiteral` stays green; the README examples may).
- **Secrets in tests** are 03's two synthetic keys and a synthetic PAT string `zyggy-test-pat-0001` defined once in `tests/Zyggy.Core.Tests/Infrastructure/TestKeys.cs` (`TestPat`), never anything token-shaped (`ghp_`, `github_pat_`). The **AC-29 sweep** is a shared helper `tests/Zyggy.Core.Tests/Infrastructure/SecretSweep.cs` (linked into the Integration project like `TestKeys.cs`): `AssertAbsent(IEnumerable<string> texts)` fails when any text contains `TestPat`, `Secret1`/`Secret2` (raw or lowercase hex) — called on every recorded `ProcessSpec` (arguments, environment values, stdin), every captured log line, every exception message and every CLI stdout/stderr in Steps 4–14.
- **Logging in unit and integration tests** goes through a small `tests/Zyggy.Core.Tests/Infrastructure/RecordingLogger<T>.cs` (`ILogger<T>` capturing level, message, scopes) — never assert on log text as the only evidence; the asserted facts are files, commits and returned records, the log assertions are the §11 "once per state change" counts.
- **Clock**: `FakeTimeProvider` everywhere in unit tests; `BusRepository`'s retry delays and `BusListener.RunAsync` waits use `Task.Delay(…, clock, ct)`, so a test advances the fake clock. Integration tests of the real loop use short real intervals (`poll.activeSeconds: 1` in the test `node.json`, `--timeout 5`).
- **Jitter**: `BusRepository` takes `Func<double> jitter` in [0, 1] (delay = `1 s + jitter() × 2 s`); `BusListener` takes `Random` and derives `jitterUnit = random.NextDouble() × 2 − 1`. Tests pass fixed functions / a seeded `Random`.
- **Paths**: every bus path in `src/` comes from `BusPaths`; the file system join is `BusPaths.Resolve(checkoutRoot, relative)`. Unit tests of `BusRepository` lay a temp checkout out with `BusPaths` and record git; integration tests assert on the **bare** repository (`git show main:<path>`, `git log -1 --format=%s main`).
- **Never**: the real `claude`; GitHub; a bare program name other than `git` in a `ProcessSpec`; `UseProxy = false` under `Bus/`; `DateTime.UtcNow`/`TimeProvider.System` outside DI registration; a static mutable field; a `-c credential.*`, `http.extraHeader`, URL userinfo or `GIT_ASKPASS` set by Zyggy.
- **Public API** per `.claude/instructions/public-api.md`: the spec's visibility table (ex-OQ8) is binding — `GitHubBusProvider`, `RateLimitHeaders`, `GitClient` and its additions, `EnvelopeParser.PeekFrom` stay `internal`; every public member XML-documented citing the §4/§5 rule it implements.

---

## Step 1 — Every bus path for tenant `acme` comes from `BusPaths` and parses back to `(tenant, machine, folder, id)`; nothing in `src/` builds a bus path or names GitHub outside its adapter; a receiver can name `wrong_recipient` as a rejection reason; the sender of an unverifiable file can still be read leniently from its raw front matter

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusPaths.cs` *(create, `public static class`, namespace `Zyggy.Core.Bus`)*: `Tenant(TenantId)`, `Registry(TenantId, MachineName)`, `Jobs`, `Claimed`, `Rejected`, `Reports`, `Context` (each `(TenantId, MachineName)`), `Archive(TenantId, int year, int month)` (`YYYY-MM`), `EnvelopeFile(string folder, EnvelopeId id)` (`<folder>/<ULID>.md`), `Protocol` (`PROTOCOL.md`), `Resolve(string checkoutRoot, string relative)` (file-system join, `Path.Join` after replacing `/` with the OS separator), `TryParse(string relative, out BusLocation location)`. All results `/`-separated, repository-relative, typed inputs, no default tenant, no parameterless overload. `TryParse` is strict: lowercase labels only, exactly the §4 shapes, `..`/backslash/uppercase/`policy.yaml` → false; it never assumes a tenant.
- `src/Zyggy.Core/Bus/BusFolder.cs` *(create)*: `public enum BusFolder { Jobs, Claimed, Rejected, Reports, Context, Registry, Archive }`.
- `src/Zyggy.Core/Bus/BusLocation.cs` *(create)*: `public sealed record BusLocation(TenantId Tenant, MachineName? Machine, BusFolder Folder, EnvelopeId? Id)`.
- `src/Zyggy.Core/Envelope/EnvelopeRejectionReason.cs` *(modify, additive)*: `WrongRecipient` appended (`<c>wrong_recipient</c>: <c>to</c> is not the receiver's machine (§4, deliverable 04)`); `EnvelopeWire.cs` *(modify)*: `ToWire(WrongRecipient) => "wrong_recipient"`.
- `src/Zyggy.Core/Envelope/EnvelopeParser.cs` *(modify, additive)*: `internal static MachineName? PeekFrom(ReadOnlyMemory<byte> file)` — splits with `FrontMatterReader.Read`; when the read succeeds and the top-level `from` is a scalar that `MachineName.TryParse` accepts, returns it; **null** for a malformed file, a non-mapping root, a missing/non-scalar/invalid `from`; never throws for any byte input (ex-OQ10).
- `tests/Zyggy.Core.Tests/Bus/BusPathsTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Envelope/EnvelopeWireTests.cs` *(modify: one row; the "every member has a row" fact → 10)*; `tests/Zyggy.Core.Tests/Envelope/EnvelopeParserTests.cs` *(modify: `PeekFrom` facts)*; `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify: the AC-2 facts — they go green now and stay the guard for every later step)*.

**Seams**: none (pure).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusPathsTests|FullyQualifiedName~EnvelopeWireTests|FullyQualifiedName~EnvelopeParserTests|FullyQualifiedName~SourceHygieneTests"`):
- `BusPathsTests` (AC-1): `[Theory] Build_EveryMember_EqualsSpecPath` (the ten exact strings of the spec's `BusPaths` table for `acme`/`home-laptop`/`01J8Y3N7Q2X9Z4A5B6C7D8E9F0`, incl. `Archive(acme, 2026, 9) == "tenants/acme/archive/2026-09"` and `Protocol == "PROTOCOL.md"`); `Build_NeverContainsBackslash` (every member on Windows); `[Theory] TryParse_ProducedPath_RoundTrips` (each produced path → the same `(Tenant, Machine, Folder, Id)`); `TryParse_OtherTenantPath_YieldsThatTenant` (`tenants/globex/nodes/x/jobs/<id>.md` → `globex`); `[Theory] TryParse_Refused_ReturnsFalse` (`tenants/acme/policy.yaml`, `../x`, `tenants/Acme/nodes/home-laptop/jobs/<id>.md`, `tenants/acme/nodes/home-laptop/jobs/<id>.txt`, `tenants/acme/nodes/home-laptop/jobs/claimed/rejected/<id>.md`, `tenants\acme\…`, `""`); `Resolve_JoinsWithOsSeparator`; `Api_HasNoParameterlessOrStringTenantOverload` (reflection: every public method taking a tenant takes `TenantId`).
- `EnvelopeWireTests`: `ToWire_WrongRecipient_IsWrongRecipient`; `TryFromWire_wrong_recipient_RoundTrips`; the member-count fact updated to ten.
- `EnvelopeParserTests`: `PeekFrom_SignedGoldenJob_ReturnsCentral` (`Golden.Md("job")`); `PeekFrom_TamperedSignature_StillReturnsFrom` (`EnvelopeText.WithValue("sig", …)`); `PeekFrom_ForeignTenantAndSchema2_StillReturnsFrom`; `PeekFrom_FromNotALabel_ReturnsNull` (`from: Not-A-Label`); `PeekFrom_NoFrontMatter_ReturnsNull`; `PeekFrom_FromMissing_ReturnsNull`; `PeekFrom_RandomBytes_ReturnsNullWithoutThrowing`.
- `SourceHygieneTests` (AC-2): `Src_BusPathLiterals_OnlyInBusPaths` (`"tenants/"`, `"nodes/"`, `"jobs/"`, `"reports/"`, `"context/"`, `"archive/"`, `"registry/"`, `"PROTOCOL.md"` as string literals only in `src/Zyggy.Core/Bus/BusPaths.cs`; the existing memory code is excluded by matching the literal with the leading quote); `Src_GitHubLiterals_OnlyInGitHubBusProvider` (`api.github.com`, `github.com`, `x-ratelimit`, `x-poll-interval` → only `src/Zyggy.Core/Bus/GitHubBusProvider.cs` — the file does not exist yet, so the fact asserts "no holder or exactly that file"); `Src_Bus_NeverDisablesProxy` (no `UseProxy = false` / `UseProxy=false` under `src/Zyggy.Core/Bus/`); `Src_Bus_NoClockOrStaticState` (under `Bus/` and `Configuration/`: no `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `TimeProvider.System`, `static` field that is not `readonly`/`const`); the existing `Src_ContainsNoTenantLiteral` stays green.

**GREEN**: as Scope. `BusPaths` builds with `string.Concat`/interpolation over `TenantId.Value`, `MachineName.Value`, `EnvelopeId.Value`; `TryParse` splits on `/`, validates each segment with `TenantId.TryParse`/`MachineName.TryParse`/`EnvelopeId.TryParse` and the fixed folder words; `PeekFrom` reuses `FrontMatterReader.Read` and `FrontMatterScalar`.

**Contract impact**: ⚠️ **§4 protocol change**: `EnvelopeRejectionReason` gains `WrongRecipient` (`wrong_recipient`) — decided ex-OQ1, founding-spec amendments A1/A2 (Step 15 confirms them pasted). `BusPaths` is the §4 layout contract 07/08/12/15/17 build on. No new package.

**VERIFY**: failing-run command passes; `git diff tests/golden/` empty (03's goldens untouched); `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + `dotnet format Zyggy.slnx --verify-no-changes` green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — A test bus repository for tenant `acme` has the §4 layout on `main`, seeded through the production `BusPaths`; a second clone acting as the sender pushes a signed job addressed to `home-laptop`, and the fixture can show any file, the head SHA and the last commit subject of the bare repository

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs` *(modify)*: constructor `BusRepoFixture(TenantId tenant)` (**no default**, no parameterless constructor — the brief's rule); `Tenant` property; a parameterless xUnit class-fixture path is impossible, so tests use `IAsyncLifetime` with `new BusRepoFixture(TenantId.Parse("acme"))` or the new `AcmeBusRepoFixture : BusRepoFixture` (sealed, parameterless, `acme`) for `IClassFixture<>`; `SenderDir` (a second clone created in `InitializeAsync`); `SeedTenantAsync(TenantId tenant, IReadOnlyList<MachineName> machines, CancellationToken ct)` — creates `BusPaths.Protocol` (placeholder line), `BusPaths.Registry` directory, `Jobs`/`Claimed`/`Rejected`/`Reports`/`Context` of every machine and `Archive(tenant, 2026, 9)` each with a `.gitkeep`, commits `seed <tenant>` from the **sender** clone and pushes; then `git pull` in the SUT clone; `SeedSignedJobAsync(MachineName from, MachineName to, KeyId signWith, ISecretStore store, string body, Func<Envelope, byte[]>? mutate = null, string? fileNameOverride = null, CancellationToken ct)` — builds `Envelope.CreateJob` (`project: probe`, `timeout_minutes: 2`, `created` from a fixed instant), signs with the production `EnvelopeSigner`, writes `BusPaths.EnvelopeFile(BusPaths.Jobs(tenant, to), id)` (or the override) in the sender clone, commits `<tenant> job <id> <from>→<to>` and pushes; returns `(EnvelopeId Id, string RelativePath, byte[] Bytes)`; `SeedRawFileAsync(string relativePath, byte[] bytes, string subject, ct)` (for the malformed and foreign-tenant files); `AdvanceMainFromSenderAsync(ct)` (one foreign commit touching `tenants/<t>/nodes/central/context/.gitkeep`-style noise, for the push-rejection scenarios); `ShowBytesAsync(path, ct)`; `ExistsOnMainAsync(path, ct)` (`git cat-file -e main:<path>`); `RevListCountAsync(string range, ct)` on the clone.
- `tests/Zyggy.Integration/Bus/BusRepoFixtureSmokeTests.cs` *(modify)*: `Fixture_PublicSurface_HasNoTenantMember` is **replaced** by `Fixture_RequiresATenant` (no parameterless constructor on `BusRepoFixture`) — the one sanctioned edit to 01's facts (the brief supersedes 01); the other smoke facts adapt to `AcmeBusRepoFixture`.
- `tests/Zyggy.Integration/Bus/BusLayoutTests.cs` *(create)*.

**Seams**: none in production code; the fixture runs `git` through `Process` by design (test infrastructure must not depend on the production client).

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusLayoutTests|FullyQualifiedName~BusRepoFixtureSmokeTests"`):
- `BusLayoutTests` (AC-17 arrange, integration-testing skill rule "seed with the production `BusPaths`"): `SeedTenantAsync_Acme_CreatesEveryFolderOfTheSpecLayoutOnMain` (`git ls-tree -r --name-only main` equals exactly the `.gitkeep` set derived from `BusPaths` for `central` and `home-laptop` plus `PROTOCOL.md`); `SeedTenantAsync_Globex_NeverTouchesAcme` (a second tenant seeded beside the first; `acme` tree unchanged); `SeedSignedJobAsync_ToHomeLaptop_FileOnMainParsesAndVerifiesWithAcme1` (production `ParseAndVerifyAsync` over `ShowBytesAsync`; `Header.To == home-laptop`; subject `acme job <id> central→home-laptop`); `SeedSignedJobAsync_Mutated_FileOnMainIsTheMutatedBytes` (a body byte flipped after signing → `ParseAndVerifyAsync` → `InvalidSignature`); `AdvanceMainFromSenderAsync_MovesHeadSha`; `Fixture_RequiresATenant`.

**GREEN**: as Scope (test project only; no production code).

**Contract impact**: none in `src/`; the fixture's shape is the integration-testing skill's (finding 2 of the spec: the skill text shows `Tenant = "geoffrey"` — forwarded, not edited here).

**VERIFY**: failing-run command passes; `dotnet test Zyggy.slnx --filter "Category!=Integration"` still runs zero `Zyggy.Integration` tests; `<temp>/zyggy-it/` left clean; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (every bus path from one place; a seeded test bus) *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `BusPathsTests` (ten exact paths, round trips, refusals), `EnvelopeParserTests.PeekFrom_*`, `SourceHygieneTests` AC-2 facts, `BusLayoutTests` (the `ls-tree` of the seeded bare repository shown beside the §4 layout block).
- [x] Contract review: AC-1, AC-2 against the spec; the `BusPaths` table member by member; `wrong_recipient` wire string; `PeekFrom` null cases (ex-OQ10); the fixture seeds through `BusPaths` and asserts on the bare repository.
- [x] ⚠️ Risk review (§4 protocol change): `EnvelopeRejectionReason.WrongRecipient` appended last (existing values unchanged); 03's golden files byte-identical; A1/A2 wording for the founding spec acknowledged as pending (Step 15); the one sanctioned test edit (`Fixture_PublicSurface_HasNoTenantMember` → `Fixture_RequiresATenant`).
- [x] User approved — implementation may continue past this gate

---

## Step 3 — The poller decides when to wake without any I/O: on a fake clock a `304` keeps the ETag and schedules the next tick at 10 s ± 2 s (60 s idle), a `200` stores the new ETag and says "pull", a rate-limit answer backs off until the later of 120 s, `x-ratelimit-reset` and `retry-after`, `x-poll-interval` raises the interval, a network error doubles from 10 s to a 5-minute cap, and the phase change is reported exactly once

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/HeadProbe.cs` *(create)*: `public abstract record HeadProbe { public TimeSpan? MinimumInterval { get; init; } }` with `Unchanged(int? RateLimitRemaining)`, `Changed(string ETag, int? RateLimitRemaining)`, `Throttled(DateTimeOffset? ResetUtc, TimeSpan? RetryAfter, int? RateLimitRemaining)`, `Unreachable(string Detail)` (closed: private protected constructor).
- `src/Zyggy.Core/Bus/PollMode.cs`, `PollPhase.cs`, `PollOptions.cs` (`Default` = 10 s, 60 s, 2 s, 120 s, 100, 10 s, 5 min; validation: positive spans, floor ≥ 0), `PollState.cs` (`Initial(now)`: `ETag null`, `Polling`, `NextDueUtc = now`, `BackoffUntilUtc null`, `ConsecutiveFailures 0`, `RateLimitRemaining null`; `Apply(HeadProbe, PollMode, DateTimeOffset now, double jitterUnit, PollOptions)` pure; `jitterUnit` outside [−1, 1] → `ArgumentOutOfRangeException`), `PollTransition.cs` (`(PollState State, bool Pull, PollPhase? PhaseChanged)`) *(create, all public, namespace `Zyggy.Core.Bus`)*.
- `tests/Zyggy.Core.Tests/Bus/PollStateTests.cs` *(create)*.

**Seams**: `TimeProvider` only as the `now` argument (`FakeTimeProvider.GetUtcNow()` in tests); no I/O.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~PollStateTests"`):
- AC-3: `[Theory] Apply_UnchangedActive_NextDueIsTenSecondsPlusJitter` (`jitterUnit` −1, 0, +1 → 8 s, 10 s, 12 s; `Pull == false`; `PhaseChanged == null`); `Apply_UnchangedIdle_BaseIsSixtySeconds`; `Apply_UnchangedRepeated_KeepsETagAndPhase`.
- AC-4: `Apply_Changed_StoresETagPullsAndKeepsInterval` (`W/"abc"`, remaining 4999); `Apply_UnchangedAfterChanged_KeepsETag`; `Apply_ChangedEmptyETag_StoresEmpty` (a `200` without validator).
- AC-5: `Apply_ThrottledWithResetSevenMinutes_BackoffIsReset`; `Apply_UnchangedRemainingBelowFloor_BackoffIsFloor` (remaining 99 → T+120 s, `Pull` false); `Apply_ThrottledRetryAfterThirty_BackoffIsFloor`; `Apply_ThrottledResetTenSeconds_BackoffIsFloor`; `Apply_ThrottledRetryAfterThreeHundred_RetryAfterWins`; `Apply_ThrottledTwice_PhaseChangedOnlyOnce`; `Apply_UnchangedAfterBackoffElapsed_ReturnsToPollingWithPhaseChanged`; `Apply_UnchangedWithMinimumIntervalThirty_NextDueIsThirtyPlusJitter`; `Apply_NextDueEqualsBackoffUntilPlusJitter`.
- AC-6: `Apply_UnreachableSevenTimes_DelaysDoubleToCap` (10, 20, 40, 80, 160, 300, 300 s; `ConsecutiveFailures` 1..7; `PhaseChanged == NetworkBackoff` on the first only); `Apply_UnchangedAfterNetworkBackoff_ResetsFailuresWithPhaseChangedPolling`; `Apply_ChangedAfterNetworkBackoff_PullsAndResets`.
- AC-7: `Apply_Any_LeavesInputStateUnchanged` (records: reference and value equality before/after); `Apply_JitterUnitOutOfRange_Throws` (−1.01, 1.01); `Initial_DueImmediatelyPolling`; `Default_MatchesSpecTable`.

**GREEN**: `PollState.Apply` as the spec's transition table (first match wins; `interval(mode) = max(ActiveInterval|IdleInterval, outcome.MinimumInterval)`; `Throttled` back-off `max(now + ThrottleInterval, reset, now + retryAfter)`; `Unreachable` → `now + min(Max, Min × 2^failures)` with `failures` = the previous `ConsecutiveFailures`; `PhaseChanged` only when the phase differs; `ConsecutiveFailures` resets on any non-`Unreachable`).

**Contract impact**: ⚠️ §5 poller contract (07 hosts it unchanged); the ex-OQ6 reshape (floor, `retry-after`, `x-poll-interval`) is amendment A5. `PollState.ETag`/`RateLimitRemaining`/`BackoffUntilUtc` are 07's health fields.

**VERIFY**: failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — The doorbell is rung the §5 way: `GitHubBusProvider` sends exactly one conditional `GET` with the PAT read from `ISecretStore` on every request, `User-Agent: zyggy/<version>`, `Accept` and `X-GitHub-Api-Version`, maps `304`/`200`/`403`/`429`/`401`/`5xx`/exceptions/timeouts to `HeadProbe` without ever throwing or leaking the token, refuses to call out when the PAT is absent, and its handler keeps the system proxy with default credentials

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/IBusProvider.cs` *(create)*: the spec's interface (`Remote`, `ProbeHeadAsync(string? etag, ct)`, `PullAsync(ct)`, `PushAsync(ct)`), XML docs citing §5 items 1–2 and the §9 seam rule; `BusRemote.cs` (`(string Owner, string Repository, string Branch, Uri HttpsUrl, Uri? SshUrl)`), `BusSyncResult.cs` (`(bool Succeeded, string Detail)`), `PushResult.cs` (`Pushed`, `Rejected(Detail)`, `PushFailed(Detail)`, closed) *(create, public)*.
- `src/Zyggy.Core/Bus/BusIdentity.cs` *(create, public)*: `(TenantId Tenant, MachineName Machine)`.
- `src/Zyggy.Core/Bus/RateLimitHeaders.cs` *(create, `internal static`)*: parses `x-ratelimit-remaining`, `x-ratelimit-reset` (epoch seconds → `DateTimeOffset`), `retry-after` (seconds or HTTP date via `RetryConditionHeaderValue`), `x-poll-interval` → `TimeSpan`; never throws on garbage (null).
- `src/Zyggy.Core/Bus/GitHubBusProvider.cs` *(create, `internal sealed class`)*: constructor `(HttpMessageHandler handler, GitClient git, BusRemote remote, ISecretStore secrets, BusIdentity me, string checkoutRoot, string version, Uri apiBaseUri, ILogger<GitHubBusProvider> log)`; `ProbeHeadAsync`: `secrets.GetAsync(me.Tenant, SecretName.Parse("github-pat"))` → null → `Unreachable("no github-pat for tenant <t>")` and **no request**; else `GET {apiBaseUri}/repos/{owner}/{repo}/commits?sha={branch}&per_page=1` with `Authorization: Bearer <utf8 token>`, `If-None-Match` when `etag` non-null (verbatim, weak validators kept), `User-Agent: zyggy/<version>`, `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`; 30 s timeout via a linked `CancellationTokenSource`; mapping per the spec's table (`200` without `ETag` → `Changed("")` logged once at `Warning`); every response parses `x-ratelimit-remaining` and `x-poll-interval`; `Detail` contains only status codes, exception type names and rate-limit numbers. `PullAsync`/`PushAsync` are **stubs in this step** (`NotImplementedException` is forbidden by "never throws" — they return `BusSyncResult(false, "not wired")` / `PushFailed("not wired")` until Step 5).
- `src/Zyggy.Core/Bus/GitHubBusOptions.cs` *(create, public)*: `BusRemote? Remote`, `BusIdentity? Identity`, `string? CheckoutRoot`, `string? Version`, `Uri ApiBaseUri = new("https://api.github.com")` (**Assumption A3**: the test-only override for the out-of-process CLI tests), `GitIdentity? CommitIdentity` (filled in Step 6).
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(create, public, `AddGitHubBus(this IServiceCollection, Action<GitHubBusOptions>? configure = null)`)*: registers `GitHubBusOptions`, the singleton `IBusProvider` → `GitHubBusProvider` built with `new SocketsHttpHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials, AllowAutoRedirect = false }` (the one place an `HttpMessageHandler` for the bus is created; `internal static SocketsHttpHandler CreateHandler()` for the test to inspect), `GitClient` (singleton, `GitClientOptions` with `Identity` from options — Step 6), `BusRepository` and `BusListener` (Steps 6–9; registered here as they appear). Requires `ISecretStore`, `IProcessRunner`, `TimeProvider`, and either explicit options or a registered `NodeConfiguration` (Step 11) — **no hidden default**.
- `tests/Zyggy.Core.Tests/Infrastructure/StubGitHubHandler.cs` *(create — an `HttpMessageHandler` scripted with a queue of `Func<HttpRequestMessage, HttpResponseMessage>` or an exception to throw; records every request)*; `tests/Zyggy.Core.Tests/Infrastructure/TestKeys.cs` *(modify: `TestPat`)*; `tests/Zyggy.Core.Tests/Infrastructure/SecretSweep.cs`, `RecordingLogger.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Bus/GitHubBusProviderTests.cs`, `tests/Zyggy.Core.Tests/Bus/GitHubBusRegistrationTests.cs` *(create)*.

**Seams**: `ISecretStore` (`InMemorySecretStore` holding `github-pat` for `acme`; NSubstitute for the call count), HTTP through the injected `HttpMessageHandler` (the §9 "HTTP call injected"), `IProcessRunner` (`RecordingProcessRunner`, unused in this step), `TimeProvider`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~GitHubBusProviderTests|FullyQualifiedName~GitHubBusRegistrationTests|FullyQualifiedName~SourceHygieneTests"`):
- AC-8: `ProbeHeadAsync_WithETag_SendsOneSpecRequest` (URL, five headers exact; `User-Agent` = `zyggy/<the version passed>`); `ProbeHeadAsync_NoETag_OmitsIfNoneMatch`.
- AC-9: `[Theory] ProbeHeadAsync_Response_MapsToHeadProbe` — `304` + `x-ratelimit-remaining: 4321` → `Unchanged(4321)`; `200` + `ETag: W/"new"` + `4000` → `Changed("W/\"new\"", 4000)`; `403` + `remaining: 0` + `reset: <epoch>` → `Throttled(epoch, null, 0)`; `429` + `retry-after: 60` → `Throttled(null, 60 s, …)`; `401` → `Unreachable("unauthorized")`; `500` → `Unreachable("http 500")`; `HttpRequestException` → `Unreachable(…)`; `TaskCanceledException` (handler) → `Unreachable(…)`; `ProbeHeadAsync_PollIntervalHeader_SetsMinimumInterval` (on `304` and `200`); `ProbeHeadAsync_RetryAfterAsHttpDate_ParsedToDelay`; `ProbeHeadAsync_200WithoutETag_ChangedEmptyLoggedOnce`; `ProbeHeadAsync_NeverThrows` (every scripted case completes); `ProbeHeadAsync_DetailNeverContainsTokenOrHeaderValues` (`SecretSweep` over every `Detail` and every log line; also no `W/` text in a `Detail`).
- AC-10: `ProbeHeadAsync_NoPatInStore_UnreachableWithoutRequest` (`Detail == "no github-pat for tenant acme"`; handler received zero requests).
- AC-11: `CreateHandler_UsesSystemProxyDefaultCredentialsNoRedirect` (`UseProxy == true`, `DefaultProxyCredentials == CredentialCache.DefaultCredentials`, `AllowAutoRedirect == false`); `ProbeHeadAsync_TwoRequests_ReadsPatTwice` (NSubstitute `ISecretStore` → `GetAsync` received exactly 2 calls with `acme`/`github-pat`); `Request_TimeoutIsThirtySeconds` (the linked token cancels at 30 s on a hanging handler driven by `FakeTimeProvider` — or, if the handler's delay cannot be faked, the test asserts the `CancellationToken` passed to the handler is cancelled after `FakeTimeProvider.Advance(31 s)`).
- `GitHubBusRegistrationTests`: `AddGitHubBus_WithExplicitOptionsAndStore_ResolvesGitHubBusProviderSingleton`; `AddGitHubBus_WithoutSecretStore_ResolvingThrowsInvalidOperationException`; `AddGitHubBus_ReturnsSameCollection`.
- `SourceHygieneTests.Src_GitHubLiterals_OnlyInGitHubBusProvider` now has exactly one holder.

**GREEN**: as Scope. `ProbeHeadAsync` wraps the whole request in `try/catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested or InvalidOperationException)` → `Unreachable(ex.GetType().Name)`; a caller's own cancellation propagates (the loop ends on Ctrl+C).

**Contract impact**: ⚠️ public API `IBusProvider`, `BusRemote`, `HeadProbe` family, `PushResult` family, `BusSyncResult`, `BusIdentity`, `GitHubBusOptions`, `AddGitHubBus` (the §9 seam — reviewed at the gate); ⚠️ secrets: the PAT travels only as the `Authorization` header; ⚠️ proxy: the product's `UseProxy = false` pattern deliberately not followed (AC-11, hygiene fact).

**VERIFY**: failing-run command passes; `Select-String -Path src/Zyggy.Core/Bus/*.cs -Pattern 'UseProxy'` shows only the `= true` line; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 5 — The provider's `pull` brings a foreign commit into the checkout and its `push` lands a local commit on the bare repository's `main` through the real `git`; a push against a remote that moved on is a `Rejected` result, never an exception; and both the product provider (head check answered from the bare repository's `rev-parse main`) and the test double `LocalBareRepoBusProvider` pass the same contract suite

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Git/GitClient.cs` *(modify, additive)*: `PullRebaseAsync(string repository, string branch, ct)` → `pull --rebase origin <branch>`.
- `src/Zyggy.Core/Bus/GitHubBusProvider.cs` *(modify)*: `PullAsync` → `git.PullRebaseAsync(checkoutRoot, remote.Branch)` → `BusSyncResult(result.Succeeded, stderr first 300 chars, userinfo-redacted)`; `PushAsync` → `git.PushAsync(checkoutRoot, remote.Branch)` → `Pushed` / `Rejected` when stderr contains `[rejected]`, `non-fast-forward` or `fetch first` / `PushFailed(detail)` otherwise (incl. `ExitCode == null`: start failed or timed out).
- `tests/Zyggy.Core.Tests/Git/GitClientTests.cs` *(modify: `PullRebaseAsync_BuildsSpecArguments`)*; `tests/Zyggy.Core.Tests/Bus/GitHubBusProviderTests.cs` *(modify: the push classification rows over `RecordingProcessRunner`)*.
- `tests/Zyggy.Integration/Infrastructure/LocalBareRepoBusProvider.cs` *(create, `internal sealed class : IBusProvider`)*: `ProbeHeadAsync(etag)` = `git rev-parse <branch>` in the bare directory (through the production `GitClient` over `ProcessRunner`), `Unchanged` when equal to `etag`, else `Changed(sha, null)`; `PullAsync`/`PushAsync` via the same `GitClient` on the clone; `Remote` describes the bare directory as a `file://` URL.
- `tests/Zyggy.Integration/Infrastructure/BareRepoHeadHandler.cs` *(create)*: an `HttpMessageHandler` that answers the §5 request with `200` + `ETag: "<rev-parse main of the bare dir>"` or `304` when `If-None-Match` matches — the in-process stand-in for GitHub's API over the bare repository.
- `tests/Zyggy.Integration/Infrastructure/TestHost.cs` *(create)*: builds the production services for a given clone (`AddProcessRunner().AddEnvelopeSigning()` + an `InMemorySecretStore` registered as `ISecretStore` + `AddGitHubBus(o => …)`, with the handler replaced by `BareRepoHeadHandler` through an internal factory hook on `GitHubBusOptions` — `internal Func<HttpMessageHandler>? HandlerFactory`, test-only, documented).
- `tests/Zyggy.Integration/Bus/BusProviderContractTests.cs` *(create, `public abstract class`, one `[Fact]` per AC-24 rule, `protected abstract Task<(IBusProvider Provider, BusRepoFixture Bus)> CreateAsync(ct)`)*; `GitHubBusProviderContractTests.cs`, `LocalBareRepoBusProviderContractTests.cs` *(create, sealed subclasses)*.

**Seams**: `IBusProvider` — the two implementations under one suite; `IProcessRunner` wired (`ProcessRunner`, real `git`); HTTP through `BareRepoHeadHandler`.

**RED** (`dotnet test Zyggy.slnx --filter "FullyQualifiedName~GitClientTests|FullyQualifiedName~GitHubBusProviderTests|FullyQualifiedName~BusProviderContractTests|FullyQualifiedName~ProviderContractTests"`):
- Unit: `GitClientTests.PullRebaseAsync_BuildsSpecArguments` (`pull --rebase origin main`, `FileName == "git"`, `GIT_TERMINAL_PROMPT=0`); `GitHubBusProviderTests`: `[Theory] PushAsync_Stderr_Classifies` (`! [rejected] main -> main (fetch first)` → `Rejected`; `non-fast-forward` → `Rejected`; `Authentication failed` → `PushFailed`; exit null → `PushFailed`; exit 0 → `Pushed`); `PullAsync_Failure_IsResultNotException`; `PushAsync_DetailNeverContainsUrlUserinfo` (`https://user:secret@host/` in stderr → redacted).
- Integration, every rule of AC-24 in `BusProviderContractTests` and run twice: `ProbeHeadAsync_NullETag_IsChanged`; `ProbeHeadAsync_SameETag_IsUnchanged`; `ProbeHeadAsync_AfterForeignPush_IsChangedWithNewETag`; `PullAsync_AfterForeignPush_BringsCommitIntoClone` (`rev-parse HEAD` of the clone == bare `main`); `PushAsync_LocalCommit_LandsOnMain` (a file committed in the clone by the fixture's own git; `ExistsOnMainAsync`); `PushAsync_RemoteAhead_IsRejectedNotException` (`AdvanceMainFromSenderAsync` then push → `Rejected`; bare `main` unchanged); `AnyFailure_IsAResult` (the provider pointed at a deleted bare directory → `PullAsync` false, `PushAsync` `PushFailed`, `ProbeHeadAsync` `Unreachable`/`Changed` per implementation — never a thrown exception).

**GREEN**: as Scope.

**Contract impact**: ⚠️ `GitClient` additive (`PullRebaseAsync`); the §5 pull/push semantics behind the seam; the test double lives in the test project only (spec Decision Table).

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ProviderContractTests"` lists 2 × 7 facts; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (the poller wakes on an ETag change and talks to GitHub through the system proxy) *(covers Steps 3–5)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `PollStateTests` (the AC-3..AC-7 numbers shown as a table of inputs → `NextDueUtc`), `GitHubBusProviderTests` (the recorded request with its five headers, the mapping theory, zero requests without a PAT, two `GetAsync` calls for two requests), `GitHubBusRegistrationTests`, the contract suite green for both providers.
- [x] Contract review: AC-3..AC-11 and AC-24 against the spec; the `IBusProvider` shape and the `HeadProbe`/`PushResult` families against Contracts; `PollOptions.Default` against §5 as amended by A5; the request URL/headers against §5 item 1 + the Decision Table (`Accept`, `X-GitHub-Api-Version`).
- [x] ⚠️ Risk review (§5 shared contract, secrets, proxy, public API): `PollState` is pure (AC-7) and `PhaseChanged` carries "log once"; the PAT is read on every request, appears only in the `Authorization` header, never in a `Detail`, log or option (`SecretSweep` facts); `SocketsHttpHandler` keeps `UseProxy = true` with `DefaultProxyCredentials` (AC-11) and the hygiene fact forbids `UseProxy = false` under `Bus/`; `GitHubBusProvider` and `RateLimitHeaders` are `internal`; `GitHubBusOptions.ApiBaseUri` / `HandlerFactory` test hooks accepted (**Assumption A3**); new public types listed and XML-documented; no package added.
- [x] User approved — implementation may continue past this gate

---

## Step 6 — Over a recorded `git`, the node's repository moves a job to `jobs/claimed/` with the §5 write sequence — `pull --rebase`, `mv`, one commit `acme claim <id> central→home-laptop` signed as `zyggy (home-laptop) <home-laptop@zyggy.org>`, `push`; a rejected push is retried after 1–3 s up to three times and a still-failing push leaves one clean, unpushed commit with one `Error` log; a dirty checkout at start-up is stashed under a named stash with one `Warning`, a wrong branch or a rebase in progress is refused, and a write outside my own folders is a caller bug

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Git/GitClient.cs` *(modify, additive)*: `GitIdentity` record (`Name`, `Email`) in `src/Zyggy.Core/Git/GitIdentity.cs` *(create, internal)*; `GitClientOptions.Identity : GitIdentity?`; `CommitOnlyAsync` prefixes `-c user.name=<Name> -c user.email=<Email>` **only when `Identity` is set** (28's call sites unchanged); `MoveAsync(repo, from, to)` → `mv -- <from> <to>`; `StashPushAsync(repo, message)` → `stash push --include-untracked -m <message>`; `LsRemoteAsync(repo, url, branch, environment)` → `ls-remote --exit-code <url> refs/heads/<branch>` with the environment additions merged over `options.Environment` (and `GIT_TERMINAL_PROMPT=0` always); `PushDryRunAsync(repo, url, refspec)` → `push --dry-run <url> <refspec>`; `ConfigGetAsync(repo, key)` → `config --get <key>` → trimmed value or null (exit 1 = unset).
- `src/Zyggy.Core/Bus/WriteOutcome.cs` (`Pushed(int Attempts)`, `Deferred(int Attempts, string Detail)`, `WriteFailed(string Stage, string Detail)`), `BusStartupResult.cs` (`Clean, Stashed, WrongBranch, NotARepository, NoTenantDirectory, RebaseInProgress`), `BusFile.cs` (`(string RelativePath, EnvelopeId Id)`), `JobIntake.cs` (`AcceptedJob(BusFile File, Envelope Job)`, `RejectedJob(BusFile File, EnvelopeRejectionReason Reason, string? Field, string Detail, MachineName? From)`) *(create, public)*.
- `src/Zyggy.Core/Bus/BusRepository.cs` *(create, `public sealed class`, constructor per the spec: `(GitClient git, IBusProvider provider, BusIdentity me, string checkoutRoot, EnvelopeSigner signer, TimeProvider clock, Func<double> jitter, ILogger<BusRepository> log)`)*. This step implements `EnsureCleanAsync` (order: `rev-parse --git-dir` fails → `NotARepository`; `OperationInProgressAsync` → `RebaseInProgress`; `CurrentBranchAsync != provider.Remote.Branch` → `WrongBranch`; `Directory.Exists(Resolve(checkoutRoot, BusPaths.Tenant(me.Tenant)))` false → `NoTenantDirectory`; `StatusPorcelainAsync` non-empty → `StashPushAsync(zyggy-<machine>-<yyyyMMddTHHmmssZ>)` + one `Warning` → `Stashed`; else `Clean`), `ClaimAsync` and the private `WriteAsync(action, relativePaths, Func<Task<GitResult>> change, message)` sequence (spec "Write sequence" 1–5: `PullAsync` → change → `CommitOnlyAsync` → `PushAsync`; `Rejected` → `Task.Delay(1 s + jitter() × 2 s, clock)` → `PullAsync` → push, at most 3 retries; `PushFailed` counts as an attempt in the same loop; after 4 pushes → `Deferred(4, detail)` with one `Error` log, **no reset**; `pull` failure before the change → `WriteFailed("pull", …)`; `mv`/`commit` failure → `WriteFailed(stage, …)` after `CheckoutPathsAsync` restores the paths), and the **write-rule guard** `private void AssertMayWrite(string relativePath)` (`BusPaths.TryParse` must yield `me.Tenant`; for claim/report/reject the machine must be `me.Machine` and the folder ∈ {Jobs, Claimed, Rejected, Reports}; for submit the folder must be `Jobs`; `Archive`/`Registry`/`Context` → `InvalidOperationException("write rule: …")`). `ListNewJobsAsync`, `VerifyIntakeAsync`, `ReportAsync`, `RejectAsync`, `SubmitAsync`, `FindReportsAsync` are **stubs returning empty results in this step** and land in Step 7.
- `tests/Zyggy.Core.Tests/Git/GitClientTests.cs` *(modify: AC-12 rows)*; `tests/Zyggy.Core.Tests/Bus/BusRepositoryWriteTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/TempCheckout.cs` *(create: a temp directory laid out with `BusPaths.Resolve` for `acme`/`home-laptop`, holding files the tests drop; deleted on dispose)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner` scripted per sub-command), `IBusProvider` (NSubstitute: `PullAsync` → success, `PushAsync` scripted `Rejected`×2 then `Pushed`, etc. — in unit tests the provider is a substitute; the recorder sees only `mv`/`commit`/`stash`/`status`), `TimeProvider` (`FakeTimeProvider`), `ILogger` (`RecordingLogger`).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~GitClientTests|FullyQualifiedName~BusRepositoryWriteTests"`):
- `GitClientTests` (AC-12): `[Theory] NewMembers_BuildExactArgumentVectors` over `MoveAsync`, `PullRebaseAsync`, `StashPushAsync`, `LsRemoteAsync` (env additions + `GIT_TERMINAL_PROMPT=0`), `PushDryRunAsync`, `ConfigGetAsync`; `CommitOnlyAsync_WithIdentity_PrefixesUserConfig` (`-c user.name=zyggy (home-laptop) -c user.email=home-laptop@zyggy.org commit --only -F - -- <paths>`); `CommitOnlyAsync_WithoutIdentity_Unchanged` (byte-identical vector to today's); `ConfigGetAsync_Unset_ReturnsNull`; `EverySpec_IsGitWithoutShell`.
- `BusRepositoryWriteTests`: AC-13 `ClaimAsync_PushRejectedTwice_RetriesAfterJitteredDelaysAndPushes` (provider calls in order pull, push, pull, push, pull, push; recorder: `mv`, `commit`; `FakeTimeProvider` advanced by the test shows two 2 s delays for jitter 0.5 → `Pushed(3)`); `ClaimAsync_JitterZeroAndOne_DelaysAreOneAndThreeSeconds`; `ClaimAsync_CommitMessageAndIdentity_MatchSpec` (stdin of `commit` == `acme claim <id> central→home-laptop`; identity prefix present); `ClaimAsync_MovesWithGitMvNotCopy` (exactly one `mv`, no `add`/`rm`); AC-14 `ClaimAsync_PushFailsFourTimes_DeferredWithoutResetAndOneErrorLog` (four `PushAsync` calls, no fifth, no `reset`/`checkout` in the recorder, `Deferred(4, …)`, exactly one `Error`); `ClaimAsync_PullFails_WriteFailedPullNothingChanged` (no `mv`); `ClaimAsync_MvFails_WriteFailedMvAndPathsRestored`; AC-15 `EnsureCleanAsync_DirtyTree_StashesWithNamedStashAndWarnsOnce` (`stash push --include-untracked -m zyggy-home-laptop-20260930T100000Z` from the fake clock); `EnsureCleanAsync_CleanTree_NoStashNoLog`; `EnsureCleanAsync_OnDev_WrongBranchNothingStashed`; `EnsureCleanAsync_RebaseMergeDirExists_RebaseInProgress`; `EnsureCleanAsync_NoTenantDirectory_NoTenantDirectory`; AC-16 `[Theory] Write_OutsideMyFolders_ThrowsWriteRule` (a claim path under `nodes/central/`, under `tenants/globex/`, under `archive/2026-09/`, `registry/` → `InvalidOperationException` containing `write rule`; no git call); `SecretSweep_OverEveryRecordedSpecAndLog_Clean`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ §4 state-machine row 2 and §5 git operations (commit message with `<tenant>` and the action word, retry rule, the *deferred commit* instead of "uncommitted" — ex-OQ9, amendments A3/A4); ⚠️ `GitClient` additive change used by the live dream/offload paths (AC-12 last clause; `DreamCommitterTests` and the dream integration tests stay green unedited); public API `BusRepository` + records.

**VERIFY**: failing-run command passes; `git diff tests/Zyggy.Core.Tests/Dream tests/Zyggy.Integration/Dream tests/Zyggy.Integration/Memory` empty; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 7 — Over a recorded `git` and a temp checkout, the node lists only its own `tenants/acme/nodes/home-laptop/jobs/*.md` in ULID order, verifies each file before anything else (03's order, then `to == me` → `wrong_recipient`), writes a signed `status: done` report and removes the claimed job in one `acme report <ulid> home-laptop→central` commit, rejects into `jobs/rejected/` with a `status: rejected` report whose `reason` is the §4 wire name and whose `to` is the peeked `from` (move only when `from` is unreadable), submits a signed job into `nodes/<to>/jobs/` as `acme job <id> central→home-laptop`, and finds the verified reports that answer a given job

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify)*: `ListNewJobsAsync` (enumerate `Resolve(checkoutRoot, BusPaths.Jobs(me))` top level, `*.md` whose stem `EnvelopeId.TryParse`s, ordinal order; never `claimed/`/`rejected/`; a `*.md` whose stem is not a ULID is still listed, with a synthetic `BusFile.Id = EnvelopeId.New(clock)` used only for logging, and `VerifyIntakeAsync` reports it as `RejectedJob(Malformed, null, "file name is not a ULID", From: null)` so `RejectAsync` moves it — **Assumption A8**); `VerifyIntakeAsync(file)` (read bytes → `signer.ParseAndVerifyAsync(bytes, me.Tenant)` → rejected → `RejectedJob(reason, field, detail, EnvelopeParser.PeekFrom(bytes))`; accepted and `Header.To != me.Machine` → `RejectedJob(WrongRecipient, "to", "to is not this machine", Header.From)`; else `AcceptedJob`; a `Warning` "id differs from file name" when the front-matter `id` ≠ the file stem); `ReportAsync(claimed, ReportFields report, body, signWith)` (new `EnvelopeId.New(clock.GetUtcNow())`, `Envelope.CreateReport(new EnvelopeHeader(me.Tenant, id, me.Machine, claimed.Job.Header.From, now, Normal, InReplyTo: claimed.File.Id), report, body)` → `SignAsync` → `UnknownKey` → `WriteFailed("sign", …)`; write `BusPaths.EnvelopeFile(BusPaths.Reports(me), id)`, `AddAsync` it, `RemoveAsync` the claimed path, commit `acme report <ulid> home-laptop→<from>`, push via `WriteAsync`); `RejectAsync(rejected, signWith)` (`mv` `jobs/<stem>.md` → `jobs/rejected/<stem>.md`; when `From != null`: report `status: rejected`, `reason = EnvelopeWire.ToWire(reason)`, `in_reply_to` = the file-name ULID, `to = From`, body `rejected: <reason> (<field>)\n`, signed, added in the **same commit**; message `acme reject <stem> <me>→<From>` or `acme reject <stem> <me>→unknown` when no report; one `Warning` with id and reason, never the body); `SubmitAsync(signedJob)` (`AssertMayWrite` on `BusPaths.Jobs(me.Tenant, job.Header.To)`; `EnvelopeWriter.Serialize`; `add`; commit `acme job <id> <from>→<to>`); `FindReportsAsync(reporter, inReplyTo)` (enumerate `BusPaths.Reports(me.Tenant, reporter)`, `ParseAndVerifyAsync(bytes, me.Tenant)`, keep `Type == Report && Header.InReplyTo == inReplyTo && Header.To == me.Machine`).
- `tests/Zyggy.Core.Tests/Bus/BusRepositoryIntakeTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/TestEnvelopes.cs` *(create: `SignedJobBytesAsync(from, to, store, signWith, body, mutate)` built with the production factory + signer — the unit counterpart of the fixture's seeder)*.

**Seams**: `IProcessRunner` (recorder; `mv`/`add`/`rm`/`commit` succeed, the test asserts the vectors and reads the files the SUT wrote into the temp checkout), `IBusProvider` (substitute, pull/push succeed), `ISecretStore` (`InMemorySecretStore` with `acme/1`; NSubstitute wrapper to assert no `globex` lookup), `TimeProvider`, `ILogger`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryIntakeTests"`):
- `ListNewJobsAsync_TwoJobsAndClaimedAndRejectedFiles_ListsOnlyTopLevelInUlidOrder`; `ListNewJobsAsync_GlobexTenantFiles_NeverListedOrRead` (a `tenants/globex/...` job in the checkout; also no `File.Open` of it — assert via a file locked exclusively by the test); `ListNewJobsAsync_EmptyFolder_Empty`.
- `VerifyIntakeAsync_GoodJobToMe_Accepted`; `[Theory] VerifyIntakeAsync_Rejected_ReasonAndPeekedFrom` — body flipped → `InvalidSignature`/`From central`; `tenant: globex` + `key_id: globex/1` → `TenantMismatch` and the store received **no** `GetAsync` for `globex`; `to: central` → `WrongRecipient`, `Field "to"`; `schema: 2` → `SchemaUnsupported`; no front matter → `Malformed`, `From null`; `from: Not-A-Label` + bad sig → `InvalidField`/`InvalidSignature` (whichever 03's order yields — the test asserts `From == null`); `VerifyIntakeAsync_IdDiffersFromFileName_AcceptedWithWarning`.
- `ReportAsync_Done_WritesSignedReportRemovesJobOneCommit` (the written file parses and verifies with `acme/1`; `in_reply_to`, `to`, `from`, `status`, `started`/`finished`/`duration_seconds` present; recorder: `add <report>`, `rm <claimed>`, one `commit` with stdin `acme report <ulid> home-laptop→central` and both paths; `WriteOutcome.Pushed`); `ReportAsync_KeyMissing_WriteFailedSignNothingWritten`.
- `RejectAsync_WithFrom_MovesAndWritesRejectedReportInSameCommit` (`mv` + `add` + one `commit`; report `status: rejected`, `reason: wrong_recipient`, `in_reply_to` = file stem, `to: central`, body `rejected: wrong_recipient (to)\n`, signed by `acme/1`, `from: home-laptop`); `[Theory] RejectAsync_EveryReason_WireNameInReport` (the ten reasons → the ten wire strings parse back with `EnvelopeWire.TryFromWire`); `RejectAsync_FromUnreadable_MoveOnlyNoReportOneWarningNamingFile`; `RejectAsync_NeverLogsBody`.
- `SubmitAsync_ToHomeLaptop_AddsSignedFileUnderTargetJobsWithJobCommit` (`acme job <id> central→home-laptop`; bytes == `EnvelopeWriter.Serialize`); `SubmitAsync_ToOtherTenant_ThrowsWriteRule`; `SubmitAsync_Unsigned_ThrowsInvalidOperation` (the writer's rule).
- `FindReportsAsync_ReportForMyJob_Returned`; `FindReportsAsync_ReportForOtherJobOrOtherTo_Ignored`; `FindReportsAsync_UnverifiableReport_Ignored`.
- `SecretSweep_Clean`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ §4 rejection report fields (`reason` = rejection wire name, `in_reply_to` = file name, `to` = peeked `from`; ex-OQ1/ex-OQ10, amendment A1); §4 rows 1 and 3; `context` action reserved for 12.

**VERIFY**: failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — Against the bare repository with the real `git`: a job pushed by the sender clone is claimed by `home-laptop` (byte-identical at `jobs/claimed/`, gone from `jobs/`, one commit `acme claim <id> central→home-laptop`), answered with a report that verifies with `acme/1` while the claimed job is removed, pushed on the second attempt when the sender moved `main` in between, kept as one clean unpushed commit when every push fails, and rejected the four §4 ways plus the malformed and the foreign-tenant cases exactly as the unit tests promised

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/HookedProcessRunner.cs` *(create: an `IProcessRunner` decorator over the production `ProcessRunner` with `Func<ProcessSpec, Task>? BeforeRun` and `Func<ProcessSpec, ProcessResult?>? Override` — the integration-testing skill's "inject a hook through `IProcessRunner` decoration")*; `TestHost.cs` *(modify: `BuildBusRepository(bus, machine, store, hooks?)` through `AddGitHubBus` with `IProcessRunner` replaced by the hooked runner and `IBusProvider` = `GitHubBusProvider` over `BareRepoHeadHandler`)*.
- `tests/Zyggy.Integration/Bus/ClaimAndReportTests.cs`, `PushRetryTests.cs`, `RejectionTests.cs` *(create; `IClassFixture<AcmeBusRepoFixture>` or per-test fixtures where the bare repository must be pristine)*.

**Seams**: `IProcessRunner` wired (real `git` through `ProcessRunner`, decorated); `IBusProvider` wired (`GitHubBusProvider` over the bare repository); `ISecretStore` in-memory; `TimeProvider.System` for the real delays (1–3 s per retry — jitter fixed to 0 by `TestHost` so a retry costs 1 s).

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ClaimAndReportTests|FullyQualifiedName~PushRetryTests|FullyQualifiedName~RejectionTests"`):
- `ClaimAndReportTests`: AC-17 `ClaimAsync_JobFromSender_MovedOnMainByteIdenticalInOneCommit` (`ShowBytesAsync(claimed)` == seeded bytes; `ExistsOnMainAsync(jobs/<id>.md)` false; `LastCommitSubjectAsync == "acme claim <id> central→home-laptop"`; `git log --format=%an main -1` == `zyggy (home-laptop)`; exactly one commit after the seed); AC-18 `ReportAsync_Done_ReportOnMainVerifiesAndClaimedJobGone` (new ULID file under `reports/`, `ParseAndVerifyAsync` with `acme/1`, `in_reply_to`, `to central`, `from home-laptop`, `status: done`, `started`/`finished`/`duration_seconds`; claimed path absent; subject `acme report <ulid> home-laptop→central`; one commit); `SubmitAsync_FromCentralClone_JobOnMainForHomeLaptop` (a `BusRepository` for `central` on the sender clone; subject `acme job <id> central→home-laptop`).
- `PushRetryTests`: AC-21 `ClaimAsync_RemoteAdvancedBetweenPullAndPush_RetriedOnceAndOnTop` (`BeforeRun` on the first `push` → `AdvanceMainFromSenderAsync`; `Pushed(2)`; both commits on `main`, the SUT's on top — `git log --format=%s -2 main`); AC-22 `ClaimAsync_EveryPushFails_DeferredCleanTreeOneUnpushedCommit` (`Override` every `push` → exit 1 `! [rejected] … (fetch first)`; `Deferred(4)`; `rev-list origin/main..HEAD` == 1; `status --porcelain` empty; bare `main` unchanged; one `Error`); `WriteAfterDeferred_PushesTheDeferredCommitToo` (hooks removed; a `ReportAsync` on the same repository → both commits on `main`, `rev-list origin/main..HEAD` == 0 — the "next write carries it" half of AC-22; the listener's `Information` line is Step 10).
- `RejectionTests`: AC-19 `[Theory] VerifyThenReject_FourSpecCases_RejectedOnMainWithReasonReport` — (a) body flipped → `invalid_signature`; (b) `tenant: globex` + `key_id: globex/1` → `tenant_mismatch` and the NSubstitute-wrapped store saw no `globex` lookup; (c) `to: central` → `wrong_recipient`; (d) `schema: 2` → `schema_unsupported`; each: file at `jobs/rejected/<id>.md`, a `reports/<ulid>.md` `status: rejected` with that `reason`, `in_reply_to` = `<id>`, `to` = the job's `from`, signed by `home-laptop` (verifies with `acme/1`), one `Warning`; AC-20 `Reject_NonEnvelopeFile_MovedNoReportOneWarningNamingFileAndMalformed`; `Reject_InvalidFromAndBadSig_MovedNoReport`; AC-23 `ListNewJobsAsync_GlobexJobBesideAcme_OnlyAcmeListedGlobexUntouchedUnlogged` (`SeedTenantAsync(globex, [home-laptop])` + a signed `globex` job; after processing, `ShowBytesAsync` of the globex file unchanged, no log line contains `globex`).
- Every test ends with `SecretSweep.AssertAbsent` over the hooked runner's recorded specs and the captured logs (AC-29).

**GREEN**: test infrastructure only unless a defect surfaces — a production fix here is a plan deviation to report at the gate.

**Contract impact**: none beyond Steps 6–7 (this is their wire).

**VERIFY**: failing-run command passes; the three classes run under 60 s on Windows; `<temp>/zyggy-it/` clean; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C (claim, report, retry, defer, reject on a real bus) *(covers Steps 6–8)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `GitClientTests` new rows; `BusRepositoryWriteTests` (the recorded command order of AC-13 and AC-14 shown verbatim); `BusRepositoryIntakeTests`; the integration classes — show `git log --format='%s | %an' main` of one bare repository after claim + report, the `jobs/rejected/` listing and one rejection report file, the `rev-list origin/main..HEAD = 1` + clean status after the deferral.
- [x] Contract review: AC-12..AC-23 against the spec; commit subjects `<tenant> <action> <id> <from>→<to>` (A3/A4); the rejection report fields (A1); the write-rule guard; `ListNewJobsAsync` lists top level only; `Deferred` leaves no dirty tree and no `reset`.
- [x] ⚠️ Risk review (§4/§5 shared contracts, additive `GitClient`, work boundary): `git diff` of every Dream/Memory/Offload test file empty and the live-path registrations untouched (`CommitOnlyAsync` without identity byte-identical); no secret in any recorded spec/log (`SecretSweep`); report and rejection bodies are fixed strings; `BusRepository` is public but `GitClient` stays internal; the fixture never uses the production client for setup/assertions.
- [x] User approved — implementation may continue past this gate

---

## Step 9 — Over substitutes and a fake clock, the listen loop does the whole receiver's work: `StartAsync` refuses a dirty start the §5 way, pulls once and processes what is already waiting; `ProcessOnceAsync` verifies, claims, calls the handler only for an accepted and claimed job, writes its report, and rejects everything else; `RunAsync` waits until the poller's `NextDueUtc`, rings the doorbell, pulls only when told, never overlaps requests, logs one `Information` per state change and nothing on a silent tick, with `tenant`, `machine` and `phase` on every scope

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/JobHandler.cs` *(create, public delegate `Task<JobReply> JobHandler(Envelope job, CancellationToken ct)`)*; `JobReply.cs` *(create, public record `(ReportStatus Status, string? Reason, ReadOnlyMemory<byte> Body, DateTimeOffset Started, DateTimeOffset Finished)`)*.
- `src/Zyggy.Core/Bus/BusListener.cs` *(create, `public sealed class`, constructor per the spec: `(IBusProvider provider, BusRepository bus, PollOptions options, KeyId signWith, TimeProvider clock, Random jitter, JobHandler handler, ILogger<BusListener> log)`)*: `StartAsync` (`EnsureCleanAsync` → `WrongBranch`/`NotARepository`/`NoTenantDirectory`/`RebaseInProgress` returned without pull; key check: `bus.SignerCanSignAsync(signWith)` — a small `BusRepository` helper that probes `ISecretStore` for `signWith.SecretName` through the signer's store, returning false → the listener returns a new `BusStartupResult.KeyMissing` (**Assumption A4**: spec says "key checked in `StartAsync`" but lists no result member; `KeyMissing` appended to the enum); then `provider.PullAsync`, `ProcessOnceAsync`, `State = PollState.Initial(now)`); `ProcessOnceAsync` (for each `ListNewJobsAsync` file: `VerifyIntakeAsync` → `RejectedJob` → `RejectAsync` → `Warning` once; `AcceptedJob` → `ClaimAsync` → `Pushed`/`Deferred` → `Information` `claim` → `handler(job)` → `ReportAsync(…, JobReply → ReportFields(Status, Reason, Started, Finished, DurationSeconds))` → `Information` `report`; `WriteFailed` → `Error`, file skipped this round; an exception thrown by the handler is caught, logged `Error`, and the job stays claimed for 07/17's resume — no new `reason` code, no report (**Assumption A4**); returns the count handled; when the deferred-commit count (`UnpushedCommitSubjectsAsync` before vs after a `Pushed` write) dropped, `Information` "pushed N deferred commit(s)"); `RunAsync(mode, ct)` (loop: `Task.Delay(State.NextDueUtc − now, clock, ct)`; `ProbeHeadAsync(State.ETag)`; `State.Apply(outcome, mode, now, jitter.NextDouble() × 2 − 1, options)`; `PhaseChanged` → `Information` (`Throttled`/`NetworkBackoff` → `Warning`); `Pull` → `PullAsync` (false → `Error` once per phase) → `ProcessOnceAsync`; ends on `OperationCanceledException`); `State` property; every log call inside `log.BeginScope(new Dictionary<string, object> { ["tenant"], ["machine"], ["phase"] })`.
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify: `BusListener` registration needs the `JobHandler` and `KeyId` — registered by the host; `AddGitHubBus` registers `BusListener` as transient resolved with `ActivatorUtilities` when both are present)*.
- `tests/Zyggy.Core.Tests/Bus/BusListenerTests.cs` *(create)*.

**Seams**: `IBusProvider` (NSubstitute; scripted `HeadProbe` sequence), `IProcessRunner` (recorder, for the `BusRepository` the listener drives over a `TempCheckout`), `ISecretStore` in-memory, `TimeProvider` (`FakeTimeProvider`; the test advances to `NextDueUtc`), `ILogger` (`RecordingLogger` with scopes), `JobHandler` (a recording delegate).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusListenerTests"`):
- `StartAsync_WrongBranch_ReturnsWrongBranchWithoutPull`; `StartAsync_KeyMissing_ReturnsKeyMissing`; `StartAsync_JobAlreadyWaiting_ProcessedBeforeFirstProbe` (spec Edge Cases); `StartAsync_Clean_StateIsInitialDueNow`.
- `ProcessOnceAsync_AcceptedJob_ClaimsThenHandlerThenReport` (order of recorder calls: `mv`, `commit`, then the handler, then `add`/`rm`/`commit`; the handler received the parsed `Envelope`); `ProcessOnceAsync_RejectedJob_HandlerNeverCalled` (AC-19's "zero calls"); `ProcessOnceAsync_TwoJobs_UlidOrderEachOwnCommits`; `ProcessOnceAsync_ClaimDeferred_HandlerStillCalled` (spec: handler after `Pushed` **or** `Deferred`); `ProcessOnceAsync_ClaimWriteFailed_HandlerNotCalledErrorLogged`; `ProcessOnceAsync_DeferredCommitPushedByLaterWrite_LogsPushedDeferred` (`Information` "pushed 1 deferred commit"); `ProcessOnceAsync_HandlerThrows_ErrorLoggedJobStaysClaimed`.
- `RunAsync_UnchangedTick_NoLogNoPull`; `RunAsync_ChangedTick_PullsAndProcesses`; `RunAsync_PhaseChangeToNetworkBackoff_LoggedOnceAcrossRepeatedFailures`; `RunAsync_RecoveryToPolling_LoggedOnce`; `RunAsync_WaitsUntilNextDue` (no probe before the fake clock reaches `NextDueUtc`); `RunAsync_OneInFlightRequest` (a slow `ProbeHeadAsync` completion source; no second probe before the first returns); `RunAsync_Cancelled_EndsWithoutException`; `Logs_EveryEntryHasTenantMachinePhaseScope`; `SecretSweep_Clean`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ §5 poll-loop host contract (07 hosts `BusListener` unchanged); `JobHandler` is a delegate, not an interface (ex-OQ8); `BusStartupResult.KeyMissing` (**Assumption A4**); no new `reason` code.

**VERIFY**: failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — On the bare repository, one tick of the real loop with the probe handler takes a job from `jobs/` to a verified `done` report on `main` (doorbell → pull → claim → handler → report → push), a second tick is silent, the four rejection cases and the malformed file go through `ProcessOnceAsync` with the handler never invoked, and a deferred claim is carried by the next write with the "pushed 1 deferred commit" line

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/Probe/ProbeHandler.cs` *(create, public static)*: `public static JobHandler For(MachineName machine, TimeProvider clock)` — replies `done`, body `zyggy transport probe acknowledged by <machine>\n`, `Started`/`Finished` from the clock (the fixed text of the spec; the first `Probe/` type).
- `tests/Zyggy.Integration/Infrastructure/TestHost.cs` *(modify: `BuildListener(bus, machine, store, handler, hooks?)`)*; `tests/Zyggy.Integration/Bus/BusListenerRoundTripTests.cs` *(create)*.

**Seams**: all wired as Step 8 plus `JobHandler = ProbeHandler.For(home-laptop, clock)`; `RunAsync` driven for a bounded number of ticks by a `CancellationTokenSource` the test cancels after the report appears (polling the bare repository with the fixture, timeout 30 s), `PollOptions` with 1 s intervals for the test.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusListenerRoundTripTests"`):
- AC-25 `RunAsync_JobPushedBySender_OneTickClaimsReportsPushes` (after cancellation: a `reports/<ulid>.md` on `main` parses, verifies, `in_reply_to` = job id, body == the fixed acknowledgement; `jobs/` and `claimed/` empty; log: exactly one `Information` with phase `claim` and one with `report`, none for the `Unchanged` tick; every entry's scope has `tenant`/`machine`/`phase`); `ProbeHeadAsync_SequenceIsChangedThenUnchanged` (the `BareRepoHeadHandler` request log: the first probe answered `200`, the next `304`).
- AC-19 via the loop: `[Theory] ProcessOnceAsync_FourRejectionCases_RejectedOnMainHandlerNeverCalled` (the handler is a counting delegate asserting zero calls; files and reports as Step 8); AC-20 `ProcessOnceAsync_MalformedFile_MovedNoReport`.
- AC-22 second half: `ProcessOnceAsync_AfterDeferredClaim_NextWritePushesItAndLogsPushedDeferred` (hook every `push` to fail during the first `ProcessOnceAsync`; remove the hook; a second job seeded; `ProcessOnceAsync` → both claims and the report on `main`; one `Information` "pushed 1 deferred commit").
- `SecretSweep` over recorded specs, logs.

**GREEN**: `ProbeHandler`; test infrastructure.

**Contract impact**: none beyond Step 9; the probe body text is the spec's constant (fixed, never work data — §8).

**VERIFY**: failing-run command passes within 60 s; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D (the listen loop runs the whole receiver) *(covers Steps 9–10)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `BusListenerTests` (handler-never-called, deferred-then-pushed, once-per-phase logging, one in-flight request), `BusListenerRoundTripTests` — show the captured log of one round trip (two `Information` lines, scopes) and `git log main` of the bare repository.
- [x] Contract review: AC-19, AC-20, AC-22 (second half), AC-25 against the spec; `JobHandler`/`JobReply` shapes; the §11 logging rule (a `304` silent; claim/report/reject/phase change once); **Assumption A4** (`BusStartupResult.KeyMissing`) and the "handler throws → job stays claimed" decision reviewed.
- [x] ⚠️ Risk review (§5 host contract for 07, §8): the handler is invoked only after verification **and** a claim; rejection never reaches it; the probe body is the fixed text; no secret in logs.
- [x] User approved — implementation may continue past this gate

---

## Step 11 — A machine's `node.json` is read into typed configuration with the §10 refusals (no `tenant` → refuse; `signWith` of another tenant → refuse; a key named `pat`/`token`/`hmac`/`secret`/`password` anywhere → refuse; unknown keys ignored; `bus.branch` defaults to `main`; `--config` → `ZYGGY_NODE_JSON` → the per-OS default path), the probe ledger appends and reads JSONL records, `stats` aggregates them per target with nearest-rank percentiles, and git's stderr is classified into the `reach` statuses

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Configuration/NodeConfiguration.cs` (`(TenantId Tenant, MachineName Machine, KeyId? SignWith, BusSettings Bus, PollSettings Poll, SecretsSettings Secrets)`), `BusSettings.cs` (`Owner`, `Repo`, `Branch`, `LocalPath`, `Remote` as raw strings — no URL derivation here, because `https://github.com/…` may only be built inside `GitHubBusProvider` (AC-2); the provider derives `BusRemote` from `GitHubBusOptions`, Step 14), `PollSettings.cs` (`ActiveSeconds = 10`, `IdleSeconds = 60` → `ToPollOptions()` over `PollOptions.Default`), `SecretsSettings.cs` (`Root?`), `NodeConfigurationError.cs` (`FileMissing, Malformed, MissingField, InvalidField`), `NodeConfigurationResult.cs` (`Ok(NodeConfiguration)` / `Error(NodeConfigurationError, string? Field, string Detail)`), `NodeConfigurationReader.cs` (`public static NodeConfigurationResult Read(string path)`; `public static string ResolvePath(string? configOption, IReadOnlyDictionary<string, string?> environment)` — `--config` → `ZYGGY_NODE_JSON` → `%LOCALAPPDATA%\zyggy\node.json` on Windows, `$XDG_CONFIG_HOME/zyggy/node.json` or `~/.config/zyggy/node.json` elsewhere (**Assumption A5**); parsing with `JsonDocument` for the secret-key walk and the source-generated `NodeConfigurationJsonContext` for binding; `signWith` optional at read time — `listen`/`send` require it, `reach` does not) *(create, public except the JSON context)*.
- `src/Zyggy.Core/Bus/Probe/ProbeOutcome.cs` (`Done, Rejected, Timeout, PushFailed` + `ProbeOutcomeWire` `done|rejected|timeout|push_failed`), `ProbeRecord.cs` (`Id, To, SentUtc, ObservedUtc?, Outcome, Reason?, RoundTripMs?`), `ProbeLedger.cs` (`AppendAsync(record)`, `ReadAll()`, path from constructor; `DefaultPath(tenant)` = `<LocalApplicationData>/zyggy/probe/<tenant>/sent.jsonl`; JSONL via source-generated context; append-only, one `File.AppendAllText` per record with `\n`), `ProbeStats.cs` (`public static IReadOnlyList<ProbeStatsRow> Aggregate(records, MachineName? to)` → per target: `Count, Done, Rejected, Timeout, PushFailed, P50, P95, Min, Max` over `done` records, nearest-rank; `RenderMarkdown(rows)`), `ReachStatus.cs` (`Ok, AuthFailed, Unreachable, TlsFailed, Skipped`), `ReachResult.cs` (`(string Check, string Endpoint, ReachStatus Status, string Detail, string Proxy)`), `ReachabilityCheck.cs` (this step: the **pure** parts — `internal static ReachStatus ClassifyGit(int? exitCode, string stderr)` per the spec's table, `ClassifyApi(HeadProbe)`, `Redact(string)` (userinfo stripped, first 300 chars), `ProxyDisplay(Uri? proxy)` host:port or `none`; the runner part is Step 14) *(create, public, namespace `Zyggy.Core.Bus.Probe`)*.
- `tests/Zyggy.Core.Tests/Configuration/NodeConfigurationReaderTests.cs`, `tests/Zyggy.Core.Tests/Bus/Probe/ProbeLedgerTests.cs`, `ProbeStatsTests.cs`, `ReachabilityCheckTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify: the `Configuration/` folder joins the clock/static fact — done in Step 1's regex; verify)*.

**Seams**: none (pure + temp files).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~NodeConfigurationReaderTests|FullyQualifiedName~ProbeLedgerTests|FullyQualifiedName~ProbeStatsTests|FullyQualifiedName~ReachabilityCheckTests"`):
- AC-30: `Read_MinimalSpecFile_TypedConfiguration` (the spec's JSON with `acme`/`home-laptop`/`acme/1`); `Read_UnknownKeys_Ignored` (07's `role`, `workHours`, `claude`); `Read_MissingFile_FileMissingWithPath`; `Read_InvalidJson_Malformed`; `Read_TenantMissing_MissingFieldTenant`; `Read_TenantUppercase_InvalidFieldTenant`; `Read_SignWithOtherTenant_InvalidSignWithTenantDiffers`; `Read_BranchAbsent_DefaultsMain`; `Read_PollAbsent_DefaultsTenSixty`; `Read_PollZero_InvalidField`; `[Theory] Read_SecretLookingKeyAnyDepth_Refused` (`pat`, `token`, `hmac`, `secret`, `password`, `GitHubToken` nested under `bus`, `Secrets` as a top-level key beside `secrets` — case-insensitive → `Invalid(<key>, "secrets never in node.json")`; the legitimate `secrets.root` object key itself is allowed); `ResolvePath_Precedence_OptionThenEnvThenDefault`; `ResolvePath_Default_PerOs`.
- `ProbeLedgerTests`: `AppendAsync_TwoRecords_TwoJsonLinesWithWireFields` (`outcome: "timeout"`, `roundTripMs` absent when null); `ReadAll_RoundTrips`; `ReadAll_MissingFile_Empty`; `DefaultPath_UnderLocalAppDataProbeTenant`.
- `ProbeStatsTests`: `Aggregate_TwoRecordsOneDoneOneTimeout_CountsAndPercentiles` (AC-26's row: count 2, done 1, timeout 1, p50 = p95 = min = max = the one round trip); `Aggregate_NearestRank_P50AndP95OverTenValues`; `Aggregate_FilterTo_OnlyThatMachine`; `RenderMarkdown_Rows_TableWithSpecColumns`; `Aggregate_Empty_NoRows`.
- `ReachabilityCheckTests`: `[Theory] ClassifyGit_StderrPatterns` (`Authentication failed` → `AuthFailed`; `could not read Username` → `AuthFailed`; `Permission denied (publickey)` → `AuthFailed`; `Could not resolve host` / `Failed to connect` / `Connection timed out` / `Connection refused` / `Network is unreachable` / `407` → `Unreachable`; `SSL certificate problem` / `schannel:` → `TlsFailed`; exit 0 → `Ok`; exit null → `Unreachable`); `[Theory] ClassifyApi_HeadProbe` (`Changed`/`Unchanged` → `Ok`; `Unreachable("unauthorized")` → `AuthFailed`; `Unreachable("no github-pat …")` → `AuthFailed` with detail `no token`; `Unreachable("HttpRequestException")` → `Unreachable`; `AuthenticationException` detail → `TlsFailed`); `Redact_UrlWithUserinfo_Stripped`; `Redact_LongStderr_First300Chars`; `ProxyDisplay_WithUserinfo_HostPortOnly`; `ProxyDisplay_Null_None`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ `node.json` keys (ex-OQ5, amendment A6) and the §10 refusal rule; public API `Zyggy.Core.Configuration.*`, `Zyggy.Core.Bus.Probe.*`; the ledger file format (sender-side only, O21).

**VERIFY**: failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 12 — `zyggy secret set hmac/1 --hex` stores the key read from stdin under `<root>/acme/hmac/1` with 03's permissions and echoes only `stored acme/hmac/1 (32 bytes)` (a value on the command line is a usage error); `zyggy probe listen` with a `node.json` lacking `tenant`, with `tenant: Acme`, with a `bus.localPath` that is not a checkout or a checkout on `dev` exits 3 with one stderr line naming the condition before stashing or pulling anything; and the production composition `AddProcessRunner + AddFileSecretStore + AddEnvelopeSigning + AddGitHubBus` resolves provider, repository, listener and client

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/BusHost.cs` *(create, internal static)*: `LoadConfiguration(ParseResult, CliEnvironment) → (NodeConfiguration?, int exitCode)` (prints `zyggy: configuration error: <key>: <detail>` → exit 3); `BuildServices(NodeConfiguration, bool verbose, string version, CliEnvironment)` → `ServiceCollection().AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(verbose ? Debug : Information)).AddProcessRunner().AddFileSecretStore(o => o.RootDirectory = cfg.Secrets.Root).AddEnvelopeSigning().AddGitHubBus(o => { Remote/Identity/CheckoutRoot/Version from cfg; ApiBaseUri from ZYGGY_BUS_API_BASE when set (**Assumption A3**); CommitIdentity = ("zyggy (<machine>)", "<machine>@zyggy.org") })`; the common `--config` and `--verbose` options.
- `src/Zyggy.Cli/Commands/SecretCommand.cs` *(create)*: `zyggy secret set <name> [--hex]` — no value argument exists (System.CommandLine → exit 2 on an extra token); reads stdin to end, strips one trailing `\n`/`\r\n`, `--hex` → `Convert.FromHexString` (invalid → exit 2 `value is not hexadecimal`), empty → exit 2; `ISecretStore.SetAsync(cfg.Tenant, SecretName.Parse(name), bytes)`; prints `stored <tenant>/<name> (<n> bytes)`; exit 3 on configuration (incl. an invalid `SecretName`).
- `src/Zyggy.Cli/Commands/ProbeCommand.cs` *(create)*: the `probe` command with `listen` only in this step — `BusHost.LoadConfiguration` (requires `signWith` → else exit 3 `signWith missing`); builds services; `BusListener.StartAsync` → `WrongBranch` → stderr `checkout is on '<branch>', expected '<cfg branch>'`, `NotARepository` → `bus.localPath is not a git repository`, `NoTenantDirectory` → `no tenants/<tenant>/ directory in bus.localPath`, `RebaseInProgress`, `KeyMissing` → exit 6 (spec table: `StartAsync ≠ Clean/Stashed` → 6; **the configuration-shaped conditions of AC-27 exit 3** — the CLI checks `Directory.Exists(localPath/.git)` and the branch via `GitClient` **before** `StartAsync` to honour AC-27's "nothing stashed, nothing pulled" and exit 3); `Clean`/`Stashed` → `RunAsync(Active, ct)` until Ctrl+C → exit 0; one stdout line per handled file (`<id> claimed`, `<id> reported <ulid>`, `<id> rejected <reason>`).
- `src/Zyggy.Cli/CliApplication.cs` *(modify: `ProbeCommand.Create(environment)`, `SecretCommand.Create(environment)` added to the root)*.
- `tests/Zyggy.Integration/Cli/SecretSetCommandTests.cs`, `ProbeListenStartupTests.cs`, `tests/Zyggy.Integration/Bus/GitHubBusRegistrationTests.cs` *(create)*; `tests/Zyggy.Integration/Infrastructure/NodeJson.cs` *(create: writes a test `node.json` for `(tenant, machine, signWith, bus clone dir, remote, poll seconds, secrets root)` into a scratch dir)*.

**Seams**: wired end to end through the production DI in a child process (`ZyggyCli.RunAsync`); `FileSecretStore` on a temp root; real `git` for the branch check.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SecretSetCommandTests|FullyQualifiedName~ProbeListenStartupTests|FullyQualifiedName~GitHubBusRegistrationTests"`):
- AC-31 `SecretSet_HexFromStdin_FileHoldsHexAndStdoutNamesSize` (stdin = hex of `TestKeys.Secret1` + `\n`; file `<root>/acme/hmac/1` == `<hex>\n`; stdout `stored acme/hmac/1 (32 bytes)\n`; stderr empty; the value nowhere in stdout/stderr); `SecretSet_RawFromStdin_HexEncoded` (`github-pat` with `TestPat` → file holds its hex); `SecretSet_ValueAsArgument_ExitTwo`; `SecretSet_InvalidHex_ExitTwo`; `SecretSet_NoNodeJson_ExitThree`; Linux: `SecretSet_File_Is0600` (`SkipUnless = IsLinux`, as 03).
- AC-27 `[Theory] ProbeListen_BadConfiguration_ExitThreeOneStderrLineNothingTouched` — `tenant` missing → `tenant missing`; `tenant: Acme` → `tenant invalid`; `bus.localPath` = an empty temp dir → `bus.localPath is not a git repository`; the clone switched to `dev` → `checkout is on 'dev', expected 'main'`; for each: exit 3, exactly one stderr line containing the text, `git stash list` empty, bare `main` unchanged, the clone's `FETCH_HEAD` absent/unchanged; `ProbeListen_SignWithMissing_ExitThree`; `ProbeListen_KeyNotInStore_ExitSix` (`hmac/1` absent → `StartAsync` `KeyMissing`).
- AC-32 `GitHubBusRegistrationTests` (integration, in process): `ProductionComposition_ResolvesProviderRepositoryListenerClient` (`IBusProvider` is `GitHubBusProvider`, singleton; `BusRepository`, `BusListener` (with a registered `JobHandler` and `KeyId`), `GitClient` resolve); `AddGitHubBus_WithoutSecretStore_ThrowsOnResolve`; `ExistingDreamRegistration_StillResolves` (`AddZyggyDream` composition from `DreamCliTests`' arrangement still builds).

**GREEN**: as Scope.

**Contract impact**: ⚠️ secrets verb pulled forward from 09 (ex-OQ2): value only via stdin, never argv; ⚠️ public composition root `AddGitHubBus` used by 07/08; CLI exit codes 0/2/3/6 per `ExitCodes`.

**VERIFY**: failing-run command passes; `zyggy --help` lists `probe` and `secret`; `zyggy secret set hmac/1 somevalue` → exit 2; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — Slice E, first half (configuration, the secrets verb, the composition root) *(covers Steps 11–12)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `NodeConfigurationReaderTests` (the refusal theory), `ProbeLedgerTests`, `ProbeStatsTests`, `ReachabilityCheckTests`; by hand on the dev machine: a scratch `node.json` + `echo <hex> | zyggy secret set hmac/1 --hex` → `stored acme/hmac/1 (32 bytes)`, `icacls` shows the single-entry DACL; `zyggy probe listen --config <bad>` → exit 3 lines as AC-27.
- [x] Contract review: AC-27, AC-30, AC-31, AC-32 against the spec; the `node.json` minimal schema and precedence (**Assumption A5**: `~/.config/zyggy/node.json` on Linux although `LocalApplicationData` maps to `~/.local/share` — the spec's words win); the ledger JSON field names; the `reach` classification table.
- [x] ⚠️ Risk review (secrets, public API): the secret value never appears in argv, stdout, stderr or a log (`SecretSweep` over the CLI runs); `FileSecretStore` permissions as 03; `ZYGGY_BUS_API_BASE` is a documented test hook that only changes the API host, never a credential (**Assumption A3**); new public types XML-documented; no package.
- [x] User approved — implementation may continue past this gate

---

## Step 13 — From the built `zyggy` binary: `probe send --to home-laptop --timeout 5` with no listener pushes a signed probe job onto `main`, records `outcome: timeout` in the ledger and exits 6; with a `zyggy probe listen` process started by the test on a second `node.json` for `home-laptop` against the same bare repository (the API doorbell answered by a loopback stand-in), `send` observes the verified report, prints the round trip, records `outcome: done` with `roundTripMs > 0` and exits 0; `probe stats` prints one row for `home-laptop` (count 2, done 1, timeout 1, p50/p95) and exits 1 on an empty ledger; the test PAT and HMAC key appear nowhere

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/Probe/ProbeSender.cs` *(create, public sealed class `(IBusProvider provider, BusRepository bus, EnvelopeSigner signer, BusIdentity me, KeyId signWith, PollOptions options, TimeProvider clock, Random jitter, ProbeLedger ledger, ILogger<ProbeSender> log)`)*: `SendOneAsync(MachineName to, TimeSpan timeout, ct) → ProbeRecord` — build `Envelope.CreateJob(header(me.Tenant, EnvelopeId.New(now), me.Machine, to, now), new JobFields("probe", TimeoutMinutes: 2), "zyggy transport probe\n")`, `SignAsync`, `bus.SubmitAsync` (`Deferred`/`WriteFailed` → `push_failed`); then a sender-side loop with its own `PollState` (`Active`): wait until due, `ProbeHeadAsync`, `Apply`, on `Pull` → `PullAsync` → `bus.FindReportsAsync(to, id)` → first verified report → `done` (`roundTripMs = observed − sent`) or `rejected <reason>` when `status: rejected`; the timeout elapsed → `timeout`; each record appended to the ledger. `SendManyAsync(to, count, interval, timeout, ct)`.
- `src/Zyggy.Cli/Commands/ProbeCommand.cs` *(modify)*: `send --to <machine> [--count 1] [--interval 30] [--timeout 120] [--ledger <path>]` (requires `signWith`; one stdout line per probe `<id> <to> done <ms> ms` / `rejected <reason>` / `timeout` / `push_failed`; summary `sent N, done D, rejected R, timeout T, push_failed P`; exit 0 all done, 6 otherwise); `stats [--to <machine>] [--ledger <path>]` (Markdown table; exit 1 `no probes` on an empty ledger).
- `tests/Zyggy.Integration/Infrastructure/FakeGitHubApi.cs` *(create: a loopback `HttpListener` answering the §5 request from the bare repository's `rev-parse main` — `200` + `ETag` or `304`; also scriptable to `connection refused` (not listening) for Step 14; records requests incl. the `Authorization` header for the sweep)*; `tests/Zyggy.Core.Tests/Bus/Probe/ProbeSenderTests.cs` *(create — unit over substitutes: `SendOneAsync_ReportObserved_Done`, `SendOneAsync_NoReportBeforeTimeout_Timeout`, `SendOneAsync_SubmitDeferred_PushFailed`, `SendOneAsync_RejectedReport_RejectedWithReason`, `SendOneAsync_JobFieldsFixed` (`project: probe`, `timeout_minutes: 2`, body text))*; `tests/Zyggy.Integration/Cli/ProbeRoundTripTests.cs` *(create)*.

**Seams**: wired end to end out of process (`ZyggyCli.RunAsync` / `ZyggyCli.Start` for the listener, killed in `finally`); the only stand-in is `FakeGitHubApi` for `api.github.com` (`ZYGGY_BUS_API_BASE`); `FileSecretStore` temp roots seeded with `acme/1` and the test PAT via `zyggy secret set`; two `node.json` files (`central` on the sender clone, `home-laptop` on the fixture clone, `poll.activeSeconds: 1`).

**RED** (`dotnet test Zyggy.slnx --filter "FullyQualifiedName~ProbeSenderTests|FullyQualifiedName~ProbeRoundTripTests"`):
- AC-26: `Send_NoListener_ExitSixJobOnMainLedgerTimeout` (job at `tenants/acme/nodes/home-laptop/jobs/<id>.md` on `main`; ledger line `"outcome":"timeout"`; stdout contains `<id> home-laptop timeout`); `Send_WithListener_ExitZeroLedgerDoneRoundTripPositive` (the listener process started first and seen polling — `FakeGitHubApi` received ≥ 1 request; then `send`; report on `main`; stdout `… done <ms> ms`; `roundTripMs > 0`; the listener's stdout shows `claimed`/`reported` lines after it is stopped); `Stats_AfterBoth_OneRowCountTwoDoneOneTimeoutOne` (p50/p95 in ms present); `Stats_EmptyLedger_ExitOneNoProbes`; `Send_CountTwoOneTimeout_ContinuesSummaryExitSix` (spec Edge Cases: the listener stopped between the two probes).
- AC-29: `RoundTrip_NoSecretAnywhere` — `SecretSweep.AssertAbsent` over both processes' stdout/stderr, every `FakeGitHubApi` request **except** its `Authorization` header (which must equal `Bearer <TestPat>` — the one allowed place), the two `node.json` files, and `git log -p main` of the bare repository; plus `GitConfig_NeverWrittenByZyggy` (`git config --list --show-origin` of both clones identical before and after).

**GREEN**: as Scope.

**Contract impact**: ⚠️ the probe job shape (`project: probe`, `timeout_minutes: 2`, fixed body — owner-visible, never work data); sender-side latency only (O21); CLI surface per the spec table.

**VERIFY**: failing-run command passes within 90 s; build/test/format green; the publish + smoke step of CI still passes locally for `win-x64` (`dotnet publish src/Zyggy.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/win-x64; & artifacts/win-x64/zyggy.exe probe --help` → exit 0).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 14 — `zyggy probe reach` prints the four-row matrix (`api`, `fetch`, `push`, `ssh`) with `check | endpoint | result | detail | proxy` and a header naming git's `http.proxy`, `http.sslBackend`, `credential.helper` (userinfo stripped) and the system proxy for the API host; against the fixture `fetch` and `push` are `ok` with the bare repository unchanged by the dry-run push, `api` is `ok` when the loopback stand-in answers and `unreachable (connection refused)` when nothing listens, `ssh` is `skipped` or a result that never changes the exit code, and the exit is 0 only when `api`, `fetch` and `push` are `ok`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/Probe/ReachabilityCheck.cs` *(modify: the runner — `public sealed class ReachabilityCheck(IBusProvider provider, GitClient git, string checkoutRoot, BusRemote remote, ILogger<ReachabilityCheck> log)` with `RunAsync(ct) → ReachReport(IReadOnlyList<ReachResult> Rows, string GitProxy, string GitSslBackend, string CredentialHelper, string SystemProxy)`: `api` = `ClassifyApi(await provider.ProbeHeadAsync(null))`, endpoint `remote.HttpsUrl.Host` (the API host is the provider's business; the row shows the provider's `Remote` and the result); `fetch` = `git.LsRemoteAsync(root, remote.HttpsUrl, branch, env: none)`; `push` = `git.PushDryRunAsync(root, remote.HttpsUrl, "HEAD:refs/heads/<branch>")`; `ssh` = `remote.SshUrl is null → Skipped("no ssh remote configured")` else `LsRemoteAsync(root, SshUrl, branch, { GIT_SSH_COMMAND = "ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new" })`; `proxy` column = `ProxyDisplay(HttpClient.DefaultProxy.GetProxy(remote.HttpsUrl))` for `api`, `ConfigGetAsync("http.proxy")` redacted for the git rows; header values from `ConfigGetAsync`; each check with a 30 s timeout; `ExitCode` = 0 when `api`, `fetch`, `push` are `Ok`, else 6; `RenderMarkdown()`)*; `src/Zyggy.Core/Bus/GitHubBusProvider.cs` *(modify: `Remote` built from `GitHubBusOptions`: `HttpsUrl = https://github.com/<owner>/<repo>.git` unless the configured `bus.remote` is HTTPS (then that), `SshUrl = git@github.com:<owner>/<repo>.git` or the configured SSH remote — the only file naming `github.com` (AC-2); for the fixture the configured `bus.remote` is the bare `file://` URL, which the provider passes through as `HttpsUrl` when its scheme is not `https` — documented test accommodation, **Assumption A6**)*.
- `src/Zyggy.Cli/Commands/ProbeCommand.cs` *(modify: `reach` — `signWith` not required; prints the header lines then the table; exit per `ReachReport.ExitCode`)*.
- `tests/Zyggy.Core.Tests/Bus/Probe/ReachabilityCheckTests.cs` *(modify: the runner over substitutes/recorder — `RunAsync_BuildsFourRowsInSpecOrder`, `RunAsync_SshNotConfigured_SkippedExitUnaffected`, `RunAsync_SshFails_ExitStillZeroWhenOthersOk`, `RunAsync_FetchAuthFailed_ExitSix`, `RunAsync_GitEnvForSsh_ExactBatchModeCommand`, `RunAsync_NeverPassesCredentialToGit` (no `-c credential.*`, `http.extraHeader`, userinfo, `GIT_ASKPASS` in any recorded spec))*; `tests/Zyggy.Integration/Cli/ProbeReachTests.cs` *(create)*.

**Seams**: `IBusProvider` wired; `IProcessRunner` wired (real `git` `ls-remote`/`push --dry-run` against the bare `file://` remote); HTTP to the loopback `FakeGitHubApi` or to a closed port.

**RED** (`dotnet test Zyggy.slnx --filter "FullyQualifiedName~ReachabilityCheckTests|FullyQualifiedName~ProbeReachTests"`):
- AC-28: `Reach_ApiAnswers_FourRowsFetchPushOkExitZeroBareUnchanged` (`rev-parse main` identical before/after; rows `api`, `fetch`, `push`, `ssh` in order; `ssh` = `skipped (no ssh remote configured)`; columns exactly `check | endpoint | result | detail | proxy`; the header lines name `http.proxy`, `http.sslBackend`, `credential.helper` as `none`/the fixture's value); `Reach_ApiRefused_ApiUnreachableConnectionRefusedExitSix` (`ZYGGY_BUS_API_BASE` on a closed loopback port); `Reach_NoPatInStore_ApiAuthFailedNoToken`; `Reach_ProxyColumn_HostPortOrNone` (set `HTTPS_PROXY=http://user:pw@proxy.local:3128` in the child's environment → the `api` row's `proxy` is `proxy.local:3128`, the git rows `none`; stdout never contains `pw`); `Reach_SshRemoteConfiguredButUnreachable_RowUnreachableExitZero` (an `ssh://127.0.0.1:1/x.git` remote → `unreachable`, exit still 0); `Reach_NeverWritesGitConfig` (both clones' `config --list` unchanged); `SecretSweep` over stdout/stderr.

**GREEN**: as Scope.

**Contract impact**: ⚠️ the §5/§8 rule "Zyggy never carries the git credential and never writes `git config`" asserted (AC-29); `probe reach` is side-effect-free on the remote (spec Behaviors).

**VERIFY**: failing-run command passes; build/test/format green; publish + smoke for `linux-x64` as well (`dotnet publish src/Zyggy.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o artifacts/linux-x64` succeeds locally; the smoke run is CI's on `ubuntu-latest`).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice E (the `zyggy probe` verbs work end to end from the built binary) *(covers Steps 13–14)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `ProbeSenderTests`, `ProbeRoundTripTests` (paste the `send` stdout, the two ledger lines, the `stats` table), `ProbeReachTests` (paste one `reach` table); by hand on the dev machine against a scratch bare repository: `zyggy probe listen` in one terminal, `zyggy probe send --to home-laptop` in another, `zyggy probe stats`, `zyggy probe reach`; CI green on both runners with the publish + smoke step (AC-38).
- [x] Contract review: AC-26, AC-28, AC-29 against the spec; the CLI table (options, output lines, exit codes); the probe job fields; the `reach` rows, header and exit rule; `Remote` URL derivation (**Assumption A6** — `file://` pass-through for tests); `GitHubBusProvider` is still the only file naming `github.com`.
- [x] ⚠️ Risk review (PAT never reaches git; secrets; work boundary; proxy): `RunAsync_NeverPassesCredentialToGit` and the integration sweep green; `HTTPS_PROXY` userinfo never printed; `probe reach` left the bare repository unchanged; the probe body is the fixed string; no package; `Directory.Packages.props` unchanged.
- [x] User approved — implementation may continue past this gate

---

## Step 15 — The documents say how to run and how to recover: `src/Zyggy.Core/README.md` gets the `Bus/` folder row corrected (spec finding 3: `GitClient` lives in `Git/`) and a "Troubleshooting — Bus" section with the spec's Failure-modes entries (dirty working copy, checkout, push deferred, pull failed, rate limit, rejected envelopes) naming the founding-spec amendments the code implements; `src/Zyggy.Cli/README.md` gets the `probe` and `secret` verbs with exit codes, the `node.json` minimal schema, the per-machine setup (key seeding, PAT, Git Credential Manager, proxy and TLS settings pass) and the "Probe:" troubleshooting entries — the `bus-conflict.md` draft and the probe runbook; the owner (or the orchestrating session at his instruction) pastes the founding-spec amendments A1–A6 and the agent confirms them present

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(agent for the READMEs; **owner-run** for the founding spec)*:
- `src/Zyggy.Core/README.md` *(modify)*: Responsibilities table — `Bus/` row = `BusPaths`, `IBusProvider`/`GitHubBusProvider`, `PollState`, `BusRepository`, `BusListener`, `Probe/`; a `Git/` row = `GitClient` (28) with the 04 additions; a `Configuration/` row; new section `## Troubleshooting — Bus` with one table row per spec Failure-modes entry marked "Core README" (`Bus: dirty working copy`, `Bus: checkout`, `Bus: push deferred`, `Bus: pull failed`, `Bus: rate limit`, `Bus: rejected envelopes`), each naming the symptom, the command to inspect (`git stash list`, `git log origin/main..HEAD`, `git rebase --abort`, `probe reach`) and the amendment it implements (A1, A3/A4, A5) — this is the `bus-conflict.md` draft (§11) until the runbook directory exists (10).
- `src/Zyggy.Cli/README.md` *(modify)*: Verbs table gains `probe send|listen|stats|reach` and `secret set` (used by, what it does, exit codes 0/1/2/3/6); new sections `## node.json (minimal, deliverable 04)` (the spec's JSON with `<tenant>`/`<machine>` placeholders and the key table, the location precedence, the secret-key refusal), `## Per-machine setup for the transport proof` (Central: deploy key over SSH `Host github.com-zyggy-bus`, `zyggy secret set hmac/1 --hex`, `zyggy secret set github-pat`; laptops: `win-x64` binary copied by hand, Git for Windows with Git Credential Manager, the PAT stored by GCM at the first HTTPS push, the same two `secret set` calls; the one documented git settings pass after a failing `reach`: `git config --global http.proxy <host:port>`, `git config --global http.sslBackend schannel`, `git config --global credential.helper manager` — owner's settings, never written by Zyggy), `## Troubleshooting — Probe` (`Probe: proxy and TLS`, `Probe: PAT`, `Probe: keys`, `Probe: git through the proxy`, `Probe: no report`, `node.json`).
- **Owner-run**: the orchestrating session pastes A1–A6 of the spec ("Founding-spec amendments to apply") into `_specs/00 …` with the owner's approval, listing every edit in its journal; the agent then checks read-only that the six replacement texts are present (`Select-String` for one distinctive phrase per amendment: `wrong_recipient` in §4, `a `rejected` report carries one of the §4 rejection reasons` in §9, `stays a local, unpushed commit` in §4 row 2, `<action>` in §5, `the later of 120 s` in §5 item 4, `"signWith"` in §10) and reports present/absent — **never pastes itself**.

**Seams**: none.

**RED**: `Select-String -Path src/Zyggy.Core/README.md -Pattern 'Troubleshooting — Bus'` → 0 hits; `Select-String -Path src/Zyggy.Cli/README.md -Pattern 'probe reach'` → 0 hits; the six founding-spec phrases absent.

**GREEN**: as Scope; `SourceHygieneTests` stays green (READMEs are not `.cs`, but the README examples use `<tenant>` placeholders anyway — public-repository hygiene).

**Contract impact**: documents only; AC-37's first two clauses and the amendments clause.

**VERIFY**: both READMEs render (no broken table); the six phrases present (or the gate records which are not); `dotnet format Zyggy.slnx --verify-no-changes` green (no code change).

**REFACTOR** *(executor)*: none.

---

## 🛑 HUMAN GATE — end of Slice F (documents and the founding-spec amendments) *(covers Step 15)*

*Executor: STOP here. Present the results and WAIT for user approval — do not start Step 16.*

- [x] Behavioral verification: both README diffs; the six-phrase check output for `_specs/00 …`.
- [x] Contract review: AC-37 (first two clauses + amendments) against the spec's Failure-modes table; every "Core README"/"Cli README" entry of that table present; the per-machine setup matches the spec's Context table and the `add-tenant.md` steps; A1–A6 present or explicitly deferred by the owner (A7 has no 04 text).
- [x] ⚠️ Risk review (public repository): no company name, token, personal data or real hostname in the READMEs beyond the `zyggy-org`/`zyggy-bus` names already public in the spec; placeholders for tenant and machine.
- [x] User approved — implementation may continue past this gate

---

## Step 16 — **Owner-run.** The tenant's bus repository exists the `add-tenant.md` way: private `zyggy-org/zyggy-bus` with force-push and deletion blocked on `main`, a `PROTOCOL.md` placeholder and the three machines' folders committed, three fine-grained PATs scoped to that one repository, one write deploy key on Central — and no secret value in any repository or in 0001

- [ ] Done *(checked by the executor when the owner reports the items below; evidence rows in 0001)*

**Scope** *(owner at the keyboard; the agent prepares the exact text to commit and records the evidence)*:
1. Repository: `gh repo create zyggy-org/zyggy-bus --private --description "Zyggy bus (tenant transport)"` (or the GitHub UI). Ruleset (UI: *Settings → Rules → Rulesets → New branch ruleset*): name `main`, target `main`, enforcement *Active*, rules **Block force pushes** and **Restrict deletions** ticked, no required reviews.
2. Seed commit from the owner's machine (`<tenant>` = the live tenant label of the spec's `node.json` example):
   ```powershell
   git clone https://github.com/zyggy-org/zyggy-bus.git; cd zyggy-bus
   Set-Content PROTOCOL.md "Mirrors §4 of the Zyggy technical specification; the verbatim copy lands in deliverable 15.`n"
   foreach ($m in 'central','home-laptop','work-laptop') { foreach ($d in 'jobs','jobs/claimed','jobs/rejected','reports','context') { New-Item -ItemType Directory -Force "tenants/<tenant>/nodes/$m/$d" | Out-Null; New-Item -ItemType File -Force "tenants/<tenant>/nodes/$m/$d/.gitkeep" | Out-Null } }
   New-Item -ItemType Directory -Force tenants/<tenant>/registry, tenants/<tenant>/archive | Out-Null
   New-Item -ItemType File -Force tenants/<tenant>/registry/.gitkeep, tenants/<tenant>/archive/.gitkeep | Out-Null
   git add -A; git commit -m "<tenant> seed"; git push
   ```
   (The agent may generate this tree locally with `BusPaths` to compare against the owner's `git ls-tree -r --name-only main` — read-only check.)
3. PATs (UI: *Settings → Developer settings → Fine-grained tokens*; resource owner `zyggy-org` — the organisation must allow fine-grained PATs): `central-bus` (Repository access: only `zyggy-bus`; Contents: **Read**), `home-laptop-bus` (Contents: **Read and write**), `work-laptop-bus` (Contents: **Read and write**); expiry 90 days, the date noted in 0001. Each token is pasted **only** into `zyggy secret set github-pat` on its machine (Step 18) and, on the laptops, into Git Credential Manager at the first HTTPS push prompt — never into a file, a chat or 0001.
4. Deploy key on Central (over Tailscale SSH, as the memory repository's key in runbook 27/28):
   ```bash
   sudo -u zyggy ssh-keygen -t ed25519 -f /home/zyggy/.ssh/zyggy_zyggy-bus_ed25519 -N '' -C 'zyggy central bus'
   sudo -u zyggy cat /home/zyggy/.ssh/zyggy_zyggy-bus_ed25519.pub     # → GitHub zyggy-bus → Settings → Deploy keys → Add, "Allow write access" ticked
   sudo -u zyggy tee -a /home/zyggy/.ssh/config <<'EOF'
   Host github.com-zyggy-bus
     HostName github.com
     User git
     IdentityFile ~/.ssh/zyggy_zyggy-bus_ed25519
     IdentitiesOnly yes
   EOF
   sudo -u zyggy ssh -T git@github.com-zyggy-bus      # expects "successfully authenticated"
   ```
5. 0001: a "Bus repository" paragraph — repository name, ruleset rules, seed commit SHA, the three PAT **names** and expiry date, the deploy-key file name. No value.

**Seams**: none (GitHub UI and the owner's git).

**RED**: `gh repo view zyggy-org/zyggy-bus` → not found; 0001 has no "Bus repository" paragraph.

**GREEN**: the items above; the agent fills the 0001 paragraph from the owner's pasted output.

**Contract impact**: ⚠️ a repository and three credentials created by hand (spec Risk Areas); §5 "Credentials per machine" with the ex-OQ3 reshape (laptops: PAT over HTTPS via GCM).

**VERIFY**: `gh api repos/zyggy-org/zyggy-bus --jq .private` → `true`; `gh api repos/zyggy-org/zyggy-bus/rulesets` lists the ruleset; `git ls-tree -r --name-only main` equals the `BusPaths`-generated list + `PROTOCOL.md`; `grep -c -E 'ghp_|github_pat_' _plans/decisions/0001-transport-and-vm.md` → 0.

**REFACTOR** *(executor)*: none.

---

## Step 17 — A tagged release exists with checksummed `win-x64` and `linux-x64` single-file binaries; on Central it is installed root-owned as a new pinned version under `/opt/zyggy/<v>/` (instance pin commit, template pulled, nothing restarted — the probe is a foreground process), and **the owner** copies the `win-x64` file from the CI artifact onto the home laptop and the work laptop

- [ ] Done *(checked by the executor when VERIFY passes; the laptop copies are owner-reported)*

**Scope** *(agent for release and Central per 39 Step 7 / 38 Step 12; owner for the laptops)*:
- This repo: merge Steps 1–15 (PR, CI green on `windows-latest` and `ubuntu-latest`, publish + smoke green for both RIDs — AC-38); tag `v<next>` (**Assumption A1**: `v0.8.0`, the next minor after 0.7.0 — public API added); tag CI green; release with `zyggy` (linux-x64), `zyggy.exe` (win-x64) and `SHA256SUMS`; `sha256sum -c` locally.
- Central (`az vm run-command` + `scp` over Tailscale; as 39 Step 7): `/opt/zyggy/<v>/zyggy` root:root 755, hash = `SHA256SUMS`; symlink `/usr/local/bin/zyggy` → `<v>`; `zyggy --version`; instance commit `instance/zyggy.json` → `<v>` (the pin is read by the dream verbs; the probe verbs do not check it), instance CI/bats green, pulled on Central (`runuser -u zyggy -- git -C /srv/agent/central pull --ff-only`); **no unit restarted** (the dream timer, the offload unit and `claude-remote` untouched — `systemctl list-timers` and `systemctl status claude-remote` unchanged). Rehearsal as `zyggy`: `runuser -u zyggy -- zyggy probe --help` → exit 0; `runuser -u zyggy -- zyggy dream status` → as before.
- Owner (laptops): download `zyggy.exe` from the release (or the CI artifact), verify `Get-FileHash zyggy.exe -Algorithm SHA256` against `SHA256SUMS`, place it at `%LOCALAPPDATA%\zyggy\bin\zyggy.exe` and add that folder to the user PATH (`[Environment]::SetEnvironmentVariable('Path', $env:Path + ';' + "$env:LOCALAPPDATA\zyggy\bin", 'User')`); `zyggy --version` → `<v>`. Same on the work laptop (owner-only; a blocked download → record it in 0001 and copy the file by a channel the owner trusts).
- 0001: "Release and install" rows (tag, CI run ids, SHA-256, Central path, the two laptop `--version` outputs).

**Seams**: the real Central (read-only agent access), the owner's laptops.

**RED**: `zyggy --version` on Central ≠ `<v>`; no `zyggy` on either laptop.

**GREEN**: as Scope.

**Contract impact**: ⚠️ live binary on Central (additive `GitClient` change under the dream/offload paths — the next nightly dream is the live proof, checked at Step 21).

**VERIFY**: symlink + pin = `<v>`; `claude-remote` active with 0 new restarts; `zyggy dream status` unchanged; the owner's two `--version` lines in 0001.

**REFACTOR** *(executor)*: none (operations step).

---

## Step 18 — **Owner-run.** On each of the three machines the probe is configured and the four GitHub paths are measured: `node.json` written, `zyggy-bus` cloned to `bus.localPath`, the same `hmac/1` seeded with `zyggy secret set hmac/1 --hex` and the machine's PAT with `zyggy secret set github-pat`, Git Credential Manager primed on the laptops, then `zyggy probe reach` run and its table pasted; on the work laptop a failing `fetch`/`push` gets the one documented git settings pass and a second `reach`; the 3 × 4 matrix with timestamps goes into 0001

- [ ] Done *(checked by the executor when the three tables are in 0001)*

**Scope** *(owner at each keyboard; the agent fills 0001 from pasted output and may run the Central part read-only via `az vm run-command` **except** the secret seeding, which the owner types)*:
1. HMAC key, generated once on the owner's machine: `openssl rand -hex 32` (Git for Windows ships `openssl.exe`); copied to the other two machines over a channel the owner trusts (never a repository, never a chat log); then on **every** machine: `zyggy secret set hmac/1 --hex` → paste the hex → Enter → `Ctrl+Z` Enter (Windows) / `Ctrl+D` (Linux) → `stored <tenant>/hmac/1 (32 bytes)`. On Central: `runuser -u zyggy -- zyggy secret set hmac/1 --hex --config /home/zyggy/.config/zyggy/node.json`.
2. PAT per machine: `zyggy secret set github-pat` → paste the machine's token → Enter → `Ctrl+Z`/`Ctrl+D` → `stored <tenant>/github-pat (<n> bytes)`.
3. `node.json` per machine (the spec's minimal schema; **Central**: `"machine": "central"`, `"signWith": "<tenant>/1"`, `"bus": { "owner": "zyggy-org", "repo": "zyggy-bus", "branch": "main", "localPath": "/srv/agent/bus", "remote": "git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git" }`, `"secrets": { "root": null }` at `/home/zyggy/.config/zyggy/node.json`, mode 600, owner `zyggy`; **laptops**: `"machine": "home-laptop"` / `"work-laptop"`, `"localPath": "C:/agents/bus"`, `"remote": "https://github.com/zyggy-org/zyggy-bus.git"` at `%LOCALAPPDATA%\zyggy\node.json`).
4. Clone: Central `runuser -u zyggy -- git clone git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git /srv/agent/bus`; laptops `git clone https://github.com/zyggy-org/zyggy-bus.git C:\agents\bus` — GCM prompts once in the browser (sign in with the owner's account; the PAT is entered as the password when prompted, or the browser flow is used — the owner's choice; the goal is that `git push` later needs no prompt). Prime the write path: `cd C:\agents\bus; git commit --allow-empty -m "<tenant> probe credential check"; git push; git reset --hard origin/main~0` — **or** skip the empty commit and rely on `probe reach`'s `push --dry-run`, which also exercises GCM (preferred: no noise commit).
5. `zyggy probe reach` on each machine (Central: `runuser -u zyggy -- zyggy probe reach`), the full output pasted to the agent with the local timestamp. Work laptop: inside the corporate network, on a working day.
6. Work laptop only, when `fetch` or `push` is not `ok` (ex-OQ7): one settings pass — `git config --global http.proxy <host:port as the proxy column of the api row shows>`, `git config --global http.sslBackend schannel`, `git config --global credential.helper manager` — then `zyggy probe reach` again; **both** tables go to 0001; a still-failing required check is recorded for 05, never worked around here.
7. 0001 transport section: the 3 × 4 matrix (rows per machine, cells `ok` / `auth failed` / `unreachable (<class>)` / `tls failed` / `skipped`), timestamps, the `proxy` column values (host:port only), the four checklist rows ticked or marked failed per machine (AC-33); the facts from ex-OQ6 (`304`s do not count; all PATs of one account share 5,000/h).

**Seams**: the real machines; nothing beyond GitHub endpoints is contacted (§8).

**RED**: 0001's four transport checklist rows unticked; no `node.json` on any machine.

**GREEN**: the items above; rows filled from the owner's pastes.

**Contract impact**: ⚠️ secrets on three machines (three PATs, three copies of `hmac/1`, one deploy key); ⚠️ first Zyggy code on the work laptop (fixed body, GitHub endpoints only, no `claude`, no work data); the per-machine auth path measured for 05 (A7).

**VERIFY**: three `reach` tables in 0001 with timestamps; `Select-String -Path _plans/decisions/0001-transport-and-vm.md -Pattern 'ghp_|github_pat_|[0-9a-f]{64}'` → 0 hits; on Central `ls -l /home/zyggy/.local/share/zyggy/secrets/<tenant>/` shows `hmac/1` and `github-pat` mode 600 (agent, read-only; names only).

**REFACTOR** *(executor)*: none.

---

## 🛑 HUMAN GATE — Slice G, first half (the bus repository, the binaries, the matrix) *(covers Steps 16–18)*

*Executor: STOP here. Present the results and WAIT for user approval — do not start Step 19.*

- [ ] Behavioral verification: the three `reach` tables and the ruleset screenshot or `gh api` output; `zyggy --version` on three machines; the 0001 paragraphs.
- [ ] Contract review: AC-33, AC-36, AC-38 (install clause) against the spec; the matrix cells use the spec's vocabulary; the work-laptop settings pass recorded as one pass with both tables (ex-OQ7); the per-machine auth path recorded for 05 (A7).
- [ ] ⚠️ Risk review (secrets on three machines, work boundary, hand-made repository): no token or key value anywhere in this repository, the instance repository or 0001 (sweep output shown); PATs repo-scoped with the right permission per machine; the deploy key file 0600; the work laptop ran only `zyggy probe reach` (GitHub endpoints) — if any required check failed there, the record goes to 05 and Step 20 is run as far as the failing path allows (owner decides here).
- [ ] User approved — implementation may continue past this gate

---

## Step 19 — **Owner-run.** From Central, ten probes reach the home laptop and come back: `zyggy probe listen` runs in the foreground on the home laptop, `zyggy probe send --to home-laptop --count 10 --interval 30` on Central shows ≥ 10 `done` lines, and `zyggy probe stats` (count, done, failures, p50, p95, min, max) is pasted into 0001 with timestamps

- [ ] Done *(checked by the executor when the stats table is in 0001)*

**Scope** *(owner; agent fills 0001)*:
- Home laptop: `zyggy probe listen` (leave running; stderr shows one line per state change, stdout one per handled file).
- Central: `runuser -u zyggy -- zyggy probe send --to home-laptop --count 10 --interval 30` (about 5–7 minutes); then `runuser -u zyggy -- zyggy probe stats --to home-laptop`; paste both outputs with the UTC time.
- Read-only agent checks (`az vm run-command`): `git -C /srv/agent/bus log --format='%h %s %an' origin/main -25` shows the `<tenant> job`, `<tenant> claim`, `<tenant> report` triplets; `tenants/<tenant>/nodes/home-laptop/jobs/` and `jobs/claimed/` empty on `origin/main`; `reports/` holds ten files.
- 0001: AC-34 rows — the `send` summary line, the `stats` table, the listener's `claim`/`report` line count, timestamps.

**Seams**: the real transport (GitHub), the real machines.

**RED**: 0001 AC-34 row empty; `tenants/<tenant>/nodes/home-laptop/reports/` empty on `origin/main`.

**GREEN**: rows filled; anything not met is reported at the gate with the record (a fix is a bugfix or a new plan step).

**Contract impact**: none (measurement).

**VERIFY**: ≥ 10 `done`; the stats table in 0001; `git log` triplets as described.

**REFACTOR** *(executor)*: none.

---

## Step 20 — **Owner-run, work laptop inside the corporate network on a working day.** `zyggy probe listen` runs in the foreground on the work laptop behind the real corporate proxy with HTTPS push through Git Credential Manager; Central sends `--count 10 --interval 30`; the number `done`, every failure's error class and the `stats` table go into 0001 with timestamps — a failing path is recorded for 05, not worked around

- [ ] Done *(checked by the executor when the record is in 0001, whatever the outcome)*

**Scope** *(owner on the work laptop and on Central; agent fills 0001 read-only)*:
- Work laptop: `zyggy probe listen` (foreground; the owner notes the first stderr lines — a `NetworkBackoff`/`Throttled` phase change names the class).
- Central: `runuser -u zyggy -- zyggy probe send --to work-laptop --count 10 --interval 30`; `runuser -u zyggy -- zyggy probe stats --to work-laptop`.
- 0001: AC-35 rows — attempted, `done`, each failure's class (`timeout` with the listener's last phase line, `push_failed`, `rejected <reason>`), the stats table, timestamps, the proxy host:port from `reach`, whether the git settings pass of Step 18 was applied. No workaround: a failing required path is the input to 05's candidate-C / `bus: disabled` evaluation.
- Agent read-only: `git log` triplets for `work-laptop`; `reports/` count on `origin/main`.

**Seams**: the real corporate proxy (owner-only access).

**RED**: 0001 AC-35 row empty.

**GREEN**: rows filled from the owner's pastes.

**Contract impact**: ⚠️ work-laptop trust boundary (§8): only signed `report` envelopes with the fixed text leave the machine; measurement bias noted (working day, inside the network).

**VERIFY**: the AC-35 rows present with ≥ 10 attempted (or the reason fewer were possible), the number `done`, failure classes, timestamps.

**REFACTOR** *(executor)*: none.

---

## Step 21 — The transport section of `_plans/decisions/0001-transport-and-vm.md` is complete for 05 (matrix, two probe series, per-machine auth path, the ex-OQ6 facts, the open fallback line), the first nightly dream on the new binary ran unchanged on Central (the additive `GitClient` change proven live), and `_plans/ROADMAP.md` #04 records the status with commits and CI run ids

- [ ] Done *(checked by the executor when VERIFY passes)*

**Scope** *(agent; read-only on Central)*:
- `_plans/decisions/0001-transport-and-vm.md` *(modify)*: transport section — the 3 × 4 matrix, AC-34 and AC-35 tables, the per-machine auth path actually used (Central SSH deploy key; laptops HTTPS + GCM) for 05's §5 table amendment (A7), the ex-OQ6 facts, "fallback: candidate C evaluated in 05 only if …" with the measured outcome, the release/install rows, the PAT expiry dates; status line unchanged (`closed by deliverable 05`).
- Central read-only (`az vm run-command`): `runuser -u zyggy -- zyggy dream status` after the first nightly run on `<v>` → `committed`/`partial`/`nothing` as the nights before, no `git_error`; `journalctl -u zyggy-dream --since <install>` has no `zyggy` error line; the offload unit as before.
- `_plans/ROADMAP.md` *(modify — status only, as 39 Step 9)*: #04 → Done or "Done with 05 inputs recorded" (date, PR, commits, CI run ids, tag); the DoD line already trimmed by the project-manager (ex-OQ4) noted; the three spec findings forwarded (bus-protocol skill text, integration-testing skill text, the Core README `Bus/` row now fixed).

**Seams**: none.

**RED**: 0001 transport section without the two series and the auth-path paragraph; ROADMAP #04 status `In progress`.

**GREEN**: as Scope.

**Contract impact**: none.

**VERIFY**: 0001 renders; every AC-33..AC-36 item has a dated, sourced row; `zyggy dream status` shows a run on `<v>`; ROADMAP #04 updated.

**REFACTOR** *(executor)*: none.

---

## 🛑 HUMAN GATE — end of Slice G — **definition of done for deliverable 04** *(covers Steps 19–21)*

*Executor: STOP here. Present the results and WAIT for user approval.*

- [ ] Behavioral verification: 0001 transport section complete (matrix, two `stats` tables, auth paths, fallback line); `git log origin/main` of `zyggy-bus` showing the probe triplets for both laptops; the first nightly dream on `<v>` recorded; CI green on both runners for the merged commit and the tag.
- [ ] Contract review: AC-1..AC-38 → step map below all ticked; `ROADMAP.md` #04 definition of done ticked item by item, noting the spec's reshapes (deferred commit instead of "uncommitted"; `<action>` in the commit message; `wrong_recipient`; no second-tenant refusal; `node.json` read by Core; `secret set` pulled forward; home laptop on HTTPS + GCM); the founding-spec amendments A1–A6 present; the inputs 05 needs listed (per-machine auth path, 304/shared-budget facts, the gate scenario to build from Steps 8–10's fixtures).
- [ ] ⚠️ Risk review: §4/§5 shared contracts now live on the bus (`PROTOCOL.md` is a placeholder until 15 — any later change is a protocol change); secrets: no value anywhere (sweeps shown), PAT expiry dates recorded; the work laptop ran only the probe with its fixed body; the proxy honoured by both clients (the `reach` `api` row and the `fetch`/`push` rows); rate limit: `304`s observed as free (the `x-ratelimit-remaining` numbers from `--verbose` if captured); measurement bias acknowledged (working day); the additive `GitClient` change proven by the nightly dream; the repository created by hand matches `add-tenant.md`.
- [ ] User approved — deliverable 04 is done

---

## Acceptance-criteria → step map

| AC | Steps | AC | Steps | AC | Steps |
|----|-------|----|-------|----|-------|
| AC-1 | 1 | AC-14 | 6 | AC-27 | 12 |
| AC-2 | 1 (guard for every later step) | AC-15 | 6 | AC-28 | 14 |
| AC-3 | 3 | AC-16 | 6, 7 | AC-29 | 4, 6–10, 12, 13, 14 (sweep helper) |
| AC-4 | 3 | AC-17 | 2 (fixture), 8 | AC-30 | 11 |
| AC-5 | 3 | AC-18 | 8 | AC-31 | 12 |
| AC-6 | 3 | AC-19 | 7 (U), 8 (I), 10 (via the loop) | AC-32 | 4 (U), 12 (I) |
| AC-7 | 3 | AC-20 | 7, 8, 10 | AC-33 | 18 |
| AC-8 | 4 | AC-21 | 8 | AC-34 | 19 |
| AC-9 | 4 | AC-22 | 8 (first half), 10 (recovery line) | AC-35 | 20 |
| AC-10 | 4 | AC-23 | 7, 8 | AC-36 | 16 |
| AC-11 | 4 | AC-24 | 5 | AC-37 | 15 (READMEs, amendments), 21 (0001) |
| AC-12 | 5 (`PullRebaseAsync`), 6 | AC-25 | 9 (U), 10 (I) | AC-38 | every gate (CI), 17 (install) |
| AC-13 | 6 | AC-26 | 13 | | |

---

## Assumptions (where the spec is silent, leaves a shape to the planner, or is inconsistent with itself; each is reviewed at the named gate)

1. **A1 *(Gate after Step 18)* — release number**: the next **minor** after the live 0.7.0 (`v0.8.0`): a whole public namespace is added.
2. **A2 *(Gate A)* — `BusRepoFixture` without a default tenant and xUnit**: `IClassFixture<T>` needs a parameterless constructor, so the sealed `AcmeBusRepoFixture : BusRepoFixture` (tenant `acme`) exists in the test project; the base keeps the required `TenantId` parameter and no parameterless constructor (`Fixture_RequiresATenant`). 01's `Fixture_PublicSurface_HasNoTenantMember` is replaced — the brief superseded it.
3. **A3 *(Gate B, Gate E-1)* — test hooks on `GitHubBusOptions`**: `ApiBaseUri` (default `https://api.github.com`, settable from `ZYGGY_BUS_API_BASE` by the CLI — the out-of-process stand-in of AC-26/AC-28's loopback `HttpListener`) and the `internal` `HandlerFactory` (in-process `BareRepoHeadHandler`). Neither carries a credential; both are documented as test-only. Alternative rejected: a second `IBusProvider` implementation in `src/` for tests (public-api.md forbids it).
4. **A4 *(Gate D)* — `BusStartupResult.KeyMissing`**: the spec's Failure-modes table says the key is checked in `StartAsync` and `listen` exits 6, but the enum lists no member for it; `KeyMissing` is appended. A handler exception in `ProcessOnceAsync` is logged `Error` and the job stays claimed (no new `reason`; 07/17 own the resume) — the probe handler never throws.
5. **A5 *(Gate E-1)* — `node.json` default path on Linux**: the spec says `<LocalApplicationData>/zyggy/node.json` **and** `~/.config/zyggy/node.json` on Linux; .NET maps `LocalApplicationData` to `~/.local/share`. The plan follows the words: `%LOCALAPPDATA%\zyggy\node.json` on Windows, `$XDG_CONFIG_HOME/zyggy/node.json` or `~/.config/zyggy/node.json` elsewhere (the directory Central already uses). Reported to the owner as a spec inconsistency (question 1 below).
6. **A6 *(Gate E-2)* — `BusRemote` URL derivation for a non-GitHub remote**: `GitHubBusProvider` derives `https://github.com/<owner>/<repo>.git` and `git@github.com:<owner>/<repo>.git` from `bus.owner`/`bus.repo`; when the configured `bus.remote` has a scheme other than `https`/`ssh`/`git@` (the fixture's `file://` bare repository), it is used verbatim as `HttpsUrl` and `SshUrl` is null — so `reach`'s `fetch`/`push` rows can run against the fixture (AC-28). Only `GitHubBusProvider` names `github.com` (AC-2).
7. **A7 *(Gate C)* — AC-27's exit 3 for a bad checkout**: the spec routes `WrongBranch`/`NotARepository` through `BusStartupResult` (`listen` exit 6 for `StartAsync ≠ Clean/Stashed`) **and** requires exit 3 "configuration" for the same conditions in AC-27. The CLI checks `.git` presence and the branch (through `GitClient`, no stash, no pull) before `StartAsync` and exits 3; `StartAsync`'s own `WrongBranch`/`NotARepository` remain reachable only by a race and exit 6.
8. **A8 *(Gate C)* — a non-ULID file name in `jobs/`**: listed and rejected as `malformed` (move only), its `BusFile.Id` synthetic for logging; the spec's AC-20 covers content, not names.
9. **A9 *(Gate E-2)* — `probe send` observes reports with its own `PollState` in `Active` mode** on the sender's `node.json` intervals; the measured round trip therefore includes up to one sender interval (spec Edge Cases; 05 interprets).

## Questions for the owner (answered at the named gate; the plan proceeds with the assumption marked)

> **Answered by the owner on 2026-10-12, before Step 1** (asked one by one in chat): 1 → `~/.config/zyggy/node.json` on Linux (A5 confirmed); 2 → pre-check in the CLI, exit 3 (A7 confirmed); 3 → yes, `BusStartupResult.KeyMissing` is added (A4 confirmed); 4 → prime Git Credential Manager with `probe reach`'s `push --dry-run`, no empty commit. **Plan approved by the owner the same day ("Approve, start building"); the founding-spec amendments A1–A6 were pasted by the orchestrating session with the owner's approval ("Paste them now") and listed in the session journal.**

1. *(Gate E-1, A5)* On Linux, is the default `node.json` location `~/.config/zyggy/node.json` (the spec's words and Central's existing directory) or `~/.local/share/zyggy/node.json` (what `LocalApplicationData` means in .NET)? The plan uses `~/.config/zyggy/node.json`.
2. *(Gate C, A7)* For a checkout on the wrong branch or a non-repository `bus.localPath`, is exit 3 (AC-27) the only acceptable code, so the CLI may pre-check before `StartAsync`? The plan does so.
3. *(Gate D, A4)* May `BusStartupResult` gain `KeyMissing` (public enum, additive) for the "hmac key absent at start" case the spec's Failure-modes table describes?
4. *(Gate after Step 18)* Does the owner prefer priming Git Credential Manager through `probe reach`'s `push --dry-run` (no commit on the bus) rather than an empty commit? The plan prefers the dry run.

## Notes for the executor

1. **Branch discipline.** Steps 1–15 on `feature/04-transport-a-proxy-proof`; the owner reviews before commit (auto-memory): leave changes uncommitted and report at each gate unless the owner says otherwise; never push; nothing is merged, tagged or installed before Step 17.
2. **Shared-contract edits** (`EnvelopeRejectionReason`, `EnvelopeWire`, `EnvelopeParser`, `GitClient`, `BusRepoFixture`): run the whole existing suite before the edit; after GREEN, `git diff` of `tests/golden/`, `tests/Zyggy.Core.Tests/Dream`, `tests/Zyggy.Integration/Dream`, `tests/Zyggy.Integration/Memory` must be empty, and `BusRepoFixtureSmokeTests` may change only as Step 2 says.
3. **No package.** `Directory.Packages.props` must not change; `SocketsHttpHandler`, `System.Text.Json` source generation, `HttpListener` (tests) are shared-framework. A temptation towards `Microsoft.Extensions.Http`, `Octokit` or `Polly` is a plan change — stop at the gate.
4. **Never the real GitHub in a test**; `FakeGitHubApi`/`BareRepoHeadHandler` stand in; every integration test asserts on the bare repository. **Never `claude`** anywhere in this deliverable.
5. **Secrets**: `TestKeys.TestPat` is the only PAT-like string in the repository; the `SecretSweep` helper runs in every step from 4 on; the owner types every live secret himself (Steps 16, 18); the agent records names, never values.
6. **Central** as plans 28/37/38/39: `runuser -u zyggy -- …`; the probe is a foreground process, no unit; never restart a unit in this deliverable; the agent cannot reach the laptops — Steps 17–20's laptop parts are the owner's.
7. **Do not edit** `_specs/00 …` (A1–A6 are pasted by the orchestrating session with the owner's approval and only confirmed in Step 15), `_specs/04-…`, genome files, or the roadmap except Step 21 (status). The project-manager trims the 04 DoD line and 05's gate line (ex-OQ4).
8. **Plain words at every gate**: describe what the system now does ("the laptop wakes on an ETag change and answers within n seconds"), not only AC numbers; the AC map is for the record.
