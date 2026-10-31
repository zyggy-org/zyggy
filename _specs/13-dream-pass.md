# Spec: 13 — The dream reading the bus: Central picks up what the machines send, as it arrives; the nightly dream files it (closes P1)

> Founding-spec sections: §3 Components (Node row, Central instance, `dream` skill), §4 Bus protocol (Repository layout, Write rules, Envelope, Signature, State machine), §5 Transport (poll loop items 2–6, Git operations — the commit subject), §6 "Central executing its own jobs", Invocation (`ZYGGY_HOOKS=off`), §7 Memory model (Layout, Context loading, Dream pass step 1 and Rules, Hub `get_context`), §8 Injection, Secrets, Audit, §9 design rules (poller as a pure state machine, `BusPaths`/`MemoryPaths`, no static state), §10 `node.json`, Central systemd units, §11 Logging, Runbooks, §12 P1 gate, §13 decisions (GitHub as the only transport, single Central owner of memory, HMAC envelopes, WebSocket deferred, MVP scope), §14 Tenancy. Spec 12 (D1, D3, D4, D5). Roadmap entry: `_plans/ROADMAP.md` #13.

> **Reconciled with `main` (2026-10-29).** What exists: the whole nightly dream since 28 (`zyggy dream` in `src/Zyggy.Core/Dream/`: lock, `BatchPlanner` over `inbox/*.md` and `daily/`, ledger `.dream/ledger.json` keyed by file path and line hash, the model call through `IModelRunner`, checks, `Rollup` — consumed inbox files deleted after the grace period — one commit and push; run by the instance's `zyggy-dream.timer`/`.path`/`.service` with the principal in `Environment=` and `ZYGGY_HOOKS=off`); the poller (`PollState` pure state machine, `PollOptions` with a fixed ±2 s `Jitter`, `PollSettings` from `node.json` `poll.*`, `BusListener` loop: start-up pull, tick → pull → `ProcessOnceAsync`, safety fetch → pull → `ProcessOnceAsync`, one listener per checkout); `BusRepository` (the one §5 write sequence, `AssertMayWrite` refusing any other machine's folder except a job submission); `BusPaths.Archive(tenant, year, month)` and the archive parser; `BusAuditor` (archive files verified, no writer check); `BusInventory.TraceAsync` already reads `archive/`; spec 12's `zyggy context` writing `tenants/<tenant>/nodes/<from>/context/<id>.md`, `to: central`; the Hub's `get_context` (`MemoryStore` + `ContextRanker`) which **never reads `inbox/`**; `SecretPatterns`, `FactLineWriter`, `MemoryFileWriter.WriteAtomically`; `MachineName.Central`. What does not exist: anything in `src/` that reads a bus `context/` or `reports/` file into `inbox/`, a Central move into `archive/`, a pickup configuration for the Node service, and Part 2 of `Gates/P1_WalkingSkeleton.cs`.

> **Status: approved by the owner (2026-10-29), zero Open Questions.** Q1–Q3 of the draft were answered as recommended and are recorded as decisions Q1–Q3 in the Decision Table; their founding-spec wording is A13-1..A13-9 under Deliberate deviations. The spec is planner-ready.

## User Story

**As** the owner, working in a Claude Code session on the home laptop,
**I want** a fact I tell the laptop (`zyggy context`, the session hooks) and the result of a job a laptop ran for Central to reach Central's memory within seconds, and to be filed into its category by the nightly dream,
**So that** a Central session can use a laptop fact almost at once (`get_context`), memory still has one owner and one consolidation pass, and the P1 walking-skeleton gate closes: fact sent from a laptop → Central → `get_context` returns it, with the injected memory under 6k tokens.

---

## Acceptance Criteria

Legend: (U) unit test, (I) integration test against the local bare repository and a temporary memory tree, (G) gate test in `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`, (T) text-invariant test of a shipped file, (O) owner-executed on the real machines, (D) documentation check. ACs marked **[Q2]** implement decision Q2 (owner, 2026-10-29).

### Pickup on Central (owner decision O1)

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | Central's node (`node.json` `machine: central`, tenant `acme`, pickup configuration for principal `acme/alice`) running against the bare repository; a signed context envelope from `home-laptop` (spec 12's `zyggy context`, body `- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot`) pushed to `main` | the poller sees the change | within the test window: `inbox/bus-context-home-laptop-<id>-<created UTC date>.md` exists under the principal directory, its front matter is the Contracts shape and its body is the envelope body byte for byte; one bus commit `acme archive 1 central→archive` moves `tenants/acme/nodes/home-laptop/context/<id>.md` to `tenants/acme/archive/<YYYY-MM of created>/<id>.md` with identical bytes, pushed to `main`; the memory repository gained **no** commit; no model process was started (I) |
| AC-2 | **context** bodies holding `[stated]` lines (the verb) and `[observed]` lines (the hook spool) | picked up | every line reaches the inbox file unchanged (context envelopes only; a report becomes one line, AC-3) — no tag added, removed, upgraded or downgraded, no prefix, no reordering (U, I) |
| AC-3 | a signed report from `home-laptop`, `to: central`, `status: done` (or `failed` / `timeout`), answering job J | picked up | `inbox/bus-report-home-laptop-<J>-<report id>-<created UTC date>.md` whose body is **exactly one line** in the `FactLine` grammar: `- [observed] <report created, UTC date> [report <first 8 chars of J>]: <first non-empty line of the report body, trimmed>` (decision Q-P1); no other body line is copied; the report is archived in the same commit as any other file of the pass (U for the line, I for the file) |
| AC-3a | a report as in AC-3 whose first non-empty body line matches a secret pattern; or whose body has no non-empty line | picked up | archived, **no** inbox file; the secret case logs one warning `pickup: <id>: report line refused (pattern <name>)` — never the line (U) |
| AC-4 | a signed report `to: central` with `status: rejected` | picked up | archived, **no** inbox file (a protocol rejection carries no fact; `zyggy status --id` still finds it in the archive, AC-25) (U) |
| AC-5 | files in `nodes/central/reports/` (Central's own reports, e.g. of a job it submitted to itself) or `nodes/central/context/` | a pass | not copied, not moved (left for 17's sweep; a Central report fed back into Central's memory would loop) (U) |
| AC-6 | a `reports/` or `context/` file of another machine whose `to` is not `central` | a pass | not copied, not moved, no log line (U) |
| AC-7 | a file under `tenants/acme/…` whose front matter says `tenant: other-org` | a pass | never ingested, never moved; one warning per file per process lifetime `pickup: left <path>: tenant_mismatch (tenant)`; `zyggy verify` reports it (I) |
| AC-8 | a file that is malformed, unsigned, signed with an unknown key or a wrong signature, whose `from` is not its folder's machine, whose `type` does not match its folder (`report` in `context/`), or whose file name is not its `id` | a pass | never ingested, never moved; tenant and signature are checked before any byte of the body reaches the inbox or a log line; one warning per file per process lifetime naming the path and the §4 / audit reason (never the body); the next pass does not log it again (U, I for the signature case) |
| AC-9 | a context body with three lines, the second matching a line of `secret-patterns.txt` | picked up | the inbox file holds lines 1 and 3 only; one warning `pickup: <id>: dropped 1 line (pattern <name>)` — never the line; the bus file is archived (it is in the bus history already; the runbook row says what to do); a body left with no line produces no inbox file and is still archived; `SecretSweep.AssertAbsent` over the log lines and the inbox tree (U, I) |
| AC-10 | the inbox file for envelope E already exists (a crash after the inbox write and before the archive commit) | the next pass | the inbox file is not rewritten (bytes and last-write time unchanged), no second inbox file is created, E is archived (U) |
| AC-11 | the archive write fails (`lock` busy, `pull` fails, `mv` fails) | a pass | the inbox files written by the pass stay; the bus files stay in their folders; one error line per failure streak `pickup: archive failed at <stage>: <redacted detail>`; the next pass re-archives them without writing any inbox file again (AC-10) (I for `pull`, U for the rest) |
| AC-12 | the archive commit is made but every push attempt is refused (the §5 retry rule) | a pass | outcome `Deferred`: the local checkout no longer has the files in their live folders, so no later pass picks them up; the next bus write of Central pushes the commit (I, the 08 `PushRetryTests` set-up) |
| AC-13 | two context envelopes from `home-laptop` and one report from a second machine `work-laptop` waiting | one pass | three inbox files, **one** archive commit `acme archive 3 central→archive` whose body lists one line per move `<from path> → <archive path>`; one Information log line for the pass (I) |
| AC-14 | the listener's lifecycle | start-up pull; a tick whose probe is `Changed` and whose pull succeeds; a safety fetch that finds the branch moved; a `304` tick; a tick after a safety fetch already pulled | the pass runs after the start-up pull, after every successful pull caused by the doorbell or the safety fetch (after the job pass of the same pull), never on a `304` tick and never twice for one pull (U with a substituted provider and `FakeTimeProvider`) |
| AC-15 | `home-laptop`'s node (any machine whose `node.json` `machine` is not `central`) | started and running | no pickup is constructed, no pickup environment variable is read, `inbox/` of any tree is never touched (U; the existing part 1/1b gates run unchanged) |
| AC-16 | Central's node with `ZYGGY_MEMORY_ROOT` / `ZYGGY_TENANT` / `ZYGGY_USER` unset, the principal directory missing, `ZYGGY_TENANT` ≠ `node.json` `tenant`, `ZYGGY_TIMEZONE` unknown, or the secret-pattern file unresolvable or malformed | `zyggy-node` starts | exit 3, one stderr line `zyggy-node: configuration error: pickup: <detail>` (the detail is `DreamConfiguration`'s / `SecretPatterns`' message for the same key); nothing else started — fail closed, the files wait safely on the bus (U) |
| AC-17 | an unexpected exception inside a pass (an unreadable inbox directory, a full disk) | a pass | the pass ends with one error line naming the exception type; the loop keeps polling and processing jobs; no bus file is moved for a file whose inbox write failed (U) |
| AC-18 | any pass | before and after | the only files written are under `<memory root>/<tenant>/<user>/inbox/` and the bus checkout; no git command runs in the memory repository; no `claude` process starts; no file under another `tenants/<org>/` is opened (U — recorded `IProcessRunner`; I — tree fingerprint of the memory tree outside `inbox/`) |
| AC-19 | a pass that moved at least one file / a pass with nothing to do | logs | exactly one Information line `picked up <n> file(s) from <m> machine(s): <k> to inbox, oldest created <UTC timestamp>; archive <pushed\|deferred>` / nothing (log once per state change, §11); every line carries `tenant`, `machine`, `phase=pickup`; never a body line (U) |

### Poll interval (owner decisions O2, O3)

| # | Given | When | Then |
|---|-------|------|------|
| AC-20 | `PollOptions` with `ActiveInterval` 1 s and `Jitter` 2 s, a `Changed` or `Unchanged` outcome, jitter units −1 and +1 | `PollState.Apply` | `NextDueUtc` = now + 0.5 s / now + 1.5 s (the jitter is capped at half the interval); with 10 s: now + 8 s / now + 12 s and with 60 s idle: now + 58 s / now + 62 s — byte-for-byte the current results; throttle and network back-off keep the uncapped ±2 s (U) |
| AC-21 | an `x-poll-interval: 60` header, `x-ratelimit-remaining: 99`, a 403/429 with `retry-after`, a network failure | at a 1 s active interval | next check ≥ 60 s later; throttled ≥ 120 s; until the later of 120 s, the reset and `retry-after`; 10 s doubling to 5 min — the §5 back-off rules unchanged at 1 s (U) |
| AC-22 | `node.json` `"poll": { "activeSeconds": 1 }` | read | accepted (`TryPositiveInt` already allows 1); `0` and negative values still refused with the existing line (U, existing tests extended) |
| AC-23 | the Node README and `deploy/README.md` | read | the Central `node.json` example sets `"poll": { "activeSeconds": 1 }` and has no `workHours` (always active); one paragraph states the rate-limit reasoning of Behaviors "Polling every second" and that no webhook or socket is used (§13) (D) |
| AC-24 | release installed on Central and the home laptop, Central at 1 s | the owner sends 10 facts with `zyggy context` from the home laptop, minutes apart | for each, latency = Central's `picked up` log timestamp − the time `zyggy context` printed its `context <id>` line (the push completed); median and maximum recorded in the private evidence page and in the plan's final gate; until then no text in the product says "about a second" — the README says "within seconds; measured value pending" and is updated with the measured median (O, D) |

### Readers that must keep working once reports leave `reports/` within seconds

| # | Given | When | Then |
|---|-------|------|------|
| AC-25 | a report to Central already archived by the pickup | `zyggy status --id <job>` on Central; `zyggy status` on Central; `zyggy probe` (04) from Central with the node running | the trace shows `reported by home-laptop` with the body (already true: `TraceAsync` reads `archive/`); the "recent reports" block lists the newest reports across every `reports/` folder **and** `archive/` months; `BusRepository.FindReportsAsync` / `FindUnverifiedReportsAsync` also search `archive/`, so the probe still finds its answer (I) |

### `get_context` sees picked-up lines at once **[Q2]**

| # | Given | When | Then |
|---|-------|------|------|
| AC-26 | the inbox file of AC-1, no dream run since | Hub `get_context` with topic `staging database` (in-process MCP client, principal from the environment) | the result holds the line verbatim under a heading `## inbox (picked up, not yet filed by the dream)` inside the existing `<zyggy-context …>` data framing (I) |
| AC-27 | an inbox holding `bus-context-…`, `bus-report-…`, `remember-<date>.md`, a backfill-derived file (`files-<date>.md`) and a ledger marking some `bus-*` lines consumed | `get_context` | only unconsumed lines of `inbox/bus-*.md` and `inbox/remember-*.md` are candidates; the backfill file never appears; inbox lines use at most 20 % of the budget, newest file first, cut at a line boundary with the marker `[context truncated: <n> more inbox lines not included]`; an unreadable or invalid ledger means "nothing consumed", never an error (U) |
| AC-28 | the same tree | `zyggy memory digest identity\|index\|daily` | no inbox line in any section (§7 "inbox/ … never injected" unchanged) (U) |

### Gate and dream

| # | Given | When | Then |
|---|-------|------|------|
| AC-29 | `#region Part 2 — 13` appended to `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`, tenant `acme`, principal `acme/alice` on a fixture memory tree; parts 1 and 1b untouched | `zyggy context --scope project:calizr -- "<fact>"` in process with `home-laptop`'s `node.json` → the production host as `central` with the pickup configuration → the Hub's `get_context` in process | `get_context` returns the fact line verbatim; the three digest sections together are ≤ 20,000 bytes (the §7 "< 6k tokens" target) and contain no inbox line; the context file is in `archive/` on `main`; `zyggy verify` on Central reports every file `OK` (G) |
| AC-30 | the inbox file of AC-1 | `zyggy dream` with the fake `claude` filing the batch | `BatchPlanner` reads the file's date from its name and classes the `[stated]` line as a stated inbox line; the run consumes it in the ledger; the dream reads and writes nothing on the bus (U for the planner; I with the existing 28 end-to-end harness) |
| AC-31 | release installed, Central polling at 1 s, a real fact sent and a real job run from the home laptop | the next real nightly dream | the owner reviews the memory repository's `dream YYYY-MM-DD` diff (the fact filed in its category with its tag) at the plan's final gate; the bus shows both files under `archive/` (O) |
| AC-32 | the nightly entry point (decision Q1) | — | the instance's `zyggy-dream.timer`/`.path`/`.service` are unchanged, no dream job is ever submitted to the bus, and the founding spec text says so (A13-2, A13-3) (D) |

### Delivery

| # | Given | When | Then |
|---|-------|------|------|
| AC-33 | the release artefact | built | ships `deploy/systemd/zyggy-node.service.d/pickup.conf.example` (placeholders `acme`, `alice` and `<instance-dir>` for every path — never a `/srv/…` path; `Environment=` for `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE`, `ZYGGY_SECRET_PATTERNS`; no secret) listed in `SHA256SUMS`, pinned by a text-invariant test under `tests/Zyggy.Core.Tests/Deploy/`; `install-node.md` (Central section) and `deploy/README.md` name it as **required on Central from this release** (T, D) |
| AC-34 | the §12 definition of done | the deliverable closes | Node README troubleshooting rows: "Pickup: configuration error", "Pickup: file left on the bus", "Pickup: archive failed / deferred", "Pickup: secret-shaped line dropped", "Pickup: slow (throttled)"; the 28 runbook row "failed or aborted dream run" exists (checked, not rewritten); the founding-spec amendments of Deliberate deviations proposed as exact wording; `PROTOCOL.md` stays the placeholder (15) (D) |
| AC-35 | every test, fixture, example and runbook text | reviewed | tenant `acme`, user `alice`, machines `central` / `home-laptop` / `work-laptop`, project `calizr`; no owner name, employer, real path or id (hygiene grep) (D) |

---

## Decision Table

### Owner decisions taken before the spec (2026-10-29)

| # | Decision | Consequence in this spec |
|---|----------|--------------------------|
| **O1** | Pickup is event-driven and separate from the nightly dream: Central's Node service, each time the poller sees a change (and on the safety fetch), reads every context envelope and report addressed `to: central` from every machine's folder of the tenant, verifies the signature, copies the lines into Central's memory inbox unchanged, archives the processed files and pushes. The nightly Claude consolidation stays nightly. | §7 step 1 leaves the dream and becomes a stage of Central's poll loop (`BusPickup`); no `zyggy dream ingest` verb; the dream (28) is unchanged. |
| **O2** | Central polls every second: `poll.activeSeconds: 1` in Central's `node.json` (no work hours, always active). The fixed ±2 s jitter scales with the interval; the spec states the rate-limit reasoning and requires one owner measurement of push→pickup latency before "about a second" is promised. | `PollState.Apply` caps the quiet-branch jitter at half the interval (AC-20); configuration only for the interval (AC-22/23); AC-24. |
| **O3** | No webhooks, no sockets — the §13 transport decision stands. | Nothing in this spec opens a port or subscribes to an event. |

### Decisions taken at the spec gate (owner, 2026-10-29)

| # | Decision | Consequence in this spec |
|---|----------|--------------------------|
| **Q1** (owner, 2026-10-29) | The nightly dream **stays the direct timer** built in 28 (`zyggy-dream.timer`/`.path`/`.service` running `zyggy dream`); it is not submitted as a bus job. | No bus job, no `JobRunner` change, no Bash permission for a job; the dream's ledger is its memory commit and run record. The sweep (17) still runs as a job because it moves bus files. Founding spec amended by A13-2 (§3 Central instance, §6 Central executing its own jobs). AC-32. |
| **Q2** (owner, 2026-10-29) | `get_context` returns the lines of `inbox/bus-*.md` and `inbox/remember-*.md` that the dream has not yet consumed, at most 20 % of the budget, newest file first; every other inbox file stays excluded. | `MemoryStore.PickedUpLines`, a third `ContextRanker` block, `ContextOptions.InboxBudgetPercent = 20`; the `SessionStart` digest stays inbox-free; the gate proves the round trip without a dream run. Founding spec amended by A13-9. AC-26..AC-29. |
| **Q-P1** (owner, 2026-10-29 (plan gate)) | A picked-up **report** is not copied verbatim: it becomes exactly one inbox line `- [observed] <date of the report's created, UTC> [report <job id first 8>]: <first non-empty line of the report body, trimmed>`, secret scan applied; if that line matches a secret pattern, or the body has no non-empty line, the report is archived with no inbox file (pattern name logged). Context envelopes are still copied verbatim, no prefix. | The dream only offers and consumes `- ` lines (`Dream/MemorySnapshot.cs`, `Dream/Compressor.cs`), so prose report bodies would never be filed nor rolled up. AC-3, AC-3a; Contracts report variant; founding spec follow-up A13-10. |
| **Q-P2** (owner, 2026-10-29 (plan gate)) | Every path in the shipped drop-in example is the placeholder `<instance-dir>`, never `/srv/…`. | AC-33. |
| **Q3** (owner, 2026-10-29) | The §4/§5 wording for Central's immediate archive move is approved as proposed: a new state-machine row for the pickup, the write rule naming the only moves allowed into `archive/`, archive month = the envelope's `created` month (shared with 17's sweep), one commit per pass with subject `<tenant> archive <n> central→archive` and one body line per move. | `BusRepository.ArchiveAsync`, the one new `AssertMayWrite` branch; founding spec amended by A13-4..A13-6; `PROTOCOL.md` (15) inherits the wording. AC-1, AC-13. |

### Feature verdicts

| Founding-spec item (section) | Verdict | Target type / library | Justification |
|------------------------------|---------|-----------------------|---------------|
| §7 step 1 "`agentbus dream ingest`: fetch the bus; copy … into `inbox/` … move processed bus files to `archive/`" as a dream stage | **Reshape** (**O1**) | `BusPickup` run by Central's `BusListener` after every successful pull | A fact that waits for 03:00 is useless for the session that needs it now; the poll loop already pulls the bus within seconds and holds the only listener lock on the checkout, so the pickup costs one pass over two folders per machine and no model call. The dream stays one consolidation per night (28's caps, cost). |
| A `zyggy dream ingest` CLI verb (§7 step 1, roadmap scope) | **Defer** (not built) | — | With the pickup in the node, a verb would be a second writer of the same moves competing for the checkout lock. Recovery is "restart the node" (the pickup runs after the start-up pull). Re-open only if an operator need appears. |
| `zyggy dream rollup` as a separate verb (§7 step 6, roadmap scope) | **Keep as built** (28) | `Rollup` stage inside `zyggy dream` | Already shipped and tested in 28; a verb adds a second entry point to one stage. |
| Fetch the bus (§7 step 1) | **Keep** (reuse) | the listener's pull (§5 item 2) and safety fetch (§5 item 6) | No second fetch: the pickup reads the checkout the listener just pulled. |
| Read from **every** `tenants/<tenant>/nodes/<machine>/` of the tenant, `to: central` only (§7 step 1 as amended by A12-1; spec 12 D1) | **Keep** | `BusRepository.ListPickupCandidatesAsync` over `BusPaths.Nodes(tenant)` → each machine ≠ `central` → `Context(…)` and `Reports(…)` | The sender writes its own folder (D1); Central's own folder is excluded (AC-5): Central never writes context (spec 12 AC-12) and its own reports fed back would loop through memory. |
| A file whose `tenant` differs from its subtree is never ingested (§7 step 1, §4) | **Keep** (exists) | `EnvelopeSigner.ParseAndVerifyAsync(bytes, location.Tenant)` | 03's order: tenant before signature, so a foreign key is never looked up. |
| Signature verified before any model call / any use (§8 Injection) | **Keep** | same; plus the audit's writer rules (`from` = folder machine, `type` = folder, file name = `id`) | Security boundary, never softened. The HMAC key is per tenant, so any machine of the tenant can sign `from: <other>`; the writer rule is the defence in depth the auditor already applies. A refused file is left in place (not `jobs/rejected/`, which is for jobs) and logged once per file (AC-7, AC-8). |
| Copy "as one file each" (§7 step 1) | **Keep** | `PickupInbox.Write` → `inbox/bus-context-<from>-<id>-<date>.md`, `inbox/bus-report-<from>-<job>-<id>-<date>.md`, front matter in `FactLineWriter`'s format, body verbatim for context and one `FactLine` for a report (Q-P1), `MemoryFileWriter.WriteAtomically` | The name is derived from the envelope alone (idempotent, AC-10); it carries the sender and the job id because the dream prompt shows the model only `<path>: <line>` (`DreamPrompts`), and §7 wants `[observed]` lines to carry the job id as provenance; the trailing date is the one `BatchPlanner`'s `-(YYYY-MM-DD)\.md$` rule reads. |
| Copy lines unchanged; tags never upgraded (spec 12 D4, owner O1) | **Keep** for context envelopes | context body bytes copied; no line rewritten (reports: next row) | The tag records the speech act at the source (spec 12); the dream already treats `[stated]` inbox lines as first class (`BatchPlanner`). |
| Reports: "which part becomes a fact" (roadmap question) | **Reshape** (decision **Q-P1**, owner, 2026-10-29 (plan gate)) | for `done` / `failed` / `timeout`: one inbox line `- [observed] <created UTC date> [report <job id first 8>]: <first non-empty body line, trimmed>`, secret-scanned (a match → archived, no inbox file, pattern logged); `rejected` archived without an inbox file (AC-4) | The dream only offers and consumes `- ` lines (`Dream/MemorySnapshot.cs`, `Dream/Compressor.cs`): prose report bodies copied verbatim would never be filed and their inbox files never rolled up. One `[observed]` line with the job id is the §7 provenance rule ("facts a node derives … are `[observed]`", "carry the job id"); the full report stays readable with `zyggy status --id` from the archive. A `rejected` report's body is `rejected: <reason>` — protocol state, not a fact about the owner's world; filing it would cost a dream batch line for nothing, and `zyggy status --id` still shows it. |
| Re-scan lines for secret patterns on pickup (roadmap question) | **Keep** (Central-side control) | `SecretPatterns` (the same file the dream uses, fail closed); a matching line is dropped, logged by pattern name and envelope id | Spec 12 scans on the laptop, but a laptop may run an older pattern list, a hand-edited spool line ships (spec 12 AC-21 scans again at flush, a hand-crafted envelope would not), and a report body is never scanned at the sender. Memory is the durable state; one regex pass per line is free. Dropping (not refusing the file) keeps the other facts; the dream's own write-time check stays as the last gate. |
| Move processed files to `archive/` (§7 step 1; §4 "Only Central writes `archive/`", state machine row 5) | **Keep**, timing **Reshape** (**O1**; wording decision **Q3**, owner, 2026-10-29) | `BusRepository.ArchiveAsync` (Central only), target `BusPaths.Archive(tenant, created.Year, created.Month)`, one commit per pass `<tenant> archive <n> central→archive` | The existing row moves `reports/`/`context/` to `archive/` by Central after 14 days; the pickup moves the files addressed to Central as soon as they are in the inbox. One commit per pass, not per file, so an outage backlog costs one push (GitHub's content-creation limits, §5 retry). The archive month is the envelope's `created` month so a retried or a later sweep move lands in the same place. |
| Write rule for Central moving files out of another machine's folder (§4; roadmap question) | **Reshape** (decision **Q3**, owner, 2026-10-29) | `AssertMayWrite` gains exactly one branch: when `Me.Machine` is `central`, a `git mv` **from** `nodes/<any machine>/{reports,context}/` of its own tenant **to** `archive/YYYY-MM/` of its own tenant | Nothing else is loosened: no other machine may move into `archive/`, Central still may not write another machine's `reports/`/`context/`/`jobs/claimed/`, and a move across tenant prefixes stays refused. |
| No double archiving with 17's 14-day sweep (roadmap question) | **Keep** (by construction) + hand-off note to 17 | — | A file lives in exactly one path; the pickup empties the live folders of every verifiable file addressed to Central, so 17's sweep only sees what the pickup leaves: files not addressed to Central, Central's own reports, and files that fail verification. 17 must (a) use the same `created`-month rule and (b) never archive a verifiable `to: central` file unless the pickup has run in the same Central process first — recorded as a forward item for 17 (Out of Scope). |
| Idempotence: never picked up twice (roadmap question) | **Keep** | order per pass: verify → inbox write (skipped when the file exists) → one archive commit; the archive commit is local before the push, so a deferred push never re-exposes a file | Crash between inbox write and archive commit → the next pass finds the inbox file present and only archives (AC-10). Concurrent safety fetch and tick → the listener is one loop with one listen lock, and `pulledThisTick` already prevents a second pass for one pull (AC-14). Residual: if the archive **commit** fails on every pass for longer than the dream's inbox grace (consumed + 7 days) the dream may delete the inbox file and a later pass re-create it; this needs a broken checkout for a week, is logged once per streak (AC-11), and the dream's restatement merge absorbs the duplicate. Recorded under Edge Cases; no extra state file. |
| Pickup configuration on Central's node (§10: "On Central the Hub and the memory verbs require the principal from the environment") | **Keep** | `PickupConfiguration.Load(environment, nodeConfiguration)` — the same keys and messages as `DreamConfiguration`; a shipped drop-in example `zyggy-node.service.d/pickup.conf.example` | `node.json` names no user (spec 11 D1); the instance's dream unit already uses `Environment=` for the same variables. Missing configuration on Central refuses start with exit 3 (AC-16): a silent node that polls but never picks up would look healthy while memory starves; the bus keeps the files safe until the drop-in exists. |
| Pickup enabled only on Central | **Keep** | `node.json` `machine` equals `MachineName.Central` (spec 12's precedent: the label decides, `node.json` has no `role` key) | No new `node.json` key; laptops are untouched (AC-15). |
| Nightly dream steps 2–4, 6, 7 and Rules (§7) | **Keep as built** (28) | `DreamRunner` unchanged | 28 shipped them; 13 only feeds the inbox. AC-30 proves the hand-over (file name date, line class). |
| Nightly run as a bus job to `nodes/central/jobs/` (§3 Central instance "from 13 the timer submits a `dream` job instead"; §6 "Central executing its own jobs"; O6) vs the direct timer of 28 | **Keep as built** (decision **Q1**, owner, 2026-10-29) | the instance's dream timer units, unchanged | After O1 the dream reads no bus file; a bus job would add three bus commits a night and either a model session whose only task is to start `zyggy dream` (nested runs, a Bash permission, a job slot) or a new "verb job" runner contract for one caller. The memory commit and run record are the dream's ledger. A13-2. |
| `ZYGGY_HOOKS=off` for the dream run (§6 Invocation as amended by A12-4) | **Keep** (exists) | the instance's dream unit sets it; a job run would get it from the runner (spec 12 D5) | Either Q1 answer keeps the memory writers silent inside the dream's model call. |
| Step 5 `agents.md` from the registry (§7) | **Defer** to 14 | — | Roadmap. |
| Step 7 Telegram diff of identity files (§7) | **Defer** to 22 | — | Roadmap; the owner reviews one diff by hand at this plan's gate (AC-31). |
| Poll interval 10 s / 60 s (§5 item 3) | **Reshape** for Central only (**O2**) | `node.json` `poll.activeSeconds: 1` on Central; code defaults stay 10 / 60 | Configuration, not code: the laptops keep 10 s (they wait for jobs, minutes matter less). |
| Jitter ±2 s (§5 item 3) | **Reshape** (**O2**) | `PollState.Apply`: quiet-branch jitter = `min(options.Jitter, interval / 2) × unit`, `interval` after the `x-poll-interval` raise | A ±2 s jitter on a 1 s interval makes the next check due in the past half of the time (a tight loop bounded only by request latency) and the mean interval meaningless. Halving keeps 10 s and 60 s byte-identical (2 s ≤ 5 s and 30 s). Throttle and back-off spans are ≥ 10 s and keep the uncapped jitter. |
| Back-off on 403/429, `x-ratelimit-remaining < 100`, `retry-after`, `x-poll-interval`; network back-off (§5 items 4–5) | **Keep** | `PollState` unchanged | They are what makes 1 s safe (AC-21). |
| Safety fetch every 15 min (§5 item 6) | **Keep** | triggers the pickup when the branch moved | O1 names it. |
| No webhooks, no sockets (§5, §13) | **Keep** (**O3**) | — | — |
| `get_context` never returns `inbox/` (§7 Hub table, spec 11 D3) | **Reshape** (decision **Q2**, owner, 2026-10-29) | `MemoryStore.PickedUpLines`, a third block in `ContextRanker`, `ContextOptions.InboxBudgetPercent = 20`, prefixes `bus-` and `remember-` | Without it a picked-up fact is invisible to every session until the next night, which defeats O1/O2. Unconsumed lines only (the ledger is in the tree), its own 20 % share, the same data framing; backfill-derived inbox files stay out (GDPR note of 28/O34). A13-9. |
| `SessionStart` digest: `inbox/` never injected, < 6k tokens (§7 Context loading) | **Keep** | `DigestBuilder` unchanged; AC-28, AC-29 | `get_context` is on demand; injection stays capped and inbox-free. |
| Readers of `reports/` that break when reports move within seconds | **Reshape** (supporting change) | `FindReportsAsync` / `FindUnverifiedReportsAsync` and `zyggy status`'s recent-reports list also read `archive/` months | `TraceAsync` already does. Without this, `zyggy probe` from Central and the `status` read-out lose every report to Central the second it arrives. 21's `job_status` inherits the same rule. |
| P1 gate part 2 (§12, roadmap) | **Keep** | `#region Part 2 — 13` in `P1_WalkingSkeleton.cs` | DoD. |
| One real nightly run reviewed (roadmap DoD) | **Keep** | AC-31 | One-time gate review; 28's "no daily review" rule is untouched. |
| `/health` fields for the pickup | **Defer** | — | §11 is a shared contract; the log line (AC-19) and `zyggy status` (archive and live counts) show what happened. Re-open with 21's `list_nodes`. |
| Tenancy (§14): tenant-prefixed bus paths, principal-scoped memory paths, tenant check before signature | **Keep** | `BusPaths`, `MemoryPaths` with an explicit `Principal`, `ZYGGY_TENANT` = `node.json` `tenant` (AC-16) | Costs nothing; no tenant-only code. |
| `PROTOCOL.md` (§4 "must match verbatim") | **Keep** (placeholder) | untouched | 15 publishes it with the §4/§5 amendments approved as decision Q3. |
| Runbook entries (§11, §12) | **Keep** | Node README rows (AC-34), `install-node.md` Central section | Public, placeholder-only. |

---

## Contracts

### Types and interfaces

No new seam. Git still runs through `GitClient`/`IProcessRunner`; GitHub only behind `IBusProvider`; no model call.

Namespace `Zyggy.Core.Bus` (additions):

```
public sealed class BusPickup                                                      // Central only; never throws except cancellation
    BusPickup(BusRepository bus, EnvelopeSigner signer, PickupInbox inbox, SecretPatterns patterns, TimeProvider clock, ILogger<BusPickup> log)
    Task<PickupResult> RunAsync(CancellationToken ct);

public sealed record PickupResult(int Inboxed, int ArchivedOnly, int LinesDropped, int Left, WriteOutcome? Archive);
                                                                                     // Archive null when nothing was due

public enum PickupLeftReason                                                        // closed; the wire names reuse §4 / audit names
    TenantMismatch, Malformed, SchemaUnsupported, KeyIdTenantMismatch, MissingField, InvalidField,
    MissingSignature, UnknownKey, InvalidSignature, WriterMismatch, TypeFolderMismatch, IdDiffers, InboxWriteFailed

public sealed record PickupCandidate(BusFile File, MachineName Folder, BusFolder Kind, byte[] Bytes);

// BusRepository (additions)
Task<IReadOnlyList<PickupCandidate>> ListPickupCandidatesAsync(CancellationToken ct);
    // under the checkout write lock: every `*.md` of nodes/<m>/context/ and nodes/<m>/reports/ for each machine m ≠ central, ordinal order, bytes read
Task<WriteOutcome> ArchiveAsync(IReadOnlyList<(BusFile File, DateTimeOffset Created)> files, CancellationToken ct);
    // the §5 write sequence; one `git mv` per file to BusPaths.EnvelopeFile(BusPaths.Archive(tenant, created.Year, created.Month), id);
    // subject `<tenant> archive <n> central→archive`, body one line per move; InvalidOperationException when Me is not central,
    // a source is not reports/ or context/ of this tenant, or the target is not this tenant's archive (AssertMayWrite's one new branch)
FindReportsAsync / FindUnverifiedReportsAsync                                       // also enumerate tenants/<t>/archive/<YYYY-MM>/*.md (same filters)

// BusListener (change)
public BusListener(…, JobGate? gate = null, BusPickup? pickup = null)               // pickup null on every machine but central
    // after every successful pull — StartAsync, a Changed tick, a Moved safety fetch — and after that pull's ProcessOnceAsync: await pickup.RunAsync
```

Namespace `Zyggy.Core.Memory` (additions):

```
internal sealed class PickupInbox(MemoryPaths paths, TimeZoneInfo zone, TimeProvider clock)
    string FileName(Model envelope);                    // bus-context-<from>-<id>-<yyyy-MM-dd of created UTC>.md
                                                        // bus-report-<from>-<in_reply_to>-<id>-<yyyy-MM-dd of created UTC>.md
    InboxWrite Write(Model envelope, IReadOnlyList<string> lines);   // Created | AlreadyPresent | Failed(detail); atomic; never overwrites
internal enum InboxWriteOutcome { Created, AlreadyPresent, Failed }

public static class PickupConfiguration
    static PickupConfigurationResult Load(IReadOnlyDictionary<string,string?> environment, NodeConfiguration node);
    // ZYGGY_MEMORY_ROOT, ZYGGY_TENANT (must equal node.Tenant), ZYGGY_USER, ZYGGY_TIMEZONE (default UTC),
    // ZYGGY_SECRET_PATTERNS else ZYGGY_INSTANCE_DIR/../.claude/hooks/secret-patterns.txt — DreamConfiguration's rules and messages; patterns loaded and validated

// [Q2] Hub read side
MemoryStore.PickedUpLines(DreamLedger? ledger) → IReadOnlyList<InboxLine>   // inbox/bus-*.md and inbox/remember-*.md, body lines only, unconsumed, newest file first
ContextRanker: third block "## inbox (picked up, not yet filed by the dream)" between the files block and the daily block
ContextOptions.InboxBudgetPercent = 20; InboxFilePrefixes = ["bus-", "remember-"]
```

Namespace `Zyggy.Core.Bus` `PollState.Apply` (behaviour change, signature unchanged): the quiet branch uses `min(options.Jitter, interval / 2) × jitterUnit`.

`Zyggy.Node`: `NodeHost.Build` constructs `BusPickup` when `configuration.Machine == MachineName.Central`, after `PickupConfiguration.Load` succeeds; otherwise `NodeStartupException(NodeExitCodes.Configuration, "pickup: <detail>")`.

### Envelope / file formats

No envelope field, canonical-form rule or signature rule changes; every archived file keeps its bytes and still verifies (`BusAuditor` checks archive files without a writer rule, as today).

Bus moves (state machine, new row — decision Q3):

| From | To | Actor | Git operation |
|---|---|---|---|
| `nodes/<machine>/reports/<id>.md`, `nodes/<machine>/context/<id>.md` of any machine but Central, addressed `to: central`, verified and copied to the inbox | `archive/<YYYY-MM of created>/<id>.md` | Central (pickup) | `git mv`, one commit per pass `<tenant> archive <n> central→archive`, body `<from path> → <to path>` per line, push |

Inbox file written by the pickup (under `memory/<tenant>/<user>/inbox/`, never committed by the pickup):

```
---
name: bus context 01K8…
description: context from home-laptop created 2026-10-29T10:42:07Z scope project:calizr (picked up from the bus; data)
updated: 2026-10-29
---
- [stated] 2026-10-29 (project:calizr): The staging database is restored from Sunday's snapshot
```

Report variant: `name: bus report <id>`, `description: report from home-laptop in reply to job <job id> status done created <timestamp> (picked up from the bus; data)`; body exactly one line, `FactLine.Format("observed", "", "report <job id first 8>", <created UTC date>, <first non-empty body line, trimmed>)`, e.g. `- [observed] 2026-10-29 [report 01K8ABCD]: Root cause is the expired staging certificate` (decision Q-P1). `updated` is Central's local date (`ZYGGY_TIMEZONE`), as every inbox writer. Context body = the envelope body bytes minus dropped secret-shaped lines, a missing final newline added. LF, UTF-8 without BOM. No colon followed by a space in `description` (the `FactLineWriter` front matter is unquoted).

### CLI surface

No new verb. Changed output: `zyggy status` "recent reports (<k> of <n>)" counts and lists reports from every `reports/` folder and every `archive/` month, newest first; the per-machine counts table stays live-folder counts (on Central, `reports` and `context` of other machines drop to 0 after pickup — documented in the Cli README).

### MCP tools **[Q2]**

| Tool | Input | Output | Notes |
|------|-------|--------|-------|
| `get_context` | unchanged | unchanged framing; a new block `## inbox (picked up, not yet filed by the dream)` | unconsumed lines of `inbox/bus-*.md` and `inbox/remember-*.md` only, ≤ 20 % of the budget, newest file first, `[context truncated: <n> more inbox lines not included]` marker; reads at call time (no cache), so a line is visible as soon as the pickup's atomic write completes |

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `poll.activeSeconds` | `node.json` | 10 (code); **1 on Central** (owner O2) | positive integer; the §5 back-off rules always win over it; policy (18) may only lengthen it |
| `poll.idleSeconds`, `poll.safetyFetchMinutes` | `node.json` | 60, 15 | unchanged |
| `workHours` | `node.json` | absent on Central = always active | unchanged |
| `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE` | Central's node environment (`zyggy-node.service.d/pickup.conf`) | none (required on Central), `UTC` for the zone | `ZYGGY_TENANT` must equal `node.json` `tenant`; ignored on every other machine |
| `ZYGGY_SECRET_PATTERNS`, else `ZYGGY_INSTANCE_DIR` → `../.claude/hooks/secret-patterns.txt` | same | none | fail closed (AC-16) |
| Jitter cap (half the interval), inbox budget share (20 %), inbox file prefixes | code | as listed | change with a release; no knob |

---

## Behaviors & Conventions

- **One pass, in order** — list candidates under the checkout write lock (bytes read once); per file: parse and verify against the path's tenant → writer rules (`from` = folder machine, `type` = folder, file name = `id`) → `to` = `central` (else silently skipped) → `rejected` report: archive only → other report: the one `[observed]` line of Q-P1 → secret scan line by line (a report whose one line matches: archive only) → inbox write (skipped when present); then one `ArchiveAsync` for every file that reached the inbox or is archive-only; then one log line. A file whose inbox write failed is not archived in that pass — override: none.
- **When it runs** — after the start-up pull, and after every successful pull triggered by the doorbell (`Changed`) or by a safety fetch that found the branch moved, after the job pass of that pull; never on a `304`; at most once per pull — override: none.
- **Where it runs** — only in a node whose `node.json` `machine` is `central`; that node refuses to start without the pickup configuration — override: none (the drop-in is the configuration).
- **What it never does** — call a model, run git in the memory repository, write outside `inbox/`, read another tenant's subtree, rewrite a line, upgrade or downgrade a tag, delete an inbox file (deletion is the dream's rollup) — override: none.
- **Left files** — a file that fails verification or a writer rule stays where it is, is logged once per process lifetime, is reported by `zyggy verify`, and is archived later by 17's sweep under its 14-day rule — override: none.
- **Polling every second (Central)** — rate-limit reasoning, recorded in the Node README (AC-23): a conditional request answered `304` does not count against GitHub's primary limit when sent with an `Authorization` header ([REST best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)); every `200` (a real push) counts one of the account's 5,000 per hour, shared by all of the owner's tokens. The secondary limit is 900 points per minute for REST, most `GET` requests costing 1 point, and 100 concurrent requests ([REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)); Central at 1 s is ~60 requests per minute, the two laptops at 10 s add ~12, one request in flight per machine — under a tenth of the secondary budget, assuming the commits endpoint costs 1 point (GitHub says some endpoints cost more without publishing which; the measurement of AC-24 and the `x-ratelimit-*` headers would show it). The §5 back-off rules stay in force at 1 s: below 100 remaining or on 403/429 the poller waits at least 120 s (logged once), and an `x-poll-interval` header raises the interval. GitHub itself recommends webhooks over polling; §13 rejects inbound endpoints, so the conditional poll is the doorbell — override: `node.json` `poll.activeSeconds`.
- **Latency is measured, not promised** — the end-to-end delay is the poll interval plus GitHub's own propagation between a push and the REST answer changing, plus Central's pull; only AC-24's measurement may put a number in the docs — override: none.
- **Archive month** — the envelope's `created` UTC month, never the move date, so retries and 17's sweep land in the same folder — override: none.
- **The nightly dream** — unchanged from 28: 03:00 and on request, `zyggy dream` over the inbox the pickup fills; it never reads or writes the bus and is never submitted as a job (decision Q1) — override: none.
- **Log lines** — `phase=pickup`; Information once per pass that moved files; Warning once per left file per process lifetime; Warning per dropped secret-shaped line (pattern name and envelope id only); Error once per archive-failure streak — override: `node.json` `log.level`.

---

## Failure modes

| Situation | Observable outcome | Runbook entry |
|-----------|--------------------|---------------|
| Pickup configuration missing or wrong on Central (variables, principal directory, tenant ≠ `node.json`, zone, secret patterns) | node exit 3, `zyggy-node: configuration error: pickup: <detail>`; systemd does not restart (`RestartPreventExitStatus=3 6`); files wait on the bus | Node README "Pickup: configuration error" → install `zyggy-node.service.d/pickup.conf` from the example, `systemctl daemon-reload`, restart |
| A file fails verification or a writer rule | left in place; Warning `pickup: left <path>: <reason> (<field>)` once per process lifetime; `zyggy verify` lists it | "Pickup: file left on the bus" → `zyggy verify`; `unknown_key` → `rotate-hmac.md`; `writer_mismatch` → treat as a compromised key (`revoke-machine.md`) |
| A body line matches a secret pattern | line dropped from the inbox copy; Warning names pattern and envelope id; file archived | "Pickup: secret-shaped line dropped" → the value is in the bus history: rotate the leaked secret at its source; history rewrite only on the owner's decision |
| Inbox not writable (permissions, disk) | that file not archived; Error once per streak; retried every pass | "Pickup: archive failed / deferred" (inbox branch) → fix the memory tree's ownership (`zyggy` user) or disk |
| Archive write fails at `lock` / `pull` / `mv` / `commit` | inbox files kept, bus files stay; Error once per streak `pickup: archive failed at <stage>: <detail>`; retried every pass without rewriting inbox files | "Pickup: archive failed / deferred" → `bus-conflict.md` |
| Archive push refused 4 times | `Deferred`; the commit goes with Central's next bus write | same row → `bus-conflict.md` |
| Throttled or rate-limited at 1 s | poller in `Throttled` for ≥ 120 s (one Warning); pickup latency grows to minutes | "Pickup: slow (throttled)" → check other tools using the owner's tokens; raise `poll.activeSeconds` |
| GitHub unreachable | network back-off 10 s → 5 min (one Warning); no pickup until reachable | existing "Node: back-off" row |
| Unexpected exception in a pass | one Error naming the type; loop continues | "Pickup: archive failed / deferred" (report it) |
| Dream run fails or aborts | unchanged from 28 (run record, no half-commit, inbox intact) | 28's "failed or aborted dream run" row (exists) |

---

## Dependencies

| Package | License | Why |
|---------|---------|-----|
| — | — | **No new package.** `System.Text.Json`, the existing `Envelope`/`Bus`/`Memory`/`Dream` code, `Microsoft.Extensions.*`. `Directory.Packages.props` unchanged. |

---

## Deliberate deviations from the founding spec

Approved by the owner at the spec gate (2026-10-29). Only the owner edits `_specs/00 - Personal Agent Platform — Technical Specification.md`; the orchestrating session pastes the "After" text and lists every edit in the journal. "Before" quotes the current text verbatim (as of 2026-10-29); "After" is the replacement verbatim. A13-2 follows decision Q1; A13-9 follows decision Q2; A13-4..A13-6 follow decision Q3; A13-1, A13-3, A13-7 and A13-8 follow O1/O2. A13-10 (decision Q-P1, approved at the plan gate 2026-10-29) replaces the A13-3 text already pasted into the founding spec; its "Before" is that pasted text.

### A13-1 — the Node's responsibility (O1)

**§3 Components table, row `AgentBus.Node`.**
Before:
> | `AgentBus.Node` | .NET Worker Service | every machine incl. Central | Poll GitHub, pull, claim, execute, report, push. Publish registry. |

After:
> | `AgentBus.Node` | .NET Worker Service | every machine incl. Central | Poll GitHub, pull, claim, execute, report, push. Publish registry. On Central, pick up the reports and context addressed to Central into the memory inbox and archive them (§7 step 1). |

### A13-2 — the dream stays a direct timer on Central (decision Q1)

**§3 Central agent instance, third bullet.**
Before:
> - Nightly at 03:00 and on the owner's request, `zyggy-dream.service` runs `zyggy dream` (timer and a request-file path unit); from 13 the timer submits a `dream` job instead.

After:
> - Nightly at 03:00 and on the owner's request, `zyggy-dream.service` runs `zyggy dream` (timer and a request-file path unit). The dream reads no bus file: Central's `AgentBus.Node` copies what the machines send into the inbox as it arrives (§7 step 1; amended 29 October 2026, deliverable 13).

**§6 Central executing its own jobs, first paragraph.**
Before:
> Central runs the same `AgentBus.Node`, machine name `central`, project = the tenant/user memory directory or Central-local projects. Scheduled work (mail triage, sweeps, dream) is submitted by systemd timers as jobs to `tenants/<org>/nodes/central/jobs/`, so every action, even local, is a ledger entry attributed to a tenant.

After:
> Central runs the same `AgentBus.Node`, machine name `central`, project = the tenant/user memory directory or Central-local projects. Scheduled work that acts on the bus or on a project (sweeps, mail triage) is submitted by systemd timers as jobs to `tenants/<org>/nodes/central/jobs/`, so every such action, even local, is a ledger entry attributed to a tenant. The dream pass is not a job: it reads only the memory repository, runs as its own timer unit, and its ledger is the memory repository's `dream YYYY-MM-DD` commit and the run record (amended 29 October 2026, deliverable 13).

### A13-3 — dream step 1 becomes Central's pickup (O1)

**§7 Dream pass, step 1.**
Before:
> 1. `agentbus dream ingest`: fetch the bus; copy the new `reports/` and `context/` files addressed to Central (`to: central`) from every `tenants/<org>/nodes/<machine>/` of the tenant into that tenant/user's `inbox/` as one file each; move processed bus files to `tenants/<org>/archive/`. A file whose `tenant` field differs from the subtree it sits in is never ingested (amended 28 October 2026, deliverable 12; the sender writes its own folder, §4).

After:
> 1. Pickup (not part of the nightly run): each time Central's `AgentBus.Node` pulls a change, it verifies every `reports/` and `context/` file addressed to Central (`to: central`) in every `tenants/<org>/nodes/<machine>/` of the tenant except Central's own, copies its body unchanged into that tenant/user's `inbox/` as one file (`bus-context-<from>-<id>-<date>.md`, `bus-report-<from>-<job>-<id>-<date>.md`; lines matching a secret pattern are dropped; a `rejected` report is not copied), and moves the processed files to `tenants/<org>/archive/<YYYY-MM of created>/` in one commit. A file whose `tenant` field differs from the subtree it sits in, that does not verify, or whose `from` is not its folder's machine is never ingested and stays for the sweep (amended 29 October 2026, deliverable 13).

### A13-4 — Central's archive move in the layout and write rules (decision Q3)

**§4 Repository layout, the `archive/` line.**
Before:
> `    archive/YYYY-MM/                # moved by Central's sweep after 14 days`

After:
> `    archive/YYYY-MM/                # moved by Central: on pickup (files addressed to Central, §7 step 1) or by the sweep after 14 days; YYYY-MM = the envelope's created month`

**§4 Write rules, third bullet.**
Before:
> - Only Central writes `archive/`.

After:
> - Only Central writes `archive/`, and the only writes there are `git mv` moves of `reports/` and `context/` files from any machine's folder of its own tenant (amended 29 October 2026, deliverable 13).

### A13-5 — the pickup row of the state machine (decision Q3)

**§4 State machine, a row inserted before the 14-day row.**
Before (the existing last row, unchanged, quoted for position):
> | `reports/`, `context/` older than 14 days | `archive/YYYY-MM/` (same tenant prefix) | Central | `git mv` |

After (new row, then the existing row reworded to share the month rule):
> | `reports/`, `context/` of another machine addressed to Central, after their body is in Central's inbox | `archive/YYYY-MM/` (same tenant prefix, month of `created`) | Central (pickup) | `git mv`, one commit per pass |
> | `reports/`, `context/` older than 14 days | `archive/YYYY-MM/` (same tenant prefix, month of `created`) | Central (sweep) | `git mv` |

### A13-6 — the `archive` commit action (decision Q3)

**§5 Git operations, third bullet.**
Before:
> - Every write sequence is: `pull --rebase` → local change → `commit -m "<tenant> <action> <id> <from>→<to>"` → `push`, where `<action>` is `job` (a submission), `claim`, `report`, `reject` or `context`.

After:
> - Every write sequence is: `pull --rebase` → local change → `commit -m "<tenant> <action> <id> <from>→<to>"` → `push`, where `<action>` is `job` (a submission), `claim`, `report`, `reject` or `context`; Central's archive moves commit `"<tenant> archive <n> central→archive"` with one line per move in the commit body (amended 29 October 2026, deliverable 13).

### A13-7 — the poll loop wakes the pickup; jitter scales (O2)

**§5 Poll loop, item 2.**
Before:
> 2. `304` → sleep and repeat. `200` → store the new ETag, run `git pull --rebase`, process `tenants/<my tenant>/nodes/<me>/jobs/`, then continue. Commits touching other machines' folders or other tenants' subtrees wake the poller like any other commit but produce no work.

After:
> 2. `304` → sleep and repeat. `200` → store the new ETag, run `git pull --rebase`, process `tenants/<my tenant>/nodes/<me>/jobs/`, on Central then run the pickup of §7 step 1, then continue. Commits touching other machines' folders or other tenants' subtrees wake the poller like any other commit but produce no work, except Central's pickup of files addressed to it (amended 29 October 2026, deliverable 13).

**§5 Poll loop, item 3.**
Before:
> 3. Interval: 10 s inside `work_hours`, 60 s outside; jitter ±2 s; one in-flight request at a time.

After:
> 3. Interval: 10 s inside `work_hours`, 60 s outside; Central, which has no work hours, uses 1 s (`poll.activeSeconds`, §10); jitter ±2 s, at most half the interval; one in-flight request at a time (amended 29 October 2026, deliverable 13).

### A13-8 — Central's node configuration (O1, O2)

**§10 `node.json` example, the `poll` line.**
Before:
> `  "poll": { "activeSeconds": 10, "idleSeconds": 60, "safetyFetchMinutes": 15 },`

After:
> `  "poll": { "activeSeconds": 10, "idleSeconds": 60, "safetyFetchMinutes": 15 },   // Central: "activeSeconds": 1 (deliverable 13)`

**§10 Central, systemd units bullet — the `agentbus-node.service` clause.**
Before:
> `agentbus-node.service` (`Type=notify`, `Restart=on-failure`, `RestartPreventExitStatus=3 6` — a start-up refusal waits for a human, a crash restarts after 30 s; shipped as `deploy/systemd/zyggy-node.service` with the release, deliverable 10),

After:
> `agentbus-node.service` (`Type=notify`, `Restart=on-failure`, `RestartPreventExitStatus=3 6` — a start-up refusal waits for a human, a crash restarts after 30 s; shipped as `deploy/systemd/zyggy-node.service` with the release, deliverable 10; on Central the drop-in `zyggy-node.service.d/pickup.conf` names the memory principal and the secret-pattern file the pickup needs, and the node refuses to start without them, deliverable 13),

### A13-9 — `get_context` returns picked-up lines (decision Q2)

**§7 Hub MCP surface table, row `get_context`, Notes column.**
Before:
> ranked by match on slug, `name`, `aliases` and `description` (exact, then all terms, then any term), then `updated` descending; searches only the principal's `<side>/<category>/<slug>.md` files and the last 30 `daily/YYYY-MM-DD.md` files (daily lines at most 20 % of the budget); `inbox/`, `auto/`, `archive/`, the monthly roll-ups and the identity files are never returned; the budget is `max_tokens × 4` bytes, cut at a line boundary with a marker (amended 28 October 2026)

After:
> ranked by match on slug, `name`, `aliases` and `description` (exact, then all terms, then any term), then `updated` descending; searches only the principal's `<side>/<category>/<slug>.md` files, the last 30 `daily/YYYY-MM-DD.md` files (daily lines at most 20 % of the budget) and the lines of `inbox/bus-*.md` and `inbox/remember-*.md` the dream has not yet consumed (at most 20 % of the budget, newest first), so a fact picked up from the bus or remembered on Central is usable before the nightly dream; the other inbox files, `auto/`, `archive/`, the monthly roll-ups and the identity files are never returned; the budget is `max_tokens × 4` bytes, cut at a line boundary with a marker (amended 28 and 29 October 2026, deliverables 11 and 13)

### A13-10 — a report becomes one `[observed]` line (decision Q-P1, plan gate)

Follow-up to A13-3, which is already in the founding spec; its "copies its body unchanged" no longer holds for reports.

**§7 Dream pass, step 1 (as amended by A13-3).**
Before:
> 1. Pickup (not part of the nightly run): each time Central's `AgentBus.Node` pulls a change, it verifies every `reports/` and `context/` file addressed to Central (`to: central`) in every `tenants/<org>/nodes/<machine>/` of the tenant except Central's own, copies its body unchanged into that tenant/user's `inbox/` as one file (`bus-context-<from>-<id>-<date>.md`, `bus-report-<from>-<job>-<id>-<date>.md`; lines matching a secret pattern are dropped; a `rejected` report is not copied), and moves the processed files to `tenants/<org>/archive/<YYYY-MM of created>/` in one commit. A file whose `tenant` field differs from the subtree it sits in, that does not verify, or whose `from` is not its folder's machine is never ingested and stays for the sweep (amended 29 October 2026, deliverable 13).

After:
> 1. Pickup (not part of the nightly run): each time Central's `AgentBus.Node` pulls a change, it verifies every `reports/` and `context/` file addressed to Central (`to: central`) in every `tenants/<org>/nodes/<machine>/` of the tenant except Central's own and writes one file per envelope into that tenant/user's `inbox/`: a context envelope's body unchanged (`bus-context-<from>-<id>-<date>.md`; lines matching a secret pattern are dropped), a report as exactly one line `- [observed] <created date> [report <job id first 8>]: <first non-empty line of its body>` (`bus-report-<from>-<job>-<id>-<date>.md`; a `rejected` report, a report without a non-empty line, or one whose line matches a secret pattern gets no inbox file); the full report stays readable in the archive. It then moves the processed files to `tenants/<org>/archive/<YYYY-MM of created>/` in one commit. A file whose `tenant` field differs from the subtree it sits in, that does not verify, or whose `from` is not its folder's machine is never ingested and stays for the sweep (amended 29 October 2026, deliverable 13).

**Roadmap (project-manager's edit, not part of the founding spec):** the #13 Goal still describes `zyggy-dream.timer` submitting a job and `zyggy dream ingest|rollup` verbs; under O1 and decision Q1 it should read as this spec's User Story. That edit belongs to the project-manager.

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| The same fact sent twice | two envelopes, two inbox files; the dream merges the restatement |
| A burst after an outage (dozens of files from several machines) | one pass, one archive commit, one push |
| The dream runs while the pickup writes a new inbox file | the file is atomic and not in the dream's snapshot; it is filed by the next run; the rollup only deletes files quiet for 24 h and consumed |
| `zyggy memory remember` writes `remember-<date>.md` while the pickup writes `bus-…` files | different names; no shared file |
| The archive commit fails for more than a week and the dream has consumed and deleted the inbox file | the next successful pass would re-create the inbox file (residual of the idempotence rule); the archive-failure Error line was logged at the start of the streak; the dream merges the duplicate as a restatement |
| A report whose body starts with a heading or blank lines (`REPORT`, `## REPORT`) | the first non-empty line is used as is, heading text included — a mechanical rule, no model call; the full report stays in the archive for `zyggy status --id` |
| A report to Central answering a probe job (04) | becomes one `[observed]` line like any `done` report; the dream drops it as expired moving state; `zyggy probe` finds the report in `archive/` (AC-25) |
| An envelope whose `created` lies in the future (clock skew on the laptop) | archived into that month's folder; inbox date from `created`; nothing rejected for skew |
| A context file in `nodes/central/context/` (should never exist) | not picked up (Central's own folder); `zyggy verify` judges it; 17's sweep archives it |
| `to: central` written in a different case (`Central`) | refused at parse time as an invalid machine label (§4 labels are lowercase) and left |
| Central's node restarts mid-pass | at start-up the pull and the pass run again; present inbox files are not rewritten; unarchived files are archived |
| Laptop and Central both write in the same second | the §5 retry rule; the pickup's archive commit rebases over the laptop's commit |
| Pickup configuration present on a laptop's environment | ignored; only `machine: central` builds the pickup |

---

## Out of Scope

- A `zyggy dream ingest` verb and a `zyggy dream rollup` verb (pickup is in the node; rollup is a stage of `zyggy dream`).
- Any change to `zyggy dream` itself, its prompts, checks, caps, ledger or units (28); submitting the dream as a bus job or a model-less "verb job" type (rejected by decision Q1).
- Dream step 5 `agents.md` from the registry (14); the Telegram identity diff (22).
- 17's 14-day sweep of what the pickup leaves. **Forward items for 17:** use the `created`-month archive rule; never archive a verifiable `to: central` file that the pickup has not had a chance to take (run the pickup first in the same Central process, or exclude such files).
- 21's `job_status` and `list_nodes`: `job_status` must read `archive/` as `TraceAsync` does (forward item); no `/health` pickup fields here.
- `hub --proxy` on laptops (21).
- DLP filtering and `share: true` lines from the work node (19, 20, 24) — the pickup copies what a verified sender sent; the work boundary is enforced at the work node.
- Policy-governed poll intervals (18); WebSocket/MQTT signalling, webhooks, any inbound endpoint (§13, O3).
- A local "already picked" state file (the idempotence rule above is the whole mechanism).
- `PROTOCOL.md` content (15); a `role` key in `node.json`.

---

## Open Questions

None. The three questions of the draft were answered by the owner on 2026-10-29, each as recommended, and are recorded as decisions Q1–Q3 at the head of the Decision Table: **Q1** the nightly dream stays the direct timer of 28 (A13-2); **Q2** `get_context` returns the unconsumed lines of `inbox/bus-*.md` and `inbox/remember-*.md`, at most 20 % of the budget (A13-9); **Q3** the §4/§5 wording for Central's immediate archive move as proposed — new state-machine row, write rule, `created`-month archive folder, one commit per pass `<tenant> archive <n> central→archive` (A13-4..A13-6). The spec is approved and planner-ready.
