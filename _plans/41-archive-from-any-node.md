# Plan: 41 — Archive from any node — "As the owner in a hand-run Claude Code session on any laptop (the home laptop or the employer's work laptop), I want to hand over a file, say 'archive this for project X', see project, name, description, type, size and the route, give my go, and have it reach Central's project archive — through my OneDrive archive folder when this laptop can reach it, through the bus otherwise — so that the item sits in Central's memory repository under `archive/<project>/` with its sidecar and a `[stated]` index line, committed and pushed, indistinguishable from an item archived in a Central session"

> **Scheduling note.** Branch `feature/41-archive-from-any-node` from `main` in `zyggy`; the sibling template repository `zyggy-core` (`D:\source\zyggy-core`) gets its own branch `feature/41-node-archive`, **created by the owner**, for Step 13. Both repositories are left **uncommitted** at every 🛑 HUMAN GATE for the owner's review (auto-memory "Owner reviews before commit"); the agent never commits, pushes or tags. The founding-spec wording **W41-1..W41-16** and the `_specs/37-project-archive-memory.md` wording **W41-19** are **not** plan steps: the orchestrating session pastes them verbatim once the owner has approved the changed lines, listing every edit in its journal (AC-47; CLAUDE.md "Founding spec"). Until then the spec's W41 text is the contract this plan builds against. W41-17/W41-18 (`zyggy-core` `security.md`) are Step 13.

## Overview

After this plan any laptop can archive. `zyggy archive --project <slug> --name <text> --description <text> --file <absolute path> [--slug <slug>] [--via bus]` runs, on the laptop, every archive check the laptop can run (source, bytes read once, type and Office content, the laptop cap `archive.itemMaxBytes`, secrets and contact details, the composed index line), then the checkout and key checks and a pull, and only then picks the route: with `node.json` `archive.oneDriveFolder` set it copies the bytes into `<oneDriveFolder>/<project>/<slug>.<ext>` (the laptop's own OneDrive sync client uploads them) and publishes a small signed `item` envelope `via: onedrive`; without that key (the recorded "no OneDrive access", typically the work laptop) or with `--via bus` it publishes a signed `item` envelope `via: bus` carrying the file as 76-column base64. The envelope sits in the sender's own `tenants/<tenant>/nodes/<sender>/items/<ULID>.md`, addressed `to: central`, through the one §5 write sequence. On Central the node's pickup verifies the item, obtains the bytes (decoded from the body, or downloaded read-only from `<offload.root_folder>/<project>/<drive_name>` through the existing Graph reader, at most one lookup a minute per item, `Retry-After` honoured, refused `drive_missing` after `item_drive_wait_hours`), checks size and SHA-256 against the signed values, files them through the **one** archive writer (`ArchiveService.AddBytesAsync`: the same closed checks, sidecar, `[stated]` index line, one memory commit pushed), and in the pass's one bus commit removes the `items/` file and writes Central's signed report (`done`, or `failed` with `reason: archive_refused`). `zyggy status --id` shows the item queued, then reported; `zyggy verify` judges `items/` files (new drift `wrong_recipient`). The offload later **adopts** the OneDrive copy (no upload). The `zyggy-core` template gains `node/skills/archive/SKILL.md` (thin, owner-go step), its README rows and the replaced `security.md` clauses. `tests/Zyggy.Integration/Gates/P1_ArchiveFromNode.cs` runs the whole round trip with tenant `acme`.

Implements `_specs/41-archive-from-any-node.md` (owner approved 2026-10-31; zero Open Questions; owner decisions 1–4 and Q1 = (a) are binding: AC-1..AC-50, the Decision Table's Keep/Reshape/Library/Defer rows, the Contracts). Founding-spec sections: §1, §2, §3 (Node row, Cli verbs, Skills `archive`), §4 (layout, write rules, envelope, mandatory fields, signature, canonical form, state machine), §5 (commit subjects), §6 (DLP not applied), §7 (layout `archive/<project>/`, step 1, rules), §8 (work boundary, Secrets Graph-certificate row, injection, audit), §9 (Cli verbs, design rules), §10 (`node.json`, Central drop-ins), §13, §14.

**Reference pattern** (read across all layers; mirrored here): **deliverable 12's sender** — `src/Zyggy.Core/Bus/ContextSubmitter.cs` (`BuildAsync`: refuse before signing, `EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, MachineName.Central, now, Normal)`, sign, `Serializer`, `ParseAndVerifyAsync`, `Differs` over every §4 field, the body and the key set; `SubmitAsync`: `SigningFailedException` → `SubmitFailed("sign")`, `SecretStoreUnavailableException` → `SubmitFailed("secret-store")`), `BusRepository.PublishContextAsync` + `WriteAsync` + `AssertMayWrite`, `SubmitResult.cs`, the DI factory in `Bus/ServiceCollectionExtensions.AddGitHubBus`, `src/Zyggy.Cli/Commands/ContextCommand.cs` (`ZYGGY_HOOKS` → `BusHost.LoadConfiguration` → `CheckSender` → `signWith missing` → `AddSecretPatterns` → `BuildServices` → `CheckCheckoutAsync` → Core entry → `RenderAsync` exit switch), `CliHarness`/`ContextCommandTests` (bare repo, `SecretSweep.AssertAbsent`, `Hooks.Override`, `Bus.DeleteBare()`); **deliverable 13's pickup** — `BusPickup` (`RunCoreAsync`, `WriterRule`, `Leave` once per path, `NoteArchiveFailed`, never throws but on cancellation), `BusRepository.ListPickupCandidatesAsync` / `ArchiveAsync` / `AssertMayArchive`, `PickupConfiguration`, `AddBusPickup`, `NodeHost.Build` (Central-only pickup), `tests/Zyggy.Integration/Bus/BusPickupTests.cs`, `Node/NodeHostPickupTests.cs`, `Infrastructure/PickupMemory.cs`; **deliverables 37/38/39's archive** — `ArchiveService.AddAsync`, `ArchiveChecks.Check`, `ArchiveArguments.ParseAdd`, `ArchiveOptions`, `ArchiveConfiguration`, `SourceDenyList`, `MemoryPublisher`, `ArchiveIndexLine`, `ArchiveMediaTypeWire` (the closed extension list), `ArchiveVerb` (rendering of refusals), `OffloadService` (adoption, `Classified.FromRead`), `OffloadVerb` + `M365Session.CreateGraph` (how a Graph reader is built from the instance), `GraphReader` / `GraphHttp` / `GraphEndpoints`, `tests/Zyggy.Core.Tests/M365/GraphFixture.cs`, `Infrastructure/StubGraphHandler.cs` + `StubOneDrive.cs` (the offload test stub), `tests/Zyggy.Integration/Infrastructure/OffloadHarness.cs` + `MemoryRepoFixture.cs`; **deliverable 03's goldens** — `tests/golden/README.md` ("How the oracle files are produced — never by the code under test"), `EnvelopeParserGoldenTests`, `EnvelopeWriterTests`; **the template** — `zyggy-core` `node/skills/remember/SKILL.md`, `node/README.md`, `.claude/skills/archive/SKILL.md`, `.claude/rules/security.md`, `tests/node.bats`, `tests/fixtures/zyggy-stub.sh`, `tests/repo.bats`; sibling-repo step shape: `_plans/12-context-envelopes-from-nodes.md` Step 9.

**Phase**: 41 serves P1 but does not close a §12 phase (13 closed P1). Planner rule 15 is applied as the owner asked: the last slice adds the sibling gate scenario `tests/Zyggy.Integration/Gates/P1_ArchiveFromNode.cs` (tenant `acme`), the runbook rows and the owner-run check; `Gates/P1_WalkingSkeleton.cs` is never edited (`git diff tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` empty at every gate).

**What this plan deliberately is not** (spec Out of Scope / Defer): no `media_type` envelope field, no `--wait`, no `--via onedrive`, no `zyggy status` overview column, no `node.json` `archive.sourceDeny`, no persisted retry state on Central, no bus-wide budget, no PDF/Office content scan, no Office parsing on laptops, no list/remove from a laptop; **no Graph call, credential or consent on any laptop**; **no OneDrive write or delete by Central's pickup** (the drive writer is never registered in the node); no stale-`items/` sweep (17); no verbatim `PROTOCOL.md` (stays the placeholder pointing to §4); no laptop install wiring (15); no DLP change; no remote Hub; **no new NuGet package** (`Directory.Packages.props` unchanged — `SourceHygieneTests.Packages_RowCountPinned` is the guard, AC-49).

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — The `item` envelope on the wire | 1, 2 | an `item` (`via: bus` with a base64 body, or `via: onedrive` pointer) is created, signed, serialised, re-parsed and verified; every malformed field or body is refused; three new goldens pin the canonical form (1); it lives in `nodes/<sender>/items/`, `zyggy verify` says `OK`, `writer_mismatch`, `type_folder_mismatch` or `wrong_recipient` on a bare repo (2) | 🛑 after Step 2 (⚠️ §4 shared contract, ⚠️ signing/canonical form, ⚠️ public API) |
| B — A laptop sends a file through the bus | 3, 4, 5 | the laptop refuses everything the archive would refuse before anything leaves it (3); a passed file becomes a signed `via: bus` item published into the sender's own `items/` folder with every outcome named (4); `zyggy archive` does it from `work-laptop`'s `node.json` on a bare repo, `status --id` shows it queued (5) | 🛑 after Step 5 (⚠️ secrets, ⚠️ work boundary — decision 2, ⚠️ `node.json` contract, ⚠️ public API) |
| C — A laptop sends a file through its OneDrive folder | 6, 7 | the route is chosen from `archive.oneDriveFolder` / `--via bus`, the folder is probed, written idempotently and verified, never a silent fallback (6); against a temp folder and a bare repo the exact AC-18 output appears and a retried call publishes once (7) | 🛑 after Step 7 (⚠️ work boundary: nothing reaches the folder the bus would refuse) |
| D — Central files a bus-route item | 8, 9, 10 | given bytes are filed through the one writer byte-identically to `archive add`, an already-filed item is recognised (8); the pickup settles a bus item in one pass — filed + `done` report, refused + `archive_refused`, left, or waiting (9); against a bare bus and a memory repo with a bare remote, the item is filed, the memory commit pushed, the `items/` file removed with Central's report in one commit, `status --id` shows `reported by central` (10) | 🛑 after Step 10 (⚠️ §4 state machine + §5 commit subject, ⚠️ secrets, ⚠️ memory git from the pickup — spec 13 deviation) |
| E — Central fetches a OneDrive-route item | 11, 12 | the Graph reader downloads one file read-only through the content redirect, capped; the fetcher waits politely, honours `Retry-After`, refuses `drive_missing` / `drive_mismatch`; the pickup files a OneDrive item exactly like a bus item (11); with the stub Graph handler the item is filed, no Graph write is made, the offload later adopts the copy, three routes give byte-identical results (12) | 🛑 after Step 12 (⚠️ secrets: Graph credential in Central's node, §8 W41-12; ⚠️ new download host rule) |
| F — The laptop template | 13 | `zyggy-core` holds `node/skills/archive/SKILL.md` (thin, owner-go, exit mapping), the `node/README.md` rows, W41-17/W41-18 in `security.md`; the stub knows `archive`; bats green | 🛑 after Step 13 (⚠️ work boundary wording; public hygiene) |
| G — Definition of done | 14, 15 | READMEs, `pickup.conf.example`, runbook rows, hygiene facts (14); `Gates/P1_ArchiveFromNode.cs` runs home-laptop (OneDrive, Markdown + PNG) and work-laptop (bus, DLP on) through Central's production host, a tampered item never filed, an oversized file refused on the laptop (15) | 🛑 after Step 15 — **definition of done** (owner-run AC-50) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally; the `zyggy-core` side (Step 13) is proven by its bats suite in the local container (`MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "git config --global --add safe.directory /w; trap '' PIPE; bats tests/"`).

<!--
Decomposition strategy: vertical slices, not horizontal layers. Slice A is the wire format (pure parse/sign tests and goldens,
then the bus folder judged by `zyggy verify` against a bare repo). Slice B proves the laptop's checks and the bus-route sender
through substituted seams (IBusProvider, RecordingProcessRunner, InMemorySecretStore, FakeTimeProvider) and wires `zyggy archive`
on a bare repo. Slice C adds the OneDrive route through a substituted drop folder, then a real temp folder. Slice D proves
Central's filing through fakes (recording git for memory and bus) and wires it on a bare bus + memory repo with a bare remote.
Slice E proves the read-only Graph fetch with the stub Graph handler and a fake clock, then wires it end to end. Slices F and G
are the template and the definition of done. Never the real claude, never Graph, never GitHub in any test.
-->

---

## Shared rules for this plan

- **Identity in every test, fixture and example**: tenant `acme`, user `alice`, machines `central` / `home-laptop` / `work-laptop`, project `calizr`, key `acme/1` (`TestKeys.Acme1`), the fake clock at `2026-10-31T14:05:00Z` unless a test says otherwise, mailbox `alice@acme.example` (the stub's), OneDrive root `Zyggy/archive`, download host `acme-my.sharepoint.com`. No employer name, real id, real path, `geoffrey` or real mail address anywhere (`SourceHygieneTests`; Step 14's facts; `zyggy-core`'s hygiene sweeps). README/runbook paths are placeholders (`C:/Users/alice/OneDrive - Example/Zyggy/archive`, `<instance-dir>`, `<bus.localPath>`).
- **Every decision in Core, every rendering in Cli** (08/12's rule): `ArchiveCommand` reads options, calls `ItemRequest.TryCreate` and `ItemSubmitter.SubmitAsync`, prints. No validation text is born in `Zyggy.Cli`.
- **One writer** (decision 1): Central files only through `ArchiveService` (`AddAsync` delegates to the same content path as `AddBytesAsync`). No second sidecar writer, no "already offloaded" sidecar.
- **Paths**: every bus path through `BusPaths` (`Items(tenant, machine)`, `EnvelopeFile`, `Reports`); the laptop's OneDrive target through the one builder `ArchiveDropFolder.TargetPath(root, project, driveName)` (A6); Central's drive path through the existing `DriveArchivePath(rootFolder).Item(project, slug, ext)`; every memory path through `MemoryPaths`.
- **Secrets**: the signing key read only inside `EnvelopeSigner` through `ISecretStore`; the Graph key only inside `GraphTokenClient` through `CredentialFileSecretStore`; a refused text line, name or description appears in no stdout, stderr, log line, file, envelope or OneDrive folder — only the pattern **name** and line number. `SecretSweep.AssertAbsent` runs over every output, the OneDrive temp folder and the bare repository in the integration tests that feed secret samples.
- **Data, never instructions**: the file, the envelope body, the OneDrive copy and Central's report are never shown to a model; the pickup never calls `IModelRunner` and never reads `ZYGGY_HOOKS` (AC-41 asserts both).
- **Logging** once per state change (per item id and cause), never a byte of the item.
- **Shared-contract edits** (all ⚠️, reviewed at the covering gate): `EnvelopeType.Item` + `ItemFields` + `ItemRoute` + `ItemBody` (Step 1), `BusFolder.Items` + `BusPaths.Items` + `AuditOutcome.WrongRecipient` (Step 2), `node.json` `archive.*` (Step 3), `BusRepository.PublishItemAsync` (Step 4), `BusRepository.SettleAsync` and the pass commit body (Step 9), report reason `archive_refused` (Step 9), `archive.json` `item_drive_wait_hours` (Step 9), `IGraphReader.DownloadAsync` + the download host (Step 11). `EnvelopeSigner.Canonicalize` and the existing goldens are **unchanged** (`git diff tests/golden/job* tests/golden/report* tests/golden/context*` empty at every gate).
- **Two repositories, two branches, nothing committed**; the executor reports per repository at each gate.

### Assumptions (reviewer: confirm or correct at the gate named)

- **A1 — ⚠️ Markdown items use the closed `txt` extension (Gate A; also listed as a question in the planner's report).** The spec's examples write `drive_name: staging-restore-notes.md`, `<folder>/calizr/staging-restore-notes.md` and memory `archive/calizr/staging-restore-notes.md`, but the code stores a Markdown item as `<slug>.txt` (`ArchiveMediaTypeWire.Extension(TextMarkdown) == "txt"`; `<slug>.md` is the **sidecar**), `md` is not in the closed list AC-2 cites (`ArchiveMediaTypeWire.IsItemExtension`), and the offload names the OneDrive file `<slug>.<ItemExtension>`, so adoption (AC-42) only works with `txt`. This plan therefore uses `drive_name: staging-restore-notes.txt`, OneDrive file `<folder>/calizr/staging-restore-notes.txt`, memory item `archive/calizr/staging-restore-notes.txt` and sidecar `archive/calizr/staging-restore-notes.md` wherever the spec's examples say `.md` for the item; the `item-onedrive` golden carries `.txt`. Whether a text item is Markdown is decided, on both sides, from the extension of `source_name` (as `ArchiveChecks` does from the source path today).
- **A2 — Body checks in the parser (Gate A).** AC-3/AC-4 are enforced by `EnvelopeParser` for `type: item` (it has the body): the length bound first (AC-4: longer than `ItemBody.MaxBodyLength(ArchiveOptions.ItemCeiling)` → `invalid_field (body)` before decoding), then the alphabet and line shape, then decoded length = `size` and SHA-256 = `sha256`; `via: onedrive` with a non-empty body → `invalid_field (body)`. So `verify`, `status` and the pickup all refuse a bad body the same way. `ItemBody.MaxBodyLength(n) = 4·⌈n/3⌉ + ⌈4·⌈n/3⌉ / 76⌉`.
- **A3 — Field limits (Gate A).** `name` 1–100 and `description` 1–149 Unicode scalars (`TextCollapse.CharCount`), no `\r`/`\n`; `source_name` 1–100 UTF-16 chars, no `char.IsControl`; `size` 1…`long.MaxValue` parsed by a new `FieldReader.TryLong` (invariant, digits only, no sign, no leading zero except `0` which is refused); `sha256` `^[0-9a-f]{64}$`; `drive_name` = `<slug>.<ext>` with the stem equal to `slug` and `ArchiveMediaTypeWire.IsItemExtension(ext)`. Messages via `FieldReader.Invalid(field, …)`.
- **A4 — Stage line wording (Gate B).** AC-16 "the spec 12 lines verbatim": the item verb prints `ContextCommand.RenderAsync`'s lines with the word `context` replaced by `item` (`zyggy: item failed at <stage>: <detail>`, `zyggy: refusing item: the envelope does not round-trip (<field>)` per AC-27); the signing-key, store and lock lines are byte-identical. `ContextCommand.RenderAsync` gains a `noun` parameter rather than a copy.
- **A5 — `node.json` `archive` is validated by `NodeConfigurationReader` (Gate B).** A bad `archive.itemMaxBytes` (not an integer, < 1, > 26,214,400) or a non-absolute `archive.oneDriveFolder` is a reader refusal `archive.<key>: <detail>`, so every verb and the node print `zyggy: configuration error: archive.<key> …` (AC-14, exit 3). `"oneDriveFolder": null` equals absent. The key name `archive` holds no secret word, so the secret-key refusal is unaffected.
- **A6 — The drop folder (Gate C).** `internal interface IArchiveDropFolder { DropProbe Probe(); DropPlacement Place(Slug project, string driveName, ReadOnlySpan<byte> bytes); string TargetPath(Slug project, string driveName); }` in `src/Zyggy.Core/Bus/ArchiveDropFolder.cs`, one implementation `FileSystemArchiveDropFolder(string root)`; `enum DropProbe { Available, Missing, NotAFolder, AccessDenied }` (wire `missing`, `not_a_folder`, `access_denied`); `DropPlacement` = `DropWritten | DropIdentical | DropTaken | DropFailed(string Detail)`. `Probe` = directory exists, is a directory, and a zero-byte probe file `.zyggy-probe-<ULID>` can be created and deleted (`UnauthorizedAccessException`/`IOException` → `AccessDenied`). `Place` creates `<root>/<project>/` when absent, writes with `FileMode.CreateNew` (never overwrites), flushes, re-reads and compares SHA-256; a failure after the file was created deletes **that** file only. Identical bytes already present → `DropIdentical` (AC-19); different bytes → `DropTaken` (AC-20). Not a §9 seam (no second backend in v1, spec Contracts); tests substitute it through an `internal` factory property on `ItemSubmitter`.
- **A7 — Pre-write pull (Gate C).** AC-16 requires lock and pull failures to stop the call before the OneDrive write, so `ItemSubmitter.SubmitAsync` calls `BusRepository.PullAsync` once after the key check and before the route; the publish then runs the normal `WriteAsync` (which pulls again). A pull failure → `SubmitFailed("pull", …)`; the busy result → `SubmitFailed("lock", …)`.
- **A8 — Central's archive configuration for the pickup (Gate D).** `PickupConfiguration.Load` keeps refusing start-up exactly as today for its five variables (spec 13), and additionally loads, **without failing start-up**: the archive options (`ArchiveConfiguration.Load`) only when `ZYGGY_ARCHIVE_CONFIG` or `ZYGGY_INSTANCE_DIR` is set — otherwise `ArchiveError = "archive configuration missing (ZYGGY_INSTANCE_DIR not set)"`; an invalid `archive.json` gives its error text. Any `ArchiveError` → every item waits, one Error `pickup: archive configuration missing: <detail>` once, contexts and reports unaffected (AC-38). The M365 part is A13.
- **A9 — Central's report (Gate D).** Built with `Envelope.CreateReport(new EnvelopeHeader(acme, EnvelopeId.New(now), central, <item from>, now, Normal, InReplyTo: <item id>), new ReportFields(Done | Failed, Failed ? "archive_refused" : null, …), body)` and signed by the pickup with the node's `signWith` (`KeyId` from DI, already mandatory in `NodeHost`). A signing failure (key absent, store unavailable) → the item waits, one Error `pickup: cannot sign reports: <detail>` once. Body on `done`: `archived: <item>` / `sidecar: <sidecar>` / `<index line>` / `commit: <sha> pushed | push deferred` / (OneDrive route) `onedrive: <root_folder>/<project>/<drive_name> (adopted by the next offload)`, each `\n`-ended; on `failed`: `archive: refused: <word> (<detail>)` (rendered by `ArchiveVerb.Render`, made `internal static`) and, OneDrive route, `the OneDrive copy at <path> stays; delete it in OneDrive if wanted`.
- **A10 — The pass commit (Gate D).** `BusRepository.SettleAsync(IReadOnlyList<(BusFile File, DateTimeOffset Created)> moves, IReadOnlyList<SettledItem> items, CancellationToken ct)`; `ArchiveAsync(files, ct)` becomes `SettleAsync(files, [], ct)` (its tests unchanged). `public sealed record SettledItem(BusFile Item, Model SignedReport, string Outcome)` with `Outcome` = `filed archive/<project>/<slug>.<ext>` or `refused <word>`. Subject `<tenant> archive <n> central→archive`, n = moves + removals; body: the existing move lines, then one `<items path> → <Outcome>` line per item. Change: `git mv` each move, `git rm` each item (`AssertMayArchive(path, source: true)` now accepts `BusFolder.Items`), write each report to `BusPaths.EnvelopeFile(BusPaths.Reports(tenant, central), reportId)` (`AssertMayWrite(…, BusFolder.Reports)`) + `git add`. Report paths join the commit's path list.
- **A11 — Item order and the per-pass flow (Gate D).** `ListPickupCandidatesAsync` returns every machine's `context/` and `reports/` files first (as today), then every machine's `items/` files (machines and files ordinal). Per item: verify → writer rule (`id` = file name, `type: item` in `items/`, `from` = folder machine) → `to == central` else **left** `wrong_recipient` (AC-35, unlike a context which is skipped) → bytes (decode, or the fetcher) → `AddBytesAsync` → outcome. Each memory commit is pushed by `AddBytesAsync` before the pass's bus commit (Behaviors). `PickupResult` gains `ItemsFiled`, `ItemsRefused`, `ItemsWaiting` as `init` properties (its positional record unchanged, so the spec-13 tests stay unedited); the pass's Information line gains the suffix `; items filed <f>, refused <r>` only when an item was settled — no second Information line (AC-28).
- **A12 — In-memory wait state (Gate E).** `ItemDriveFetcher` keeps `Dictionary<EnvelopeId, DateTimeOffset> _nextAttempt` (60 s after each lookup that did not file) and `DateTimeOffset? _holdUntil` (set from a 429's `Retry-After`, else 60 s); both are instance fields of a singleton (no static state); a restart retries at once (spec Contracts). `item_drive_wait_hours` is measured from the item's signed `created` against `TimeProvider.GetUtcNow()`; transient causes (429, 5xx, network, 403, not configured) never trigger the deadline refusal — only a 404 lookup after the deadline does (AC-36).
- **A13 — Graph in Central's node (Gate E).** `AddBusPickup` builds, lazily and once, a read-only Graph reader from the node environment through `M365Session.Load(new M365VerbContext(environment, clock, TimeZoneInfo.FindSystemTimeZoneById, processes, handler, checkKeyOwnership: true), baseOnly: false)` and `session.CreateGraph(context).Reader` (only `GraphParts.Reader` is used; `GraphParts.DriveWriter` is never called and no `IGraphDriveWriter` is registered). A load error or a sign-in failure → OneDrive items wait, one Error `pickup: OneDrive fetch not configured: <detail>` (AC-37). Test hook: `public sealed class ItemDriveOptions { public Func<HttpMessageHandler>? GraphHandlerFactory { get; set; } public bool CheckKeyOwnership { get; set; } = true; }` registered with `TryAddSingleton` by `AddBusPickup` and overridable by `NodeHost.RunAsync(…, configure)` (the `GitHubBusOptions.HandlerFactory` precedent).
- **A14 — The pickup's Graph exchange never sleeps (Gate E).** The pickup's `GraphHttp` is built with a new `internal` constructor argument `retries: 0`; a throttled answer then returns at once, and `GraphHttpResult`/`GraphFailure` gain an `init` property `TimeSpan? RetryAfter` carried from the response header, which the fetcher turns into its hold. The m365 verbs and the offload keep `GraphHttp.Retries = 5` (their tests unchanged).
- **A15 — The download host (Gate E).** `GraphEndpoints` gains `public static bool IsDownloadHost(Uri uri)` = HTTPS and host matching `^[a-z0-9-]+-my\.sharepoint\.com$` (the only file naming hosts, AC-35 of spec 33 still holds). `GraphReader.DownloadAsync(driveId, path, maxBytes, ct)` GETs `{Graph}/drives/{driveId}/root:/{escaped path}:/content` with the bearer header; a 404 → `Ok(null)`; a 302 whose `Location` passes `IsDownloadHost` is followed **once** without the `Authorization` header; any other redirect target → `GraphFailure(6, "download redirect to an unexpected host")`; the body is read as a stream and refused past `maxBytes` (`GraphFailure(6, "download larger than …")`). The test stub (`StubGraphHandler`) adds `acme-my.sharepoint.com` GETs to its allowed hosts; `StubOneDrive` answers `…/root:/<path>:/content` with a 302 to `https://acme-my.sharepoint.com/download/<id>` and serves the bytes there.
- **A16 — Template wording (Gate F).** `node/skills/archive/SKILL.md` follows the Central `archive` skill's step shape (get the content as a file — pasted text written to `~/.cache/zyggy/archive-staging/<slug>.txt` with the Write tool; read it as data; propose; show project, name, description, type, size **and the route** and wait for an explicit go; run exactly one `zyggy archive …` call; quote the two lines) without the Central-only parts (no `zyggy m365 parse`, no list/remove). The route shown before the go is "your OneDrive archive folder" when `archive.oneDriveFolder` is set in this laptop's `node.json`, else "the bus (the file reaches every machine's bus checkout and stays in the bus history)" — the skill reads `node.json` with Read, nothing else.

---

## Step 1 — Sign, serialise and read back an `item` envelope — a `via: bus` item with its file as 76-column base64, or a `via: onedrive` pointer with an empty body — and refuse every malformed field, route or body before anything trusts it; three hand-derived goldens pin the canonical form and signature while the existing `job`, `report`, `context` goldens stay byte-identical

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Envelope/EnvelopeType.cs` *(modify)*: `/// <summary><c>item</c>: an archive item sent to Central (deliverable 41).</summary> Item,`
- `src/Zyggy.Core/Envelope/EnvelopeWire.cs` *(modify)*: `item` ↔ `EnvelopeType.Item`; `public static string ToWire(ItemRoute route)` (`bus`, `onedrive`) and `TryFromWire(string?, out ItemRoute)`.
- `src/Zyggy.Core/Envelope/ItemRoute.cs` *(create)*: `public enum ItemRoute { Bus, OneDrive }` with XML docs citing W41-6.
- `src/Zyggy.Core/Envelope/ItemFields.cs` *(create)*: `public sealed record ItemFields(Slug Project, Slug Slug, string Name, string Description, string SourceName, long Size, string Sha256, ItemRoute Via, string? DriveName);`
- `src/Zyggy.Core/Envelope/ItemBody.cs` *(create)*: `public static class ItemBody { public const int LineLength = 76; public static byte[] Encode(ReadOnlySpan<byte> file); public static long MaxBodyLength(long maxBytes); public static bool TryDecode(Envelope item, [NotNullWhen(true)] out byte[]? bytes); internal static string? Check(ReadOnlySpan<byte> body, long size, string sha256, out byte[]? decoded); }` — `System.Convert` base64 only; `Encode` = standard alphabet, `=` padding, lines of 76 except the last (1–76), each ended by `\n`; `Check` returns the detail of the first fault (length bound, alphabet, line shape, decoded length, digest) or `null`.
- `src/Zyggy.Core/Envelope/Envelope.cs` *(modify)*: `public ItemFields? Item { get; }`, constructor parameter, `WithSignature` passes it; `public static Envelope CreateItem(EnvelopeHeader header, ItemFields item, ReadOnlyMemory<byte> fileBytes)` — `Common(header, Item)`, then `via`, `project`, `slug`, `drive_name` (only for `OneDrive`), `name`, `description`, `source_name`, `size`, `sha256`; body = `ItemBody.Encode(fileBytes)` for `Bus`, empty for `OneDrive` (a non-empty `fileBytes` with `OneDrive` → `ArgumentException("fileBytes")`); validated through `EnvelopeParser.Interpret` like the other factories. The class remark "exactly one of the three per-type views" becomes four.
- `src/Zyggy.Core/Envelope/EnvelopeParser.cs` *(modify)*: type message `must be job, report, context or item`; `ReadItem(out ItemFields?, ReadOnlyMemory<byte> body)` per A2/A3 (field order: `via`, `project`, `slug`, `drive_name`, `name`, `description`, `source_name`, `size`, `sha256`, then the body); the `type` switch gains `EnvelopeType.Item => ReadItem(...)`.
- `src/Zyggy.Core/Envelope/FieldReader.cs` *(modify)*: `internal static bool TryLong(string text, out long value)`.
- `tests/golden/item-bus.md`, `item-bus.canonical`, `item-bus.sig` *(create)*: `via: bus`, `from: home-laptop`, `to: central`, project `calizr`, slug `staging-restore-notes`, name `Staging restore notes`, description `How the staging database was restored`, source `restore-notes.md`, the text item of `tests/golden/README.md` §archive (68 bytes, its SHA-256), key `acme/1`.
- `tests/golden/item-bus-png.md|.canonical|.sig` *(create)*: a 1×1 PNG (the `ImageBytes.Png(1, 1)` shape materialised with `printf`), slug `roof-photo`, source `roof.png`, key `acme/1`.
- `tests/golden/item-onedrive.md|.canonical|.sig` *(create)*: `via: onedrive`, `drive_name: staging-restore-notes.txt` (A1), empty body, the text item's size and SHA-256, key `acme/1`.
- `tests/golden/README.md` *(modify)*: three rows in "Cases" and how the bytes were produced: lengths with `wc -c`, digests with `sha256sum`, the body with GNU `base64 -w 76` plus a final newline, canonical bytes with `sed '1d;/^sig: /d'`, signature with `openssl dgst -sha256 -hmac 'zyggy-golden-test-key-acme-00001'` — never the code under test.
- `tests/Zyggy.Core.Tests/Envelope/ItemEnvelopeTests.cs`, `ItemBodyTests.cs` *(create)*; `EnvelopeParserGoldenTests.cs`, `EnvelopeWriterTests.cs`, `EnvelopeWireTests.cs`, `EnvelopeFactoryTests.cs`, `EnvelopeSignerVerifyTests.cs` *(modify — item rows)*.

**Seams**: `ISecretStore` faked (`InMemorySecretStore` via `TestKeys.StoreWithBothAsync`; a counting store for AC-7 "never asked"). Pure otherwise.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Envelope/ItemEnvelopeTests.cs`
  - `CreateItem_BusRoute_SignSerialiseParseVerify_EveryFieldEqual_BodyDecodesToTheBytes_SchemaOne_OnlySection4Keys` (AC-1, bus: `Type == Item`, `Item!.Via == Bus`, `DriveName == null`, `ItemBody.TryDecode` equals the input bytes; key set exactly `schema, id, tenant, type, from, to, created, priority, via, project, slug, name, description, source_name, size, sha256, key_id, sig`)
  - `CreateItem_OneDriveRoute_DriveNamePresent_EmptyBody` (AC-1, onedrive: the key set adds `drive_name`; `Body.Length == 0`)
  - `Parse_MissingMandatoryField_MissingFieldNamesIt` (`[Theory]` over `via, project, slug, name, description, source_name, size, sha256`; AC-2)
  - `Parse_InvalidField_InvalidFieldNamesIt` (`[Theory]`, AC-2: `project: Calizr`, `slug: -x`, `name` 101 chars, `name` with `\n` (quoted YAML), `description` 150 chars, `source_name` 101 chars, `source_name` with U+0007, `size: 0`, `size: -1`, `size: 1.5`, `size: 01`, `sha256` uppercase, `sha256` 63 chars, `via: drive`, `drive_name` with `via: bus`, `drive_name` absent with `via: onedrive`, `drive_name: other-slug.txt`, `drive_name: staging-restore-notes.md`, `drive_name: staging-restore-notes.exe`)
  - `Parse_BusBody_BadAlphabet_LongLine_WrongLength_WrongDigest_InvalidFieldBody` (`[Theory]`, AC-3: a `*`, a 77-char line before the last, a body decoding to `size - 1`, a body whose digest ≠ `sha256`)
  - `Parse_OneDriveItemWithBody_InvalidFieldBody` (AC-3)
  - `Parse_BusBodyLongerThanCeilingBound_InvalidFieldBody_BeforeDecoding` (AC-4: a body of `MaxBodyLength(26_214_400) + 1` `A`s with a deliberately non-base64 tail is refused with the length detail, not the alphabet detail)
  - `Verify_ForeignTenant_TenantMismatch_StoreNeverAsked` and `Verify_KeyIdOfAnotherTenant_KeyIdTenantMismatch_StoreNeverAsked` (AC-7)
- Unit test file: `tests/Zyggy.Core.Tests/Envelope/ItemBodyTests.cs`
  - `Encode_ExactMultipleOf57Bytes_EveryLine76_LastLineEndsWithLf`
  - `Encode_OneByte_SingleLineWithPadding`
  - `MaxBodyLength_Ceiling_IsBase64LengthPlusLineBreaks` (26,214,400 → the formula of A2, asserted as a number computed by hand in the test comment)
  - `TryDecode_RoundTripsRandomBytes` (`[Theory]` over lengths 1, 57, 58, 1000)
- Unit test file: `tests/Zyggy.Core.Tests/Envelope/EnvelopeParserGoldenTests.cs` (append): `ItemGoldens_CanonicalBytesAndSignatureEqualTheFiles` (`[Theory]` `item-bus`, `item-bus-png`, `item-onedrive`; AC-5); `ItemGolden_OneByteOrFieldChangedAfterSigning_InvalidSignature` (`[Theory]`, AC-6, each tamper chosen so the parser still accepts the file and only the signature catches it: `item-onedrive` with `size` and with `sha256` changed (its empty body is not checked against them), `drive_name` changed to another valid `<slug>.<ext>` with `slug` changed alike, and `item-bus` with one body byte swapped for another base64 character together with matching `size`/`sha256` — the expected values written by hand in the test); `ExistingGoldens_Unchanged` (the existing theory stays green unedited).
- Unit test file: `tests/Zyggy.Core.Tests/Envelope/EnvelopeWireTests.cs` (append): `EnvelopeType_Item_WireIsItem`, `ItemRoute_EveryMember_WireAndBack`.
- Golden files: `tests/golden/item-bus.*`, `item-bus-png.*`, `item-onedrive.*` + expected signatures (hand-derived).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ItemEnvelopeTests|FullyQualifiedName~ItemBodyTests|FullyQualifiedName~EnvelopeParserGoldenTests|FullyQualifiedName~EnvelopeWireTests"`

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- The files above. `ItemFields` validation lives once, in `EnvelopeParser.ReadItem` (the factory goes through `Interpret`, the "one validation path" rule). Constants `NameMax = 100`, `DescriptionMax = 149`, `SourceNameMax = 100` move to `internal static class ArchiveLimits` in `src/Zyggy.Core/Memory/ArchiveLimits.cs`, used by `ArchiveArguments`, `ArchiveChecks.SourceName` and the parser (no behaviour change for 37's tests).

**Contract impact**: ⚠️ **§4 shared contract** (W41-6: type `item`, its mandatory fields, body rule) and ⚠️ **signing / canonical form**: `Canonicalize` is not edited; the new goldens prove an `item` canonicalises like any envelope (W41-6 "valid UTF-8"). ⚠️ **public API**: `EnvelopeType.Item`, `ItemRoute`, `ItemFields`, `ItemBody`, `Envelope.Item`, `Envelope.CreateItem`. `schema: 1` unchanged. `PROTOCOL.md` stays the placeholder.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`); the named tests pass; `git diff tests/golden/job* tests/golden/report* tests/golden/context*` empty; each new `.sig` is 64 bytes; `ArchiveArgumentsTests`, `ArchiveChecksTests` pass unedited.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — Keep item envelopes in the sender's own `nodes/<sender>/items/` folder and have `zyggy verify` judge them on a bare repository: `OK` for a well-formed item, `writer_mismatch` when `from` is not the folder's machine, `type_folder_mismatch` for another type in `items/`, and the new `wrong_recipient` when `to` is not `central`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusFolder.cs` *(modify)*: `/// <summary><c>…/items</c>: archive items written by the machine, addressed to Central (deliverable 41).</summary> Items,`
- `src/Zyggy.Core/Bus/BusPaths.cs` *(modify)*: `public static string Items(TenantId tenant, MachineName machine) => Node(tenant, machine) + "/items";` and `TryParseNode` case `"items" => BusFolder.Items`.
- `src/Zyggy.Core/Bus/AuditOutcome.cs` *(modify)*: `WrongRecipient` (wire `wrong_recipient`; `AuditWire.FromRejection(EnvelopeRejectionReason.WrongRecipient)` now maps to it).
- `src/Zyggy.Core/Bus/BusAuditor.cs` *(modify)*: expected type `BusFolder.Items => EnvelopeType.Item`; `case BusFolder.Items when envelope.Header.From != machine` → `WriterMismatch` (`from`); then `case BusFolder.Items when envelope.Header.To != MachineName.Central` → `WrongRecipient` (`to`, detail `to <machine>, items go to central`).
- `tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs` *(modify)*: `public async Task<SeededJob> SeedSignedItemAsync(MachineName from, MachineName to, KeyId key, ISecretStore store, ItemFields fields, byte[] fileBytes, DateTimeOffset? created = null, Func<byte[], byte[]>? mutate = null, CancellationToken ct = default)` — the `SeedSignedContextAsync` shape, written under `BusPaths.Items(tenant, from)`.
- `tests/Zyggy.Core.Tests/Bus/BusPathsTests.cs`, `BusAuditorTests.cs` *(modify)*; `tests/Zyggy.Integration/Cli/VerifyCommandTests.cs` *(modify)*.

**Seams**: `ISecretStore` faked; `IProcessRunner` real git in the integration test (bare repo + clone, `BusRepoFixture`).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusPathsTests.cs` (append): `Items_AcmeHomeLaptop_TenantPrefixedItemsFolder` (AC-8), `TryParse_ItemsFile_LocationWithItemsFolderAndId`, `TryParse_ItemsFileOtherTenantOrNonUlid_Refused` (`[Theory]`: `tenants/ACME/…`, `…/items/readme.md`, `…/items/sub/<ULID>.md`).
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusAuditorTests.cs` (append): `Judge_ItemFromFolderMachineToCentral_Ok`, `Judge_ItemFromAnotherMachine_WriterMismatchFrom`, `Judge_ContextInItemsFolder_TypeFolderMismatch`, `Judge_ItemToHomeLaptop_WrongRecipientTo` (AC-43), `AuditWire_WrongRecipient_IsWrongRecipient`.
- Integration test file: `tests/Zyggy.Integration/Cli/VerifyCommandTests.cs` *(modify)*:
  - `Verify_SignedItemInHomeLaptopsItemsFolder_OkLine` (happy path: `zyggy verify` on `central`'s node.json prints the `OK` line for `tenants/acme/nodes/home-laptop/items/<id>.md`, exit 0)
  - `Verify_ItemAddressedToHomeLaptop_WrongRecipientDrift` (failure path: the drift line names `wrong_recipient (to)`; the exit code is the one `VerifyCommand` already returns for any drift)
  - `Verify_ItemFromWorkLaptopInHomeLaptopsFolder_WriterMismatch`
  - Harness: local bare repo + clone (`CliHarness`, seeded with `central`, `home-laptop`, `work-laptop`).
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~VerifyCommandTests"`
- Failing-run command (unit): `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusPathsTests|FullyQualifiedName~BusAuditorTests"`

**GREEN**:
- The files above. `CliHarness.CreateAsync` seeds `work-laptop` too (the existing tests ignore it). `BusInventory.ListAsync` is not changed (status overview column deferred).

**Contract impact**: ⚠️ **§4 layout and write rules** (W41-6 layout line, W41-7 bullet 2: `items/` written only by its machine). ⚠️ **public API**: `BusFolder.Items`, `BusPaths.Items`, `AuditOutcome.WrongRecipient`. `zyggy verify` output gains one drift word (§8 Audit).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `VerifyCommandTests` and the existing `BusAuditorTests` pass.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A: the `item` envelope on the wire *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `ItemEnvelopeTests`, `ItemBodyTests`, the golden theory over `item-bus`, `item-bus-png`, `item-onedrive`, `BusPathsTests`, `BusAuditorTests`, `VerifyCommandTests.Verify_SignedItemInHomeLaptopsItemsFolder_OkLine` / `…WrongRecipientDrift…` green; the three `.md` goldens shown to the reviewer.
- [x] Contract review: the `item` key set and order against W41-6 (§4 item-specific fields, mandatory row `via, project, slug, name, description, source_name, size, sha256; drive_name when via is onedrive`); the body rule (76-column base64, `=` padding, `\n`, nothing else); `items/` layout line (W41-6) and writer rule (W41-7); **A1** (Markdown item = `.txt`, the `item-onedrive` golden's `drive_name`), A2 (body checks in the parser), A3 (field limits).
- [x] ⚠️ Risk review: `EnvelopeSigner.Canonicalize` untouched (`git diff src/Zyggy.Core/Envelope/EnvelopeSigner.cs` empty); existing goldens byte-identical; goldens derived by hand per `tests/golden/README.md` (the README rows name the commands); new public types each carry XML docs.
- [x] User approved — implementation may continue past this gate

---

## Step 3 — Refuse a file on the laptop before anything leaves it: usage faults, the `central` machine, `archive.*` configuration errors, a refused source (the laptop deny list), an empty, unrecognised, Office-refused or over-cap file, a secret-shaped line, name or description, a contact detail, and a composed index line over 400 characters — each with the archive's own words, the matched text nowhere; Central's `archive add` keeps its behaviour byte for byte

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/ArchiveChecks.cs` *(modify)*: split into `public static ArchiveSourceResult CheckSource(string source, SourceDenyList deny)` (steps 1–2: not a regular file, denied, not found, symlink/not normalised, read once, empty → `Refuse(Empty)`; returns `byte[] Bytes, string SourceName`) and `public static ArchiveCheckResult CheckContent(ArchiveContentRequest request, ArchiveCheckScope scope, ArchiveOptions? options, MemoryPaths? paths, SecretPatterns secrets, DateOnly today)` (type + Office, `TypeNotAllowed`/caps/slug only for `ArchiveCheckScope.Central`, size against `request.MaxBytes`, secrets on text lines, name, description, contact details, composed line, its length); `Check(...)` = `CheckSource` then `CheckContent(…, Central, …)` — unchanged results; `SourceName` becomes `internal static`. `internal enum ArchiveCheckScope { Central, Laptop }`; `internal sealed record ArchiveContentRequest(Slug Project, string Name, string Description, Slug Slug, string SourceName, byte[] Bytes, long MaxBytes)`.
- `src/Zyggy.Core/Memory/SourceDenyList.cs` *(modify)*: `public static SourceDenyList ForNode(IReadOnlyDictionary<string, string?> environment, NodeConfiguration node, string nodeJsonPath)` — the AC-12 list: `~/.ssh`, `~/.config/zyggy`, `~/.local/state/zyggy`, `~/.local/share/zyggy`, `$XDG_CONFIG_HOME/zyggy`, the folder of `nodeJsonPath`, `node.EffectiveNodeDir`, `node.Bus.LocalPath`, `node.Secrets` root, `CREDENTIALS_DIRECTORY`, `~/.claude` except `~/.claude/uploads/`, and `node.Archive.OneDriveFolder` (label `denied_location`).
- `src/Zyggy.Core/Configuration/ArchiveSettings.cs` *(create)*: `public sealed record ArchiveSettings { public const long ItemCeiling = 26_214_400; public long ItemMaxBytes { get; init; } = 10_485_760; public string? OneDriveFolder { get; init; } }`.
- `src/Zyggy.Core/Configuration/NodeConfiguration.cs` *(modify)*: `public ArchiveSettings Archive { get; init; } = new();`
- `src/Zyggy.Core/Configuration/NodeConfigurationReader.cs` *(modify)*: the `archive` object per A5.
- `src/Zyggy.Core/Memory/ArchiveArguments.cs` *(modify)*: the checks after option collection move to `internal static (ArchiveAddRequest? Request, string? Error) Validate(string? project, string? name, string? description, string? file, string? slug)`; `ParseAdd` calls it (messages unchanged).
- `src/Zyggy.Core/Bus/ItemRequest.cs` *(create)*: `public sealed record ItemRequestError(string Detail);` `public sealed class ItemRequest` (internal ctor; `Slug Project`, `string Name`, `string Description`, `string SourcePath`, `Slug Slug`, `bool ViaBus`) with `public static ItemRequestError? TryCreate(string? project, string? name, string? description, string? file, string? slug, string? via, out ItemRequest? request)` (`ArchiveArguments.Validate` messages; `--via` other than `bus` → `--via must be bus`) and `public static ItemRequestError? CheckSender(MachineName machine)` (→ `this machine is central; use zyggy memory archive add`).
- `src/Zyggy.Core/Bus/ItemChecks.cs` *(create)*: `internal static class ItemChecks { public static ItemCheckResult Check(ItemRequest request, ArchiveSettings settings, SourceDenyList deny, SecretPatterns secrets, DateOnly today); }` → `ItemCandidate(byte[] Bytes, ArchiveMediaType Type, string Sha256, string SourceName, string DriveName)` (`DriveName = slug + "." + ArchiveMediaTypeWire.Extension(type)`) or `(ArchiveRefusal, string? Detail)` or a usage message (index line > 400, exit 2).
- `tests/Zyggy.Core.Tests/Memory/ArchiveChecksTests.cs` *(unchanged, must stay green)*; `tests/Zyggy.Core.Tests/Bus/ItemRequestTests.cs`, `ItemChecksTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Memory/SourceDenyListTests.cs` *(modify — `ForNode` rows)*; `tests/Zyggy.Core.Tests/Configuration/NodeConfigurationReaderTests.cs` *(modify — archive rows)*.

**Seams**: none beyond the file system under a temp directory (`ScratchDirectory`-style) and the golden secret patterns; `TimeProvider` faked where a date is composed.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ItemRequestTests.cs`
  - `TryCreate_UsageFaults_OneMessageEach` (`[Theory]`, AC-9: underivable slug, `--project Calizr`, empty `--name`, 101-char `--name`, 150-char `--description`, relative `--file`, `--via onedrive` → the `ArchiveArguments` words / `--via must be bus`)
  - `TryCreate_Valid_SlugDerivedLikeArchiveAdd`
  - `CheckSender_Central_UseMemoryArchiveAdd` (AC-10 text) and `CheckSender_HomeLaptop_Null`
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ItemChecksTests.cs`
  - `Check_SourceRefused_DetailPerLocation` (`[Theory]`, AC-12 over every entry of `ForNode`, a missing file, a directory, a symlinked parent (Linux only, `SkipUnless`), a non-normalised path)
  - `Check_UploadsUnderDotClaude_Allowed` (AC-12 exception)
  - `Check_Empty_TypeRefused_OfficeRefused_TooLarge` (`[Theory]`, AC-13: zero bytes, random bytes, `golden/office/sample.docm` → `office_refused (macro_enabled)`, a 10,485,761-byte text → `too_large (10485761 > 10485760)`)
  - `Check_ItemMaxBytesSetLower_TooLargeAgainstIt`
  - `Check_SecretLine_Name_Description_ContactDetail_RefusedByPatternName_TextNowhere` (`[Theory]` over `SecretPatternsTests.SecretSamples` in a line, in `--name`, in `--description`; a mail address in the name → `contact_detail (name)`; AC-15)
  - `Check_ComposedIndexLineOver400_UsageMessage`
  - `Check_MarkdownSource_TypeTextMarkdown_DriveNameTxt` (A1: `restore-notes.md` → `drive_name staging-restore-notes.txt`)
  - `Check_NoAllowedTypesNoCapsNoSlugCheck_OnTheLaptop` (a PNG passes with no `ArchiveOptions` at all)
- Unit test file: `tests/Zyggy.Core.Tests/Configuration/NodeConfigurationReaderTests.cs` (append): `Read_ArchiveAbsent_Defaults`, `Read_ArchiveItemMaxBytesOutOfRange_RefusedNamesKey` (`[Theory]` 0, 26214401, `"10"`, 1.5; AC-14), `Read_OneDriveFolderRelative_RefusedNamesKey`, `Read_OneDriveFolderNull_EqualsAbsent`.
- Unit test file: `tests/Zyggy.Core.Tests/Memory/SourceDenyListTests.cs` (append): `ForNode_CoversEveryAc12Location_AndTheOneDriveFolder`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ItemRequestTests|FullyQualifiedName~ItemChecksTests|FullyQualifiedName~SourceDenyListTests|FullyQualifiedName~NodeConfigurationReaderTests|FullyQualifiedName~ArchiveChecksTests|FullyQualifiedName~ArchiveArgumentsTests"`

**GREEN**:
- The files above. `ArchiveService.AddAsync` still calls `ArchiveChecks.Check` (refactored onto the two halves in Step 8).

**Contract impact**: ⚠️ **§10 `node.json`** (W41-15: `archive.itemMaxBytes`, `archive.oneDriveFolder`). ⚠️ **secrets**: the laptop pre-scan before anything leaves (decision 1 "no weaker checks"). ⚠️ **public API**: `ItemRequest`, `ItemRequestError`, `ArchiveSettings`, `NodeConfiguration.Archive`, `SourceDenyList.ForNode` (the class stays internal: `ForNode` is `internal`; only `ItemRequest*`, `ArchiveSettings` and `NodeConfiguration.Archive` are public).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ArchiveChecksTests`, `ArchiveArgumentsTests`, `ArchiveVerbAddTests`, `ArchiveVerbRefusalTests` pass **unedited**.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — Build, sign and publish a passed file as a `via: bus` item into the sender's own `items/` folder through the one §5 write sequence, and name every outcome — sent, committed locally, key or store failure, lock or pull failure, refused by a check, or a round trip that does not read back — never throwing and writing nothing on any refusal

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify)*: `public Task<WriteOutcome> PublishItemAsync(Model signedItem, CancellationToken ct)` — the `PublishContextAsync` body with `EnvelopeType.Item`, `To == MachineName.Central` (else `InvalidOperationException("write rule: the item envelope is not addressed to central")`), `BusPaths.Items(_me.Tenant, _me.Machine)`, `AssertMayWrite(path, BusFolder.Items)`, subject `Subject("item", id, from, "central")`.
- `src/Zyggy.Core/Bus/SubmitResult.cs` *(modify)*: `public sealed record ItemRefused(ArchiveRefusal Refusal, string? Detail) : SubmitResult;` `public sealed record ItemUsage(string Message) : SubmitResult;` `public sealed record OneDriveUnavailable(string Reason, string Folder) : SubmitResult;` `public sealed record OneDriveWriteFailed(string Detail) : SubmitResult;` (the last two are produced from Step 6).
- `src/Zyggy.Core/Bus/ItemSubmission.cs` *(create)*: `public sealed record ItemSubmission(SubmitResult Result, ItemRoute? Route, string? ItemPath, string? Name, string? MediaType, long Size);` — `ItemPath` = `<project>/<drive_name>`, `MediaType` = `ArchiveMediaTypeWire.ToWire`.
- `src/Zyggy.Core/Bus/ItemSubmitter.cs` *(create)*: `public sealed partial class ItemSubmitter` with `internal ItemSubmitter(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, NodeConfiguration node, SourceDenyList deny, TimeProvider clock, ILogger<ItemSubmitter> log)`, `internal Func<Model, byte[]> Serializer { get; init; } = EnvelopeWriter.Serialize;`, `public Task<ItemSubmission> SubmitAsync(ItemRequest request, CancellationToken ct)`: (1) `ItemChecks.Check` → `ItemRefused` / `ItemUsage` (nothing signed); (2) `_bus.CheckSigningKeyAsync(signWith)` → `Missing` → `SubmitFailed("sign", …)`, `StoreUnavailable` → `SubmitFailed("secret-store", …)`; (3) `_bus.PullAsync` (A7) → `SubmitFailed("lock" | "pull", …)`; (4) route = `Bus` in this step (OneDrive in Step 6); (5) `Model.CreateItem(header, new ItemFields(…, ItemRoute.Bus, null), candidate.Bytes)`, sign, `Serializer`, `ParseAndVerifyAsync`, `Differs` over `id, tenant, type == Item, from, to == central, created, priority, in_reply_to null, via, project, slug, drive_name, name, description, source_name, size, sha256`, the body bytes and the key set → `SubmitRefused(field, …)`; (6) `PublishItemAsync` → `Submitted | SubmitDeferred | SubmitFailed`. `LoggerMessage` lines carry ids, routes and pattern names only.
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: `TryAddSingleton` factory for `ItemSubmitter` beside `ContextSubmitter` (needs `NodeConfiguration`, `SecretPatterns`, `KeyId`; the deny list is built in the factory with `SourceDenyList.ForNode(environment, node, nodeJsonPath)` from a registered `ItemSubmitterOptions { IReadOnlyDictionary<string, string?> Environment; string NodeJsonPath; }` that the verb registers).
- `tests/Zyggy.Core.Tests/Bus/BusRepositoryWriteTests.cs` *(modify — item rows)*; `tests/Zyggy.Core.Tests/Bus/ItemSubmitterBusTests.cs` *(create)*.

**Seams**: `IBusProvider` substituted (pull ok / `BusSyncResult(false, …)`; push `Rejected` ×4), `IProcessRunner` faked (`RecordingProcessRunner`), `ISecretStore` faked (`InMemorySecretStore`, `ThrowingSecretStore`, an empty store), `TimeProvider` faked.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusRepositoryWriteTests.cs` (append): `PublishItemAsync_SignedItemFromMe_FileUnderMyItemsFolder_SubjectAcmeItemIdHomeLaptopToCentral`; `PublishItemAsync_Unsigned_ForeignTenant_FromAnotherMachine_NotToCentral_NotAnItem_Throws` (`[Theory]`).
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ItemSubmitterBusTests.cs` (identity `work-laptop`, `node.Archive.OneDriveFolder == null`):
  - `SubmitAsync_NoOneDriveFolder_ViaBusItemPublished_SubmittedWithItemsPath_RouteBus_FileVerifies` (AC-17, AC-25 through the seam: `Route == Bus`, the file at `tenants/acme/nodes/work-laptop/items/<id>.md` decodes to the source bytes)
  - `SubmitAsync_PushRejectedFourTimes_SubmitDeferred` (AC-26 through the seam)
  - `SubmitAsync_KeyAbsent_StoreUnavailable_LockHeld_PullFails_SubmitFailedStage_NothingWritten` (`[Theory]`, AC-16: `_processes.Calls` holds no `add`/`commit`)
  - `SubmitAsync_RefusedByCheck_ItemRefused_SignerNeverCalled_BusNeverCalled` (AC-13: the store counts zero reads; `_provider.ReceivedCalls()` empty)
  - `SubmitAsync_SecretLine_ItemRefusedSecretPattern_TextInNoLogLine` (AC-15: `SecretSweep.AssertAbsent(_log.Messages)`)
  - `SubmitAsync_SerializerCorruptsOneBodyByte_SubmitRefusedBody` and `…CorruptsName_SubmitRefusedNameOrSig` (AC-27)
  - `SubmitAsync_DlpEnabledOnWorkLaptop_NoExtraRule` (AC-25: a `node.json` with `dlp.enabled: true` changes nothing)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryWriteTests|FullyQualifiedName~ItemSubmitterBusTests"`

**GREEN**:
- The files above.

**Contract impact**: ⚠️ **§4 write rule / state machine row 1** (W41-7, W41-8: `(none) → items/<id>.md`, sender ≠ Central) and **§5 subject** (W41-9 `item`). ⚠️ **public API**: `ItemSubmitter`, `ItemSubmission`, the four new `SubmitResult` records, `BusRepository.PublishItemAsync`.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ContextSubmitterSubmitTests` pass unedited.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 5 — `zyggy archive` sends a file from `work-laptop`'s `node.json` through the bus to a bare repository: exit 0 with the two output lines ending `— via bus`, exit 7 when every push is refused, exit 5 for an unattended run or a refusal, exit 2 on `central` or a usage fault, exit 6 for a failed pull — the matched secret in no output and nowhere in the bare repository; `zyggy status --id` shows the item queued at the sender for Central

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/ArchiveCommand.cs` *(create)*: `internal static class ArchiveCommand { public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure); }` — options `--project`, `--name`, `--description`, `--file`, `--slug`, `--via` (each `Option<string?>`, arity exactly one; a repeated option is a parse error → exit 2), `BusHost.ConfigOption`, `BusHost.VerboseOption`. Order exactly the spec's CLI surface: usage (`ItemRequest.TryCreate`) → `ZYGGY_HOOKS=off` → exit 5 `zyggy: refused: unattended` (before `node.json`; the file is not opened) → `BusHost.LoadConfiguration` (3) → `ItemRequest.CheckSender` (2) → `signWith missing` (3) → `AddSecretPatterns` (3) → `BuildServices` (registers `ItemSubmitterOptions`) → `CheckCheckoutAsync` (3) → `ItemSubmitter.SubmitAsync` → render. Output on 0/7: `item <id> to central (<path>)[ (committed locally, not yet pushed: the next bus write on this machine pushes it)]` then `<project>/<drive_name> "<name>" (<media type>, <size as ArchiveIndexLine prints it>) — via bus|OneDrive — check: zyggy status --id <id>`. Refusals: `zyggy: refused: <word> [(<detail>)]` (secret pattern: `zyggy: refused: secret_pattern <pattern> (line <n> | name | description)`).
- `src/Zyggy.Cli/Commands/ContextCommand.cs` *(modify)*: `RenderAsync` gains `string noun = "context"` (A4); `ArchiveCommand` calls it with `"item"` for the shared failure lines.
- `src/Zyggy.Cli/CliApplication.cs` *(modify)*: register `ArchiveCommand.Create(environment, io, configure)`; the top-level `archive` never collides with `memory archive` (that stays a raw verb).
- `src/Zyggy.Core/Bus/BusInventory.cs` *(modify)*: `TraceAsync` also looks in `BusPaths.Items(tenant, machine)` → `TraceState.Queued` with the item's summary; reports answering an item id are found as today (Central's `reports/`).
- `src/Zyggy.Core/Bus/InventoryReport.cs` *(modify)*: the summary record gains `ItemRoute? Via` and `string? Project` (filled for items).
- `src/Zyggy.Cli/Commands/StatusCommand.cs` *(modify)*: the `--id` header line for an item: `item    <id>  <from>→central  project <project>  via <bus|onedrive>  created <created>`; state `queued at <from> for central` (AC-44 before).
- `tests/Zyggy.Integration/Infrastructure/NodeJson.cs` *(modify)*: optional `archiveItemMaxBytes`, `oneDriveFolder`, `dlpEnabled` parameters.
- `tests/Zyggy.Integration/Cli/CliHarness.cs` *(modify)*: `WorkLaptopNodeJson` (machine `work-laptop`, no `oneDriveFolder`, `dlp.enabled: true`) over a third clone.
- `tests/Zyggy.Integration/Cli/ArchiveCommandTests.cs` *(create)*; `tests/Zyggy.Integration/Cli/StatusCommandTests.cs` *(modify)*; `tests/Zyggy.Core.Tests/Bus/BusInventoryTests.cs` *(modify)*.

**Seams**: `IProcessRunner` wired (real git through `HookedProcessRunner`), `IBusProvider` wired (`GitHubBusProvider` with `BareRepoHeadHandler`), `ISecretStore` wired (file store under the scratch root), `TimeProvider` system.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusInventoryTests.cs` (append): `TraceAsync_ItemInSendersItemsFolder_QueuedWithItemSummary`.
- Integration test file: `tests/Zyggy.Integration/Cli/ArchiveCommandTests.cs` *(create)*:
  - `Archive_WorkLaptopNoOneDriveFolder_DlpOn_ViaBusItemOnMain_TwoLinesEndingViaBus_Exit0` (AC-25 happy path: `ParseOnMainAsync` → `Item.Via == Bus`, body decodes to the source; stdout exactly the two lines; one commit `acme item <id> work-laptop→central`)
  - `Archive_PushRefusedEveryAttempt_Exit7_LineOneEndsCommittedLocally` (AC-26, `Hooks.Override` refusing push ×4)
  - `Archive_SecretLineInFile_Exit5_RefusedNamesPatternAndLine_SecretNowhere` (AC-15 failure path: `SecretSweep.AssertAbsent` over stdout, stderr, the clone and `git log -p` of the bare repo; no new commit)
  - `Archive_PullFails_Exit6_ItemFailedAtPull_NothingCommitted` (AC-16, `Bus.DeleteBare()`)
  - `Archive_OnCentral_Exit2_UseMemoryArchiveAdd` (AC-10) and `Archive_HooksOff_Exit5_RefusedUnattended_FileNotOpened` (AC-11: the source path is a file the test locks exclusively; the verb still answers 5)
  - `Archive_FileOverCap_Exit5_TooLarge_NoCommit` (AC-13) and `Archive_RepeatedOption_Exit2_OneLine` (AC-9)
  - Harness: local bare repo + clones (`CliHarness`), file secret store; no fake-claude needed.
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveCommandTests"`
- Integration test file: `tests/Zyggy.Integration/Cli/StatusCommandTests.cs` (append): `StatusId_ItemSentFromHomeLaptop_HeaderLineAndQueuedAtHomeLaptopForCentral` (AC-44 before).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveCommandTests|FullyQualifiedName~StatusCommandTests"`

**GREEN**:
- The files above.

**Contract impact**: ⚠️ **§9 Cli verbs** (W41-4/W41-14: `archive`). ⚠️ **work boundary** (decision 2, W41-11): no DLP filter or extra rule for an item from `work-laptop` — proven by the DLP-on test. ⚠️ **secrets**: the integration sweep.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ContextCommandTests` pass unedited; `zyggy --help` lists `archive`.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B: a laptop sends a file through the bus *(covers Steps 3–5)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `ItemRequestTests`, `ItemChecksTests`, `ItemSubmitterBusTests`, `ArchiveCommandTests`, `StatusCommandTests.StatusId_ItemSentFromHomeLaptop_…` green; one sample run shown (stdout of AC-25, the item file on the bare repo's `main`); `ArchiveChecksTests` and the `memory archive` verb tests unedited and green.
- [x] Contract review: the CLI surface and check order against the spec's CLI section; the exit-code table (0/2/3/5/6/7); the `node.json` `archive` keys (W41-15) and A5; A4 (stage-line wording); the two output lines (AC-18 shape, `— via bus`).
- [x] ⚠️ Risk review: no secret text in any output, log, envelope or bus commit (the sweeps); the work-laptop sends with no extra rule and DLP untouched (decision 2); new public API documented; `Directory.Packages.props` unchanged.
- [x] User approved — implementation may continue past this gate

---

## Step 6 — Choose the route from `archive.oneDriveFolder` and `--via bus` and send a OneDrive-route item through a substituted drop folder: the bytes are placed and verified before signing, an identical file already there is reused, a different one is `slug_taken (onedrive)`, an unavailable folder is exit 6 with the `--via bus` hint and never a silent fallback, a failed or mismatching write removes only the file this call created and publishes nothing, and `--via bus` never touches the folder

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/ArchiveDropFolder.cs` *(create)*: the interface, `DropProbe`, `DropPlacement` records and `DropProbeWire` per A6 (the file-system implementation's body is Step 7; this step adds the type with a minimal implementation only if needed to compile).
- `src/Zyggy.Core/Bus/ItemSubmitter.cs` *(modify)*: `internal Func<string, IArchiveDropFolder> DropFolderFactory { get; init; } = root => new FileSystemArchiveDropFolder(root);` route step: `request.ViaBus || node.Archive.OneDriveFolder is null` → `Bus` (the folder is not touched); else `Probe()` ≠ `Available` → `OneDriveUnavailable(DropProbeWire.ToWire(probe), folder)` (nothing written, nothing published); else `Place(project, driveName, bytes)`: `DropWritten`/`DropIdentical` → build `ItemFields(…, ItemRoute.OneDrive, driveName)` with an empty body → sign → round trip → publish; `DropTaken` → `ItemRefused(SlugTaken, "onedrive")`; `DropFailed(d)` → `OneDriveWriteFailed(d)`. Order stays: checks → key → pull → route → place → sign (AC-16).
- `tests/Zyggy.Core.Tests/Bus/ItemSubmitterOneDriveTests.cs` *(create)*.

**Seams**: `IArchiveDropFolder` substituted (NSubstitute over the internal interface; `InternalsVisibleTo` already covers `DynamicProxyGenAssembly2` — the executor confirms, else a hand-written `ScriptedDropFolder` test double), `IBusProvider` substituted, `IProcessRunner` faked, `ISecretStore` faked, `TimeProvider` faked.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ItemSubmitterOneDriveTests.cs` (identity `home-laptop`, `OneDriveFolder = <temp>`):
  - `SubmitAsync_FolderAvailable_PlacedThenViaOneDriveItemPublished_EmptyBody_DriveNameSlugTxt_RouteOneDrive` (A1; `Place` received before `SignAsync` — call order recorded)
  - `SubmitAsync_IdenticalFileAlreadyThere_NothingRewritten_EnvelopePublished` (AC-19)
  - `SubmitAsync_DifferentFileThere_ItemRefusedSlugTakenOneDrive_NoEnvelope` (AC-20)
  - `SubmitAsync_FolderMissingNotAFolderAccessDenied_OneDriveUnavailableWithReason_NothingPublished_NoFallback` (`[Theory]`, AC-21: `_processes.Calls` has no `add`; no bus file)
  - `SubmitAsync_PlaceFailed_OneDriveWriteFailed_NoEnvelope` (AC-22)
  - `SubmitAsync_ViaBusWithFolderConfigured_FolderNeverTouched_ViaBusItem` (AC-24: `Probe` and `Place` never received)
  - `SubmitAsync_FolderAbsentFromNodeJson_FolderFactoryNeverCalled` (AC-17)
  - `SubmitAsync_RefusedByCheck_FolderNeverTouched` and `SubmitAsync_PullFails_FolderNeverTouched` (AC-15/AC-16 "nothing written to the OneDrive folder")
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ItemSubmitterOneDriveTests"`

**GREEN**:
- The route branch above; `ItemSubmission.Route` set for every published outcome.

**Contract impact**: ⚠️ **work boundary / decision 3**: nothing reaches the OneDrive folder that the bus route would refuse; an unavailable folder never falls back silently. The envelope contract is Step 1's (no change).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ItemSubmitterBusTests` unchanged and green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 7 — `zyggy archive` from `home-laptop` with `archive.oneDriveFolder` = a temp folder writes `<folder>/calizr/staging-restore-notes.txt` (project folder created), re-reads it, publishes one `via: onedrive` item and prints exactly the AC-18 lines; a missing folder is exit 6 with the hint; `--via bus` sends through the bus; a publish that fails after the write is re-run with the same command and publishes exactly one envelope

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/ArchiveDropFolder.cs` *(modify)*: `internal sealed class FileSystemArchiveDropFolder(string root) : IArchiveDropFolder` per A6 (`TargetPath = Path.Join(root, project.Value, driveName)` — the one builder).
- `src/Zyggy.Cli/Commands/ArchiveCommand.cs` *(modify)*: render `OneDriveUnavailable` → exit 6 `zyggy: OneDrive folder <path> is not available (<reason>): fix the folder, or archive through the bus with --via bus`; `OneDriveWriteFailed` → exit 6 `zyggy: OneDrive write failed: <detail>`; `ItemRefused(SlugTaken, "onedrive")` → exit 5 `zyggy: refused: slug_taken (onedrive)`; line 2 ends `— via OneDrive` for the OneDrive route.
- `tests/Zyggy.Core.Tests/Bus/FileSystemArchiveDropFolderTests.cs` *(create)*; `tests/Zyggy.Integration/Cli/ArchiveCommandOneDriveTests.cs` *(create)*.

**Seams**: the file system (temp folder standing in for the synced OneDrive folder) wired; `IProcessRunner`, `IBusProvider`, `ISecretStore` wired as in Step 5.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/FileSystemArchiveDropFolderTests.cs`:
  - `Probe_Missing_NotAFolder_Available` (`[Theory]`), `Probe_ReadOnlyDirectory_AccessDenied` (Linux only, `chmod 0555`; skipped elsewhere)
  - `Place_NewFile_ProjectFolderCreated_BytesEqual_DropWritten`; `Place_SameBytesPresent_DropIdentical_FileUntouched` (mtime unchanged); `Place_OtherBytesPresent_DropTaken_FileUntouched`
  - `Place_WriteThrowsMidway_DeletesOnlyItsOwnFile_DropFailed` (a `Stream` wrapper factory `internal Func<string, Stream>` throwing after N bytes; a sibling file in the folder survives)
- Integration test file: `tests/Zyggy.Integration/Cli/ArchiveCommandOneDriveTests.cs` *(create)*:
  - `Archive_HomeLaptopOneDriveFolder_FileInFolder_ViaOneDriveItemOnMain_ExactTwoLines_Exit0` (AC-18 happy path: `<folder>/calizr/staging-restore-notes.txt` SHA-256 = source; one commit `acme item <id> home-laptop→central`; item `drive_name: staging-restore-notes.txt`, empty body; stdout exactly `item <id> to central (tenants/acme/nodes/home-laptop/items/<id>.md)` and `calizr/staging-restore-notes.txt "Staging restore notes" (text/markdown, 2.0 KB) — via OneDrive — check: zyggy status --id <id>`)
  - `Archive_OneDriveFolderMissing_Exit6_HintViaBus_NothingOnBus` (AC-21 failure path)
  - `Archive_OneDriveFolderMissing_WithViaBus_ViaBusItem_FolderUntouched` (AC-24)
  - `Archive_PushRefusedAfterWrite_Exit7_FileStays_RerunSameCommand_IdenticalReused_ExactlyOneMoreEnvelope` (AC-23: the second run's commit count +1, no second file in the folder)
  - `Archive_SecretInName_Exit5_FolderEmpty_BareRepoUnchanged` (AC-15: `SecretSweep.AssertAbsent` over outputs, the temp folder and the bare repo)
  - Harness: `CliHarness` with `HomeLaptopNodeJson` rewritten through `NodeJson.Write(…, oneDriveFolder: Scratch.File("onedrive"))`.
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveCommandOneDriveTests"`
- Failing-run command (unit): `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~FileSystemArchiveDropFolderTests"`

**GREEN**:
- The files above.

**Contract impact**: none beyond Step 6 (the laptop writes only under `archive.oneDriveFolder/<project>/`; no Graph call — decision 4).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ArchiveCommandTests` green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C: a laptop sends a file through its OneDrive folder *(covers Steps 6–7)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `ItemSubmitterOneDriveTests`, `FileSystemArchiveDropFolderTests`, `ArchiveCommandOneDriveTests` green; the AC-18 stdout and the temp folder listing shown.
- [x] Contract review: route rule (key absent = bus, configured but unavailable = exit 6, never a silent switch — Decision Table "No access" row); A6 (drop folder: probe file, `CreateNew`, re-read, delete only own file); A7 (pre-write pull); the exit-6 sentence verbatim against AC-21.
- [x] ⚠️ Risk review: nothing reaches the folder that the bus route would refuse (refusal and pull tests assert an empty folder); no Graph, credential or network call on the laptop (`git grep -n "Graph" src/Zyggy.Core/Bus/ItemSubmitter.cs src/Zyggy.Core/Bus/ArchiveDropFolder.cs` empty).
- [x] User approved — implementation may continue past this gate

---

## Step 8 — File given bytes through the one archive writer exactly as `zyggy memory archive add` would — same closed checks, sidecar (with the given source name), `[stated]` index line and memory commit — and recognise an item already filed (a sidecar with the same SHA-256, committed or not) without a second item or index line

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/ArchiveService.cs` *(modify)*: `public Task<ArchiveOutcome> AddBytesAsync(ArchiveAddBytes request, CancellationToken ct)` — preflight → **already filed?** (sidecar at `paths.ArchiveSidecar(project, slug)` parses and its `Sha256` equals the bytes' digest → if the two paths are uncommitted (`git status --porcelain` through `GitClient`), `CommitAndPushAsync` them with the `ArchiveCommitMessage.Add` message; return `AlreadyFiled(item, sidecar, sha, pushed)`) → `ArchiveChecks.CheckContent(…, ArchiveCheckScope.Central, options, paths, …)` → write item + sidecar (`SourceName = request.SourceName`) → index line → commit + push — steps 3–5 of `AddAsync` moved into one private method both call. `AddAsync` = preflight → `CheckSource` → the same private method.
- `src/Zyggy.Core/Memory/ArchiveAddBytes.cs` *(create)*: `internal sealed record ArchiveAddBytes(Slug Project, string Name, string Description, Slug Slug, string SourceName, byte[] Bytes);`
- `src/Zyggy.Core/Memory/ArchiveOutcome.cs` *(modify)*: `ArchiveOutcomeKind.AlreadyFiled` and `ArchiveOutcome.AlreadyFiled(string item, string sidecar, string? sha, bool pushed)`.
- `tests/Zyggy.Core.Tests/Memory/ArchiveServiceAddBytesTests.cs` *(create)*; `ArchiveServiceTests.cs` *(unchanged, must stay green)*.

**Seams**: `IProcessRunner` faked (`RecordingProcessRunner` scripting `rev-parse`, `status`, `add`, `commit`, `push`), `TimeProvider` faked (`2026-09-30T10:00:00Z`, `Europe/Brussels` → UTC off Linux as the 37 tests do); memory tree on a temp directory.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/ArchiveServiceAddBytesTests.cs`:
  - `AddBytesAsync_SameInputsAsAddAsync_ItemSidecarIndexLineAndCommitMessageByteIdentical` (AC-30 unit half: two temp trees, `AddAsync` with the golden text item from a file vs `AddBytesAsync` with the same bytes and `SourceName = "quote.txt"`; compares `golden/archive/sidecar-text.md`, the inbox file and the recorded commit stdin)
  - `AddBytesAsync_MarkdownSourceName_StoredTxtTypeTextMarkdown` (A1)
  - `AddBytesAsync_TypeNotAllowed_ProjectCap_TotalCap_SlugTakenOtherSha_SecretLine_ContactDetail_OfficeRefused_Refused_NothingWritten` (`[Theory]`, AC-33 check half)
  - `AddBytesAsync_SidecarWithSameShaCommitted_AlreadyFiled_NoWriteNoCommit` (AC-39)
  - `AddBytesAsync_SidecarWithSameShaUncommitted_AlreadyFiled_CommittedAndPushed` (AC-39)
  - `AddBytesAsync_NotOnBranch_IndexLock_GitError_NothingWritten` (`[Theory]`, AC-38 git half)
  - `AddBytesAsync_PushNotRecoverable_ArchivedPushedFalse` (AC-40 writer half)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ArchiveServiceAddBytesTests|FullyQualifiedName~ArchiveServiceTests|FullyQualifiedName~ArchiveVerbAddTests"`

**GREEN**:
- The files above.

**Contract impact**: none on the wire; ⚠️ **one writer** (decision 1): the reviewer checks `AddAsync` and `AddBytesAsync` share one write path (`git diff` shows the move, no second sidecar writer).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; every `Archive*Tests` (unit and integration) passes unedited.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 9 — Central's pickup settles a `via: bus` item in one pass through fakes: filed through the one writer with a signed `done` report, refused by a check with `failed` / `archive_refused`, left untouched with one Warning when it does not verify or breaks the writer or recipient rule, or waiting (memory busy, archive configuration missing, report key unusable, any exception) — the `items/` file removed and the report written in the pass's single commit; a `via: onedrive` item waits with one Error until Slice E

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify)*: `ListPickupCandidatesAsync` per A11; `SettleAsync` + `SettledItem` per A10; `AssertMayArchive` accepts `BusFolder.Items` as a source.
- `src/Zyggy.Core/Bus/PickupCandidate.cs` *(modify)*: `Kind` may be `BusFolder.Items`.
- `src/Zyggy.Core/Bus/PickupResult.cs` *(modify)*: `ItemsFiled`, `ItemsRefused`, `ItemsWaiting` (`init`), `PickupLeftReason.WrongRecipient` (wire `wrong_recipient`).
- `src/Zyggy.Core/Bus/PickupItemReport.cs` *(create)*: `internal static class PickupItemReport { public static byte[] DoneBody(ArchiveOutcome outcome, string? oneDrivePath); public static byte[] RefusedBody(string word, string? detail, string? oneDrivePath); }` (A9).
- `src/Zyggy.Core/Bus/BusPickup.cs` *(modify)*: constructor gains `KeyId signWith`, `ItemFiler? items` (null when the archive part did not load → items wait); item path per A11; `PickupItemOutcome { Filed, AlreadyFiled, Refused, Waits, Left }`; `LoggerMessage` lines: Warning `pickup: item {Id} refused: {Word} ({Detail})` once per id, Error `pickup: archive configuration missing: {Detail}` once, Error `pickup: cannot sign reports: {Detail}` once, Error `pickup: item {Id} failed: {ExceptionType}` once (AC-41, the item stays), Information `pickup: item {Id} waits for OneDrive` (Slice E), Error `pickup: OneDrive fetch not configured` once (a `via: onedrive` item in this step — AC-37).
- `src/Zyggy.Core/Bus/ItemFiler.cs` *(create)*: `internal sealed class ItemFiler(ArchiveService archive, ArchiveOptions options)` — `FileAsync(Model item, byte[] bytes, CancellationToken)` → `ArchiveAddBytes` (from the signed fields) → `ArchiveOutcome`; a bus body over `options.ItemMaxBytes` (from `size`) → `TooLarge` **before decoding** (AC-34).
- `src/Zyggy.Core/Memory/ArchiveVerb.cs` *(modify)*: `Render` becomes `internal static` (one rendering of `<word> (<detail>)`).
- `src/Zyggy.Core/Memory/ArchiveOptions.cs` *(modify)*: `public int ItemDriveWaitHours { get; init; } = 24;` validated 1–168 (`item_drive_wait_hours`).
- `src/Zyggy.Core/Memory/ArchiveConfiguration.cs` *(modify)*: parse `item_drive_wait_hours`.
- `src/Zyggy.Core/Memory/PickupConfiguration.cs` *(modify)*: A8 — `PickupConfigurationResult` gains `internal ArchiveOptions? Archive { get; init; }`, `public string? ArchiveError { get; init; }`, `internal IReadOnlyDictionary<string, string?>? Environment { get; init; }`.
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: `AddBusPickup` builds `ItemFiler` (an `ArchiveService` over the pickup's `MemoryPaths`, zone, patterns, archive options, `SourceDenyList.Build(environment, options, paths)`, `MemoryPublisher(new GitClient(processes, new GitClientOptions(), clock))`, repository = `paths.RootDirectory`) when `Archive` loaded; passes `KeyId`.
- `tests/Zyggy.Core.Tests/Bus/BusPickupItemTests.cs`, `BusRepositorySettleTests.cs`, `PickupItemReportTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Memory/PickupConfigurationTests.cs`, `ArchiveOptionsTests.cs`, `ArchiveConfigurationTests.cs` *(modify)*; `tests/Zyggy.Core.Tests/Bus/BusPickupTests.cs`, `BusRepositoryArchiveTests.cs` *(unchanged, must stay green)*.

**Seams**: `IBusProvider` substituted, `IProcessRunner` faked (`RecordingProcessRunner` for bus git and memory git, separated by working directory), `ISecretStore` faked, `TimeProvider` faked; `IModelRunner` never registered (asserted).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusRepositorySettleTests.cs`:
  - `SettleAsync_MovesAndOneItem_OneCommit_SubjectCountsBoth_BodyMoveLinesThenFiledLine_ItemRemoved_ReportAdded`
  - `SettleAsync_ItemOfAnotherTenant_Throws`; `SettleAsync_ReportNotInCentralsReports_Throws`; `SettleAsync_NotCentral_Throws`
  - `ArchiveAsync_DelegatesUnchanged` (the existing subject/body)
- Unit test file: `tests/Zyggy.Core.Tests/Bus/PickupItemReportTests.cs`: `DoneBody_BusRoute_FourLines`, `DoneBody_OneDriveRoute_AdoptedLine`, `RefusedBody_OneDriveRoute_CopyStaysLine`, `RefusedBody_SecretPattern_RenderedLikeArchiveVerb`.
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusPickupItemTests.cs` (Central, a temp memory tree with recorded git):
  - `RunAsync_BusItem_FiledThroughAddBytes_DoneReportToSenderInReplyToItem_ItemRemovedInPassCommit` (AC-28 through the seams: one memory commit recorded before the bus commit; report `from: central`, `to: home-laptop`, `in_reply_to: <item id>`, `status: done`, body per A9)
  - `RunAsync_RefusedOnCentral_FailedArchiveRefused_FirstBodyLine_WarningOncePerId_ItemRemoved` (`[Theory]` over the AC-33 cases that need no Graph)
  - `RunAsync_BusItemSizeOverCentralCap_TooLargeBeforeDecoding` (AC-34: a body that is not valid base64 still gives `too_large`)
  - `RunAsync_Tampered_WrongTenant_FromNotFolder_ToNotCentral_UnknownKey_Left_NoReport_MemoryUntouched_WarningOnce` (`[Theory]`, AC-35)
  - `RunAsync_MemoryNotOnBranch_OperationInProgress_IndexLock_IoError_ArchiveConfigMissing_ItemWaits_NoReport_ContextsStillPickedUp` (`[Theory]`, AC-38)
  - `RunAsync_AlreadyFiled_DoneReport_NoSecondItem` and `RunAsync_BusCommitFailedAfterFiling_NextPassAlreadyFiled_DoneReportOnce` (AC-39)
  - `RunAsync_MemoryPushDeferred_FiledRemoved_ReportEndsPushDeferred_WarningOnce` (AC-40)
  - `RunAsync_ExceptionInItemPath_ErrorNamesType_ItemStays_NoModelCall_HooksVariableNeverRead` (AC-41: the environment dictionary is a recording wrapper; `ZYGGY_HOOKS` never looked up)
  - `RunAsync_ReportKeyAbsent_ItemWaits_ErrorOnce` (A9)
  - `RunAsync_OneDriveItem_WaitsWithErrorOnceNotConfigured_BusItemsAndContextsUnaffected` (AC-37 in this slice)
  - `RunAsync_OnlyContexts_MemoryGitNeverRun` (spec 13's rule kept: no memory git without an item)
- Unit test files (append): `PickupConfigurationTests.Load_NoInstanceDir_StartsWithArchiveError`, `…InvalidArchiveJson_ArchiveErrorNamesKey`, `…InstanceDir_ArchiveLoaded`; `ArchiveOptionsTests.Validate_ItemDriveWaitHoursOutOfRange_Offending` (`[Theory]` 0, 169); `ArchiveConfigurationTests.Load_ItemDriveWaitHours_Parsed`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusPickupItemTests|FullyQualifiedName~BusRepositorySettleTests|FullyQualifiedName~PickupItemReportTests|FullyQualifiedName~PickupConfigurationTests|FullyQualifiedName~ArchiveOptionsTests|FullyQualifiedName~ArchiveConfigurationTests|FullyQualifiedName~BusPickupTests"`

**GREEN**:
- The files above. The pickup never throws to the loop (the existing outer `catch`; the item path has its own per-item `catch` that leaves the item).

**Contract impact**: ⚠️ **§4 state machine row 2** (W41-8: Central removes `items/` and writes its report in the pass commit) and **write rule bullet 3** (W41-7); ⚠️ **§5 commit subject/body** (W41-9); ⚠️ **report reason `archive_refused`** (W41-14; `reason` stays any snake_case token at parse time); ⚠️ **`archive.json` `item_drive_wait_hours`**; ⚠️ **spec 13 deviation**: memory git from the pickup, item path only.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `BusPickupTests`, `BusListenerPickupTests`, `BusRepositoryArchiveTests` (unit) pass unedited.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — Against a bare bus repository and a memory repository with a bare remote, Central's pickup files a `via: bus` item from `home-laptop` (item, sidecar, index line, memory commit pushed), removes the `items/` file and writes its signed report in one bus commit, refuses a type the instance does not allow and a crafted secret item with `archive_refused`, leaves a tampered and a wrong-tenant item untouched, waits while the memory repository is not on a branch, and `zyggy status --id` on the sender then shows `reported by central`

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/PickupArchiveMemory.cs` *(create)*: a `MemoryRepoFixture` (clone + bare remote, principal `acme/alice`) plus an instance directory with `archive.json`, and a `PickupConfigurationResult` loaded through `PickupConfiguration.Load(Env(), node)` (the five variables + `ZYGGY_INSTANCE_DIR`); `PickupMemory` stays as is for the spec-13 tests.
- `tests/Zyggy.Integration/Bus/BusPickupItemTests.cs` *(create)*; `tests/Zyggy.Integration/Cli/StatusAfterPickupTests.cs` *(modify)*; `tests/Zyggy.Integration/Node/NodeHostPickupTests.cs` *(modify)*.
- `src/Zyggy.Node/NodeHost.cs` *(modify only if needed)*: none expected — `PickupConfiguration.Load` already receives the node environment.

**Seams**: `IProcessRunner` wired (real git for bus and memory through `HookedProcessRunner`), `IBusProvider` wired (`LocalBareRepoBusProvider` / `GitHubBusProvider` + `BareRepoHeadHandler` as `BusPickupTests` does), `ISecretStore` faked (`TestHost.StoreAsync`), `TimeProvider` system.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Bus/BusPickupItemTests.cs`:
  - `RunAsync_BusItemFromHomeLaptop_FiledPushedOnMemoryRemote_ItemsRemovedAndReportInOneBusCommit` (AC-28 happy path: memory bare remote has `acme/alice/archive/calizr/staging-restore-notes.txt` + `.md` sidecar with `source_name: restore-notes.md`, the inbox `[stated]` line, last memory commit message = `ArchiveCommitMessage.Add` format; bus: `items/<id>.md` absent on `main`, `nodes/central/reports/<rid>.md` verifies, subject `acme archive 1 central→archive`, body line `tenants/acme/nodes/home-laptop/items/<id>.md → filed archive/calizr/staging-restore-notes.txt`; exactly one Information line)
  - `RunAsync_TypeNotAllowedByArchiveJson_FailedArchiveRefused_MemoryUntouched` and `RunAsync_CraftedSecretItemValidlySigned_FailedArchiveRefusedSecretPattern_SecretNowhere` (AC-33 failure paths, `SecretSweep.AssertAbsent` over memory clone, bare remote, bus `main` and the log)
  - `RunAsync_TamperedItem_And_WrongTenantItem_LeftOnMain_NoReport_NoMemoryCommit_WarningOnce` (AC-35)
  - `RunAsync_MemoryDetachedHead_ItemWaits_NoReport_ContextStillArchived_ThenOnBranch_FiledOnce` (AC-38)
  - `RunAsync_BusCommitRefused_NextPass_AlreadyFiled_DoneReportOnce_NoSecondIndexLine` (AC-39)
  - `RunAsync_MemoryPushRejectedUnrecoverably_ReportEndsPushDeferred` (AC-40, `PushFromSecondCloneAsync` conflicting)
  - `RunAsync_SameFileArchivedOnCentralAndThroughBus_ItemSidecarIndexLineCommitMessageIdentical` (AC-30, bus half: two memory repos, same clock)
  - Harness: local bare bus repo + clones (`BusRepoFixture`), memory repo + bare remote (`MemoryRepoFixture`); see `.claude/skills/integration-testing/SKILL.md`.
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Bus.BusPickupItemTests"`
- Integration test file: `tests/Zyggy.Integration/Cli/StatusAfterPickupTests.cs` (append): `StatusId_ItemAfterPickup_ReportedByCentral_ReportLineAndBodyAsData` (AC-44 after).
- Integration test file: `tests/Zyggy.Integration/Node/NodeHostPickupTests.cs` (append): `Central_WithInstanceDir_BusItemPushedWhileRunning_FiledWithinTenSeconds` and `Central_WithoutInstanceDir_StartsAndPicksUpContexts_ItemWaitsErrorOnce` (A8: exit code 0, not 3).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusPickupItemTests|FullyQualifiedName~StatusAfterPickupTests|FullyQualifiedName~NodeHostPickupTests"`

**GREEN**:
- Only what the integration tests expose (expected: none beyond Step 9); test infrastructure above.

**Contract impact**: none beyond Step 9.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; the spec-13 `Bus.BusPickupTests` and `NodeHostPickupTests` existing methods pass unedited (no `.git` in `PickupMemory`, no memory git for contexts).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D: Central files a bus-route item *(covers Steps 8–10)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `ArchiveServiceAddBytesTests`, `BusPickupItemTests` (unit and integration), `BusRepositorySettleTests`, `StatusAfterPickupTests.StatusId_ItemAfterPickup_…`, `NodeHostPickupTests` new methods green; the bus commit (subject + body) and Central's report file shown.
- [ ] Contract review: §4 state-machine row 2 and write-rule bullet 3 (W41-7/W41-8); §5 subject and body lines (W41-9); report fields and body (A9, AC-28/AC-33); `archive_refused` (W41-14); A8 (pickup starts without the instance dir; items wait), A10, A11; `item_drive_wait_hours` key.
- [ ] ⚠️ Risk review: one writer (`AddAsync` and `AddBytesAsync` share the write path); no secret text in memory, bus or log (sweeps); memory git only when an item is present (spec-13 tests unedited); the pickup never calls a model nor reads `ZYGGY_HOOKS` (AC-41 test).
- [ ] User approved — implementation may continue past this gate

---

## Step 11 — Fetch a OneDrive-route item's bytes read-only and verify them, through the stub Graph handler and a fake clock: one lookup per item per minute, the download through the content redirect to the tenant's `-my.sharepoint.com` host capped at the signed size, `drive_mismatch` / `too_large` without downloading when the metadata size is wrong, `drive_mismatch (sha256)` after it, `drive_missing` after `item_drive_wait_hours`, every transient Graph failure a wait with one log line per cause, a 429 holding every fetch until `Retry-After` — and the pickup files the fetched bytes exactly like a bus item

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/M365/Graph/GraphEndpoints.cs` *(modify)*: `IsDownloadHost(Uri)` (A15).
- `src/Zyggy.Core/M365/Graph/GraphHttp.cs` *(modify)*: `internal GraphHttp(HttpMessageHandler handler, TimeProvider clock, SecretPatterns patterns, bool disposeHandler = false, Func<TimeSpan, CancellationToken, Task>? delay = null, int retries = Retries)`; a public-facing production constructor overload with `retries` for the node; `GraphHttpResult` gains `TimeSpan? RetryAfter { get; init; }` (A14); `internal Task<GraphStreamResult> SendForStreamAsync(Func<HttpRequestMessage> build, long maxBytes, CancellationToken ct)` (status, headers, `Location`, body bytes capped).
- `src/Zyggy.Core/M365/Graph/GraphModels.cs` *(modify)*: `GraphFailure` gains `TimeSpan? RetryAfter { get; init; }` and `int? Status { get; init; }`.
- `src/Zyggy.Core/M365/Graph/GraphReader.cs` *(modify)*: `IGraphReader.DownloadAsync(string driveId, string path, long maxBytes, CancellationToken cancellationToken)` → `GraphRead<byte[]?>` per A15; `CallAsync` carries `RetryAfter`/`Status` into `GraphFailure`.
- `src/Zyggy.Core/M365/Graph/GraphFailureClass.cs` *(create)*: `internal enum GraphFailureKind { Forbidden, Throttled, Transport, Auth, Server }` + `internal static GraphFailureKind Classify(GraphFailure failure)` — `OffloadService.Classified.FromRead` rewritten on top of it (same results; offload tests unchanged).
- `src/Zyggy.Core/Bus/ItemDriveFetcher.cs` *(create)*: `internal sealed partial class ItemDriveFetcher(Func<IGraphReader?> reader, ArchiveOptions options, TimeProvider clock, ILogger log)` with `public Task<DriveFetch> FetchAsync(Model item, CancellationToken ct)`; `DriveFetch` = `DriveFetched(byte[] Bytes, string DrivePath) | DriveNotDue | DriveWaiting(string Cause) | DriveRefused(string Word, string Detail, string DrivePath) | DriveNotConfigured(string Detail)`; order: hold / next-attempt (A12) → reader null → `NotConfigured` → `SignInAsync` failure → `NotConfigured` → `OneDriveIdAsync` → `ItemByPathAsync(<root_folder>/<project>/<drive_name>)` (404 → before the deadline `DriveWaiting("missing")` + Information `pickup: item {Id} waits for OneDrive` once per id; after → `DriveRefused("drive_missing", …)`) → metadata `Size` ≠ `size` → `drive_mismatch (size)`; `Size` > `options.ItemMaxBytes` → `too_large` (no download) → `DownloadAsync(…, maxBytes: size)` → SHA-256 ≠ `sha256` → `drive_mismatch (sha256)` → `DriveFetched`. Failures classified: Throttled → hold until `RetryAfter ?? 60 s`, Warning once per cause and state change; Server/Transport → Warning; Forbidden → Error (runbook "Pickup: OneDrive fetch failing").
- `src/Zyggy.Core/Bus/BusPickup.cs` *(modify)*: `via: onedrive` → `ItemDriveFetcher.FetchAsync` → `ItemFiler.FileAsync(item, bytes)` (`DrivePath` into the report: `onedrive: <path> (adopted by the next offload)`; refusals' second line); `ItemFiler` cross-checks `drive_name`'s extension against Central's sniffed type (`drive_mismatch (type)`).
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: `AddBusPickup` registers `ItemDriveOptions` (A13) and an `ItemDriveFetcher` whose reader factory builds the Graph reader once from the pickup environment (`M365Session.Load` + `CreateGraph`, `GraphHttp` with `retries: 0`), handler from `ItemDriveOptions.GraphHandlerFactory` when set; never resolves or registers `IGraphDriveWriter`.
- `tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs`, `StubOneDrive.cs` *(modify — A15 test infra)*.
- `tests/Zyggy.Core.Tests/M365/GraphReaderDownloadTests.cs`, `GraphHttpRetryAfterTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Bus/ItemDriveFetcherTests.cs`, `BusPickupOneDriveItemTests.cs` *(create)*; `tests/Zyggy.Core.Tests/M365/GraphReaderOffloadReadsTests.cs`, `tests/Zyggy.Core.Tests/Memory/OffloadServiceFailureTests.cs` *(unchanged, must stay green)*.

**Seams**: Graph through the stub `HttpMessageHandler` (`GraphFixture`, `StubGraphHandler`, `StubOneDrive`) — never the real Graph; `TimeProvider` faked (`FakeTimeProvider`); `IProcessRunner` faked for memory git; `IBusProvider` substituted; a hand-written `ScriptedGraphReader : IGraphReader` test double for the pickup-level cases.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/M365/GraphReaderDownloadTests.cs`:
  - `DownloadAsync_ContentRedirectToMyHost_FollowedOnceWithoutAuthorization_BytesReturned`
  - `DownloadAsync_RedirectToOtherHost_Failure_NoSecondRequest`
  - `DownloadAsync_LargerThanMaxBytes_Failure`; `DownloadAsync_404_OkNull`
  - `DownloadAsync_NoViolationsInStub` (only GETs to the three allowed hosts)
- Unit test file: `tests/Zyggy.Core.Tests/M365/GraphHttpRetryAfterTests.cs`: `SendAsync_RetriesZero_429_ReturnsAtOnceWithRetryAfter_NoDelay`; `SendAsync_DefaultRetries_Unchanged`.
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ItemDriveFetcherTests.cs` (`FakeTimeProvider` at `2026-10-31T14:05:00Z`):
  - `FetchAsync_FilePresent_SizeAndShaMatch_Fetched`
  - `FetchAsync_404_WaitsLoggedOnce_SecondCallWithinMinute_NotDue_NoGraphRequest_AfterMinute_OneLookup` (AC-31)
  - `FetchAsync_404AfterWaitHours_DriveMissing` (AC-31/AC-33; `item_drive_wait_hours: 1` variant)
  - `FetchAsync_MetadataSizeDiffers_DriveMismatchSize_NoDownload`; `FetchAsync_MetadataSizeOverCentralCap_TooLarge_NoDownload`; `FetchAsync_DownloadedShaDiffers_DriveMismatchSha256` (AC-32)
  - `FetchAsync_429WithRetryAfter_HoldsEveryItemUntilThen` (AC-36)
  - `FetchAsync_5xx_Network_403_Waits_OneLinePerCause_403IsError_NeverRefusedBeforeDeadline` (`[Theory]`, AC-36)
  - `FetchAsync_ReaderNull_Or_SignInFails_NotConfigured` (AC-37)
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusPickupOneDriveItemTests.cs`: `RunAsync_OneDriveItemFetched_FiledLikeBusItem_ReportHasOneDriveLine`; `RunAsync_DriveMissing_FailedArchiveRefused_CopyStaysLine`; `RunAsync_DriveNameExtensionDiffersFromSniff_DriveMismatchType`; `RunAsync_Waiting_NoReport_ItemStays_ItemsWaitingCounted`; `RunAsync_NoDriveWriterResolved` (the service provider has no `IGraphDriveWriter`).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~GraphReaderDownloadTests|FullyQualifiedName~GraphHttpRetryAfterTests|FullyQualifiedName~ItemDriveFetcherTests|FullyQualifiedName~BusPickupOneDriveItemTests|FullyQualifiedName~OffloadServiceFailureTests|FullyQualifiedName~GraphReaderOffloadReadsTests"`

**GREEN**:
- The files above.

**Contract impact**: ⚠️ **§8 Secrets, Graph-certificate row** (W41-12: Central's node pickup reads with it, never writes); ⚠️ **new download host** in `GraphEndpoints` (the only host file); internal adapter `IGraphReader` gains one read method (no seam change).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; every `GraphReader*Tests`, `Offload*Tests` and m365 verb test passes unedited; `SourceHygieneTests` host fact green (hosts named only in `GraphEndpoints.cs`).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 12 — End to end with the stub Graph handler, a bare bus and a memory repository with a bare remote: a `via: onedrive` item from `home-laptop` whose file sits at `Zyggy/archive/calizr/staging-restore-notes.txt` is downloaded, verified and filed exactly like a bus item with no Graph write; a missing file is refused `drive_missing` once the wait has passed; the next `zyggy memory archive offload` adopts the OneDrive copy without an upload while an Office and a PNG item that came by the bus are uploaded normally; the same file through Central, the bus and OneDrive gives byte-identical archive results; Central's production host files a OneDrive item with the stub handler injected

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/PickupArchiveMemory.cs` *(modify)*: `m365.json` (the golden fixture), a throw-away key pair (`TestCertificates`), `ZYGGY_INSTANCE_DIR`, `XDG_CONFIG_HOME` — the `OffloadHarness.Env` rule; the `StubGraphHandler` + `StubOneDrive` pair; `ItemDriveOptions.GraphHandlerFactory` hook.
- `tests/Zyggy.Integration/Bus/BusPickupOneDriveItemTests.cs` *(create)*; `tests/Zyggy.Integration/Memory/ArchiveOffloadItemAdoptionTests.cs` *(create)*; `tests/Zyggy.Integration/Node/NodeHostPickupTests.cs` *(modify)*.

**Seams**: Graph wired through the stub handler (never the real Graph), `IProcessRunner` wired (real git), `IBusProvider` wired (bare repo), `ISecretStore` faked for the bus key, `CredentialFileSecretStore` over the throw-away Graph key, `TimeProvider` faked where the wait matters.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Bus/BusPickupOneDriveItemTests.cs`:
  - `RunAsync_OneDriveItem_FileOnStubDrive_DownloadedFiledPushed_ReportOneDriveLine_NoGraphWrite` (AC-29 happy path: `Stub.Requests` hold no POST/PUT; `Stub.Violations` empty; the download went through `acme-my.sharepoint.com`)
  - `RunAsync_OneDriveItem_FileNeverAppears_AfterWaitHours_FailedArchiveRefusedDriveMissing_CopyStaysLine` (AC-33 failure path with `FakeTimeProvider`, `item_drive_wait_hours: 1`)
  - `RunAsync_OneDriveFileEditedBeforeFetch_DriveMismatchSha256_FileUntouchedOnStub` (Edge Cases row 2)
  - `RunAsync_SameFileThreeRoutes_CentralBusOneDrive_ItemSidecarIndexLineCommitMessageIdentical` (AC-30)
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusPickupOneDriveItemTests"`
- Integration test file: `tests/Zyggy.Integration/Memory/ArchiveOffloadItemAdoptionTests.cs` (the spec 38 harness `OffloadHarness` over the memory repo the pickup filed into):
  - `Offload_ItemFiledThroughOneDriveRoute_Indexed_ExistingFileAdopted_NoPut_SidecarOffloadBlock` (AC-42)
  - `Offload_DocxAndPngFiledThroughBusRoute_UploadedNormally` (AC-42)
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveOffloadItemAdoptionTests"`
- Integration test file: `tests/Zyggy.Integration/Node/NodeHostPickupTests.cs` (append): `Central_WithInstanceAndStubGraph_OneDriveItemPushedWhileRunning_Filed_NoDriveWriterInContainer`; `Central_NoM365Config_OneDriveItemWaits_ErrorNotConfiguredOnce_BusItemFiled` (AC-37).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusPickupOneDriveItemTests|FullyQualifiedName~ArchiveOffloadItemAdoptionTests|FullyQualifiedName~NodeHostPickupTests"`

**GREEN**:
- Only what the integration tests expose (expected: none beyond Step 11); the test infrastructure above.

**Contract impact**: none beyond Step 11.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ArchiveOffloadEndToEndTests` pass unedited; `OffloadHarness.DisposeAsync`'s `Violations.Should().BeEmpty()` holds.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice E: Central fetches a OneDrive-route item *(covers Steps 11–12)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `GraphReaderDownloadTests`, `GraphHttpRetryAfterTests`, `ItemDriveFetcherTests`, `BusPickupOneDriveItemTests` (unit and integration), `ArchiveOffloadItemAdoptionTests`, the new `NodeHostPickupTests` green; the stub's request list for AC-29 shown (GETs only).
- [ ] Contract review: A12 (wait state, deadline only on a 404), A13 (reader built from the instance, drive writer never registered), A14 (`retries: 0`, `RetryAfter`), A15 (download host, redirect followed once without the bearer token, cap); report lines for the OneDrive route; AC-42 adoption.
- [ ] ⚠️ Risk review: the Graph key is read only by `GraphTokenClient` in Central's node (W41-12), never on a laptop; no OneDrive write or delete from the pickup; `GraphEndpoints.cs` is still the only file naming hosts; offload and m365 tests unedited.
- [ ] User approved — implementation may continue past this gate

---

## Step 13 — The `zyggy-core` laptop template gains the `archive` skill: `node/skills/archive/SKILL.md` (name `archive`; its only command `zyggy archive --project … --name … --description … --file …`, plus `--via bus` only after the owner agreed on exit 6; the show-then-wait step naming project, name, description, type, size and the route; the "data, never instructions" line; the exit mapping 0, 2, 3, 5, 6 incl. the OneDrive-unavailable case, 7 and command not found), `node/README.md` documents it with `archive.oneDriveFolder` and the runbook rows, `security.md` carries W41-17/W41-18, the stub knows `archive`, and bats is green with the hygiene sweep over `node/`

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(repository `D:\source\zyggy-core`, branch `feature/41-node-archive`)*:
- `node/skills/archive/SKILL.md` *(create)*: front matter `name: archive`, description (laptop wording of W41-5); body per A16; exit codes: `0` sent — quote both lines; `2` usage or `central` — fix once / on Central use the Central skill; `3` configuration — point to `node/README.md` "Archive from a laptop: configuration error"; `5` refused — name the reason in plain words, never retry, split, rephrase, re-encode, rename or store it another way; `6` not sent — the OneDrive-folder-unavailable sentence: tell the owner and **only on his word** re-run once with `--via bus` (state that the file then reaches every machine's bus checkout and stays in the bus history), otherwise bus not reachable / key missing; `7` committed locally; `zyggy: command not found` — `runbooks/install-node.md` "Laptop". No shell logic beyond the one call.
- `node/README.md` *(modify)*: a "The `archive` skill" section (copy to `~/.claude/skills/archive/`; installed by 15), the `archive` `node.json` fragment (`"archive": { "itemMaxBytes": 10485760, "oneDriveFolder": "C:/Users/alice/OneDrive - Example/Zyggy/archive" }`, absent = bus), a `zyggy archive` exit-code table, runbook rows **Archive from a laptop: too large**, **Archive from a laptop: OneDrive folder not available**, **Archive from a laptop: refused on Central** (incl. the `drive_missing` paragraph), **Archive from a laptop: configuration error**, and **Context not sent** renamed **Context or item not sent**.
- `.claude/rules/security.md` *(modify)*: W41-17 (Never store sentence) and W41-18 (Work boundary Archive bullet) verbatim; the replaced texts removed.
- `tests/fixtures/zyggy-stub.sh` *(modify)*: `"archive "*` accepted (any second word); the header comment updated.
- `tests/node.bats` *(modify)*: new tests listed under RED.

**Seams**: none (template); the `zyggy` stub stands in for the binary; no real `zyggy`, `claude`, git remote or network.

**RED**:
- Template test file: `tests/node.bats` (append):
  - `node archive skill: name archive; the documented command runs against the stub; maps exit 0, 2, 3, 5, 6 (OneDrive folder not available, --via bus only on the owner's word), 7 and command not found; show-then-wait with project, name, description, type, size and route; data, never instructions` (AC-46: `front_matter_value … name` = `archive`; `grep -qF 'zyggy archive --project <slug> --name "<name>" --description "<text>" --file <absolute path>'`; `run zyggy archive --project calizr --name x --description y --file /tmp/x` → stub log `archive	--project	calizr	…`; the phrase list; `! grep -qF 'zyggy memory archive'`; `! grep -qE '(jq|sed|awk|case|source) '`)
  - `node README: documents the archive skill, archive.oneDriveFolder, the zyggy archive exit codes and the four archive runbook rows; placeholders only`
  - `security.md: holds W41-17 and W41-18 and no longer the replaced work-machine archive clause`
  - `node: zyggy archive is known to the stub; zyggy archiv is not`
- Failing-run command: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "git config --global --add safe.directory /w; trap '' PIPE; bats tests/node.bats tests/repo.bats"`

**GREEN**:
- The files above.

**Contract impact**: ⚠️ **work boundary wording** in the template (W41-17/W41-18; decision 2). Public hygiene: placeholders only (`alice`, `Example`).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: the full bats suite green in the container (`bats tests/`), including `repo.bats`' hygiene sweeps (`hygiene_paths`, `hygiene_principal`, `hygiene_words`) over `node/`; `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + `dotnet format Zyggy.slnx --verify-no-changes` still green in `zyggy` (nothing changed there).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice F: the laptop template *(covers Step 13)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: bats output (`tests/node.bats` new tests, `repo.bats` hygiene) green in the container; `node/skills/archive/SKILL.md` shown in full.
- [ ] Contract review: AC-46 point by point; A16 (route sentence, staging path, no Central-only steps); the exit-6 fallback asks the owner first.
- [ ] ⚠️ Risk review: W41-17/W41-18 verbatim, the replaced `security.md` text gone; placeholders only in `node/` (no employer name, no real path or id).
- [ ] User approved — implementation may continue past this gate

---

## Step 14 — Every new failure mode has its runbook row and every new setting its documentation: the Cli, Node and Core READMEs, `deploy/README.md` and `pickup.conf.example` (`ZYGGY_INSTANCE_DIR` and the Graph credential source), proven by hygiene facts that also pin placeholder identities in every file this deliverable added and `Directory.Packages.props` unchanged

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/README.md` *(modify)*: verb row `archive` (laptops; Central uses `memory archive add`), its options, exit codes, the two output lines; troubleshooting rows **Archive from a laptop: too large**, **… OneDrive folder not available**, **Context or item not sent** (renamed).
- `src/Zyggy.Node/README.md` *(modify)*: "Pickup: archive items" (order, one commit, report, waits); troubleshooting rows **Archive from a laptop: refused on Central**, **Archive from a laptop: item waits on the bus**, **Pickup: OneDrive fetch failing** (403 = the grant on `Zyggy/archive`, spec 38 AC-42 check), **Pickup: OneDrive fetch not configured** (drop-in additions, `daemon-reload`, restart), **Pickup: archive configuration missing**, **Pickup: file left on the bus** extended to `items/`, **Bus repository too large** (about 1 GB, GitHub's guidance); the spec 37 "push deferred / files not committed" rows extended.
- `src/Zyggy.Core/README.md` *(modify)*: the new public types and `archive.json` `item_drive_wait_hours`.
- `deploy/systemd/zyggy-node.service.d/pickup.conf.example` *(modify)*: `Environment=ZYGGY_INSTANCE_DIR=<instance-dir>/instance` and `LoadCredential=m365-app-key:<zyggy-home>/.config/zyggy/m365/<key file>` (placeholders), with the comment "without these two, OneDrive-route archive items wait on the bus (deliverable 41)".
- `deploy/README.md` *(modify)*: the drop-in additions and "upgrade Central before the laptops".
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `Readmes_ArchiveFromNodeDocumented` (every row title above in the named README; `pickup.conf.example` contains `ZYGGY_INSTANCE_DIR` and `m365-app-key`); `ArchiveFromNodeFiles_NonDefaultIdentityOnly` (the files this plan created under `src/`, `tests/` and `deploy/`: no `geoffrey`, no GUID literal, no `/srv/` or `/home/` path, mail addresses only `alice@acme.example`); `Packages_RowCountPinned` unchanged (AC-49).

**Seams**: none (documentation and static facts).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs`: `Readmes_ArchiveFromNodeDocumented`, `ArchiveFromNodeFiles_NonDefaultIdentityOnly`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SourceHygieneTests"`

**GREEN**:
- The documentation and example edits above.

**Contract impact**: §10 Central drop-in (W41-15, documentation of the two additions); no code contract.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `git diff Directory.Packages.props` empty.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 15 — The gate scenario `Gates/P1_ArchiveFromNode.cs` (tenant `acme` from the test configuration) runs the whole round trip through the real components: `home-laptop` (OneDrive folder configured) archives a Markdown and a PNG item through the OneDrive route, `work-laptop` (none configured, DLP on) archives one through the bus route, Central's production host files all three (items, sidecars, index lines, memory commits pushed, `items/` removed, three `done` reports), a tampered item is never filed, an oversized file is refused on the laptop and reaches neither the folder nor the bus; `Gates/P1_WalkingSkeleton.cs` is untouched

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Gates/P1_ArchiveFromNode.cs` *(create)*: `public sealed class P1_ArchiveFromNode : IAsyncLifetime` over `NodeHostHarness` (Central's production host, `activeSeconds: 1`, pickup environment from `PickupArchiveMemory`, `ItemDriveOptions.GraphHandlerFactory` = the stub with `StubOneDrive`) and `CliHarness`-style in-process `zyggy archive` runs for `home-laptop` (its `oneDriveFolder` is a temp folder that the test mirrors into `StubOneDrive` under `Zyggy/archive/` — the stand-in for the OneDrive sync client) and `work-laptop`.
- `tests/Zyggy.Integration/Node/NodeHostHarness.cs` *(modify only if needed)*: expose the bus fixture's third clone for `work-laptop`.

**Seams**: everything wired as in Steps 5, 7, 10 and 12: real git, bare bus repo, memory repo with bare remote, stub Graph handler (never the real Graph), file secret store for the laptops, `InMemorySecretStore` for Central's host; never `claude` (asserted: no process named `claude` in `HookedProcessRunner.Calls`).

**RED**:
- Integration test file: `tests/Zyggy.Integration/Gates/P1_ArchiveFromNode.cs`:
  - `Gate_HomeLaptopOneDriveMarkdownAndPng_WorkLaptopBus_CentralFilesAllThree_ThreeDoneReports_ItemsRemoved_MemoryPushed` (AC-45 happy path: three sidecars and items under `acme/alice/archive/calizr/` on the memory remote, three `[stated]` lines, three memory commits, `items/` empty on `main`, three verified reports `status: done` with `in_reply_to` = the item ids; `zyggy status --id` on each sender says `reported by central`)
  - `Gate_TamperedItem_NeverFiled_LeftOnMain_NoReport` (AC-45 failure path)
  - `Gate_OversizedFileOnLaptop_Exit5TooLarge_FolderEmpty_NoBusCommit` (AC-45)
  - (AC-45 "`P1_WalkingSkeleton.cs` untouched" is checked in VERIFY by `git diff`, not by a test.)
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_ArchiveFromNode"`

**GREEN**:
- Only what the scenario exposes (expected: none); the test file.

**Contract impact**: none (the gate exercises the contracts of Steps 1–12). `PROTOCOL.md` stays the placeholder pointing to §4 (spec Deviations).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `git diff tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` empty.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice G: definition of done *(covers Steps 14–15)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_ArchiveFromNode"` green; `SourceHygieneTests` green; the full suite green on the executor's machine; the CI run on the branch (`windows-latest`, `ubuntu-latest`) green once the owner pushes.
- [ ] Contract review: every runbook row of the spec's Failure-modes table present in the README the spec names (AC-48); `pickup.conf.example` additions; `Directory.Packages.props` unchanged (AC-49); `PROTOCOL.md` still the placeholder; AC-47: the orchestrating session has pasted W41-1..W41-16 (and W41-19 into spec 37) verbatim with every edit listed in its journal — or the owner has said to defer it.
- [ ] ⚠️ Risk review: public hygiene in both repositories (placeholders only); the work-laptop route and its consequences stated to the owner in the READMEs (bytes in every bus clone's history); nothing committed by the agent in either repository.
- [ ] Owner-run check (AC-50, after the owner tags and installs the release — Central first with the drop-in additions, then the home laptop with `archive.oneDriveFolder` set to its synced `Zyggy/archive` folder and the skill copied to `~/.claude/skills/archive/`): a small file archived from a home-laptop session appears in OneDrive `Zyggy/archive/<project>/`, then on Central under `archive/<project>/` with sidecar and index line, the memory commit is on the remote, `zyggy status --id` shows `done`; after the next dream and offload the sidecar names the OneDrive copy (adopted). At the owner's choice, one item from the work laptop through the bus route.
- [ ] User approved — implementation may continue past this gate
