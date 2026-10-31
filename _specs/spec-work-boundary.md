# Spec — Work boundary and Zyggy on the work laptop

Owner: Geoffrey · Date: 2026-10-31 · Target: `zyggy-core` (template) + `zyggy-geoffrey` (instance) · Base: zyggy 0.12.0

## Goal

The owner works with agents on every machine, the RIZIV/INAMI work laptop included. From the work laptop he must
be able to (a) send facts and session notes to Central's memory (`remember`, `Stop`/`SessionEnd` hooks) and (b) read
the relevant part of his memory (Hub) — inside an explicit, enforced work boundary.

## Why a change is needed

Today the rules forbid it, and the Hub cannot do it:

- `security.md` §Never store: "never archive … anything from the employer's work laptop".
- `security.md` §Browser and §GitHub: logged-in sites and the GitHub skills are restricted "until the owner's
  work-boundary rules exist".
- `security.md` §LinkedIn: a draft never holds "anything from his employer or work laptop".
- `operations.md` §What comes later: "jobs on the owner's laptops" do not exist yet (stale since deliverable 15).
- Memory `agents.md`: "`work-laptop` … nothing may be asked of it today" (and the hostname there is wrong).
- `zyggy hub` (deliverable 11) is stdio-only and reads a local memory clone: a laptop has none, by design.

## Part A — Rules (template + instance, no binary change)

### A1. New section "Work boundary" in `security.md`

Define the boundary once; every other rule refers to it.

- **Machines.** The instance names its work machines in `instance/work-boundary.json`:
  `{"work_machines": ["work-laptop"], "side": "business", "projects": ["riziv-*"], "outbound": ["business/"]}`.
  A machine not listed there is not a work machine.
- **Inbound (work laptop → memory).** Allowed: facts the owner states (`zyggy context`) and the one-line session
  notes of the `Stop` hook. They are filed on the `business` side only, under a project matching `projects`.
  Never: secrets (existing secret patterns), mail bodies, document contents, source code, client infrastructure or
  security details beyond subject level (same rule as the morning brief), personal data of client staff beyond
  name, role and organisation.
- **Archive.** Files from a work machine stay excluded from `archive/` (no change to that clause). *Open decision
  D1 — confirm.*
- **Outbound (memory → work laptop).** Only files under `outbound` paths (`business/`, filtered further to the
  `projects` list where the file is project-scoped). Never `private/`, `profile.md` personal lines, `daily/`
  of other machines, `inbox/`, `archive/`. *Open decision D2 — is all of `business/` acceptable (Digiverse banking,
  Specified contract, negotiation) or only `business/areas/riziv-*` and `business/topics/engineering-practices.md`?*
- **No action from the work laptop.** No m365 action, no LinkedIn post, no GitHub clone triggered from there.
- **Owner responsibility.** The owner has confirmed the client's IT policy allows this agent and its traffic to
  GitHub (bus) and to Central. Record the date in the instance (`work-boundary.json` `policy_checked`).

### A2. Replace the "until the owner's work-boundary rules exist" clauses

In §Browser and §GitHub: replace with "see Work boundary". The restrictions themselves (no logged-in site in an
unattended run, skills only on owner request) stay as they are — only the dangling reference changes.

### A3. `operations.md`

- §What comes later: remove "jobs on the owner's laptops"; state what exists (node, laptop `remember`, hooks) and
  what does not (scheduled jobs run on a laptop on Central's request, if still out of scope).
- §Laptops (node side): add the work-machine behaviour (forced scope, see B1) and the exit code for a refusal by
  the boundary.

### A4. Instance

- `instance.md`: a "Work laptop" section: hostname `LT256076` = `work-laptop`, reachable only through the client
  proxy, GitHub reachable (to verify, see C), `node.json` `machine: work-laptop`.
- `instance/work-boundary.json` as in A1.
- Memory (dream or owner edit): `agents.md` — correct hostname L256056 → LT256076 and replace "nothing may be asked
  of it today" (fact already in `inbox/remember-2026-10-31.md`).

## Part B — Binary (zyggy, new release)

### B1. Node on a work machine: enforced inbound boundary

- `zyggy context` reads `work-boundary.json` (shipped to laptops through the bus or `node.json`, not trusted from
  the laptop alone — Central re-checks, see B2).
- On a work machine, scope is forced: `project:<name>` must match `projects`, else the envelope is addressed to
  `project:riziv` (default from config). `general` and `machine` scopes are mapped to that project.
- The `Stop` hook spool scope uses the same mapping (today it derives `project:<last folder>` from the cwd).
- Refusals keep the existing exit codes (5 secret, 2 usage); add none unless needed.

### B2. Central: enforce on receipt

- The dream (bus reader, 0.12.0) checks every envelope from a work machine against `work-boundary.json`:
  wrong side or project → re-filed under the default project, logged in the dream report; secret pattern → dropped,
  logged. Central is the enforcement point; the laptop check is a convenience.

### B3. Remote Hub for nodes (new deliverable)

- `zyggy hub` gains a remote mode served by Central for nodes: request/answer over the bus (simplest, works through
  the proxy as it only needs GitHub) or over HTTPS on Central (needs an endpoint reachable through the client proxy;
  not today's posture — Central exposes nothing). **Recommendation: over the bus**, latency of the poll accepted.
- The laptop runs a thin `zyggy hub --remote` stdio server: same tools `get_context` and `remember` (the latter =
  `zyggy context`).
- Central answers `get_context` only from `outbound` paths for a work machine; the filter runs on Central, never
  on the laptop. A request outside the boundary gets the existing answer `no memory file or daily line matches`.
- Answers are signed (HMAC, as envelopes), are data on the laptop, and are not cached on disk beyond the session.
- Tests: a work-machine request never returns a `private/` line (fixture with a canary string in `private/`).

### B4. Laptop install

- `install.ps1`: no change expected beyond writing `machine: work-laptop`; add the `hub --remote` server to the
  user-scope Claude Code MCP config, and the laptop `remember` skill, as the README already describes for hooks.
- No administrator rights anywhere (existing constraint).

## Part C — Checks before going live on LT256076

1. From LT256076, through the proxy: `git ls-remote https://github.com/zyggy-org/zyggy-bus.git` works with the
   Git Credential Manager (and push --dry-run).
2. `install.ps1 -Tenant geoffrey -Machine work-laptop -SignWith geoffrey/<n>` → task Running, `zyggy status` green.
3. `remember` a test fact → arrives in Central's inbox under `business` / `project:riziv`.
4. `get_context` for "Calizr" and "Nathalie" from the laptop → nothing; for "Redis" → the RIZIV Redis file.
5. A fact with a secret-shaped string → refused locally (5) and, if forced through, dropped by Central.

## Open decisions for the owner

- **D1** Archive from the work laptop: stays forbidden (recommended) or allowed for owner-authored documents?
- **D2** Outbound scope: all of `business/`, or only RIZIV-related files (recommended: RIZIV only; Digiverse
  finances and the Specified negotiation stay off the client's machine)?
- **D3** Remote Hub transport: bus (recommended) or HTTPS endpoint on Central?
- **D4** Client IT policy confirmed (date)?

## Out of scope

Scheduled jobs pushed from Central to a laptop; m365, LinkedIn and GitHub skills on the laptop; any change to the
m365 or LinkedIn security rules.
