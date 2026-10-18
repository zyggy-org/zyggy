# Zyggy.Cli

The `zyggy` command-line tool, published as a single-file binary and placed on the PATH of every machine. It is how Claude Code skills and the user interact with the bus and the node without touching git or envelope formats by hand.

## Verbs

| Verb | Used by | What it does |
|------|---------|--------------|
| `submit` | Central's `delegate` skill, the user | Build a signed job envelope from options and push it into `nodes/<target>/jobs/`. |
| `status` | user, skills | Show this node's health (from the local health endpoint) and the state of jobs on the bus; `--costs 30d` sums `cost_usd` per machine and project. |
| `report` | skills | Read a report by job id. |
| `context` | the `remember` skill on nodes | Emit a `context` envelope with facts for Central's memory inbox. |
| `discover` | node install, skills | Scan dev roots and write `registry/<machine>.yaml`; `--link` symlinks the shared skills into projects. |
| `verify` | operations | Re-check every signature on the bus and report drift. |
| `run` | Central's direct fast path | Execute one job file locally, bypassing the poll loop (the ledger entry still exists). |
| `hub --proxy` | project `.mcp.json` on laptops | A local MCP proxy answering `get_context` from a cached copy of the bus and turning `remember` into a `context` envelope. |
| `init`, `diagnose` | install, support | Provision a machine from a join token; produce a redacted support bundle (§14). |
| `probe send --to <machine> [--count N] [--interval s] [--timeout s] [--ledger <file>]` | the transport proof (deliverable 04), Central | Push signed probe jobs to a machine and wait for each verified report; one line per probe, a summary line, every outcome appended to the sender's ledger. Exit 0 when all are done, 6 otherwise, 3 configuration. |
| `probe listen` | the transport proof, every machine | Poll the bus (conditional head check, 10 s ± 2 s) and answer every probe job addressed to this machine with a signed `done` report, until Ctrl+C. Exit 0 on Ctrl+C; 3 configuration (bad `node.json`, not a repository, wrong branch — nothing touched); 6 when the start-up check fails (missing signing key, rebase in progress, no tenant directory). |
| `probe stats [--to <machine>] [--ledger <file>]` | the transport decision record | The ledger per target: count, done, rejected, timeout, push_failed, p50/p95/min/max ms (nearest-rank), as a Markdown table. Exit 1 on an empty ledger. |
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
| `signWith` | `<tenant>/<n>`, same tenant; required by `send` and `listen`, not by `reach` | — |
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
| Probe: keys — `listen` exits 6 "signing key … is not in the secret store", or every job is `invalid_signature` | `zyggy secret set hmac/1 --hex` with the same hex on every machine. On Windows also check `core.autocrlf` (Core README, "Bus: CRLF conversion"). |
| Probe: git through the proxy — `fetch`/`push` `unreachable` or `tls failed` while `api` is `ok` | git does not read `HTTPS_PROXY` the same way: run the settings pass above once, then `reach` again. |
| Probe: no report — `send` prints `timeout` | Is `listen` running on the target, and what does its stderr say? Run `reach` on the target. A `rejected <reason>` line instead means the target refused the job: see the Core README "Bus: rejected envelopes". |
| Probe: `push_failed pull: …` from `send` | The sender's own checkout is dirty or behind; `send` does not stash (only `listen` does): `git -C <bus.localPath> status`, then `git stash` or `git checkout -- .`. |
| `node.json` — exit 3 `zyggy: configuration error: <key>: <detail>` | The key named is missing or invalid; `secrets never in node.json` means a key looked like a credential. The schema is above. |
