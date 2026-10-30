# Plan: 13 — The dream reading the bus: Central picks up what the machines send, as it arrives; the nightly dream files it (closes P1) — "As the owner, working in a Claude Code session on the home laptop, I want a fact I tell the laptop (`zyggy context`, the session hooks) and the result of a job a laptop ran for Central to reach Central's memory within seconds, and to be filed into its category by the nightly dream, so that a Central session can use a laptop fact almost at once (`get_context`), memory still has one owner and one consolidation pass, and the P1 walking-skeleton gate closes"

> **Scheduling note.** Branch `feature/13-dream-pass` from `main` once 12 (`feature/12-context-envelopes`) is merged — this plan builds on 12's `BusRepository.PublishContextAsync`, `MachineName.Central`, `ContextSubmitter`, `zyggy context`, `CliHarness.PatternsPath` and the `SecretPatternsRegistration` facade. The owner creates the branch. The sibling template repository `zyggy-core` (`D:\source\zyggy-core`) gets its own branch `feature/13-pickup-runbook`, **created by the owner and stacked on `feature/12-node-context`** (12's template branch, not yet merged), for Step 13 only. Both repositories stay **uncommitted** at every 🛑 HUMAN GATE for the owner's review (auto-memory "Owner reviews before commit"). The agent never commits, pushes or tags. The ten founding-spec amendments **A13-1..A13-10** are **already pasted** (A13-10 being the report-as-one-line follow-up to §7 step 1) into `_specs/00 …` (§3 Node row and Central instance, §4 layout, write rules and state machine, §5 poll loop items 2–3 and Git operations, §6 Central executing its own jobs, §7 step 1 and Hub table, §10 `node.json` and Central units). This plan assumes them and edits the founding spec nowhere. The roadmap wording of entry 13 (Goal: "the timer submits a job", "`zyggy dream ingest|rollup` verbs") is the project-manager's edit, not this plan's.

> **Status: approved by the owner (2026-10-29)** with Q-P1 = (b) and Q-P2 = `<instance-dir>`. The analyst amended `_specs/13-dream-pass.md`: AC-2 is now context-only, and the new AC-3/AC-3a say a report becomes exactly one `[observed]` line. The founding-spec follow-up A13-10 (§7 step 1) is already pasted. Steps 2, 3 and 9–11 and their gates reflect the new report shape.

## Overview

After this plan, Central's node (`node.json` `machine: central`, the only machine where this happens) runs a **pickup** stage (`BusPickup`) after every successful pull. It runs after the start-up pull, after a `Changed` tick and after a safety fetch that found the branch moved, each time after that pull's job pass, never on a `304` and at most once per pull. The pickup reads every `reports/*.md` and `context/*.md` of every other machine's folder of the tenant. Each file is checked: tenant, then signature, then the writer rules (`from` = folder machine, `type` = folder, file name = `id`). Files addressed `to: central` reach the inbox in two ways:
- A context envelope's body is copied verbatim into `<memory root>/acme/alice/inbox/bus-context-<from>-<id>-<date>.md`, with secret-shaped lines dropped by pattern name.
- A report becomes `bus-report-<from>-<job>-<id>-<date>.md` whose body is **exactly one** `FactLine` line `- [observed] <created UTC date> [report <job id first 8>]: <first non-empty body line, trimmed>` (Q-P1 = (b), AC-3). A `rejected` report, a report with no non-empty line, or one whose line matches a secret pattern gets no inbox file (AC-3a, AC-4) and is archived all the same; the full report stays readable in the archive. All processed files are then moved to `tenants/<tenant>/archive/<YYYY-MM of created>/` in **one** commit `acme archive <n> central→archive`, through the one §5 write sequence. This is `BusRepository.ArchiveAsync`, the one new `AssertMayWrite` branch. A failed or refused file stays where it is, with one warning per file per process lifetime. The pickup never calls a model, never runs git in the memory repository and never throws to the loop. Central's node refuses to start (exit 3) without the pickup configuration (`ZYGGY_MEMORY_ROOT`/`ZYGGY_TENANT`/`ZYGGY_USER`/`ZYGGY_TIMEZONE`/`ZYGGY_SECRET_PATTERNS`), which the shipped drop-in `deploy/systemd/zyggy-node.service.d/pickup.conf.example` supplies. Other changes:
- Central polls every second: `poll.activeSeconds: 1`, with the quiet-branch jitter capped at half the interval so 10 s and 60 s stay byte-identical.
- The archive-aware readers (`FindReportsAsync`, `FindUnverifiedReportsAsync`, the `zyggy status` recent-reports block) keep `zyggy probe` and `status` working once reports leave `reports/` within seconds.
- The Hub's `get_context` returns the dream-unconsumed lines of `inbox/bus-*.md` and `inbox/remember-*.md` in its own ≤ 20 % block (decision Q2). The `SessionStart` digest stays inbox-free.
- The nightly dream (28, unchanged) files a picked-up line and consumes it.
- `Gates/P1_WalkingSkeleton.cs` gains `#region Part 2 — 13`, which closes P1.

Implements `_specs/13-dream-pass.md` (approved 2026-10-29, zero Open Questions; O1–O3 and Q1–Q3 and the Contracts are binding: AC-1..AC-35 including AC-3a, the Decision Table's Keep/Reshape/Defer rows). Founding-spec sections: §3 (Node row, Central instance), §4 (Repository layout, Write rules, Envelope, Signature, State machine as amended), §5 (poll loop items 2–6, Git operations), §6 (Central executing its own jobs, `ZYGGY_HOOKS=off`), §7 (Layout, Context loading, Dream pass step 1 and Rules, Hub `get_context`), §8 (Injection, Secrets, Audit), §9 (folder map and design rules), §10 (`node.json`, Central units), §11 (Logging, Runbooks), §12 (P1 gate), §13, §14.

**Reference pattern** (read across all layers; mirrored here):
- **Bus write and read side**: `src/Zyggy.Core/Bus/BusRepository.cs`.
  - `ClaimAsync` is the `git mv` move: `AssertMayWrite` on both ends and `WriteAsync([from, to], mv)`.
  - The private `WriteAsync` is the §5 sequence: checkout write lock, `PullUnlockedAsync`, change, `CommitOnlyAsync` with `-F -` stdin so a multi-line message works, push ×4 with jitter, `RestoreAsync`, `Pushed | Deferred | WriteFailed`.
  - Also mirrored: `AssertMayWrite`, `Subject`, `RedactedDetail`, `VerifyIntakeAsync` (03's order: name, then tenant, then signature) and `FindReportsAsync` / `FindUnverifiedReportsAsync`.
- **Inventory and audit**: `BusInventory.cs` (machines under `BusPaths.Nodes`, the write-lock read, `ReportsAnswering` over `archive/`) and `BusAuditor.cs` (`JudgeEnvelopeAsync`: tenant, signature, `id_differs`, `type_folder_mismatch`, `writer_mismatch`; `AuditWire` names).
- **The loop**: `BusListener.cs` — `StartAsync`, `RunAsync` with `pulledThisTick`, `SafetyFetchAsync`, `PullAndNoteAsync`, `Scope(phase)`, `LoggerMessage` once per state change, `_pullErrorLogged` streak flags.
- **The poller**: `PollState.cs` (`Apply`, `Quiet`, `Throttle`, `BackOff`), `PollOptions.cs`, `NodeConfigurationReader.cs` (`TryPositiveInt` for `poll.*`).
- **Registration**: `Bus/ServiceCollectionExtensions.cs` (the `BusListener` transient factory with `sp.GetService<JobGate>()`) and `Configuration/ServiceCollectionExtensions.AddNodeServices`.
- **The host**: `src/Zyggy.Node/NodeHost.cs` — `Build`, `NodeStartupException(NodeExitCodes.Configuration, …)`, the `zyggy-node: configuration error: ` stderr line, `ProcessEnvironment()`.
- **Memory side**: `Memory/MemoryEnvironment.Resolve` (the `ZYGGY_*` messages), `Dream/DreamConfiguration.Load` (the secret-pattern sentences), `Memory/SecretPatterns.TryMatch`, `Memory/FactLineWriter` (the unquoted front matter), `Memory/MemoryFileWriter.WriteAtomically`, `Memory/MemoryPaths.InboxFile`, `Memory/MemoryStore` (`DailyLines`, `ReadText`), `Memory/ContextRanker` (`LayOutDaily`, `DailyMarker`, `Rank`), `Memory/ContextOptions`, `Hub/GetContextTool`, `Dream/DreamLedger.Load`, `Dream/LineHash.Of`, `Dream/MemorySnapshot.BodyLines` (only `- ` lines), `Dream/BatchPlanner.Source` (`-(YYYY-MM-DD)\.md$`), `Dream/Rollup.Plan`.
- **Unit test shapes**:
  - `tests/Zyggy.Core.Tests/Bus/BusListenerTests.cs`: `Listener(...)`, `TempCheckout`, `RecordingProcessRunner.On("mv", …)`, the substituted `IBusProvider`, `FakeTimeProvider`, `RecordingLogger<T>`.
  - `BusRepositoryWriteTests.cs`, `BusRepositoryIntakeTests.cs` (`FindReportsAsync_*`), `BusInventoryTests.cs`, `PollStateTests.cs`, `Configuration/NodeConfigurationReaderTests.cs`.
  - `Memory/ContextRankerTests.cs`, `MemoryStoreTests.cs`, `Hub/HubHarness.cs` / `HubServerGetContextTests.cs`, `Dream/BatchPlannerTests.cs`, `Deploy/SystemdUnitTests.cs` + `Deploy/Expected/`, `Infrastructure/SourceHygieneTests.cs`.
- **Integration shapes**:
  - `tests/Zyggy.Integration/Bus/PushRetryTests.cs` (`TestHost.Build`, `HookedProcessRunner.Override` refusing `push`) and `BusRepoFixture` (`SeedSignedReportAsync`, `SeedRawFileAsync`, `SenderDir`/`CloneDir`/`BareDir`, `ExistsOnMainAsync`, `ShowBytesAsync`, `DeleteBare`).
  - `Node/NodeHostHarness.cs` (`WriteNodeJson`, `Configure`, `RunUntilAsync`, `WaitUntilAsync`), `Node/NodeHostStartupTests.cs`, `Node/ZyggyNodeProcessTests.cs`.
  - `Cli/StatusCommandTests.cs`, `Cli/ContextCommandTests.cs`, `Dream/DreamEndToEndTests.cs` over `MemoryRepoFixture` (`SeedAsync`, `DreamAsync`, `MaterialiseScenario`).
  - `Gates/P1_WalkingSkeleton.cs` (part 1/1b helpers `CentralNodeJson`, `CliAsync`, `CliServices`).
- **Template**: `zyggy-core/runbooks/install-node.md` (Central section) and `tests/repo.bats` (the runbooks test at line 966).

**Phase**: 13 **closes P1**. The last slice (E) is the gate slice of planner rule 15. It appends `#region Part 2 — 13` to `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` (parts 1 and 1b untouched), ships the drop-in, the runbook rows and the template runbook, and its final 🛑 HUMAN GATE carries the two owner-executed items (AC-24 latency measurement, AC-31 one real nightly dream reviewed).

**What this plan deliberately is not** (spec Out of Scope / Defer):
- No `zyggy dream ingest` or `zyggy dream rollup` verb.
- No change to `zyggy dream`, its prompts, checks, caps, ledger format or units. No dream bus job, no `JobRunner` change.
- No 14-day sweep (17), no `agents.md` (14), no Telegram diff (22).
- No `job_status` / `list_nodes` / `/health` pickup fields (21).
- No webhook, socket or inbound endpoint. No policy-governed intervals (18). No `role` key in `node.json`.
- No local "already picked" state file. No DLP or `share: true` (19/20/24).
- No `PROTOCOL.md` content (15 publishes it; the template's stays the placeholder).
- No change to `EnvelopeSigner`, `EnvelopeWriter`, the canonical form, any envelope field or any file under `tests/golden/` (`git diff tests/golden/` empty at every gate; `tool-get_context.json` keeps its description because the tool's input and framing are unchanged).
- No new NuGet package: `Directory.Packages.props` stays at 24 rows, and `Packages_RowCountPinned` is the guard.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — Central copies what is addressed to it into the inbox and archives it | 1, 2, 3 | (1) Central moves verified `reports/`/`context/` files of other machines to `archive/<created month>/` in one commit, and every other archive write is refused. (2) One pickup pass verifies, filters `to: central`, drops secret-shaped context lines, turns a report into one `[observed]` line, writes one inbox file per envelope (never twice), archives, logs once and never throws. (3) The same pass works against the bare repository: bytes identical in `archive/`, inbox verbatim, the memory repository untouched, every failure path named. | 🛑 after Step 3 (⚠️ §4 write rule and state machine, Q3; ⚠️ secrets; ⚠️ public API) |
| B — Central's node picks up as it polls, every second | 4, 5, 6 | (4) The listener runs the pickup after every successful pull and only then. The pickup configuration loads with the dream's messages or names the key. (5) `PollState` caps the quiet jitter at half the interval, and `node.json` accepts `activeSeconds: 1`. (6) The production host as `central` at 1 s with the production jitter picks a pushed context up within seconds. Without the configuration it exits 3 with one stderr line. A laptop host never builds a pickup. | 🛑 after Step 6 (⚠️ §5 shared contract: jitter rule; ⚠️ host start-up contract) |
| C — The readers keep finding reports that left `reports/` | 7, 8 | (7) `FindReportsAsync`, `FindUnverifiedReportsAsync` and the inventory's recent reports also read `archive/` months. (8) After a real pickup, `zyggy status`, `status --id` and the probe's report search still show the report. | 🛑 after Step 8 |
| D — `get_context` sees a picked-up line at once; the dream files and consumes it | 9, 10, 11 | (9) `MemoryStore.PickedUpLines` + the ranker's third block under 20 % of the budget, unconsumed `bus-*`/`remember-*` lines only, digest unchanged. (10) The Hub in process returns the line of a real pickup verbatim. (11) `zyggy dream` with the fake `claude` files the `bus-context-…` line and the `bus-report-…` line and consumes both in the ledger, after which `get_context` no longer lists them in the inbox block. | 🛑 after Step 11 (⚠️ §7 shared contract, Q2) |
| E — Delivery and the P1 gate (gate slice) | 12, 13, 14 | (12) `pickup.conf.example` shipped and pinned; the Node README carries the Central example, the rate-limit paragraph and five troubleshooting rows; the Cli/Core READMEs and the hygiene facts are updated. (13) `zyggy-core/runbooks/install-node.md` names the drop-in as required on Central. (14) `Gates/P1_WalkingSkeleton.cs` part 2 runs `zyggy context` → production host as `central` → Hub `get_context`, with the digest ≤ 20,000 bytes and `verify` all `OK`. | 🛑 after Step 14 — **definition of done, closes P1** (owner items AC-24, AC-31) |

Every step ends with PROVE: `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally. Step 13 is proven by the `zyggy-core` bats suite in the local container (`MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/"`).

<!--
Decomposition strategy: vertical slices, not horizontal layers. Slice A proves the pickup through the seams (RecordingProcessRunner for git,
a substituted IBusProvider for pull/push, an InMemorySecretStore, a FakeTimeProvider, a MemoryTree — Steps 1–2) and wires it against the
local bare repository with the real git (Step 3). Slice B is the listener hook and configuration (fake, Step 4), the pure jitter rule
(Step 5) and the production host as central (wire, Step 6). Slice C is the archive-aware readers (fake 7, wire 8). Slice D is the Hub read
side (fake 9, wire 10) and the dream hand-over (11, fake-claude). Slice E is the delivery files, the template runbook and the P1 gate.
Never the real claude, never GitHub, never an owner name in any test.
-->

---

## Shared rules for this plan

- **Identity in every test, fixture and example**: tenant `acme`, user `alice`, machines `central` / `home-laptop` / `work-laptop`, project `calizr`, key `acme/1` (`TestKeys.Acme1`), the fact `The staging database is restored from Sunday's snapshot` (the spec's AC-1 body `- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot`), the unit-test clock at `2026-10-29T10:42:07Z` unless a test says otherwise. No owner name, GUID, mail address, real path (`/srv/…`, `/home/…`, `C:\Users\…`) or real id anywhere (`SourceHygieneTests`; Step 12's new fact). README and runbook paths are placeholders (`<instance-dir>`, `<zyggy-home>`, `<bus.localPath>`).
- **Order of one pass** (spec Behaviors, binding):
  1. `ListPickupCandidatesAsync` runs under the checkout write lock and reads the bytes once.
  2. Per file: `ParseAndVerifyAsync(bytes, location.Tenant)` (tenant before signature).
  3. The writer rules: file stem = `id`, `type` = folder (`context/` ↔ `Context`, `reports/` ↔ `Report`), `from` = folder machine.
  4. `to == MachineName.Central`, else silently skipped (no log, no move).
  5. A `rejected` report is archived only.
  6. The inbox lines:
     - Context: the body lines with secret-shaped lines dropped.
     - Report: exactly one line, `FactLine.Format("observed", "", "report " + job8, createdUtcDate, firstNonEmptyLine.Trim())`. When the body has no non-empty line, or that line matches a secret pattern, the report is archive-only (AC-3a).
  7. `PickupInbox.Write` (skipped when the file is present).
  Then one `ArchiveAsync` for every file that reached the inbox or is archive-only, then one Information line. A file whose inbox write failed is not archived in that pass.
- **Never throws to the loop**: `BusPickup.RunAsync` catches everything except `OperationCanceledException` when `ct` is cancelled. One Error line names the exception type. No bus file is moved for a file whose inbox write failed.
- **Secrets**: a dropped context line, a refused report line, the rest of a report body (only its first non-empty line is ever copied), a refused file's body and an envelope body never appear in a log line, a commit message, a process argument or stdout. Only the pattern **name**, the envelope id and the path appear. Git stderr goes through `RedactedDetail`. `SecretSweep.AssertAbsent` runs over every log and output in the integration tests.
- **Paths**: every bus path through `BusPaths` (`Nodes`, `Context`, `Reports`, `Archive`, `ArchiveRoot`, `EnvelopeFile`, `Resolve`, `TryParse`). Archive month folders are enumerated in one helper, `ArchiveMonths.Folders` (A5), which turns each `YYYY-MM` directory name into `BusPaths.Archive(tenant, year, month)`; no `+ "/" +` concatenation of an archive path anywhere. Every memory path goes through `MemoryPaths` (`InboxFile`, `InboxDirectory`, `Ledger`).
- **Log once per state change** (§11): Information once per pass that moved at least one file; Warning once per left file per process lifetime (a `HashSet<string>` of paths in `BusPickup`); Warning once per dropped line; Error once per archive-failure streak and once per inbox-write-failure streak. Nothing on a pass with nothing to do. Every line is scoped `tenant`, `machine`, `phase=pickup`.
- **Shared-contract edits**:
  - In this plan: §4 write rule and state-machine row (Step 1, Q3/A13-4..6); §5 jitter (Step 5, O2/A13-7); §7 `get_context` (Step 9, Q2/A13-9); the host's start-up contract (Step 6, A13-8). All of these are already in the founding spec.
  - Untouched: `EnvelopeSigner`, `EnvelopeWriter`, `EnvelopeParser`, `BusAuditor`, `BusPaths` (no new member: the A5 helper lives beside it), `DigestBuilder`, `DreamRunner`, `BatchPlanner`, `Rollup`, `tests/golden/*`.
- **Two repositories, two branches, nothing committed**: `zyggy` on `feature/13-dream-pass`, `zyggy-core` on `feature/13-pickup-runbook` (stacked on `feature/12-node-context`). The executor reports per repository at each gate.

### Assumptions (reviewer: confirm or correct at the gate named)

- **A1** (Gate A) — **Visibility.**
  - `BusPickup`, `PickupResult`, `PickupLeftReason`, `PickupCandidate` and `PickupConfiguration` are public, as the Contracts name them.
  - `BusPickup`'s constructor is `internal`, because it takes the internal `PickupInbox` and `SecretPatterns`.
  - `PickupConfigurationResult` is a public `sealed record` whose `SecretPatterns`/`PickupInbox`-bearing members are `internal`.
  - The Node builds the pickup through one new public Core extension, `Bus/ServiceCollectionExtensions.AddBusPickup(this IServiceCollection, PickupConfigurationResult)`. `NodeHost` then names no memory, secret or bus type beyond it (`Src_Node_NoProcessHttpClientGitHubClaudeOrSecretStoreReference` stays green).
  - `Zyggy.Node` (assembly `zyggy-node`) already has `InternalsVisibleTo` but does not need it.
- **A2** (Gate A) — **Counting and the Information line.**
  - A file whose inbox file was already present (AC-10) counts as `ArchivedOnly`, as do a `rejected` report (AC-4), a context body left with no line (AC-9) and a report with no non-empty line or a refused line (AC-3a).
  - `Inboxed` counts only `Created`.
  - The line is `picked up {n} file(s) from {m} machine(s): {k} to inbox, oldest created {yyyy-MM-ddTHH:mm:ssZ}; archive {pushed|deferred}`, where n = `Inboxed + ArchivedOnly`, m = distinct folder machines of the archived files, k = `Inboxed`.
  - No Information line when the archive write failed; the Error line replaces it.
- **A3** (Gate A) — **Report line details** (AC-3):
  - "First non-empty line" means the first line of `BodyText` (split on `\n`, `\r` removed) with a non-whitespace character, then `Trim()`. No collapsing of inner whitespace and no length cut, because the spec names neither (the REPORT body is already bounded by §6's 2,000 characters).
  - `job8` = the first 8 characters of `in_reply_to`.
  - The date is the report's `created` in UTC, `yyyy-MM-dd`.
  - **Context bodies** — "a body left with no line" (AC-9) means no line with a non-whitespace character remains after the secret scan. Blank lines inside a kept body are copied as they are. The body is split on `\n` only, with a trailing empty element from the final newline dropped; a `\r` inside a line is kept verbatim (the bytes are copied, not normalised).
- **A4** (Gate A) — **Log texts the spec does not spell out.**
  - Left file: `pickup: left {Path}: {Reason} ({Field})`, where `Reason` is the §4 wire name via `EnvelopeWire`/`AuditWire` (`tenant_mismatch`, `malformed`, `schema_unsupported`, `key_id_tenant_mismatch`, `missing_field`, `invalid_field`, `missing_signature`, `unknown_key`, `invalid_signature`, `writer_mismatch`, `type_folder_mismatch`, `id_differs`) and `Field` is `-` when there is none.
  - Dropped context line: `pickup: {Id}: dropped 1 line (pattern {Name})`, one per line (Behaviors: "Warning per dropped secret-shaped line").
  - Refused report line: `pickup: {Id}: report line refused (pattern {Name})` (AC-3a, verbatim from the spec). A report with no non-empty line logs nothing.
  - Inbox write failure: `pickup: inbox write failed for {Id}: {ExceptionType}`.
  - Archive failure: `pickup: archive failed at {Stage}: {Detail}`.
  - Unexpected exception: `pickup: pass failed: {ExceptionType}`.
- **A5** (Gate C) — **Archive months.** `src/Zyggy.Core/Bus/ArchiveMonths.cs`: `internal static class ArchiveMonths { public static IReadOnlyList<string> Folders(string checkoutRoot, TenantId tenant); }`. It enumerates the directories under `BusPaths.Resolve(root, BusPaths.ArchiveRoot(tenant))`, keeps the names that parse as `yyyy-MM` (`DateOnly.TryParseExact(name + "-01", "yyyy-MM-dd")`), and returns `BusPaths.Archive(tenant, year, month)` in ordinal order. `BusInventory.ReportsAnswering` is refactored onto it (its one `ArchiveRoot(…) + "/" + …` concatenation goes away; `BusInventoryTests` unedited and green).
- **A6** (Gate B) — **Environment injection for the host.** `NodeHost.Build(string[] args, Action<IServiceCollection>? configure = null, IReadOnlyDictionary<string, string?>? environment = null)` and `NodeHost.RunAsync(…, IReadOnlyDictionary<string, string?>? environment = null)`. `null` means the process environment (today's behaviour, `Program.cs` unchanged). The pickup configuration and `ResolvePath` read the same dictionary. `NodeHostHarness` gains `public Dictionary<string, string?>? Environment { get; set; }` passed to both, and `WriteNodeJson(…, MachineName? machine = null)` (default `HomeLaptop`; every existing caller unchanged).
- **A7** (Gate B) — **Start-up pull.** The pickup runs after the start-up pull **only when that pull succeeded** (the Contracts: "after every successful pull — StartAsync, …"). A failed start-up pull is logged as today, and the next `Changed` tick or moved safety fetch picks up.
- **A8** (Gate B) — **The tenant mismatch message** (AC-16): `ZYGGY_TENANT '{value}' is not node.json's tenant '{tenant}'`. The other details are verbatim:
  - `MemoryEnvironment.Resolve`'s sentences, the same text as `DreamConfiguration`'s for those keys.
  - `DreamConfiguration`'s two secret-pattern sentences (`ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR to derive it from)`, `secret patterns {path} are missing (ZYGGY_SECRET_PATTERNS)`), extracted into one internal helper `SecretPatternsLocation.ResolveForInstance(environment)` → `(string? Path, string? Error)`. `DreamConfiguration` is refactored onto it, and `DreamConfigurationTests` stay unedited and green.
  - `SecretPatterns.Load`'s error for a malformed file.
- **A9** (Gate C) — **`InventoryReport` gains `public int ReportsTotal { get; init; }`**: live `reports/` files plus archive files whose peeked `type` is `report`. `StatusCommand` prints `recent reports ({k} of {ReportsTotal})`. The per-machine table stays live counts. A positional parameter is not added, so existing constructions compile.
- **A10** (Gate D) — **Inbox candidates for `get_context`**:
  - Only the lines `MemorySnapshot.BodyLines` yields (body lines starting `- `, the lines the dream's ledger hashes), not yet in the ledger under `inbox/<name>` with `LineHash.Of`.
  - Matched against the topic's terms exactly as daily lines are (any term, ordinal-ignore-case `Contains`).
  - Ordered by the file-name date (`-(YYYY-MM-DD)\.md$`) descending, then the file name descending (ordinal), lines in file order.
  - `ContextRanker.Rank` gains an optional trailing `IReadOnlyList<InboxLine>? inbox = null`, so `ContextRankerTests` compile unedited. `ContextResult` gains `InboxLinesIncluded`/`InboxLinesOmitted` with defaults.
  - The block order is files → inbox → daily. `NoMatch` only when all three are empty.
- **A11** (Gate D) — **The ledger for the Hub**: `MemoryStore.Ledger()` reads `paths.Ledger` with `ReadText` and returns `DreamLedger.Load(text).Ledger`. A missing file gives an empty ledger; `Invalid`/`Unsupported`/unreadable gives `null`, which `PickedUpLines(null)` treats as "nothing consumed" (AC-27).
- **A12** (confirmed by the owner, Q-P2) — **The drop-in's placeholders** follow the existing `<zyggy-home>` convention, `<instance-dir>` for the instance checkout; no `/srv/` path, and no hygiene fact loosened:
  - `Environment=ZYGGY_MEMORY_ROOT=<instance-dir>/memory`
  - `Environment=ZYGGY_TENANT=acme`
  - `Environment=ZYGGY_USER=alice`
  - `Environment=ZYGGY_TIMEZONE=UTC`
  - `Environment=ZYGGY_SECRET_PATTERNS=<instance-dir>/.claude/hooks/secret-patterns.txt`
- **A13** (Gate E) — **The gate's memory tree** is a scratch copy of `tests/golden/digest/sided/` (copied to the Integration output as `golden/digest/sided/`, read-only use, nothing under `tests/golden/` edited). It has identity files, two sides, a ledger and a `remember-` inbox file: a realistic tree whose digest is far under 20,000 bytes.

### Owner decisions at the plan gate (resolved 2026-10-29)

- **Q-P1 — resolved: (b), a report becomes one `[observed]` line.** The problem: the dream only offers, hashes and consumes body lines starting with `- ` (`MemorySnapshot.BodyLines` → `BatchPlanner`, `Rollup`). So a verbatim prose report would never have been filed, and its inbox file never rolled up. The spec now says (AC-3, AC-3a, Contracts report variant, A13-10):
  - A picked-up report becomes exactly one line, `FactLine.Format("observed", "", "report <job8>", <created UTC date>, <first non-empty body line, trimmed>)`, for example `- [observed] 2026-10-29 [report 01K8ABCD]: Root cause is the expired staging certificate`.
  - A report whose body has no non-empty line, or whose line matches a secret pattern, is archived with no inbox file. The secret case logs one warning naming the pattern.
  - Context envelopes stay verbatim (AC-2, context-only).
- **Q-P2 — resolved: `<instance-dir>`.** The drop-in uses the `<instance-dir>` placeholder (A12). `SystemdFiles_LfOnlyPlaceholdersOnlyNoSecret` and the `zyggy-core` runbooks bats test keep refusing `/srv/`.

---

## Step 1 — Central moves verified `reports/` and `context/` files of the tenant to `archive/<YYYY-MM of created>/` in one commit and refuses every other archive write

`BusRepository.ArchiveAsync` takes the list of files and their `created` instants. For each file it creates the month folder, runs one `git mv` into `BusPaths.EnvelopeFile(BusPaths.Archive(tenant, created.Year, created.Month), id)`, then makes **one** commit, subject `acme archive <n> central→archive` with one body line `<from path> → <to path>` per move, and pushes with the four-attempt retry: `Pushed | Deferred | WriteFailed(lock|pull|mv|commit)`, with the checkout restored on failure. `AssertMayWrite` gains exactly one branch: only `central`, only from `nodes/<any machine>/{reports,context}/` of its own tenant, only to its own tenant's `archive/YYYY-MM/`. `ListPickupCandidatesAsync` reads, under the write lock, the bytes of every `*.md` in `context/` and `reports/` of every machine except `central`, in ordinal order.

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/PickupCandidate.cs` *(create)*: `public sealed record PickupCandidate(BusFile File, MachineName Folder, BusFolder Kind, byte[] Bytes);` (Kind ∈ {`Context`, `Reports`}).
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify)*:
  - `public async Task<IReadOnlyList<PickupCandidate>> ListPickupCandidatesAsync(CancellationToken ct)`:
    - Takes the checkout write lock with `LockTimeout`; when the lock is busy it throws `BusBusyException`, as `BusInventory` does.
    - Machines are the directories under `BusPaths.Resolve(root, BusPaths.Nodes(tenant))` that parse as `MachineName`, ordinal order, `!= MachineName.Central`.
    - Per machine it reads `BusPaths.Context(...)` then `BusPaths.Reports(...)`: top-level `*.md` in ordinal order. The `BusFile` id is the ULID stem, else a synthetic id as in `ListFolderAsync`. Bytes are read with `File.ReadAllBytesAsync`.
    - Never another tenant's subtree.
  - `public Task<WriteOutcome> ArchiveAsync(IReadOnlyList<(BusFile File, DateTimeOffset Created)> files, CancellationToken ct)`:
    - Throws `ArgumentNullException`, and `ArgumentException` when the list is empty.
    - Throws `InvalidOperationException("write rule: only central archives")` when `_me.Machine != MachineName.Central`.
    - For each file: `to = BusPaths.EnvelopeFile(BusPaths.Archive(_me.Tenant, created.UtcDateTime.Year, created.UtcDateTime.Month), file.Id)`, then `AssertMayWrite(from, BusFolder.Reports|Context, archiveMove: true)` and `AssertMayWrite(to, BusFolder.Archive, archiveMove: true)`.
    - `WriteAsync(Subject("archive", count, "central", "archive"), [.. froms, .. tos], change, ct, body: string.Join('\n', moves.Select(m => $"{m.From} → {m.To}")))`, where `change` does `Directory.CreateDirectory(BusPaths.Resolve(root, monthFolder))` and then `Failure("mv", await _git.MoveAsync(root, from, to))` per file, stopping at the first failure.
  - `WriteAsync` gains an optional `string? body = null`: the commit message becomes `subject + "\n\n" + body` when it is set. `LogWriteFailed`/`LogDeferred` keep the subject only.
  - `Subject` gains an overload `Subject(string action, int count, string from, string to)` → `<tenant> archive <n> central→archive`.
  - `AssertMayWrite(string relativePath, BusFolder expectedFolder, bool submission = false, bool archiveMove = false)`: when `archiveMove` is set, the only checks are `_me.Machine == MachineName.Central`, the path's tenant is `_me.Tenant`, and the folder is `expectedFolder` ∈ {`Reports`, `Context`, `Archive`}; the machine check is skipped for a source in another machine's folder. Every other path is unchanged.
- `tests/Zyggy.Core.Tests/Bus/BusRepositoryArchiveTests.cs` *(create)*.

**Seams**:
- `IProcessRunner` faked: `RecordingProcessRunner` records `mv` and `commit` (stdin = the message), with `mv` scripted to fail once.
- `IBusProvider` substituted: `PullAsync` succeeds or fails, `PushAsync` succeeds or returns `Rejected("fetch first")` ×4.
- `TimeProvider` faked: the retry delays advanced as `JobSubmitterSubmitTests` does.
- `TempCheckout(Acme, Central)` holds the dropped files.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusRepositoryArchiveTests.cs`:
  - `ListPickupCandidatesAsync_ContextAndReportsOfOtherMachines_OrdinalOrderWithBytes`: files under `home-laptop/context`, `home-laptop/reports` and `work-laptop/context` are returned in that order with exact bytes; `.gitkeep` is ignored.
  - `ListPickupCandidatesAsync_CentralsOwnFolders_NeverListed` (AC-5).
  - `ListPickupCandidatesAsync_JobsClaimedRejectedRegistryArchive_NeverListed`.
  - `ListPickupCandidatesAsync_OtherTenantSubtree_NeverOpened`: a file under `tenants/other-org/nodes/home-laptop/context/` is absent from the result, and no directory under `tenants/other-org` is enumerated (fingerprint unchanged).
  - `ListPickupCandidatesAsync_LockHeld_ThrowsBusBusy`.
  - `ArchiveAsync_TwoContextsOneReport_OneMvEachOneCommitSubjectAndBodyPushed` (AC-13 through the seam): three `mv` calls with the exact `from`/`to`; one `commit` whose stdin is `acme archive 3 central→archive\n\n<from1> → <to1>\n<from2> → <to2>\n<from3> → <to3>`; the commit's path list holds all six paths; `Pushed(1)`.
  - `ArchiveAsync_ArchiveMonthIsCreatedMonthNotTodays` (clock 2026-11-02, `created` 2026-10-31T23:59:59Z → `archive/2026-10/`).
  - `ArchiveAsync_CreatedInTheFuture_ThatMonthsFolder` (edge case: skew).
  - `ArchiveAsync_PullFails_WriteFailedPull_NoMvNoCommit` (AC-11 `pull`).
  - `ArchiveAsync_MvFails_WriteFailedMv_CheckoutRestored_NoCommit` (AC-11 `mv`).
  - `ArchiveAsync_LockHeld_WriteFailedLock` (AC-11 `lock`).
  - `ArchiveAsync_EveryPushRejected_DeferredAttemptsFour_CommitLocal` (AC-12 through the seam).
  - `ArchiveAsync_NotCentral_ThrowsWriteRule_NothingCalled`.
  - `ArchiveAsync_SourceInJobsOrClaimed_ThrowsWriteRule`.
  - `ArchiveAsync_SourceInAnotherTenant_ThrowsWriteRule`.
  - `ArchiveAsync_EmptyList_ThrowsArgument`.
  - `ArchiveAsync_CentralsOwnReport_Allowed`: the write rule permits any machine's folder (17's sweep), even though the pickup never lists it.
  - `ClaimReportRejectSubmitPublishContext_WriteRulesUnchanged`: the existing `BusRepositoryWriteTests` / `BusRepositoryIntakeTests` run unedited — named in VERIFY, not a new test.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryArchiveTests"`

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- The changes above. `WriteAsync`, `RestoreAsync`, `PullUnlockedAsync` and `RedactedDetail` are reused. No `+ "/" +` path concatenation is introduced; the month folder comes from `Path.GetDirectoryName` of `to`, or is kept from `BusPaths.Archive(...)`.

**Contract impact**: ⚠️ **§4 shared contract (Q3; A13-4, A13-5, A13-6 already in the founding spec)**: the new state-machine row, the write-rule branch, the `created`-month folder and the commit action `archive` with its body lines are fixed here. ⚠️ **Public API**: `PickupCandidate`, `ListPickupCandidatesAsync`, `ArchiveAsync`.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`).
- `BusRepositoryArchiveTests` pass. `BusRepositoryWriteTests`, `BusRepositoryIntakeTests`, `BusAuditorTests`, `JobSubmitterSubmitTests` and `ContextSubmitterSubmitTests` pass **unedited**.
- `Src_BusPathLiterals_OnlyInBusPaths` is green.
- `git diff src/Zyggy.Core/Bus/BusPaths.cs src/Zyggy.Core/Bus/BusAuditor.cs src/Zyggy.Core/Envelope/ tests/golden/` is empty.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — One pickup pass copies every verified context addressed to Central into the inbox verbatim, turns every report into one `[observed]` line, leaves every refused file in place with one warning, and archives in one commit without ever throwing

`PickupInbox.Write` creates `inbox/bus-context-<from>-<id>-<created UTC date>.md` or `inbox/bus-report-<from>-<in_reply_to>-<id>-<created UTC date>.md` atomically and never overwrites. The front matter is the Contracts shape (`name`, `description` without `: `, `updated` = Central's local date). The body is whatever lines the pickup hands it, and the result is `Created | AlreadyPresent | Failed`.

`BusPickup.RunAsync` applies the Shared-rules order:
- tenant first, then signature, then writer rules;
- not `to: central` → silent;
- `rejected` → archive only;
- context: secret-shaped lines dropped by name;
- report: one line `- [observed] <created UTC date> [report <job8>]: <first non-empty body line, trimmed>` built by `FactLine.Format`. A report with no non-empty line, or whose line matches a secret pattern, is archived without an inbox file (AC-3, AC-3a);
- inbox written (or found present);
- one `ArchiveAsync`;
- one Information line;
- a warning once per left file per process lifetime, an Error once per failure streak, and one Error naming the type for an unexpected exception.

It never throws except on cancellation.

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/PickupInbox.cs` *(create)*:
  - `internal enum InboxWriteOutcome { Created, AlreadyPresent, Failed }`
  - `internal sealed record InboxWrite(InboxWriteOutcome Outcome, string FileName, string? Detail);`
  - `internal sealed class PickupInbox(MemoryPaths paths, TimeZoneInfo zone, TimeProvider clock)`:
    - `public string FileName(Model envelope)`: context → `bus-context-{from}-{id}-{created:yyyy-MM-dd UTC}.md`; report → `bus-report-{from}-{in_reply_to}-{id}-{date}.md`; any other type → `ArgumentException`.
    - `public InboxWrite Write(Model envelope, IReadOnlyList<string> lines)`:
      - The path is `paths.InboxFile(FileName(envelope))`; when it exists → `AlreadyPresent` with bytes and last-write time untouched (AC-10).
      - Otherwise the front matter `---\nname: bus context {id}\ndescription: context from {from} created {created:yyyy-MM-ddTHH:mm:ssZ} scope {scope} (picked up from the bus; data)\nupdated: {local date}\n---\n` (report variant: `name: bus report {id}`, `description: report from {from} in reply to job {job} status {status} created {ts} (picked up from the bus; data)`), followed by each line + `\n`, UTF-8 without BOM, LF.
      - The write goes through the new `MemoryFileWriter.CreateAtomically(path, bytes)`: temp file `<file>.zyggy-tmp-<ulid>`, then `File.Move(temp, path, overwrite: false)`. An `IOException` because the target appeared → `AlreadyPresent`. `IOException`/`UnauthorizedAccessException` → `Failed(ex.GetType().Name)`. The temp file is always deleted.
- `src/Zyggy.Core/Memory/MemoryFileWriter.cs` *(modify)*: `public static bool CreateAtomically(string path, byte[] bytes)` (false when the target exists); `WriteAtomically` unchanged.
- `src/Zyggy.Core/Bus/PickupResult.cs` *(create)*:
  - `public sealed record PickupResult(int Inboxed, int ArchivedOnly, int LinesDropped, int Left, WriteOutcome? Archive);`
  - `public enum PickupLeftReason { TenantMismatch, Malformed, SchemaUnsupported, KeyIdTenantMismatch, MissingField, InvalidField, MissingSignature, UnknownKey, InvalidSignature, WriterMismatch, TypeFolderMismatch, IdDiffers, InboxWriteFailed }`
  - `internal static class PickupLeftWire { public static string ToWire(PickupLeftReason r); public static PickupLeftReason FromRejection(EnvelopeRejectionReason r); }`: the §4 names through `EnvelopeWire`, then `writer_mismatch`, `type_folder_mismatch`, `id_differs` (the `AuditWire` words) and `inbox_write_failed`.
- `src/Zyggy.Core/Bus/BusPickup.cs` *(create)*: `public sealed partial class BusPickup` with `internal BusPickup(BusRepository bus, EnvelopeSigner signer, PickupInbox inbox, SecretPatterns patterns, TimeProvider clock, ILogger<BusPickup> log)` and `public async Task<PickupResult> RunAsync(CancellationToken ct)`, behaving as in the step title and the Shared rules.
  - Private state: `HashSet<string> _warnedLeft`, `bool _archiveFailureLogged`, `bool _inboxFailureLogged` (reset on the next success).
  - `LoggerMessage` methods: `LogPickedUp` (Information), `LogLeft` (Warning), `LogDroppedLine` (Warning), `LogReportLineRefused` (Warning, `pickup: {Id}: report line refused (pattern {Name})`), `LogArchiveFailed` (Error), `LogInboxFailed` (Error), `LogPassFailed` (Error), with message texts as in A2/A4.
  - Every line is inside a `tenant`/`machine`/`phase=pickup` scope (the `BusListener.Scope` shape).
  - Context lines come from `envelope.BodyText` split on `\n`, with the trailing empty element removed (A3).
  - The report line comes from `internal static string? ReportLine(Model report)`: the A3 rules, then `FactLine.Format("observed", string.Empty, "report " + report.Header.InReplyTo!.Value[..8], created.UtcDateTime.ToString("yyyy-MM-dd"), first.Trim())`; it returns `null` when the body has no non-empty line. The secret scan runs on the produced `fact` text, and a match means archive only.
  - `PickupResult.LinesDropped` counts both dropped context lines and refused report lines.
- `tests/Zyggy.Core.Tests/Memory/PickupInboxTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Bus/BusPickupTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/TestEnvelopes.cs` *(modify, additive)*: `SignedContextAsync(from, to, body, scope, created, tenant = acme, mutate = null)` and `SignedReportAsync(from, to, inReplyTo, status, body, created)`, which return the serialised bytes signed with `acme/1` through the production signer.

**Seams**:
- `IProcessRunner` faked: `RecordingProcessRunner` (`mv`, `commit` recorded; `mv` scripted to fail for AC-11).
- `IBusProvider` substituted: pull/push as in Step 1.
- `ISecretStore` faked: `InMemorySecretStore` via `TestKeys.StoreWithBothAsync`, with `acme/1` only, so a file signed `acme/2` is `unknown_key` (store built without it).
- `TimeProvider` faked: `FakeTimeProvider(2026-10-29T10:42:07Z)`; the zone is `HubHarness.Brussels` (+02:00), to prove `updated` is local.
- Files: `TempCheckout(Acme, Central)` and a `MemoryTree` for `acme/alice`; the patterns come from `tests/golden/secret-patterns/secret-patterns.txt`.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/PickupInboxTests.cs`:
  - `FileName_Context_BusContextFromIdCreatedUtcDate` (`created` 2026-10-29T23:30:00Z with zone +02:00 → name date `2026-10-29`, the UTC date; `updated: 2026-10-30`, the local date).
  - `FileName_Report_BusReportFromJobIdDate`.
  - `Write_Context_FrontMatterShapeAndBodyVerbatim_LfNoBom`: the exact bytes of the spec's example; the `description` contains no `": "`.
  - `Write_Report_DescriptionNamesJobStatusCreated`.
  - `Write_ContextStatedAndObservedLines_Unchanged_NoTagNoPrefixNoReorder` (AC-2, context only).
  - `Write_ReportOneLine_FrontMatterThenExactlyThatLine` (AC-3: the body is exactly `- [observed] 2026-10-29 [report 01K8ABCD]: Root cause is the expired staging certificate\n`).
  - `Write_ExistingFile_AlreadyPresent_BytesAndLastWriteUnchanged` (AC-10).
  - `Write_InboxNotWritable_FailedNamesExceptionType_NoTempLeft`: a read-only directory on Linux, or a file squatting on `inbox` on Windows.
  - `Write_NeverTouchesAnythingOutsideInbox` (`TreeFingerprint` of the tree minus `inbox/` is equal).
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusPickupTests.cs`:
  - `RunAsync_ContextToCentral_InboxFileThenOneArchiveCommit_InformationLineOnce` (AC-1 through the seam). The inbox file is present with the AC-1 body; one `mv` goes from `tenants/acme/nodes/home-laptop/context/<id>.md` to `tenants/acme/archive/2026-10/<id>.md`; the commit stdin starts `acme archive 1 central→archive`; `PickupResult(1, 0, 0, 0, Pushed)`; exactly one Information line `picked up 1 file(s) from 1 machine(s): 1 to inbox, oldest created 2026-10-29T10:42:07Z; archive pushed`.
  - `RunAsync_ReportDoneFailedOrTimeout_InboxFileHoldsExactlyOneObservedLine` (`[Theory]`, AC-3). The report body is `"\n  ## REPORT\nRoot cause is the expired staging certificate\n- files_changed: none\n"`. The inbox body is exactly `- [observed] 2026-10-29 [report <first 8 of J>]: ## REPORT\n`: the first non-empty line, trimmed. A second case with the body starting `Root cause …` gives that line. No other body line reaches the file.
  - `ReportLine_FirstNonEmptyLineTrimmed_DateIsCreatedUtc_SourceIsReportJob8` (AC-3 unit on `ReportLine`: CRLF body, leading blank and whitespace-only lines, `created` 2026-10-29T23:30:00Z with zone +02:00 → date `2026-10-29`).
  - `RunAsync_ReportFirstLineSecretShaped_ArchivedNoInboxFile_OneWarningNamesPattern_LineNowhere` (AC-3a: first line `key AKIAABCDEFGHIJKLMNOP` → `pickup: <id>: report line refused (pattern aws-access-key)`; `SecretSweep.AssertAbsent` over the logs and the inbox directory; one `mv`).
  - `RunAsync_ReportSecretOnALaterLine_NotCopiedAnyway_InboxHoldsFirstLineOnly` (AC-3: only the first non-empty line is ever copied, so a secret on line 3 never reaches memory and no warning is logged).
  - `RunAsync_ReportBodyBlankOnly_ArchivedNoInboxFileNoWarning` (AC-3a).
  - `RunAsync_ReportRejected_ArchivedNoInboxFile` (AC-4).
  - `RunAsync_ToIsNotCentral_NotCopiedNotMovedNoLogLine` (AC-6: a context `to: work-laptop` in `home-laptop/context/`).
  - `RunAsync_TenantFieldIsOtherOrg_LeftWarnedOnceTenantMismatch_NeverRead`: AC-7. `_log.Messages` holds exactly `pickup: left tenants/acme/nodes/home-laptop/context/<id>.md: tenant_mismatch (tenant)`; the store saw no key lookup.
  - `RunAsync_RefusedFiles_LeftWithReasonNeverBody` (`[Theory]`, AC-8):
    - malformed → `malformed`
    - no `sig` → `missing_signature`
    - signed `acme/2` (absent from the store) → `unknown_key`
    - one flipped signature byte → `invalid_signature`
    - `from: work-laptop` in `home-laptop/context/` → `writer_mismatch (from)`
    - a report in `context/` → `type_folder_mismatch (type)`
    - file name ≠ `id` → `id_differs (id)`
    
    In every case the body token is absent from every log message, no inbox file is written and no `mv` is called.
  - `RunAsync_LeftFile_SecondPass_NoSecondWarning` (AC-8 "once per file per process lifetime").
  - `RunAsync_SecretShapedSecondOfThreeLines_InboxHoldsLinesOneAndThree_WarningNamesPatternAndId_Archived` (AC-9: `AKIAABCDEFGHIJKLMNOP` on line 2; the warning `pickup: <id>: dropped 1 line (pattern aws-access-key)`; `SecretSweep.AssertAbsent` over the log messages and the inbox file).
  - `RunAsync_EveryLineSecretShaped_NoInboxFile_StillArchived` (AC-9; A3).
  - `RunAsync_InboxAlreadyPresent_NotRewritten_NoSecondFile_Archived` (AC-10).
  - `RunAsync_ArchiveMvFails_InboxKept_BusFilesStay_ErrorOncePerStreak_NextPassArchivesWithoutRewritingInbox` (AC-11: pass 1 has `mv` fail → one Error `pickup: archive failed at mv: …`; pass 2 has `mv` fail → no second Error; pass 3 succeeds → archived and the inbox bytes and last-write time are unchanged since pass 1).
  - `RunAsync_ArchiveLockBusyAtListing_ErrorOnceNothingWritten` (AC-11 `lock`).
  - `RunAsync_PushRefusedFourTimes_ArchiveDeferred_InformationSaysDeferred` (AC-12).
  - `RunAsync_TwoContextsAndOneReportFromTwoMachines_ThreeInboxFilesOneCommitOneInformationLine` (AC-13: `picked up 3 file(s) from 2 machine(s): 3 to inbox, …`).
  - `RunAsync_InboxWriteFails_ThatFileNotArchived_OthersAre_ErrorOnce` (AC-17 partial: Failure modes "Inbox not writable").
  - `RunAsync_UnexpectedException_ReturnsWithOneErrorNamingTheType_NeverThrows` (AC-17: a `TempCheckout` whose `nodes/` is replaced by a file mid-run, or a `SecretPatterns` built so that `TryMatch` throws, gives one Error `pickup: pass failed: <Type>`, and `RunAsync` completes).
  - `RunAsync_Cancelled_ThrowsOperationCanceled`.
  - `RunAsync_NothingToDo_NoLogLineNoCommit` (AC-19).
  - `RunAsync_EveryLineCarriesTenantMachinePhasePickupScope_NeverABodyLine` (AC-19).
  - `RunAsync_OnlyGitCallsAreTheBusCheckout_NoClaude` (AC-18: every recorded `ProcessSpec.WorkingDirectory` is the checkout root; no `FileName` ends with `claude`; `TreeFingerprint` of the memory tree outside `inbox/` is unchanged).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~PickupInboxTests|FullyQualifiedName~BusPickupTests"`

**GREEN**:
- The files above. No registration yet (Step 4).

**Contract impact**:
- ⚠️ **§7 inbox file format** (spec Contracts "Inbox file written by the pickup", including the report one-line variant of Q-P1 / A13-10) is fixed here. The dream reads it unchanged (Step 11 proves the hand-over for both kinds).
- ⚠️ **Secrets**: the Central-side re-scan of context lines and of the one report line.
- ⚠️ **Public API**: `BusPickup`, `PickupResult`, `PickupLeftReason`.
- No envelope or canonical-form change.

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- The two classes pass. `FactLineTests`, `FactLineWriterTests`, `SecretPatternsTests` and `MemoryFileTests` are unedited and green, and `git diff src/Zyggy.Core/Memory/FactLine.cs` is empty (reused, not changed).
- `Src_Bus_NoClockOrStaticState` and `Src_BusPathLiterals_OnlyInBusPaths` are green.
- `git diff tests/golden/` is empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 3 — Against the local bare repository, a context envelope pushed from `home-laptop` lands verbatim in Central's inbox and byte-identical in `archive/` on `main`

The context file pushed from `home-laptop` is archived on `main` in one commit `acme archive 1 central→archive`, and the memory repository gains no commit. Other observable results:
- Reports land too, and a batch of three files is one commit.
- A foreign-tenant or badly signed file stays on `main` untouched.
- A deleted remote leaves the inbox written and the bus files in place.
- Four refused pushes leave a local commit that Central's next bus write pushes.
- No git command runs in the memory tree.

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: `public static IServiceCollection AddBusPickup(this IServiceCollection services, PickupConfigurationResult configuration)`, which `TryAddSingleton`s the `BusPickup` factory over `BusRepository`, `EnvelopeSigner`, `new PickupInbox(configuration.Paths, configuration.Zone, clock)`, `configuration.Patterns`, `TimeProvider` and the logger (A1).
- `src/Zyggy.Core/Memory/PickupConfiguration.cs` *(create — the result record only; the static `PickupConfiguration.Load` joins this file in Step 4)*: `public sealed record PickupConfigurationResult(MemoryPaths? Paths, TimeZoneInfo? Zone, string? Error)` with `internal SecretPatterns? Patterns { get; init; }` and `internal static PickupConfigurationResult For(MemoryPaths paths, TimeZoneInfo zone, SecretPatterns patterns)` (A1).
- `tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs` *(modify, additive)*: `public async Task<SeededJob> SeedSignedContextAsync(MachineName from, MachineName to, KeyId signWith, ISecretStore store, string body, ContextScope? scope = null, DateTimeOffset? created = null, Func<byte[], byte[]>? mutate = null, CancellationToken ct = default)`. It writes under `BusPaths.Context(tenant, from)` and pushes from the sender with subject `<tenant> context <id> <from>→<to>` (the `SeedSignedReportAsync` shape).
- `tests/Zyggy.Integration/Infrastructure/PickupMemory.cs` *(create)*: a scratch memory tree `memory/acme/alice/` (with `profile.md`, `inbox/` and `daily/{today}.md`), `PickupConfigurationResult.For(new MemoryPaths(root, acme/alice), TimeZoneInfo.Utc, SecretPatterns.Load(golden).Patterns!)`, `InboxFiles()`, `FingerprintOutsideInbox()` over the linked `TreeFingerprint`, and `Env()`, the five `ZYGGY_*` variables for Steps 6 and 14.
- `tests/Zyggy.Integration/Bus/BusPickupTests.cs` *(create)*.

**Seams**: the real edge.
- The real `git` runs through `HookedProcessRunner` over the production runner, against `BusRepoFixture` with central over `CloneDir` and the seeding sender over `SenderDir`.
- The doorbell is `BareRepoHeadHandler`; the secrets come from `TestHost.StoreAsync(Acme)`.
- The pickup is resolved from `TestHost.Build(_bus, Central, _store, _hooks, logging: _log)` + `AddBusPickup(PickupMemory.Configuration)`.
- Never GitHub, never `claude`.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Bus/BusPickupTests.cs` (each test pulls the clone with `_bus.PullCloneAsync` before `pickup.RunAsync`, as the listener would):
  - `RunAsync_ContextFromHomeLaptop_InboxVerbatim_ArchivedByteIdenticalOnMain_OneCommit_MemoryUntouched` (AC-1):
    - `inbox/bus-context-home-laptop-<id>-<date>.md` has the Contracts front matter, and its body equals the envelope body byte for byte.
    - `ExistsOnMainAsync(context path)` is false. `ShowBytesAsync(BusPaths.EnvelopeFile(BusPaths.Archive(acme, y, m), id))` equals the seeded bytes. `LastCommitSubjectAsync == "acme archive 1 central→archive"`, and the body line is `<from> → <to>`.
    - `_hooks.Calls` holds no call whose working directory is under the memory root and none whose file name is `claude` (AC-18). `FingerprintOutsideInbox` is unchanged. The memory root has no `.git` call.
  - `RunAsync_ReportDoneFromHomeLaptop_InboxBusReportFileWithOneObservedLine_ArchivedInSameCommitAsAContext` (AC-3, the file half): the inbox body is exactly one `- [observed] <date> [report <job8>]: <first line>` line; the archived report on `main` is byte-identical to the seeded one, full body included.
  - `RunAsync_ReportFirstLineSecretShaped_ArchivedOnMain_NoInboxFile_WarningNamesPattern` (AC-3a against the bare repository; `SecretSweep.AssertAbsent` over the log and the inbox directory).
  - `RunAsync_TwoContextsAndAReportFromWorkLaptop_ThreeInboxFilesOneCommitBodyThreeLines_OneInformationLine` (AC-13, with `work-laptop` added to the seeded tenant).
  - `RunAsync_ForeignTenantFieldUnderAcme_LeftOnMainByteIdentical_WarnedOnce_VerifyReportsIt` (AC-7). The raw file is seeded with `tenant: other-org`. After the pass, `zyggy verify` in process with central's `node.json` over `CloneDir` reports that path `tenant_mismatch`.
  - `RunAsync_FlippedSignatureByte_LeftOnMain_NoInboxFile_WarnedOnceAcrossTwoPasses` (AC-8, the signature case).
  - `RunAsync_SecretShapedLine_DroppedFromInbox_Archived_ValueNowhereInLogsOrInbox` (AC-9).
  - `RunAsync_BareRepositoryDeleted_InboxWritten_BusFilesStayInLiveFolders_ErrorOnce_ThenRecreatedRemoteArchivesWithoutRewritingInbox` (AC-11 `pull`). `Bus.DeleteBare()` comes after the clone pulled. For the recovery half, the test re-initialises the bare repository from the clone (`git push --mirror` from `CloneDir` into a new bare) or, if the fixture cannot, ends at the failure half and the recovery stays proven by Step 2.
  - `RunAsync_EveryPushRefused_DeferredLocalCommit_FilesGoneFromLiveFolders_NextPickupPassPushesBoth` (AC-12: `_hooks.Override` refuses `push`; `RevListCountAsync("origin/main..HEAD") == 1`; then `Override = null`, a second context is seeded and pulled, and a second pass gives `main` both archive moves).
  - `RunAsync_CentralsOwnReportOnMain_NeverTouched` (AC-5).
  - `RunAsync_AllLogLines_NoBodyNoKey` (`SecretSweep.AssertAbsent(_log.Lines)` and the AC-1 body text absent from the logs).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Zyggy.Integration.Bus.BusPickupTests"`

**GREEN**:
- `AddBusPickup`, the fixture and helper additions. A defect found here is fixed in `src/Zyggy.Core/Bus/` with a unit test added to Step 1's or Step 2's class first.

**Contract impact**: ⚠️ the §4 move as git sees it: `git mv` (a rename in `git log --follow`), one commit, the `archive` action. `BusAuditor` judges archived files `OK` (no writer rule under `archive/`, unedited).

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `BusPickupTests` (integration) pass. `PushRetryTests`, `ClaimAndReportTests`, `RejectionTests` and `VerifyCommandTests` pass unedited.
- `dotnet test Zyggy.slnx --filter "Category!=Integration"` is green.
- `git diff tests/golden/ tests/Zyggy.Integration/Gates/ Directory.Packages.props` is empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A *(covers Steps 1–3)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryArchiveTests|FullyQualifiedName~PickupInboxTests|FullyQualifiedName~BusPickupTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Zyggy.Integration.Bus.BusPickupTests"` are green. The executor pastes from one real run:
  - one inbox file in full;
  - `git -C <bare> log -1 --format=%B main` of the archive commit and `git show --stat` (the rename);
  - the AC-7 and AC-8 warning lines;
  - the AC-9 drop line;
  - the AC-11 Error line;
  - the AC-13 Information line.
- [x] Contract review (⚠️ §4, Q3 / A13-4..A13-6):
  - Only `central` archives, only from `reports/`/`context/` of its own tenant, only into its own `archive/YYYY-MM/`; the month is `created`'s.
  - One commit per pass with the subject and body as approved.
  - Nothing else in `AssertMayWrite` is loosened: the existing write-rule tests are unedited.
  - Central's own folders are never listed.
  - `BusAuditor` is unedited.
- [x] ⚠️ Risk review (secrets): tenant and signature are checked before any body byte reaches the inbox or a log line. A dropped line appears only as a pattern name. `SecretSweep` is clean over the logs, the inbox and the commit messages. The memory repository saw no git command.
- [x] ⚠️ Risk review (public API, A1): `PickupCandidate`, `ListPickupCandidatesAsync`, `ArchiveAsync`, `BusPickup` (internal constructor), `PickupResult`, `PickupLeftReason`, `PickupConfigurationResult` and `AddBusPickup` are new public surface, every member documented.
- [x] Report shape (Q-P1 = (b), AC-3/AC-3a, A13-10) reviewed:
  - A report reaches the inbox as exactly one `[observed]` line built by `FactLine.Format`, with the `[report <job8>]` source.
  - No other report line is copied.
  - A blank or secret-shaped first line gives an archive move and no inbox file, plus one `report line refused` warning for the secret case.
  - The executor pastes one `bus-report-…` inbox file.
- [x] Assumptions A1–A4 confirmed or corrected; `git diff tests/golden/ tests/Zyggy.Integration/Gates/ Directory.Packages.props` is empty; nothing committed.
- [x] User approved — implementation may continue past this gate

---

## Step 4 — The listener runs the pickup after every successful pull and never otherwise; Central's pickup configuration loads with the dream's messages or names the key that is missing

The pickup runs after the start-up pull (A7), after a `Changed` tick's pull and after a moved safety fetch's pull, each time after that pull's job pass. It never runs on a `304` or after the Idle→Active pass without a pull, and never twice for one pull (`pulledThisTick`). A listener without a pickup (every laptop, the probe verbs) behaves exactly as before. `PickupConfiguration.Load(environment, node)` returns the principal's paths, the zone and the loaded patterns, or one detail sentence (A8).

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusListener.cs` *(modify)*:
  - The constructor gains a trailing `BusPickup? pickup = null`, kept in `_pickup`, with the XML doc updated.
  - A private `Task PickupAsync(CancellationToken ct) => _pickup is null ? Task.CompletedTask : _pickup.RunAsync(ct)`, awaited:
    - in `StartAsync`, after `ProcessOnceAsync` **only when the pull succeeded**;
    - in `RunAsync`, right after the `ProcessOnceAsync` that follows `PullAndNoteAsync == true`;
    - in `SafetyFetchAsync`'s `Moved` branch, after its `ProcessOnceAsync`.
  - Nothing else changes.
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: the `BusListener` factory passes `sp.GetService<BusPickup>()`.
- `src/Zyggy.Core/Memory/PickupConfiguration.cs` *(modify — Step 3 created it with the result record)*:
  - `public static class PickupConfiguration { public static PickupConfigurationResult Load(IReadOnlyDictionary<string, string?> environment, NodeConfiguration node); }`. The steps:
    1. `MemoryEnvironment.Resolve(environment)`; on error → `Error = detail`.
    2. `ZYGGY_TENANT` ≠ `node.Tenant.Value` → the A8 sentence.
    3. `SecretPatternsLocation.ResolveForInstance(environment)` → path or sentence.
    4. `SecretPatterns.Load(path)` → patterns or its error.
- `src/Zyggy.Core/Memory/SecretPatternsLocation.cs` *(modify)*: `internal static (string? Path, string? Error) ResolveForInstance(IReadOnlyDictionary<string, string?> environment)`, with `DreamConfiguration`'s two sentences verbatim.
- `src/Zyggy.Core/Dream/DreamConfiguration.cs` *(modify)*: its pattern block calls `ResolveForInstance` (behaviour byte-identical).
- `tests/Zyggy.Core.Tests/Bus/BusListenerPickupTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Memory/PickupConfigurationTests.cs` *(create)*.

**Seams**:
- `IBusProvider` substituted (`ProbeHeadAsync` scripted `Unchanged`/`Changed`; `PullAsync` scripted).
- `TimeProvider` faked (`FakeTimeProvider` driving the loop, the `RunAsync_*` shape of `BusListenerTests`).
- `IProcessRunner` faked: `RecordingProcessRunner` with `mv` scripted to **fail** in every pickup pass, so each pass that finds a due file shows as exactly one `mv` attempt. The count of `mv` calls = the count of passes that saw the file. This is how the tests observe "a pass ran" without a test hook in production code.
- A real `BusPickup` over a `TempCheckout(Acme, Central)` holding one verified context `to: central`, and a `MemoryTree`.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusListenerPickupTests.cs` (AC-14):
  - `StartAsync_PullSucceeds_PickupRunsOnceAfterTheJobPass`: one `mv`, and the recorded order puts the `ListNewJobsAsync` read before the pickup's `mv` (assert via the order of the `RecordingProcessRunner` calls: job-pass `git` calls first).
  - `StartAsync_PullFails_NoPickup` (A7).
  - `RunAsync_ChangedTickPullSucceeds_PickupRunsOnce`.
  - `RunAsync_UnchangedTick_NoPickup`: the file is dropped after start, a `304` follows, and there is no `mv`.
  - `RunAsync_ChangedTickPullFails_NoPickup`.
  - `RunAsync_SafetyFetchMoved_PickupRunsOnce`.
  - `RunAsync_SafetyFetchMovedAndChangedSameTick_OnePullOnePickup` (`pulledThisTick`).
  - `RunAsync_IdleToActiveWithoutPull_NoPickup`.
  - `RunAsync_PickupPassFails_LoopKeepsPollingAndClaimingJobs` (AC-17: a pass whose `mv` fails and the next tick claims a seeded job as usual).
  - `Listener_WithoutPickup_ExistingBehaviourUnchanged`: the whole `BusListenerTests` class unedited and green, named in VERIFY.
- Unit test file: `tests/Zyggy.Core.Tests/Memory/PickupConfigurationTests.cs` (AC-16):
  - `Load_AllSet_PathsZonePatterns` (`ZYGGY_TIMEZONE` absent → UTC).
  - `Load_MissingVariable_DreamSentence` (`[Theory]` over `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT` and `ZYGGY_USER` → `<KEY> is not set`).
  - `Load_RootOrPrincipalDirectoryMissing_DreamSentence`.
  - `Load_TenantDiffersFromNodeJson_TenantSentence` (A8).
  - `Load_UnknownTimeZone_DreamSentence`.
  - `Load_PatternsUnset_NoInstanceDir_DreamSentence`.
  - `Load_PatternsDerivedFromInstanceDir`.
  - `Load_PatternsFileMissing_DreamSentence`.
  - `Load_PatternsMalformed_SecretPatternsError`.
  - `Load_SameInputs_SameDetailAsDreamConfiguration` (for each failing environment of the theories above, `PickupConfiguration.Load(...).Error == DreamConfiguration.Load(...).Error`, where both apply).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusListenerPickupTests|FullyQualifiedName~PickupConfigurationTests"`

**GREEN**:
- The changes above.

**Contract impact**: ⚠️ **§5 poll loop item 2 (A13-7, already in the founding spec)**: "on Central then run the pickup" is fixed here. `BusListener`'s public constructor gains an optional parameter (source-compatible).

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- The two classes pass. `BusListenerTests`, `DreamConfigurationTests`, `RememberVerbTests` and `HubStartupTests` are unedited and green.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 5 — Polling every second stays sane: the quiet-branch jitter is at most half the interval, 10 s and 60 s are byte-identical, the back-off rules are unchanged at 1 s, and `node.json` accepts `poll.activeSeconds: 1`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/PollState.cs` *(modify)*:
  - `Apply` passes `jitterUnit` (not the pre-multiplied span) to `Quiet`.
  - `Quiet` computes `interval` as today (mode, then the `x-poll-interval` raise), then `TimeSpan jitter = Min(options.Jitter, interval / 2) * jitterUnit`.
  - `Throttle` and `BackOff` keep `options.Jitter * jitterUnit` (uncapped).
  - The XML doc of `Apply` and `jitterUnit` names the cap.
- `tests/Zyggy.Core.Tests/Bus/PollStateTests.cs` *(modify, append)*; `tests/Zyggy.Core.Tests/Configuration/NodeConfigurationReaderTests.cs` *(modify, append)*.

**Seams**: none (pure state machine and a JSON reader).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/PollStateTests.cs`:
  - `Apply_OneSecondActive_JitterCappedAtHalf` (`[Theory]` AC-20: `ActiveInterval 1 s`, `Jitter 2 s`, `Unchanged` and `Changed`, unit −1 → now + 0.5 s, unit +1 → now + 1.5 s, unit 0 → now + 1 s).
  - `Apply_TenAndSixtySeconds_ByteIdenticalToBefore` (`[Theory]`: 10 s at −1/+1 → 8 s/12 s; 60 s idle → 58 s/62 s; the existing `Apply_UnchangedActive_NextDueIsTenSecondsPlusJitter` stays unedited and green).
  - `Apply_OneSecondWithPollIntervalSixty_JitterFromRaisedInterval` (AC-21: `x-poll-interval: 60` at 1 s → next due ≥ now + 58 s, ±2 s uncapped because 2 ≤ 30).
  - `Apply_OneSecondRemainingBelowFloor_ThrottledAtLeast120Seconds` (AC-21).
  - `Apply_OneSecondThrottledRetryAfterAndReset_LaterWins` (AC-21).
  - `Apply_OneSecondUnreachable_TenSecondsDoublingToFiveMinutes_UncappedJitter` (AC-21).
- Unit test file: `tests/Zyggy.Core.Tests/Configuration/NodeConfigurationReaderTests.cs`:
  - `Read_PollActiveOne_Accepted` (AC-22: `Poll.ActiveSeconds == 1`; `ToPollOptions().ActiveInterval == 1 s`).
  - `Read_PollActiveNegative_InvalidField` (`-1` → `poll.activeSeconds`; `Read_PollZero_InvalidField` exists and stays).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~PollStateTests|FullyQualifiedName~NodeConfigurationReaderTests"` (RED: the 1 s theory fails today with now − 1 s / now + 3 s; the reader tests pass already, which is the AC-22 proof, as the spec says).

**GREEN**:
- The `PollState` change only. No change to `PollOptions` or the reader.

**Contract impact**: ⚠️ **§5 item 3 (O2 / A13-7)**: "jitter ±2 s, at most half the interval". `PollState` stays a pure state machine (no clock, no I/O).

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- Every pre-existing `PollStateTests` method is unedited and green.
- `git diff src/Zyggy.Core/Bus/PollOptions.cs src/Zyggy.Core/Configuration/NodeConfigurationReader.cs` is empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — The production host as `central` at `activeSeconds: 1` picks a pushed context up within seconds; without the pickup configuration it refuses to start with exit 3 and one stderr line; a laptop host never builds a pickup

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Node/NodeHost.cs` *(modify, A6)*:
  - `Build` and `RunAsync` gain the optional `environment`. `ResolvePath` reads it (else `ProcessEnvironment()`).
  - After the `signWith` check, `if (configuration.Machine == MachineName.Central)`: `PickupConfigurationResult pickup = PickupConfiguration.Load(environment, configuration); if (pickup.Error is { } detail) throw new NodeStartupException(NodeExitCodes.Configuration, "pickup: " + detail);`, then `builder.Services.AddBusPickup(pickup);` after `AddNodeServices`.
  - On any other machine nothing of the pickup is constructed or read (AC-15).
- `src/Zyggy.Node/NodeExitCodes.cs` *(modify, doc only)*: `Configuration` names the pickup refusal on Central.
- `tests/Zyggy.Integration/Node/NodeHostHarness.cs` *(modify, additive)*: `Environment` property and `WriteNodeJson(…, MachineName? machine = null)` (A6); `Build`/`RunAsync`/`RunUntilAsync` pass `Environment`.
- `tests/Zyggy.Integration/Node/NodeHostPickupTests.cs` *(create)*; `tests/Zyggy.Integration/Node/ZyggyNodeProcessTests.cs` *(modify, append one test)*.

**Seams**: the real edge.
- The production host (`NodeHost.Build`/`RunAsync`, production DI) runs with `node.json` `machine: central` over `Bus.CloneDir`, through the real `git` behind `HookedProcessRunner`.
- The doorbell is `BareRepoHeadHandler` (`NodeHostHarness.Configure`).
- `PickupMemory` from Step 3 provides the environment. The fake `claude` exists but no job is seeded, so it never starts.
- For the 1 s proof the harness's 200 ms jitter override is replaced by the production `ToPollOptions()` (`more: s => s.AddSingleton(sp => sp.GetRequiredService<NodeConfiguration>().Poll.ToPollOptions())`).
- `ZyggyNode` (the built `zyggy-node` executable) is used for the stderr line.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Node/NodeHostPickupTests.cs`:
  - `Central_ContextPushedWhileRunning_InboxFileAndArchiveOnMainWithinTenSeconds_ProductionJitterAtOneSecond` (AC-1 through the host, O2). The host is started; once `/health` `lastPollUtc` is set, a context is seeded from the sender. `WaitUntilAsync` waits ≤ 10 s for the inbox file **and** `ExistsOnMainAsync(archive path)`. The log has exactly one `picked up 1 file(s)` line. `SecretSweep.AssertAbsent(LogLines)`. The memory tree outside `inbox/` is unchanged.
  - `Central_ContextAlreadyWaitingAtStart_PickedUpAfterTheStartupPull` (AC-14 start-up, A7).
  - `Central_ReportFromHomeLaptopToCentral_PickedUpAndArchived` (AC-3 through the host).
  - `Central_PickupEnvironmentMissingOrWrong_BuildThrowsConfigurationPickupDetail` (`[Theory]`, AC-16): `ZYGGY_MEMORY_ROOT` unset → `pickup: ZYGGY_MEMORY_ROOT is not set`; principal directory missing; `ZYGGY_TENANT=other-org` → the A8 sentence; `ZYGGY_TIMEZONE=Mars/Olympus`; `ZYGGY_SECRET_PATTERNS` at a missing file; a malformed patterns file. `NodeHost.Build` throws `NodeStartupException` with `ExitCode == 3` and the `Message` above. `RunAsync` returns 3, with no log directory line and nothing bound.
  - `Laptop_PickupVariablesSetOrNot_NoPickupBuilt_InboxNeverTouched` (AC-15). The `home-laptop` host runs with **no** pickup variables, and then with all five set to a valid tree: `host.Services.GetService<BusPickup>()` is null in both runs; a context `to: central` sitting in `home-laptop`'s own `context/` stays put after two ticks; the memory tree fingerprint is unchanged.
  - `ExistingHostTests_Unchanged`: `NodeHostStartupTests`, `NodeHostJobFlowTests`, `HealthEndpointTests` and the P1 part 1/1b run unedited (VERIFY).
- Integration test file: `tests/Zyggy.Integration/Node/ZyggyNodeProcessTests.cs`:
  - `ZyggyNode_CentralWithoutPickupConfiguration_ExitThreeOneStderrLine` (AC-16 through the real binary: `node.json` `machine: central`, no `ZYGGY_MEMORY_ROOT` in the child environment → exit 3, stderr exactly `zyggy-node: configuration error: pickup: ZYGGY_MEMORY_ROOT is not set\n`).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~NodeHostPickupTests|FullyQualifiedName~ZyggyNodeProcessTests"`

**GREEN**:
- The `NodeHost` change and the harness additions.

**Contract impact**: ⚠️ **host start-up contract (A13-8)**: Central's node refuses to start without the drop-in's variables (exit 3, `RestartPreventExitStatus=3 6` already in the unit). Laptops are unchanged. `/health` is unchanged (pickup fields deferred).

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- The new tests pass. `NodeHostStartupTests`, `NodeHostJobFlowTests`, `HealthEndpointTests`, `ZyggyNodeProcessTests` (existing) and `Gates.P1_WalkingSkeleton` pass unedited.
- `Src_Node_NoProcessHttpClientGitHubClaudeOrSecretStoreReference`, `Src_Node_NoBusPathLiteral` and `Src_Node_NoClockOrStaticStateOutsideNodeHost` are green.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B *(covers Steps 4–6)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusListenerPickupTests|FullyQualifiedName~PickupConfigurationTests|FullyQualifiedName~PollStateTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~NodeHostPickupTests|FullyQualifiedName~ZyggyNodeProcessTests"` are green. The executor pastes:
  - the host log of the 1 s test (poll, pull, `picked up` lines with timestamps, showing the push-to-pickup delay in the test);
  - the exit-3 stderr line of the binary;
  - the AC-20 table (unit → next due) for 1 s, 10 s and 60 s.
- [x] Contract review:
  - §5 item 2 as amended: the pickup runs after the job pass of the same pull, never on `304`, once per pull.
  - §5 item 3: the jitter cap, with 10 s and 60 s byte-identical.
  - §10: the pickup is Central-only by `machine`, with no new `node.json` key.
  - AC-16: the messages are the dream's.
- [x] ⚠️ Risk review (§5 shared contract): back-off and throttle at 1 s unchanged (AC-21 tests); one request in flight (`RunAsync_OneInFlightRequest` unedited).
- [x] Assumptions A6–A8 confirmed or corrected; nothing committed.
- [x] User approved — implementation may continue past this gate

---

## Step 7 — Reports that left `reports/` are still found: `FindReportsAsync`, `FindUnverifiedReportsAsync` and the inventory's recent reports also read every `archive/YYYY-MM/` month

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/ArchiveMonths.cs` *(create, A5)*.
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify)*: `FindReportsAsync` and `FindUnverifiedReportsAsync` enumerate `BusPaths.Reports(tenant, reporter)` **and** every `ArchiveMonths.Folders(root, tenant)`. For archive files the existing filters apply, plus `Header.From == reporter` (an archive month mixes machines and types). The order is the file name (ULID), ordinal, across both sources.
- `src/Zyggy.Core/Bus/BusInventory.cs` *(modify)*:
  - `ListAsync`'s report candidates add the archive months' files whose `EnvelopeParser.PeekHeader(bytes)?.Type == EnvelopeType.Report`.
  - `RecentReports` is the newest `recent` across both sources, and `ReportsTotal` counts both (A9).
  - `ReportsAnswering` uses `ArchiveMonths.Folders` (A5).
- `src/Zyggy.Core/Bus/InventoryReport.cs` *(modify)*: `public int ReportsTotal { get; init; }` on `InventoryReport` (A9).
- `src/Zyggy.Cli/Commands/StatusCommand.cs` *(modify)*: `recent reports ({k} of {report.ReportsTotal})`.
- `tests/Zyggy.Core.Tests/Bus/BusRepositoryIntakeTests.cs` *(modify, append)*; `tests/Zyggy.Core.Tests/Bus/BusInventoryTests.cs` *(modify, append)*.

**Seams**: `TempCheckout` with files dropped into `archive/2026-10/`; `EnvelopeSigner` over `InMemorySecretStore`; no process is called (reads only).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusRepositoryIntakeTests.cs`:
  - `FindReportsAsync_ReportArchived_Returned`.
  - `FindReportsAsync_ReportInBothLiveAndArchive_BothOrderedById`.
  - `FindReportsAsync_ArchivedReportFromAnotherReporter_NotReturned`.
  - `FindReportsAsync_ArchivedContext_NotReturned`.
  - `FindUnverifiedReportsAsync_TamperedArchivedReport_ReturnedRejection`.
  - `FindReportsAsync_ArchiveRootAbsentOrNonMonthFolder_Ignored` (`archive/notes/` ignored).
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusInventoryTests.cs`:
  - `ListAsync_ArchivedReports_InRecentAndInTotal_LiveCountsUnchanged` (AC-25: one live report, two archived reports, one archived context → `RecentReports` 3 newest-first, `ReportsTotal == 3`, `Machines[*].Reports` = live counts only).
  - `TraceAsync_ArchivedReport_StillReported` (exists for 08: confirm unedited and green, VERIFY).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryIntakeTests|FullyQualifiedName~BusInventoryTests"`

**GREEN**:
- The changes above.

**Contract impact**: the `zyggy status` output's recent-reports line now counts the archive (spec CLI surface; Cli README updated in Step 12). Public API: `InventoryReport.ReportsTotal`.

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `ProbeSenderTests` (if present), `BusInventoryTests` and `StatusCommandTests` pass. `Gates.P1_WalkingSkeleton.Part1b_*` (whose `recent reports (1 of 1)` must still hold with no archive) passes unedited.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — After a real pickup on the bare repository, `zyggy status`, `zyggy status --id <job>` and the probe's report search on Central still show the report the pickup archived

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Cli/StatusAfterPickupTests.cs` *(create)*.

**Seams**: the real edge.
- The bare repository and real `git`.
- The pickup resolved as in Step 3 over central's `CloneDir`.
- `zyggy status` / `status --id` in process (`ZyggyCliInProcess.RunAsync` with central's `node.json` over `CloneDir` and the doorbell/hook services of P1's `CliServices`).
- `BusRepository.FindReportsAsync` resolved from `TestHost.Build(_bus, Central, …)`.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Cli/StatusAfterPickupTests.cs` (arrange: a job from `central` to `home-laptop` and its `done` report seeded with `SeedSignedReportAsync`, then one pickup pass archives the report):
  - `StatusId_AfterPickup_ReportedByHomeLaptopWithBody` (AC-25: `state   reported by home-laptop`, then `body:` and the body).
  - `Status_AfterPickup_RecentReportsListsTheArchivedReport_LiveReportsColumnZero` (`recent reports (1 of 1)` and its line; the `home-laptop` row shows `0` under `reports`).
  - `FindReportsAsync_AfterPickup_ProbeStillFindsItsAnswer` (AC-25 probe half, through the API `ProbeSender` calls).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~StatusAfterPickupTests"`

**GREEN**:
- None expected beyond Step 7. A defect is fixed in Core with a unit test in Step 7's classes first.

**Contract impact**: none beyond Step 7.

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `StatusAfterPickupTests` pass. `StatusCommandTests`, `ProbeRoundTripTests` and `VerifyCommandTests` pass unedited.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C *(covers Steps 7–8)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryIntakeTests|FullyQualifiedName~BusInventoryTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~StatusAfterPickupTests"` are green. The executor pastes the `zyggy status` and `zyggy status --id` output after a pickup.
- [ ] Contract review: the per-machine table is live counts (it drops to 0 on Central after pickup, documented in Step 12); recent reports and the trace read the archive; `ArchiveMonths` is the only archive-month enumeration (A5); no new `BusPaths` member.
- [ ] Assumptions A5 and A9 confirmed or corrected; nothing committed.
- [ ] User approved — implementation may continue past this gate

---

## Step 9 — `get_context` returns the dream-unconsumed lines of `inbox/bus-*.md` and `inbox/remember-*.md` in its own block of at most 20 % of the budget, newest file first; every other inbox file and the `SessionStart` digest stay inbox-free

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/ContextOptions.cs` *(modify)*: `public int InboxBudgetPercent { get; init; } = 20;` and `public IReadOnlyList<string> InboxFilePrefixes { get; init; } = ["bus-", "remember-"];`.
- `src/Zyggy.Core/Memory/MemoryStore.cs` *(modify)*:
  - `internal sealed record InboxLine(string FileName, DateOnly? FileDate, string Text);`
  - `public DreamLedger? Ledger()` (A11).
  - `public IReadOnlyList<InboxLine> PickedUpLines(DreamLedger? ledger, ContextOptions options)` over `paths.InboxDirectory`: files whose names start with one of the prefixes and end `.md`, not starting `_`, each confirmed by `paths.TryResolve` with `Area == MemoryArea.Inbox`, read with `ReadText`. `MemorySnapshot.BodyLines` gives the lines; a line is skipped when `ledger?.IsConsumed("inbox/" + name, LineHash.Of(text)) == true`. The ordering is A10.
  - The class doc is updated (the "never `inbox/`" sentence becomes the Q2 rule).
- `src/Zyggy.Core/Memory/ContextRanker.cs` *(modify)*:
  - `Rank(..., IReadOnlyList<InboxLine>? inbox = null)`.
  - The inbox matches by terms as daily lines do.
  - `LayOutInbox(lines, budget * options.InboxBudgetPercent / 100)` uses the heading `## inbox (picked up, not yet filed by the dream)` and the marker `[context truncated: {n} more inbox lines not included]` (the `LayOutDaily` shape).
  - The layout is `head + filesBlock + inboxBlock + dailyBlock + Foot`, with the files' remaining budget reduced by both blocks.
  - `ContextResult` gains `InboxLinesIncluded = 0, InboxLinesOmitted = 0`.
- `src/Zyggy.Core/Hub/GetContextTool.cs` *(modify)*: passes `store.PickedUpLines(store.Ledger(), options)`. The tool description and schema are unchanged, so the golden is unchanged.
- `src/Zyggy.Hub/README.md` *(modify)*: the `get_context` row names the inbox block.
- `tests/Zyggy.Core.Tests/Memory/MemoryStoreTests.cs`, `ContextRankerTests.cs`, `tests/Zyggy.Core.Tests/Hub/HubServerGetContextTests.cs` *(modify, append)*; `tests/Zyggy.Core.Tests/Memory/DigestBuilderPickupTests.cs` *(create)*.

**Seams**: `MemoryTree` (a temp memory tree), `FakeTimeProvider` (`HubHarness`), the in-process Hub over pipes (`HubHarness`). No process, no bus.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/MemoryStoreTests.cs` (AC-27):
  - `PickedUpLines_BusAndRememberFiles_Only_BackfillAndOthersNever` (`inbox/bus-context-…`, `bus-report-…`, `remember-2026-10-29.md`, `files-2026-10-29.md`, `m365-mail-backfill-…`, `github-inventory-…` → lines from the first three only).
  - `PickedUpLines_LedgerConsumedLines_Skipped`.
  - `PickedUpLines_LedgerNull_NothingConsumed`.
  - `PickedUpLines_NewestFileFirst_FileOrderKept`.
  - `PickedUpLines_FrontMatterAndNonDashLines_NotCandidates` (A10).
  - `PickedUpLines_SymlinkOutOfPrincipal_Skipped` (Linux only, the `MemoryPathsSymlinkTests` precedent).
  - `Ledger_InvalidOrUnsupportedJson_Null_MissingFile_Empty` (A11).
- Unit test file: `tests/Zyggy.Core.Tests/Memory/ContextRankerTests.cs`:
  - `Rank_InboxLinesMatchingTopic_ThirdBlockBetweenFilesAndDaily_HeadingVerbatim`.
  - `Rank_InboxBlockNeverOverTwentyPercent_CutAtLineBoundaryWithInboxMarker` (AC-27).
  - `Rank_OnlyInboxMatches_WrapperAndInboxBlockNotNoMatch`.
  - `Rank_InboxNull_ByteIdenticalToBefore` (the existing tests unedited).
- Unit test file: `tests/Zyggy.Core.Tests/Hub/HubServerGetContextTests.cs`:
  - `GetContext_PickedUpBusLine_ReturnedVerbatimUnderInboxHeadingInsideDataFraming` (AC-26 in process).
  - `GetContext_ConsumedBusLine_NotReturned`.
  - `GetContext_InvalidLedger_LineReturned_NoError` (AC-27).
  - `GetContext_LeavesTreeByteIdentical` (exists; still green with inbox files present).
- Unit test file: `tests/Zyggy.Core.Tests/Memory/DigestBuilderPickupTests.cs` (AC-28):
  - `Digest_IdentityIndexDaily_NoInboxLineEvenWithBusFiles`: the golden `sided` tree copied plus two `bus-*` files; none of their lines appear in any section. The `DigestBuilderGoldenTests` outputs stay byte-identical.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MemoryStoreTests|FullyQualifiedName~ContextRankerTests|FullyQualifiedName~HubServerGetContextTests|FullyQualifiedName~DigestBuilderPickupTests"`

**GREEN**:
- The changes above. `DigestBuilder` is untouched.

**Contract impact**: ⚠️ **§7 Hub `get_context` (Q2 / A13-9, already in the founding spec)**: the result text gains a block; input, framing and description are unchanged; `tests/golden/hub/tool-get_context.json` is unchanged.

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- The four classes pass. `ContextRankerTreeTests`, `DigestBuilderGoldenTests`, `DigestBuilderSizeTests` and `HubToolDescriptorsTests` are unedited and green.
- `git diff tests/golden/ src/Zyggy.Core/Memory/DigestBuilder.cs` is empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — The Hub in process returns, verbatim and under the inbox heading, the line a real pickup copied from the bare repository a moment earlier

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/InProcessHub.cs` *(create)*: `HubServer.RunAsync` over two `Pipe`s with an SDK `McpClient`, as `HubHarness` does in Core.Tests (Integration sees Core's internals). The constructor takes `MemoryPaths` and `TimeZoneInfo`; it exposes `CallGetContextAsync(string topic, int? maxTokens = null)` and `DisposeAsync` (asserts the server returned 0).
- `tests/Zyggy.Integration/Hub/GetContextAfterPickupTests.cs` *(create)*.

**Seams**: the real edge: bare repository → real pickup (Step 3's composition) → the scratch memory tree → the Hub in process with the real file system. No git in the memory tree.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Hub/GetContextAfterPickupTests.cs`:
  - `GetContext_AfterPickupOfContextFromHomeLaptop_LineVerbatimUnderInboxHeading` (AC-26: topic `staging database` → the text contains `## inbox (picked up, not yet filed by the dream)\n- [stated] <date> (project:calizr): The staging database is restored from Sunday's snapshot\n` inside `<zyggy-context tenant="acme" user="alice" …>`).
  - `GetContext_AfterPickupOfAReport_ItsOneObservedLineReturned` (a `done` report whose first line is `Root cause is the expired staging certificate`; topic `staging certificate` → `- [observed] <date> [report <job8>]: Root cause is the expired staging certificate` under the inbox heading, and no other line of that report).
  - `GetContext_BeforeAnyPickup_NoInboxBlock` (control).
  - `Digest_AfterPickup_NoInboxLine` (AC-28 through `zyggy memory digest daily` in process with `PickupMemory.Env()`).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~GetContextAfterPickupTests"`

**GREEN**:
- The helper. No production change expected.

**Contract impact**: none beyond Step 9.

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `HubMcpServerTests` and `MemoryDigestCommandTests` pass unedited.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 11 — The nightly dream files and consumes both kinds of picked-up line, after which `get_context` no longer shows them in the inbox block

With the fake `claude` filing the batch, the dream classes a picked-up `bus-context-…` line as a stated inbox line and a picked-up `bus-report-…` line as an observed inbox line, files both and consumes both in the ledger. The dream reads and writes nothing on the bus.

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Core.Tests/Dream/BatchPlannerTests.cs` *(modify, append)*.
- `tests/Zyggy.Integration/Fixtures/dream-bus/acme/alice/**` *(create)*: the `dream` fixture's identity files, `auto/MEMORY.md` and the category `_index.md` files (no inbox files of its own), plus two files in the Step 2 format:
  - `inbox/bus-context-home-laptop-01K8ZX3P4Q5R6S7T8V9W0XYZAB-{d-1}.md`, holding the AC-1 line dated `{d-1}`;
  - `inbox/bus-report-home-laptop-01K8ZX2A3B4C5D6E7F8G9H0JKM-01K8ZX4Q5R6S7T8V9W0XYZABC-{d-1}.md`, holding the one line `- [observed] {d-1} [report 01K8ZX2A]: Root cause is the expired staging certificate`.
- `tools/fake-claude/scenarios/dream-file-bus.jsonl` *(create)*: the proposal shape of `dream-file-ok.jsonl`. It files `L1` (the stated context line; `BatchPlanner` offers stated inbox lines first) and `L2` (the observed report line) into a new file `business/areas/calizr.md` (`name: calizr`, `description: The calizr project`). It is materialised with `{today}`/`{d-1}` via `MemoryRepoFixture.MaterialiseScenario` if dates appear.
- `tools/fake-claude/README.md` *(modify)*: one scenario-table row.
- `tests/Zyggy.Integration/Dream/DreamBusInboxTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `Src_Dream_NeverReferencesTheBus` (AC-30: no file under `src/Zyggy.Core/Dream/` or `src/Zyggy.Cli/Commands/DreamCommand.cs` names `BusRepository`, `BusPaths`, `IBusProvider`, `BusPickup` or `GitHubBusOptions`).

**Seams**: unit — a `MemorySnapshot` built in memory; integration — `MemoryRepoFixture` (bare memory remote + clone), the real `zyggy dream` out of process, the fake `claude` (`dream-file-bus`), then `InProcessHub` over the clone.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Dream/BatchPlannerTests.cs`:
  - `Plan_BusContextInboxFile_DateFromNameStatedClass` (AC-30: `inbox/bus-context-home-laptop-<id>-2026-10-29.md` → `FileDate == 2026-10-29`, class `StatedInbox`).
  - `Plan_BusReportOneObservedLine_DateFromNameObservedClass` (AC-3 hand-over: `inbox/bus-report-home-laptop-<job>-<id>-2026-10-29.md` holding the one `- [observed] … [report <job8>]: …` line → one candidate, class `ObservedInbox`, `FileDate == 2026-10-29`).
  - `Plan_BusContextAndBusReport_StatedFirstThenObserved` (`L1` = the context line, `L2` = the report line).
- Integration test file: `tests/Zyggy.Integration/Dream/DreamBusInboxTests.cs`:
  - `Dream_BusContextAndReportLines_FiledAndConsumed_InboxFilesNeverCommitted`:
    - exit 0;
    - `business/areas/calizr.md` on `main` contains both lines with their `[stated]` and `[observed]` tags (the report line keeps its `[report <job8>]` provenance);
    - `.dream/ledger.json` lists both inbox files, each with one consumed hash;
    - both inbox files are unchanged on disk and absent from the commit.
  - `GetContext_AfterDream_LinesNoLongerInInboxBlock` (`InProcessHub` over the clone's `acme/alice`; topics `staging database` and `staging certificate` → no `## inbox (` heading).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BatchPlannerTests|FullyQualifiedName~SourceHygieneTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~DreamBusInboxTests"`

**GREEN**:
- The fixture, the scenario, the README row and the hygiene fact. **No production change is expected** (28's planner already reads the name's date and the `- [stated]` / `- ` prefixes, and the report's one line is a `- ` line by construction). If one is needed, it is out of this plan's scope and the executor stops at the gate.

**Contract impact**: none (28 unchanged; AC-30 proves the hand-over). The new fake-claude scenario is a test fixture, not a golden capture (`done.jsonl` untouched).

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `DreamEndToEndTests`, `DreamRollupTests` and `FakeClaudeTests` pass unedited.
- `git diff src/Zyggy.Core/Dream/ tools/fake-claude/scenarios/done.jsonl tests/golden/` is empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D *(covers Steps 9–11)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MemoryStoreTests|FullyQualifiedName~ContextRankerTests|FullyQualifiedName~HubServerGetContextTests|FullyQualifiedName~DigestBuilderPickupTests|FullyQualifiedName~BatchPlannerTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~GetContextAfterPickupTests|FullyQualifiedName~DreamBusInboxTests"` are green. The executor pastes:
  - one `get_context` result with the inbox block;
  - the same result after the dream (no block);
  - the ledger entries for the `bus-context-…` and `bus-report-…` files.
- [ ] Contract review (⚠️ §7, Q2 / A13-9):
  - Only `bus-*` and `remember-*` inbox files, and only unconsumed `- ` lines.
  - ≤ 20 % of the budget, with its own marker; newest file first.
  - An invalid ledger means "nothing consumed".
  - The digest has no inbox line.
  - The tool description and the golden are unchanged.
- [ ] Q-P1 = (b) end to end: the report's one `[observed]` line is returned by `get_context` (Step 10), then filed and consumed by the dream (Step 11), so its `bus-report-…` inbox file becomes eligible for the rollup like any other inbox file.
- [ ] Assumptions A10–A11 confirmed or corrected; nothing committed.
- [ ] User approved — implementation may continue past this gate

---

## Step 12 — The release ships `pickup.conf.example` and the READMEs say how Central is configured and what to do when the pickup fails

The release ships `deploy/systemd/zyggy-node.service.d/pickup.conf.example`, pinned byte for byte and listed by the existing `SHA256SUMS` step. The READMEs gain:
- The Node README: the Central `node.json` example (`"poll": { "activeSeconds": 1 }`, no `workHours`), the rate-limit paragraph ("within seconds; measured value pending", no webhook or socket), the pickup in "What it does" and the five "Troubleshooting — Pickup" rows.
- `deploy/README.md`: the drop-in, as required on Central from this release.
- The Cli README: the `status` archive note.
- The Core README: the new rows.

New hygiene facts pin the rules: placeholders only, and no bus path literal outside `BusPaths`.

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `deploy/systemd/zyggy-node.service.d/pickup.conf.example` *(create, LF, A12 — `<instance-dir>` confirmed by the owner, Q-P2)*: three comment lines in the `credentials.conf.example` style:
  1. "Required on Central from deliverable 13: the memory principal and secret patterns the node's pickup needs (founding spec §7 step 1, §10)."
  2. "Copy to /etc/systemd/system/zyggy-node.service.d/pickup.conf, replace <instance-dir>, then: systemctl daemon-reload && systemctl restart zyggy-node."
  3. "Without it the node on Central exits 3 (configuration error: pickup: …); laptops ignore these variables."

  Then `[Service]` and the five `Environment=` lines of A12. No secret.
- `tests/Zyggy.Core.Tests/Deploy/Expected/pickup.conf.example` *(create)*: the expected bytes.
- `tests/Zyggy.Core.Tests/Deploy/SystemdUnitTests.cs` *(modify)*:
  - `private const string PickupDropIn = "systemd/zyggy-node.service.d/pickup.conf.example";`
  - `PickupDropIn_EqualsTheExpectedTextByteForByte`.
  - `PickupDropIn_ExactlyTheFiveZyggyVariables_PlaceholdersAcmeAlice` (the `Environment=` keys in order: `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT=acme`, `ZYGGY_USER=alice`, `ZYGGY_TIMEZONE`, `ZYGGY_SECRET_PATTERNS`; no `LoadCredential`).
  - `PickupDropIn_CommentsNameCopyTargetRestartAndExitThree`.
  - `SystemdFiles_LfOnlyPlaceholdersOnlyNoSecret` gains `[InlineData(PickupDropIn)]`, with no exemption.
- `deploy/README.md` *(modify)*: the drop-in row ("**Required on Central** from this release: …"), and a "Central `node.json`" block with `"poll": { "activeSeconds": 1 }` and the note "no `workHours`: always active".
- `src/Zyggy.Node/README.md` *(modify)*:
  - "What it does" gains step 5b **Pickup (Central only)**: one paragraph on the pass, archive and inbox, pointing to founding spec §7 step 1.
  - The `node.json` table row for `poll.activeSeconds` notes "Central: 1".
  - A `## Central` section with the `node.json` example and the drop-in pointer.
  - A `## Polling every second` paragraph: the spec's Behaviors reasoning, condensed (the conditional `304` costs nothing; a `200` costs 1 of 5,000/h; the secondary limit of 900 points/min against ~72 requests/min across three machines; the §5 back-off at 1 s; no webhook, no socket — §13). It ends with "Latency: within seconds; measured value pending (deliverable 13, AC-24)".
  - `## Exit codes` row 3 adds "on Central: `pickup: <detail>`".
  - A `## Troubleshooting — Pickup` table with exactly the five rows the spec's Failure modes give: "Pickup: configuration error", "Pickup: file left on the bus", "Pickup: archive failed / deferred", "Pickup: secret-shaped line dropped", "Pickup: slow (throttled)". Each names its log line and its remedy, with the pointers `rotate-hmac.md`, `revoke-machine.md`, `bus-conflict.md`.
  - The log `phase` list adds `pickup`.
- `src/Zyggy.Cli/README.md` *(modify)*: the `status` verb row notes "recent reports include `archive/`; on Central the `reports`/`context` columns of other machines read 0 after the pickup".
- `src/Zyggy.Core/README.md` *(modify)*: the `Bus/` row gains `BusPickup` (Central's pickup, §7 step 1), `BusRepository.ArchiveAsync` / `ListPickupCandidatesAsync` and `ArchiveMonths`; the `Memory/` row gains `PickupInbox`, `PickupConfiguration` and `MemoryStore.PickedUpLines`; "Troubleshooting — Node" points at the Node README's pickup rows.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*:
  - `Readmes_PickupDocumented`: the Node README contains `## Troubleshooting — Pickup`, the five row titles, `"activeSeconds": 1`, `pickup.conf` and `measured value pending`, and not `about a second`; `deploy/README.md` contains `pickup.conf.example` and `Required on Central`.
  - `PickupTestsAndFixtures_NonDefaultIdentityOnly` (AC-35, the `ContextTestsAndFixtures_NonDefaultIdentityOnly` shape) over every file of this deliverable in `src/`, `tests/`, `deploy/` and the fixture tree: no `geoffrey`, no `MailAddress()`, no GUID literal, no `/home/`, `/srv/` or `C:\Users\`; at least one file contains each of `acme`, `alice`, `home-laptop`, `calizr`.
  - `Src_PickupClasses_NoConsoleClockOrStaticState` (`BusPickup.cs`, `PickupResult.cs`, `PickupCandidate.cs`, `ArchiveMonths.cs`, `PickupInbox.cs`, `PickupConfiguration.cs`).

**Seams**: none (shipped files, documentation and file facts).

**RED**:
- Unit test files: `tests/Zyggy.Core.Tests/Deploy/SystemdUnitTests.cs` (the four new tests fail: the file does not exist) and `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` (`Readmes_PickupDocumented` fails; the other two are RED only if an offender exists — run them and fix the offender, never loosen a fact).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SystemdUnitTests|FullyQualifiedName~SourceHygieneTests|FullyQualifiedName~CiWorkflowTests"`

**GREEN**:
- The files and README edits above. The CI `SHA256SUMS` step already globs `deploy/**` (`CiWorkflowTests` unedited proves it).

**Contract impact**: the §12 definition-of-done rows: a runbook entry for every new failure mode, `PROTOCOL.md` unchanged (placeholder; 15 publishes Q3's wording), `node.json` schema unchanged (no new key), founding-spec amendments already in place.

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `SystemdUnitTests` (old and new) and `SourceHygieneTests` (including `Deploy_AndReadmes_NoRealIdentifierNoAbsoluteHomeNoSkipPermissions` and `Packages_RowCountPinned` = 24) are green.
- `git ls-files --eol deploy/` shows `i/lf` for the new file once the owner stages it (the executor reports `w/lf` from `git ls-files --eol -o`).

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 13 — The `zyggy-core` runbook installs Central with the pickup drop-in and the 1-second `node.json`, proven by bats

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

> **Branch:** `D:\source\zyggy-core` on `feature/13-pickup-runbook`, **created by the owner, stacked on `feature/12-node-context`** (12's template branch, unmerged). The executor checks `git -C D:\source\zyggy-core branch --show-current` first and stops if it is not this branch. Uncommitted.

**Scope** (all in `D:\source\zyggy-core`):
- `runbooks/install-node.md` *(modify)*:
  - The opening file list adds `zyggy-node.service.d/pickup.conf.example`.
  - Step 1's "the four `deploy/…` files" becomes "the five".
  - Step 3's `poll` becomes `"poll": { "activeSeconds": 1 }` (Central polls every second; deliverable 13).
  - A new step 3b `[central/<vm-admin>]` **Required from deliverable 13**: `mkdir -p /etc/systemd/system/zyggy-node.service.d`; copy `deploy/systemd/zyggy-node.service.d/pickup.conf.example` to `…/pickup.conf`, replace `<instance-dir>`; `systemctl daemon-reload`. "Without it `zyggy-node` exits 3 with `configuration error: pickup: <detail>` and the files wait on the bus."
  - Step 6's checks add `journalctl -u zyggy-node | grep 'picked up'` after a laptop's `zyggy context`.
  - `### Upgrade` adds "from deliverable 13 on, install `pickup.conf` before starting the new version".
  - Placeholders only; no `/srv/`.
- `tests/repo.bats` *(modify)*: the runbooks test (line 966) adds `pickup.conf.example`, `pickup.conf` and `"activeSeconds": 1` to the `install-node.md` facts.

**Seams**: none (documentation; bats over the files).

**RED**:
- `tests/repo.bats`: the extended runbooks test fails against the unchanged `install-node.md`.
- Failing-run command: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/repo.bats"`

**GREEN**:
- The runbook edit.

**Contract impact**: none on the wire. `PROTOCOL.md` in `zyggy-core` stays the placeholder (`git -C D:\source\zyggy-core diff --stat PROTOCOL.md` empty).

**VERIFY**:
- The full bats suite (`bats tests/`) is green in the container.
- `git -C D:\source\zyggy-core status` shows only `runbooks/install-node.md` and `tests/repo.bats`.
- `git -C D:\source\zyggy-core diff --stat PROTOCOL.md .claude/` is empty.
- In this repository `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + `dotnet format Zyggy.slnx --verify-no-changes` stay green (nothing changed here).

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 14 — The P1 walking-skeleton gate, part 2: a fact sent with `zyggy context` from `home-laptop` reaches Central's `get_context` verbatim with no dream run

`zyggy context --scope project:calizr -- "<fact>"`, run in process with `home-laptop`'s `node.json`, goes through the production host running as `central` with the pickup configuration, and then the Hub's `get_context` in process returns the line verbatim. The three digest sections together are ≤ 20,000 bytes with no inbox line, the context file sits in `archive/` on `main`, and `zyggy verify` on Central reports every file `OK`.

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` *(modify — append only)*:
  - The header comment's "Part 2 (deliverable 13)" line is updated to name what part 2 does.
  - The region `#region Part 2 — 13` is appended after Part 1b, holding `Part2_ContextFromHomeLaptop_PickedUpByCentral_GetContextReturnsIt_DigestUnderSixKTokens` and its private helpers (`HomeLaptopNodeJsonOverSender()`, `PickupTree()` copying `golden/digest/sided/` into scratch per A13).
  - `git diff` shows only additions below line 279 and the one header-comment line.
- `tests/Zyggy.Integration/Node/NodeHostHarness.cs`: no change beyond Step 6.

**Seams**: the full real chain against the local bare repository.
- The CLI runs in process (`ZyggyCliInProcess.RunAsync` with `ZYGGY_NODE_JSON` = home-laptop's over `Bus.SenderDir`, `ZYGGY_SECRET_PATTERNS` = the golden patterns, and P1's `CliServices`).
- The production host runs as `central` over `Bus.CloneDir` (`_harness.WriteNodeJson(machine: Central)`, `_harness.Environment = pickup env over the scratch tree`).
- The Hub runs in process (`InProcessHub`).
- `zyggy memory digest identity|index|daily` and `zyggy verify` run in process.
- Never GitHub, never the real `claude` (no job in this part).

**RED**:
- Gate test file: `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`, method `Part2_ContextFromHomeLaptop_PickedUpByCentral_GetContextReturnsIt_DigestUnderSixKTokens` (AC-29):
  1. Start the central host and wait for `/health` `lastPollUtc`.
  2. Run `zyggy context --scope project:calizr -- "The staging database is restored from Sunday's snapshot"`: exit 0 with the two 12 lines; capture the id.
  3. `WaitUntilAsync` (≤ 30 s) until `inbox/bus-context-home-laptop-<id>-<date>.md` exists **and** `ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Archive(acme, y, m), id))`, then stop the host (exit 0).
  4. `get_context` with topic `staging database` → the text contains the stored line verbatim under `## inbox (picked up, not yet filed by the dream)`.
  5. The three digest sections are concatenated: their UTF-8 byte count is ≤ 20,000, and none contains the fact.
  6. `ExistsOnMainAsync(BusPaths.EnvelopeFile(BusPaths.Context(acme, home-laptop), id))` is false.
  7. `zyggy verify` with central's `node.json` → exit 0 and the summary line `verified <n> ok, 0 failed, …`.
  8. `SecretSweep.AssertAbsent` over every CLI output, the host log and `_harness.Hooks.Calls`. No hooked call has a working directory under the scratch memory root.
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_WalkingSkeleton"` (RED: the new method fails until Steps 1–11 are in; parts 1/1b stay green).

**GREEN**:
- The appended region only. A failure points at a defect fixed in its slice's code with a unit test there first.

**Contract impact**: the §12 P1 gate is closed (both parts green with tenant `acme`).

**VERIFY**:
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green.
- `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_WalkingSkeleton"` is green: parts 1, 1b and 2.
- `git diff tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` shows additions only (plus the one header-comment line).
- `git diff tests/golden/ Directory.Packages.props` is empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice E, definition of done *(covers Steps 12–14; closes the deliverable and P1)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification (this repository): `dotnet test Zyggy.slnx` is green on Windows, and CI is green on both runners once the owner pushes the branch. The executor pastes:
  - the Part 2 test's `get_context` text and the digest byte count;
  - the `zyggy verify` summary line;
  - `pickup.conf.example` in full;
  - the Node README's "Troubleshooting — Pickup" table and the "Polling every second" paragraph.
  
  `Packages_RowCountPinned` is 24, and `git diff tests/golden/` is empty.
- [ ] Behavioral verification (template): the `zyggy-core` bats suite is green in the container. The executor pastes the `install-node.md` diff and `git -C D:\source\zyggy-core status`. The branch is `feature/13-pickup-runbook`, stacked on `feature/12-node-context`.
- [ ] Contract review against the roadmap's definition of done and the spec:
  - `Gates/P1_*` is green in both parts with tenant `acme` (AC-29).
  - `PROTOCOL.md` is the placeholder in both repositories (AC-34).
  - The founding-spec amendments A13-1..A13-9 are in place (pasted before this plan); `§3`/`§6` say the dream stays a direct timer and no dream job is submitted (AC-32).
  - The Node README has the five pickup rows (AC-34).
  - The 28 runbook row "failed or aborted dream run" exists in the instance's private runbook, section 14 "Dream pass": the owner confirms it; checked, not rewritten.
  - The drop-in is shipped and pinned (AC-33).
  - Placeholders only everywhere (AC-35).
- [ ] ⚠️ Risk review:
  - §4 write rule, as reviewed at Gate A.
  - §5 jitter, as at Gate B.
  - §7 `get_context`, as at Gate D.
  - Secrets: `SecretSweep` is clean across the gate run.
  - Public hygiene: both repositories are public; every path is a placeholder; the drop-in uses `<instance-dir>` (Q-P2, resolved).
- [ ] **Owner-executed, AC-24 (latency measurement — not a test)**: release installed on Central (with `pickup.conf` and `"activeSeconds": 1`) and on the home laptop. The owner sends 10 facts with `zyggy context` from the home laptop, minutes apart. For each, latency = Central's `picked up` log timestamp − the time `zyggy context` printed its `context <id>` line. Median ____ s, maximum ____ s, recorded here and in the private evidence page. The executor then (in a follow-up, not before) replaces the Node README's "measured value pending" with the measured median.
- [ ] **Owner-executed, AC-31 (one real nightly dream reviewed)**: after a real fact sent and a real job run from the home laptop, the next real nightly `dream YYYY-MM-DD` commit's diff in the memory repository shows the fact filed in its category with its tag. The bus shows both files under `archive/`. Reviewed by the owner and recorded here.
- [ ] Nothing committed by the agent in either repository. The owner commits, pushes, merges and tags. The roadmap entry 13 is updated by the project-manager (status, version, the Goal re-worded to the spec's User Story per decision Q1/O1). Forward items are recorded for 17 (the `created`-month rule; never archive a verifiable `to: central` file before the pickup ran) and for 21 (`job_status` reads `archive/`).
- [ ] User approved — the deliverable is done and P1 is closed
