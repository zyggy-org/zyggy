# Spec: 59 — Quick wins: jobs and reports

## Status

- 2026-11-21: spec written by an unattended job from `main` at v0.25.0 (`0286488`), after Central coordinated about fifteen jobs on 2026-11-21. Not approved. Placed in the roadmap after 18 group C (in progress) and before every Not started deliverable, 58 included (owner decision of 2026-11-21).
- 2026-11-21: answers to Q1–Q9 written in (owner: Q1, Q4, Q8; Central, following the spec's recommendation: Q2, Q3, Q5, Q6, Q7, Q9) and moved to Decisions; Constraints, Behavior, Acceptance criteria, Slices, Out of scope and the zyggy-core rule wording brought in line. New open question Q10 (path patterns for a fetch on a work machine). **Not approved** — the owner still reads it.
- 2026-11-21: owner's further answer on Q4, "2c => no restrictions": a work machine is treated like the owner's other machines for `fetch` and `command` jobs (same confinement, same allow-list, same go rule, so Q1's standing go applies there too); the work-machine-only limits (per-job go, business-side filing) removed. Q10 answered no: `jobs.fetchAllowedPaths` dropped. New open question Q11 (standing go for read-only commands). **Not approved.**
- 2026-11-21: **approved by the owner** in his conversation with Central, with the decisions recorded here and both proposed `zyggy-core` rule wordings (standing go for read-only jobs; fetch and command jobs on a work machine). Q11 stays open: until the owner answers it, every `command` job needs his go. Build order: slices 1–7 and 9 now; slice 8 held if 18 group C slice 14 starts first; slices 10 and 11 after 18 group C.
- 2026-11-21: slice 1 built (fetch job; long report as an attached file), two unattended jobs. Core: `kind`/`fetch_path`/`fetch_ref`, the fetch run (`git show origin/<ref>:<path>`, size cap, no model), `reports/<id>.att` with signed `file_name`/`file_sha256`, every node advertises `fetch`, the sender refuses a machine that does not; the sweep archives a report's `.att` with it. Cli: `zyggy submit --fetch <path> [--ref <branch>]` (exit 2 on a refusal), `zyggy report <id> --file [<out>]` (refuses a file whose SHA-256 differs from the signed one). Proof: Core fetch unit tests (31) and the registry/discovery/sweep unit tests (309) pass, `--filter "FullyQualifiedName~FetchJob"` 4/4, build 0 warnings, format clean. Decision: a fetch job takes the project lock like any job. Open: no test yet for the sweep moving the `.att`. Awaiting the owner's approval.
- 2026-11-21: slice 2 built (allow-listed command job without a model), unattended jobs. Core: `kind: command` + `command` field, reason `command_failed`, `node.json` `commands` (name → argv or `{argv, timeoutMinutes 1–30}`; program a bare name or absolute path) read into `NodePolicy`, the runner runs only the listed argv in the project folder without a shell (unknown name `schema_unsupported` before any process), every node advertises `command`, the sender refuses a machine that does not. Cli: `zyggy submit --command <name>` (exit 2 on a refusal; excludes `--fetch`). Proof: Core filtered unit run 193/193 (submitter, reader, intake, runner command, reason wire), `--filter "FullyQualifiedName~CommandJob"` 4/4, build 0 warnings. AC-15 relies on the same runner path (no work-machine branch exists); no separate work-machine unit test. Awaiting the owner's approval.
- 2026-11-21: slice 3 built (base checked on the origin, git's message in the report), unattended job. Core: `mandate grant` refuses a `--branch`, and `submit --mandate` a `--base`, that `git ls-remote --exit-code origin refs/heads/<branch>` answers absent (exit 2) when the sender's own scan of its `devRoots` holds the project (Q5); no local checkout, or any other git failure, sends as before. The node's body is now `git_error: base: origin/<base> not found (<first error line>)`, the failed fetch's line first, else `rev-parse --verify`'s. Proof: `--filter "FullyQualifiedName~Mandate|FullyQualifiedName~Worktree"` unit 356/356 (RED 5 seen first), integration 69/69, whole unit project 7433 passed / 60 skipped (Linux-only) / 0 failed, build 0 warnings, format clean. Decision: a branch check git cannot complete (no network) lets the job go; the node's message is the safety net. Awaiting the owner's approval.
- 2026-11-21: slice 4 built (failures before the model starts do not pause a mandate), unattended job. Core: the mandate ledger's report entry carries `model_started` (`model` or `num_turns` present, Q5); the pause counts the three newest such reports since the last resume, and the paused summary names only those. Slice 3 repair: the node's `git fetch` now prunes, so a base deleted on the origin after an earlier fetch no longer resolves from a stale remote-tracking ref; integration test with a fetched-then-deleted base. Proof: unit project 7440 passed / 60 skipped (Linux-only) / 0 failed (RED 5 seen first), integration `--filter "Mandate|Supervis|Worktree|OwnerView"` 79/79 (RED 1 seen first), build 0 warnings, format clean. Decision: a report with only `num_turns` counts as a model run. Awaiting the owner's approval.
- 2026-11-21: slice 5 built (a failed or timed-out run keeps its work on `zyggy/<id>` only), unattended job. Core: the worktree finish pushes the mandate's `base` only when the run ended `done`; a `failed` or `timeout` run pushes `zyggy/<id>` and the body says `branch <base> not updated: run <status>` instead of a fast-forward line. `tools/fake-claude` gained the `max-turns` scenario (`error_max_turns`, 200 turns). Also: the slice 4 review-check filter corrected (`MandateCheckTests|SupervisorWakeTests`; `MandateLedger` matched no test). Proof: unit project 7442 passed / 60 skipped (Linux-only) / 0 failed (RED 2 seen first), integration `--filter "Worktree|Mandate|Supervis|NodeHost"` 121/121 (RED 2 seen first: base moved, body said fast-forwarded), build 0 warnings, format clean. Awaiting the owner's approval.
- 2026-11-21: slice 6 built (a run continues from an earlier job's branch, with its own turn limit), unattended job. Core: job fields `max_turns` (≥ 1, model job only) and `continue` (a job id, model job with `worktree: true` only), written only when set; the worktree run starts at `origin/zyggy/<continue>` as fetched (after the `base` check when both are given; spec 18's base rules apply at Finish), a branch the origin lacks is `rejected` / `branch_refused` with body `branch_refused: continue: zyggy/<id> not found` before any writing git command; the runner's `--max-turns` is `min(max_turns, claude.maxTurns)` with the body line `max_turns lowered to <n>` when lowered; `MandateJobRequest` carries `Continue` and `MaxTurns` (Q6: same step, counted by the step cap as one more job). Cli: `zyggy submit --max-turns <n>` and `--continue <job-id>` (also with `--mandate`; `--continue` needs `--worktree`, exit 2 otherwise; refused with `--session`, `--fetch` and `--command`). Proof: RED seen first (compile errors on the new fields, then 7 parser tests); unit project 7528 passed / 60 skipped (Linux-only) / 0 failed, `--filter "FullyQualifiedName~ContinueJob"` 6/6, integration `--filter "Worktree|Mandate|Supervis|NodeHost|FetchJob|CommandJob|ContinueJob|SubmitCommand"` 167/167, build 0 warnings, format clean. Decision: `continue` together with `base` keeps the base check at Prepare (a missing base still fails `git_error: base`), then starts from the continued branch. Awaiting the owner's approval.
- 2026-11-21: slice 6 tightening (Central's decision, see Edge cases) and slice 7 built (true liveness and the zyggy version per machine), unattended job. Tightening: `MandateLimit.Continue` (wire `continue`) in the one mandate check both ends run — `continue` under a mandate must name a counted job of the same mandate and step, never the job itself; `MandateSubmit` carries `Continue`, so Central refuses at submit and a node at intake. Slice 7: the registry file carries `version` (the publishing `zyggy`'s informational version, `discovery` options filled from the assembly; content, so an upgrade publishes at once; an older file parses without it); the publisher re-commits an unchanged file once `last_seen` is older than half of `lastSeenHours` (3 h at the default 6 h); `zyggy status` prints one `registry <machine> last_seen <stamp> (<age>) · version <v|unknown> · health <level|unknown>[ · stale]` line per readable registry file (`BusInventory.RegistryAsync`, `MachineLiveness`), `stale` only past the threshold, through the one rule `RegistryDocument.IsStaleAt` that `zyggy registry` uses too. Golden `tests/golden/registry/home-laptop.yaml` gained `version: 0.26.0`. Proof: unit `--filter "Registry|MandateCheckTests|MandateSubmitTests|Discover"` 338/338 (the 18→19 wire count and the new `MandateSubmit.Continue` member were RED as compile/assert failures first; part A's rule also seen failing by breaking it on purpose), unit project 7547 passed / 60 skipped (Linux-only) / 0 failed, integration `--filter "Mandate|Supervis|Worktree|ContinueJob|NodeHost|Registry|Discover|Status"` 251 total: 248 passed, 2 skipped, 1 failed on first run (`RegistryPublishRoundTripTests`, its bus-block comparison now excludes the new registry lines; 2/2 on rerun), likewise the P1 gate's line indices moved by the new registry line; build 0 warnings, format clean. Awaiting the owner's approval.
- 2026-11-21: slice 8 built (the job guard accepts quoted Windows paths and harmless redirects), unattended job. Core `ShellCommand`: in Bash a backslash inside double quotes is a path character (`"C:\src\a.txt"`, a `\\` pair is one backslash; `\"` and a backslash before a line break stay escapes, refused); the harmless redirects are simple words of their own (after a blank, followed by the end, a blank or a separator): Bash `2>&1`, `2>/dev/null`, `>/dev/null`; PowerShell `2>&1`, `2>$null` — each only in its own shell, since `>/dev/null` in PowerShell on Windows would write a file. The quoted part is still matched whole against the job's rules, the read root still judges a quoted path, the network check still runs, and a redirect reads no path. Everything else stays refused (any other redirect or target, a backslash outside quotes, `2> /dev/null` with a blank, chaining after the path). `JobPrompt`: one line after the tool list says a call outside it is refused by a hook and counted. Slice 7 repairs: the intake continue table gained the row "self" (a job naming itself; seen failing with the guard removed); the P1 health-down read-out waits for the registry file on main and checks the registry and `recent reports` lines by position again. Proof: RED seen first (quoted path `'\'`, redirect `'>'`, `/dev/null` taken as a path outside the read root); unit project 7616 passed / 60 skipped (Linux-only) / 0 failed, integration `--filter "Guard|Hook|Mandate|NodeHost|Worktree|P1_WalkingSkeleton"` 218 passed / 2 skipped / 0 failed, build 0 warnings, format clean. Open: without a read root a quoted path is not confined (reads were never confined there; a registry path like `HKLM\…` is no file path), and `>$null` in PowerShell is not accepted because the spec does not name it. Awaiting the owner's approval.
- 2026-11-21: slice 9 built (`zyggy report <id> --wait [--timeout <minutes>]`), unattended job. Cli `ReportCommand`: with `--wait` the verb pulls the bus and traces the job every 20 s on the host's clock until a verified report answers it — any status (`done`, `failed`, `timeout`, `rejected`), since each ends the job — then prints it as today (exit 0); past the timeout it prints `no report yet for <id> in tenant <t> after <n> min` (exit 3, `ExitCodes.NoReportYet`); `--timeout` is 1–1440 minutes, default 30 (decision: the spec gave none), refused without `--wait` (exit 2); `--file` composes after the wait. The pull and each look take the checkout's write lock only for their own duration; the wait between them holds none, and a busy checkout at one look is tried again at the next (test: another process takes the write lock mid-wait and the wait recovers). Slice 8 repair: in the read-root check only an unquoted redirect word reads nothing; a quoted one (`cat "2>/dev/null"`) is an ordinary argument judged as a path (`ShellCommand.Words` now says whether a word was quoted); a UNC path (`\\server\share\file`) is rooted on every system, never under the run directory; `>/dev/null/../x` stays refused as not simple. Proof: RED seen first (3 quoted-redirect cases allowed; 7 of 10 wait tests); unit project 7623 passed / 60 skipped (Linux-only) / 0 failed, integration `--filter "Report|Guard|Hook|NodeHost|Sweep"` 250 passed / 2 skipped / 0 failed (`ReportWait` 10/10), build 0 warnings, format clean. Open: a job that lands in `rejected/` without any report (an unverifiable envelope) does not end the wait, since only a report does. Awaiting the owner's approval.

## Goal

**As** the owner and Central, **I want** the job round trip to stop costing a model run, a go or a guess where none is needed — read a file, run a fixed check, see the real git error, keep a failed run's work apart, hear about every report, and see each machine's true state — **so that** Central can coordinate a day of jobs without the friction met on 2026-11-21.

## Context

- **Roadmap entry**: `_plans/ROADMAP.md` #59.
- **Founding-spec sections**: §4 Bus protocol (envelope fields, report body, registry file), §5 Poller, §6 Job runner (pre-flight, invocation, report mapping, reason enum), §8 Work boundary (the hook on the work laptop), §9 design rules, §11 Notifications.
- **Reference feature**: a job's branch gets a name and a description (`_specs/53-job-branch-name.md`): new job fields on the envelope, CLI options on `zyggy submit`, worktree prepare/finish rules and a report body line, Fake → Wire. Spec 56 (`_specs/56-names-only-routing.md`) is the model for a small change with a Node-host integration test.
- **Friction of 2026-11-21 and what the code at `0286488` already does** (each item is an owner wish; files read):

| # | Wish | Found in the code |
| --- | --- | --- |
| 1 | Central reads a file of a project repository; a report longer than about 2 KB | **Partly exists.** The report summary is cut at 2,000 characters on purpose (§6 report summary, a `…` at the end); a job body may be 64 KB. No model-free job exists: every job starts `claude -p`. |
| 2 | One fixed, allow-listed command without a model | **Does not exist.** |
| 3 | Mandate jobs: `git_error: base` without detail; pre-model failures count toward the pause | **Confirmed.** Prepare answers `base` when `origin/<base>` does not resolve after the fetch, with no git message. Neither `zyggy mandate grant` nor `zyggy submit --mandate` checks the base on the origin. The pause counts the three newest reports that are not `done`, whatever their reason, also a rejection before the model started. |
| 4 | A run that died at the turn limit had its partial work fast-forwarded onto the feature branch; no way to continue; one turn limit | **Confirmed — a defect.** Finish pushes `zyggy/<id>` and then fast-forwards the mandate's `base` branch for every run that started, `failed` and `timeout` included. The turn limit is the node's single `claude.maxTurns`; no job sets its own. |
| 5 | Central is told when any job reports | **Partly exists.** The supervisor wake reacts to mandate events only. `zyggy report` has no wait option. |
| 6 | Guard noise; backslashes and `2>&1` refused | **Partly exists.** The job prompt already lists the job's allowed tools (`{{allowed_tools}}`). The hook refuses every redirection, and a backslash in a Bash command (an escape in bash); PowerShell commands accept a backslash already. |
| 7 | Registry says "stale" while the machine is online; version not visible | **Confirmed — a defect.** A node re-commits an unchanged registry file only once `last_seen` is older than `discovery.lastSeenHours` (6 h), and `zyggy registry` calls a machine stale once `last_seen` is older than the same 6 h — so every healthy machine shows "stale" for up to one discovery interval every six hours. The node's version is in its local `/health` only, never on the bus. |
| 8 | A standing go for read-only jobs on the owner's own machines | A `zyggy-core` rule, not code: decided under Q1, wording in *Rule changes in zyggy-core*. |
| 9 | Start from another job's branch; Stop hook "no change" lines; drafts kept nowhere | `zyggy/` is refused as `--branch` by the branch rules (spec 53). The "no change" lines are written by a Stop hook outside this repository (Q7: fixed where that hook lives). Drafts: nothing in this repository keeps them (Q8: a drafts folder in Central's memory, after 18 group C). |

- **Branches open on 2026-11-21** (`git diff --stat origin/main...<branch>`): `feature/18-supervision-group-c` (the owner's own build) touches `Supervision/INotifier`, `OwnerNotifier`, `OwnerView`, `Brief/BriefDocument`, `Brief/BriefRenderer`, `Core/README.md`, the 18 spec and their tests; group C's slices 13–14 (owner mail and stop, session tasks) are not built yet and will touch more of Supervision, the node loop and probably the job runner. `feature/57-work-mandates-work-memory` touches only `_plans/ROADMAP.md` so far. `feature/58-work-mail-outlook` touches the roadmap and its own spec.

## Constraints

- ⚠️ **Shared contract (§4 envelope, §6 runner I/O).** New job fields (`kind`, `fetch_path`, `fetch_ref`, `command`, `max_turns`, `continue`), the report fields `file_name` and `file_sha256` (Q2) and the registry key `version` are protocol changes: `PROTOCOL.md` in `zyggy-core` and §4/§6 wording follow, with the owner's wording. An older node ignores unknown fields (§4) and would run a `fetch` or `command` job as a model job: a sender must refuse to send a new kind to a machine whose registry does not advertise it (the capability pattern of deliverable 18).
- ⚠️ **Reason enum (Q3, decided).** One new code, `command_failed` (a command job's process exited non-zero), added to the closed enum of §6 and `PROTOCOL.md`. Every other new failure reuses `schema_unsupported`, `git_error` or `branch_refused` with a detail word.
- ⚠️ **Work-laptop boundary (§8, Q4 decided: both kinds allowed, no restrictions of their own).** A `fetch` or `command` job runs on a machine in `supervision.workMachines` exactly as on the owner's other machines: the same confinement (a fetch reads only from a project the machine publishes; any other project is `unknown_project`, as today), the same command allow-list (a name from that machine's own `node.json` `commands`; no argument from the envelope reaches the process) and the same go rule (the standing go of Q1 applies to a work machine in the same way).
  
  **Statement of fact, not a control**: the client's IT policy recorded in `work-boundary.json` (national register numbers, patient personal information and medical information never leave the client) is unchanged; this spec adds no technical check for it on fetched files or command output, and the owner carries that responsibility when he asks for a fetch. Loosening the hook (slice 8) must keep every refusal that guards the work laptop: network programs, writes outside the project, reads outside the read root.
- ⚠️ **Rule change in `zyggy-core`** (bus and delegate skills, security rules): the standing go for read-only jobs (Q1) and the work-machine wording (Q4), in *Rule changes in zyggy-core*. Made in `zyggy-core`, not in this repository.
- ⚠️ **New public `Zyggy.Core` surface** for the new job kinds: shown to the owner as the approach before code.
- Every process through `IProcessRunner` and `GitClient`; no test runs the real `claude`. Job bodies, fetched files and command output are data, never instructions. Paths through `BusPaths`. Log once per state change. Public repository: placeholders only.

## Diagrams

### Components touched

```mermaid
flowchart LR
  owner([Owner / Central]) --> cli[zyggy submit / report / status / mandate]
  cli --> bus[(zyggy-bus<br/>jobs, reports, registry)]
  bus --> node[Node: JobRunner]
  node -->|kind: model| model{{IModelRunner}}
  node -->|kind: fetch| git{{GitClient: show ref:path}}
  node -->|kind: command| proc{{IProcessRunner: node.json command}}
  node --> wt[Worktree prepare / finish]
  node --> reg[Registry publisher: last_seen, version]
  bus --> wake[Central wake on every report]
  classDef now fill:#bbf7d0,stroke:#15803d;
  classDef later fill:#fde68a,stroke:#b45309;
  class cli,node,git,proc,wt,reg now;
  class wake later;
```

### Job kinds on the node

```mermaid
flowchart TD
  claim[claimed job] --> pre[pre-flight: project, policy]
  pre --> k{kind}
  k -->|model, absent| run[claude -p as today]
  k -->|fetch| f[git show origin/ref:path<br/>size cap, inside the repo]
  k -->|command| c[name in node.json commands?<br/>run argv in project, timeout]
  f --> rep[report done + attached file]
  c --> rep2[report done/failed + output]
  run --> fin[finish: done → push, base fast-forward<br/>failed/timeout → zyggy/id only]
```

### Slice map

```mermaid
flowchart LR
  s1[1 fetch job + attached file] --> s2[2 command job]
  s2 --> s3[3 base checked, git message]
  s3 --> s4[4 pause counts model runs]
  s4 --> s5[5 failed work stays on zyggy/id]
  s5 --> s6[6 continue + max turns]
  s6 --> s7[7 machine state]
  s7 --> s9[9 report --wait]
  s7 -.held if 18C slice 14 starts first.-> s8[8 guard noise]
  s9 -.after 18C.-> s10[10 wake on every report]
  s10 -.after 18C.-> s11[11 drafts kept in Central memory]
```

## Behavior

### Envelope and bus

| Field (job) | Values | Meaning |
| --- | --- | --- |
| `kind` | `model` (default when absent), `fetch`, `command` | What the node runs. |
| `fetch_path` | repository-relative path, `/` separators | `fetch` only; no `..`, no absolute path. |
| `fetch_ref` | branch name | `fetch` only; read as `origin/<ref>` after a fetch; default the project's default branch. |
| `command` | a name, `[a-z0-9-]` | `command` only; looked up in the node's `node.json` `commands`. |
| `max_turns` | 1 – the node's ceiling | `model` only; the run's turn limit, never above the node's `claude.maxTurns`. |
| `continue` | a job id | `model` with `worktree: true` only; the run starts from `origin/zyggy/<id>` of that earlier job. Allowed with `mandate` (Q6). |

| Field (report) | Values | Meaning |
| --- | --- | --- |
| `file_name` | the attached file's name (the fetched file's base name, `output.txt`, `report.md`) | present only when a file is attached |
| `file_sha256` | lower-case hex | the attached file's SHA-256; in the signed front matter, so the signature covers the file |

- **Attached file (Q2 decided)**: a report may carry one attached file, `reports/<id>.att` beside the report (path through `BusPaths`), named by `file_name` and covered by the report's signature through `file_sha256`. The report body keeps the 2,000-character summary; the attachment holds the full text (a fetched file, a command's output, or a model run's full REPORT section). Size cap 1 MB (`jobs.fetchMaxBytes`, also the cap for output and REPORT text). The sweep archives the attachment together with its report.
- **Registry**: each machine's file carries `version` (the node's informational version). An unchanged file is re-committed when `last_seen` is older than half of `lastSeenHours`, so a healthy machine never crosses the stale threshold.

### CLI

- `zyggy submit --fetch <path> [--ref <branch>] --project <name> --to <machine>` — a `fetch` job.
- `zyggy submit --command <name> --project <name> --to <machine>` — a `command` job.
- `zyggy submit --max-turns <n>` and `--continue <job-id>` for a model job.
- `zyggy report <id> --file [<out>]` writes the attached file; `zyggy report <id> --wait [--timeout <minutes>]` blocks until the report exists (exit 0) or the timeout passes (exit 3, message `no report yet`).
- `zyggy status` lists per machine: `last_seen`, the node's `version`, `health`, and `stale` only past the threshold.
- `zyggy mandate grant --base <branch>` and `zyggy submit --mandate` refuse a `base` that does not exist on the origin of the named project when the sender holds a checkout of that project (checked with `git ls-remote`, Q5); a sender without the project sends the job, and the node's report carries git's message (slice 3).

### Configuration

| Key | Where | Default | Rule |
| --- | --- | --- | --- |
| `commands` | `node.json` | `{}` | name → argv array (program plus fixed arguments), optional `timeoutMinutes` (1–30). The program must be a bare name on PATH or an absolute path the owner wrote; no shell. |
| `claude.maxTurns` | `node.json` (exists) | as today | The ceiling a job's `max_turns` may lower, never raise. |
| `jobs.fetchMaxBytes` | `node.json` | 1,048,576 | Larger files are refused; also the cap of an attached file (Q2). |

### Failure modes

| Situation | Outcome |
| --- | --- |
| `fetch` or `command` for a project the machine does not publish (work machines included) | `rejected`, reason `unknown_project`, as today |
| `fetch_path` absent at that ref | `failed`, reason `git_error`, body `git_error: fetch: <git's first error line>` |
| `fetch_path` with `..`, absolute, or above the cap | `rejected`, reason `schema_unsupported` (detail `fetch_path` / `too large`) |
| `command` not in `node.json` | `rejected`, reason `schema_unsupported`, detail `unknown command <name>` |
| command exits non-zero | `failed`, reason `command_failed` (new, Q3); body has the exit code and the output's tail, full output attached |
| command program does not start (not found, no permission) | `failed`, reason `command_failed`; body `command_failed: command <name> did not start: <error>`, no attachment — decided by Central on 2026-11-21: the spec was silent |
| A new kind sent to a node that does not advertise it | the sender refuses before writing the job; nothing reaches the bus |
| Base branch missing on the origin at run time | `failed`, reason `git_error`, body `git_error: base: origin/<base> not found (<git's first error line>)` |
| A failed or timed-out run with `base` | `zyggy/<id>` pushed and named; `base` not touched; body `branch <base> not updated: run <status>` |
| `continue` names a job whose branch is not on the origin | `rejected`, `branch_refused`, detail `continue: zyggy/<id> not found` |

### Edge cases

| Case | Expected behavior |
| --- | --- |
| `max_turns` above the node's ceiling | the ceiling applies; body line `max_turns lowered to <n>` |
| `continue` together with `branch` | `continue` is the start point; the `branch` rules of spec 53 apply after it |
| `continue` together with `mandate` (Q6) | allowed: a mandate step may continue a failed step's branch; it keeps the same step name, counts as a new job of the mandate, and the `base` rules of spec 18 apply after it |
| `continue` under a mandate naming a job of another step, of another mandate, of no mandate, a rejected job or an unknown id — decided by Central on 2026-11-21: the spec was silent | refused: under a mandate a job continues only from a job that ran under the same mandate and the same step (a counted job of that step, open or reported). Central refuses at submit with one line, limit `continue` (`continue: <id> is not a job of this mandate's step '<step>'`), nothing written; a node refuses it at intake as `mandate_invalid` (limit `continue`) too, since it does not trust the sender. Outside a mandate, `continue` is as slice 6 built it |
| A report without `model` and `num_turns` (Q5) | the model did not start: not counted toward the mandate pause |
| `fetch` or `command` to a machine in `supervision.workMachines` | runs exactly as on any other machine (published projects, allow-listed command); the go rule is Central's (zyggy-core), the same for every machine, not a node check |
| A Bash command `cat "C:\src\a.txt" 2>&1` | allowed when the quoted path is inside the root and the redirect is one of `2>&1`, `2>/dev/null`, `2>$null`, `>/dev/null` |
| A redirect into a file (`> out.txt`) | still refused (write path not checked) |

## Decisions

| Founding-spec item (section) | Verdict | Why |
| --- | --- | --- |
| §6 "every job runs `claude -p`" | reshape | `fetch` and `command` need no model; they keep pre-flight, lock and the report mapping. |
| §6 report summary 2,000 characters | keep | The summary stays short for Central's prompts; the full text goes in the attached file. |
| §6 one turn limit per node | reshape | A job may lower it; the node's value stays the ceiling. |
| §5 / spec 17 liveness | reshape | Refresh at half the stale threshold, so "stale" means really silent. |
| Spec 18 pause after three not-done reports | reshape | Count only reports of runs where the model started (recognised per Q5). |
| Spec 18 base fast-forward | fix | Only a `done` run moves `base`; the current behavior contradicts the mandate's intent. |
| Spec 53 `zyggy/` refused as `--branch` | keep | `--continue <id>` gives the same result without letting a job write onto another job's branch. |
| §8 work boundary: content from a work machine to the bus | reshape | Q4 below: `fetch` and `command` allowed on work machines with no restriction of their own; the client's IT policy is unchanged and not checked by code. |
| Q1 — standing go for read-only jobs (wish 8) | **(a) wording accepted** — owner, 2026-11-21; extended to work machines by the owner's Q4 answer of 2026-11-21 | Central may send read-only jobs (read tools only, no branch) to any of the owner's machines, work machines included, without asking first. A `zyggy-core` rule change (bus and delegate skills, security rules); wording in *Rule changes in zyggy-core*. |
| Q2 — attached file contract | **(a) the recommendation** — Central, 2026-11-21 | One file `reports/<id>.att`; `file_name` and `file_sha256` in the signed report front matter; 1 MB cap; archived together with the report. |
| Q3 — reason codes for the new kinds | **(a) add `command_failed` only** — Central, 2026-11-21 | A non-zero exit is not a `claude_error`; every other case reuses `schema_unsupported`, `git_error`, `branch_refused` with a detail word. A protocol change: §6 enum and `PROTOCOL.md` follow. |
| Q4 — fetch and command on a work machine | **(c) both allowed, no restrictions** — owner, 2026-11-21 ("2c => no restrictions") | A work machine is treated exactly like the owner's other machines for these two kinds: same confinement, same command allow-list, same go rule (Q1 applies). No per-job go and no business-side filing of their own. The client's IT policy in `work-boundary.json` is unchanged; no technical check of fetched files or command output is added, and the owner carries that responsibility when he asks for a fetch. |
| Q10 — path allow-list `jobs.fetchAllowedPaths` on a work machine | **no** — owner, 2026-11-21 | Follows from Q4 "no restrictions": no extra config key, no path check beyond the general fetch rules (no `..`, no absolute path, size cap). |
| Q5 — how "the model did not start" is recognised | **(a) a report without `model`/`num_turns`** — Central, 2026-11-21 | No new report field. The base check runs at the sender with `git ls-remote` only when the sender holds the project; otherwise the node's report message (slice 3) is the safety net. |
| Q6 — `continue` with a mandate | **(a) yes** — Central, 2026-11-21 | Same step name, counted as a new job of the mandate. |
| Q7 — Stop hook "no change" lines (wish 9b) | **(a) fix where the hook lives** — Central, 2026-11-21 | The session-memory Stop hook is outside this repository; skip writing when the summary says nothing changed. Not a slice here. |
| Q8 — where drafts for the owner are kept (wish 9c) | **(a) a drafts folder in Central's memory** — owner, 2026-11-21 | `drafts/<date>-<slug>.md` in Central's memory repository, listed on the waiting list of 18 group C; built after 18 group C. |
| Q9 — overlap with 18 group C slices 13–14 | **(c) build 1–7 and 9 now; hold 8** — Central, 2026-11-21 | Slice 8 (the hook) waits if 18 group C slice 14 starts first, and then follows its merge; otherwise it runs in order. Slices 10–11 after 18 group C. |

## Acceptance criteria

| # | Criterion | Check |
| --- | --- | --- |
| AC-1 | Given a `fetch` job for an existing path at a branch, the node reports `done` without starting the model, and `reports/<id>.att` equals the file at `origin/<ref>` byte for byte, with `file_name` and `file_sha256` in the signed report | Integration test (bare repo, fake-claude never invoked): fetch round trip |
| AC-2 | A `fetch_path` with `..`, an absolute path, a missing file or a file above the cap gives the reason and detail of the Failure modes table | Unit tests per case |
| AC-3 | A model run's REPORT longer than 2,000 characters is kept whole in the attached file; the body still holds the cut summary | Unit test on the report mapping; `zyggy report --file` integration test |
| AC-4 | A `command` job runs exactly the argv of its `node.json` entry in the project folder, no shell, no envelope text in the argv, and reports exit code and output; a non-zero exit is `failed` with reason `command_failed` | Integration test with a test command (exit 0 and non-zero); unit test that an unknown name is refused before any process starts |
| AC-5 | A sender refuses `kind: fetch` or `command` to a machine whose registry does not advertise it | Unit test on the submitter |
| AC-6 | `mandate grant` and `submit --mandate` refuse a base that does not exist on the origin when the sender holds the project; a sender without it sends, and a node that meets the missing base reports git's message | Unit tests (with and without a local checkout); integration test with a deleted base |
| AC-7 | Reports without `model` and `num_turns` (the model did not start) do not count toward the pause; three model failures still pause | Unit tests on the mandate ledger |
| AC-8 | A `failed` or `timeout` run with `base` pushes `zyggy/<id>` and leaves `base` where it was | Integration test with fake-claude ending at the turn limit (it already scripts outcomes) |
| AC-9 | `--continue <id>` starts the worktree at `origin/zyggy/<id>`; with `--mandate` the step keeps its step name and counts as a new job of the mandate; `--max-turns` reaches the model request, capped by the node | Unit tests on the request and the mandate ledger; integration test on the start SHA |
| AC-10 | A healthy node never shows `stale`; the registry file carries `version`; `zyggy status` shows `last_seen` and `version` per machine | Unit test on the refresh rule with `FakeTimeProvider`; CLI integration test |
| AC-11 | The hook allows a quoted Windows path inside the root and the listed harmless redirects in Bash and PowerShell, and still refuses a redirect to a file, a network program and a path outside the root | Unit tests on the hook (table of commands); the existing hook tests stay green |
| AC-12 | `zyggy report <id> --wait` returns as soon as the report lands, and exits 3 after the timeout | CLI integration test |
| AC-13 | Central is woken for every report, not only a mandate's (after 18 group C) | Integration test through the Central wake |
| AC-14 | A draft written for the owner is kept as `drafts/<date>-<slug>.md` in Central's memory repository and appears on the owner's waiting list (after 18 group C) | Integration test through the waiting list of 18 group C |
| AC-15 | On a machine in `supervision.workMachines` a `fetch` or `command` job runs exactly as on any other machine: a project the machine does not publish is `unknown_project`, a command not in its `node.json` is `schema_unsupported`, and no envelope text reaches a command's argv | Unit tests with the target listed as a work machine |

## Slices

Files are the expected touch set at `0286488`, given because the owner asked for the overlap check; the builder confirms them from the code. "Now" = no file in common with `feature/18-supervision-group-c` or `feature/57-work-mandates-work-memory` today.

| # | Slice | Criteria | Expected files | Start | Review check |
| --- | --- | --- | --- | --- | --- |
| 1 | ⚠️ Central reads a file of a project at a branch, and a long report travels whole as an attached file (wish 1) | AC-1, AC-2, AC-3, AC-5, AC-15 | Core `Envelope/` (JobFields, parser, Envelope), `Bus/` (BusPaths, JobSubmitter, report writer/reader), `Jobs/` (JobRunner dispatch, a fetch run, ReportSummary), `Git/GitClient`, `Registry/RegistryDocument` (capability); Cli `SubmitCommand`, `ReportCommand`; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~FetchJob"` |
| 2 | ⚠️ A node runs one allow-listed command without a model (wish 2) | AC-4, AC-5, AC-15 | Core `Envelope/`, `Jobs/` (a command run), `Runs/` (reason `command_failed`), `Policy/` (node.json `commands`), `Registry/RegistryDocument`; Cli `SubmitCommand`; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~CommandJob"` |
| 3 | A mandate's base is checked on the origin, and the report carries git's message (wish 3a) | AC-6 | Core `Bus/MandateVerbs`, `Bus/JobSubmitter`, `Jobs/WorktreeRun`, `Git/GitClient`; Cli `MandateCommand`; tests | now | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Mandate"` |
| 4 | Failures before the model starts do not pause a mandate (wish 3b) | AC-7 | Core `Bus/MandateRules`; tests | now (57 may later touch mandates) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MandateCheckTests\|FullyQualifiedName~SupervisorWakeTests"` |
| 5 | A failed or timed-out run keeps its work on `zyggy/<id>` only (wish 4c, defect) | AC-8 | Core `Jobs/WorktreeRun`; `tools/fake-claude` (turn-limit outcome) if missing; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Worktree"` |
| 6 | A run continues from an earlier job's branch, with its own turn limit (wishes 4a, 4b, 9a) | AC-9 | Core `Envelope/`, `Jobs/JobRunner`, `Jobs/WorktreeRun`, `Bus/JobSubmitter`; Cli `SubmitCommand`; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContinueJob"` |
| 7 | ⚠️ Each machine shows its true liveness and version (wish 7, defect) | AC-10 | Core `Registry/` (RegistryDocument, RegistryPublisher, DiscoveryOptions); Cli `StatusCommand`, `RegistryCommand`; tests | now | `zyggy status` against a local bare repo; `--filter "FullyQualifiedName~Registry"` |
| 8 | ⚠️ The job guard accepts quoted Windows paths and harmless redirects (wish 6) | AC-11 | Core `Jobs/Hook/ShellCommand`, `Jobs/Hook/JobHookPolicy`, `Jobs/JobPrompt` (one line: tools outside the list are refused); tests | now — held until 18C slice 14 is merged if that slice starts first (Q9) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Hook"` |
| 9 | The owner or Central waits for a report with one command (wish 5a) | AC-12 | Cli `ReportCommand`; Core `Bus/` report lookup; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ReportWait"` |
| 10 | Central is woken for every report (wish 5b) | AC-13 | Core `Supervision/SupervisorWake`, `SupervisorStep`, the inventory; tests | after 18C | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SupervisorWake"` |
| 11 | Drafts written for the owner are kept in Central's memory (wish 9c, Q8) | AC-14 | Central memory `drafts/` folder; `Supervision/OwnerView` (waiting list of 18C); tests | after 18C | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Drafts"` |

## Rule changes in zyggy-core

Made in `zyggy-core` (bus and delegate skills, security rules), not in this repository; `PROTOCOL.md` follows the protocol changes of Constraints.

- **Standing go for read-only jobs (Q1, wording accepted by the owner 2026-11-21; "and not in `supervision.workMachines`" dropped after the owner's Q4 answer, proposal the owner approves at review)**, in the bus and delegate skills and the security rules: *"Central may submit a job without asking the owner when all of these hold: the target is one of the owner's own machines, work machines included; `allowed_tools` holds read tools only (`Read`, `Grep`, `Glob`, or a `fetch` job); no `worktree`, `branch`, `base` or `mandate`; the body asks for no message, mail or post. Central logs each such job in the decision log with basis `standing-read-only`."*
- **Fetch and command jobs on a work machine (Q4, proposal following the owner's answer "2c => no restrictions"; the owner approves it at review)**, in the security rules (work boundary) and the bus skill: *"A `fetch` or `command` job may target a machine in `supervision.workMachines` under the same rules as any of the owner's machines: a fetch reads only from a project that machine publishes, a command is only a name from that machine's own `node.json` allow-list, and the same go rule applies. The client's IT policy in `work-boundary.json` is unchanged; Zyggy does not check fetched files or command output against it, and the owner carries that responsibility when he asks for a fetch."*

## Out of scope

- The `zyggy-core` edits themselves (above): made in `zyggy-core`.
- The Stop hook's "no change" lines (wish 9b): fixed where that hook lives, outside this repository (Q7).
- Binary or multi-file fetch, shell commands with arguments from the envelope, a check of fetched content or command output against the client's IT policy (Q4: the owner carries that responsibility), a path allow-list for fetches on a work machine (Q10: no).
- Central reading project repositories by cloning (deliverable 32/34 territory).

## Open questions

- [x] **Q1 — Standing go for read-only jobs (wish 8).** Answered 2026-11-21 by the owner: (a) the proposed wording is accepted; extended to work machines by the Q4 answer. → Decisions; wording in *Rule changes in zyggy-core*.
- [x] **Q2 — Attached file contract.** Answered 2026-11-21 by Central (the recommendation): (a) `reports/<id>.att`, `file_name` + `file_sha256` signed, 1 MB, archived together. → Decisions.
- [x] **Q3 — Reason codes for the new kinds.** Answered 2026-11-21 by Central (the recommendation): (a) add `command_failed` only. → Decisions.
- [x] **Q4 — Fetch and command on a work machine.** Answered 2026-11-21 by the owner: (c) both allowed; further answer the same day, "2c => no restrictions": a work machine is treated like the owner's other machines. → Decisions; wording in *Rule changes in zyggy-core*.
- [x] **Q5 — How "the model did not start" is recognised.** Answered 2026-11-21 by Central (the recommendation): (a) a report without `model`/`num_turns`; the sender checks the base with `git ls-remote` only when it holds the project. → Decisions.
- [x] **Q6 — `continue` with a mandate.** Answered 2026-11-21 by Central (the recommendation): (a) yes, same step name, a new job of the mandate. → Decisions.
- [x] **Q7 — Stop hook "no change" lines.** Answered 2026-11-21 by Central (the recommendation): (a) fixed where the hook lives; not a slice here. → Decisions.
- [x] **Q8 — Where drafts for the owner are kept.** Answered 2026-11-21 by the owner: (a) a drafts folder in Central's memory, after 18 group C. → Decisions.
- [x] **Q9 — Overlap with 18 group C slices 13–14.** Answered 2026-11-21 by Central (the recommendation): (c) build 1–7 and 9 now; hold 8 if 18 group C slice 14 starts first. → Decisions.
- [x] **Q10 — Path allow-list `jobs.fetchAllowedPaths` on a work machine.** Answered 2026-11-21 by the owner: no. → Decisions.
- [ ] **Q11 — Standing go for read-only commands.** Should the standing go of Q1 also cover `command` jobs whose `node.json` allow-list entry is marked read-only (for example a `readOnly: true` flag the owner sets on the entry)? Today Q1's wording covers read tools and `fetch` jobs only, so every `command` job still needs the owner's go. Recommendation: yes — the owner marks an entry read-only on the machine itself, and Central may then send it without asking, logged with basis `standing-read-only`. A yes adds the flag to the `commands` config row, an AC and a line in the Q1 wording (slice 2).
