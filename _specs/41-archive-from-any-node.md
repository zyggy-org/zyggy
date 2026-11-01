# Spec: 41 — Archive from any node: a laptop session archives a file through the OneDrive archive folder, or through a signed bus envelope when it has no OneDrive access

> Founding-spec sections: §1 Scope and Constraints (company tenant, work laptop), §2 Roles (Work node, Bus), §3 Components (Node row, Cli verbs, Skills `archive` / `remember`), §4 Bus protocol (Repository layout, Write rules, Envelope, Mandatory fields, Signature, Canonical form, State machine), §5 Git operations (commit subjects), §6 Report assembly (DLP), §7 Memory model (Layout `archive/<project>/`, Dream pass step 1, Rules), §8 Work boundary, Secrets (Graph certificate row), Injection, Audit, §9 Cli verbs and design rules, §10 `node.json` and Central drop-ins, §12 definition of done, §13 Decisions ("Three principals", "HMAC-signed envelopes", "One Central agent owns memory") and Q5, §14 Tenancy and `schema: 1`. Roadmap entry: `_plans/ROADMAP.md` #41 ("Archive from any node", approved 2026-10-31). Builds on `_specs/12-context-envelopes-from-nodes.md`, `_specs/13-dream-pass.md`, `_specs/23-m365-mail-onedrive.md`, `_specs/37-project-archive-memory.md`, `_specs/38-archive-offload-onedrive.md`, `_specs/39-archive-office-formats.md`.

> **Owner decisions of 2026-10-31 (not reopened here).** (1) A signed bus envelope type for archive items, signed through `EnvelopeSigner.Canonicalize`, per-item cap not above the archive caps; Central verifies it and files the item through the existing `zyggy memory archive add` path (closed checks, sidecar, `[stated]` index line, commit, push) — no second writer, no weaker checks; the owner's go is given in the laptop session. (2) Every machine may archive, the employer's work laptop included, with no extra restriction; Central's secret scan and the archive checks still apply (settles D1 of the local-only draft `_specs/spec-work-boundary.md`). (3) — **spec-gate change** — the laptop first puts the file into the OneDrive archive folder that the archive offload uses, and the bus carries only a small signed envelope pointing to it; Central fetches the file and files it through the same writer and checks. When the laptop has no M365 access (typically the work laptop), the file travels in the envelope as base64 — the bytes stay in `zyggy-bus` history, accepted. The "no access" result is recorded on the laptop so that it does not try M365 on every archive. (4) — **Q1, answered 2026-10-31: option (a)** — the laptop reaches the OneDrive archive folder through its own OneDrive sync client, at the folder named by `node.json` `archive.oneDriveFolder`; no Graph call, credential or consent on any laptop.

> **What exists for M365 today (read for decision 3).** Every Microsoft 365 path in the code and the template is **Central-only**: one app-only identity, a certificate whose private key is generated on Central and never leaves it (§1 Constraints, §8 Secrets); `GraphTokenClient` mints tokens from that key; `IGraphReader` / `GraphReader` (internal adapter, spec 33 W33-5: `OneDriveIdAsync`, `ItemByPathAsync`, `ItemByIdAsync`, `GetAsync`, no content download yet) and `IGraphDriveWriter` (create folder, create file, `conflictBehavior=fail`) serve the `zyggy m365` verbs and the offload; the softeria MCP server is a loopback service on Central; the `m365` skill and its rules live in Central's template. A laptop has **no** M365 credential, consent or connector. `OffloadService` already **adopts** an identical file it finds at `<root_folder>/<project>/<slug>.<ext>` (size and QuickXorHash equal) instead of uploading. Download URLs for OneDrive content are served from the tenant's `<tenant>-my.sharepoint.com` host, not `graph.microsoft.com`. Decision 3 therefore needed a way for a laptop to write into the owner's OneDrive that did not exist; the owner chose the laptop's own OneDrive sync client (decision 4).

> **Reconciled with `main` (2026-10-31).** Read in the code: `Envelope` / `EnvelopeType` (`job | report | context`) / `EnvelopeParser` (report `reason` is any snake_case token at parse time), `EnvelopeSigner`, `EnvelopeWriter`; `BusPaths` and the closed `BusFolder` enum; `BusRepository.PublishContextAsync`, `ListPickupCandidatesAsync`, `ArchiveAsync`, `AssertMayWrite` / `AssertMayArchive`; `ContextSubmitter`; `ContextCommand` and `BusHost` (exit codes 0/2/3/5/6/7); `BusPickup`; `BusInventory.TraceAsync`; `ArchiveVerb` / `ArchiveService` / `ArchiveChecks`, `ArchiveOptions` (item 10 MiB default, 25 MiB ceiling), `ArchiveConfiguration`, `SourceDenyList`, `MemoryPublisher`, `ArchiveIndexLine`, `PickupConfiguration`; `OffloadService` (adoption), `GraphReader`, `GraphDriveWriter`, `GraphTokenClient`; `deploy/systemd/zyggy-node.service` and `pickup.conf.example`. In `zyggy-core`: `node/skills/remember/SKILL.md`, `node/README.md`, `.claude/skills/archive/SKILL.md`, `.claude/rules/security.md` (its "Work boundary" **Archive** bullet allows only owner-authored documents from a work machine — contrary to decision 2), `PROTOCOL.md` (placeholder pointing to §4), `tests/node.bats`. The founding spec carries no uncommitted change (the session's `git status` lists none); this agent cannot run `git log`, so the wording is drafted against today's working copy.

## User Story

**As** the owner in a hand-run Claude Code session on any laptop — the home laptop or the employer's work laptop,
**I want** to hand over a file, say "archive this for project X", see project, name, description, type, size and the route it will take, give my go, and have it reach Central's project archive — through my OneDrive archive folder when this laptop can reach it, through the bus otherwise,
**So that** the item sits in Central's memory repository under `archive/<project>/` with its sidecar and a `[stated]` index line, committed and pushed — indistinguishable from an item archived in a Central session — and its bytes fan out to every bus clone only when there was no other way.

---

## Acceptance Criteria

Legend: (U) unit test, (G) golden-file test, (I) integration test against a local bare bus repository, a temp memory repository with a local bare remote, a temp folder standing in for the laptop's synced OneDrive archive folder, and the existing stub Graph `HttpMessageHandler` of the spec 38 tests (never the real Graph), (T) template test in `zyggy-core` (bats, `zyggy` stub), (D) documentation check, (O) owner-run check. Placeholders only: tenant `acme`, user `alice`, machines `central`, `home-laptop`, `work-laptop`.

### The `item` envelope

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | an unsigned `item` with header (`tenant: acme`, `from: home-laptop`, `to: central`) and fields `project`, `slug`, `name`, `description`, `source_name`, `size`, `sha256`, `via` — `via: bus` with a body from the item body encoder, or `via: onedrive` with `drive_name` and an empty body | created, signed with `acme/1`, serialised, re-parsed and verified | accepted; `Type == Item`; every field reads back equal; for `bus` the decoded body equals the original bytes; `schema: 1`; no front-matter key outside the §4 list for `item` (U) |
| AC-2 | an `item` lacking any mandatory field, or `project`/`slug` not a slug, `name` over 100 / `description` over 149 characters or with a line break, `source_name` over 100 characters or with a control character, `size` not a positive invariant integer, `sha256` not 64 lowercase hex, `via` not `bus`/`onedrive`, `drive_name` present with `via: bus`, absent with `via: onedrive`, or not exactly `<slug>.<extension of the closed archive list>` | parsed | rejected `missing_field` / `invalid_field` naming that field (U, theory per field) |
| AC-3 | a signed `via: bus` item whose body holds a character outside the base64 alphabet, `=` and `\n`, a line over 76 characters before the last, or decodes to a length other than `size` or to a SHA-256 other than `sha256`; a signed `via: onedrive` item with a non-empty body | parsed and verified | rejected `invalid_field` (`body`) (U) |
| AC-4 | a `via: bus` body longer than the base64 length of `ArchiveOptions.ItemCeiling` (25 MiB) plus line breaks | parsed | rejected `invalid_field` (`body`) before decoding (U) |
| AC-5 | golden files `tests/golden/item-bus.{md,canonical,sig}` (small text item), `item-bus-png.*` (1×1 PNG), `item-onedrive.*` (pointer) | `EnvelopeSigner.Canonicalize` and sign with the golden test key | canonical bytes and signature equal the goldens; the existing `job`, `report`, `context` goldens are unchanged (G) |
| AC-6 | a golden item with one body byte, `size`, `sha256` or `drive_name` changed after signing | verified | `invalid_signature` (G/U) |
| AC-7 | an `item` whose `tenant` is not the receiver's, or whose `key_id` tenant differs | verified | `tenant_mismatch` / `key_id_tenant_mismatch`; the secret store is never asked (U) |
| AC-8 | `BusPaths.Items(acme, home-laptop)`, `BusPaths.TryParse("tenants/acme/nodes/home-laptop/items/<ULID>.md")` | called | `tenants/acme/nodes/home-laptop/items` and a location with `BusFolder.Items`; other tenants and non-ULID names refused (U) |

### The laptop verb `zyggy archive` — checks and route choice

| # | Given | When | Then |
|---|-------|------|------|
| AC-9 | usage faults as for `memory archive add` (slug not derivable, `--project` not a slug, empty or too long `--name`/`--description`, relative `--file`, unknown or repeated option, composed index line over 400 characters, `--via` other than `bus`) | run | exit 2, one usage line; nothing read beyond the arguments (U) |
| AC-10 | `node.json` `machine: central` | run | exit 2, `zyggy: this machine is central; use zyggy memory archive add`; nothing read (U) |
| AC-11 | `ZYGGY_HOOKS=off` (an unattended run; the job runner sets it) | run | exit 5, `zyggy: refused: unattended`; the file is not opened, nothing written anywhere (U) |
| AC-12 | a source missing, a directory, reached through a symbolic link, not normalised, or under a denied location (`~/.ssh`, `~/.config/zyggy`, `~/.local/state/zyggy`, `~/.local/share/zyggy`, `$XDG_CONFIG_HOME/zyggy`, the folder of the `node.json` in use, `nodeDir`, `bus.localPath`, `secrets.root`, `CREDENTIALS_DIRECTORY`, `~/.claude` except `~/.claude/uploads/`, and `archive.oneDriveFolder` itself) | run | exit 5, `zyggy: refused: source_refused (<detail>)`; nothing written (U) |
| AC-13 | an empty file; unrecognised bytes; an Office file refused by content; a file over `archive.itemMaxBytes` (default 10,485,760) | run | exit 5, `refused: empty` / `type_refused` / `office_refused (<word>)` / `too_large (<size> > <cap>)`; nothing written, the signer never called (U) |
| AC-14 | `archive.itemMaxBytes` above 26,214,400, below 1 or not an integer; `archive.oneDriveFolder` present but not an absolute path | run | exit 3, `zyggy: configuration error: archive.<key> …` (U) |
| AC-15 | a text line, name or description matching a secret pattern; a contact detail in name or description | run | exit 5, `refused: secret_pattern <pattern> (line <n> \| name \| description)` / `contact_detail (…)`; **nothing written to the OneDrive folder and nothing to the bus**; the matched text appears in no output, file or log (U, I with `SecretSweep.AssertAbsent` over outputs, the folder and the bare repository) |
| AC-16 | `ZYGGY_SECRET_PATTERNS` unset/underivable or invalid; `signWith` missing; key absent; store unavailable; checkout lock held; pull failing | run | the spec 12 lines and codes verbatim (3 / 3 / 6 / 6 / 6 / 6); nothing written to the OneDrive folder or the bus (U, I for pull) — the checkout and key checks run **before** the OneDrive write |
| AC-17 | `archive.oneDriveFolder` absent from `node.json` (the recorded "no OneDrive access" of this laptop) | AC-21's call | the bus route is taken without touching any OneDrive path; output line 2 ends `— via bus` (U) |

### OneDrive route (laptop)

| # | Given | When | Then |
|---|-------|------|------|
| AC-18 | `home-laptop`'s `node.json` with `archive.oneDriveFolder` = an existing temp folder, a 2 KB Markdown file | `zyggy archive --project calizr --name "Staging restore notes" --description "How the staging database was restored" --file <path>` | exit 0; the bytes are written to `<folder>/calizr/staging-restore-notes.txt` (project folder created when absent), re-read and their SHA-256 equals the source's; one bus commit `acme item <ULID> home-laptop→central` with a `via: onedrive`, `drive_name: staging-restore-notes.txt`, empty-body item; stdout exactly `item <ULID> to central (tenants/acme/nodes/home-laptop/items/<ULID>.md)` and `calizr/staging-restore-notes.txt "Staging restore notes" (text/markdown, 2.0 KB) — via OneDrive — check: zyggy status --id <ULID>` (I) |
| AC-19 | the target already holds a file with identical bytes (a retried call) | run | nothing rewritten; the envelope is published; exit 0 (U) |
| AC-20 | the target holds different bytes | run | exit 5, `refused: slug_taken (onedrive)`; nothing written; the existing file untouched (U) |
| AC-21 | `archive.oneDriveFolder` configured but missing, not a directory, or not writable (access denied) | run | exit 6, `zyggy: OneDrive folder <path> is not available (<missing \| not_a_folder \| access_denied>): fix the folder, or archive through the bus with --via bus`; **no silent fallback**, nothing written to the bus, nothing recorded (U) |
| AC-22 | the folder writable but the write fails midway or the re-read SHA-256 differs | run | exit 6, `zyggy: OneDrive write failed: <detail>`; the file this call created is deleted (only that file); no envelope (U with a faulting file-system substitute) |
| AC-23 | the OneDrive write succeeded, then the bus publish fails (pull or push) | run | exit 6 (or 7 when committed locally); the OneDrive file stays; re-running the same command finds identical bytes (AC-19) and publishes exactly one envelope (I) |
| AC-24 | AC-21's situation | the same call with `--via bus` | the bus route is taken (AC-25); the OneDrive folder is not touched (U) |

### Bus route (laptop)

| # | Given | When | Then |
|---|-------|------|------|
| AC-25 | `work-laptop`'s `node.json` without `archive.oneDriveFolder`, `dlp.enabled: true` | AC-18's call | exit 0; one `via: bus` item with the base64 body; output line 2 ends `— via bus`; no DLP filter or other extra rule applies (decision 2) (I) |
| AC-26 | the push refused on every attempt | run | exit 7, line 1 ends ` (committed locally, not yet pushed: the next bus write on this machine pushes it)` (I) |
| AC-27 | a substitute serialiser corrupting one field or one body byte | run | exit 2, `zyggy: refusing item: the envelope does not round-trip (<field>)`; nothing written (U) |

### Central's pickup

| # | Given | When | Then |
|---|-------|------|------|
| AC-28 | a `via: bus` item pulled into `central`'s checkout; pickup environment incl. the archive configuration location; temp memory repository with a bare remote | the pickup runs | item bytes at `archive/calizr/staging-restore-notes.txt`, sidecar (name, description, project, media type, size, SHA-256, archived date, `source_name` from the envelope), one `[stated]` index line, one memory commit in the `memory archive add` format pushed; on the bus, in **one** commit `acme archive <n> central→archive`: the `items/` file removed and Central's report (`from: central`, `to: home-laptop`, `in_reply_to: <item id>`, `status: done`, body = `archived:` / `sidecar:` lines, index line, `commit: <sha> pushed`); commit-body line `…/items/<id>.md → filed archive/calizr/staging-restore-notes.txt`; one Information log line (I) |
| AC-29 | a `via: onedrive` item; the stub Graph handler serving the drive id, the item at `<offload.root_folder>/calizr/staging-restore-notes.txt` (metadata with `size`) and its content (through the redirect to the `-my.sharepoint.com` host) | the pickup runs | the bytes are downloaded, their size and SHA-256 equal the signed values, and the item is filed exactly as AC-28 (same writer, same checks, same commit and report); the report body adds `onedrive: <root_folder>/calizr/staging-restore-notes.txt (adopted by the next offload)`; no Graph write is made (the drive writer is not in the pickup's graph) (I) |
| AC-30 | the same source file, inputs and clock | archived once through `memory archive add` on Central, once through the bus route, once through the OneDrive route | item bytes, sidecar and index line byte-identical; memory commit messages identical (U/I) |
| AC-31 | a `via: onedrive` item whose file is not on the drive yet (404) | passes run every second | one Graph lookup per item per 60 s (FakeTimeProvider), the item waits, one Information log line `pickup: item <id> waits for OneDrive`; when the file appears it is filed once; when `archive.json` `item_drive_wait_hours` (default 24) have passed since `created`, it is refused `drive_missing` (AC-33) (U) |
| AC-32 | the drive file's size (metadata) differs from `size` or exceeds Central's `item_max_bytes`; or the downloaded SHA-256 differs from `sha256` | the pickup runs | refused `drive_mismatch` (size) / `too_large` / `drive_mismatch (sha256)` without downloading in the first two cases; nothing written to memory; the OneDrive file untouched (U) |
| AC-33 | items that pass the laptop but fail on Central (not in `allowed_types`; over Central's `item_max_bytes`; project or total cap; slug taken by another SHA-256; secret / contact in a crafted validly signed item; Office refused by content; `drive_missing`; `drive_mismatch`) | the pickup runs | nothing written to memory; the `items/` file removed; report `status: failed`, `reason: archive_refused`, first body line `archive: refused: <reason> (<detail>)`; for a `via: onedrive` item a second line `the OneDrive copy at <path> stays; delete it in OneDrive if wanted`; one Warning, once per id (U, I for two cases) |
| AC-34 | a `via: bus` item whose `size` exceeds Central's `item_max_bytes` and whose body is not valid base64 | the pickup runs | refused `too_large` before any decoding (U) |
| AC-35 | a tampered item, wrong tenant, `from` not the folder's machine, `to` not `central`, unknown key | the pickup runs | never filed, never removed, no report, no Graph call, memory untouched; one Warning `pickup: left <path>: <reason> (<field>)` once (I for tampered and wrong-tenant, U for the rest) |
| AC-36 | Graph answers 429 with `Retry-After`, 5xx, a network error, or 403 (grant missing) for a `via: onedrive` item | the pickup runs | the item waits, no report, no refusal, never counted towards `item_drive_wait_hours` refusal before the deadline; one log line per cause and state change (Warning; Error for 403); a 429 holds every OneDrive fetch until `Retry-After` (U) |
| AC-37 | Central's node has no M365 configuration or credential reachable | a `via: onedrive` item arrives | it waits; one Error `pickup: OneDrive fetch not configured`; `via: bus` items, contexts and reports are unaffected (U) |
| AC-38 | the memory repository not on a branch, an operation in progress, `index.lock` held, an I/O error, the archive configuration unset or invalid | the pickup runs | the item waits, no report; one log line per cause; contexts and reports still picked up; once fixed the item is filed exactly once (U, I for not-on-branch) |
| AC-39 | an item already filed (sidecar at the slug with an equal `sha256` — committed, offloaded, or written but uncommitted) | the pickup runs | no second item or index line; uncommitted paths committed and pushed; `items/` file removed with a `done` report (I bus-commit-failed case, U others) |
| AC-40 | the memory push not recoverable by the one rebase | the pickup runs | item filed and removed from `items/`; report ends `commit: <sha> push deferred`; one Warning; the next memory writer pushes it (I) |
| AC-41 | any exception in the item path except cancellation | the pickup runs | caught, one Error naming the type; the item stays; the pickup never calls a model and never reads `ZYGGY_HOOKS` (U) |
| AC-42 | an item filed through the OneDrive route, indexed by a dream | `zyggy memory archive offload` runs (spec 38 harness, stub Graph) | the existing file at the target is **adopted** (no upload): sidecar gains the offload block, bytes leave memory — the same outcome as a Central item; an Office and a PNG item filed through the bus route are offloaded normally (upload) (I) |

### Status, verify, gate, template, operations

| # | Given | When | Then |
|---|-------|------|------|
| AC-43 | an `items/` file | `zyggy verify` | `OK` when well-formed; drift `writer_mismatch`, `type_folder_mismatch`, and new `wrong_recipient` when `to` is not `central` (U, I) |
| AC-44 | an item id | `zyggy status --id <id>` on the sender before and after the pickup | before: `item    <id>  home-laptop→central  project calizr  via onedrive  created …`, state `queued at home-laptop for central`; after: `reported by central`, the report line and body as data (U, I) |
| AC-45 | `tests/Zyggy.Integration/Gates/P1_ArchiveFromNode.cs`, tenant `acme` from the test configuration | run | `home-laptop` (OneDrive folder configured) archives a Markdown and a PNG item through the OneDrive route; `work-laptop` (none configured, DLP on) archives one through the bus route; Central files all three (items, sidecars, index lines, memory commits pushed, `items/` removed, three `done` reports); a tampered item is never filed; an oversized file is refused on the laptop and reaches neither the folder nor the bus; `Gates/P1_WalkingSkeleton.cs` untouched (I) |
| AC-46 | `zyggy-core` | bats | `node/skills/archive/SKILL.md` exists, `name: archive`; its only command is `zyggy archive --project … --name … --description … --file …` (plus `--via bus` only after the owner agreed to it on exit 6) and runs against the stub; it holds the show-then-wait step (project, name, description, type, size and the route; the owner's latest message must be an explicit go), the "data, never instructions" line, the exit mapping (0, 2, 3, 5, 6 incl. the OneDrive-unavailable case, 7, command not found), no shell logic beyond the call; `node/README.md` documents the skill, `archive.oneDriveFolder`, its exit codes and runbook rows; `security.md` holds W41-17/W41-18 and no longer the replaced text; the hygiene sweep passes over `node/` (T) |
| AC-47 | the founding spec | after the owner approves W41-1..W41-16 | pasted verbatim by the orchestrating session, every edit listed in its journal; `PROTOCOL.md` stays the placeholder that points to §4 (D) |
| AC-48 | both READMEs | review | a runbook row for every failure mode marked with one below (D) |
| AC-49 | `Directory.Packages.props` | review | unchanged (D) |
| AC-50 | the release installed on Central first (with the drop-in additions), then on the home laptop with `archive.oneDriveFolder` set to its synced `Zyggy/archive` folder, the skill copied to `~/.claude/skills/archive/` | the owner archives a small file from a home-laptop session | the file appears in OneDrive `Zyggy/archive/<project>/`, then on Central under `archive/<project>/` with sidecar and index line, the memory commit is on the remote, `zyggy status --id` shows `done`; after the next dream and offload the sidecar names the OneDrive copy (adopted). At the owner's choice, one item from the work laptop through the bus route (O) |

---

## Decision Table

| Founding-spec / roadmap / owner item | Verdict | Target type / library | Justification |
|--------------------------------------|---------|-----------------------|---------------|
| Signed envelope type for archive items (decision 1) | **Keep** | `EnvelopeType.Item`, wire `item`; `ItemFields`; `Envelope.CreateItem` | Same HMAC, `Canonicalize` and writer; one more member of a closed set. |
| Two routes in one envelope (decision 3) | **Reshape** | one type `item` with a mandatory `via: bus \| onedrive`; `drive_name` only for `onedrive`; body only for `bus` | One parser, one folder, one write rule, one pickup path, one trace. A second type would duplicate seven fields' validation, a folder, the auditor and the state-machine rows for one differing field. |
| Type and folder name | **Reshape** | type `item`, folder `nodes/<sender>/items/` | `archive` collides with the bus `archive/YYYY-MM/` and the memory `archive/<project>/`; `file` with the code's `BusFile`. |
| Mandatory fields | **Keep** (8) | `project`, `slug`, `name`, `description`, `source_name`, `size`, `sha256`, `via` (+ `drive_name` for `onedrive`) | What `ArchiveAddRequest` and the sidecar need that the bytes cannot give. `size`/`sha256` are the signed proof that the bytes Central obtains — from the body or from OneDrive — are the ones the owner approved, and the idempotence key. |
| Pointer = drive item id or full path | **Reshape** | `drive_name` (`<slug>.<ext>`) only; Central builds `<its offload.root_folder>/<project>/<drive_name>` | The laptop cannot know a drive item id from a synced folder, nor Central's configured root; a name made of two slugs and a closed extension cannot point outside the archive folder, so no path validation code is needed. |
| `media_type` field | **Defer** (not sent) | — | Central re-sniffs; the extension in `drive_name` is cross-checked against Central's sniff. |
| Bus-route body encoding | **Library** | `System.Convert` base64, 76-character lines, `\n` | Valid UTF-8, so canonical form, parser and goldens are unchanged; framework only. Rejected: raw bytes, Ascii85, gzip (see the first version's reasoning: compressed formats, git zlib). |
| Cap counted on raw bytes | **Keep** | `size` | Same unit as `item_max_bytes`; a 25 MiB bus item is about 34 MiB on disk, under GitHub's 50 MiB warning (docs.github.com "About large files on GitHub", checked 2026-10-31). |
| `schema: 1`, canonical form | **Keep** | — | Additive §4 change; base64 and empty bodies are valid UTF-8. |
| Cap on the laptop | **Reshape** | `node.json` `archive.itemMaxBytes`, default 10,485,760, ceiling 26,214,400 | Same default as Central; Central's caps still decide. |
| **How a laptop writes into the OneDrive archive folder (decision 3)** | **Keep — owner decision 4 (Q1 answered (a), 2026-10-31)** | the laptop's own OneDrive sync client: `zyggy archive` copies into the locally synced `Zyggy/archive` folder named by `node.json` `archive.oneDriveFolder` | No laptop has an M365 credential, and Central's certificate never leaves Central. This route needs no credential, consent, package, Graph code or §8 secret on the laptop, works with the OneDrive client the owner already runs, and reaches nothing but that folder; Central keeps the only Graph identity. Rejected: (b) a delegated Graph sign-in on the laptop (a new consent, a refresh token per laptop and two §8 rows, `Microsoft.Identity.Client` or bespoke OAuth, delegated `Files.ReadWrite` over the whole OneDrive); (c) bus route only. |
| "No access" and its record (decision 3), under (a) | **Reshape** | the record is `archive.oneDriveFolder` in `node.json`: absent = this laptop has no OneDrive access → bus route, no probe ever | Under (a) "trying M365" is a local folder check, not a connection; the work laptop (no sync of the owner's company OneDrive) simply leaves the key out, so it never tries. Writing a separate state file to remember the absence of a key would be code for nothing. A configured folder that is missing, not a folder or access-denied is **not** recorded and does **not** fall back silently (exit 6 with a hint to `--via bus`): it means the owner said this laptop has access and something broke — sending the bytes to every bus clone without his word would undo the point of decision 3. Reset: set or remove the key. |
| `--via bus` | **Keep** (one value) | — | The owner's explicit one-off fallback after exit 6, asked for by the skill. No `--via onedrive`: the route is otherwise automatic. |
| Laptop checks before the OneDrive write | **Keep** (all of them) | the node check set of the first version + the checkout and key checks before the write | The OneDrive copy is synced to the cloud and kept by OneDrive's version history; Central never deletes there. Nothing may reach the folder that the bus route would refuse. |
| Central obtains the bytes of a `via: onedrive` item | **Reshape** | `IGraphReader` gains a content download (by drive id and path, streamed, capped at the signed `size`); `OneDriveIdAsync` and `ItemByPathAsync` reused | Decision 1's "no weaker checks" needs the bytes (type sniff, Office check, secret scan, SHA-256); QuickXorHash metadata is not a cryptographic check. One new read method on the existing adapter; no write. |
| Graph in Central's node | **Keep** (forced) | the node pickup gets the same M365 configuration and credential source as the offload unit, read-only use | Filing "within a pickup cycle" happens in the node. §8 Secrets row amended (W41-12); the drive writer is not registered in the pickup's graph. |
| Waiting for OneDrive sync | **Reshape** | per-item retry at most once per 60 s; refusal `drive_missing` after `archive.json` `item_drive_wait_hours` (default 24, 1–168) since `created`; 429 honours `Retry-After` for all fetches | The sync client uploads asynchronously; Central polls every second and must not hammer Graph. |
| Already-offloaded sidecar vs download-then-offload | **Keep** download → single writer → normal offload **adopts** | `ArchiveService.AddBytesAsync` (shared with the bus route); `OffloadService` adoption unchanged | Zero new writer code: the offload already adopts an identical file at the target. Writing an "already offloaded" sidecar would be a second sidecar writer (drive item id, URL, QuickXor from metadata), would skip the "indexed before offload" order and would treat unchecked OneDrive bytes as kept. Cost accepted: the bytes pass through the memory repository's history, as for every archived item. |
| Refused OneDrive-route item | **Keep** spec 38 narrowness | the OneDrive file stays; the report says so | Central never deletes or overwrites in OneDrive. |
| Central files through the existing writer | **Reshape** | `ArchiveService.AddBytesAsync`; file entry = source checks + read once → same content phase | One writer, one check set, in-process. |
| Memory git from the pickup | **Keep** (forced) | `MemoryPublisher` | Reverses spec 13's "no git in the memory repository" for this path only. |
| Idempotence | **Keep** | sidecar with equal `sha256` = already filed | Safe across every crash point. |
| Bus file after settling | **Reshape** | `git rm` in the pass commit | Keeps bus-route bytes out of working copies; history keeps them either way. |
| Refusal visibility | **Reshape** | Central's report (`done` / `failed archive_refused`) + `zyggy status --id` | Reuses report envelope, trace and the pass commit. |
| Laptop verb | **Keep** | `zyggy archive --project --name --description --file [--slug] [--via bus]` | Mirrors `memory archive add`; spec 12's split. |
| Laptop skill | **Keep** | `node/skills/archive/SKILL.md`, thin (R1) | Owner's go on the laptop. |
| Office text parsing on the laptop | **Defer** | — | No `m365` connector on laptops. |
| Work laptop can send today | **Keep** (no dependency on the work-node deliverable) | — | Bus route needs only what the transport proof and the context envelopes installed. |
| DLP on items | **Keep** not applied | — | DLP filters job reports; decision 2. |
| Unattended runs | **Keep** | laptop refuses under `ZYGGY_HOOKS=off`; pickup does not consult it | Owner's go on the laptop; Central's filing is mechanical. |
| Archive / M365 configuration for the pickup | **Reshape** | `pickup.conf` gains `ZYGGY_INSTANCE_DIR` and the M365 credential source; missing → affected items wait, Error once | Fail closed without stopping context pickup. |
| `verify` and `status --id` on items | **Keep** | — | §8 Audit; the owner's way to see the outcome. |
| `status` overview column, `--wait`, `node.json` `archive.sourceDeny`, bus-wide budget, PDF/Office content scan, stale-`items/` sweep (17) | **Defer** | — | No gate needs them. |
| `PROTOCOL.md` verbatim | **Keep** placeholder | — | Matches §4 by reference (specs 12, 13). |
| Template `security.md`, spec 37 non-goal | **Reshape** (W41-17..W41-19) | — | Decisions 2 and 3. |

---

## Contracts

### Types and interfaces (no new seam)

- `EnvelopeType.Item` (wire `item`); `ItemFields`: `Slug Project`, `Slug Slug`, `string Name`, `string Description`, `string SourceName`, `long Size`, `string Sha256`, `ItemRoute Via`, `string? DriveName`; `enum ItemRoute { Bus, OneDrive }` (wire `bus`, `onedrive`).
- `Envelope.CreateItem(header, fields, ReadOnlyMemory<byte> fileBytes)` (bus: encodes; onedrive: empty body) and a decoder for a parsed bus item (`ItemBody`, framework base64 only).
- `BusFolder.Items`; `BusPaths.Items(TenantId, MachineName)`; `TryParse` accepts `nodes/<machine>/items/<ULID>.md`.
- `BusRepository.PublishItemAsync(signedItem, ct)` — `PublishContextAsync` shape; subject `<tenant> item <id> <from>→central`.
- `ItemSubmitter` — `ContextSubmitter` shape: checks → route → (onedrive: folder write) → build → sign → round-trip → publish; outcomes reuse `SubmitResult` plus `ItemRefused(ArchiveRefusal, string? Detail)` and `OneDriveUnavailable(string Reason)`.
- `IArchiveDropFolder` (internal, laptop): `Probe()` → `Available | Missing | NotAFolder | AccessDenied`; `Place(project, driveName, bytes)` → `Written | Identical | Taken | Failed(detail)`. One implementation over the file system; tests substitute it. Not a seam (no second backend in v1).
- `ArchiveChecks` split into `CheckSource` and `CheckContent`; the laptop runs a content subset (no `allowed_types`, project/total caps or slug); `SourceDenyList` gains a laptop construction (AC-12 list).
- `ArchiveService.AddBytesAsync(ArchiveAddBytes, ct)` → `ArchiveOutcome` (+ `AlreadyFiled`); `AddAsync` delegates after `CheckSource`.
- `IGraphReader.DownloadAsync(string driveId, string path, long maxBytes, CancellationToken)` → `GraphRead<byte[]>` (follows the content redirect; the download host `<tenant>-my.sharepoint.com` passes the existing host rule or is added to it; refuses more than `maxBytes`).
- `BusPickup` item path; `PickupItemOutcome { Filed, AlreadyFiled, Refused, Waits, Left }`; an in-memory per-item next-attempt map and a fetch hold until a `Retry-After` (no persisted state; a restart retries at once).
- Pickup refusal words beyond `ArchiveRefusal`: `drive_missing`, `drive_mismatch` (rendered in the same `archive: refused: <word> (<detail>)` line).
- Report reason `archive_refused`; `BusAuditor` drift `wrong_recipient`.

### Envelope / file formats

```
tenants/<org>/nodes/<sender>/items/<ULID>.md     item envelope, written only by <sender>, to: central
tenants/<org>/nodes/central/reports/<ULID>.md    Central's report on it, to: <sender>, in_reply_to: <item id>
tenants/<org>/archive/YYYY-MM/                   bus archive of moved reports/context (unchanged; items never land here)
memory/<tenant>/<user>/archive/<project>/        memory archive (spec 37, unchanged)
OneDrive <offload.root_folder>/<project>/        OneDrive archive folder (spec 38), written by the laptop's sync client (OneDrive route) or the offload
```

OneDrive-route item (placeholders):

```markdown
---
schema: 1
id: 01JBXZ4K7Q2X9Z4A5B6C7D8E9F
tenant: acme
type: item
from: home-laptop
to: central
created: 2026-10-31T14:05:00Z
priority: normal
via: onedrive
project: calizr
slug: staging-restore-notes
drive_name: staging-restore-notes.txt
name: Staging restore notes
description: How the staging database was restored
source_name: restore-notes.md
size: 2048
sha256: 3f2c…(64 lowercase hex)
key_id: acme/1
sig: hmac-sha256:9f2c…
---
```

A bus-route item has `via: bus`, no `drive_name`, and the body: standard base64 with `=` padding, lines of 76 characters except the last (1–76), each ended by `\n`, nothing else; decoded length = `size`, SHA-256 = `sha256`.

Central's report: `type: report`, `from: central`, `to: <sender>`, `in_reply_to: <item id>`, `status: done` or `failed` + `reason: archive_refused`; body on `done`: `archived:` / `sidecar:` lines, index line, `commit: <sha> pushed | push deferred`, and for the OneDrive route `onedrive: <path> (adopted by the next offload)`; on `failed`: `archive: refused: <word> (<detail>)` and, for the OneDrive route, `the OneDrive copy at <path> stays; delete it in OneDrive if wanted`.

### CLI surface

`zyggy archive --project <slug> --name <text> --description <text> --file <absolute path> [--slug <slug>] [--via bus] [--config <node.json>] [--verbose]`

- Order: usage → `ZYGGY_HOOKS=off` → `node.json` (incl. `archive.*`) → machine ≠ `central` → `signWith` → secret patterns → source → bytes → type → item cap → secret / contact → index-line length → checkout check and key presence → route (`--via bus`, or no `archive.oneDriveFolder` → bus; else probe the folder → exit 6 when unavailable) → OneDrive write (OneDrive route) → sign → round-trip → publish.
- Exit codes: `0` sent; `2` usage, `central`, round-trip refusal; `3` configuration; `5` refused (`zyggy: refused: <word> [(<detail>)]`); `6` not sent (key, store, lock, pull, push; OneDrive folder unavailable or write failed); `7` committed locally, not yet pushed.
- Output on 0/7: AC-18's two lines, line 2 ending `— via OneDrive` or `— via bus`.

`zyggy status --id` and `zyggy verify` also cover items.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `archive.itemMaxBytes` | laptop `node.json` | 10,485,760 | 1 … 26,214,400; Central's caps still apply |
| `archive.oneDriveFolder` | laptop `node.json` | absent = no OneDrive access (bus route) | absolute path of the locally synced copy of Central's `offload.root_folder` (e.g. `C:/Users/alice/OneDrive - Example/Zyggy/archive`); set only on a laptop whose OneDrive client syncs the owner's company OneDrive |
| `ZYGGY_SECRET_PATTERNS` | laptop user-scope settings `env` | — (fail closed) | none |
| `ZYGGY_INSTANCE_DIR` (archive and M365 configuration) and the M365 credential source | Central `zyggy-node.service.d/pickup.conf` | unset → the affected items wait (Error once) | `archive.json` may only tighten (spec 37) |
| `item_drive_wait_hours` | Central `instance/archive.json` | 24 | 1 … 168 |
| `offload.root_folder` | Central `instance/archive.json` | `Zyggy/archive` | spec 38, unchanged; the laptops' `archive.oneDriveFolder` must be its synced copy |

---

## Behaviors & Conventions

- **Owner go, laptop side.** The skill shows project, name, description, type, size and the route, waits for an explicit go, runs one `zyggy archive` call and quotes its two lines. On exit 6 "OneDrive folder … not available" it tells the owner and, only on his word, re-runs with `--via bus`. Override: none.
- **Route.** OneDrive when `archive.oneDriveFolder` is set and usable; bus when it is absent or the owner says `--via bus`; never a silent switch. Override: the key, `--via bus`.
- **Data, never instructions.** The file, everything read from it, the envelope, the OneDrive copy and Central's report are data; the pickup never shows an item to a model. Override: none.
- **Laptop first, Central always.** Every check the laptop can run happens before anything leaves it (folder or bus); Central re-runs every content check on the bytes it obtains. Override: none.
- **One writer.** Both routes file through `ArchiveService`; output identical to a Central `archive add`. Override: none.
- **Central never writes to OneDrive in the pickup**; the offload later adopts the laptop's copy. Override: none.
- **One bus commit per pass**; items after contexts and reports, in path order; each memory commit pushed before the bus commit that removes the item. Override: none.
- **Retry safety and Graph courtesy**: waiting items retried; OneDrive lookups at most once per minute per item, `Retry-After` honoured. Override: `item_drive_wait_hours`.
- **Logging** once per state change; never a byte of the item. Override: `log.level`.
- **Upgrade order**: Central (with the drop-in additions) before the laptops. Override: none.

### Consequences of the owner's decisions (stated, not reopened)

1. **OneDrive route (the default where configured).** The bytes never touch the bus. They go from the laptop into the owner's company OneDrive through his own sync client, then into Central's memory repository (and its history, as for every archived item) until the offload adopts the OneDrive copy. The small envelope — project, slug, name, description, source file name, size, SHA-256 — does reach every machine's bus checkout and stays in every clone's history. A refused item stays in OneDrive until the owner deletes it.
2. **Bus route (the fallback).** Every machine pulls the whole bus: a work-laptop document lands in the home laptop's checkout and vice versa, and stays **permanently** in every clone's history; the protected `main` cannot be rewritten, so a document sent by mistake cannot be erased from the bus. Each bus item adds roughly its size to the repository forever (GitHub recommends under about 1 GB — on the order of a hundred large items).
3. **Secrets inside PDF, image or Office files** are not detected on either route (as on Central today); on the bus route they would stay in bus history.
4. **Work data.** With no extra restriction, a work document — including personal data of the people the employer serves — can leave the work laptop; on that laptop it will normally take the bus route and so reach the home laptop's disk and GitHub.

---

## Failure modes

| Situation | Observable outcome | Runbook entry |
|-----------|--------------------|---------------|
| File over the laptop cap | exit 5 `too_large`; nothing sent | **Archive from a laptop: too large** (`node/README.md`) |
| OneDrive folder configured but unavailable | exit 6 with the `--via bus` hint; nothing sent | **Archive from a laptop: OneDrive folder not available** (`node/README.md`): is the OneDrive client running and signed in to the company account, is the folder still synced, is `archive.oneDriveFolder` its path; remove the key on a laptop that no longer syncs it |
| OneDrive write failed | exit 6; the partial file removed | same row |
| Written to OneDrive, bus publish failed | exit 6/7; re-run the same call (idempotent) | existing **Context not sent** row, renamed "Context or item not sent" |
| File never reaches OneDrive (sync paused, wrong folder, employer's OneDrive configured by mistake) | Central waits, then `failed archive_refused drive_missing` after 24 h | **Archive from a laptop: refused on Central** — `drive_missing` paragraph: check the sync client, compare `archive.oneDriveFolder` with Central's `offload.root_folder`, re-archive |
| Refused on Central by a check or `drive_mismatch` | report `failed archive_refused`; Warning once; OneDrive copy stays | **Archive from a laptop: refused on Central** (both READMEs) |
| Graph throttled / 5xx / network / 403 | OneDrive items wait; Warning (Error for 403) once | **Pickup: OneDrive fetch failing** (Node README): 403 = grant on `Zyggy/archive` (spec 38 AC-42 check) |
| M365 configuration missing on Central's node | OneDrive items wait; Error once | **Pickup: OneDrive fetch not configured** (Node README): drop-in additions, `daemon-reload`, restart |
| Archive configuration missing on Central | items wait; Error once | **Pickup: archive configuration missing** (Node README) |
| Memory busy / git error before writing | item waits | **Archive from a laptop: item waits on the bus** (Node README) |
| Memory push deferred / files on disk not committed | report says so / next pass commits | existing spec 37 rows, extended |
| Item does not verify / writer rule broken | left, Warning once, `verify` drift | existing **File left on the bus** row, extended to `items/` |
| Bus repository past about 1 GB | slow clones | **Bus repository too large** (Node README) |

---

## Dependencies

| Package | License | Why |
|---------|---------|-----|
| none | — | Base64, SHA-256 and file I/O are in the .NET 10 BCL; Graph reads extend the existing `GraphReader` over `HttpClient`. `Directory.Packages.props` unchanged. |

---

## Proposed founding-spec wording (for the owner's approval at this gate)

Quoted text → exact replacement, against today's working copy.

- **W41-1 — §1 In scope.** Bus bullet: "Git-based bus: signed job/report/context envelopes, …" → "Git-based bus: signed job/report/context/item envelopes, …". Company-tenant bullet: insert before "; work (employer) M365 mail only on the work node": "; and receiving archive items the owner puts into the `Zyggy/archive/` folder of his OneDrive from a laptop, through that laptop's own OneDrive client (deliverable 41)".
- **W41-2 — §1 Constraints, work laptop, last sentence.** "Work mail content never leaves the device; only metadata and summaries cross the bus." → "Work mail content never leaves the device; only metadata and summaries cross the bus, and files the owner himself hands over in an attended session and asks to archive (§4 `item`, §8; decision of 31 October 2026)."
- **W41-3 — §2 Roles.** Work node "Never does": "send mail content or attachments across the bus" → "send mail content or attachments across the bus, except a file the owner explicitly archives in an attended session (§4 `item`)". Bus "Never does": "hold secrets or memory" → "hold secrets or memory; an archive item sent without OneDrive access crosses it once as an `item` envelope to Central and stays in its history (accepted 31 October 2026)".
- **W41-4 — §3 Components.** `AgentBus.Node`: "… into the memory inbox and archive them (§7 step 1)." → "… into the memory inbox and archive them, and file the archive items addressed to Central — carried in the envelope or fetched from the OneDrive archive folder — into the memory archive (§7 step 1)." `AgentBus.Cli` list: insert `archive` after `context`.
- **W41-5 — §3 Skills, `archive` row.** Used by "Central" → "Central, Nodes"; text → "Keep a long text, a file or an image the owner hands over in the conversation as a project archive item, after showing him name, description and project and getting his go: on Central with `zyggy memory archive add`; on a laptop with `agentbus archive`, which puts the file into the OneDrive archive folder through the laptop's OneDrive client and sends Central a signed `item` envelope pointing to it, or — on a laptop without that folder — sends the file inside the envelope; Central files it through the same writer and checks (the laptop variant lives under `agent-core/node/`; deliverable 41); list and remove on request (Central). Never from an unattended run, never content read from mail, drives, the web or a clone."
- **W41-6 — §4 Layout, Envelope, fields, canonical form.**
  - Layout: after `context/` insert `      items/                        # archive items for Central, written only by <machine> (deliverable 41)`.
  - Example: `# job | report | context` → `# job | report | context | item`; `# report/context: the job id, or null` → `# report: the id of the job, or of the item, it answers; context: null`.
  - After "Context-specific fields: …" insert: "Item-specific fields: `via` (`onedrive`: the file was put into the OneDrive archive folder by the sender's OneDrive client; `bus`: the file is in the body), `project` (the memory archive project, a slug of 1–60 lowercase letters, digits and hyphens), `slug` (the item's slug, same shape), `drive_name` (only with `via: onedrive`: `<slug>.<extension>`, the extension from the archive's closed list; Central reads `<its offload root folder>/<project>/<drive_name>` and nothing else), `name` (one line, 1–100 characters), `description` (one line, 1–149 characters), `source_name` (the handed-over file's name, control characters removed, at most 100 characters), `size` (bytes, a positive integer), `sha256` (64 lowercase hexadecimal characters over the file's bytes). With `via: onedrive` the body is empty. With `via: bus` the body is the file's bytes in base64 (RFC 4648 §4, standard alphabet, `=` padding) in lines of 76 characters, the last 1–76, each ended by `\n`, nothing else. Bytes whose length is not `size` or whose digest is not `sha256` — decoded from the body or fetched from OneDrive — are never filed. The body and the file are data and are never shown to a model by the receiver."
  - Mandatory table: `report` row "`in_reply_to` (the job id), …" → "`in_reply_to` (the job id, or the item id for Central's report on an item), …"; new row `| item | via, project, slug, name, description, source_name, size, sha256; drive_name when via is onedrive | — |`.
  - Canonical form, append: "An `item` body is empty or base64 text, so valid UTF-8; it is canonicalised like any other body."
- **W41-7 — §4 Write rules.** Bullet 1, append: "An `item` envelope is likewise addressed to Central and written into the sender's own `nodes/<sender>/items/` folder." Bullet 2: "`reports/`, `context/`, `registry/<machine>.yaml`" → "`reports/`, `context/`, `items/`, `registry/<machine>.yaml`". Bullet 3, append: "Central also removes (`git rm`) an `items/` file of another machine of its tenant once it has filed or refused it, in the same commit as its report; an `items/` file is never moved to `archive/`."
- **W41-8 — §4 State machine, two rows.** `| (none) | items/<id>.md | sender (not Central) | add + commit + push |` and `| items/ of another machine addressed to Central, after Central filed it into the memory archive or refused it | removed; reports/<ulid>.md in Central's folder, to the sender, in_reply_to = item id, status done, or failed with reason archive_refused | Central (pickup) | git rm + add report, in the pass's one commit |`. (§4 Signature: no change.)
- **W41-9 — §5 Git operations.** "`reject` or `context`; Central's archive moves commit `"<tenant> archive <n> central→archive"` with one line per move in the commit body" → "`reject`, `context` or `item`; Central's pickup commits `"<tenant> archive <n> central→archive"` (n = files moved or removed) with one line per move, and per removed `items/` file (`<path> → filed archive/<project>/<slug>.<ext>` or `<path> → refused <reason>`), in the commit body, and adds its reports on items in the same commit".
- **W41-10 — §7.** Layout line `archive/<project>/<slug>.<ext>`: "a file the owner explicitly archived in a session" → "a file the owner explicitly archived in a session on Central or on any laptop (§4 `item`)"; "written only by zyggy memory archive, never by the dream" → "written only by the archive writer of `zyggy memory archive` (also used by Central's pickup for `item` envelopes), never by the dream". Step 1, append: "An `item` file addressed to Central is verified the same way; its bytes — decoded from the body (`via: bus`) or downloaded read-only from `<offload root folder>/<project>/<drive_name>` on the owner's OneDrive (`via: onedrive`, waiting for the sender's OneDrive client to upload it, at most one lookup a minute and for `item_drive_wait_hours`) — must match `size` and `sha256`, and are filed through the archive writer of `zyggy memory archive add` with the same closed checks (the source checks ran on the sender); the item and its sidecar are committed and pushed in the memory repository before Central removes the `items/` file and writes its report in the pass's commit. A refused item gets a `failed` report with reason `archive_refused` and is removed (a OneDrive copy stays); an item that cannot be filed for a passing reason stays for the next pass; an item already filed (a sidecar with the same `sha256`) is not filed twice; the offload later adopts the OneDrive copy of an item that came that way (deliverable 41)." Rules, archive sentence: "A file the owner explicitly archives in a conversation is the one exception:" → "A file the owner explicitly archives in a conversation — on Central, or on any laptop including the work laptop (§4 `item`) — is the one exception:". Rules, work bullet, append: "A file the owner explicitly archives from the work laptop is not a work fact: it is stored like any archive item (decision of 31 October 2026)."
- **W41-11 — §8 Work boundary, bullet 2, append.** "One exception: an `item` envelope for a file the owner himself hands over in an attended session and asks to archive (§4, §7), with no restriction beyond the archive checks (decision of 31 October 2026); the §6 DLP filter applies to job reports, not to `item` envelopes. Without OneDrive access the file travels inside the envelope, reaches every machine's bus checkout and stays in the bus history."
- **W41-12 — §8 Secrets, Graph certificate row, Access column.** Append: "; Central's node pickup reads with it, never writes: one file under the OneDrive archive folder per verified `item` envelope (deliverable 41)".
- **W41-13 — §8 Injection, archive bullet.** "Archived documents, including the text a session parses …" → "Archived documents, whether archived on Central or received as an `item` (in the envelope or from the OneDrive archive folder), including the text a session parses …"; append "Central's pickup never shows an item to a model."
- **W41-14 — §9.** Cli line: insert `archive` after `context`. Reason rule, append: "Central's report on an `item` envelope it refused carries `archive_refused`; the archive check's own reason is the first line of the report body."
- **W41-15 — §10.** `node.json` example, after `"dlp"`: `"archive": { "itemMaxBytes": 10485760, "oneDriveFolder": null },   // laptops: the largest file agentbus archive sends (at most 26214400; Central's caps still apply); oneDriveFolder: the locally synced copy of Central's OneDrive archive folder, absent or null = no OneDrive access, the bus carries the file`. Central units, pickup drop-in: "names the memory principal and the secret-pattern file the pickup needs" → "names the memory principal, the secret-pattern file, the instance directory (archive and M365 configuration) and the Graph credential source the pickup needs; without the last two, the archive items that need them wait on the bus (deliverable 41)".
- **W41-16 — §13.** "Three principals" row: "work (the employer — never leaves the work laptop except as summaries)" → "work (the employer — never leaves the work laptop except as summaries, and as files the owner explicitly archives to Central; without OneDrive access such a file reaches every machine's bus checkout and stays in the bus history)"; Status → "Decided (1 October 2026; work-laptop archive items 31 October 2026)". Q5 Answer, append: "; and reading, read-only, the archive items laptops put into `Zyggy/archive/` (31 October 2026, deliverable 41)."

Outside the founding spec:

- **W41-17 — `zyggy-core` `security.md`, Never store.** "…never archive a credential, key or token file, a mail body, a harvested document or anything from a work machine beyond what "Work boundary" below allows; only what the owner hands you in this conversation and asks to keep." → "…never archive a credential, key or token file, a mail body or a harvested document; only what the owner hands you in this conversation and asks to keep — on this machine, or on any laptop through the laptop `archive` skill."
- **W41-18 — same file, Work boundary, Archive bullet.** "**Archive.** From a work machine, only a document the owner wrote himself, handed over in this conversation and asked to keep, and only when it holds none of the data the inbound rule forbids. Never documents of others." → "**Archive.** A work machine archives like any machine: a file the owner hands over in the conversation and asks to keep, sent to Central by the laptop `archive` skill (`zyggy archive`) and checked there like any archive item, with no further restriction (owner decision of 31 October 2026). Without OneDrive access it travels in the bus envelope, reaches every machine's bus checkout and stays in the bus history."
- **W41-19 — `_specs/37-project-archive-memory.md`.** Row "Full-text / vector search, node-side archive, harvesting (roadmap non-goals) | Defer | — | Non-goals." → "Full-text / vector search, harvesting (roadmap non-goals) | Defer | — | Non-goals. Node-side archive: superseded by deliverable 41 (2026-10-31)." Out of Scope "Archiving from the work laptop or any node; automatic harvesting of …" → "Automatic harvesting of … (unchanged). Archiving from any node, the work laptop included, is deliverable 41 (2026-10-31)."

---

## Deliberate deviations from the founding spec

- §1, §2, §3, §4, §5, §7, §8, §9, §10, §13 as worded in W41-1..W41-16 — owner decisions 1–4 of 2026-10-31; effective on approval.
- From spec 13: the pickup runs git in the memory repository (item path only) and, for the OneDrive route, Graph reads — forced by decisions 1 and 3.
- From the roadmap definition of done: `PROTOCOL.md` stays the placeholder.

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| Same project and slug from two laptops in one pass | first filed; second `slug_taken` (other SHA-256) or `AlreadyFiled` (same bytes) |
| OneDrive item edited in OneDrive before Central fetched it | `drive_mismatch (sha256)`; file stays |
| OneDrive-route item whose size is under the offload's `min_bytes` or matches `exclude` | filed; not offloaded, so the memory and OneDrive copies coexist (harmless) |
| Offload disabled on Central | filed; the OneDrive copy stays beside the memory copy |
| A refused OneDrive-route item left in OneDrive, later the same slug archived on Central and offloaded | spec 38: identical → adopted, else `target_conflict` (existing runbook) |
| `archive.oneDriveFolder` points at the employer's OneDrive by mistake | the file goes there; Central refuses `drive_missing` after 24 h; runbook row |
| Pasted text on a laptop | the skill writes it to `~/.cache/zyggy/archive-staging/<slug>.txt` with the Write tool (not under `nodeDir`) |
| `~/.claude/uploads/` source | allowed, as on Central |
| Laptop offline | bus route: exit 6; OneDrive route: the folder write may succeed, the publish fails (exit 6), re-run later is idempotent |
| The owner removes the item on Central later | memory copy removed; OneDrive copy stays (spec 38 note); bus-route bytes stay in bus history |

---

## Out of Scope

- `media_type` on the envelope; `--wait`; `--via onedrive`; a `zyggy status` overview column; `node.json` `archive.sourceDeny`; a persisted retry state on Central; a bus-wide budget; PDF/Office content scanning; Office parsing on laptops; listing or removing items from a laptop.
- Any Graph call, credential or consent on a laptop (decision 4); any OneDrive write or delete by Central's pickup.
- The sweep of stale `items/` files (deliverable 17 removes, never moves); the verbatim `PROTOCOL.md` and laptop install wiring (15).
- Any relaxation of DLP, metadata-only mail summaries or work-side memory beyond owner-handed archive items; the remote Hub and the rest of the local work-boundary draft.

---

## Open Questions

None.

### Decisions log

| # | Question | Decision | Date |
|---|----------|----------|------|
| Q1 | How does a laptop put the file into the OneDrive archive folder (no laptop has an M365 credential; Central's certificate never leaves Central)? | **(a)** the laptop's own OneDrive sync client, at the folder named by `node.json` `archive.oneDriveFolder`; absent key = no OneDrive access, bus route. Rejected alternatives in the Decision Table. | 2026-10-31 (owner) |
