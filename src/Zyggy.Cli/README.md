# Zyggy.Cli

The `zyggy` command-line tool, published as a single-file binary and placed on the PATH of every machine. It is how Claude Code skills and the user interact with the bus and the node without touching git or envelope formats by hand.

## Verbs

| Verb | Used by | What it does |
|------|---------|--------------|
| `submit --to <machine> --project <path|name> [--agent <name>] [--timeout <minutes>] [--allowed-tools <a,b>]… [--priority low|normal|high] [<body>]` | Central's `delegate` skill, the user | Build, sign and publish a job envelope into `nodes/<machine>/jobs/` (deliverable 08). The body is the last argument or standard input. Every option is checked by shape before anything is signed (`to`/`project`/`agent`/`timeout_minutes`/`allowed_tools[i]`/`priority`/`body`; limits: project 1024, agent 64, tool item 128 characters, timeout 1–1440 min, body 64 KiB); the signed bytes are parsed and verified again before they are written. Prints `submitted <id> to <machine> (<path>)`. Exit 0; 7 committed locally but not pushed (the next bus write pushes it); 2 an option refused (`zyggy: <field>: <detail>`) or the self-check refused; 3 configuration (`node.json`, `signWith` missing, checkout not a repository / wrong branch / no `tenants/<tenant>/`); 6 the signing key is not in the store (the hint names `zyggy secret set`), the checkout busy, or the write failed at `pull`/`add`/`commit` (working copy restored). |
| `status [--id <ulid>]` | user, skills | Without `--id`: the node block (from `http://localhost:4711/health`, read through the loopback-only health client) and the bus block — `node <machine> (<tenant>) · zyggy-node <version> · health: green|amber|red|down [— <reasons>]`, the last poll/pull/head/running/claimed/last report/claude auth line, `bus head <sha12> · <n> tenant director(y|ies)`, the per-machine table `queued claimed rejected reports context`, and `recent reports (<shown> of <total>)`. "Green" is `HealthRules` in Core: red when `claude` is not logged in or the node has not polled for 3 × the idle interval; amber for back-off, an unprobed login, a low rate limit, never pulled or a version skew; down when nothing answers. Exit 0 green · 1 amber · 6 red or down · 3 configuration. With `--id`: the trace of one job — the `job` line, `state queued at|claimed by|rejected by|reported by <machine>`, one `report` line per answer and the newest verified report's body verbatim under `body:` (data, never interpreted). Exit 0 found · 1 not found · 2 not a ULID. Read-only: no pull, never the listen lock. `--costs 30d` is deliverable 25. |
| `report` | skills | Read a report by job id. |
| `context` | the `remember` skill on nodes | Emit a `context` envelope with facts for Central's memory inbox. |
| `discover` | node install, skills | Scan dev roots and write `registry/<machine>.yaml`; `--link` symlinks the shared skills into projects. |
| `verify [--verbose]` | operations, `restore-central.md` | The §8 audit of this tenant's subtree (deliverable 08): every file parsed and verified against the tenant named by its path, then the §4 write rules — the file name is the `id`, the type belongs in its folder, the writer is the folder's machine. One `FAIL <path> <outcome> [(<field>)]` line per drift (`invalid_signature`, `tenant_mismatch`, `unknown_key` (the key named), `id_differs`, `type_folder_mismatch`, `writer_mismatch`, `unexpected_path`, …), `WARN tenants/<other>/ second tenant directory (§13: one bus repository per tenant); not read` per other tenant directory, `OK`/`SKIP` lines and per-folder counts with `--verbose`, then `verified <n> ok, <f> failed, <k> skipped; <t> tenant director(y|ies); HEAD <sha12>`. Skipped: non-envelope names, the registry files (unsigned in v1), the root `policy.yaml` (deliverable 18). Exit 0 clean · 6 drift · 3 configuration. Read-only. |
| `run` | Central's direct fast path | Execute one job file locally, bypassing the poll loop (the ledger entry still exists). |
| `hub --proxy` | project `.mcp.json` on laptops | A local MCP proxy answering `get_context` from a cached copy of the bus and turning `remember` into a `context` envelope. |
| `init`, `diagnose` | install, support | Provision a machine from a join token; produce a redacted support bundle (§14). |
| `probe send --to <machine> [--count N] [--interval s] [--timeout s] [--ledger <file>]` | the transport proof (deliverable 04), Central | Push signed probe jobs to a machine and wait for each verified report; one line per probe, a summary line, every outcome appended to the sender's ledger. Exit 0 when all are done, 6 otherwise, 3 configuration. |
| `probe listen` | the transport proof, every machine | Poll the bus (conditional head check, 10 s ± 2 s) and answer every probe job addressed to this machine with a signed `done` report, until Ctrl+C. Exit 0 on Ctrl+C; 3 configuration (bad `node.json`, not a repository, wrong branch — nothing touched); 6 when the start-up check fails (missing signing key, rebase in progress, no tenant directory). |
| `probe stats [--to <machine>] [--ledger <file>]` | the transport decision record | The ledger per target: count, done, rejected, timeout, unverified_report (a report answering the probe was seen but did not verify: key mismatch or tampering; a failure), push_failed, p50/p95/min/max ms (nearest-rank), as a Markdown table. Exit 1 on an empty ledger. |
| `probe reach` | the reachability matrix, every machine | Four checks — the API head request, `git ls-remote` over HTTPS, `git push --dry-run` over HTTPS, `git ls-remote` over SSH — with git's `http.proxy`, `http.sslBackend`, `credential.helper` and the system proxy in the header. Writes nothing to the remote; never passes a credential to git. Exit 0 when `api`, `fetch` and `push` are ok, 6 otherwise; `ssh` never affects the exit code. |
| `secret set <name> [--hex]` | setup on every machine | Store a tenant-scoped secret read from standard input (`--hex` decodes hexadecimal text); the value is never given on the command line. Prints `stored <tenant>/<name> (<n> bytes)`. Exit 2 when a value is passed as an argument or the input is empty or not hexadecimal, 3 configuration. |

## Rules

- Built on System.CommandLine, one file per verb.
- All behaviour comes from `Zyggy.Core`: the CLI parses arguments, calls the library, prints results, and returns a non-zero exit code with a one-line error on failure.
- Never handles credentials itself; signing keys come from `ISecretStore`, git authentication is git's concern.

Founding spec: §3 (component table), §11 (status and costs), §14 (init, diagnose).

## node.json (minimal, deliverable 04)

Every bus verb reads `node.json`: `--config <path>`, else `ZYGGY_NODE_JSON`, else `%LOCALAPPDATA%\zyggy\node.json` on Windows and `~/.config/zyggy/node.json` on Linux.

```json
{
  "tenant": "<tenant>",
  "machine": "<machine>",
  "signWith": "<tenant>/1",
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

| Key | Rule | Default |
|-----|------|---------|
| `tenant` | lowercase label; **required** — there is no default tenant | — |
| `machine` | lowercase label; required | — |
| `signWith` | `<tenant>/<n>`, same tenant; required by `submit`, `probe send` and `probe listen`; not by `status`, `verify`, `probe reach` | — |
| `bus.owner`, `bus.repo`, `bus.localPath` (absolute), `bus.remote` | required; the remote is the checkout's `origin` URL (HTTPS or SSH form) | — |
| `bus.branch` | | `main` |
| `poll.activeSeconds`, `poll.idleSeconds` | positive integers | 10, 60 |
| `poll.safetyFetchMinutes` | positive integer; read by the Node service (07): the period of the unconditional `git fetch` that catches a head check that lies; the probe verbs validate it but otherwise ignore it | 15 |
| `workHours` | object `{ "timezone", "days", "from", "to" }` or absent; read by the Node service (07): `timezone` an IANA id (`Europe/Brussels`), `days` a non-empty array of `Mon`..`Sun` without duplicates, `from`/`to` as `HH:mm` with `to` after `from` (no overnight window); **absent = always inside work hours** (Central); refused with the dotted key (`workHours.timezone` unknown, `workHours.days[0]` not a day, `workHours.to` not after `from`); the probe verbs validate it but otherwise ignore it | absent |
| `maxConcurrentJobs` | positive integer; read by the Node service (07): how many jobs may run at once (a value above 4 is accepted with one advisory warning); the probe verbs validate it but otherwise ignore it | 2 |
| `log.level` | `Debug`, `Information`, `Warning` or `Error`; read by the Node service (07) for its rolling log file; the probe verbs validate it but otherwise ignore it | `Information` |
| `secrets.root` | the file secret store's root | the per-OS default |
| `claude.path` | the `claude` executable; a non-empty string | `claude` (resolved on `PATH`) |
| `claude.maxTurns` | positive integer; the turn limit of every job run (`--max-turns`) | 60 |
| `claude.defaultTimeoutMinutes` | positive integer; used when a job names no `timeout_minutes` | 30 |
| `claude.defaultAllowedTools` | array of non-empty strings; used when a job names no `allowed_tools` | `["Read", "Grep", "Glob"]` |
| `claude.maxBudgetUsd` | `null` or a positive number; the per-run `--max-budget-usd` | `null` (no cap) |
| `devRoots` | array of absolute paths; read now, enforced with the registry (deliverable 14) | `[]` |
| `nodeDir` | absolute path; job transcripts under `runs/<tenant>/` (kept 30 days), logs under `logs/` | `%LOCALAPPDATA%\zyggy` / `~/.local/share/zyggy` |
| any other key | ignored (the Node service adds its own) | |
| any key named `pat`, `token`, `hmac`, `secret`, `password` (any depth, any case) | **refused**: secrets never live in `node.json` | |

The `claude.*`, `devRoots` and `nodeDir` keys (deliverable 06) are read by the Node service (07); the probe verbs validate them but otherwise ignore them. A wrong value is refused with its dotted key named, for example `zyggy: configuration error: claude.maxTurns: must be a positive integer`; an absent `claude` section means the defaults above.

### Test hooks

Two environment variables exist for the integration tests only; neither is a credential and neither is a production override: `ZYGGY_BUS_API_BASE` (deliverable 04: points the bus host's API request at a loopback stand-in) and `ZYGGY_HEALTH_URL` (deliverable 08: the health URL `status` reads; loopback only — any other host is refused with exit 3 before a request exists; the production port 4711 never changes).

## Per-machine setup for the transport proof

The bus repository `zyggy-org/zyggy-bus` is created by hand (spec 04 AC-36): private, a ruleset on `main` blocking force-push and deletion, a `PROTOCOL.md` placeholder, `tenants/<tenant>/nodes/<machine>/{jobs,jobs/claimed,jobs/rejected,reports,context}` and `tenants/<tenant>/registry` with `.gitkeep` files, **and a `.gitattributes` with `* -text`** so no checkout ever rewrites an envelope's line endings. One fine-grained token per machine, scoped to that one repository.

**Central (Linux, user `zyggy`)**

1. `ssh-keygen -t ed25519 -f ~/.ssh/zyggy_zyggy-bus_ed25519 -N ''`; add the public key as a write deploy key; in `~/.ssh/config`: `Host github.com-zyggy-bus` → `HostName github.com`, `IdentityFile ~/.ssh/zyggy_zyggy-bus_ed25519`, `IdentitiesOnly yes`.
2. `git clone git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git <bus.localPath>`.
3. `openssl rand -hex 32 | zyggy secret set hmac/1 --hex` (keep the hex: every machine of the tenant needs the same bytes) and `zyggy secret set github-pat` (paste the token, Enter, Ctrl+D) — the token is for the API head check only (Contents read).
4. `zyggy probe reach`, then `zyggy probe listen` in a terminal (a foreground process in P0, no unit).

**Laptops (Windows)**

1. Copy the `win-x64` single-file `zyggy.exe` onto the PATH; install Git for Windows (Git Credential Manager is its default helper).
2. `git clone https://github.com/zyggy-org/zyggy-bus.git <bus.localPath>`; `git -C <bus.localPath> config core.autocrlf false` until the repository's `.gitattributes` is in place.
3. The same two `zyggy secret set` calls (the identical `hmac/1` hex; this laptop's fine-grained token, Contents read and write).
4. `zyggy probe reach`: the `push` check (`git push --dry-run`) makes Git Credential Manager ask for the token once and store it in the Windows Credential Manager.
5. **One documented git settings pass**, only if `reach` fails on the work laptop, as the owner's own settings (Zyggy never writes `git config`): `git config --global http.proxy http://<host>:<port>`, `git config --global http.sslBackend schannel` (trust the corporate root CA from the Windows store), `git config --global credential.helper manager`. Run `reach` again; a check that still fails goes into the transport decision record, not into more tweaking.

## Troubleshooting — Probe

| Situation | What to do |
|-----------|------------|
| Probe: proxy and TLS — `reach` shows `api` `unreachable (<class>)` or `tls failed` | The API request uses the system proxy with your Windows credentials (`HTTPS_PROXY` or the WinHTTP setting) and the OS trust store. `zyggy probe reach --verbose` prints the exception class; an inspecting proxy needs its root CA in the Windows store. Git has its own settings — see the settings pass above. |
| Probe: PAT — `api` is `auth failed` | `unauthorized`: the token expired, was revoked or lacks Contents on this repository. `no token`: `zyggy secret set github-pat` was never run on this machine. |
| Bus: keys — `listen` or `submit` exits 6 "signing key … is not in the secret store", or every job is `invalid_signature` | `zyggy secret set hmac/1 --hex` with the same hex on every machine. On Windows also check `core.autocrlf` (Core README, "Bus: CRLF conversion"). |
| Probe: git through the proxy — `fetch`/`push` `unreachable` or `tls failed` while `api` is `ok` | git does not read `HTTPS_PROXY` the same way: run the settings pass above once, then `reach` again. |
| Probe: no report — `send` prints `timeout` | Is `listen` running on the target, and what does its stderr say? Run `reach` on the target. A `rejected <reason>` line instead means the target refused the job: see the Core README "Bus: rejected envelopes". An `unverified_report` outcome means the target answered but its report did not verify (`unknown_key`: the sender does not hold the key the target signs with; `invalid_signature`: the bytes changed on the way): the fix is the keys row above: the same `hmac/1` hex on both machines. |
| Probe: `push_failed pull: …` from `send` | The sender's own checkout is dirty or behind; `send` does not stash (only `listen` does): `git -C <bus.localPath> status`, then `git stash` or `git checkout -- .`. |
| `node.json` — exit 3 `zyggy: configuration error: <key>: <detail>` | The key named is missing or invalid; `secrets never in node.json` means a key looked like a credential. The schema is above. |

## Troubleshooting — Submit / Status / Verify

| Situation | What to do |
|-----------|------------|
| Submit: `zyggy: <field>: <detail>` (exit 2) | An option is refused by shape before anything is signed: `to` a lowercase label; `project` a registry name (`[a-z0-9-]`) or an absolute path (POSIX or Windows), ≤ 1024 characters, no `..`; `agent` 1–64 of `A–Z a–z 0–9 . _ -`; `timeout_minutes` 1–1440; `allowed_tools[i]` no empty item (a double comma), ≤ 128 characters; `priority` `low`, `normal` or `high` (case-sensitive); `body` not blank, ≤ 64 KiB. Fix the option named. |
| Submit: `refusing to submit: the envelope does not round-trip (<field>)` (exit 2) | The signed bytes did not read back as the request — a bug in the writer, never a user error. Nothing was written. Report it with the field named. |
| Submit: `signing key <tenant>/<n> is not in the secret store` (exit 6) | The "Bus: keys" row above: `zyggy secret set hmac/<n> --hex` on this machine. |
| Submit: `submit failed at pull: …` (exit 6) | The sender's checkout could not be brought up to date (remote unreachable, authentication, a conflicting local change). Nothing was written; the working copy is as it was. `zyggy probe reach`, then `git -C <bus.localPath> status`. |
| Submit / status / verify: `the bus checkout is busy: another zyggy process held its lock` (exit 3 or 6) | Another `zyggy` command or the node held the checkout's write lock for the whole 60 s wait. Wait and retry; if it persists, a stale `.git/zyggy-bus.lock` from a killed process can be deleted once nothing else runs. |
| Submit: `submitted … (committed locally, not yet pushed …)` (exit 7) | The push was refused four times. The job is committed in your checkout and goes out with the next bus write from this machine (a `submit`, or the node's next claim or report). Nothing to undo. |
| Status: `health: down — no node service answering on http://localhost:4711/health` (exit 6) | The node service is not running on this machine (or binds another port, which deliverable 07 does not allow). Start the service; the bus block below is still complete. |
| Status: `health: red — …` (exit 6) | `claude not logged in`: run `claude login` as the service user. `not polling: last poll <age>` / `never polled`: the service is wedged or just started — its log under `<nodeDir>/logs/`. |
| Status: `health: amber — …` (exit 1) | Look later: `backing off until …` (the bus host rate-limited the node), `claude auth not yet probed` (just started), `rate limit low: <n>`, `never pulled`, `node <v>, cli <v>` (upgrade one of them). |
| Status: `not found in tenant <tenant>` (exit 1) | No file on the bus names that id: never submitted here, or already archived beyond what the bus still holds. |
| Verify: `FAIL … invalid_signature (sig)` / `tenant_mismatch (tenant)` / `id_differs (id)` / `type_folder_mismatch (type)` / `writer_mismatch (from|to)` | Drift: a file was edited after signing, written by hand, or moved by hand. The ledger is append-only: investigate with `git -C <bus.localPath> log -p -- <path>`; a hand-edited file is restored from history, never re-signed. |
| Verify: `FAIL … unknown_key (<tenant>/<n>)` | The file was signed with a key this machine does not hold — after a rotation, the retired key (deliverable 09 keeps retired keys verify-only); otherwise a foreign writer. |
| Verify: `WARN tenants/<other>/ second tenant directory …` (exit 6) | §13: one bus repository per tenant. The other directory is never read; remove it from the repository or give that tenant its own. |
| Verify: `FAIL <path> unexpected_path` | A file or folder §4 does not know (a stray folder, a file at the tenant root other than `policy.yaml`). Remove it or move it to a §4 place. |
| Status: the health endpoint answers but not with JSON | `health: down — …` after `unparsable body` or `HTTP <code>`: something else listens on 4711. `netstat`/`ss` for the port. |
