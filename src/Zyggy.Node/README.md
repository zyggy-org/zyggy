# Zyggy.Node

The always-running service on every machine, Central included. It is the only process that writes to the bus. Published as the single-file binary `zyggy-node` and installed as a Windows service (laptops) or a systemd unit (Central).

## What it does

One loop, described in the founding spec §5 and §6:

1. **Poll** GitHub for the bus repository's head commit with a conditional request (ETag). A `304` is silent; a `200` means something changed.
2. **Pull** the bus checkout (`git pull --rebase`).
3. **Claim** jobs addressed to this machine: verify the signature and the `to` field, then `git mv` the job into `jobs/claimed/` and push. Bad signature or wrong address → `jobs/rejected/` plus a rejection report.
4. **Run** the job: resolve the project and optional subagent against the registry, take the project lock, optionally create a worktree, and execute `claude -p` with the job's tool allowlist and timeout.
5. **Report**: write `reports/<id>.md` (status, summary, files changed, cost), delete the claimed job, commit, push.
6. **Discover**: on start and hourly, scan the dev roots and publish `registry/<machine>.yaml` if it changed.

It also serves a health endpoint on `http://localhost:4711/health` used by `zyggy status` and by Central's `list_nodes`.

## Roles by machine

| Machine | Extra behaviour |
|---------|-----------------|
| Central | Same loop, machine name `central`; scheduled work (mail triage, sweeps) is submitted to itself as jobs so every action is a ledger entry. |
| Home laptop | Default node. |
| Work laptop | `dlp.enabled = true`: report bodies are filtered, only mail metadata and summaries may leave the machine (§8). |

## Configuration

`node.json` next to the binary (machine name, bus remote, poll intervals, work hours, dev roots, concurrency, claude path). Secrets are never in it; they come from `ISecretStore`. `policy.yaml` on the bus may tighten these settings, never loosen them.

## Structure

Hosted services (`PollLoop`, `DiscoveryTimer`), a `JobDispatcher` that enforces one job per project and at most two per machine, and the health endpoint. All logic lives in `Zyggy.Core`; this project wires it into DI and hosts it.

Tests: `tests/Zyggy.Integration` runs the loop against a local bare git repository and the `tools/fake-claude` script.
