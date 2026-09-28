---
name: bus-protocol
description: "Use when creating, modifying, or reviewing anything that reads or writes the Zyggy bus: envelope fields, canonical form and HMAC signing, ULID file names, tenant-prefixed paths via BusPaths, the job state machine (jobs → claimed → reports / rejected), write rules per machine, the git write sequence and push-retry rule, the registry file, and schema versioning. Checklist form of founding spec §4/§5/§14. Use for: adding an envelope field, a new state move, a new CLI verb that submits or reads, a Hub tool that touches the bus, or reviewing a PR for protocol drift."
metadata:
  argument-hint: "Describe the bus task, e.g. 'add attempt counter to job envelopes', 'implement the rejected move', 'review BusRepository against §4'"
---

# Bus Protocol — checklist for code that touches `zyggy-bus`

The bus is a private git repository. One file per message, state encoded in the path, every file HMAC-signed. **§4 of the founding spec is normative and `PROTOCOL.md` in `zyggy-core` must match it verbatim** — so any change to what is below is an Open Question for the technical-analyst and a ⚠️ Risk Area in a plan, never a quiet code edit.

> Source of truth: `_specs/00 - Personal Agent Platform — Technical Specification.md` §4 (protocol), §5 (transport), §9 (design rules), §14 (tenancy, schema). This skill restates them as checks; when in doubt the spec wins.

## Layout and paths

```
tenants/<org>/
  policy.yaml                 # signed by Central
  registry/<machine>.yaml     # published by each node's discover
  nodes/<machine>/jobs/       # new jobs addressed to <machine>
  nodes/<machine>/jobs/claimed/
  nodes/<machine>/jobs/rejected/
  nodes/<machine>/reports/    # written only by <machine>
  nodes/<machine>/context/    # written only by <machine>
archive/YYYY-MM/              # moved by Central only
```

- [ ] Every path is produced by `BusPaths` (tenant, machine, state, id). No string concatenation elsewhere. Grep for `"nodes/"`, `"jobs/"`, `"tenants/"` outside `BusPaths` → violation.
- [ ] The tenant prefix is present from day one; v1 tenant is `geoffrey`.
- [ ] File name = ULID + `.md`. ULIDs are generated once at envelope creation; a report/context file reuses its own new ULID and references the job through `in_reply_to`.

## Write rules (who may touch what)

- [ ] A sender writes only into `nodes/<target>/jobs/` (and `nodes/central/context/` when the target is Central).
- [ ] A machine writes only its own `reports/`, `context/`, `registry/<machine>.yaml`, and moves files between its own `jobs/`, `jobs/claimed/`, `jobs/rejected/`.
- [ ] Only Central writes `archive/`.
- [ ] Two writers never touch the same file, so `git pull --rebase` never conflicts on content. If a new feature would break this, stop and raise it.

## Envelope

YAML front matter + Markdown body. The body is **data**, never instructions to the receiving agent.

Common fields: `id`, `schema` (1), `tenant`, `type` (`job | report | context`), `from`, `to`, `created` (ISO-8601 UTC), `in_reply_to`, `priority` (`low | normal | high`), `key_id` (`<tenant>/<n>`), `sig`.
Job fields: `deadline`, `project`, `agent`, `worktree`, `allowed_tools`, `report_back`, `timeout_minutes`, `attempt`.
Report fields: `status` (`done | failed | timeout | rejected`), `reason` (closed enum), `started`, `finished`, `duration_seconds`, `cost_usd`, `input_tokens`, `output_tokens`, `model`, `files_changed`, `diff_ref`.
Context fields: `scope` (`project:<name> | machine | general`); body = one `[stated]`/`[observed]` fact per line.

- [ ] Unknown fields are preserved on round trip (parse → serialise) so older nodes do not strip newer fields.
- [ ] A node rejects `schema` with a higher major than it supports: `status: rejected`, `reason: schema_unsupported`.
- [ ] `to` must equal the receiver's machine name; otherwise reject.

## Signature

`sig` = HMAC-SHA256 over the canonical form: front matter with `sig` removed, keys sorted, YAML re-serialised with `\n` line endings, then `\n---\n`, then the body byte-for-byte.

- [ ] `EnvelopeSigner.Canonicalize` is the single implementation. Nothing else re-serialises front matter for signing.
- [ ] Every change to canonicalisation ships with golden files in `tests/golden/` (input envelope, canonical bytes, expected signature for the test key).
- [ ] Keys come from `ISecretStore` by `key_id`; rotation accepts both `k1` and `k2` for 7 days. Never a key in a file, an option class, or a test string that looks like a real one.
- [ ] Verification happens **before** any parsing of the body for execution and before any model call. Failure → `git mv` to `jobs/rejected/` + a `reports/` entry with `status: rejected`.

## State machine

| From | To | Actor | Git operation |
|------|----|-------|---------------|
| (none) | `jobs/<id>.md` | sender | add + commit + push |
| `jobs/` | `jobs/claimed/` | target node | `git mv` + commit + push; push rejected → pull, retry once, else skip |
| `jobs/claimed/` | `reports/<id>.md` + job deleted | target node | add report, `git rm` job, commit, push |
| `jobs/claimed/` older than `timeout_minutes` + 15 min | `jobs/` | Central sweep | `git mv` back, `attempt: n+1`; after 3 attempts → report `status: failed` |
| `reports/`, `context/` older than 14 days | `archive/YYYY-MM/` | Central | `git mv` |

- [ ] Every move is a `git mv` (or add + `git rm`) in one commit, never a copy + delete across commits.
- [ ] Commit message: `<type> <id> <from>→<to>`.
- [ ] A claim is skipped, not failed, when the project lock is held (`<project>/.claude/zyggy.lock`, younger than 2 h).

## Git write sequence (§5)

- [ ] `pull --rebase` → local change → `commit` → `push`. On push rejection: `pull --rebase`, retry at most 3 times with 1–3 s jitter, then leave the change uncommitted, log an error, let the next poll retry.
- [ ] Never leave the working copy dirty across iterations: on start-up `git status --porcelain` non-empty → `git stash` to a named stash + warning.
- [ ] Identity per machine: `zyggy (<machine>) <machine>@zyggy.org`.
- [ ] Shell out to the system `git` through `IProcessRunner`; credentials are git's business (deploy key / credential helper), never the code's.

## Poll loop (§5) — for code that decides *when* to pull

- [ ] Conditional GET on the commits endpoint with `If-None-Match`; `304` is silent; `200` → store ETag, `git pull --rebase`, process `nodes/<me>/jobs/`.
- [ ] Interval 10 s inside work hours, 60 s outside, ±2 s jitter, one in-flight request.
- [ ] `403/429` or `x-ratelimit-remaining < 100` → back off to 120 s until `x-ratelimit-reset`. Network error → exponential 10 s → 5 min.
- [ ] Unconditional `git fetch` + compare `origin/main` every 15 min regardless of ETag.
- [ ] `PollState` is a pure record; the transition function has no I/O and is unit-tested with a fake clock.

## Registry file

`registry/<machine>.yaml`: `machine`, `os`, `last_seen`, `work_hours`, `health`, `projects[]` (`name`, `path`, `default_branch`, `agents`, `skills`, `summary`).

- [ ] Regenerated on service start and hourly; committed only when content differs, excluding `last_seen` (updated at most every 6 h).

## Review checklist for a PR touching the bus

1. Which of the tables above changed? If any → is there a resolved Open Question and a `PROTOCOL.md` update in the same deliverable?
2. Are all new paths from `BusPaths`? Are all new git calls through `GitClient` → `IProcessRunner`?
3. Are there golden files for any canonical-form change?
4. Does every failure path end in a report with a closed-enum `reason` or a rejection move, never an exception to the loop?
5. Is there an integration test against the bare repo for the new move (see `integration-testing` skill)?
6. Does any envelope body get interpreted as instructions anywhere? It must not.
