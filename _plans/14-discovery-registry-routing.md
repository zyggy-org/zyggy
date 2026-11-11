# Plan: 14 — The discovery registry and routing by project name — "As Central (the owner's `delegate` skill in 15, the Hub's `list_nodes` in 21), I want every node to publish what it can run — its Claude Code projects with their subagents and skills, plus a liveness stamp — and to accept a job that names a project by that registry name, so that a job is routed by speciality and name rather than by a machine-specific path; and as a node I want to resolve a job's `project` against my own latest scan and refuse any path outside my `devRoots`, so that nothing on the bus — signed or not — can steer a run into a folder I did not discover myself"

> **Scheduling note.** Nothing of this plan starts until the owner has approved it at the plan gate. The owner creates the branch (suggested `feature/14-discovery-registry-routing` from `main`); every step is built there. At each 🛑 HUMAN GATE the changes are left **uncommitted** for the owner's review (auto-memory "Owner reviews before commit"); the agent never pushes; CI runs only when the owner pushes. The one owner-run item is the real-machine check at the final gate (spec AC-33).

## Overview

After this deliverable every node — the home laptop, the work laptop later, and Central's own node — scans the direct children of its `devRoots` for Claude Code projects (`.claude/`), derives a routable name for each, reads its subagents' and skills' `description`, its `CLAUDE.md` summary and its default branch, and publishes `tenants/<org>/registry/<machine>.yaml` as a plain, unsigned, canonical YAML document — at start (before any job is admitted), every `discovery.intervalMinutes`, and on demand through `zyggy discover [--dry-run]`. A commit happens only when the content changed or `last_seen` is older than `discovery.lastSeenHours`. The job runner's pre-flight then resolves a `project` **name** against the node's own in-memory scan (`RegistrySnapshot`), never against the bus copy, and accepts an absolute **path** only under a `devRoots` entry; the P1 path-only clause is retired. `tests/Zyggy.Integration/Gates/P2_RegistryWorktreeSweep.cs` part 1 proves the round trip: registry published, `zyggy submit --project <name>` routed by name, run by the fake `claude`, reported on `main`.

Implements `_specs/14-discovery-registry-routing.md` (approved 2026-11-07, zero Open Questions; OQ-1..OQ-3 resolved to the recommendations — Decision Table, Contracts and AC-1..AC-33 are binding). Founding-spec sections: §1, §3 (Node "Publish registry", `discover`), §4 (opening sentence, Write rules, **Registry file** as amended by A14-1), §5 (poll loop items 2–3, write sequence), §6 (pre-flight step 1 as amended by A14-3, step 2, Concurrency), §7 (step 5 as amended by A14-4; `list_nodes` deferred), §8 (Work boundary, Injection, Secrets), §9 (folder map `Registry/`, design rules, packages), §10 (`devRoots`, `workHours`, `discovery` key per A14-5), §11 (logging phase `discover`, health verdict, runbooks incl. A14-6), §12 (definition of done), §13, §14. **This plan never edits `_specs/00 …`**: A14-1, A14-3, A14-4, A14-5 and A14-6 were pasted by the orchestrating session on 2026-11-07; Step 11 only confirms they are in place.

**Reference patterns** (read across all layers; mirrored here):

- **A bus write of a machine's own file**: `src/Zyggy.Core/Bus/BusRepository.cs` — `PublishContextAsync`/`PublishItemAsync` (caller-bug `InvalidOperationException`s, `AssertMayWrite(path, BusFolder.X)`, `WriteAsync(subject, [path], change, ct)` under `BusCheckoutLock`, pull → change → one commit → push with the jittered retry rule → `Pushed`/`Deferred`/`WriteFailed`), `Subject(...)`, `RedactedDetail`; `BusPaths.Registry`/`RegistryFolder`/`TryParseRegistry`, `BusFolder.Registry`; `BusAuditor` already skips `registry/*.yaml` ("registry file (unsigned in v1)"). Tests: `tests/Zyggy.Core.Tests/Bus/BusRepositoryWriteTests.cs` (`TempCheckout`, `RecordingProcessRunner`, substituted `IBusProvider`, `FakeTimeProvider`), `tests/Zyggy.Integration/Bus/PushRetryTests.cs` (`HookedProcessRunner.BeforeRun` + `AdvanceMainFromSenderAsync`).
- **The one YAML emitter/reader**: `src/Zyggy.Core/Envelope/FrontMatterEmitter.Emit(FrontMatterMapping, includeSig)` (ordinal keys by construction of `FrontMatterMapping`, flow sequences of scalars, the plain → single → double quoting ladder, `\n`, UTF-8 without BOM, no `---`) and `FrontMatterReader` (strict event-stream reading: no anchors, aliases, tags, directives, merge keys, second document; unique scalar keys) — `ReadYaml` is the core a delimiter-less document entry point reuses.
- **The job path (06/07)**: `Jobs/PreFlight.cs` (`internal static`, fixed check words), `PreFlightResult`, `JobRunner.PipelineAsync` (pre-flight → `ProjectLock` → model), `JobDispatcher.AdmitAsync` (project key only for a fully-qualified `project`), `Jobs/ServiceCollectionExtensions` (`AddJobRunner` with an identity-less `GitClient`, `AddJobDispatcher`), `Policy/NodePolicy.DevRoots` via `NodeJsonPolicySource`. Tests: `PreFlightTests`, `JobRunnerTests`, `JobDispatcherTests`, `Infrastructure/JobRunnerHarness.cs`, `Infrastructure/ScratchProject.cs` (Core: empty `.git/`, recorded git).
- **`node.json` sections**: `Configuration/NodeConfigurationReader.cs` (`TryArchive` — an optional object section with ranged integers and `Invalid("<section>.<key>", …)`; the secret-key refusal first), `NodeConfiguration` init properties, `Configuration/ServiceCollectionExtensions.AddNodeServices` (the one composition root for Node, CLI and tests).
- **Node hosting**: `src/Zyggy.Node/NodeHost.Build` (registrations, `PollLoop` factory with internal types, test hook `configure`), `PollLoop.ExecuteAsync` (`StartAsync` → health → `ResumeClaimedAsync` → `RunAsync`), `ClaudeAuthProbe` (a `PeriodicTimer` on the injected clock, once-per-transition logs), `HealthServer.Snapshot()`, `NodeStatus`; the build-feature catalog's "Node hosted service" rule (the `BackgroundService` only schedules; the body is testable in Core).
- **Health verdict**: `Health/HealthRules.Evaluate(NodeHealth?, now, PollOptions, version, url)` → `HealthLevel` (the one "green" rule, 08); `Health/HealthClient` (loopback only, no proxy, 2 s) as used by `Cli/Commands/StatusCommand.cs` with the `ZYGGY_HEALTH_URL` test hook.
- **CLI verbs**: `Cli/Commands/ContextCommand.cs` (load `node.json` → exit 3, `BusHost.BuildServices(..., extra)` with `AddSecretPatterns(environment.Variables)`, `BusHost.CheckCheckoutAsync`, one stderr line per failure), `VerifyCommand.cs`, `CliApplication` (the one registration list), `ExitCodes`; in-process driving through `tests/Zyggy.Integration/Infrastructure/ZyggyCliInProcess.cs`.
- **Secret patterns**: `Memory/SecretPatterns` (`internal`, `TryMatch(text, out name)`), `SecretPatternsRegistration.AddSecretPatterns(services, environment)` (returns the error, registers nothing — fail closed), `tests/golden/secret-patterns/{secret-patterns,secret-samples,benign-samples}.txt`.
- **Integration harness**: `tests/Zyggy.Integration/Node/NodeHostHarness.cs` (production host over the bare repo, `Configure`, `RunUntilAsync`, `CommitSubjectsAsync`, `LogLines`, `Environment`), `Infrastructure/TestHost.cs` (`BuildRunnerListener`, `NodeConfigurationFor`), `Infrastructure/ScratchProject.cs` (Integration: real `git init` under `<temp>/zyggy-it/<guid>`), `Infrastructure/NodeJson.cs`, `BusRepoFixture` (`SeedTenantAsync` creates `registry/.gitkeep`, `SeedRawFileAsync`, `AdvanceMainFromSenderAsync`, `ShowBytesAsync`, `ExistsOnMainAsync`), `Gates/P1_WalkingSkeleton.cs` (gate-file shape, regions per part, central environment for the pickup host).
- **Source hygiene**: `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` (`SourceFiles()`, `BusSourceFiles()`, `Src_ContainsNoTenantLiteral`, `StaticMutableField()`, `Tests_NeverResolveRealExternalPrograms`).

**Phase**: 14 **opens** P2 and **creates** `Gates/P2_RegistryWorktreeSweep.cs` part 1 (16 and 17 append; 17 closes P2). Rule 15 is therefore not mandatory, but the spec asks for the gate file: the last slice creates it, together with the documents and runbook rows, and its 🛑 HUMAN GATE is the definition of done.

**What this plan deliberately is not** (spec Defer / Out of Scope): no `zyggy dream agents`, no `agents.md` change (OQ-3); no `list_nodes` (21); no `delegate`/`discover` skills, no `zyggy discover --link`, no `PROTOCOL.md` content (15 — the file stays the placeholder); no "Node silent" alert (22); no worktrees, no `zyggy run` (16); no signing or auditing of the registry file (`BusAuditor` keeps skipping it, OQ-1); no `node.json` list/exclude/alias of projects, no recursive scan, no depth knob; no `/health` discovery field, no change to `NodeHealth` or `HealthRules`; no reading of another machine's or tenant's registry file on a node; no policy-governed `dev_roots`, no `policy.yaml` (§14); no DLP (19); no work-laptop configuration (20 sets `detail: names`); no change to the §4 envelope, the state machine, `BusPaths`, `RunFailureReason`, `PROTOCOL.md` or the envelope golden files; no new NuGet package; no change to `install.ps1`'s `node.json` template (the `discovery` defaults apply when the key is absent); no release step.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — A machine can describe what it can run | 1, 2, 3, 4 | a registry document serialises byte-identically in the §4 canonical form and parses strictly; a scan of `devRoots` finds exactly the Claude Code projects with routable, collision-free names, their default branch, agents, skills and summary, never a secret and never more than the caps; `node.json`'s `discovery` section is read and refused with the dotted key; `zyggy discover --dry-run` prints the document a machine would publish | 🛑 after Step 4 (⚠️ §4 shared contract — the registry format; ⚠️ secrets in published strings; ⚠️ work boundary — `detail: names`) |
| B — A machine publishes its registry to the bus | 5, 6 | the publisher commits only on changed content or a stale `last_seen`; `zyggy discover` publishes to the bare bus repository through the one write path with the retry rule, reports `published`/`unchanged`/push failed/busy, reads `health` from the local node; `zyggy verify` keeps skipping registry files | 🛑 after Step 6 (⚠️ a new bus write — the write-rule guard and tenancy) |
| C — The node publishes on start and on its interval | 7, 8 | the node scans before it admits any job, publishes after its listener is ready, rescans every interval without overlap, survives a failing scan; the real host publishes `acme registry home-laptop` on start, nothing on an unchanged rescan, one commit on a changed tree; Central publishes `projects: []`; foreign registry commits cause nothing | 🛑 after Step 8 (⚠️ the host start-up order — a shared contract with 07) |
| D — Jobs are routed by name, paths are confined to `devRoots` | 9, 10 | pre-flight resolves a name from the node's own scan, refuses a non-label, an unknown name and a path outside `devRoots`; the dispatcher's busy rule sees a name and its path as one project; through the host a job named by registry name runs in the folder the node found, a tampered bus copy never steers it, and every 06/07 suite stays green with its scratch root listed in `devRoots` | 🛑 after Step 10 (⚠️ §6 shared contract — pre-flight step 1; ⚠️ §8 boundary — nothing on the bus steers execution) |
| E — The P2 gate file (part 1), hygiene and documents | 11 | `Gates/P2_RegistryWorktreeSweep.cs` part 1 proves registry published → submit by name → done report, and unknown name → `unknown_project: unknown name`; source-hygiene facts guard the registry paths and tenancy; the READMEs carry the new runbook rows and keys | 🛑 after Step 11 — **definition of done** (incl. the owner's real-machine check, AC-33) |

Every code step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally (Windows); Linux by CI on both runners once the owner has pushed the branch.

<!--
Decomposition strategy: vertical slices. Fake = substituted IProcessRunner (RecordingProcessRunner) behind GitClient, substituted
IBusProvider behind BusRepository over a TempCheckout, IPolicySource substitutes, FakeTimeProvider, temp directory trees; unit tests in
tests/Zyggy.Core.Tests/{Registry,Jobs,Configuration,Bus}. Wire = the production DI (AddNodeServices, NodeHost, CliApplication) against
BusRepoFixture's bare repository, real git in scratch projects, tools/fake-claude as claude.path, BareRepoHeadHandler as the doorbell;
integration tests in tests/Zyggy.Integration/{Registry,Node,Jobs,Gates}. Never the real claude, never GitHub.
-->

---

## Plan assumptions (binding for the executor; each is named again at the gate that covers it)

- **A1 — The first scan is awaited before the listener starts, not after.** The spec's Contract says `PollLoop` awaits `FirstScan` "after `BusListener.StartAsync` succeeded and before `ResumeClaimedAsync`". The code shows `BusListener.StartAsync` already runs `ProcessOnceAsync` (it admits, claims and runs waiting jobs through the handler), so a job waiting in `jobs/` at start would be answered `unknown name` before the snapshot exists — the exact failure the spec's own row "First scan not finished when a job arrives: impossible by contract" forbids. The plan therefore awaits `FirstScan` **before** `_listener.StartAsync`. The scan touches no bus file, so nothing of the §5 start-up rule moves; the **publish** of that first scan still waits until the listener has started (A2). Recorded as owner question Q1.
- **A2 — The first publish waits for the listener to be ready and to have polled once (bounded).** `health` is `HealthRules.Evaluate` over the node's own `/health` body. Before the loop's first poll that body has `lastPollUtc: null` → the verdict is `red` ("never polled"), and with an unchanged tree that `red` would sit on the bus for up to `lastSeenHours`. The node's first publish therefore waits for `NodeStatus` to report the listener started **and** `Listener.Status.LastPollUtc` set, checked once per second on the injected clock, bounded by 60 s; after the bound it publishes with whatever verdict holds. A refused start-up (exit 6) never publishes. This also keeps the first registry commit out of the §5 start-up stash/clean sequence. Recorded as owner question Q2.
- **A3 — The cycle lives in Core, the timer in Node.** Per the build-feature catalog ("the `BackgroundService` only schedules"), the scan → `RegistrySnapshot.Replace` → publish cycle, the non-overlap rule, the failure logging and `FirstScan` live in a public `Zyggy.Core.Registry.DiscoveryLoop`; `Zyggy.Node.DiscoveryTimer : BackgroundService` only calls `DiscoveryLoop.RunAsync(stoppingToken)`. AC-16's unit tests therefore drive `DiscoveryLoop` in `tests/Zyggy.Core.Tests` (no Node test project exists). `PollLoop` awaits `DiscoveryLoop.FirstScan`.
- **A4 — The publisher's result carries the detail.** The Contract's `Task<PublishOutcome> PublishAsync(…)` cannot give `zyggy discover` the redacted push detail it must print. The publisher returns `Task<RegistryPublishResult>` with `public sealed record RegistryPublishResult(PublishOutcome Outcome, string? Detail)`; `PublishOutcome` stays exactly the Contract's enum. Additive; named at Gate B.
- **A5 — A test-only interval override.** `DiscoveryOptions` gains `public TimeSpan? IntervalOverride { get; set; }` (XML doc: "tests only; production leaves it null"), the same shape as `JobRunnerOptions.TimeoutOverride`, so integration tests can rescan every 1–2 s instead of waiting a minute. `node.json` cannot set it.
- **A6 — `node.json` `discovery` → `NodeConfiguration.Discovery`.** A new `Zyggy.Core.Configuration.DiscoverySettings(int IntervalMinutes = 60, int LastSeenHours = 6, DiscoveryDetail Detail = Full)` record on `NodeConfiguration` (init property, defaults when absent), mapped into `IOptions<DiscoveryOptions>` by `AddNodeServices`.
- **A7 — A delegate test hook, not a seam.** The spec says "no new seam". `DiscoveryScanner` gets an **internal** constructor overload taking `Func<string, IEnumerable<DirectoryInfo>>? listChildren` (production: `new DirectoryInfo(root).EnumerateDirectories()` with no recursion) so AC-7 (`UnauthorizedAccessException` from a root) and AC-8 (the fake clock advanced inside the walk → `SlowScan`) are deterministic on both OSes.
- **A8 — The fixed check words.** `PreFlightResult.Rejected.Check` documents eight words: the six of 06 plus `unknown name` and `outside devRoots`. `not absolute` is no longer produced (a non-rooted `project` is now `unknown name`) and stays in the doc comment as "retired with deliverable 14" so old reports remain explicable. `RunFailureReason` is unchanged.
- **A9 — Registry golden files.** The canonical registry form gets golden files `tests/golden/registry/home-laptop.yaml` (the §4 example's content) and `tests/golden/registry/central.yaml` (the minimum). They are the **emitter's** output. If the emitter's quoting differs from the §4 example (e.g. `from: '08:00'` emitted plain), the golden follows the emitter, the executor lists the difference at Gate A, and the orchestrating session — not this plan — aligns the §4 example after the owner says so.
- **A10 — Scratch roots in `devRoots`.** Core unit tests: `ScratchProject` (Core) gains `public static string ParentRoot` (`<temp>/zyggy-it`) and `JobRunnerHarness.NodePolicy` defaults to `NodePolicy.Default with { DevRoots = [ScratchProject.ParentRoot] }`. Integration: `TestHost.NodeConfigurationFor` sets `DevRoots = [ScratchProject.ParentRoot]` (Integration `ScratchProject` gains the same property) — these compositions never scan, they only resolve paths. `NodeHostHarness` (which scans) gets its **own** `ProjectsRoot = Scratch.File("projects")`, writes it into `devRoots` by default, and `ProjectAsync` creates its scratch projects there — so a host's scan sees only its own projects.
- **A11 — The node registers the secret patterns when the environment names them.** `NodeHost.Build` calls `builder.Services.AddSecretPatterns(environment)` and ignores the returned error (absence is not a start-up refusal; the scanner degrades to `names` with one `PatternsUnavailable` warning, AC-10). The CLI verb does the same and prints one stderr note.
- **A12 — One `ZYGGY_HEALTH_URL` test hook.** The constant moves from `StatusCommand` to `BusHost.HealthUrlVariable` (internal) and `discover` reuses it; loopback-only as before.
- **A13 — `JobDispatcher` keeps its 3-argument constructor.** A new public 4-argument constructor takes `RegistrySnapshot registry`; the old one chains with an empty snapshot so 07's tests compile unchanged. `AddJobDispatcher` passes `sp.GetService<RegistrySnapshot>() ?? new RegistrySnapshot()`.
- **A14 — `JobRunner` reads the policy before pre-flight** (it needs `DevRoots`); the run's other uses of the policy are unchanged.

---

## Shared rules for this plan

- **Tenant and machines in tests**: tenant `acme`, machines `central`, `home-laptop`, `work-laptop` (AC-27 only), foreign tenant `other-org`, key `acme/1` from `TestKeys`; example paths `C:/src/calizr`, `/home/alice/src` (spec placeholders). Nothing in `src/`, fixtures or documents names the owner, his employer, a real host, mailbox or the private instance repository (`Src_ContainsNoTenantLiteral` stays green).
- **The §9 seams**: git only through `GitClient` over `IProcessRunner`; the bus only through `BusRepository` over `IBusProvider`; time only through `TimeProvider`; no `Process`, `HttpClient` (outside `HealthClient`), GitHub or secret-store reference added anywhere. No new seam interface.
- **Paths**: bus paths only from `BusPaths` (`Registry`, `RegistryFolder`, `Resolve`); file-system paths under a project only as `Path.Join(projectPath, ".claude", …)` inside `DiscoveryScanner`/`PreFlight`; no `"registry/"` literal outside `BusPaths` (Step 11 hygiene fact).
- **Clock**: `FakeTimeProvider` in every unit test (scanner elapsed, publisher liveness, loop schedule). Integration tests use the production clock with bounded waits that poll a condition (≤ 60 s), never a fixed sleep longer than a second.
- **Logging**: phase `discover`, `tenant`/`machine` enriched, no `jobId`; every line from the spec's Behaviors list verbatim; once per state change; **never** a description, a summary, a `CLAUDE.md` line or a matched secret — folder paths, names, counts, pattern names and notice kinds only. Unit tests assert through `RecordingLogger<T>`, integration tests through `RecordingLoggerProvider` or the host's Serilog file (`NodeHostHarness.LogLines`), always alongside the real evidence (files and commits on the bare repository, stdout, the capture file).
- **Never**: the real `claude` (`claude.path` is `FakeClaude.ExecutablePath`); GitHub (the doorbell is `BareRepoHeadHandler`); a secret in a log line, a registry file, stdout/stderr or a process argument (`SecretSweep.AssertAbsent` in Steps 4, 6, 8, 10, 11); a static mutable field; `TimeProvider.System`/`DateTime.UtcNow` outside composition roots; reading `tenants/<other>/registry/`.
- **Public API** per `.claude/instructions/public-api.md`, XML-documented: in `Zyggy.Core.Registry` — `RegistryDocument`, `RegistryParse`, `RegistryProject`, `RegistryEntry`, `RegistryOs`, `DiscoveryDetail`, `DiscoveryOptions`, `DiscoveryScanner` (public class, **internal** constructors — it takes the internal `GitClient` and `SecretPatterns`, like `JobRunner`), `ScanResult`, `ScanNotice`, `ScanNoticeKind`, `RegistrySnapshot`, `RegistryPublisher` (internal constructor), `PublishOutcome`, `RegistryPublishResult` (A4), `DiscoveryLoop` (A3, internal constructor), `ServiceCollectionExtensions.AddDiscovery`; in `Zyggy.Core.Bus` — `BusRepository.ReadOwnRegistryAsync`, `PublishRegistryAsync`; in `Zyggy.Core.Configuration` — `DiscoverySettings`, `NodeConfiguration.Discovery`; in `Zyggy.Core.Jobs` — the new `JobDispatcher` constructor; in `Zyggy.Node` — `DiscoveryTimer` (internal; `InternalsVisibleTo Zyggy.Integration` exists).
- **Shared-contract edits are additive**: before touching `BusRepository`, `PreFlight`, `JobRunner`, `JobDispatcher`, `NodeConfiguration`/`NodeConfigurationReader`, `AddNodeServices`, `AddJobRunner`/`AddJobDispatcher`, `NodeHost`, `PollLoop`, `NodeStatus`, `TestHost`, `NodeHostHarness`, `NodeJson`, `ScratchProject` (both) or `JobRunnerHarness`, run the whole suite and note the counts; after GREEN the counts may only grow. `git diff tests/golden/` shows only the new `tests/golden/registry/` files. The **only** permitted edits to existing assertions are the ones the spec retires, each listed in its step: the 06 expectation `not absolute` → `unknown name` (Step 9) and the P1 gate's comment on the probe job (Step 11).
- **Test run commands**: unit `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~<Class>"`, integration `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~<Class>"`; the exact filters are in each step's RED.

---

## Step 1 — A registry document round-trips byte-identically in the §4 canonical form: `RegistryDocument.Serialize()` emits ordinal keys at every level, `\n`, UTF-8 without BOM, no `---`, no `null`, optional keys omitted, `schema: 1`; `Parse` applies the §4 strictness, refuses with a one-word field and never throws; `ContentEquals` ignores the liveness group

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Registry/RegistryDocument.cs` *(create)*: `public sealed record RegistryDocument(TenantId Tenant, MachineName Machine, RegistryOs Os, DateTimeOffset? LastSeen, HealthLevel? Health, WorkHoursSettings? WorkHours, IReadOnlyList<RegistryProject> Projects)` with `public const int Schema = 1`; `public byte[] Serialize()` — builds a `FrontMatterMapping` (keys `health`, `last_seen`, `machine`, `os`, `projects`, `schema`, `tenant`, `work_hours`; `health` lowercased `green|amber|red`, `HealthLevel.Down` is a caller bug → `ArgumentException`; `last_seen` as `yyyy-MM-dd'T'HH:mm:ss'Z'` invariant UTC; `os` lowercase; `work_hours` as `{days: [Mon, …], from, timezone, to}`; per project `{agents, default_branch?, name, path?, skills, summary?}`; per entry `{description, name}`) and calls `FrontMatterEmitter.Emit(mapping, includeSig: true)` — the one emitter; `public static RegistryParse Parse(ReadOnlyMemory<byte> bytes)` — never throws on input; `public bool ContentEquals(RegistryDocument other)` — compares everything except `LastSeen` and `Health`, lists element-wise (records with list members get explicit structural equality: `Equals`/`GetHashCode` overrides comparing `Projects`, `Agents`, `Skills`, `WorkHours.Days` with `SequenceEqual`, so `Parse(Serialize(d)) == d` holds).
- `src/Zyggy.Core/Registry/RegistryParse.cs` *(create)*: `public sealed record RegistryParse(RegistryDocument? Document, string? Field, string? Detail)` (exactly one of `Document`/`Field` non-null; `IsOk`).
- `src/Zyggy.Core/Registry/RegistryProject.cs`, `RegistryEntry.cs`, `RegistryOs.cs` *(create — as the spec's Contracts; `RegistryOs { Windows, Linux, Macos, Other }`, wire lowercase; `RegistryOs Current()` static helper from `OperatingSystem.Is*`)*; `DiscoveryDetail.cs` *(create: `public enum DiscoveryDetail { Full, Names }`)*.
- `src/Zyggy.Core/Envelope/FrontMatterReader.cs` *(modify, additive)*: `internal static EnvelopeRejection? ReadDocument(ReadOnlyMemory<byte> file, out FrontMatterMapping document)` — strict UTF-8 (BOM refused for the registry: the writer never emits one), then the existing `ReadYaml` core (same anchor/alias/tag/directive/merge-key/duplicate/second-document refusals). `Read` is unchanged.
- `tests/golden/registry/home-laptop.yaml`, `tests/golden/registry/central.yaml` *(create — A9; LF endings; the executor confirms `.gitattributes` keeps `tests/golden/**` byte-exact and the test project copies `golden/registry/` to the output, mirroring `golden/secret-patterns/`)*; `tests/golden/README.md` *(modify: one paragraph on the registry cases — "the emitter's output; never regenerated by hand")*.
- `tests/Zyggy.Core.Tests/Registry/RegistryDocumentTests.cs` *(create)*.

**Seams**: none (pure serialisation); `YamlDotNet` through the existing emitter/reader only.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~RegistryDocumentTests"`):
- `RegistryDocumentTests` (AC-12, AC-13, AC-14):
  - `Serialize_FullDocument_EqualsGoldenHomeLaptop` (the §4 example's values incl. `work_hours` from a `WorkHoursSettings("Europe/Brussels", [Mon..Fri], "08:00", "19:00")` → bytes equal `golden/registry/home-laptop.yaml`); `Serialize_MinimumCentral_EqualsGoldenCentral` (no `work_hours`, no `health`, `projects: []`).
  - `Serialize_Twice_ByteIdentical`; `Serialize_TopLevelKeysOrdinal` (key order `health, last_seen, machine, os, projects, schema, tenant, work_hours`); `Serialize_NoBomNoCrNoDelimiterNoNull` (bytes do not start with `EF BB BF`, contain no `\r`, do not start with `---`, contain no `null`/`~` scalar); `Serialize_AbsentOptionalKeys_Omitted` (`path`, `default_branch`, `summary`, `health`, `work_hours` absent from the text; `agents: []`, `skills: []` present; an absent description is `''`); `Serialize_HealthDown_Throws`.
  - `Parse_SerializeRoundTrip_EqualsOriginal` (both goldens); `Parse_MissingOptionalKey_NotARefusal`.
  - `[Theory] Parse_Refusals_FieldNamed` rows: `schema: 2` → `schema`; a sequence root → `root`; `tenant: Acme` → `tenant`; a project without `name` → `projects[0].name`; a 151-character description → `projects[0].agents[0].description`; an anchor/alias → `root`-level detail naming anchors; a second document → `root`; 50 KiB of unrelated YAML (a mapping without `schema`) → `schema`; non-UTF-8 bytes → `root`. Each returns `Document == null`, never throws.
  - `ContentEquals_DifferOnlyInLastSeenAndHealth_True`; `ContentEquals_OneDescriptionDiffers_False`; `ContentEquals_ProjectOrderSameValues_True` only when the scanner's ordinal order holds (documents are compared as given — the scanner sorts, Step 2).

**GREEN**: as Scope. `Parse` reads into the records with field paths built as `projects[i].agents[j].description`; label fields through `TenantId.TryParse`/`MachineName.TryParse`; `last_seen` with `DateTimeOffset.TryParseExact(…, "yyyy-MM-dd'T'HH:mm:ss'Z'", InvariantCulture, AssumeUniversal)`.

**Contract impact**: ⚠️ **§4 shared contract** — the registry file format (A14-1) is fixed here and read later by 15, 21, 22; golden files under `tests/golden/registry/` (A9). The envelope canonical form and its goldens are untouched.

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSigner|FullyQualifiedName~FrontMatter"` unchanged (the reader's envelope path is untouched); `git diff tests/golden/` shows only `registry/` and the README paragraph; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — A scan of `devRoots` finds exactly the Claude Code projects with routable, collision-free names and their default branch: direct children with `.claude/` only (hidden folders, files, links and nested projects excluded, links never followed), names derived as labels, duplicates and unusable names left out with a notice, missing/unreadable roots reported, caps and slow scans noticed, `detail: names` reading nothing under `.claude/`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Registry/DiscoveryScanner.cs` *(create, public sealed partial; internal constructors per A7)*: `Task<ScanResult> ScanAsync(IReadOnlyList<string> devRoots, DiscoveryDetail detail, CancellationToken ct)` — roots normalised (`Path.GetFullPath`, trailing separator trimmed), de-duplicated (case-insensitive on Windows, ordinal elsewhere), a root lying under another root dropped as a root (Edge Cases); per root: missing → `RootMissing`, `UnauthorizedAccessException`/`IOException` while listing → `RootUnreadable`; children: directories only, name not starting with `.`, no `FileAttributes.ReparsePoint`, holding a `.claude` directory (itself not a reparse point); name = `ProjectName.Derive(folderName)` — empty → `NameUnusable` (subject = the folder path); names grouped → any group > 1 removed entirely with one `DuplicateName` notice (subject = the name, detail = the paths joined by `; ` in ordinal order); sorted by name ordinal; cap `MaxProjects` → `ProjectCapReached`; per project `default_branch` through `GitClient.DefaultBranchAsync` (non-git folder → omitted, no notice); `path` = normalised full path with `/` separators (only under `Full`); `Elapsed` from the injected clock; `Elapsed > SlowScanThreshold` → `SlowScan`. Never throws for a file-system or git failure. Under `Names` nothing under `.claude/` and no `CLAUDE.md` is opened (Step 3 adds the `Full` content).
- `src/Zyggy.Core/Registry/ProjectName.cs` *(create, internal static)*: `string Derive(string folderName)` — lowercase invariant, every run of characters outside `[a-z0-9]` → one `-`, trimmed of `-`, cut to `Label.MaxLength` (63) and re-trimmed; empty when nothing remains. `bool IsLabel(string)` delegates to `Label.IsValid`.
- `src/Zyggy.Core/Registry/ScanResult.cs`, `ScanNotice.cs` (incl. `ScanNoticeKind`), `DiscoveryOptions.cs` *(create — the spec's Contracts, plus `IntervalOverride` per A5; `Validate()` enforcing 1–1440 / 1–168)*.
- `src/Zyggy.Core/Git/GitClient.cs` *(modify, additive)*: `public async Task<string?> DefaultBranchAsync(string repository, CancellationToken ct)` — `symbolic-ref -q --short refs/remotes/origin/HEAD` (strip the leading `origin/`), else `rev-parse --verify -q refs/heads/main` → `main`, else the same for `master`, else `CurrentBranchAsync`, else `null`.
- `tests/Zyggy.Core.Tests/Registry/DiscoveryScannerTests.cs`, `tests/Zyggy.Core.Tests/Registry/ProjectNameTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/ScanTree.cs` *(create: a temp tree builder under `<temp>/zyggy-unit/<guid>` — `Root(name)`, `Project(root, folder, git: bool)`, `Hidden`, `File`, `Link` (tries `Directory.CreateSymbolicLink`, returns `false` when the OS refuses), deleted on dispose)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner` with its `Hook` answering `symbolic-ref`/`rev-parse` per `WorkingDirectory`) behind the real `GitClient`; `TimeProvider` (`FakeTimeProvider`, advanced inside the A7 `listChildren` hook for AC-8); the real file system in a temp tree.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DiscoveryScannerTests|FullyQualifiedName~ProjectNameTests"`):
- `ProjectNameTests` (AC-5): `[Theory] Derive` rows `calizr` → `calizr`, `My_App` → `my-app`, `Client Portal.v2` → `client-portal-v2`, `--` → `""`, `ÄÖ` → `""`, a 70-character name → 63 characters without a trailing `-`.
- `DiscoveryScannerTests`:
  - `ScanAsync_Ac1Tree_OnlyDirectChildrenWithClaude` (AC-1: two roots; git project, non-git project with `.claude/` (included, no `default_branch`), folder without `.claude/`, `.cache` with `.claude/`, a file, a link to a project (skipped when the OS refuses to create it — `Assert.SkipWhen`), `<root>/org/repo/.claude/` → exactly the two direct children, ordered by name ordinal; `RecordingProcessRunner.Calls` hold git calls only for the two projects).
  - `ScanAsync_LinkedProject_NeverFollowed` (the link target is not listed and no git call names it).
  - `[Theory] ScanAsync_DefaultBranchLadder` (AC-4 rows a–e → `main`, `main`, `master`, `feature/x`, omitted); `ScanAsync_OriginHeadSet_FeatureCheckout_StillMain` (no flapping).
  - `ScanAsync_UnusableNames_SkippedWithNoticeNamingFolder` (AC-5: `--`, `ÄÖ` → two `NameUnusable` notices whose `Subject` is the folder path; derived names `calizr`, `my-app`, `client-portal-v2` published).
  - `ScanAsync_SameNameUnderTwoRoots_NeitherPublishedOneNotice` (AC-6: `<a>/calizr`, `<b>/Calizr` → no project, one `DuplicateName` with subject `calizr` and both paths).
  - `ScanAsync_MissingAndUnreadableRoot_GoodRootStillScanned` (AC-7 via the A7 hook throwing `UnauthorizedAccessException` for one root → one `RootMissing`, one `RootUnreadable`, the good root's projects, `Elapsed` set, no exception).
  - `ScanAsync_501Projects_CappedAt500WithNotice` (AC-8: `MaxProjects` set to 3 with 4 folders keeps the test fast; the first 3 in ordinal order + `ProjectCapReached`) — the executor may use the real 500 if the temp tree builds in under a second.
  - `ScanAsync_WalkLongerThanThreshold_SlowScanNotice` (AC-8: hook advances the fake clock by 31 s → `SlowScan` with the seconds).
  - `ScanAsync_NamesDetail_OnlyNameAndDefaultBranch_NothingUnderClaudeRead` (AC-11: `Path`, `Summary` null, `Agents`/`Skills` empty; the project's `.claude/agents/x.md` and `CLAUDE.md` are opened exclusively by the test (`FileShare.None`) during the scan and the scan still succeeds without a notice — proof nothing was opened).
  - `ScanAsync_DuplicateRootsAndNestedRoot_ScannedOnce` (Edge Cases: the same root twice and a root that is a child of another root → each project once).
  - `ScanAsync_RootIsItselfAProject_NotListed` (Edge Cases).

**GREEN**: as Scope. The scanner logs nothing yet beyond Debug; the notice-to-Warning logging is `DiscoveryLoop`'s (Step 7) and the CLI's (Step 4).

**Contract impact**: ⚠️ the registry `name` rule and the tie rule (§4 as amended, P1/P2 of the Decision Table); additive `GitClient.DefaultBranchAsync`; no bus, no envelope.

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~GitClient|FullyQualifiedName~PreFlightTests"` unchanged; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 3 — A scan publishes speciality, never secrets: agents and skills with their front-matter `description` (control characters removed, 150 characters without splitting a surrogate pair, `''` when absent or unparseable, unsafe names skipped), the `CLAUDE.md` summary rule over the first 64 KiB, every published string tested against the secret patterns, entries capped, and no patterns → `names` detail

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Registry/DiscoveryScanner.cs` *(modify)*: under `Full` — `.claude/agents/*.md` (files only, no reparse points; name = file name without `.md`, must match `[A-Za-z0-9._-]+` — the same rule as `PreFlight.SafeAgentName`, shared through one internal `AgentNames.IsSafe`), `.claude/skills/<name>/SKILL.md` (folder name, same rule; a folder without `SKILL.md` skipped); description = the front matter's scalar `description` through `FrontMatterReader.Read` (a non-scalar, missing or unparseable front matter → `''`); `CLAUDE.md` first 64 KiB (BOM skipped), first line that is non-empty after trimming and does not start with `#`, `>`, `-`, `*`, `+`, a digit followed by `.`/`)`, or a fence (```` ``` ````/`~~~`), with fenced blocks skipped; cap `MaxEntriesPerProject` per list → `EntryCapReached`; each list ordered by name ordinal. Every description and summary → `RegistryText.Clean` then `patterns.TryMatch`: a match → description `''` / summary omitted + one `SecretRedacted` notice (subject = the project name, detail = the pattern name). Constructed with `patterns == null` and asked for `Full` → runs as `Names`, `EffectiveDetail = Names`, one `PatternsUnavailable` notice (AC-10).
- `src/Zyggy.Core/Registry/RegistryText.cs` *(create, internal static)*: `string Clean(string text)` — removes Unicode category `Cc` plus U+2028/U+2029, then cuts to 150 UTF-16 units without ending on a high surrogate (`char.IsHighSurrogate(text[149])` → 149).
- `src/Zyggy.Core/Jobs/PreFlight.cs` *(modify, refactor only: `SafeAgentName()` moves to the shared `AgentNames.IsSafe` in `src/Zyggy.Core/Jobs/AgentNames.cs` (internal); behaviour identical)*.
- `tests/Zyggy.Core.Tests/Registry/DiscoveryScannerContentTests.cs`, `tests/Zyggy.Core.Tests/Registry/RegistryTextTests.cs` *(create)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner`) behind `GitClient`; `TimeProvider` (`FakeTimeProvider`); `SecretPatterns.Load(golden/secret-patterns/secret-patterns.txt)`; the real file system in a `ScanTree`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DiscoveryScannerContentTests|FullyQualifiedName~RegistryTextTests"`):
- `DiscoveryScannerContentTests`:
  - `ScanAsync_Ac2AgentsAndSkills_DescriptionsCleanedAndOrdered` (AC-2: `env-debugger` with `description: "Diagnoses failing environments"`, `plain` without front matter, `bad` with unparseable front matter, `with space` → agents `[{bad,''},{env-debugger,"Diagnoses failing environments"},{plain,''}]` (ordinal), `with space` absent; `deploy-staging` with a 170-character description containing a tab and `\u0007` → exactly 150 characters, no control character; `empty/` skipped; nothing throws).
  - `[Theory] ScanAsync_Ac3Summary` rows (a) the quote/list/fence document → `Multi-tenant booking SaaS, .NET/Blazor`; (b) headings only → `Summary == null`; (c) no `CLAUDE.md` → null; (d) 1 MiB file whose first qualifying line is beyond 64 KiB → null, and one whose qualifying line is within 64 KiB → that line; (e) CRLF + BOM → the line without `\r` (Edge Cases).
  - `ScanAsync_BlockScalarDescription_LineBreaksRemoved` (Edge Cases).
  - `ScanAsync_101Agents_CappedAt100WithNotice` (AC-8; `MaxEntriesPerProject = 3` variant allowed for speed).
  - `ScanAsync_SecretInDescriptionAndSummary_RedactedWithPatternNotice` (AC-9: one sample from `secret-samples.txt` in an agent description, one in `CLAUDE.md` → description `''`, summary null, two `SecretRedacted` notices naming the pattern and the project, `Subject`/`Detail` never containing the sample); `ScanAsync_BenignSample_PassesUnchanged` (a line from `benign-samples.txt`).
  - `ScanAsync_NoPatternsFullDetail_FallsBackToNamesWithNotice` (AC-10: `EffectiveDetail == Names`, one `PatternsUnavailable`, no agent/summary data, no exception).
  - `ScanAsync_LogOutput_NeverContainsContent` (AC-30, scanner half: every `RecordingLogger<DiscoveryScanner>` message checked against the descriptions, the summary and the secret sample — none appears).
- `RegistryTextTests`: `Clean_RemovesCcAndLineSeparators`; `Clean_150CutNeverSplitsSurrogatePair` (an emoji straddling index 149/150 → length 149); `Clean_ShortText_Unchanged`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ **secrets** — no published string matches a pattern (§8, A14-1's last sentence); ⚠️ **work boundary** — `names` fallback reads nothing under `.claude/`; the agent-name rule is now shared with pre-flight (no behaviour change there).

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~PreFlightTests|FullyQualifiedName~DiscoveryScannerTests"` unchanged; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — `zyggy discover --dry-run` shows what this machine would publish: `node.json`'s `discovery` section is read, defaulted and refused with the dotted key; the CLI composition registers the scanner; the verb scans real scratch projects with real git, prints the serialised document to stdout and one notice per line to stderr, writes nothing and exits 0; a bad configuration exits 3

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Configuration/DiscoverySettings.cs` *(create — A6)*; `NodeConfiguration.cs` *(modify, additive: `public DiscoverySettings Discovery { get; init; } = new();`)*; `NodeConfigurationReader.cs` *(modify: `TryDiscovery` after `TryArchive` — absent/null → defaults; not an object → `Invalid("discovery", "must be an object")`; `intervalMinutes` integer 1–1440 → `Invalid("discovery.intervalMinutes", "must be an integer from 1 to 1440")`; `lastSeenHours` 1–168 likewise; `detail` `"full"`/`"names"` → `Invalid("discovery.detail", "must be full or names")`; unknown keys inside the section ignored as elsewhere; the secret-key refusal runs first as for every section)*.
- `src/Zyggy.Core/Registry/ServiceCollectionExtensions.cs` *(create)*: `public static IServiceCollection AddDiscovery(this IServiceCollection services)` — `TryAddSingleton(TimeProvider.System)`; `AddOptions<DiscoveryOptions>()` with `PostConfigure` from a registered `NodeConfiguration.Discovery` and `Validate`; `AddSingleton(sp => new DiscoveryScanner(new GitClient(sp.GetRequiredService<IProcessRunner>(), new GitClientOptions(), clock), sp.GetService<SecretPatterns>(), options.Value, clock, logger))` — resolved lazily so a later `AddSecretPatterns` is seen. (The publisher is registered in Step 5, the snapshot and the loop in Step 7.)
- `src/Zyggy.Core/Configuration/ServiceCollectionExtensions.cs` *(modify: `AddNodeServices` calls `services.AddDiscovery()`)*.
- `src/Zyggy.Core/Registry/RegistryDocument.cs` *(modify, additive: `public static RegistryDocument FromScan(NodeConfiguration configuration, ScanResult scan, DateTimeOffset lastSeen, HealthLevel? health)` — tenant, machine, `RegistryOs.Current()`, `configuration.WorkHours`, `scan.Projects`)*.
- `src/Zyggy.Cli/Commands/DiscoverCommand.cs` *(create)*: `zyggy discover [--dry-run] [--config <path>] [--verbose]`; this step implements `--dry-run` only (without `--dry-run` the verb returns `ExitCodes.Usage` with `zyggy: discover: publishing arrives in the next step` — replaced in Step 6 and never shipped, the branch is not released between steps). Order: `BusHost.LoadConfiguration` (exit 3) → `BusHost.BuildServices(..., s => { patternsError = s.AddSecretPatterns(environment.Variables); configure?.Invoke(s); })` → `BusHost.CheckCheckoutAsync` (exit 3) → when `patternsError` is set and `discovery.detail` is `full`, one stderr line `zyggy: discover: secret patterns not registered (<error>); publishing names only` → `ScanAsync(policy.DevRoots via IPolicySource, configuration.Discovery.Detail)` → notices to stderr, one line each: `discover: duplicate name <name>: <paths>`, `discover: root missing <root>`, `discover: root unreadable <root>`, `discover: name unusable for <folder>`, `discover: <kind> cap reached`, `discover: secret pattern <pattern> redacted in <project>`, `discover: scan took <s> s` → `FromScan(..., lastSeen: clock.GetUtcNow(), health: null)` → `Serialize()` written to stdout as UTF-8 → exit 0.
- `src/Zyggy.Cli/CliApplication.cs` *(modify: register `DiscoverCommand.Create(environment, io, configure)`)*.
- `tests/Zyggy.Core.Tests/Configuration/NodeConfigurationReaderTests.cs` *(modify, additive)*; `tests/Zyggy.Core.Tests/Registry/DiscoveryRegistrationTests.cs` *(create)*.
- `tests/Zyggy.Integration/Infrastructure/ScratchProject.cs` *(modify, additive: `CreateAsync(bus, ct, agent, parent: string? = null, name: string? = null, claudeMd: string? = null, skill: string? = null)` — `parent` defaults to `ParentRoot` = `<temp>/zyggy-it`; agent files gain a `description:` line when given `agentDescription`)*; `tests/Zyggy.Integration/Infrastructure/NodeJson.cs` *(modify, additive: `IReadOnlyList<string>? devRoots`, `string? discoveryJson`)*.
- `tests/Zyggy.Integration/Registry/DiscoverCommandTests.cs` *(create)*.

**Seams**: `IPolicySource` (production `node.json` policy) for `devRoots`; `IProcessRunner` real (git in scratch projects); `TimeProvider` system; the bus checkout read-only.

**RED**:
- Unit (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~NodeConfigurationReaderTests|FullyQualifiedName~DiscoveryRegistrationTests"`):
  - `NodeConfigurationReaderTests`: `Read_DiscoveryAbsent_Defaults` (60, 6, `Full`); `Read_DiscoverySection10Example_Typed` (A14-5's `{ "intervalMinutes": 60, "lastSeenHours": 6, "detail": "full" }`); `[Theory] Read_DiscoveryInvalid_FieldNamed` rows `discovery: 5` → `discovery`, `intervalMinutes: 0` / `1441` → `discovery.intervalMinutes`, `lastSeenHours: 0` / `169` → `discovery.lastSeenHours`, `detail: "all"` → `discovery.detail`; `Read_DiscoveryNames_Typed`.
  - `DiscoveryRegistrationTests`: `AddNodeServices_ResolvesScannerWithOptionsFromNodeJson`; `AddNodeServices_WithSecretPatternsRegistered_ScannerUsesThem` (a `Full` scan of a temp project yields agent descriptions); `AddNodeServices_WithoutPatterns_ScanFallsBackToNames`.
- Integration (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~DiscoverCommandTests"`), in process through `ZyggyCliInProcess` over a `BusRepoFixture` (tenant `acme`, machine `home-laptop`) and a dedicated `devRoots` root holding two scratch projects (`calizr` with agent `env-debugger` + description and a `CLAUDE.md`, `client-portal` without agents), environment with `ZYGGY_SECRET_PATTERNS` = the golden file:
  - `DryRun_TwoProjects_DocumentOnStdoutNothingWritten` (AC-21 dry-run half: exit 0; `RegistryDocument.Parse(stdout)` ok with `tenant: acme`, `machine: home-laptop`, the test OS's `os`, both projects with `path` (forward slashes), `default_branch` = the scratch repo's branch (the executor pins it with `git symbolic-ref HEAD refs/heads/main` in the fixture), `env-debugger` with its description, `summary`; `health` absent; bare repo HEAD unchanged; clone `git status --porcelain` empty; no `registry/home-laptop.yaml` in the clone).
  - `DryRun_DuplicateName_StderrNoticeBothPaths` (a second root with `Calizr` → stdout without `calizr`, one stderr line `discover: duplicate name calizr: …` naming both paths).
  - `DryRun_NoSecretPatterns_NamesOnlyAndStderrNote` (environment without the variable → projects carry only `name`/`default_branch`; one stderr line `zyggy: discover: secret patterns not registered (...)`; exit 0).
  - `DryRun_BadConfig_ExitThree` (a `discovery.detail: "all"` file → exit 3, stderr `zyggy: configuration error: discovery.detail: must be full or names`); `DryRun_MissingConfig_ExitThree`.
  - All facts: `SecretSweep.AssertAbsent([stdout, stderr])`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ `node.json` key `discovery` (§10 as amended by A14-5); ⚠️ public API `AddDiscovery`, `DiscoverySettings`, `NodeConfiguration.Discovery`, `RegistryDocument.FromScan`; the CLI surface gains `discover` (spec CLI surface; `--link` stays 15).

**VERIFY**: both failing-run commands pass; `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Cli|FullyQualifiedName~Status|FullyQualifiedName~Verify"` unchanged; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (a machine can describe what it can run) *(covers Steps 1–4)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

**In plain words**: a machine can now look at its project folders and describe them the way the founding spec's registry example shows — project names you can route by, each project's default branch, its subagents and skills with their one-line descriptions, and the first sentence of its `CLAUDE.md`. Folders with an unusable or clashing name are left out with a clear line. Nothing that looks like a secret ever makes it into the description, and without the secret-pattern file the machine publishes names only. `zyggy discover --dry-run` prints exactly that document; nothing is written to the bus yet.

- [x] Behavioral verification: `RegistryDocumentTests` (golden `home-laptop.yaml` and `central.yaml` pasted), `DiscoveryScannerTests`, `DiscoveryScannerContentTests`, `ProjectNameTests`, `RegistryTextTests`, `NodeConfigurationReaderTests` (discovery rows), `DiscoverCommandTests` — the dry-run stdout of `DryRun_TwoProjects_DocumentOnStdoutNothingWritten` pasted.
- [x] Contract review: the golden registry file against the founding spec §4 "Registry file" example (key order, quoting — **A9**: list any quoting difference such as `from: '08:00'` for the orchestrating session); the name derivation and tie rule (AC-5, AC-6); the summary rule (AC-3); the `discovery` key (A14-5); **A5** (`IntervalOverride`), **A6** (`DiscoverySettings`), **A7** (the internal `listChildren` hook, no seam).
- [x] ⚠️ Risk review (**§4 shared contract**, **secrets**, **work boundary**): the format is the one 15/21/22 will read (schema 1, ordinal keys, no `null`); no published string matches a pattern and no log line carries a description or summary (`ScanAsync_LogOutput_NeverContainsContent`); `names` detail opens nothing under `.claude/` (`ScanAsync_NamesDetail_…`); `SecretSweep` clean over the CLI output; no new package (`git diff Directory.Packages.props` empty).
- [x] User approved — implementation may continue past this gate

---

## Step 5 — A machine commits its registry only when the content changed or the liveness stamp is stale: `RegistryPublisher` reads the bus copy, publishes on first run, skips an unchanged content within `lastSeenHours`, refreshes `last_seen`/`health` on every commit, treats a future `last_seen` as fresh and an unparseable copy as absent; `BusRepository.PublishRegistryAsync` writes `<tenant> registry <machine>` through the one write path and refuses a foreign document as a caller bug

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify, additive)*: `public async Task<byte[]?> ReadOwnRegistryAsync(CancellationToken ct)` (reads `BusPaths.Resolve(_checkoutRoot, BusPaths.Registry(_me.Tenant, _me.Machine))`; `null` when absent; no lock — a read of the checkout as `ListNewJobsAsync` does); `public Task<WriteOutcome> PublishRegistryAsync(RegistryDocument document, CancellationToken ct)` — `document.Tenant != _me.Tenant` / `document.Machine != _me.Machine` → `InvalidOperationException("write rule: the registry document belongs to another tenant|machine")`; `path = BusPaths.Registry(_me.Tenant, _me.Machine)`; `AssertMayWrite(path, BusFolder.Registry)`; `WriteAsync($"{tenant} registry {machine}", [path], write bytes + git add, ct)`. *(Signature note: the spec lists `PublishRegistryAsync(byte[] bytes, …)`; taking the `RegistryDocument` is what makes AC-31's tenant/machine refusal possible without re-parsing — the bytes are `document.Serialize()` inside. Named at Gate B.)*
- `src/Zyggy.Core/Registry/RegistryPublisher.cs` *(create, public sealed partial, internal ctor `(BusRepository bus, IOptions<DiscoveryOptions> options, TimeProvider clock, ILogger<RegistryPublisher> log)`)*: `Task<RegistryPublishResult> PublishAsync(RegistryDocument content, HealthLevel? health, CancellationToken ct)` — read the bus copy; `Parse` refusal → treated as absent + one Warning `registry: bus copy unreadable ({Field}); rewriting` per streak; `now = clock.GetUtcNow()`; `fresh = copy.LastSeen is { } seen && (seen > now || now - seen < LastSeenHours)`; `copy is not null && copy.ContentEquals(content) && fresh` → `Unchanged` (Debug `registry unchanged`); else `document = content with { LastSeen = now (truncated to seconds), Health = health ?? copy?.Health }` → `PublishRegistryAsync`: `Pushed(1)` → `Published`; `Pushed(n>1)` → `Published` + Warning `registry: push retried` once per streak; `Deferred` → `Failed` with the redacted detail + Warning `registry: push failed: {Detail}` once per streak; `WriteFailed { Stage: "lock" }` → `Busy` + Warning `registry: checkout busy` once per streak; other `WriteFailed` → `Failed` (`{Stage}: {Detail}`); a success after a failure streak → Information `registry: publishing again`; every `Published` → Information `registry published: {Count} project(s)`. Scope `tenant`/`machine`/`phase=discover`.
- `src/Zyggy.Core/Registry/PublishOutcome.cs` *(create: the Contract's enum + `RegistryPublishResult` per A4)*.
- `src/Zyggy.Core/Registry/ServiceCollectionExtensions.cs` *(modify: register `RegistryPublisher` over the registered `BusRepository`)*.
- `tests/Zyggy.Core.Tests/Registry/RegistryPublisherTests.cs`, `tests/Zyggy.Core.Tests/Bus/BusRepositoryRegistryTests.cs` *(create)*.

**Seams**: `IBusProvider` (NSubstitute: `PullAsync` ok, `PushAsync` scripted `PushSucceeded`/`Rejected`) and `IProcessRunner` (`RecordingProcessRunner`) behind a real `BusRepository` over a `TempCheckout` (the `BusRepositoryWriteTests` construction); `TimeProvider` (`FakeTimeProvider`).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~RegistryPublisherTests|FullyQualifiedName~BusRepositoryRegistryTests"`):
- `RegistryPublisherTests` (AC-15, AC-30 publisher half):
  - `PublishAsync_LivenessSequence` — first publish → `Published`, the checkout file parses with `LastSeen == T`, `Health == Green`; same content at T+1 h → `Unchanged`, no `commit` call recorded; T+6 h 1 min → `Published`, `last_seen` refreshed, the rest byte-identical to the previous file except `last_seen`; a changed description at T+8 h 1 min → `Published` with both refreshed; a clock 1 h **before** the file's `last_seen` → `Unchanged`.
  - `PublishAsync_HealthNull_KeepsBusCopyValue`; `PublishAsync_HealthNullNoCopy_KeyOmitted`.
  - `PublishAsync_UnparseableBusCopy_TreatedAsAbsentWarnedOnce` (two publishes over a garbage copy that the substituted pull restores → two `Published`, one Warning).
  - `PublishAsync_PushRejectedOnceThenOk_PublishedOneRetryWarning` (and a second streak later warns again — once per streak).
  - `PublishAsync_EveryPushRejected_FailedWithRedactedDetail` (`Rejected("https://user:tok@host …")` → `Detail` contains `://<redacted>@`; one Warning across two consecutive failing publishes; a later success logs `registry: publishing again`).
  - `PublishAsync_LockHeld_Busy` (a `BusCheckoutLock` write lock held by the test with a 0-length timeout override, or the existing lock-timeout injection used by `BusRepositoryWriteTests` — the executor reuses whichever that class uses).
  - `PublishAsync_Logs_OneLinePerStateChange_NoContent` (Information on `Published`, Debug on `Unchanged`, no description/summary text in any message).
- `BusRepositoryRegistryTests` (AC-31): `PublishRegistryAsync_WritesBusPathsRegistryWithSubject` (recorded `commit` message `acme registry home-laptop`, `add` path = `BusPaths.Registry(acme, home-laptop)`); `PublishRegistryAsync_ForeignTenant_Throws`; `PublishRegistryAsync_ForeignMachine_Throws`; `ReadOwnRegistryAsync_Absent_Null`; `ReadOwnRegistryAsync_NeverReadsAnotherMachinesFile` (a `registry/work-laptop.yaml` in the checkout is not returned).

**GREEN**: as Scope.

**Contract impact**: ⚠️ a new bus write of this machine's own `registry/<machine>.yaml` (the §4 write rule A14-2 already allows it); ⚠️ public API `ReadOwnRegistryAsync`, `PublishRegistryAsync` (document-typed, see Scope note), `RegistryPublisher`, `PublishOutcome`, `RegistryPublishResult` (A4).

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepository|FullyQualifiedName~BusAuditor"` unchanged; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — `zyggy discover` publishes to the bare bus repository: one commit on the first run, none on the second, the result line on stdout, the push retried once over a foreign commit, a push that keeps failing reported with exit 1 and the commit left local, a busy checkout reported, `health` read from the local node when it answers; `zyggy verify` and `zyggy status` unaffected by registry files

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/DiscoverCommand.cs` *(modify)*: without `--dry-run`, after the scan: `health` = read `BusHost.HealthUrlVariable` (A12) or `HealthClient.DefaultUrl` through `HealthClient`; `Reachable` → `HealthRules.Evaluate(body, now, PollOptions, BusHost.InformationalVersion(), url).Level` when `Green|Amber|Red`; `Unreachable` → `null` (the publisher keeps the bus value or omits); a non-loopback test URL → exit 3 `health url must be loopback` (as `status`); `RegistryPublisher.PublishAsync(FromScan(...), health, ct)` → `Published` → stdout `registry: <n> project(s), published`, exit 0; `Unchanged` → `registry: <n> project(s), unchanged`, exit 0; `Failed` → stderr `zyggy: registry: push failed: <detail>`, exit 1; `Busy` → stderr `zyggy: registry: checkout busy`, exit 1. The placeholder usage line of Step 4 is removed.
- `src/Zyggy.Cli/Commands/BusHost.cs` *(modify: `internal const string HealthUrlVariable = "ZYGGY_HEALTH_URL";`)*; `StatusCommand.cs` *(modify: uses it; behaviour unchanged)*.
- `tests/Zyggy.Integration/Registry/DiscoverCommandTests.cs` *(modify, additive)*; `tests/Zyggy.Integration/Registry/RegistryPublishRoundTripTests.cs` *(create)*.

**Seams**: wired — real `git` against `BusRepoFixture`'s bare repository through `GitHubBusProvider` (doorbell `BareRepoHeadHandler`), `HookedProcessRunner` for the push hook, a loopback stub for `/health` (the `HealthClient` test handler pattern of 08: a `ZYGGY_HEALTH_URL` pointing at an ephemeral `HttpListener`/Kestrel stub already used by `StatusCommand` tests — the executor reuses that helper).

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~DiscoverCommandTests|FullyQualifiedName~RegistryPublishRoundTripTests"`):
- `DiscoverCommandTests` (AC-21, AC-29 CLI half):
  - `Publish_FirstThenSecondRun_OneCommitThenUnchanged` (first: exit 0, stdout `registry: 2 project(s), published`, `git log main` has exactly one `acme registry home-laptop`, `BusPaths.Registry(acme, home-laptop)` on `main` parses; second: exit 0, `registry: 2 project(s), unchanged`, still one registry commit).
  - `Publish_DryRunAfterAddingProject_NothingWritten` (a third scratch project then `--dry-run` → three projects on stdout, `main` unchanged).
  - `Publish_NodeAnswersGreen_HealthGreen` (stub `/health` body green per `HealthRules` with the CLI's version → file `health: green`); `Publish_NoNodeAnswering_HealthOmittedOnFirstPublish` (no listener → no `health` key).
  - `Publish_EveryPushFails_ExitOneCommitLocal` (`HookedProcessRunner.Override` answers every `push` with a non-zero result → exit 1, stderr `zyggy: registry: push failed: …` with no URL user info, `git rev-list origin/main..HEAD` in the clone = 1).
  - `Publish_CheckoutLockHeld_ExitOneBusy` (the test takes `BusCheckoutLock.WriteLockName` on the clone for the run with the repository's lock timeout shortened through the existing test hook — if none exists, the executor holds the lock and asserts the 60 s path is not taken by setting the CLI's cancellation; the expected stderr is `zyggy: registry: checkout busy`).
  - `Publish_BadConfig_ExitThree`.
- `RegistryPublishRoundTripTests` (AC-22, AC-23):
  - `Verify_RegistryFilesPresent_SkippedVerdictUnchanged` (seed `registry/home-laptop.yaml` and `registry/central.yaml` through `discover` runs from both checkouts → `zyggy verify --verbose` exit 0, two `SKIP … registry file (unsigned in v1)` lines; `zyggy status` exit code and bus block unchanged compared with a run before the files existed).
  - `Publish_ForeignCommitBeforePush_RetriedOnceOnTop` (over `TestHost.Build` services with `RegistryPublisher` resolved: `HookedProcessRunner.BeforeRun` advances `main` from the sender before the first `push` → `Published`, two `push` calls, `git log -2 main` = `acme registry home-laptop`, `noise 1`; exactly one registry commit; one Warning `registry: push retried` in the `RecordingLoggerProvider`).
- All facts: `SecretSweep.AssertAbsent` over stdout/stderr and `HookedProcessRunner.Calls`.

**GREEN**: as Scope.

**Contract impact**: the CLI surface `discover` complete (spec "CLI surface"); `ZYGGY_HEALTH_URL` reused (A12); no envelope, no `PROTOCOL.md`.

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Status|FullyQualifiedName~Verify|FullyQualifiedName~PushRetryTests"` unchanged; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (a machine publishes its registry to the bus) *(covers Steps 5–6)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

**In plain words**: `zyggy discover` now writes this machine's registry file to the bus — one commit the first time, nothing when nothing changed, and a fresh "last seen" stamp at most every six hours. If someone else pushed at the same moment it retries once on top; if the push keeps failing it says so and keeps the commit for the node's next write. `zyggy verify` still ignores registry files, exactly as before.

- [x] Behavioral verification: `RegistryPublisherTests`, `BusRepositoryRegistryTests`, `DiscoverCommandTests`, `RegistryPublishRoundTripTests` — paste `git log --format=%s main` of `Publish_FirstThenSecondRun_OneCommitThenUnchanged` and the published file.
- [x] Contract review: the liveness rule (AC-15) against A14-1's "a commit happens when …"; the commit subject `<tenant> registry <machine>`; the exit codes (0/1/3) against the spec's CLI surface; **A4** (`RegistryPublishResult`); the `PublishRegistryAsync(RegistryDocument …)` signature note in Step 5's Scope; **A12**.
- [x] ⚠️ Risk review (**new bus write**): `AssertMayWrite(BusFolder.Registry)` and the tenant/machine refusal are in place (AC-31); push detail redacted; `SecretSweep` clean; the write goes through `WriteAsync` under the checkout lock (no second write path).
- [x] User approved — implementation may continue past this gate

---

## Step 7 — The node's discovery cycle keeps a current snapshot through substitutes: `DiscoveryLoop` scans at start and signals `FirstScan` even when the scan fails, replaces `RegistrySnapshot` after every scan (even when publishing fails), waits for the host's readiness before its first publish, rescans every interval on the injected clock without overlapping, and logs each notice kind once per change of its subject set

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Registry/RegistrySnapshot.cs` *(create, public sealed)*: `RegistryDocument? Current { get; }` (volatile reference), `void Replace(RegistryDocument document)`; `TryResolve` is Step 9.
- `src/Zyggy.Core/Registry/DiscoveryLoop.cs` *(create, public sealed partial, internal ctor — A3)*: `(DiscoveryScanner scanner, RegistrySnapshot snapshot, RegistryPublisher publisher, IPolicySource policy, NodeConfiguration configuration, IOptions<DiscoveryOptions> options, TimeProvider clock, ILogger<DiscoveryLoop> log)`; `Task FirstScan { get; }` (a `TaskCompletionSource` completed after the first cycle's `Replace` or its failure); `Func<HealthLevel?> Health { get; set; }` (default `() => null`) and `Func<CancellationToken, Task> BeforeFirstPublish { get; set; }` (default completed) — the Node sets both (Step 8); `Task RunCycleAsync(CancellationToken ct)` — scan (`policy.DevRoots`, `options.Detail`) → `snapshot.Replace(FromScan(configuration, scan, now, null))` → on the first cycle `FirstScan` completes → `await BeforeFirstPublish(ct)` (first cycle only) → `publisher.PublishAsync(document, Health(), ct)`; a scan exception (not cancellation) → Warning `discovery: scan failed {ExceptionType}` once per streak, the previous snapshot kept, `FirstScan` still completed; `Task RunAsync(CancellationToken ct)` — one cycle at start, then a `PeriodicTimer(options.IntervalOverride ?? FromMinutes(IntervalMinutes), clock)`; a tick while a cycle runs is skipped with one Debug `discovery: scan skipped, previous still running`; notice logging: per `ScanNoticeKind` the set of subjects is compared with the previous scan's and one Warning per newly appearing subject in the spec's wording (`discovery: duplicate name {Name}: {Paths}`, `discovery: root missing {Root}`, `discovery: root unreadable {Root}`, `discovery: name unusable for {Folder}`, `discovery: {Kind} cap reached`, `discovery: secret pattern {Pattern} redacted in {Project}`, `discovery: secret patterns not registered; publishing names only`, `discovery: scan took {Seconds} s`). Scope `tenant`/`machine`/`phase=discover`.
- `src/Zyggy.Core/Registry/ServiceCollectionExtensions.cs` *(modify: `TryAddSingleton<RegistrySnapshot>()`; `AddSingleton<DiscoveryLoop>` factory)*.
- `tests/Zyggy.Core.Tests/Registry/DiscoveryLoopTests.cs`, `tests/Zyggy.Core.Tests/Registry/RegistrySnapshotTests.cs` *(create)*.

**Seams**: `TimeProvider` (`FakeTimeProvider` driving the `PeriodicTimer`); `IPolicySource` (NSubstitute, `DevRoots` = a `ScanTree` root); `IProcessRunner` (`RecordingProcessRunner`) behind the scanner's `GitClient`; `IBusProvider` (NSubstitute) behind the publisher's `BusRepository` over a `TempCheckout`; a throwing variant through the A7 `listChildren` hook.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DiscoveryLoopTests|FullyQualifiedName~RegistrySnapshotTests"`):
- `DiscoveryLoopTests` (AC-16, AC-30 loop half):
  - `RunAsync_Schedule_ScanAtStartThenEveryInterval` (interval 60 min: one scan at start; advance 59 min → still one; +1 min → two; +3 h → five) — counted through the recorded `listChildren` calls.
  - `FirstScan_CompletesAfterSnapshotReplaced_BeforePublish` (`BeforeFirstPublish` blocked on a `TaskCompletionSource` → `FirstScan` completed, `snapshot.Current` set, no `commit` recorded; release → one commit).
  - `RunAsync_ScanThrows_WarnOnceKeepScheduleFirstScanCompleted` (hook throws on the first two scans: `FirstScan` completed, one Warning `discovery: scan failed InvalidOperationException`, third scan succeeds and the schedule held).
  - `RunCycleAsync_PublishFails_SnapshotStillReplaced` (every push rejected → `snapshot.Current` is the new scan).
  - `RunAsync_TickWhileCycleRuns_SkippedDebugOnce` (a scan blocked inside the hook across a tick → one Debug, no second concurrent scan).
  - `RunCycleAsync_HealthFunc_UsedOnPublish` (`Health = () => HealthLevel.Amber` → file `health: amber`).
  - `RunAsync_SameDuplicateTwice_OneWarning_NewDuplicate_SecondWarning` (notice logging once per change of the subject set).
- `RegistrySnapshotTests`: `Current_BeforeReplace_Null`; `Replace_ConcurrentReaders_SeeWholeDocuments`.

**GREEN**: as Scope.

**Contract impact**: public API `RegistrySnapshot`, `DiscoveryLoop` (A3); no bus or envelope change beyond Step 5.

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Registry"` all green; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — The real node host publishes its registry at start and on change: `DiscoveryTimer` runs the loop, `PollLoop` awaits the first scan before the listener starts (A1), the first publish waits for the listener to be ready and polled once (A2), `health` comes from the node's own `/health` body; one commit `acme registry home-laptop` on start, none on an unchanged rescan, one on a changed tree; Central publishes `projects: []`; foreign registry commits cause nothing

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Node/DiscoveryTimer.cs` *(create, internal sealed, `BackgroundService`)*: ctor `(DiscoveryLoop loop, NodeStatus status, HealthServer health, PollOptions poll, TimeProvider clock)`; in the ctor sets `loop.Health = () => Level(HealthRules.Evaluate(health.Snapshot(), clock.GetUtcNow(), poll, NodeHost.InformationalVersion(), "http://localhost:" + health.ConfiguredPort + "/health").Level)` (`Down` → `null`) and `loop.BeforeFirstPublish = ct => status.WhenReadyAsync(clock, TimeSpan.FromSeconds(60), ct)`; `ExecuteAsync(ct) => loop.RunAsync(ct)`.
- `src/Zyggy.Node/NodeStatus.cs` *(modify, additive)*: `void MarkListenerStarted()`; `Task WhenReadyAsync(TimeProvider clock, TimeSpan bound, CancellationToken ct)` — awaits the started signal, then checks `Listener?.Status.LastPollUtc is not null` once per second on `clock` until true or `bound` elapsed (A2).
- `src/Zyggy.Node/PollLoop.cs` *(modify)*: ctor gains `DiscoveryLoop discovery`; `ExecuteAsync`: after `LogNodeStarted`, `await _discovery.FirstScan.WaitAsync(stoppingToken)` **before** `_listener.StartAsync` (A1); after the start-up result is `Clean`/`Stashed` and the health server started, `_status.MarkListenerStarted()`; everything else unchanged.
- `src/Zyggy.Node/NodeHost.cs` *(modify)*: `builder.Services.AddSecretPatterns(environment)` (result ignored, A11) before `AddNodeServices`; `AddSingleton<IHostedService>(sp => new DiscoveryTimer(...))` registered **after** the `PollLoop` factory; the `PollLoop` factory passes `sp.GetRequiredService<DiscoveryLoop>()`.
- `tests/Zyggy.Integration/Node/NodeHostHarness.cs` *(modify, additive — A10)*: `public string ProjectsRoot => Scratch.File("projects")` (created on first use); `WriteNodeJson(..., IReadOnlyList<string>? devRoots = null, string? discoveryJson = null)` writes `devRoots` = `devRoots ?? [ProjectsRoot]`; `ProjectAsync(ct, string? name = null, string? agent = null, string? agentDescription = null)` creates under `ProjectsRoot` (default name = a GUID label as today); `Environment` defaults to a dictionary holding `ZYGGY_SECRET_PATTERNS` = the golden file (tests that set `Environment` themselves keep theirs); `Configure` also sets `services.Configure<DiscoveryOptions>(o => o.IntervalOverride = DiscoveryInterval)` when the test sets `public TimeSpan? DiscoveryInterval`; helper `RegistryOnMainAsync(MachineName, ct)` (parses `BusPaths.Registry` on `main` or returns null).
- `tests/Zyggy.Integration/Node/NodeHostRegistryTests.cs` *(create)*.

**Seams**: wired — the production host (`NodeHost.Build` + `NodeHostHarness.Configure`) over the bare repository, real git, `BareRepoHeadHandler`, `tools/fake-claude` (auth probe), `TimeProvider.System`.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~NodeHostRegistryTests"`):
- `NodeHostRegistryTests`:
  - `Start_TwoProjects_OneRegistryCommitAndLogLine` (AC-19: two projects under `ProjectsRoot` (`calizr` with agent `env-debugger` + description, `client-portal`); run until `RegistryOnMainAsync(home-laptop)` is not null → exactly one commit `acme registry home-laptop`; the file parses with `tenant: acme`, `machine: home-laptop`, `os` of the test machine, both projects with `path` and `default_branch: main`, the agent with its description, `last_seen` within the test's window, `health` present and **not** `red` (A2); log has exactly one `registry published: 2 project(s)` line carrying `discover` in the phase column).
  - `Rescan_UnchangedThenAddedProject_ExactlyTwoRegistryCommits` (AC-20: `DiscoveryInterval = 2 s`; wait for two further cycles (Debug `registry unchanged` ≥ 1, no new commit); add a third project; wait until the file lists three → `git log main` shows exactly two registry commits).
  - `Start_JobWaitingInInbox_ScannedBeforeClaim` (A1 proof: a job seeded **before** the host starts naming `calizr` by registry name — this step only asserts the order: the log line `registry`/scan completion precedes the `claim` line and the snapshot is non-empty when the first claim happens (`DiscoveryLoop.FirstScan` completed before `BusListener.StartAsync` — asserted through the log order); its `done` outcome is Step 10's).
  - `ForeignRegistryCommits_NodeProcessesNothingWritesNothing` (AC-27: default interval; after the host's own publish, the sender commits `registry/work-laptop.yaml` and `tenants/other-org/registry/x.yaml` → after two further ticks: no new commit by the node, no Information line after the start-up lines except the doorbell's own once-per-change lines, the foreign files byte-identical on `main`; `HookedProcessRunner.Calls` contain no argument naming `other-org`).
  - `Central_NoDevRoots_PublishesEmptyProjects` (AC-28: `WriteNodeJson(machine: central, devRoots: [])` + the pickup environment of `P1_WalkingSkeleton` part 2 (`ZYGGY_SECRET_PATTERNS`, instance/memory variables) → one commit `acme registry central`, `projects: []`, no `work_hours`, `health` present; `role` absent from `node.json` changes nothing).
  - `StartupRefused_NoRegistryCommit` (a checkout on the wrong branch → exit 6, no registry commit, `FirstScan` completed — the host did not hang).
  - All facts: `SecretSweep.AssertAbsent(harness.LogLines)`; no log line contains a description or summary.
- Regression (unchanged expectations): `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Node|FullyQualifiedName~Gates.P1"` — every existing host test passes with the registry commit now present (they filter subjects; the executor records the before/after counts).

**GREEN**: as Scope.

**Contract impact**: ⚠️ **host start-up order** — 07's `PollLoop` now awaits the first scan before `BusListener.StartAsync` (A1) and marks readiness (A2); the §5 start-up rule itself is unchanged; the node registers the secret patterns when the environment names them (A11). No envelope or `NodeHealth` change.

**VERIFY**: failing-run command passes; the regression filter above passes with counts ≥ before; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C (the node publishes on start and on its interval) *(covers Steps 7–8)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

**In plain words**: the node service now looks at its project folders before it touches any job, publishes its registry file once it is up and has polled, rescans every hour (seconds in the tests), and commits only when something changed. Central publishes a file too, with no projects. Another machine's or tenant's registry file arriving on the bus makes the node do nothing at all.

- [ ] Behavioral verification: `DiscoveryLoopTests`, `RegistrySnapshotTests`, `NodeHostRegistryTests` (paste the registry file and the `git log --format=%s main` of `Rescan_UnchangedThenAddedProject_ExactlyTwoRegistryCommits`); the regression counts of the Node/P1 suites before and after.
- [ ] Contract review: **A1** (first scan awaited before `BusListener.StartAsync`, deviating from the Contract's wording for the reason given — owner question Q1), **A2** (first publish waits for readiness + first poll, bounded 60 s — owner question Q2), **A3** (cycle in Core), **A10** (`ProjectsRoot` in the harness), **A11** (patterns registered when present); the log lines against the spec's Behaviors list.
- [ ] ⚠️ Risk review (**shared contract with 07 — host start-up**): a refused start-up still exits 6 without a registry commit and without hanging (`StartupRefused_NoRegistryCommit`); no Information line per tick (§11); `SecretSweep` clean over the host log.
- [ ] User approved — implementation may continue past this gate

---

## Step 9 — Pre-flight resolves a name from the node's own scan and confines paths to `devRoots`, through substitutes: a label resolves through `RegistrySnapshot.TryResolve`, a non-label or unknown name is `unknown name`, a rooted path must equal or lie under a `devRoots` entry on a separator boundary (case-insensitive on Windows), `..` segments are refused, the 06 checks then run on the resolved path; the dispatcher's busy rule treats a name and its path as one project

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Registry/RegistrySnapshot.cs` *(modify)*: `public bool TryResolve(string name, [NotNullWhen(true)] out string? path)` — `ProjectName.IsLabel(name)` required; ordinal lookup in `Current.Projects`; an entry without `Path` (a `names` scan) never resolves; the returned path converted back from `/` to the OS form via `Path.GetFullPath`.
- `src/Zyggy.Core/Jobs/PreFlight.cs` *(modify)*: `CheckAsync(JobFields job, GitClient git, RegistrySnapshot registry, IReadOnlyList<string> devRoots, CancellationToken ct)`; step 1 per the spec's Contract: non-rooted (`!Path.IsPathRooted` **and** not a Windows drive shape) → `TryResolve` or `Rejected(UnknownProject, "unknown name")`; rooted → any `.`/`..` segment (split on `/` and `\`) → `"outside devRoots"`; `GetFullPath` + trim → `UnderDevRoots(path, devRoots)` (each root normalised the same way; equal, or `path.StartsWith(root + separator)` with `OrdinalIgnoreCase` on Windows, `Ordinal` elsewhere; both `/` and `\` accepted as the separator on Windows) else `"outside devRoots"`; empty `devRoots` → every path `"outside devRoots"`; then `missing` → `not a git repository` → `no .claude` → `agent` exactly as in 06. Class doc rewritten (the P1 sentence retired).
- `src/Zyggy.Core/Jobs/PreFlightResult.cs` *(modify: the check-word list per A8)*.
- `src/Zyggy.Core/Jobs/JobRunner.cs` *(modify)*: internal ctor gains `RegistrySnapshot registry`; `PipelineAsync` reads the policy first (A14) and calls `PreFlight.CheckAsync(fields, _git, _registry, policy.DevRoots, ct)`; the reply body stays `unknown_project: <check>`.
- `src/Zyggy.Core/Jobs/JobDispatcher.cs` *(modify — A13)*: new public ctor `(IPolicySource policy, RegistrySnapshot registry, TimeProvider clock, ILogger<JobDispatcher> log)`; the 3-arg ctor chains with `new RegistrySnapshot()`; project key = fully-qualified path normalised as today, **or** `registry.TryResolve(project, out path)` → that path normalised; an unknown name → `null` key (admitted, the runner answers).
- `src/Zyggy.Core/Jobs/ServiceCollectionExtensions.cs` *(modify: `AddJobRunner` and `AddJobDispatcher` pass `sp.GetService<RegistrySnapshot>() ?? new RegistrySnapshot()`)*.
- `tests/Zyggy.Core.Tests/Infrastructure/ScratchProject.cs` *(modify, additive: `public static string ParentRoot`)*; `JobRunnerHarness.cs` *(modify — A10: `NodePolicy` default `NodePolicy.Default with { DevRoots = [ScratchProject.ParentRoot] }`; `public RegistrySnapshot Registry { get; } = new();` passed to `Build()`)*.
- `tests/Zyggy.Core.Tests/Jobs/PreFlightTests.cs`, `JobRunnerTests.cs`, `JobDispatcherTests.cs` *(modify)*; `tests/Zyggy.Core.Tests/Registry/RegistrySnapshotTests.cs` *(modify)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner`) behind `GitClient`; `IPolicySource` (NSubstitute through `JobRunnerHarness`); `IModelRunner` (NSubstitute — asserted **not** called on every rejection); `TimeProvider` (`FakeTimeProvider`); real temp folders (`ScratchProject` Core).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~PreFlightTests|FullyQualifiedName~JobRunnerTests|FullyQualifiedName~JobDispatcherTests|FullyQualifiedName~RegistrySnapshotTests"`):
- `PreFlightTests` (AC-17): `[Theory] CheckAsync_Resolution` with a snapshot `calizr → <scratch>` and `devRoots` [`<scratch parent>`, a second root] — `calizr` → `Ok(<scratch>)`; `unknown` → `unknown name`; `Calizr` → `unknown name`; the scratch path → `Ok`; a sibling-prefix root (`<parent>2/x`) → `outside devRoots`; `<parent>/x/..` → `outside devRoots`; any path with `devRoots: []` → `outside devRoots`; `CheckAsync_DriveLetterCaseDiffers_OkOnWindowsOnly` (`Assert.SkipUnless(OperatingSystem.IsWindows())`, upper-cased root); `CheckAsync_ResolvedFolderRemovedAfterScan_Missing`; `CheckAsync_ResolvedNotGit_NotAGitRepository`; `CheckAsync_ResolvedNoClaude_NoClaude`; `CheckAsync_AgentCheckedUnderResolvedPath` (an agent present under the resolved project → `Ok` with the agent; absent → `unknown agent`); `CheckAsync_NamesOnlySnapshot_UnknownName`; `CheckAsync_PathExactlyADevRoot_Accepted` (Edge Cases); `CheckAsync_NameEqualsRelativeFolderOfCwd_SnapshotOnly` (Edge Cases — a folder `calizr` in the current directory is never consulted). The 06 fact asserting `not absolute` for `relative` is **changed** to `unknown name` (the only permitted expectation edit, spec-retired).
- `JobRunnerTests` (AC-17 reply): the existing `{ "relative", "not absolute" }` / `{ "registry-name", "not absolute" }` rows become `unknown name` (spec-retired, listed); new `RunAsync_KnownName_RunsInResolvedPath` (snapshot `calizr → <scratch>` → the `ModelRunRequest.WorkingDirectory` is the scratch path, `done`); `RunAsync_PathOutsideDevRoots_RejectedUnknownProjectModelNeverStarted` (body `unknown_project: outside devRoots\n`, `Model.DidNotReceive().RunAsync`); `RunAsync_UnknownName_RejectedBodyUnknownName`.
- `JobDispatcherTests` (AC-18): `AdmitAsync_NameAndItsPath_SecondAndThirdProjectBusy` (two jobs by `calizr`, one by its path, while the first lease is held → `Deferred(ProjectBusy)` twice); `AdmitAsync_UnknownName_Admitted`; the existing `AdmitAsync_RegistryStyleProject_AdmittedWithoutLockCheck` stays green (empty snapshot).
- `RegistrySnapshotTests`: `TryResolve_LabelKnown_Path`; `TryResolve_NotALabel_False`; `TryResolve_NamesEntryWithoutPath_False`; `TryResolve_BeforeFirstScan_False`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ **§6 shared contract** — pre-flight step 1 as amended by A14-3 (O18 closed; the P1 path-only clause retired); two new check words in the report body (A8), `RunFailureReason` unchanged; ⚠️ public API — `JobDispatcher`'s new constructor (A13); `JobRunner` stays internally constructed.

**VERIFY**: failing-run command passes; `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Jobs|FullyQualifiedName~BusListener"` all green with only the listed expectation edits (`git diff tests/Zyggy.Core.Tests/Jobs/` reviewed by the executor and quoted at the gate); build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — A job named by registry name runs in the folder the node discovered, against the bare bus repository and the fake `claude`: the host claims, runs and reports `done` for a name; an unknown name and a path outside `devRoots` end in `rejected`/`unknown_project` with the model never started; a tampered bus copy of the registry never steers the run and is restored; every 06/07 round trip stays green with its scratch root in `devRoots`

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/ScratchProject.cs` *(modify, additive — A10: `public static string ParentRoot` if not added in Step 4)*; `tests/Zyggy.Integration/Infrastructure/TestHost.cs` *(modify — A10: `NodeConfigurationFor(...)` sets `DevRoots = [ScratchProject.ParentRoot]`; `BuildRunnerListener` unchanged otherwise)*.
- `tests/Zyggy.Integration/Node/NodeHostHarness.cs` *(modify, additive: `SeedJobAsync(ScratchProject project, …, string? projectField = null)` — `projectField` overrides `project.Path` (a name, or a path outside `devRoots`))*.
- `tests/Zyggy.Integration/Jobs/RoutingByNameTests.cs` *(create — the host level)*; `tests/Zyggy.Integration/Jobs/JobRunnerRoundTripTests.cs` *(modify, additive facts only)*.
- Production code: none expected — Steps 8 and 9 wire it. If a regression appears, the fix stays inside the Scope of Steps 8–9 and is named at the gate.

**Seams**: wired — production host over the bare repository, real git, `tools/fake-claude` (capture file `capture.bin` written into the run's working directory by `ZYGGY_FAKE_CLAUDE_CAPTURE`), `BareRepoHeadHandler`.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~RoutingByNameTests|FullyQualifiedName~JobRunnerRoundTripTests"`):
- `RoutingByNameTests`:
  - `JobByName_RunsInDiscoveredFolder_DoneReport` (project `calizr` under `ProjectsRoot`; job `project: calizr` seeded before start (the A1 case of Step 8) → `done` report on `main`, `capture.bin` exists in the `calizr` folder, the fake's recorded working directory is that folder).
  - `JobByUnknownName_RejectedUnknownProject_ModelNeverStarted` (`no-such-project` → `status: rejected`, `reason: unknown_project`, body `unknown_project: unknown name`; no `capture.bin` anywhere under `ProjectsRoot`, no transcript under `<nodeDir>/runs/acme/`).
  - `JobByPathOutsideDevRoots_RejectedOutsideDevRoots` (a valid scratch project created under `ScratchProject.ParentRoot` but **not** under `ProjectsRoot` → body `unknown_project: outside devRoots`; no capture).
  - `TamperedRegistryOnBus_NeverSteersRun_FileRestored` (AC-26: after the first publish the sender rewrites `registry/home-laptop.yaml` so `calizr`'s `path` points at a decoy project (with its own capture expectation) and pushes; then a job `project: calizr` → the capture appears in the real `calizr` folder, never in the decoy; with `DiscoveryInterval = 2 s` the node's next cycle publishes its own content again (`main`'s file `path` equals the real folder)).
  - `TwoJobsSameNameAndPath_SecondWaitsForTheFirst` (AC-18 through the host: `maxConcurrentJobs: 2`, `ZYGGY_FAKE_CLAUDE_DELAY_MS` on the first; a job by name and one by path to the same project → the second claim happens only after the first report — compare commit order).
- `JobRunnerRoundTripTests` *(additive)*: `RoundTrip_PathUnderDevRoots_Done` is the existing behaviour — no new code; add `RoundTrip_EmptyDevRoots_PathRejectedOutsideDevRoots` (a configuration with `DevRoots = []`).
- Regression: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Jobs|FullyQualifiedName~Node|FullyQualifiedName~Bus|FullyQualifiedName~Gates.P1"` — every existing 06/07/08 round trip passes **unchanged** except for the A10 `devRoots` additions in the harnesses (no assertion edits).

**GREEN**: harness edits per Scope; production code only if a regression demands it (Scope note).

**Contract impact**: ⚠️ **§8 boundary as an AC** — the bus copy of the registry is never an input to execution (AC-26); the harnesses' `devRoots` (planner note of the spec).

**VERIFY**: failing-run command and the regression filter pass; `SecretSweep.AssertAbsent` over host logs and `Hooks.Calls` in every new fact; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D (jobs are routed by name, paths are confined to `devRoots`) *(covers Steps 9–10)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

**In plain words**: a job can now say `project: calizr` and the node runs it in the `calizr` folder it found itself. A name the node does not know, or a folder outside the node's `devRoots`, is refused with `unknown_project` and Claude never starts. Even if someone rewrites the registry file on the bus to point somewhere else, the node ignores it and later puts its own version back. All earlier job tests still pass; they now list their test folder in `devRoots`.

- [ ] Behavioral verification: `PreFlightTests`, `JobRunnerTests`, `JobDispatcherTests`, `RegistrySnapshotTests`, `RoutingByNameTests`, `JobRunnerRoundTripTests`; the regression counts; the report bodies `unknown_project: unknown name` and `unknown_project: outside devRoots` pasted.
- [ ] Contract review: pre-flight step 1 against the founding spec §6 as amended by A14-3; the check-word list (**A8**); the expectation edits in `PreFlightTests`/`JobRunnerTests` (`not absolute` → `unknown name`) quoted from `git diff`; **A13** (`JobDispatcher` constructors), **A14** (policy read first).
- [ ] ⚠️ Risk review (**§6 shared contract**, **§8 boundary**): `TamperedRegistryOnBus_NeverSteersRun_FileRestored` green; no code path reads `BusPaths.Registry` for resolution (grep `ReadOwnRegistryAsync` callers: the publisher only); the sibling-prefix and `..` cases refused; `SecretSweep` clean.
- [ ] User approved — implementation may continue past this gate

---

## Step 11 — The P2 gate's first part passes through the real components, the hygiene facts guard the registry, and the documents carry the new keys and runbook rows: `Gates/P2_RegistryWorktreeSweep.cs` part 1 (registry published → `zyggy submit --project <name>` → done report; unknown name → `unknown_project: unknown name`), source-hygiene facts, READMEs, and the founding-spec amendments confirmed in place

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Gates/P2_RegistryWorktreeSweep.cs` *(create)*: header comment in the P1 file's style — "The P2 gate (founding spec §12). Parts and owners: Part 1 (deliverable 14, this region): registry published, a job routed by name. Part 2 (16, worktrees) and Part 3 (17, sweep) append their own regions; part 1 is never restructured. Nothing in the envelope wire format changed for this gate; `PROTOCOL.md` stays the placeholder (15 copies §4)." `public sealed partial class P2_RegistryWorktreeSweep : IAsyncLifetime` over `NodeHostHarness`; `#region Part 1 — 14`.
- `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` *(modify — comment only)*: Part1a's comment "deliverable 14 turns this into `done`" becomes "a registry name the node's scan does not know stays `unknown_project: unknown name` (deliverable 14)"; the assertions are unchanged.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify, additive)*: `Src_RegistryPathLiteral_OnlyInBusPaths` (no `"registry"` path literal / `"/registry/"` / `".yaml"` bus-path concatenation under `src/` outside `Bus/BusPaths.cs`); `Src_Registry_NeverEnumeratesTenantsFolder` (no file under `src/Zyggy.Core/Registry/` references `BusPaths.Tenants`, `RegistryFolder` or `Directory.Enumerate*` over a checkout path); `Src_Registry_NoDirectFileSystemOutsideScanner` (only `DiscoveryScanner.cs` under `Registry/` uses `Directory.`/`File.`); `Src_PreFlight_NeverReadsTheBusRegistry` (`PreFlight.cs` and `JobDispatcher.cs` do not reference `BusRepository` or `BusPaths`); the existing `StaticMutableField`, `Src_ContainsNoTenantLiteral` cover the new folder.
- `src/Zyggy.Core/README.md` *(modify)*: `Registry/` row (types and roles); the "Jobs: unknown_project / unknown_agent" runbook row rewritten (the four checks, the two resolution forms, `unknown name`, `outside devRoots`, "add the root to `devRoots` or submit by name"); new Failure-modes rows from the spec: "Registry: push failed", "Registry: checkout busy", "Registry: file rewritten", "Discovery: root missing / unreadable", "Discovery: duplicate name", "Discovery: name unusable", "Discovery: cap reached", "Discovery: slow scan", "Discovery: secret pattern redacted", "Discovery: scan failed".
- `src/Zyggy.Node/README.md` *(modify)*: "Not here yet" minus discovery; a `discovery` section (start + interval, first scan before any job (A1), first publish after the first poll (A2), `ZYGGY_SECRET_PATTERNS` in the service's environment — the laptop task / the systemd unit — else names only; the Node-README runbook row for "Secret patterns not registered on the node").
- `src/Zyggy.Cli/README.md` *(modify)*: `discover` row (the verb, `--dry-run`, exit codes 0/1/3, `--link` marked 15); the `devRoots` row "enforced: names resolve from the node's own scan; a path must lie under a root; a root itself is never a project by name"; the `discovery` keys (`intervalMinutes`, `lastSeenHours`, `detail`) in the configuration table.
- `tests/Zyggy.Integration/README.md` *(modify)*: the P2 gate file and its part 1; `NodeHostHarness.ProjectsRoot` and the `devRoots` rule for new tests.
- **Not edited**: `_specs/00 …` (the executor only greps that A14-1 "unsigned, derived state", A14-3 "own latest discovery scan", A14-4 "Reserved", A14-5 `"discovery"`, A14-6 "`git rm` its" are present and reports any missing one at the gate), `PROTOCOL.md`, `_plans/ROADMAP.md` (status is the project-manager's / orchestrating session's), `install.ps1`.

**Seams**: wired — the production host, the in-process CLI (`ZyggyCliInProcess`) as the sender with central's `node.json` over the sender clone (the `P1_WalkingSkeleton.CentralNodeJson()` pattern, copied into the P2 file's private helpers), `tools/fake-claude`, the bare repository.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P2_RegistryWorktreeSweep"` and `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SourceHygieneTests"`):
- `P2_RegistryWorktreeSweep` part 1:
  - `Part1a_RegistryPublished_SubmitByName_DoneReportOnMain` (AC-19 + AC-24: the host with two scratch projects under `ProjectsRoot`; wait until `registry/home-laptop.yaml` is on `main` and `/health` shows a poll; `zyggy submit --to home-laptop --project <scratch name> --timeout 2 --allowed-tools Read "<body>"` from central → the job is claimed, the fake `claude` runs with the resolved folder as its working directory (`capture.bin` there), a signed `done` report verifies on `main`; `git log --format=%s main` contains, in this order, `acme registry home-laptop`, `acme job <id> central→home-laptop`, `acme claim <id> central→home-laptop`, `acme report …`).
  - `Part1b_SubmitUnknownName_RejectedUnknownProject_ModelNeverStarted` (AC-25: `--project no-such-project` → `status: rejected`, `reason: unknown_project`, body `unknown_project: unknown name`; no `capture.bin`, no transcript).
  - Both: `SecretSweep.AssertAbsent` over the CLI output, the host log and `Hooks.Calls`.
- `SourceHygieneTests`: the four facts above.

**GREEN**: as Scope (test code and documents; no production change expected — a needed fix stays within Steps 1–10's Scope and is named at the gate).

**Contract impact**: documents only (`README`s); the founding-spec amendments verified, not made; `PROTOCOL.md` unchanged.

**VERIFY**: both failing-run commands pass; the whole suite `dotnet test Zyggy.slnx` green; `dotnet format Zyggy.slnx --verify-no-changes` clean; `git diff --stat` shows no change under `_specs/`, `PROTOCOL.md`, `Directory.Packages.props`, `tests/golden/` outside `registry/`.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice E (the P2 gate file part 1, hygiene, documents) — definition of done *(covers Step 11)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

**In plain words**: the first half of the P2 gate now runs as one automated scenario: the node publishes what it can run, Central submits a job by project name, the node runs it in the right folder and reports `done`; an unknown name is refused without Claude starting. The documents explain the new settings and what to do when discovery or publishing goes wrong. The last item is yours: check it on the real home laptop and Central.

- [ ] Behavioral verification: `P2_RegistryWorktreeSweep` part 1 (both facts; paste the commit subjects of `Part1a`), `SourceHygieneTests` (the four new facts), the full suite counts (before 14 vs. now), `dotnet format` clean; CI green on `windows-latest` and `ubuntu-latest` once the owner has pushed the branch.
- [ ] Contract review: the roadmap's definition of done — unit tests for the scanner and the `last_seen`-excluding diff; integration tests for one commit on first start, none on an unchanged rescan, unknown name → `unknown_project`, path outside `devRoots` → `unknown_project`; founding spec §4/§6 amended (A14-1, A14-3, A14-4, A14-5, A14-6 confirmed present by grep); the runbook rows for every new failure mode (Core and Node READMEs); `PROTOCOL.md` unchanged (15).
- [ ] ⚠️ Risk review: no new package; no secret in any committed file, fixture or document; placeholders only (`acme`, `home-laptop`, `C:/src/calizr`, `/home/alice/src`); `git diff tests/golden/` = `registry/` only.
- [ ] **Owner-run check (AC-33)** on the real machines, after the owner has installed the build from this branch on the home laptop and Central and restarted both nodes:
  1. On the home laptop (PowerShell 5.1): `zyggy discover --dry-run` — the expected projects under the laptop's `devRoots` appear with their agents and skills. If the node's service environment lacks `ZYGGY_SECRET_PATTERNS`, stderr says "publishing names only" (see question Q3).
  2. On the bus (any checkout, after a pull): `tenants/<org>/registry/home-laptop.yaml` and `tenants/<org>/registry/central.yaml` exist with plausible content (`central.yaml` has `projects: []`).
  3. On Central: `zyggy submit --to home-laptop --project <a name from step 1> "Read README.md and finish with a REPORT section; change nothing."` then `zyggy status --id <id>` — the report shows `done`, run in that project's folder.
- [ ] User approved — implementation may continue past this gate *(the deliverable is done; commit, merge and any version bump are the owner's call)*

---

## Questions for the owner (recorded, not guessed)

**Resolved 2026-11-07:** the owner approved the plan at the 🛑 plan gate ("approve"), taking the recommendation of Q1, Q2 and Q3: A1 and A2 stand as written; for Q3 the owner sets `ZYGGY_SECRET_PATTERNS` on the node's scheduled task by hand for the final check, and automating it in `zyggy setup` is a follow-up item for the project-manager.

- **Q1 — When the first scan happens (A1).** The spec says the node waits for its first scan "after the listener has started". In the code, starting the listener already picks up and runs jobs waiting on the bus, so a job by name waiting at start would be refused before the scan exists. The plan therefore scans **before** the listener starts (publishing still happens after). Recommendation: accept; the orchestrating session can align the spec's Contract sentence afterwards.
- **Q2 — The health value in the very first registry commit (A2).** Right after start the node has not polled yet, so the shared health rule says `red` ("never polled"), and that value could sit on the bus for up to six hours. The plan makes the first publish wait until the node has polled once (at most 60 s). Recommendation: accept. Alternative: publish at once and accept a `red` first file.
- **Q3 — Descriptions on the home laptop.** The laptop setup (deliverable 42) writes `ZYGGY_SECRET_PATTERNS` into Claude Code's settings, not into the node service's environment. Without it, the node publishes project names and default branches only (fail closed, as the spec requires). For the owner check, do you set the variable on the node's scheduled task by hand (the spec's runbook route), or do you want a small follow-up so `zyggy setup` sets it for the service too? Recommendation: set it by hand for the gate; a follow-up item for the project-manager if you want it automated.
