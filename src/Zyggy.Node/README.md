# Zyggy.Node

The always-running service on every machine, Central included: the only process that writes to the bus on a machine's behalf. Published as the single-file binary `zyggy-node`; installed as the systemd unit `zyggy-node.service` on Central and as the per-user logon task `zyggy-node` on a laptop (deliverable 10; `deploy/` in the release, procedure in `zyggy-core/runbooks/install-node.md`). Deliverable 07 built it; this file says what it does today.

## What it does

One loop (founding spec §5 and §6), the body of which is `Zyggy.Core.Bus.BusListener` — this project only hosts it:

1. **Start-up check**: the checkout must be a repository on the bus branch holding `tenants/<tenant>/`, with no rebase in progress and the signing key present; a dirty working copy is parked under a named stash with one warning. Only one listener per checkout.
2. **Resume**: the node's own `jobs/claimed/*.md` left behind by a previous run are verified and run again (no second claim); one that no longer verifies is left for the sweep.
3. **Poll** the bus host for the branch head with a conditional request every 10 s inside work hours and every 60 s outside (`workHours` in `node.json`; absent means always inside). A `304` is silent.
4. **Safety fetch** every `poll.safetyFetchMinutes` regardless: `git fetch` and a comparison of `origin/<branch>` with `HEAD`; a head check that lied is caught with one warning, a pull and a pass.
5. **Pull** when the branch moved, then for every file in the inbox: verify, then ask the **dispatcher** (`Zyggy.Core.Jobs.JobDispatcher`, the §6 rules: work hours unless `priority: high`, a free slot under `maxConcurrentJobs`, the project not in flight here and its lock free). A deferred job stays in `jobs/` and is looked at again on the next pass; an admitted one is **claimed** (`git mv` into `jobs/claimed/`, one commit, push).
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
| `poll.activeSeconds`, `poll.idleSeconds`, `poll.safetyFetchMinutes` | the two intervals and the safety-fetch period | 10, 60, 15 |
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
| 3 | `node.json` refused: missing or invalid key, no `tenant`, no `signWith`, a secret-looking key, an unknown argument | stderr: `zyggy-node: configuration error: <key>: <detail>`; nothing started |
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

`phase` is one of `poll pull claim run report reject` and `jobId` is set inside a run; both are blank on host lines. One line per state change, nothing on a quiet tick; never the job body, the prompt, a key or a credential. `log.level: Debug` adds the host's diagnostics.

## Structure

`NodeHost` (the entry point: arguments, `node.json`, the host), `PollLoop` (the one hosted service: start-up check → resume → the listener's loop; stop drains the runs and releases the listen lock), `HealthServer` (Kestrel on loopback), `ClaudeAuthProbe`, `NodeHealth` (the `/health` body). All logic lives in `Zyggy.Core`, composed through `AddNodeServices(node.json)` + `AddJobDispatcher()`; this project wires Serilog, the service lifetimes and the health endpoint around it.

Tests: `tests/Zyggy.Integration/Node/` runs the real host against a local bare git repository and `tools/fake-claude`; `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` part 1 is the P1 gate scenario.
