# Plan: 38 — Archive offload to OneDrive — "Every item I archived for a project, once the nightly dream has filed its line, moves on its own to `Zyggy/archive/<project>/` on my <company> OneDrive, checked byte-identical there (size and OneDrive's content hash) before its bytes leave the memory repository in one commit; the sidecar and the project's line then point at the OneDrive copy; a failed or refused offload leaves the item in git and tells me so"

> **Scheduling note (owner instruction 2026-10-11).** This plan is written before 37's final gate; that changes only *when planning happens*. Building starts only after the owner approves this plan. Steps 1–11 (code, tests, template/instance text) need no live 37 item and may be built first, on a branch, **not merged into `main` and not released** while 37's final gate is open (spec "Shared contract freeze": `ArchiveSidecar`, `ArchiveService.List` and the `archive remove` output must not land while 37's gate could still change them). **Steps 12–14 depend on 37's final gate being checked** (`_plans/37-project-archive-memory.md`, "definition of done for deliverable 37"): they need the real items Step 12 of 37 archived and the nightly dream indexed, and 37's secret sweep as baseline. The 🛑 gate after Step 11 enforces that.

## Overview

After this deliverable `zyggy memory archive offload [--dry-run | --probe | --retry <project>/<slug>]` exists. Without a model, it selects archive items whose state is `indexed` (37's computed state), checks their bytes against the sidecar's SHA-256, creates the project folder under the owner-created `Zyggy/archive` folder of the owner's OneDrive if needed, uploads each item as a new file (`conflictBehavior=fail`), re-reads the drive item and compares parent, name, size and QuickXorHash, then writes the offload keys into the sidecar (golden), removes the item from `main` in one `archive offload …` commit per item pushed through 37's `MemoryPublisher`, and appends one `[observed]` inbox line the next dream files. Every write goes through a two-operation `GraphDriveWriter` behind an in-process `DriveWriteScope` and leaves one row in the 33 action log. Failures are closed reasons: transient ones are counted and retried next run, parked after `max_failures`; permanent ones are parked at once; the item never leaves git unless verified. `list` shows `offloaded` / `offload_failed(n)` / `parked`, `remove` notes that the OneDrive copy stays, the identity digest carries one `[warning]` line while anything needs attention. The template denies the verb to the model and tells the session how to retrieve an offloaded item; the instance gets the `offload` block, the oneshot unit started by `OnSuccess=` of the dream unit, and runbook entries; on Central the folder-scoped grant is verified before enabling, one item is moved attended, then unattended, then a forced failure.

Implements `_specs/38-archive-offload-onedrive.md` (Approved 2026-10-10, zero Open Questions; OQ-1..OQ-6 decided by the owner; its Decision Table, Contracts and AC-1..AC-42 are binding). Founding-spec sections: §1 (In scope, Non-goals D7 bullet, Constraints), §3 (Cli verbs, skills), §6 (Central's own jobs), §7 (Layout, Rules, Dream pass, Context loading), §8 (Secrets, Isolation, Injection, Data protection, Work boundary), §9 (design rules), §11, §12, §13 (Q5, Decisions), §14. W38-1..W38-9 are proposals for the owner; **this plan never edits `_specs/00 …`**, the spec or the roadmap.

**Reference patterns** (read across all layers; mirrored here):

- **The archive verb family (37)**: `src/Zyggy.Core/Memory/ArchiveVerb.cs` (sub-verb dispatch, `ZYGGY_HOOKS=off` rule, configuration order, `VerbIo`, exit constants, internal test ctor with `findTimeZone` and `IProcessRunner`), `ArchiveService` (`List`, `RemoveAsync`, `ArchiveFiles`), `ArchiveSidecar` (`ToMemoryFile`/`TryParse`), `ArchiveOptions`/`ArchiveConfiguration` (ceilings, never clamped, unknown key named), `ArchiveCommitMessage`, `ArchiveOutcome`, `ArchiveListFormat` (text + JSON), `MemoryPublisher` (preflight, `CommitAndPushAsync`, `RemoveAsync`, one-rebase push), dispatched by `src/Zyggy.Cli/RawVerbs.cs` (`memory archive …` already passes every sub-verb through). Tests: `tests/Zyggy.Core.Tests/Memory/Archive*Tests.cs`, `MemoryPublisherTests`, `RecordingProcessRunner`, `MemoryTree`, `VerbConsole`; `tests/Zyggy.Integration/Memory/Archive*Tests.cs` with `MemoryRepoFixture` (`ArchiveEnv`, `ArchiveAsync`, `TreeFingerprintAsync`, `ShowAsync`, `LastCommit*Async`, `PushFromSecondCloneAsync`, `ResetRemoteToAsync`, `MaterialiseScenario`); goldens `tests/golden/archive/*`.
- **The Graph adapter (33, W33-5)**: `src/Zyggy.Core/M365/Graph/GraphReader.cs` (`CallAsync`: bearer, one re-mint on 401, tolerated statuses, `GraphRead<T>`/`GraphFailure`), `GraphHttp` (HTTPS only, no redirect, no proxy, 60 s, 429/503 × 5, `Error`), `GraphTokenClient`, `GraphEndpoints` (the only host literals — `SourceHygieneTests.Src_ServiceLiteral_OnlyInItsAdapter`), `GraphModels`; `M365Session.Load`/`CreateGraph` and `M365VerbContext` (stub handler injection, `CheckKeyOwnership`); `CredentialFileSecretStore` (`$CREDENTIALS_DIRECTORY/m365-app-key` first). Tests: `tests/Zyggy.Core.Tests/M365/GraphFixture.cs`, `GraphReaderTests`, `tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs` (violation on any non-GET), `TestCertificates`; `tests/Zyggy.Integration/Infrastructure/M365InProcess.cs` + `M365InstanceFixture` ("the binary has no test switch, so Graph-dependent paths are proven in process").
- **Action log (33)**: `src/Zyggy.Core/M365/Guard/ActionLog.cs` (`BuildRow`, `Append` 0600/0700 under an exclusive lock), `M365Paths.StateDirectory`.
- **Lock, state and digest**: `src/Zyggy.Core/Dream/DreamLock.cs` (OS lock, no stale logic); `BriefPaths`/`M365Paths` (the one builder of a state path, `ZYGGY_STATE_DIR` else `~/.local/state/zyggy`); `DigestBuilder` (the existing identity `[warning]` line) and `src/Zyggy.Cli/Commands/MemoryDigestCommand.cs`.
- **Inbox line**: `RememberService` (`Tag`, `Hint`, `Source`, `Fact` → `- [<tag>] <date><hint>[ [<source>]]: <fact>` in `inbox/remember-<date>.md`); `LinkedIn/PostFact.cs` (an `[observed]` line with a parenthesised source, filed by the dream as an observed inbox line — `BatchPlanner` classes it `ObservedInbox`).
- **Template / instance / Central steps**: `_plans/37-project-archive-memory.md` Steps 10–12 (bats rows with the text; release → install → pin → pull → one restart; 0002 evidence rows; runbook section 17 of `instance/runbooks/central-claude-config.md`); instance units `instance/systemd/zyggy-dream.service` (no credential, `~/.ssh/config` for git) and `zyggy-morning-brief.service` (`LoadCredential=` key and certificate, `ZYGGY_M365_CER_FILE=%d/m365-app-cer`, hardening lines).

**Edge-test convention.** Graph is an HTTP edge; this repository proves Graph code with the real `GraphHttp`/`HttpClient` over the stub `HttpMessageHandler` in `tests/Zyggy.Core.Tests` (plan 33/35 practice), and proves Graph-dependent verbs **in process** in `tests/Zyggy.Integration` (the binary has no test switch). So Slice A's adapter steps are unit tests over the stub handler; the end-to-end wire (real git, local bare remote, stub OneDrive, the verb in process) is Step 8; the built binary covers only the paths that end before Graph (configuration, usage, disabled, lock, `list`, `remove`, `digest`).

**Phase**: 38 serves P0b (new clause, gate wording per OQ-4) but does not close it (29 and 30 are not started), so there is no `Gates/P<n>_*.cs` slice. The last 🛑 HUMAN GATE is the definition-of-done check against `ROADMAP.md` #38 and AC-1..AC-42.

**What this plan deliberately is not** (spec Out of Scope / Defer / Rejected): no upload sessions, no item over the simple-upload limit, no change to 37's caps; no OneDrive delete, overwrite, rename, move, copy, share or permission call by the binary; no SharePoint drive, no mail, no other unattended write; no history rewrite (runbook only); no `restore` verb and no download in the binary; no re-verification of already offloaded items; no change to the model's `upload-file-content` exclusion or to the MCP guard; no Telegram alert; no `Microsoft.Graph` SDK and no new NuGet package; no `IModelRunner`, no new §9 seam; no change to `DreamChecks`, `MemorySnapshot` or the dream prompts (AC-26: unchanged code).

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — Two folder-scoped writes on OneDrive, nothing else | 1, 2 | resolve the OneDrive drive, the root folder and any item's metadata read-only; compute QuickXorHash; create a project folder and a new file **only** under the root, every other write refused before the request; one action-log row per write | 🛑 after Step 2 (⚠️ first write code in the Graph adapter, 33's "GET only" now true of the reader only) |
| B — Memory knows offloaded items | 3, 4 | sidecar with the offload keys (golden); failure/park state; `list` → `offloaded` / `offload_failed(n)` / `parked`; `remove` of an offloaded item with its note; `add` caps count bytes on disk; `archive.json` `offload` block; selection of exactly the eligible items within caps | 🛑 after Step 4 (⚠️ shared contracts: sidecar, `list`, `remove`) |
| C — One offload run, verify before commit | 5, 6, 7, 8 | `zyggy memory archive offload` moves items end to end (upload → verify → sidecar → one commit per item → push → inbox line), adopts an identical existing copy, resumes an interrupted commit, classifies every failure, parks, `--retry`, `--dry-run`, `--probe`, disabled, exit codes 0/3/4/5/6/7; against a local bare remote and a stub OneDrive | 🛑 after Step 8 (⚠️ data loss order, credentials) |
| D — The session sees it, the dream files it | 9, 10 | one `[warning]` line in the identity digest while an item needs attention; the dream files the `Moved …` line with unchanged code; the index digest unchanged in shape | 🛑 after Step 10 |
| E — Template, instance, runbook | 11 | the model is denied the verb and told how to retrieve an offloaded item; instance `offload` block (disabled), the offload unit, runbook entries; 0002 §38 opened | 🛑 after Step 11 — **also the dependency gate: 37's final gate must be checked before Step 12** |
| F — Central | 12, 13, 14 | released binary installed with the offload off; the folder grant verified and one item moved attended; the dream's `OnSuccess=` installed, one item moved unattended, retrieved from a session, a forced failure surfaced, sweep clean | 🛑 after Step 13 (⚠️ first live write to Microsoft 365) · 🛑 after Step 14 (definition of done) |

Every code step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally (Windows); Linux by CI on both runners at each gate. Steps 11–14 also need `zyggy-core` and `zyggy-geoffrey` CI (or bats in podman when the instance CI cannot start, as 37 Step 10).

<!--
Decomposition strategy: vertical slices. Fake = behaviour through substitutes (stub HttpMessageHandler for Graph,
RecordingProcessRunner for git, FakeTimeProvider), unit tests. Wire = real git against a local bare remote with the verb
in process over a stub OneDrive (Graph has no test switch in the binary), plus the built binary for the pre-Graph paths.
Gate placement: one per slice; Slice F has two (first live write; definition of done). The gate after Step 11 doubles as
the dependency gate on 37's final gate (owner instruction 2026-10-11). 38 does not close a §12 phase: no Gates/P<n> slice.
-->

---

## Shared rules for this plan

- **Principal and tenant in tests**: tenant `acme`, user `alice`, mailbox `alice@acme.example` (the `m365.json` golden). No tenant literal, address, GUID or `sharepoint.com` in `src/` (`SourceHygieneTests` stays green). Drive ids in fixtures follow the existing goldens (`b!onedrive0001`); item ids are made-up tokens (`01ROOTARCH`, `01PROJZYGGY`, `01ITEMQUOTE`).
- **Never real Graph, GitHub or `claude`.** Graph only through `StubGraphHandler` (+ the new `StubOneDrive` model); git only against `MemoryRepoFixture`'s bare remote; no test registers an `IModelRunner` for the offload.
- **Goldens are hand-derived, never produced by the code under test** (`tests/golden/README.md`). New folder `tests/golden/archive/offload/` with a README paragraph. Fixed inputs reuse 37's: the 66-byte text item `Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n` (`sidecar-text.md`) and the PNG of `sidecar-png.md`. Fixed dates: archived 2026-09-30, offloaded 2026-10-01 (`FakeTimeProvider` at `2026-10-01T03:30:00Z`, zone `Europe/Brussels` through the injected `findTimeZone`, as 37). Fixed drive values: drive `b!onedrive0001`, root `01ROOTARCH`, project folder `01PROJZYGGY`, item `01ITEMQUOTE`, `webUrl` `https://acme-my.example.invalid/personal/alice/Documents/Zyggy/archive/zyggy/quote-2026.txt` (in fixtures only; never in `src/`).
- **QuickXorHash expected values** come from an implementation independent of the code under test (see Assumption A10) and are recorded in `tests/golden/archive/offload/quickxor-vectors.tsv` (`<case>\t<length>\t<generator>\t<base64>`), with the generator formula for the large fixtures (`byte[i] = (byte)((i * 31 + 7) % 256)`) so no 25 MiB file is committed.
- **Exit codes of `offload`** (spec CLI surface): 0 moved / nothing to do / disabled / lock held · 3 configuration · 4 usage · 5 at least one item failed or parked · 6 run-level failure (no item half-done) · 7 committed, push deferred. Constants on `ArchiveVerb` beside 37's (`OffloadFailed = 5`; 0/3/4/6/7 reuse the existing constants). Precedence 6 > 7 > 5 > 0 (Assumption A6).
- **Stderr prefix** `offload: ` on every line of the sub-verb; stdout `offloaded: archive/<p>/<slug>.<ext> -> <drive_path>` then `commit: <sha> pushed|push deferred`. Detail strings never contain item content, a token, a URL query string or a `webUrl`.
- **Order is safety** (spec Behaviors): read + SHA-256 → (folder) → exists/adopt → upload → verify → sidecar → `git rm` + commit + push → inbox line → state. A unit test per step pins that no git call precedes a passed verification.
- **Windows and Linux**: everything compiles and unit-tests on both; file-mode facts (`0600`/`0700`) are `[Fact(SkipUnless = nameof(IsLinux))]`.

---

## Step 1 — The Graph adapter resolves the owner's OneDrive drive, the `Zyggy/archive` root folder and any drive item's id, parent, name, size, QuickXorHash and `webUrl` with GET requests only (404 → absent, never `/me`), and QuickXorHash of any bytes matches the independent vectors from 0 B to 25 MiB

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/M365/Graph/QuickXorHash.cs` *(create, internal sealed, `: System.Security.Cryptography.HashAlgorithm`)*: Microsoft's published algorithm (160-bit state, shift 11 per byte, the length XOR-ed little-endian into the last 64 bits), `HashSize = 160`; `static string Base64(ReadOnlySpan<byte> bytes)`. ~60 lines, no package (spec Decision Table).
- `src/Zyggy.Core/M365/Graph/DriveItemInfo.cs` *(create, internal sealed record)*: `Id`, `Name`, `ParentId`, `DriveId`, `Size`, `QuickXorHash` (nullable), `WebUrl` (nullable), `IsFolder`; `static DriveItemInfo? Parse(JsonElement)` (`parentReference.id`, `parentReference.driveId`, `file.hashes.quickXorHash`, `folder`/`root` presence).
- `src/Zyggy.Core/M365/Graph/GraphReader.cs` *(modify, additive)*: `IGraphReader` and `GraphReader` gain
  - `Task<GraphRead<string>> OneDriveIdAsync(CancellationToken)` → `GET {Mailbox}/drive?$select=id` (spec `OneDriveAsync`, id only);
  - `Task<GraphRead<DriveItemInfo?>> ItemByPathAsync(string driveId, string path, CancellationToken)` → `GET /drives/{d}/root:/{each segment Uri.EscapeDataString}?$select=id,name,size,file,folder,root,parentReference,webUrl`, 404 tolerated → `null`;
  - `Task<GraphRead<DriveItemInfo?>> ItemByIdAsync(string driveId, string itemId, CancellationToken)` → `GET /drives/{d}/items/{id}?$select=…`, 404 → `null`;
  - `Task<GraphRead<DriveItemInfo?>> ChildAsync(string driveId, string parentId, string name, CancellationToken)` → `GET /drives/{d}/items/{parent}:/{escaped name}?$select=…`, 404 → `null` (the AC-9 existence read; same URL shape as `ItemExistsAsync`).
  - Every existing member, URL and message unchanged. `M365Grammar.Id()` checks returned ids as `MailFoldersAsync` does (an unexpected id → `GraphFailure(6, …)`).
- `tests/golden/archive/offload/quickxor-vectors.tsv`, `tests/golden/archive/offload/README.md` *(create)*; `tests/golden/m365/graph/offload/*.json` *(create: `drive-id.json`, `root-folder.json`, `root-is-file.json`, `project-folder.json`, `item-quote.json` with the fixed values of Shared rules)*.
- `tests/Zyggy.Core.Tests/M365/QuickXorHashTests.cs`, `GraphReaderOffloadReadsTests.cs` *(create)*.

**Seams**: none of the five; the Graph HTTP edge through `GraphHttp` over `StubGraphHandler` (unchanged violation rules: any non-GET is a violation); `TimeProvider`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~QuickXorHashTests|FullyQualifiedName~GraphReaderOffloadReadsTests|FullyQualifiedName~GraphReaderTests|FullyQualifiedName~SourceHygieneTests"`):
- `QuickXorHashTests` (AC-12): `[Theory] Base64_Vectors_MatchIndependentReference` over every row of `quickxor-vectors.tsv` — the published vectors of the independent implementation (A10) and the fixtures 0 B (`AAAAAAAAAAAAAAAAAAAAAAAAAAA=`), 1 B, 20 B, 160 B (one block), 161 B, 1 MiB, 25 MiB (generated by the formula); `Base64_FixedTextItem_EqualsGolden` (the 66-byte item); `HashAlgorithm_IncrementalChunks_EqualsOneShot` (chunks of 1, 7, 160, 4096 bytes); `HashSize_Is160`.
- `GraphReaderOffloadReadsTests` (AC-6 resolution, AC-7, AC-11 reads; `GraphFixture`, `Stub.Violations` empty in `Dispose`):
  - `OneDriveId_UsersMailboxDrive_SelectIdOnly` (URL `GET …/users/alice@acme.example/drive?$select=id`); `OneDriveId_403_FailureExitSixForbidden`.
  - `ItemByPath_RootFolder_ParsedFolder` (`root:/Zyggy/archive` with both segments escaped); `ItemByPath_404_Null`; `ItemByPath_File_IsFolderFalse`; `ItemByPath_SegmentWithSpace_Escaped`.
  - `ItemById_File_SizeQuickXorParentNameWebUrl`; `ItemById_404_Null`; `ItemById_UnexpectedId_FailureSix`.
  - `Child_Exists_Parsed`; `Child_404_Null`.
  - `AllRequests_GetOnly_NeverMe` (every recorded request is `GET` or the token `POST`).
- `GraphReaderTests`, `GraphReaderBriefReadsTests` unchanged and green; `SourceHygieneTests.Src_ServiceLiteral_OnlyInItsAdapter` unchanged and green.

**GREEN**: as Scope. Reads reuse `CallAsync` with a 404 tolerance; no new HTTP code path.

**Contract impact**: `IGraphReader` (internal adapter, W33-5) gains four GET reads — additive. 33 AC-15 ("GET only") still holds for `GraphReader`.

**VERIFY**: failing-run command passes; `git diff src/Zyggy.Core/M365/Graph/GraphReader.cs` shows additions only; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths` (and, from Step 4, `DriveArchivePath`), no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — The binary can create a folder named like a project directly under the root folder and a new `<slug>.<ext>` file directly inside such a folder on the resolved OneDrive drive, each with `conflictBehavior=fail` — and nothing else: every other write (other drive, the root itself, a grandchild folder, `..`/`/`/control characters in a name, another extension, the replace form, a missing conflict behaviour, a size over the simple-upload limit) is refused with `write_refused (<rule>)` before any request; 201/409/403/4xx-blocked-type/5xx/429/transport/401 are classified; each sent write yields one body-free action-log row

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/DriveWriteScope.cs` *(create, internal sealed; spec Contracts places it in `Zyggy.Core.Memory`)*: `DriveWriteScope(string driveId, string rootFolderId, long simpleUploadLimit = 250_000_000)`; `void KnowProjectFolder(string folderId)` (a folder read with parent = root, or just created); `string? Check(DriveWriteRequest request)` → `null` or the rule: `other_drive`, `folder_parent_not_root`, `file_parent_not_project_folder` (covers the root itself and a grandchild), `folder_name_not_slug`, `file_name` (not `<slug>.<ext>`; `..`, `/`, `\`, control characters), `extension` (not in `txt|pdf|png|jpg|gif` — `ArchiveMediaTypeWire.IsItemExtension`), `replace_form`, `conflict_behaviour` (≠ `fail`), `too_large`. `DriveWriteRequest(DriveWriteKind Kind, string DriveId, string ParentId, string Name, string? ConflictBehaviour, long Size)`, `DriveWriteKind { CreateFolder, CreateFile, ReplaceById }` (`ReplaceById` exists only so the scope can be tested to refuse it; the writer has no method that produces it).
- `src/Zyggy.Core/M365/Graph/GraphDriveWriter.cs` *(create, internal)*:
  - `interface IGraphDriveWriter { Task<GraphWrite<DriveItemInfo>> CreateFolderAsync(string driveId, string parentId, Slug name, CancellationToken); Task<GraphWrite<DriveItemInfo>> CreateFileAsync(string driveId, string parentId, string name, ReadOnlyMemory<byte> bytes, CancellationToken); }` — exactly two members.
  - `sealed class GraphDriveWriter(GraphTokenClient tokens, GraphHttp http, DriveWriteScope scope, TimeProvider clock) : IGraphDriveWriter`: builds the `DriveWriteRequest`, `scope.Check` → refusal returns `GraphWrite.Refused(rule)` **without calling `http`**; folder = `POST {Graph}/drives/{d}/items/{parent}/children` with JSON `{"name":"<slug>","folder":{},"@microsoft.graph.conflictBehavior":"fail"}` (Assumption A2); file = `PUT {Graph}/drives/{d}/items/{parent}:/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior=fail`, `ByteArrayContent` rebuilt per attempt, `Content-Type: application/octet-stream`; own token via `tokens.MintAsync` on first write, one re-mint on 401 (the `GraphReader.CallAsync` rule; Assumption A1); `GraphHttp` unchanged (its 429/503 retries apply).
  - `GraphWrite<T>` record (`Value`, `Status`, `Failure` of `GraphWriteFailure { None, Refused, Conflict, Forbidden, TypeBlocked, ClientError, ServerError, Throttled, Transport, Auth }`, `Detail` = rule or Graph error code, never the body): 201/200 → value; 409 → `Conflict`; 403 → `Forbidden`; 4xx whose `error.code`/`error.innerError.code` names a blocked type (`typeNotAllowed`, `fileTypeBlocked`, or a message naming "file type"/"extension" — Assumption A9) → `TypeBlocked`; other 4xx → `ClientError`; 5xx → `ServerError`; 429/503 after retries → `Throttled`; transport → `Transport`; mint failure or 401 after re-mint → `Auth`.
- `src/Zyggy.Core/M365/Guard/ActionLog.cs` *(modify, additive)*: `public static string BuildOffloadRow(DateTimeOffset now, string sessionId, string tool, string driveId, string parentId, string name, long size, string status)` → `{"ts":"<UTC Z>","session_id":"archive-offload <run start ISO>","tool":"archive-offload-folder|archive-offload-upload","summary":"drive <id> parent <id> name <name> size <n>","status":"ok|error: <code>"}` (code through the existing `Code` sanitiser); `BuildRow`, `Append`, `Summary` unchanged.
- `tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs` *(modify, additive)*: `AllowDriveWrites()` opt-in that stops reporting as violations exactly `POST /v1.0/drives/{id}/items/{id}/children` and `PUT /v1.0/drives/{id}/items/{id}:/{name}:/content` (any other non-GET stays a violation, also when opted in); records the request bytes in `RecordedRequest.Bytes`. Without the opt-in every existing test behaves as today.
- `tests/golden/archive/offload/actions-rows.jsonl` *(create, hand-derived: one folder row `ok`, one upload row `ok`, one upload row `error: nameAlreadyExists`)*; `tests/golden/m365/graph/offload/created-folder.json`, `created-file.json`, `error-409.json`, `error-blocked-type.json` *(create)*.
- `tests/Zyggy.Core.Tests/Memory/DriveWriteScopeTests.cs`, `tests/Zyggy.Core.Tests/M365/GraphDriveWriterTests.cs`, `tests/Zyggy.Core.Tests/M365/ActionLogOffloadRowTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify: two facts)*.

**Seams**: Graph HTTP edge (`GraphHttp` over the stub handler with `AllowDriveWrites()`); `TimeProvider`. No `IModelRunner` anywhere.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DriveWriteScopeTests|FullyQualifiedName~GraphDriveWriterTests|FullyQualifiedName~ActionLogOffloadRowTests|FullyQualifiedName~ActionLogTests|FullyQualifiedName~SourceHygieneTests|FullyQualifiedName~GraphReaderTests"`):
- `DriveWriteScopeTests` (AC-6 table): `Check_ValidFolderUnderRoot_Null`; `Check_ValidFileInKnownProjectFolder_Null`; `[Theory] Check_Refused_NamesRule`: other drive → `other_drive`; folder under a project folder (grandchild) → `folder_parent_not_root`; file directly in the root → `file_parent_not_project_folder`; file in an unknown folder → `file_parent_not_project_folder`; folder name `Bad Slug`/`..`/`a/b` → `folder_name_not_slug`; file name `../x.txt`, `a/b.txt`, `x\u0007.txt`, `quote.txt.` → `file_name`; `quote.docx`, `quote.PNG`, `quote.md` → `extension`; `ReplaceById` → `replace_form`; conflict behaviour `replace`/`rename`/`null` → `conflict_behaviour`; size 250_000_001 → `too_large`; `EveryRule_HasAProducingRow`.
- `GraphDriveWriterTests` (AC-5, AC-6, AC-10; `GraphFixture` with `AllowDriveWrites()`, violations empty):
  - `CreateFolder_PostChildrenWithFailConflict_ReturnsItem` (exact URL, JSON body bytes, `Content-Type: application/json`).
  - `CreateFile_PutNewFileFormWithFailConflictOctetStream_BytesExact` (exact URL with `?@microsoft.graph.conflictBehavior=fail`, `RecordedRequest.Bytes` equal the input, `Content-Type: application/octet-stream`).
  - `CreateFile_ScopeRefuses_NoRequestSent` (refused write → `Refused` with the rule; `Stub.Requests` has no write and no token POST).
  - `CreateFile_409_Conflict`; `CreateFile_403_Forbidden`; `CreateFile_BlockedType_TypeBlocked`; `CreateFile_400Other_ClientError`; `CreateFile_500_ServerError`; `CreateFile_429SixTimes_Throttled` (recorded delays, five retries); `CreateFile_Transport_Transport` (`OnceThrow`); `CreateFile_401Then201_ReMintsOnce`; `CreateFile_401Twice_Auth`; `CreateFile_MintFails_AuthNoWrite`.
  - `Writer_Surface_ExactlyTwoWriteOperations` (reflection on `IGraphDriveWriter`: two methods named `CreateFolderAsync`, `CreateFileAsync`; `GraphDriveWriter` has no other public or internal method whose name contains `Delete|Move|Rename|Copy|Share|Permission|Invite|Session|Replace|Update|Mail`).
  - `Writer_NeverMeNeverOtherHost` (all recorded URIs under `/v1.0/drives/` or the token endpoint).
- `ActionLogOffloadRowTests` (AC-15 row): `BuildOffloadRow_Rows_EqualGolden` (three rows of `actions-rows.jsonl` byte for byte); `BuildOffloadRow_ErrorCodeSanitised` (`error: <code>` through the 40-char filter); `BuildOffloadRow_NeverBearerOrBody` (the row contains no `Bearer`, no `content`, no byte of the item). Existing `ActionLogTests` unchanged and green.
- `SourceHygieneTests`: `Src_ServiceLiteral_OnlyInItsAdapter` unchanged (host still only in `GraphEndpoints.cs`); new `Src_GraphWriteMethods_OnlyInDriveWriterAndTokenClient` (`HttpMethod.Put`/`HttpMethod.Post`/`HttpMethod.Patch`/`HttpMethod.Delete` under `src/Zyggy.Core/M365/` appear only in `GraphDriveWriter.cs` and `GraphTokenClient.cs`); new `Src_GraphReader_GetOnly` (`GraphReader.cs` contains no `HttpMethod.` other than `Get`).

**GREEN**: as Scope. The writer is a separate type so the reader's GET-only guarantee stays testable (spec Contracts).

**Contract impact**: ⚠️ deliberate change to 33's "GET only besides the token POST" for the adapter as a whole — now true of `GraphReader` only (spec Deviations); new internal `IGraphDriveWriter`; new action-log row shape (`tool: archive-offload-folder|archive-offload-upload`) in the 33 log; `StubGraphHandler` (shared test infrastructure) gains an opt-in.

**VERIFY**: failing-run command passes; `git diff tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs` additive; every pre-existing Graph test green unedited; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (two folder-scoped writes, nothing else) *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `QuickXorHashTests` with the vector table and its source (A10) shown; `DriveWriteScopeTests` rule table; the exact folder POST and file PUT (URL, headers, body) from `GraphDriveWriterTests`; `CreateFile_ScopeRefuses_NoRequestSent`; the three golden action-log rows; `SourceHygieneTests` (host literal, write methods, reader GET-only).
- [x] Contract review: AC-5, AC-6, AC-10, AC-12, AC-15 (row) against the spec; the failure classification table against `OffloadReason` (Contracts); A1 (writer mints its own token: at most two token POSTs per run), A2 (folder conflict behaviour in the JSON body), A9 (blocked-type error codes).
- [x] ⚠️ Risk review (first write code; shared contract 33): the writer cannot express update, delete, move, rename, copy, share, permission, upload session, mail or `/me`; the scope check precedes every request; no body, token or `webUrl` in any row or failure detail; no new package.
- [x] User approved — implementation may continue past this gate

---

## Step 3 — A sidecar carries the offload part after the 37 keys (`offloaded`, `drive_item_id`, `drive_path`, `drive_url`, `drive_quickxor`; `updated` = offload date) byte-equal to the golden and reads back all-or-none; the offload state file counts consecutive failures, parks at `max_failures` or at once for permanent reasons, and a retry clears a park; `zyggy memory archive list [--json]` shows an offloaded item as `offloaded → <drive_path>` (never `orphan`) and `offload_failed(n)`/`parked` suffixes while every 37 row stays byte-identical; `remove` of an offloaded item removes the sidecar in one `archive remove` commit with its inbox line and the stderr note that the OneDrive copy stays, never calling Graph; `add` caps count only bytes on disk and `slug_taken` still applies — proven in unit tests and from the built binary against the bare remote

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/OffloadReason.cs` *(create, internal enum + `OffloadReasonWire`)*: transient `TransportError`, `ServerError`, `Throttled`, `AuthFailed`, `GitError`, `PushDeferred`; permanent `ShaMismatch`, `WriteRefused`, `TargetConflict`, `VerifyFailed`, `TypeBlocked`, `TooLarge`, `Forbidden`; run-level `RootMissing`, `RootNotFolder`, `ConfigError`, `LockHeld`, `LogUnwritable`; `ToWire` snake_case as the spec; `IsPermanent`, `IsRunLevel`.
- `src/Zyggy.Core/Memory/ArchiveStatePaths.cs` *(create, internal sealed)*: `StateDirectory` = `<ZYGGY_STATE_DIR or HOME/.local/state/zyggy>/archive`; `StateFile` = `offload-state.json`; `LockFile` = `offload.lock` (the `BriefPaths`/`M365Paths` pattern).
- `src/Zyggy.Core/Memory/OffloadState.cs` *(create, internal sealed)*: JSON `{"items":{"<project>/<slug>":{"failures":n,"reason":"<wire>","parked":bool,"since":"YYYY-MM-DD"}},"last_run":{"at":"<UTC Z>","failed":bool,"reason":"<wire>|null"}}` (Assumption A8); `Load(ArchiveStatePaths)` (missing → empty; unreadable/invalid → error `offload-state.json`), `Save` atomically, 0600 in a 0700 directory on Linux; `RecordTransient(key, reason, max)` (count +1; park when count ≥ max), `Park(key, reason)`, `Clear(key)`, `TryRetry(key)` (false when unknown or not parked), `Parked`, `NeedsAttention` (any parked or `last_run.failed`), `RecordRun(at, failed, reason)`.
- `src/Zyggy.Core/Memory/ArchiveOffload.cs` *(create, internal sealed record)*: `DateOnly Offloaded, string DriveItemId, string DrivePath, string? DriveUrl, string DriveQuickXor`.
- `src/Zyggy.Core/Memory/ArchiveSidecar.cs` *(modify, additive)*: `ArchiveOffload? Offload { get; init; }`; `ToMemoryFile` appends, after `source_name`, `offloaded`, `drive_item_id`, `drive_path`, `drive_url` (omitted when `null` — the contact-detail edge case), `drive_quickxor` when `Offload` is set; `TryParse` reads the offload part when any of its keys is present and returns `null` when `offloaded`, `drive_item_id`, `drive_path` or `drive_quickxor` is missing or malformed (all-or-none; `drive_url` optional per the edge case); without offload keys the output is byte-identical to today.
- `src/Zyggy.Core/Memory/ArchiveListFormat.cs` *(modify, additive)*: `ArchiveIndexState.Offloaded` (wire `offloaded`); `ArchiveListRow` gains `string? DrivePath`, `int? OffloadFailures`, `string? OffloadReason`, `bool Parked` (defaults → 37 rows unchanged). Text: offloaded row `<item path>  <type>  <size>  offloaded → <drive_path>  — <description>`; a row with failures `…  <37 state>  offload_failed(<n>): <reason>  — …`; parked `…  <37 state>  parked: <reason>  — …`. JSON: keys `drive_path`, `offload_failures`, `offload_reason`, `parked` written **only** on rows that have them (Assumption A7), so 37 rows stay byte-identical.
- `src/Zyggy.Core/Memory/ArchiveService.cs` *(modify)*: `List(ArchiveListRequest, OffloadState? state = null)`: item absent + sidecar with offload part → `Offloaded` (type, size, item path from the sidecar), else 37's rules unchanged; failure/park data from `state`; `--unindexed` keeps 37's meaning (offloaded rows excluded). `RemoveAsync`: unchanged steps; when the sidecar has an offload part, `ArchiveOutcome.Note = "the OneDrive copy at <drive_path> stays; delete it in OneDrive if wanted"`; never Graph.
- `src/Zyggy.Core/Memory/ArchiveVerb.cs` *(modify)*: `list` loads `OffloadState` (missing → none; a broken file → rows without failure data and one stderr note, exit 0 — `list` must not fail on the state file); `remove` prints `archive: note: <note>` on stderr when set.
- `tests/golden/archive/offload/sidecar-offloaded.md` *(create, hand-derived from `sidecar-text.md` + Shared-rules values; `updated: 2026-10-01`)*, `sidecar-offloaded-no-url.md`, `list-offloaded.txt`, `list-offloaded.json` *(create: the 37 three-row tree plus one offloaded row, one `offload_failed(2): throttled` row, one `parked: target_conflict` row)*; `tests/golden/archive/sidecar-*.md`, `list-text.txt`, `list.json` **unchanged**.
- `tests/Zyggy.Core.Tests/Memory/OffloadReasonWireTests.cs`, `OffloadStateTests.cs`, `ArchiveSidecarOffloadTests.cs`, `ArchiveListOffloadTests.cs`, `ArchiveRemoveOffloadedTests.cs`, `ArchiveAddAfterOffloadTests.cs` *(create)*; `ArchiveSidecarTests`, `ArchiveListTests`, `ArchiveRemoveTests` *(unchanged, green)*.
- `tests/Zyggy.Integration/Fixtures/archive-offloaded/acme/alice/**` *(create: the `archive` fixture plus `archive/zyggy/quote-2026.md` = the golden offloaded sidecar, no item file; `business/areas/zyggy.md` with a line naming the path)*; `tests/Zyggy.Integration/Memory/ArchiveOffloadListRemoveCommandTests.cs` *(create)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner` for `remove`), `TimeProvider`; Wire: real git, bare remote, built binary (`ZyggyCli`). No Graph.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~OffloadReasonWireTests|FullyQualifiedName~OffloadStateTests|FullyQualifiedName~ArchiveSidecar|FullyQualifiedName~ArchiveList|FullyQualifiedName~ArchiveRemove|FullyQualifiedName~ArchiveAddAfterOffloadTests"`; integration `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveOffloadListRemoveCommandTests|FullyQualifiedName~ArchiveListCommandTests|FullyQualifiedName~ArchiveRemoveCommandTests"`):
- `OffloadReasonWireTests`: one row per member + `EveryMember_HasATestRow`; `Permanent_SetMatchesSpec`; `RunLevel_SetMatchesSpec`.
- `OffloadStateTests` (AC-19, AC-20, AC-21 state): `Load_Missing_Empty`; `RecordTransient_Twice_CountTwoNotParked`; `RecordTransient_ThirdAtMaxThree_Parked`; `Park_Permanent_ParkedAtOnce`; `Clear_AfterSuccess_ItemGone`; `TryRetry_Parked_ClearedTrue`; `TryRetry_Unknown_False`; `TryRetry_NotParked_False`; `Save_RoundTrip_EqualsJsonShape`; `Save_OnLinux_File0600Dir0700` (`SkipUnless = IsLinux`); `NeedsAttention_ParkedOrLastRunFailed`.
- `ArchiveSidecarOffloadTests` (AC-13 golden): `ToMemoryFile_WithOffload_EqualsGolden`; `ToMemoryFile_OffloadWithoutUrl_EqualsGolden`; `TryParse_OffloadedGolden_RoundTrips`; `[Theory] TryParse_PartialOffloadKeys_Null` (each of the four required keys missing in turn; a malformed `offloaded` date); `ToMemoryFile_WithoutOffload_ByteIdenticalTo37Goldens` (the three 37 goldens).
- `ArchiveListOffloadTests` (AC-24 U): `List_Offloaded_StateOffloadedNeverOrphan`; `List_Text_EqualsGolden`; `List_Json_EqualsGolden`; `List_37RowsByteIdenticalWithStatePresent` (the 37 three-row tree with an empty state → `list-text.txt` and `list.json` unchanged); `List_Unindexed_ExcludesOffloaded`; `List_BrokenStateFile_RowsWithoutFailureDataNote`; `List_NeverCallsGitOrGraph`.
- `ArchiveRemoveOffloadedTests` (AC-25 U): `Remove_Offloaded_GitRmSidecarOnlyCommitRemoveNote` (`rm -- <sidecar>`, `commit --only` of the sidecar, message in the `commit-message-remove.txt` form with the sidecar's item extension, note text exact); `Remove_Offloaded_InboxRemovedLineWithName`; `Remove_Offloaded_NoGraphDependency` (reflection on `ArchiveService`'s constructor: no Graph type).
- `ArchiveAddAfterOffloadTests` (AC-27 U): `Add_ProjectWithOffloadedItem_CapsCountOnlyBytesOnDisk` (a project at its cap before the offload accepts a new item after the item file is gone); `Add_OffloadedSlug_SlugTaken`.
- `ArchiveOffloadListRemoveCommandTests` (AC-24 I, AC-25 I; fixture `archive-offloaded`): `List_OffloadedFixture_RowOffloadedArrowDrivePath`; `List_Json_OffloadedRowHasDrivePath`; `Remove_Offloaded_SidecarGoneOnMainOneCommitStderrNote` (`git show --name-status main` one `D`; stderr contains `archive: note: the OneDrive copy at Zyggy/archive/zyggy/quote-2026.txt stays`); `Remove_Offloaded_NoM365Configuration_StillExitsZero`. Existing `ArchiveListCommandTests`/`ArchiveRemoveCommandTests` green unedited.

**GREEN**: as Scope.

**Contract impact**: ⚠️ shared contracts (spec Risk Areas): sidecar format gains the offload part (W38-3), `ArchiveIndexState` gains `Offloaded`, `list` text/JSON gain the offload forms (A7), `remove` gains a stderr note; all additive, 37 goldens unchanged. New state file `<state>/archive/offload-state.json` (A8).

**VERIFY**: failing-run commands pass; `git diff tests/golden/archive/*.md tests/golden/archive/list*` empty; build/test/format green; CI green on both runners.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`ArchiveStatePaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — `instance/archive.json` gains a validated `offload` block (off by default; out-of-range values name the key, never clamped); the drive path `Zyggy/archive/<project>/<slug>.<ext>` is built in one place; selection returns exactly the `indexed` items whose bytes are present, whose sidecar parses, that are not offloaded, not parked, not excluded and at least `min_bytes`, in ordinal path order within `max_items_per_run`/`max_bytes_per_run`, reading no item bytes; a candidate whose SHA-256 differs from its sidecar is reported `sha_mismatch` without its bytes

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/OffloadOptions.cs` *(create, public sealed record — `ArchiveOptions` is public)*: `Enabled` (false), `RootFolder` (`"Zyggy/archive"`), `MinBytes` (0), `MaxItemsPerRun` (20), `MaxBytesPerRun` (209_715_200), `MaxFailures` (3), `Exclude` (`IReadOnlyList<string>`, `<project>/<slug>`); `Validate()` → offending keys `offload.root_folder` (1–3 segments, each a plain folder name: no `..`, `/` inside a segment, `\`, control characters, leading/trailing space), `offload.min_bytes` (0..`ArchiveOptions.ItemCeiling`), `offload.max_items_per_run` (1..200), `offload.max_bytes_per_run` (1_048_576..1_073_741_824), `offload.max_failures` (1..10), `offload.exclude` (each entry two `Slug`s joined by `/`).
- `src/Zyggy.Core/Memory/ArchiveOptions.cs` *(modify, additive)*: `public OffloadOptions Offload { get; init; } = new();`; `Validate()` appends `Offload.Validate()`.
- `src/Zyggy.Core/Memory/ArchiveConfiguration.cs` *(modify, additive)*: key `offload` (JSON object; unknown nested key → `offload.<key>` "is not an archive.json key"; wrong type → `offload.<key>` "has the wrong type"). Every 37 key and message unchanged.
- `src/Zyggy.Core/Memory/DriveArchivePath.cs` *(create, internal sealed)*: `DriveArchivePath(string rootFolder)`; `Root` (the configured text), `Segments`, `ProjectFolder(Slug project)` → `<root>/<project>`, `Item(Slug project, Slug slug, string extension)` → `<root>/<project>/<slug>.<ext>`; the only builder of OneDrive paths (roadmap design rule "one drive-path type").
- `src/Zyggy.Core/Memory/OffloadSelection.cs` *(create, internal static)*: `Select(IReadOnlyList<ArchiveListRow> rows, MemoryPaths paths, OffloadOptions options, OffloadState state) → OffloadPlan(IReadOnlyList<OffloadCandidate> Candidates, IReadOnlyList<ArchiveListRow> Deferred)`: rows with `State == Indexed`, item present (`FileInfo.Length`, never read), sidecar parsed without offload part, not parked, not in `Exclude`, `Size ≥ MinBytes`; resume rows (sidecar with offload part **and** item present — AC-17) first; ordinal path order; take while count < `MaxItemsPerRun` and cumulative bytes ≤ `MaxBytesPerRun`, the first candidate always taken (Assumption A11). `OffloadCandidate(Slug Project, Slug Slug, string ItemPath, string SidecarPath, ArchiveSidecar Sidecar, long Size, bool Resume)`. `static (byte[]? Bytes, OffloadReason? Reason) ReadAndCheck(OffloadCandidate)`: `File.ReadAllBytes` once, SHA-256 hex vs `Sidecar.Sha256` → `ShaMismatch` (AC-2).
- `tests/Zyggy.Core.Tests/Memory/OffloadOptionsTests.cs`, `ArchiveConfigurationOffloadTests.cs`, `DriveArchivePathTests.cs`, `OffloadSelectionTests.cs` *(create)*; `ArchiveConfigurationTests.cs`, `ArchiveOptionsTests.cs` *(unchanged, green)*.
- `tests/Zyggy.Integration/Memory/ArchiveConfigurationOffloadCommandTests.cs` *(create)*: the built binary's `zyggy memory archive list` with an `archive.json` holding an invalid `offload` key exits 3 naming `offload.<key>`, and with a valid block exits 0 with 37's output (the configuration file is a shared contract read by every archive sub-verb).

**Seams**: `TimeProvider` (`FakeTimeProvider`). No Graph, no git. Wire: the built binary for configuration loading.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~OffloadOptionsTests|FullyQualifiedName~ArchiveConfigurationOffloadTests|FullyQualifiedName~DriveArchivePathTests|FullyQualifiedName~OffloadSelectionTests|FullyQualifiedName~ArchiveConfigurationTests|FullyQualifiedName~ArchiveOptionsTests"`; integration `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveConfigurationOffloadCommandTests"`):
- `OffloadOptionsTests`: `Defaults_MatchSpecTable` (false, `Zyggy/archive`, 0, 20, 200 MiB, 3, empty); `[Theory] Validate_OutOfRange_NamesKey` (each key below and above its range; root `""`, `a/b/c/d`, `Zyggy/..`, ` Zyggy`, `Zyggy\archive`, `Zy\u0001ggy`; exclude `zyggy`, `zyggy/Bad Slug`).
- `ArchiveConfigurationOffloadTests` (AC-3 configuration part): `Load_NoOffloadBlock_DisabledDefaults`; `Load_OffloadBlock_Applied`; `[Theory] Load_OffloadInvalid_NamesNestedKey` (`offload.foo` unknown; `offload.max_failures: "3"` wrong type; `offload.max_items_per_run: 201` above ceiling; `offload: []` not an object → `offload`); `Load_37KeysWithOffload_Unchanged`.
- `DriveArchivePathTests`: `Item_DefaultRoot_ZyggyArchiveProjectSlugExt`; `Item_ThreeSegmentRoot`; `ProjectFolder_Slug`.
- `OffloadSelectionTests` (AC-1, AC-2; `MemoryTree` with the 37 fixed items): `Select_EveryState_OnlyIndexedPresentParsedNotOffloadedNotParkedNotExcludedAboveMin` (one row per 37 state, an offloaded item, a parked item, an excluded item, an item below `min_bytes` → exactly the eligible one); `Select_OrdinalOrder`; `Select_MaxItems_RestDeferred`; `Select_MaxBytes_RestDeferred_FirstAlwaysTaken`; `Select_ResumeRowsFirst`; `Select_NeverReadsItemBytes` (item files locked exclusively on Windows / mode `000` on Linux — selection succeeds); `ReadAndCheck_ShaMatches_Bytes`; `ReadAndCheck_ShaMismatch_ShaMismatchNoBytes`.
- `ArchiveConfigurationOffloadCommandTests` (AC-3 configuration, I): `List_InvalidOffloadKey_ExitThreeNamesKey`; `List_ValidOffloadBlock_Unchanged37Output`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ `archive.json` gains the `offload` block (spec Configuration) — read by every archive sub-verb; public `ArchiveOptions.Offload` + `OffloadOptions` (`.claude/instructions/public-api.md`).

**VERIFY**: failing-run commands pass; `ArchiveConfigurationTests`/`ArchiveOptionsTests` green unedited; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`DriveArchivePath`/`ArchiveStatePaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (memory knows offloaded items) *(covers Steps 3–4)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: the offloaded sidecar golden next to 37's `sidecar-text.md`; `list` text and JSON from the built binary for the `archive-offloaded` fixture; `remove` commit (`git show --name-status`) and the stderr note; selection table of `OffloadSelectionTests`; `OffloadState` JSON after a transient, a park and a `--retry`.
- [x] Contract review: AC-1, AC-2, AC-3 (configuration), AC-13, AC-19..AC-21 (state), AC-24, AC-25, AC-27 against the spec; the `offload` keys, defaults and ranges against Configuration; `OffloadReason` wire strings against Contracts; **A7** (list/JSON forms), **A8** (state file shape), **A11** (byte cap: first candidate always taken).
- [x] ⚠️ Risk review (shared contracts): 37 goldens and 37 test files unedited and green; a sidecar without offload keys renders byte-identically; `list` never runs git or Graph; `remove` never calls Graph and deletes only the sidecar of an offloaded item.
- [x] User approved — implementation may continue past this gate

---

## Step 5 — One offload run (fakes): for each candidate the service reads and SHA-256-checks the bytes, creates the project folder once, adopts an identical existing target or uploads a new file, re-reads the item by id and compares drive, parent, name, size and QuickXorHash, only then writes the sidecar offload part, `git rm`s the item and makes one `archive offload <project>/<slug>.<ext>` commit of item and sidecar pushed through `MemoryPublisher`, then appends one `[observed]` inbox line and clears the item's state; every write leaves one `ok`/`error` row; no model is involved

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/OffloadService.cs` *(create, internal sealed)*: `OffloadService(MemoryPaths paths, TimeZoneInfo zone, TimeProvider clock, SecretPatterns secrets, ArchiveOptions options, OffloadState state, ArchiveStatePaths statePaths, MemoryPublisher publisher, string repository, IGraphReader reader, Func<DriveWriteScope, IGraphDriveWriter> writerFactory, ActionLog log)`; `Task<OffloadRun> RunAsync(CancellationToken)` in the CLI-table order after the lock/configuration (Step 7): git preflight (`not_on_branch`/`operation_in_progress` → run-level; `PushPending` → run-level `git: push_pending`, exit 6 before any upload) → `reader.SignInAsync` → `OneDriveIdAsync` → `ItemByPathAsync(drive, root)` (missing/file → Step 6) → `DriveWriteScope(drive, root.Id)` → `OffloadSelection.Select` → per candidate:
  1. `ReadAndCheck` (AC-2).
  2. Project folder: `ChildAsync(drive, root, project)`; folder under root → `scope.KnowProjectFolder`; absent → `CreateFolderAsync` (one per project per run; a 409 → re-read, use it if a folder under the root, else `target_conflict`) (AC-8).
  3. Target: `ChildAsync(drive, folder, "<slug>.<ext>")`; present with size and QuickXorHash equal to the local bytes → **adopt** (no upload); present otherwise → `target_conflict` (AC-9).
  4. Upload: `CreateFileAsync`; 201 → item; 409 → step 3's rule (AC-10).
  5. Verify: `ItemByIdAsync(drive, item.Id)` — drive, parent = folder id, name, size, `QuickXorHash.Base64(bytes)` equal → else `verify_failed`, nothing in git, the copy left (AC-11).
  6. Sidecar: `ArchiveOffload(today, id, DriveArchivePath.Item(...), webUrl unless ContactDetailPatterns.Contains(webUrl) → null + stderr note, quickXor)`; `updated` = today; `MemoryFileWriter.Write` (atomic) (AC-13).
  7. Git: `publisher.RemoveAsync([item])` then `CommitAndPushAsync(addPaths: [sidecar], commitPaths: [item, sidecar], message: OffloadCommitMessage.For(...))` (AC-14).
  8. Inbox line: `RememberService.Remember(new RememberRequest("observed", " (project:<p>) (source: archive offload)", "", "Moved archive/<p>/<slug>.<ext> to OneDrive <drive_path>"))` → exactly `- [observed] YYYY-MM-DD (project:<p>) (source: archive offload): Moved …` (AC-18; Assumption A3).
  9. `state.Clear(key)`; stdout lines.
  - Every write's result → `log.Append(ActionLog.BuildOffloadRow(...))` with `session_id = "archive-offload <run start ISO>"` before the next write (AC-15).
  - `OffloadRun` record: per-item `OffloadItemResult(Key, Outcome {Moved, Adopted, Failed, Parked, Skipped}, Reason?, Detail?, Sha?, Pushed)`, run-level reason, exit code (Step 6/7).
- `src/Zyggy.Core/Memory/OffloadCommitMessage.cs` *(create, internal static)*: `For(Slug project, Slug slug, string ext, string driveItemId, string sha256, string quickXor, long size)` → `archive offload <p>/<slug>.<ext>\n\ndrive_item_id: <id>; sha256: <hex>; quickxor: <b64>; size: <n> bytes\n\nZyggy-Tool: memory archive offload\n`.
- `src/Zyggy.Core/M365/M365Session.cs` *(modify, additive)*: `GraphParts` gains `Func<DriveWriteScope, IGraphDriveWriter> DriveWriter` built from the same `GraphHttp`/`GraphTokenClient` (`CreateGraph` unchanged for every existing caller).
- `tests/golden/archive/offload/commit-message-offload.txt`, `inbox-after-offload.md` *(create, hand-derived)*.
- `tests/Zyggy.Core.Tests/Memory/OffloadServiceTests.cs`, `OffloadCommitMessageTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/StubOneDrive.cs` *(create: an in-memory drive — folders and files by id, parent, name, bytes, size; `quickXorHash` from a test-supplied lookup over `quickxor-vectors.tsv` cases, never from `QuickXorHash` itself; `StubGraphHandler.UseDrive(StubOneDrive)` answers the Step 1 reads and the two writes from it; switches `CorruptNextHash`, `RefuseNextWith(status, code)`)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner`, git scripted), `TimeProvider` (`FakeTimeProvider` 2026-10-01T03:30:00Z), Graph through the real `GraphReader`/`GraphDriveWriter` over `StubGraphHandler` + `StubOneDrive`. No `IModelRunner`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~OffloadServiceTests|FullyQualifiedName~OffloadCommitMessageTests"`):
- `OffloadCommitMessageTests`: `For_FixedInputs_EqualsGolden`.
- `OffloadServiceTests` (temp `MemoryTree` with the indexed fixed text item; recording git; stub drive with root `Zyggy/archive`):
  - `Run_OneIndexedItem_MovedFolderCreatedFileUploadedVerifiedCommittedPushed` (AC-8, AC-10, AC-11, AC-14 U: one folder POST, one PUT, one verification GET by id; recorded git calls `rm -- <item>`, `add -- <sidecar>`, `commit --only -F - -- <item> <sidecar>` with the golden message on stdin, `rev-parse HEAD`, `push origin HEAD:main`).
  - `Run_Moved_SidecarEqualsOffloadedGolden` (AC-13).
  - `Run_Moved_InboxLineEqualsGolden` (AC-18; `inbox-after-offload.md`; `MemoryLine` is **not** asserted — A3).
  - `Run_Order_NoGitCallBeforeVerification` (the first git write is recorded after the verification GET; the sidecar file is unchanged until verification passed).
  - `Run_TwoItemsSameProject_OneFolderTwoUploadsTwoCommits` (edge case row).
  - `Run_ProjectFolderExists_Reused` (owner-created folder; no POST) and `Run_FolderCreate409_ReReadAndUsed`.
  - `Run_TargetIdenticalExists_AdoptedNoUploadCommitted` (AC-9 adopt; no PUT; commit made).
  - `Run_EachWrite_OneActionRowOkOrError` (AC-15 U: rows in order, `session_id` = `archive-offload 2026-10-01T03:30:00Z`).
  - `Run_WebUrlMatchesContactPattern_StoredWithoutUrlNote` (edge case row; golden `sidecar-offloaded-no-url.md`).
  - `Run_UploadedBytes_AreExactlyTheRepositoryBytes` (`StubOneDrive` stores the PUT bytes; equal to the item file) and `Run_ReadsBackOnlyMetadata` (no GET to `/content`) (AC-29).
  - `Run_NoModelRunnerAnywhere` (AC-29: reflection over `OffloadService` constructor parameters and fields — no `IModelRunner`, no `Zyggy.Core.Models` type).
  - `Run_Caps_TwentyFirstItemWaits` (AC-1 run level).
  - `Run_SecretsNeverInGitArgsOrRows` (AC-30 U: the key PEM and the stub token text appear in no recorded git argument, stdin, environment or action-log row).

**GREEN**: as Scope. Run-level failures, transient/permanent classification, park, resume and exit codes are Step 6; the verb is Step 7.

**Contract impact**: ⚠️ new commit contract `archive offload …` with trailer `Zyggy-Tool: memory archive offload` (its subject starts with `archive `, so 37's preflight pushes a deferred one first — unchanged code); new inbox line form (spec Contracts, A3); `GraphParts` additive member.

**VERIFY**: failing-run command passes; `MemoryPublisherTests`, `DreamRunnerGitTests` green unedited; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`DriveArchivePath`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — A failing offload never loses bytes (fakes): a missing or non-folder root, a sign-in failure, a git preflight failure, a 403 on a write or an action log that cannot be appended stop the run before any item is half-done (exit 6); a transient failure leaves the item untouched, counts it and parks it at `max_failures`; a permanent failure parks it at once; a commit failure after the sidecar write restores the item from `HEAD` and the next run re-verifies and commits it, or restores the sidecar and parks it; other candidates are still processed; a deferred push stops the run with exit 7; a second concurrent run exits 0

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/OffloadService.cs` *(modify)*:
  - Run-level: root `null` → `root_missing`, not a folder → `root_not_folder` (message `offload: root folder <path> missing or not a folder — runbook "Offload root folder"`; never a create above or at the root) (AC-7); mint failure → `auth_failed` run-level exit 6; `GraphWriteFailure.Forbidden` → run stops, `forbidden — runbook "Grant OneDrive write for the archive folder"`, exit 6, the item **not** parked (Contracts: run stops); `ActionLog.Append` throws `IOException` → `log_unwritable`, stop before the next write (AC-15).
  - Per item: `Transport|ServerError|Throttled|Auth` → `state.RecordTransient` (AC-19); `Refused|Conflict(after adopt check)|TypeBlocked|ClientError-blocked|too_large|sha_mismatch|verify_failed` → `state.Park` (AC-20); git failure after the sidecar write → `GitClient.CheckoutPathsAsync(repo, [item])` restores the item, sidecar keeps the offload part, `RecordTransient(git_error)` (AC-16; Assumption A4).
  - Resume (AC-17): a `Resume` candidate is re-verified by `drive_item_id` (`ItemByIdAsync`, size + QuickXorHash vs the local bytes, parent/name vs the sidecar's `drive_path` folder) → commit as Step 5; verification failing → `CheckoutPathsAsync(repo, [sidecar])` restores the sidecar from `HEAD` and parks `verify_failed`.
  - Push deferred (`Pushed == false`) → `push_deferred`, the run stops after that item, exit 7 (Assumption A5).
  - Exit: 6 run-level > 7 push deferred > 5 any item failed this run or any item parked in the state > 0 (Assumption A6); `state.RecordRun(at, failed, reason)` and `Save` at the end of every run that reached selection or failed at run level.
- `src/Zyggy.Core/Memory/OffloadLock.cs` *(create, internal sealed; the `DreamLock` pattern on `ArchiveStatePaths.LockFile`)*.
- `tests/Zyggy.Core.Tests/Memory/OffloadServiceFailureTests.cs`, `OffloadLockTests.cs` *(create)*.

**Seams**: as Step 5.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~OffloadServiceFailureTests|FullyQualifiedName~OffloadLockTests|FullyQualifiedName~OffloadServiceTests"`):
- Run-level (AC-7, AC-15, Failure modes): `Run_RootMissing_ExitSixNoWriteNoGitWrite`; `Run_RootIsFile_ExitSixRootNotFolder`; `Run_NeverCreatesRootOrAbove` (no POST whose parent is not the root id in any scenario of the class); `Run_MintFails_ExitSixNoItemTouched`; `Run_PreflightNotOnBranch_ExitSixBeforeAnyGraphWrite`; `Run_PreflightPushPending_ExitSixBeforeUpload`; `Run_Forbidden403OnFolder_ExitSixRunStopsNotParked`; `Run_ActionLogUnwritable_StopsBeforeNextWrite` (state directory read-only on Linux / the log file held exclusively on Windows).
- Per item (AC-16, AC-19, AC-20): `[Theory] Run_Transient_ItemUntouchedCountedRetriedNextRun` (`transport_error`, `server_error`, `throttled`, `auth_failed` mid-run: item and sidecar bytes unchanged, no git write, count 1, second run count 2, third run parked at 3); `[Theory] Run_Permanent_ParkedAtOnceNeverRetried` (`sha_mismatch`, `write_refused`, `target_conflict`, `verify_failed`, `type_blocked`, `too_large` via a 250 MB limit lowered in the scope for the test); `Run_VerifyFailed_CopyLeftOnDriveNothingInGit` (no DELETE ever; `StubOneDrive` still holds the file); `Run_OneItemFails_OtherCandidatesStillMoved`.
- Git after sidecar (AC-16, AC-17): `Run_CommitFailsAfterSidecar_ItemRestoredFromHeadSidecarKeepsOffloadGitError`; `Run_NextRun_ResumeReVerifiesAndCommits`; `Run_Resume_VerifyFails_SidecarRestoredFromHeadParked`.
- `Run_PushDeferred_ExitSevenStopsAfterItem`; `Run_ExitPrecedence_SixOverSevenOverFiveOverZero`; `Run_PreviouslyParkedItem_ExitFive` (A6); `Run_Success_ClearsStateAndLastRunOk`.
- `OffloadLockTests`: `TryAcquire_SecondHolder_Null`; `Dispose_Releases`.

**GREEN**: as Scope.

**Contract impact**: `OffloadReason` behaviour (transient vs permanent vs run-level) as Contracts; exit codes 5/6/7 semantics (A5, A6).

**VERIFY**: failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`DriveArchivePath`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, failures as closed reasons (never an exception to the caller), log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 7 — `zyggy memory archive offload [--dry-run | --probe | --retry <project>/<slug>]` (in process): exits 0 with `offload: disabled (archive.json offload.enabled)` and no Graph or git call when disabled; runs under `ZYGGY_HOOKS=off` (the only archive sub-verb that does — `add`/`remove` still refuse); takes the lock (`another run is in progress`, exit 0); configuration errors exit 3 naming the key; usage errors exit 4; `--dry-run` prints what would move without a write or git call; `--probe` reads drive, root and the root's children and prints `drive: … root: … ok`; `--retry` clears a park and runs, exit 4 when not parked; a run with a failed or parked item ends with the stderr summary and exit 5

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/ArchiveArguments.cs` *(modify, additive)*: `ParseOffload(args) → (OffloadRequest? Request, string? Error)`: none, `--dry-run`, `--probe`, `--retry <project>/<slug>` (two `Slug`s); at most one; unknown → `unexpected argument '<x>'`; `UsageSuffixOffload` = ` (usage: zyggy memory archive offload [--dry-run | --probe | --retry <project>/<slug>])`.
- `src/Zyggy.Core/Memory/ArchiveVerb.cs` *(modify)*: dispatch `offload` (unknown-verb usage text lists `add|list|remove|offload`); the `ZYGGY_HOOKS=off` refusal keeps `add`/`remove` only (AC-4); internal test ctor gains `HttpMessageHandler? graphHandler = null, bool checkKeyOwnership = true`; `OffloadAsync`: `OffloadLock.TryAcquire` (held → exit 0 `offload: another run is in progress`) → memory, secret patterns, `archive.json` (incl. `offload`), `M365Session.Load(new M365VerbContext(environment, clock, findTimeZone, processes, graphHandler, checkKeyOwnership), baseOnly: false)` (exit 3 with `offload: configuration error: …`) → `OffloadState.Load` (exit 3 `offload-state.json`) → usage (exit 4) → `Enabled` false → exit 0 one line → `--retry` (`TryRetry` false → exit 4 `offload: <p>/<slug> is not parked`) → `--probe` (reads only; exit 0/6) → `--dry-run` (preflight-free: selection + drive/root resolution; prints `would offload: archive/<p>/<slug>.<ext> -> <drive_path>` per candidate; exit 0/6) → `OffloadService.RunAsync` → stdout/stderr per Contracts; exit 5 summary `offload: <n> moved, <f> failed, <p> parked — runbook "Offload upload failed"`; journal lines = stderr lines, one per item state change, none for an unchanged run.
- `src/Zyggy.Cli/CliApplication.cs` *(modify)*: the `archive` help description names `offload`. `RawVerbs` unchanged (already passes `memory archive …`).
- `tests/Zyggy.Core.Tests/Memory/ArchiveArgumentsOffloadTests.cs`, `ArchiveVerbOffloadTests.cs` *(create)*; `ArchiveVerbUsageTests` *(modify: one row — unknown verb text lists `offload`; every other row unchanged)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner`), Graph (stub handler + `StubOneDrive`), `TimeProvider`, `TestCertificates` key/cert, the `m365.json` golden.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ArchiveArgumentsOffloadTests|FullyQualifiedName~ArchiveVerbOffloadTests|FullyQualifiedName~ArchiveVerbUsageTests|FullyQualifiedName~ArchiveArgumentsTests"`):
- `ArchiveArgumentsOffloadTests`: none/each flag ok; two flags → error; `--retry zyggy` → error; `--retry Zyggy/x` → error; `--bogus` → error.
- `ArchiveVerbOffloadTests` (AC-3, AC-4, AC-21, AC-22, AC-34a U):
  - `Run_Disabled_ExitZeroOneLineNoGraphNoGit` (stub saw no request; recorder saw no call); `Run_DisabledHooksOff_SameAsAttended`.
  - `Run_HooksOff_Enabled_Proceeds` and `Run_HooksOff_AddRemove_StillRefused` (37 AC-5 rows unchanged).
  - `Run_LockHeld_ExitZeroAnotherRun`.
  - `[Theory] Run_ConfigError_ExitThreeNamesKey` (`offload.max_failures` 11; m365.json missing; secret patterns missing; broken state file).
  - `Run_Usage_ExitFourOneLine`.
  - `Run_DryRun_PrintsCandidatesNoWriteNoGit`.
  - `Run_Probe_PrintsDriveRootOkReadsOnly`; `Run_Probe_RootMissing_ExitSix`.
  - `Run_Retry_ParkedItemClearedAndMoved`; `Run_Retry_NotParked_ExitFour`.
  - `Run_OneMovedOneParked_ExitFiveSummaryLine` (exact stderr summary); `Run_AllMoved_StdoutLinesExitZero` (exact stdout); `Run_NothingToDo_ExitZeroNoOutput` (no journal line for an unchanged run).

**GREEN**: as Scope.

**Contract impact**: new CLI surface `zyggy memory archive offload` (W38-9); 37's "`ZYGGY_HOOKS=off` refuses" now true for `add`/`remove` only (spec Deviations).

**VERIFY**: failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`DriveArchivePath`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — End to end against a temporary memory repository with a local bare remote and a stub OneDrive (verb in process, real git): an indexed text item and an indexed PNG move — on `origin/main` the item paths are gone in two `archive offload` commits, the sidecars carry the offload keys, history still has the bytes, `actions.jsonl` has one `ok` row per write, the inbox has two `Moved …` lines, `list` says `offloaded`; a failed upload leaves `git status` clean and the remote unchanged; an interrupted commit is resumed by the next run; a rejected push is rebased once or deferred (exit 7) and pushed first by the next `archive …` run; and the built binary exits 0 disabled, 3 on a bad `offload` key, 4 on bad usage and 0 on a held lock, never touching the network

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Fixtures/archive-indexed/acme/alice/**` *(create: the `archive` fixture plus `archive/zyggy/quote-2026.txt` (the 66-byte item) + its 37 golden sidecar, `archive/zyggy/screenshot.png` + sidecar, `business/areas/zyggy.md` with one `[stated]` line naming each item path — so `list` says `indexed`)*.
- `tests/Zyggy.Integration/Infrastructure/OffloadHarness.cs` *(create)*: `MemoryRepoFixture` seeded with `archive-indexed` + an instance directory with the `m365.json` golden and `archive.json` (`offload.enabled: true`) + `TestCertificates` key/cert under `<HOME>/.config/zyggy` + a state directory; `Env()` (the `ArchiveEnv` dictionary plus `ZYGGY_INSTANCE_DIR`, `ZYGGY_STATE_DIR`, `CREDENTIALS_DIRECTORY=null`, `XDG_CONFIG_HOME` inside the fixture — the `M365InstanceFixture.Env` rule); `RunInProcessAsync(StubGraphHandler, FakeTimeProvider?, params string[] args)` → `new ArchiveVerb(env, clock, findTimeZone, new ProcessRunner(TimeProvider.System), graph, checkKeyOwnership: OperatingSystem.IsLinux()).RunAsync(["offload", .. args], …)`; `ActionRows()`.
- `tests/Zyggy.Integration/Memory/ArchiveOffloadEndToEndTests.cs`, `ArchiveOffloadPushTests.cs`, `ArchiveOffloadCommandTests.cs` *(create)*.
- Production fixes only inside `src/Zyggy.Core/Memory/Offload*`, `Archive*`, `DriveWriteScope`, `src/Zyggy.Core/M365/Graph/GraphDriveWriter.cs` if the tests expose one.

**Seams**: wires `IProcessRunner` (real `ProcessRunner` → real `git`), the bare remote, the CLI host (built binary for pre-Graph paths); Graph = stub handler + `StubOneDrive` in process (no test switch in the binary).

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ArchiveOffloadEndToEndTests|FullyQualifiedName~ArchiveOffloadPushTests|FullyQualifiedName~ArchiveOffloadCommandTests"`):
- `ArchiveOffloadEndToEndTests` (AC-14 I, AC-15 I, AC-16 I, AC-17 I, AC-24 I, AC-30 I):
  - `Offload_TwoIndexedItems_TwoCommitsItemPathsGoneSidecarsUpdated` (bare `main` gained exactly two commits; subjects `archive offload zyggy/quote-2026.txt`, `archive offload zyggy/screenshot.png`; bodies per the golden form; `git show main:…/quote-2026.txt` fails; `git show main~2:…/quote-2026.txt` bytes equal the item; the sidecar on `main` parses with the offload part).
  - `Offload_ActionRows_OneOkRowPerWrite` (one folder row, two upload rows, `session_id` shared).
  - `Offload_InboxLines_NotCommitted` (two `Moved …` lines in `inbox/remember-<today>.md`; `git ls-files acme/alice/inbox` empty).
  - `Offload_ThenListBinary_ShowsOffloaded` (`zyggy memory archive list` from the built binary).
  - `Offload_UploadFails_StatusCleanRemoteUnchanged` (`RefuseNextWith(500)`; `TreeFingerprintAsync` before/after equal except the state file; exit 5).
  - `Offload_CommitInterrupted_NextRunResumes` (a commit made to fail once by a pre-created `.git/index.lock` held for the first run; item restored from `HEAD`; the second run commits without a new upload — `StubOneDrive` saw one PUT in total).
  - `Offload_KeyNeverOnDiskOrInGitOrLog` (after the run, no file under the clone, state dir or `actions.jsonl` contains the key PEM body or the stub token; AC-30 I).
- `ArchiveOffloadPushTests` (AC-14 I push): `Offload_RemoteAheadNoConflict_RebasedOncePushed` (`PushFromSecondCloneAsync`); `Offload_RemoteAheadConflict_ExitSevenStopsAfterFirstItem` (the second item waits); `Offload_AfterDeferredPush_NextArchiveAddPushesFirst` (37's preflight, unchanged code, pushes the `archive offload` commit first).
- `ArchiveOffloadCommandTests` (built binary, AC-3, AC-4, AC-34a I): `Offload_Disabled_ExitZeroOneLine` (instance `archive.json` without `offload`); `Offload_HooksOffDisabled_ExitZero`; `Offload_BadOffloadKey_ExitThreeNamesKey`; `Offload_Usage_ExitFour`; `Offload_LockHeld_ExitZero` (the test holds `<state>/archive/offload.lock`); `Offload_Enabled_NoKey_ExitSixOrThreeNoNetwork` (no key file: the token client fails before any request — the binary never reaches a real host; asserted by exit code and the stderr line naming the key, with `CREDENTIALS_DIRECTORY` unset); `AddRemove_HooksOff_StillRefused`.

**GREEN**: fixes only.

**Contract impact**: none further.

**VERIFY**: failing-run command passes on Windows; CI green on both runners (Linux facts on ubuntu); build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`DriveArchivePath`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C (one offload run, verify before commit) *(covers Steps 5–8)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `ArchiveOffloadEndToEndTests` green with `git log --stat` of the bare repo (two `archive offload` commits) and `git show main:…/quote-2026.md`; the `actions.jsonl` rows; the failed-upload fingerprint; the resume run (one PUT in total); the exit-7 push case; the binary's disabled/config/usage/lock outputs verbatim.
- [x] Contract review: AC-1..AC-22, AC-24, AC-25, AC-29, AC-30, AC-34a against the spec; order of operations (no git write before verification); commit subject/body/trailer; inbox line bytes (**A3**); exit-code precedence (**A5**, **A6**); the commit-failure restore (**A4**).
- [x] ⚠️ Risk review (data loss, credentials, first unattended write path): bytes leave `main` only after a passed verification; nothing is ever deleted or overwritten on OneDrive; the key never in argv, a child's environment, a file, a log or a row; no model, no MCP server in the path; `MemoryPublisher` and the dream's git code unchanged.
- [x] User approved — implementation may continue past this gate

---

## Step 9 — The identity digest carries exactly one `[warning] archive offload: <n> item(s) need attention — zyggy memory archive list; runbook "Offload upload failed"` line at its head while any item is parked or the last run failed (within the identity cap, absent otherwise), the `index` and `daily` sections stay byte-identical to 37, and the index digest after an offload never shows an archive path, sidecar or URL on its own (fakes)

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/OffloadDigestWarning.cs` *(create, internal static)*: `string? Line(OffloadState state)` → the AC-23 line with `<n>` = parked items + (last run failed ? the run counts as 1 when no item is parked — Assumption A12) or `null`.
- `src/Zyggy.Core/Memory/DigestBuilder.cs` *(modify, additive)*: `public IReadOnlyList<string> IdentityWarnings { get; init; } = [];` — appended to the identity head after the CLAUDE.md warning and before "The lines below are the owner's memory…" (Assumption A13); counted in the section bytes like the existing warning; not echoed to stderr. Empty list → output byte-identical to today.
- `src/Zyggy.Cli/Commands/MemoryDigestCommand.cs` *(modify)*: for `identity` only, `ArchiveStatePaths(environment)` + `OffloadState.Load` (missing or unreadable → no line; the digest never fails on it) → `IdentityWarnings`.
- `tests/golden/digest/offload/identity-with-warning.txt` *(create, hand-derived from the existing identity golden + one line)*.
- `tests/Zyggy.Core.Tests/Memory/OffloadDigestWarningTests.cs`, `DigestBuilderOffloadWarningTests.cs` *(create)*; `DigestBuilderGoldenTests` *(unchanged, green)*.

**Seams**: `TimeProvider`; file system only.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~OffloadDigestWarningTests|FullyQualifiedName~DigestBuilderOffloadWarningTests|FullyQualifiedName~DigestBuilderGoldenTests|FullyQualifiedName~DigestBuilderTests"`):
- `OffloadDigestWarningTests`: `Line_NothingParkedLastRunOk_Null`; `Line_TwoParked_TwoItems`; `Line_LastRunFailedNoneParked_OneItem` (A12); `Line_Exact_Text`.
- `DigestBuilderOffloadWarningTests` (AC-23, AC-28 U): `Identity_WithWarning_EqualsGolden`; `Identity_NoWarning_ByteIdenticalToExistingGolden`; `Identity_WithWarningAndClaudeMd_BothInOrder`; `Identity_WarningCountsTowardCap` (a profile filling the cap → the truncation marker accounts for the line); `Index_AfterOffloadedSidecar_ByteIdentical` (a tree with an offloaded sidecar → the existing index golden); `Daily_Unchanged`; `Index_NeverContainsArchivePathOrUrl`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ shared contract: the identity digest gains one conditional line (OQ-2); public `DigestBuilder.IdentityWarnings` (additive public API).

**VERIFY**: failing-run command passes; existing digest goldens unchanged (`git diff tests/golden/digest` shows only the new folder); build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`/`ArchiveStatePaths`, no static mutable state, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — End to end: after an offload, one `zyggy dream` run through fake-claude files the `Moved …` line into `business/areas/zyggy.md` in one pushed `dream` commit that touches no archive path, with the dream's code unchanged; `zyggy memory digest index` lists the project file within its cap and shows no archive path, sidecar or URL; `zyggy memory digest identity` from the binary shows the `[warning]` line after a run with a parked item and drops it after `--retry` succeeds

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tools/fake-claude/scenarios/dream-offload-ok.jsonl` *(create, hand-written; `{today}` materialised by `MemoryRepoFixture.MaterialiseScenario` as 37 Step 9)*: `dispositions: [{L1, filed, business/areas/zyggy.md}]`, `edits: [{path: business/areas/zyggy.md, append: ["- [observed] {today} [archive offload {today}]: Moved archive/zyggy/quote-2026.txt to OneDrive Zyggy/archive/zyggy/quote-2026.txt"]}]`.
- `tests/Zyggy.Integration/Dream/DreamAfterOffloadTests.cs` *(create)*; `tests/Zyggy.Integration/Memory/MemoryDigestCommandTests.cs` *(modify: two facts)*.
- Production fixes only if exposed — and **none** in `src/Zyggy.Core/Dream/**` (AC-26 "unchanged code"; a needed change there stops the step and goes to the gate).

**Seams**: real everything (fake-claude through the real `ClaudeCodeCliRunner`, real git, bare remote, CLI); offload in process over the stub OneDrive (`OffloadHarness`).

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~DreamAfterOffloadTests|FullyQualifiedName~MemoryDigestCommandTests|FullyQualifiedName~DreamArchiveTests"`):
- `DreamAfterOffloadTests` (AC-26 I, AC-28 I): `Dream_AfterOffload_FilesMovedLineOneCommitNoArchivePath` (exit 0; one `dream <today>` commit after the `archive offload` commit; its paths are the project file and `.dream/ledger.json` only); `Dream_AfterOffload_SnapshotHasNoItemForOffloadedPath` (stdin capture contains the inbox line, not the sidecar text); `Dream_AfterOffload_ArgumentsStillDenyArchiveRead` (`--disallowedTools` contains `Read(//…/archive/**)`); `Digest_IndexAfterOffloadAndDream_ProjectFileListedNoArchivePathNoUrl` (≤ 6,000 bytes, contains `- business/areas/zyggy.md — `, contains no `archive/`, no `Zyggy/archive`, no `https://`). `DreamArchiveTests` (37) green unedited.
- `MemoryDigestCommandTests` (AC-23 I): `Digest_Identity_ParkedItemInState_WarningLine`; `Digest_Identity_StateCleared_NoWarningByteIdentical`.

**GREEN**: the scenario file and the facts; fixes outside `Dream/` only.

**Contract impact**: `tools/fake-claude` gains one scenario (README row).

**VERIFY**: failing-run command passes on Windows; CI green on both runners; `git diff --stat src/Zyggy.Core/Dream` empty for the whole deliverable; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`/`MemoryPaths`, no static mutable state, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D (the session sees it, the dream files it) *(covers Steps 9–10)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: the identity digest with and without the warning (binary output); `git log --stat` of the bare repo (offload commit, then the dream commit touching only the project file and the ledger); the index digest bytes and its absence of archive paths/URLs; `git diff --stat src/Zyggy.Core/Dream` empty.
- [x] Contract review: AC-23, AC-26, AC-28 against the spec; **A12** (how "the last run failed" counts), **A13** (warning placement); the fake-claude scenario's filed line form (`[observed] D [archive offload D]: …`) — see Gap 2 in the hand-off.
- [x] ⚠️ Risk review (shared contracts): the `index` and `daily` sections byte-identical; the identity section only gains the conditional line; `DreamChecks`, `MemorySnapshot` and the prompts untouched.
- [x] User approved — implementation may continue past this gate

---

## Step 11 — The template denies the offload to the model and tells the session what the offload is and how to retrieve an offloaded item (text/PDF through `zyggy m365 parse`, an image opened with Read from the download directory and deleted); the instance carries the `offload` block (disabled), the oneshot `zyggy-archive-offload.service` without a timer or `[Install]`, and runbook entries; 0002 gains a §38 skeleton — nothing on Central changes yet, nothing merged into this repository's `main` while 37's final gate is open

- [x] Done — 2026-10-11 (executor notes: uncommitted on `feature/38-archive-offload` in zyggy-core and zyggy-geoffrey, no PR/CI per the owner's review-before-commit rule; template bats 178/178 + shellcheck in podman; instance bats 180/180 in podman (instance rows in a new `tests/instance.bats`); this repo green)

**Scope**:
- `d:\source\zyggy-core` (template; branch + PR, CI green; merge per the 37 Step 10 rule — template into the instance only after the release pin of Step 12):
  - `.claude/rules/security.md` *(modify; AC-32)*: the "Microsoft 365" section gains the spec's bullet after "You have no tool that creates …" verbatim; the downloads bullet gains the OQ-6 image exception verbatim; "Never send, post or publish" unchanged.
  - `.claude/rules/memory.md` *(modify; AC-32)*: the archive rows gain the spec's "After an offload …" sentence verbatim.
  - `.claude/skills/m365/SKILL.md` *(modify)*: the retrieval steps for an offloaded item (download by `drive_item_id` into the session download directory; text/PDF → `zyggy m365 parse`; image → Read, then delete; otherwise give the owner `drive_url`).
  - `.claude/settings.json` *(modify; AC-31)*: `permissions.deny` += `Bash(zyggy memory archive offload*)`; 37's two allow rules unchanged; `mcp__m365__upload-file-content` still excluded and denied.
  - `.claude/zyggy-min-version` → the release of Step 12 (Assumption A14); `tests/repo.bats` *(modify)*: one row per sentence above, the deny rule, the unchanged allow rules, the upload tool still denied, the min version.
- `d:\source\zyggy-geoffrey` (instance):
  - `instance/archive.json` *(modify; AC-33)*: `"offload": {"enabled": false, "root_folder": "Zyggy/archive", "min_bytes": 0, "max_items_per_run": 20, "max_bytes_per_run": 209715200, "max_failures": 3, "exclude": []}` (`enabled` flips to `true` only in Step 13).
  - `instance/systemd/zyggy-archive-offload.service` *(create; AC-34)*: `Type=oneshot`, `User=zyggy`, `Group=zyggy`, the dream unit's `Environment=` (incl. `ZYGGY_HOOKS=off`), `LoadCredential=m365-app-key:…` and `m365-app-cer:…` + `Environment=ZYGGY_M365_CER_FILE=%d/m365-app-cer` as the brief unit, `InaccessiblePaths=` `~/.config/zyggy` `~/.cache/zyggy`, `ReadWritePaths=` memory root and `~/.local/state/zyggy`, the brief unit's hardening lines, `ExecStart=/usr/local/bin/zyggy memory archive offload`, `TimeoutStartSec=60min`; **no `[Install]`, no timer**; header comment: started only by `OnSuccess=` of the dream unit (added in Step 14).
  - `zyggy-dream.service`: **unchanged in this step** (its `OnSuccess=` line lands in Step 14, after AC-37).
  - `tests/repo.bats` *(modify)*: rows: the `offload` block's keys and ranges (`jq`), `enabled` false here and absent from the template; the offload unit's required lines and the absence of `[Install]`; the dream unit has no `LoadCredential=`.
  - `instance/runbooks/central-claude-config.md` *(modify; AC-35)*: section 17 gains: "Offload upload failed" (one paragraph per `OffloadReason`; `--retry`; `offload.exclude`), "Offload root folder", "Grant OneDrive write for the archive folder" (admin consent for `ListItems.SelectedOperations.Selected`; `POST …/items/<Zyggy/archive id>/permissions` role `write` for the app; the AC-42 check in Graph Explorer; the AC-37 stop rule; 13-D7b **not** run), "Turn the offload off" (`offload.enabled: false`; revoke the folder grant), "Restore an offloaded item into the archive" (`git revert <offload sha>` + `offload.exclude`; after a history rewrite: download, `archive remove`, `archive add`), "Remove an offloaded item (both copies)", "Rewrite memory history to drop offloaded bytes" (points to 17j), "Reconcile offload uploads with the audit log" (`FileUploaded` by the app id vs `archive-offload-*` rows; a mismatch → 13 "Revoke the application credential"), "Run the offload by hand" (`sudo -u zyggy` with the unit's environment for `--dry-run`/`--probe`/`--retry`; `systemctl start zyggy-archive-offload` for a plain run). Tags as section 17.
- This repo: `_plans/decisions/0002-central-productive.md` *(modify)*: `## 38 — Archive offload to OneDrive` skeleton (spec/plan links, evidence table AC-37..AC-42 empty, the Decisions log OQ-1..OQ-6 summarised, W38-1..W38-9 status "proposed — awaiting the owner", accepted-risk paragraph: QuickXorHash is non-cryptographic; repository size not reduced until a history rewrite; the offload unit can read the memory deploy key (spec AC-34)).

**Seams**: none (repositories and CI).

**RED**: `zyggy-core` and instance `repo.bats` new rows fail before the files change (bats in podman/WSL as 37 Step 10). This repo: `dotnet test Zyggy.slnx` stays green.

**GREEN**: as Scope; bats 0 failures; template CI green; instance CI green (or bats in podman with the reason recorded, as 37).

**Contract impact**: template rules/skill/settings (Contracts "Template wording"); instance configuration and unit (Contracts "Instance unit").

**VERIFY**: `zyggy-core` CI run id; instance CI run id or podman bats count; the runbook entries checked off against AC-35 in the step summary; this repo build/test/format green.

**REFACTOR** *(executor)*: LF, shellcheck-clean; no code in this repo changes.

---

## 🛑 HUMAN GATE — end of Slice E, and **dependency gate: 37's final gate must be checked before Step 12** *(covers Step 11)*

*Executor: STOP here. Present the results and WAIT. Do not start Step 12 until **both** this gate is approved **and** the last gate of `_plans/37-project-archive-memory.md` ("definition of done for deliverable 37") is checked `[x]`.*

- [x] Behavioral verification: template bats and both CI run ids (or the podman bats count); the offload unit file and the `offload` block shown; runbook entries listed against AC-35.
- [x] Contract review: the template wording equals the spec's text verbatim; deny rule present, 37 allow rules unchanged, the upload tool still denied; AC-31..AC-35 against the spec; 0002 §38 skeleton (AC-36: Decisions log reflected, 0002 opened).
- [x] ⚠️ Risk review (work boundary, injection, credentials): the model has no path to run the offload; the image rule keeps "data, never instructions" and "delete after answering"; the offload unit gets the Graph credential and the dream unit gets none.
- [x] **37's final gate is checked** (2026-10-11, gaps recorded in 0002 §37) (`_plans/37-project-archive-memory.md`: the three real items archived and indexed, sweep clean, 37 Done in the roadmap) — Steps 1–11 may now be merged and released.
- [x] User approved — implementation may continue past this gate

---

## Step 12 — *(depends on 37's final gate)* A tagged release exists with checksummed artefacts; on Central the pinned binary is installed root-owned, template and instance are merged and pulled, the offload unit is installed but nothing starts it, `offload.enabled` is still `false`, and a rehearsal shows `zyggy memory archive offload` exit 0 `disabled`, `--probe` reading drive and root (or exit 6 `root_missing` while the owner has not created the folder), `--dry-run` listing 37's indexed items, `list` unchanged, and `add` still refused under `ZYGGY_HOOKS=off`

- [x] Done — 2026-10-11 (v0.6.0 released and installed on Central, offload disabled; rehearsal: disabled exit 0, probe/dry-run exit 6 root_missing before the folder exists; 0002 §38 Release row)

**Scope** *(agent; the 37 Step 11 procedure, runbook 14a/14b)*:
- This repo: merge Steps 1–11 (PR, CI green), tag `v<next>` (the next minor — Assumption A14), tag CI green on both runners; release with `zyggy` + `SHA256SUMS`; `sha256sum -c` locally.
- Central (`az vm run-command`, binary by `scp` over Tailscale): `/opt/zyggy/<v>/zyggy` root:root 755, hash = `SHA256SUMS` = `instance/zyggy.json`; template + instance merged and pulled (`runuser -u zyggy -- git -C /srv/agent/central pull --ff-only`); `instance/zyggy.json` → `<v>`; symlink `/usr/local/bin/zyggy` → `<v>`; `install -m 0644 instance/systemd/zyggy-archive-offload.service /etc/systemd/system/` + `systemctl daemon-reload` + `systemd-analyze verify zyggy-archive-offload.service` (AC-34 C part); the dream unit untouched; `systemctl restart claude-remote` once (settings deny rule and rules read by the session).
- Rehearsal as user `zyggy` with the unit's environment: `zyggy memory archive offload` → exit 0 `offload: disabled (archive.json offload.enabled)`; with a scratch `ZYGGY_ARCHIVE_CONFIG` setting `enabled: true`: `--probe` (reads only; `drive: … root: … ok`, or exit 6 `root_missing` before the owner created the folder — both recorded), `--dry-run` (the indexed 37 items listed, no write: `actions.jsonl` unchanged, `git status` clean); `zyggy memory archive list` (37 rows unchanged); `ZYGGY_HOOKS=off zyggy memory archive add …` → exit 2 `unattended run`.
- `_plans/decisions/0002-central-productive.md`: Release row, install row, rehearsal rows.

**Seams**: the real Central; no `claude` run by the agent; no Graph write (probe and dry-run read only).

**RED**: `zyggy --version` on Central ≠ `<v>`; `zyggy memory archive offload` → `unknown verb`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ live binary able to write to OneDrive once enabled (still disabled).

**VERIFY**: symlink + pin = `<v>`; `claude-remote` active; `zyggy dream status` and the m365 probe/check as before; units unchanged except the new inactive offload unit; rehearsal outputs recorded; `actions.jsonl` has no `archive-offload-*` row.

**REFACTOR** *(executor)*: none (operations step); fix any runbook gap found, in section 17.

---

## Step 13 — *(depends on 37's final gate)* The owner creates `Zyggy/archive`, grants the app one `write` permission on that folder only and verifies it before enabling; then, with `offload.enabled: true` in the instance but no `OnSuccess=` on the dream unit, one manual run from a shell with the owner present creates the project folder, uploads one indexed 37 item, verifies it (AC-11) and removes its bytes from `origin/main` in one commit — or, by the stop rule, the offload stays disabled and the owner is asked to choose

- [x] Done — 2026-10-11 (folder grant 201; probe ok; attended run moved reading/audible-library-screenshot-1 in memory commit eb5f25b; 0002 §38 AC-37, AC-42)

**Scope** *(owner-run in the browser and Graph Explorer; agent runs the binary on Central and records; runbook "Grant OneDrive write for the archive folder")*:
- AC-37 (owner): create the folder `Zyggy/archive` in the OneDrive; admin consent for `ListItems.SelectedOperations.Selected`; `POST …/drives/<onedrive>/items/<Zyggy/archive id>/permissions` with role `write` for the app; Graph Explorer `GET …/permissions` of the OneDrive site (`read` only) and of `Zyggy/archive` (one `write` grant for the app) — outputs recorded with ids redacted to their first 6 characters.
- Agent: `zyggy memory archive offload --probe` (unit environment) passes; instance `archive.json` → `enabled: true` **and `max_items_per_run: 1`** (so each of the next runs moves exactly one of 37's indexed items and one stays in git for AC-40; restored to 20 at the end of Step 14 — Assumption A15) (commit, pull on Central); one manual run with the owner present (`systemctl start zyggy-archive-offload` or `sudo -u zyggy …`): exit 0; `git -C memory log origin/main -1 --stat` = one `archive offload <project>/<slug>.<ext>` commit removing the item path; the sidecar's offload keys; `actions.jsonl` one `ok` folder row + one `ok` upload row; the owner opens `drive_url`; `list` → `offloaded`.
- **Stop rule** (AC-37): the token lacks the new role, the folder grant is refused, or the probe or the run answers 403 on the write → `offload.enabled` back to `false` (commit, pull), nothing falls back automatically, the agent records the evidence and **stops at the gate** for the owner's choice (13-D7b site-wide `write`, or no offload) — that choice is outside this plan.
- AC-42 (owner, after the run): Graph Explorer lists the app's permissions on the site and on `Zyggy/archive`: exactly one `write` grant (on `Zyggy/archive`) and the existing `read` grants; recorded.
- `_plans/decisions/0002-central-productive.md`: AC-37 and AC-42 rows.

**Seams**: the real Central, the real tenant (owner-driven), one real Graph write pair.

**RED**: 0002 §38 rows AC-37/AC-42 empty; no `archive-offload-*` row in `actions.jsonl`.

**GREEN**: rows filled from records; a failure is reported at the gate with the record (no code fix here — a fix is a bugfix or a new plan step).

**Contract impact**: ⚠️ first live write to Microsoft 365 by the application identity; first `write` grant in the tenant.

**VERIFY**: AC-37 evidence complete (consent, two permission listings, probe, the manual run's commit, rows, `drive_url` opened) or the stop rule recorded; AC-42 listing.

**REFACTOR** *(executor)*: none.

---

## 🛑 HUMAN GATE — first live write verified, ready to run unattended *(covers Steps 12–13)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: release row (tag, CI ids, SHA-256); `systemd-analyze verify` output; rehearsal outputs; the manual run's stdout, commit (`git show --stat`), sidecar keys, two `ok` rows; the owner opened `drive_url`.
- [ ] Contract review: AC-34 (C part), AC-37, AC-42 against the spec; the grant listings show one folder-scoped `write` and nothing site-wide.
- [ ] ⚠️ Risk review (first unattended write ahead; credentials/grant): a key holder can write only inside `Zyggy/archive`; the dream unit still has no credential and no `OnSuccess=` yet; if the stop rule fired, the offload is disabled and the owner's choice is recorded as outside this plan.
- [ ] User approved — implementation may continue past this gate

---

## Step 14 — *(depends on 37's final gate)* With `OnSuccess=zyggy-archive-offload.service` added to the dream unit, the first nightly run with no owner present moves one real indexed item and the tenant audit matches its rows; a session the next day finds it through the digest, the sidecar and the m365 read tools; a forced failure (root folder renamed for one run) leaves every item in git, fails the unit and shows the `[warning]` line, and the next run after the rename back clears it; the secret sweep is clean; 0002 §38 and the roadmap record it

- [ ] Done

**Scope** *(agent via `az vm run-command`, read-mostly; owner in the session and the tenant)*:
- Instance: `zyggy-dream.service` gains `OnSuccess=zyggy-archive-offload.service` (its only change; no credential) + bats row; commit, pull, install, `daemon-reload`, `systemd-analyze verify` (AC-34).
- AC-38 (after the next nightly dream): `journalctl -u zyggy-archive-offload` shows one item moved; `list` → `offloaded`; `git log origin/main` one `archive offload` commit removing the item path; one `ok` row per write; the owner's tenant audit (`FileUploaded` by the app id, plus the folder creation if any) matches the rows (recorded counts).
- AC-39 (owner, session the next day): asks for the item; the session finds the project line in the digest, reads the sidecar, reads the file through the m365 read tools (a PDF or text via `zyggy m365 parse`; an image downloaded from `Zyggy/archive/`, opened with Read, then deleted — OQ-6); the owner opens `drive_url`.
- AC-40: the owner renames `Zyggy/archive` for one run (or the agent sets `offload.root_folder` to a missing folder in a scratch config for one `systemctl start` — the owner chooses); the unit exits 6, `systemctl is-failed` → `failed`, every item still in git, `git status` clean; the next session's identity digest shows the `[warning]` line; folder renamed back; the next run succeeds and the warning is gone.
- After AC-40: instance `archive.json` `max_items_per_run` back to 20 (commit, pull); the remaining indexed items move on the following nights (recorded, not awaited).
- AC-41: secret sweep — 37 AC-30 scope plus `~/.local/state/zyggy/archive/`, `journalctl -u zyggy-archive-offload`, and `ps -eo args` sampled during a run: no hit; no token or key anywhere; recorded.
- AC-36 (read-only check): W38-1..W38-9 pasted into `_specs/00 …` by the owner or the orchestrator (W38-5 only after AC-37) — reported as present or absent; this plan never pastes them.
- `_plans/decisions/0002-central-productive.md`: AC-38..AC-41 rows dated and sourced; P0b checklist row for 38's clause (gate wording per OQ-4).
- `_plans/ROADMAP.md` *(modify — status only, as 37 Step 12)*: 38 → Done (date, commits, CI run ids); the P0b clause recorded as met.

**Seams**: the real Central, the real nightly dream and offload unit (never run unattended by the agent), the owner's session.

**RED**: 0002 §38 rows AC-38..AC-41 empty; the dream unit has no `OnSuccess=`.

**GREEN**: rows filled from records; any AC not met is reported at the gate with the record.

**Contract impact**: ⚠️ first unattended write to Microsoft 365 (decision 1).

**VERIFY**: AC-38 (commit, rows, audit match), AC-39 (session transcript excerpt by the owner), AC-40 (exit 6, failed unit, warning shown then gone), AC-41 (zero hits), AC-36 (W38 status).

**REFACTOR** *(executor)*: none.

---

## 🛑 HUMAN GATE — end of Slice F — **definition of done for deliverable 38** *(covers Step 14)*

*Executor: STOP here. Present the results and WAIT for user approval.*

- [ ] Behavioral verification: 0002 §38 with every AC-37..AC-42 row dated and sourced; the unattended `archive offload` commit and its rows; the audit reconciliation counts; the forced failure's exit, unit state and digest warning; sweep count 0.
- [ ] Contract review: AC-1..AC-42 → step map below all ticked; `ROADMAP.md` #38 definition of done ticked item by item, noting the spec's reshapes (folder grant instead of 13-D7b; size + QuickXorHash instead of a post-upload SHA-256; runbook "Grant OneDrive write for the archive folder" instead of "Re-grant OneDrive write (13-D7b)"); W38-1..W38-9 status.
- [ ] ⚠️ Risk review: first unattended M365 write is narrow (two operations, folder-scoped in code and by the grant), logged and reconciled; data protection (documents in the company tenant; erasure needs OneDrive + history rewrite — runbook); repository size not reduced until a history rewrite (decision 4); no secret anywhere.
- [ ] User approved — deliverable 38 is done

---

## Acceptance-criteria → step map

| AC | Steps | AC | Steps | AC | Steps |
|----|-------|----|-------|----|-------|
| AC-1 | 4, 5 | AC-15 | 2 (row), 5, 6, 8 | AC-29 | 5 |
| AC-2 | 4 | AC-16 | 6, 8 | AC-30 | 5, 8, 14 (sweep) |
| AC-3 | 4, 7, 8 | AC-17 | 6, 8 | AC-31 | 11 |
| AC-4 | 7, 8 | AC-18 | 5 | AC-32 | 11 |
| AC-5 | 2 | AC-19 | 3, 6 | AC-33 | 11, 12 (dry-run with the instance config), 13 (`enabled: true`) |
| AC-6 | 1 (resolution), 2 | AC-20 | 3, 6 | AC-34 | 11 (unit), 12 (verify), 14 (`OnSuccess=`) |
| AC-7 | 1 (reads), 6 | AC-21 | 3, 7 | AC-34a | 7, 8, 11 (runbook) |
| AC-8 | 5 | AC-22 | 6, 7 | AC-35 | 11 (reviewed at the Step 11 gate) |
| AC-9 | 5, 6 | AC-23 | 9, 10 | AC-36 | 11 (Decisions log, 0002 opened), 14 (W38 status) |
| AC-10 | 2, 5 | AC-24 | 3, 8 | AC-37 | 13 |
| AC-11 | 1 (read), 5, 6 | AC-25 | 3 | AC-38 | 14 |
| AC-12 | 1 | AC-26 | 10 | AC-39 | 14 |
| AC-13 | 3, 5 | AC-27 | 3 | AC-40 | 14 |
| AC-14 | 5, 8 | AC-28 | 9, 10 | AC-41 | 14 |
| | | | | AC-42 | 13 |

---

## Assumptions (where the spec is silent or leaves a shape to the planner; each is reviewed at the named gate)

1. **A1 *(Gate A)* — the writer mints its own token** through the shared `GraphTokenClient` (one re-mint on 401, as the reader): at most two token POSTs per run; `GraphReader` keeps its call path unchanged.
2. **A2 *(Gate A)* — folder creation** is `POST /drives/{d}/items/{root}/children` with `@microsoft.graph.conflictBehavior: "fail"` in the JSON body (Graph's documented form for creating a folder); the file upload carries it as the query parameter. `DriveWriteScope` checks the request record's conflict behaviour for both.
3. **A3 *(Gate C, D)* — the inbox line** goes through `RememberService` with tag `observed`, hint ` (project:<p>) (source: archive offload)` and an empty source, which yields exactly the AC-18 bytes in `inbox/remember-<date>.md`. That line has no bracketed provenance, so `MemoryLine` does not parse it as `[observed]` (as `PostFact`'s line); `BatchPlanner` still classes it `ObservedInbox`. Step 10 proves filing with fake-claude only (see Gap 2).
4. **A4 *(Gate C)* — commit failure after `git rm`** restores the item with `git checkout HEAD -- <item>` (`GitClient.CheckoutPathsAsync`), so the working tree again holds the bytes while the sidecar keeps the offload part; the next run takes the AC-17 resume path.
5. **A5 *(Gate C)* — a deferred push stops the run** after that item (exit 7), so unpushed offload commits never pile up; the next `archive …` or `dream` preflight pushes it first.
6. **A6 *(Gate C)* — exit precedence** 6 > 7 > 5 > 0, and an item parked in an earlier run keeps every later run at exit 5 (the unit shows `failed` until `--retry` or `offload.exclude`) — the reading of AC-22 "a run with at least one failed or parked item".
7. **A7 *(Gate B)* — `list` forms**: text `offloaded → <drive_path>`, suffixes `offload_failed(<n>): <reason>` / `parked: <reason>` after the 37 state; JSON keys `drive_path`, `offload_failures`, `offload_reason`, `parked` only on rows that have them, so every 37 row stays byte-identical.
8. **A8 *(Gate B)* — state file shape** `{"items":{"<p>/<slug>":{"failures","reason","parked","since"}},"last_run":{"at","failed","reason"}}`, 0600 in a 0700 `<state>/archive/`.
9. **A9 *(Gate A)* — blocked file type** is recognised from the Graph error code/message of a 4xx (`typeNotAllowed`, `fileTypeBlocked`, or a message naming the file type/extension); any other 4xx is a permanent `write_refused`-class park with the code as detail. The real error shape is unverified (spec Risk "Unverified platform behaviour").
10. **A10 *(Gate A)* — QuickXorHash vectors**: the expected values are taken from an implementation independent of the code under test (the published test vectors of rclone's `quickxorhash` package, MIT, plus values for the size fixtures computed once with an independent port) and recorded with their source in `tests/golden/archive/offload/README.md` (see Gap 1).
11. **A11 *(Gate B)* — byte cap**: candidates are taken in ordinal order while the cumulative size stays within `max_bytes_per_run`; the first candidate of a run is always taken so a single item larger than the cap still moves alone.
12. **A12 *(Gate D)* — warning count**: `<n>` = parked items; a failed last run with no parked item shows `1 item(s)`.
13. **A13 *(Gate D)* — warning placement**: after the CLAUDE.md warning (when present), before the "The lines below are the owner's memory…" line; not echoed to stderr.
14. **A14 *(Gate after Step 13)* — release number**: the next minor after the version live on Central (a new sub-verb and template deny rule), and the template's `zyggy-min-version` bumped to it.
15. **A15 *(Gate after Step 13)* — one item per run on Central during the live checks**: AC-37 (attended) and AC-38 (unattended) each move "one" item and AC-40 needs an item still in git, while 37 Step 12 leaves three indexed items and the default cap is 20; so `max_items_per_run` is 1 in the instance from Step 13 until AC-40 has passed, then back to 20.

## Notes for the executor

1. **Branch discipline.** Steps 1–11 are built on a branch; nothing is merged into `main`, tagged or released until 37's final gate is checked (the gate after Step 11). The owner reviews before commit (auto-memory): leave changes uncommitted per repository and report, unless the owner says otherwise.
2. **Shared-contract edits** (`ArchiveSidecar`, `ArchiveService.List`/`RemoveAsync`, `ArchiveListFormat`, `ArchiveConfiguration`, `ArchiveVerb`, `GraphReader`, `ActionLog`, `DigestBuilder`, `StubGraphHandler`): run the whole existing suite before the edit; after GREEN, `git diff` the named test files and goldens to prove "additions only".
3. **Goldens** by hand with byte-preserving tools (`[System.IO.File]::WriteAllBytes`); `git ls-files --eol tests/golden` → `i/-text`. QuickXorHash values never from `QuickXorHash.cs`; `StubOneDrive` never calls it.
4. **`src/Zyggy.Core/Dream/**` is not touched** by this deliverable (AC-26). If Step 10 needs a dream change, stop at the gate.
5. **Central** as plan 28/37: `runuser -u zyggy -- …`; never run `claude`; never enable the offload or add `OnSuccess=` before the step that says so; the owner does the tenant actions.
6. **Do not edit** `_specs/00 …`, `_specs/38-…`, genome files, or `zyggy-core`/`zyggy-geoffrey` outside Steps 11/13/14's Scope; the roadmap only in Step 14 (status).
