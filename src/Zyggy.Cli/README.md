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

## Rules

- Built on System.CommandLine, one file per verb.
- All behaviour comes from `Zyggy.Core`: the CLI parses arguments, calls the library, prints results, and returns a non-zero exit code with a one-line error on failure.
- Never handles credentials itself; signing keys come from `ISecretStore`, git authentication is git's concern.

Founding spec: §3 (component table), §11 (status and costs), §14 (init, diagnose).
