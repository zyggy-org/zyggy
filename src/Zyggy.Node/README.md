# Zyggy.Node

The always-running service on every machine, Central included: the only process that writes to the bus on a machine's behalf. Published as the single-file binary `zyggy-node`; installed as the systemd unit `zyggy-node.service` on Central and as the per-user logon task `zyggy-node` on a laptop (deliverable 10; `deploy/` in the release, procedure in `zyggy-core/runbooks/install-node.md`). Deliverable 07 built it; this file says what it does today.

## What it does

One loop (founding spec §5 and §6), the body of which is `Zyggy.Core.Bus.BusListener` — this project only hosts it:

1. **Start-up check**: the checkout must be a repository on the bus branch holding `tenants/<tenant>/`, with no rebase in progress and the signing key present; a dirty working copy is parked under a named stash with one warning. Only one listener per checkout.
2. **Resume**: the node's own `jobs/claimed/*.md` left behind by a previous run are verified and run again (no second claim); one that no longer verifies is left for the sweep.
3. **Poll** the bus host for the branch head with a conditional request every 10 s inside work hours and every 60 s outside (`workHours` in `node.json`; absent means always inside); Central polls every second (below). A `304` is silent.
4. **Safety fetch** every `poll.safetyFetchMinutes` regardless: `git fetch` and a comparison of `origin/<branch>` with `HEAD`; a head check that lied is caught with one warning, a pull and a pass.
5. **Pull** when the branch moved, then for every file in the inbox: verify, then ask the **dispatcher** (`Zyggy.Core.Jobs.JobDispatcher`, the §6 rules: work hours unless `priority: high`, a free slot under `maxConcurrentJobs`, the project not in flight here and its lock free). A deferred job stays in `jobs/` and is looked at again on the next pass; an admitted one is **claimed** (`git mv` into `jobs/claimed/`, one commit, push).
5b. **Pickup (Central only)**: after the job pass of every successful pull (the start-up pull, a changed head, a safety fetch that found the branch moved), the node reads every `context/` and `reports/` file the other machines of the tenant addressed `to: central`, checks tenant, signature and writer, and copies it into the memory inbox `inbox/bus-context-…` or `inbox/bus-report-…` (a context's lines unchanged minus any secret-shaped line; a report as one `[observed]` line; a `rejected` report not copied). It then moves the processed files to `archive/<YYYY-MM of created>/` in one commit `<tenant> archive <n> central→archive` and pushes. A file that fails a check stays where it is, with one warning. The pickup never calls a model; the nightly dream files the inbox (founding spec §7 step 1). From deliverable 41 the same pass also files the laptops' `items/` archive items into the memory repository (its only memory git; "Pickup: archive items" below).
6. **Run** the admitted job through the job runner and the fake or real `claude -p` as a tracked task, so the loop keeps polling while up to `maxConcurrentJobs` run.
7. **Report**: a signed `reports/<id>.md` (status, summary, cost, turns), the claimed file deleted, one commit, push.
8. **Health**: `GET http://localhost:4711/health` on loopback only (below).
9. **Login probe**: `claude auth status` at start, hourly and after a run that failed with an authentication error, into the `claudeAuthOk` health field.

Not here yet: discovery and the registry (14), `policy.yaml` (18), the DLP filter (19).

## Running it

```text
zyggy-node --config <path-to-node.json>     # a console process; Ctrl+C stops it (exit 0)
zyggy-node                                    # node.json from ZYGGY_NODE_JSON, else the per-user default path (below)
zyggy-node --version                          # the informational version; nothing is read
```

Under a service manager the same binary runs unchanged: the Windows-service and systemd lifetimes are no-ops outside one.

## node.json

The single configuration file (§10). Path: `--config`, else `ZYGGY_NODE_JSON`, else `%LOCALAPPDATA%\zyggy\node.json` on Windows and `~/.config/zyggy/node.json` elsewhere. The full schema is in the Cli README; the keys the service reads:

| Key | Meaning | Default |
|-----|---------|---------|
| `tenant`, `machine` | required; there is no default tenant | — |
| `signWith` | the key id the node signs its reports with; **mandatory for the service** (exit 3 without it) | — |
| `bus.*` | the bus repository and the local checkout | — |
| `poll.activeSeconds`, `poll.idleSeconds`, `poll.safetyFetchMinutes` | the two intervals and the safety-fetch period | 10, 60, 15 (Central: `activeSeconds` 1) |
| `workHours` | `{ "timezone": "<IANA id>", "days": ["Mon", …], "from": "HH:mm", "to": "HH:mm" }`; **absent = always inside** | absent |
| `maxConcurrentJobs` | parallel runs on this machine | 2 |
| `claude.*` | the `claude` executable and the run defaults | `claude` on `PATH`, 60 turns, 30 min, `Read, Grep, Glob` |
| `nodeDir` | transcripts under `runs/<tenant>/`, the log under `logs/` | the per-OS data directory |
| `log.level` | `Debug`, `Information`, `Warning`, `Error` | `Information` |
| `secrets.root` | the file secret store's root | the per-OS default |

Secrets are never in it; any key named like a credential is refused.

## Exit codes

| Code | Meaning | Where it is said |
|------|---------|------------------|
| 0 | a normal stop (Ctrl+C, SIGTERM, the service manager) | — |
| 3 | `node.json` refused: missing or invalid key, no `tenant`, no `signWith`, a secret-looking key, an unknown argument; on Central, the pickup configuration missing or wrong | stderr: `zyggy-node: configuration error: <key>: <detail>` (on Central: `pickup: <detail>`); nothing started |
| 6 | a start-up condition: `NotARepository`, `WrongBranch`, `NoTenantDirectory`, `RebaseInProgress`, `KeyMissing` (the line names the key, the store and the `zyggy secret set` command), `ListenerRunning`, the log directory not creatable, the health port in use; or `StoreUnavailable` (deliverable 09: the secret store cannot be opened) | one `[ERR] refusing to start: <condition> — see README "Node: refused to start"` line in the log (the log directory case: stderr), or `… — see README "Secrets: store unavailable"` for the store (Core README "Troubleshooting — Secrets"); `/health` is never bound |
| 1 | an unexpected fault | one `[ERR] node faulted: <ExceptionType>` line |

The Core README's "Troubleshooting — Node" table says what to do for each.

## /health

`GET http://localhost:4711/health` → `200 application/json`, `Cache-Control: no-store`; any other path `404`, any other method `405`. Bound to loopback only; no authentication. Exactly the §11 fields, camelCase, nulls written, timestamps `YYYY-MM-DDTHH:MM:SSZ`:

| Field | Source |
|-------|--------|
| `version` | this binary's informational version (the same as `zyggy --version`) |
| `tenant`, `machine` | `node.json` |
| `lastPollUtc`, `lastEtag`, `lastPullUtc`, `busHeadSha`, `jobsRunning`, `jobsClaimedByMe`, `lastReportUtc`, `rateLimitRemaining`, `backoffUntilUtc` | the listener's status snapshot: the last head check and the ETag it will send next, the last successful pull and the checkout's `HEAD`, admitted runs in flight, files in this machine's `jobs/claimed/` after the last pull, the last report commit, the bus host's remaining request budget, the back-off instant if polling backs off |
| `claudeAuthOk` | the login probe: `true`, `false`, or `null` until the first probe answers |
| `agentCoreSha` | `null` until deliverable 15 |

There is **no verdict field**: `zyggy status` (deliverable 08) decides what "green" means from these fields.

## Log

One rolling file per machine, `<nodeDir>/logs/zyggy-<yyyyMMdd>.log`, a new file every day, the last 14 kept; the same lines on the console (journald on Central). Every line:

```text
<UTC timestamp>Z [LEVEL] <tenant> <machine> <phase> <jobId> <category>: <message>
```

`phase` is one of `poll pull claim run report reject pickup` and `jobId` is set inside a run; both are blank on host lines. One line per state change, nothing on a quiet tick; never the job body, the prompt, a key or a credential. `log.level: Debug` adds the host's diagnostics.

## Central

The same binary with `machine: central`. Central has no work hours and polls every second:

```json
{
  "tenant": "acme",
  "machine": "central",
  "signWith": "acme/1",
  "bus": { "owner": "acme-org", "repo": "acme-bus", "branch": "main", "localPath": "<bus.localPath>", "remote": "https://github.com/acme-org/acme-bus.git" },
  "poll": { "activeSeconds": 1, "idleSeconds": 60, "safetyFetchMinutes": 15 }
}
```

Its pickup needs the memory principal and the secret patterns from the environment: install `deploy/systemd/zyggy-node.service.d/pickup.conf.example` as `/etc/systemd/system/zyggy-node.service.d/pickup.conf` (`ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE`, `ZYGGY_SECRET_PATTERNS`). Without it the node on Central exits 3; a laptop ignores these variables. The same drop-in carries, from deliverable 41, `Environment=ZYGGY_INSTANCE_DIR=<instance-dir>/instance` and `LoadCredential=m365-app-key:<zyggy-home>/.config/zyggy/m365-app.key` for the archive items below; without them the node starts, and the items wait.

## Pickup: archive items

Deliverable 41: a laptop's `zyggy archive` leaves a signed `item` envelope in its own `tenants/<tenant>/nodes/<machine>/items/<id>.md`, addressed `to: central`. Central's pickup files it into the project archive of the memory repository — the same writer, checks, sidecar, `[stated]` index line and memory commit as `zyggy memory archive add` in a Central session.

- **Order**: in every pass, every machine's `context/` and `reports/` files first (as above), then every machine's `items/` files, machines and files in ordinal order.
- **Per item**: signature, tenant and writer rule (`id` = file name, `type: item`, `from` = the folder's machine, `to: central`); then the bytes — decoded from the body (`via: bus`), or downloaded read-only from `<offload.root_folder>/<project>/<drive_name>` in the owner's OneDrive through the Graph application key (`via: onedrive`; at most one lookup a minute per item, `Retry-After` honoured, no Graph write ever); size and SHA-256 checked against the signed values; then the archive writer. Each memory commit is pushed before the bus commit that removes its item.
- **One bus commit per pass**: the processed `context/` and `reports/` files moved to `archive/<YYYY-MM>/`, each settled `items/` file removed, and one signed report per item written into `central`'s `reports/` — `status: done` (body: `archived:`, `sidecar:`, the index line, `commit: <sha> pushed | push deferred`, and for the OneDrive route `onedrive: <path> (adopted by the next offload)`), or `status: failed` with `reason: archive_refused` (body: `archive: refused: <reason> (<detail>)`, and for the OneDrive route a line saying the OneDrive copy stays). Subject `<tenant> archive <n> central→archive`; one body line `<items path> → filed archive/<project>/<slug>.<ext>` or `→ refused <reason>` per item. The pass's Information line ends `; items filed <f>, refused <r>` when an item was settled.
- **Waits**: an item that cannot be settled now (memory busy or not on a branch, the archive configuration missing, OneDrive not reachable or the file not there yet) stays in `items/` with no report and is tried again on the next pass; one log line per cause. A OneDrive-route item whose file is still missing `item_drive_wait_hours` (`archive.json`, default 24) after its `created` is refused `drive_missing`.
- **Never filed, never removed**: an item that does not verify or breaks the writer rule stays, with one warning (the row "Pickup: file left on the bus").
- The laptop side shows the outcome: `zyggy status --id <id>` says `queued at <machine> for central`, then `reported by central` with Central's report body as data.

Each item file stays in every clone's bus history (a bus-route item with its full content); see "Bus repository too large".

## Polling every second

Central asks the bus host for the branch head with a conditional request every second, ±0.5 s (the jitter is at most half the interval; the laptops keep 10 s ±2 s and 60 s ±2 s). The budget:

- A conditional request answered `304` does not count against GitHub's primary limit when it carries an `Authorization` header; every `200` (a real push) costs 1 of the account's 5,000 requests per hour, shared by all of the owner's tokens.
- GitHub's secondary limit is 900 points per minute for REST, most `GET` requests costing 1 point: Central at 1 s is about 60 requests per minute, two laptops at 10 s add about 12, one request in flight per machine.
- The §5 back-off stays in force at 1 s: below 100 remaining requests, or on `403`/`429`, the node waits at least 120 s (logged once); an `x-poll-interval` header raises the interval.
- No webhook and no socket: Central has no inbound endpoint (founding spec §13); the conditional poll is the doorbell.

Latency: within seconds; measured value pending (deliverable 13, AC-24).

## Troubleshooting — Pickup

| Situation | What you see | What to do |
|-----------|--------------|------------|
| Pickup: configuration error | the node on Central exits 3: `zyggy-node: configuration error: pickup: <detail>` (a `ZYGGY_*` variable missing, `ZYGGY_TENANT` not `node.json`'s tenant, an unknown time zone, the secret patterns missing or malformed) | install or fix `/etc/systemd/system/zyggy-node.service.d/pickup.conf` from `deploy/`, then `systemctl daemon-reload && systemctl restart zyggy-node`; the files wait on the bus meanwhile |
| Pickup: file left on the bus | `[WRN] … pickup: left <path>: <reason> (<field>)`, once per file per run of the node; `zyggy verify` names the same file. Applies to `context/`, `reports/` and `items/` files alike | `unknown_key` / `invalid_signature`: the sender's key differs from Central's (`runbooks/rotate-hmac.md`); `writer_mismatch` / `tenant_mismatch`: a machine wrote where it may not (`runbooks/revoke-machine.md`); `wrong_recipient` (`to`): an item not addressed to `central`; an invalid field or body: the item was edited or written by hand. The file stays for the 14-day sweep; an item left like this is never filed and gets no report — archive the file again from the laptop |
| Archive from a laptop: refused on Central | `[WRN] … pickup: item <id> refused: <reason> (<detail>)` once per item; the item's `items/` file removed and a `status: failed`, `reason: archive_refused` report from `central`, body `archive: refused: <reason> (<detail>)`; on the laptop `zyggy status --id <id>` shows it | Central re-runs every archive check on the bytes it obtains: `type_refused`, `too_large`, `project_cap` / `total_cap`, `slug_taken` (another file already has that slug: pick another `--slug`), `secret_pattern` / `contact_detail` (never retry, rephrase or split it), `office_refused`, `drive_mismatch` (the OneDrive file's size or SHA-256 is not what the laptop signed: it changed after sending — archive it again). **`drive_missing`**: the file never reached OneDrive within `item_drive_wait_hours` (default 24) — check that the laptop's OneDrive client is running, signed in to the company account and not paused; compare the laptop's `node.json` `archive.oneDriveFolder` with Central's `offload.root_folder` (it must be that folder's synced copy, not the employer's OneDrive); then archive the file again. For a OneDrive-route item the copy in OneDrive stays: delete it there if wanted |
| Archive from a laptop: item waits on the bus | the item stays in `items/` with no report; `[WRN] … pickup: item <id> waits: memory <detail>` once (memory busy, `index.lock` held, not on a branch, an operation in progress, an I/O error) | the memory repository on Central needs a hand: `git -C <instance-dir>/memory status` as the service user; finish or abort the operation in progress, remove a stale `index.lock` once nothing else runs; the next pass files the item exactly once. A wait with no such line: the rows "Pickup: archive configuration missing" and "Pickup: OneDrive fetch …" |
| Pickup: OneDrive fetch failing | `[INF] … pickup: item <id> waits for OneDrive` (the file is not there yet: normal for a minute or two after sending), or `[WRN] … pickup: OneDrive fetch failing for item <id>: <cause> (<detail>)` (throttled, 5xx, network), `[WRN] … OneDrive fetch throttled: every OneDrive fetch held until <time>`, or `[ERR] … OneDrive fetch failing … — runbook "Pickup: OneDrive fetch failing"` for a `403` | the items wait and are retried on their own, at most once a minute each; a throttle holds every OneDrive fetch until `Retry-After`. A **403** means the application's grant on the `Zyggy/archive` folder is missing or was removed: check it as for the offload (spec 38's grant check, `zyggy memory archive offload` on Central shows the same 403) and restore the grant; none of these causes counts towards the `drive_missing` deadline |
| Pickup: OneDrive fetch not configured | `[ERR] … pickup: OneDrive fetch not configured: <detail> (OneDrive-route items wait)` once; bus-route items, contexts and reports carry on | the drop-in lacks the deliverable 41 lines, or `m365.json` / the Graph key cannot be read: add `Environment=ZYGGY_INSTANCE_DIR=<instance-dir>/instance` and `LoadCredential=m365-app-key:<zyggy-home>/.config/zyggy/m365-app.key` to `/etc/systemd/system/zyggy-node.service.d/pickup.conf` (from `deploy/`), then `systemctl daemon-reload && systemctl restart zyggy-node` |
| Pickup: archive configuration missing | `[ERR] … pickup: archive configuration missing: <detail>` once; every item waits; contexts and reports carry on | `archive configuration missing (ZYGGY_INSTANCE_DIR not set)`: add `Environment=ZYGGY_INSTANCE_DIR=<instance-dir>/instance` to the drop-in, `systemctl daemon-reload && systemctl restart zyggy-node`. Otherwise the detail names what is wrong in `<instance-dir>/instance/archive.json` (the same rules as `zyggy memory archive add`); fix it and restart |
| Pickup: memory push deferred / files not committed | `[WRN] … pickup: item <id> filed; memory push deferred` and the report ends `commit: <sha> push deferred`; or an item filed on disk whose commit failed (the item waits) | as the spec 37 rows "Archive push deferred" and "Archive files on disk but not committed": a deferred push goes out with the next memory writer (the next `archive` verb, pickup item or dream); an uncommitted item is recognised by its SHA-256 on the next pass, committed and pushed, with no second item or index line |
| Bus repository too large | clones and pulls of the bus get slow; the GitHub repository page warns about its size | every bus-route item adds about its size to the bus history for good (the protected `main` cannot be rewritten); GitHub recommends staying under about 1 GB — on the order of a hundred large items. Prefer the OneDrive route (`archive.oneDriveFolder`) on laptops that can reach it; ask before sending large files through the bus |
| Pickup: archive failed / deferred | `[ERR] … pickup: archive failed at <stage>: <detail>` once per failure streak, or `picked up … ; archive deferred` | the inbox file is already written and is never written twice; `pull`/`lock`: wait or check the network (`zyggy probe reach`); a push refused four times goes with the next bus write; a push that keeps failing: `runbooks/bus-conflict.md` |
| Pickup: secret-shaped line dropped | `[WRN] … pickup: <id>: dropped 1 line (pattern <name>)` or `pickup: <id>: report line refused (pattern <name>)` | nothing to undo: the line never reached memory and is archived with its envelope; tell the sender's owner which pattern matched |
| Pickup: slow (throttled) | `/health` `backoffUntilUtc` set, one throttle warning in the log | the 1 s poll waits at least 120 s after a `403`/`429` or below 100 remaining requests; it resumes on its own; check other tokens of the same account |

## Structure

`NodeHost` (the entry point: arguments, `node.json`, the host), `PollLoop` (the one hosted service: start-up check → resume → the listener's loop; stop drains the runs and releases the listen lock), `HealthServer` (Kestrel on loopback), `ClaudeAuthProbe`, `NodeHealth` (the `/health` body). All logic lives in `Zyggy.Core`, composed through `AddNodeServices(node.json)` + `AddJobDispatcher()`; this project wires Serilog, the service lifetimes and the health endpoint around it.

Tests: `tests/Zyggy.Integration/Node/` runs the real host against a local bare git repository and `tools/fake-claude`; `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` part 1 is the P1 gate scenario.
