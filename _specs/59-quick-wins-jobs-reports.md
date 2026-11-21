# Spec: 59 — Quick wins: jobs and reports

## Status

- 2026-11-21: spec written by an unattended job from `main` at v0.25.0 (`0286488`), after Central coordinated about fifteen jobs on 2026-11-21. Not approved. Placed in the roadmap after 18 group C (in progress) and before every Not started deliverable, 58 included (owner decision of 2026-11-21).

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
| 8 | A standing go for read-only jobs on the owner's own machines | A `zyggy-core` rule, not code: Open question Q1. |
| 9 | Start from another job's branch; Stop hook "no change" lines; drafts kept nowhere | `zyggy/` is refused as `--branch` by the branch rules (spec 53). The "no change" lines are written by a Stop hook outside this repository (Q7). Drafts: nothing in this repository keeps them (Q8). |

- **Branches open on 2026-11-21** (`git diff --stat origin/main...<branch>`): `feature/18-supervision-group-c` (the owner's own build) touches `Supervision/INotifier`, `OwnerNotifier`, `OwnerView`, `Brief/BriefDocument`, `Brief/BriefRenderer`, `Core/README.md`, the 18 spec and their tests; group C's slices 13–14 (owner mail and stop, session tasks) are not built yet and will touch more of Supervision, the node loop and probably the job runner. `feature/57-work-mandates-work-memory` touches only `_plans/ROADMAP.md` so far. `feature/58-work-mail-outlook` touches the roadmap and its own spec.

## Constraints

- ⚠️ **Shared contract (§4 envelope, §6 runner I/O).** New job fields (`kind`, `fetch_path`, `fetch_ref`, `command`, `max_turns`, `continue`) and the registry key `version` are protocol changes: `PROTOCOL.md` in `zyggy-core` and §4/§6 wording follow, with the owner's wording. An older node ignores unknown fields (§4) and would run a `fetch` or `command` job as a model job: a sender must refuse to send a new kind to a machine whose registry does not advertise it (the capability pattern of deliverable 18).
- ⚠️ **Reason enum.** No new reason code without the owner's answer to Q3; the default is to reuse `schema_unsupported`, `git_error` and `branch_refused` with a detail word.
- ⚠️ **Work-laptop boundary (§8).** A fetch job returns file content to the bus: on a machine in `supervision.workMachines` it is refused unless the owner decides otherwise (Q4). A command job runs only what the node's own `node.json` lists; no argument from the envelope reaches the process. Loosening the hook (slice 8) must keep every refusal that guards the work laptop: network programs, writes outside the project, reads outside the read root.
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
  s7 --> s8[8 guard noise]
  s8 --> s9[9 report --wait]
  s9 -.after 18C.-> s10[10 wake on every report]
  s10 -.after 18C.-> s11[11 drafts kept]
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
| `continue` | a job id | `model` with `worktree: true` only; the run starts from `origin/zyggy/<id>` of that earlier job. |

- **Attached file**: a report may carry one attached file beside it, under the report's own bus path (`reports/<id>.<ext>` or a sibling folder; exact path per Q2), covered by the report's signature through a `file_sha256` field. The report body keeps the 2,000-character summary; the attachment holds the full text (a fetched file, a command's output, or a model run's full REPORT section). Size cap 1 MB by default (Q2).
- **Registry**: each machine's file carries `version` (the node's informational version). An unchanged file is re-committed when `last_seen` is older than half of `lastSeenHours`, so a healthy machine never crosses the stale threshold.

### CLI

- `zyggy submit --fetch <path> [--ref <branch>] --project <name> --to <machine>` — a `fetch` job.
- `zyggy submit --command <name> --project <name> --to <machine>` — a `command` job.
- `zyggy submit --max-turns <n>` and `--continue <job-id>` for a model job.
- `zyggy report <id> --file [<out>]` writes the attached file; `zyggy report <id> --wait [--timeout <minutes>]` blocks until the report exists (exit 0) or the timeout passes (exit 3, message `no report yet`).
- `zyggy status` lists per machine: `last_seen`, the node's `version`, `health`, and `stale` only past the threshold.
- `zyggy mandate grant --base <branch>` and `zyggy submit --mandate` refuse a `base` that does not exist on the origin of the named project, when the sender can check it (Q5); otherwise the node's report says so (slice 3).

### Configuration

| Key | Where | Default | Rule |
| --- | --- | --- | --- |
| `commands` | `node.json` | `{}` | name → argv array (program plus fixed arguments), optional `timeoutMinutes` (1–30). The program must be a bare name on PATH or an absolute path the owner wrote; no shell. |
| `claude.maxTurns` | `node.json` (exists) | as today | The ceiling a job's `max_turns` may lower, never raise. |
| `jobs.fetchMaxBytes` | `node.json` | 1,048,576 | Larger files are refused. |

### Failure modes

| Situation | Outcome |
| --- | --- |
| `fetch_path` absent at that ref | `failed`, reason `git_error`, body `git_error: fetch: <git's first error line>` |
| `fetch_path` with `..`, absolute, or above the cap | `rejected`, reason `schema_unsupported` (detail `fetch_path` / `too large`), per Q3 |
| `command` not in `node.json` | `rejected`, reason `schema_unsupported`, detail `unknown command <name>`, per Q3 |
| command exits non-zero | `failed`, reason per Q3 (proposed: new code `command_failed`; `claude_error` would mislead); body has the exit code and the output's tail, full output attached |
| A new kind sent to a node that does not advertise it | the sender refuses before writing the job; nothing reaches the bus |
| Base branch missing on the origin at run time | `failed`, reason `git_error`, body `git_error: base: origin/<base> not found (<git's first error line>)` |
| A failed or timed-out run with `base` | `zyggy/<id>` pushed and named; `base` not touched; body `branch <base> not updated: run <status>` |
| `continue` names a job whose branch is not on the origin | `rejected`, `branch_refused`, detail `continue: zyggy/<id> not found` |

### Edge cases

| Case | Expected behavior |
| --- | --- |
| `max_turns` above the node's ceiling | the ceiling applies; body line `max_turns lowered to <n>` |
| `continue` together with `branch` or `mandate` | `continue` is the start point; `branch`/`base` rules of spec 53/18 apply after it (Q6) |
| A Bash command `cat "C:\src\a.txt" 2>&1` | allowed when the quoted path is inside the root and the redirect is one of `2>&1`, `2>/dev/null`, `2>$null`, `>/dev/null` |
| A redirect into a file (`> out.txt`) | still refused (write path not checked) |

## Decisions

| Founding-spec item (section) | Verdict | Why |
| --- | --- | --- |
| §6 "every job runs `claude -p`" | reshape | `fetch` and `command` need no model; they keep pre-flight, lock and the report mapping. |
| §6 report summary 2,000 characters | keep | The summary stays short for Central's prompts; the full text goes in the attached file. |
| §6 one turn limit per node | reshape | A job may lower it; the node's value stays the ceiling. |
| §5 / spec 17 liveness | reshape | Refresh at half the stale threshold, so "stale" means really silent. |
| Spec 18 pause after three not-done reports | reshape | Count only reports of runs where the model started (Q5 states how that is recognised). |
| Spec 18 base fast-forward | fix | Only a `done` run moves `base`; the current behavior contradicts the mandate's intent. |
| Spec 53 `zyggy/` refused as `--branch` | keep | `--continue <id>` gives the same result without letting a job write onto another job's branch. |

## Acceptance criteria

| # | Criterion | Check |
| --- | --- | --- |
| AC-1 | Given a `fetch` job for an existing path at a branch, the node reports `done` without starting the model, and the attached file equals the file at `origin/<ref>` byte for byte, its sha in the signed report | Integration test (bare repo, fake-claude never invoked): fetch round trip |
| AC-2 | A `fetch_path` with `..`, an absolute path, a missing file or a file above the cap gives the reason and detail of the Failure modes table | Unit tests per case |
| AC-3 | A model run's REPORT longer than 2,000 characters is kept whole in the attached file; the body still holds the cut summary | Unit test on the report mapping; `zyggy report --file` integration test |
| AC-4 | A `command` job runs exactly the argv of its `node.json` entry in the project folder, no shell, no envelope text in the argv, and reports exit code and output | Integration test with a test command; unit test that an unknown name is refused before any process starts |
| AC-5 | A sender refuses `kind: fetch` or `command` to a machine whose registry does not advertise it | Unit test on the submitter |
| AC-6 | `mandate grant` and `submit --mandate` refuse a base that does not exist on the origin (where checkable); a node that still meets it reports git's message | Unit tests; integration test with a deleted base |
| AC-7 | Reports of runs where the model did not start do not count toward the pause; three model failures still pause | Unit tests on the mandate ledger |
| AC-8 | A `failed` or `timeout` run with `base` pushes `zyggy/<id>` and leaves `base` where it was | Integration test with fake-claude ending at the turn limit (it already scripts outcomes) |
| AC-9 | `--continue <id>` starts the worktree at `origin/zyggy/<id>`; `--max-turns` reaches the model request, capped by the node | Unit test on the request; integration test on the start SHA |
| AC-10 | A healthy node never shows `stale`; the registry file carries `version`; `zyggy status` shows `last_seen` and `version` per machine | Unit test on the refresh rule with `FakeTimeProvider`; CLI integration test |
| AC-11 | The hook allows a quoted Windows path inside the root and the listed harmless redirects in Bash and PowerShell, and still refuses a redirect to a file, a network program and a path outside the root | Unit tests on the hook (table of commands); the existing hook tests stay green |
| AC-12 | `zyggy report <id> --wait` returns as soon as the report lands, and exits 3 after the timeout | CLI integration test |
| AC-13 | Central is woken for every report, not only a mandate's (after 18 group C) | Integration test through the Central wake |
| AC-14 | A draft written for the owner is kept where Q8 decides | Per Q8 |

## Slices

Files are the expected touch set at `0286488`, given because the owner asked for the overlap check; the builder confirms them from the code. "Now" = no file in common with `feature/18-supervision-group-c` or `feature/57-work-mandates-work-memory` today.

| # | Slice | Criteria | Expected files | Start | Review check |
| --- | --- | --- | --- | --- | --- |
| 1 | ⚠️ Central reads a file of a project at a branch, and a long report travels whole as an attached file (wish 1) | AC-1, AC-2, AC-3, AC-5 | Core `Envelope/` (JobFields, parser, Envelope), `Bus/` (BusPaths, JobSubmitter, report writer/reader), `Jobs/` (JobRunner dispatch, a fetch run, ReportSummary), `Git/GitClient`, `Registry/RegistryDocument` (capability); Cli `SubmitCommand`, `ReportCommand`; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~FetchJob"` |
| 2 | ⚠️ A node runs one allow-listed command without a model (wish 2) | AC-4, AC-5 | Core `Envelope/`, `Jobs/` (a command run), `Policy/` (node.json `commands`), `Registry/RegistryDocument`; Cli `SubmitCommand`; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~CommandJob"` |
| 3 | A mandate's base is checked on the origin, and the report carries git's message (wish 3a) | AC-6 | Core `Bus/MandateVerbs`, `Bus/JobSubmitter`, `Jobs/WorktreeRun`, `Git/GitClient`; Cli `MandateCommand`; tests | now | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Mandate"` |
| 4 | Failures before the model starts do not pause a mandate (wish 3b) | AC-7 | Core `Bus/MandateRules`; tests | now (57 may later touch mandates) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MandateLedger"` |
| 5 | A failed or timed-out run keeps its work on `zyggy/<id>` only (wish 4c, defect) | AC-8 | Core `Jobs/WorktreeRun`; `tools/fake-claude` (turn-limit outcome) if missing; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Worktree"` |
| 6 | A run continues from an earlier job's branch, with its own turn limit (wishes 4a, 4b, 9a) | AC-9 | Core `Envelope/`, `Jobs/JobRunner`, `Jobs/WorktreeRun`, `Bus/JobSubmitter`; Cli `SubmitCommand`; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContinueJob"` |
| 7 | ⚠️ Each machine shows its true liveness and version (wish 7, defect) | AC-10 | Core `Registry/` (RegistryDocument, RegistryPublisher, DiscoveryOptions); Cli `StatusCommand`, `RegistryCommand`; tests | now | `zyggy status` against a local bare repo; `--filter "FullyQualifiedName~Registry"` |
| 8 | ⚠️ The job guard accepts quoted Windows paths and harmless redirects (wish 6) | AC-11 | Core `Jobs/Hook/ShellCommand`, `Jobs/Hook/JobHookPolicy`, `Jobs/JobPrompt` (one line: tools outside the list are refused); tests | now (18C slice 14 may touch the hook) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Hook"` |
| 9 | The owner or Central waits for a report with one command (wish 5a) | AC-12 | Cli `ReportCommand`; Core `Bus/` report lookup; tests | now | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ReportWait"` |
| 10 | Central is woken for every report (wish 5b) | AC-13 | Core `Supervision/SupervisorWake`, `SupervisorStep`, the inventory; tests | after 18C | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SupervisorWake"` |
| 11 | Drafts written for the owner are kept (wish 9c) | AC-14 | per Q8; likely `Supervision/OwnerView` or the brief | after 18C | per Q8 |

## Out of scope

- The standing permission for read-only jobs (wish 8): a `zyggy-core` rule, Q1.
- The Stop hook's "no change" lines (wish 9b): not in this repository, Q7.
- Binary or multi-file fetch, a fetch from a work machine (unless Q4 says otherwise), shell commands with arguments from the envelope.
- Central reading project repositories by cloning (deliverable 32/34 territory).

## Open questions

- [ ] **Q1 — Standing go for read-only jobs (wish 8).** Found: every job needs the owner's go; the rule lives in `zyggy-core`, not in this repository. Proposed wording for the owner to accept, change or refuse: *"Central may submit a job without asking the owner when all of these hold: the target is one of the owner's own machines and not in `supervision.workMachines`; `allowed_tools` holds read tools only (`Read`, `Grep`, `Glob`, or a `fetch` job); no `worktree`, `branch`, `base` or `mandate`; the body asks for no message, mail or post. Central logs each such job in the decision log with basis `standing-read-only`."* Recommendation: none — owner's decision.
- [ ] **Q2 — Attached file contract.** Path (`reports/<id>.file` beside the report, or `reports/<id>/…`), extension, size cap (1 MB proposed), and whether the sweep archives it with the report. Recommendation: one file `reports/<id>.att`, `file_sha256` and `file_name` in the signed report front matter, 1 MB, archived together.
- [ ] **Q3 — Reason codes for the new kinds.** Reuse `schema_unsupported` (bad field, unknown command) and `git_error` (fetch), and add one code `command_failed` for a non-zero exit? Adding a code is a protocol change. Recommendation: add `command_failed` only; reuse the rest with detail words.
- [ ] **Q4 — Fetch and command on a work machine.** §8: content from the work laptop to the bus. Recommendation: refused on `supervision.workMachines` in this spec; a later spec may allow named commands there.
- [ ] **Q5 — How "the model did not start" is recognised for the pause.** Options: (a) a report without `model`/`num_turns` (already set only after a run), (b) a new report field `model_started: false`. Recommendation: (a), no new field. Also: may a laptop sender check `origin/<base>` of a project it does not hold? Recommendation: the check runs where the project is known (the node's registry has `default_branch` only), so grant/submit check via `git ls-remote` only when the sender has the project; otherwise slice 3's report message is the safety net.
- [ ] **Q6 — `continue` with a mandate.** May a mandate step continue a failed step's branch, and does that count as a new step? Recommendation: yes, same step name, counted as a new job of the mandate.
- [ ] **Q7 — Stop hook "no change" lines (wish 9b).** The lines land in the daily notes written by the session-memory Stop hook, which is not part of this repository (`zyggy-core` or the memory plugin's configuration). Recommendation: fix it where that hook lives (skip writing when the summary says nothing changed); not a slice here.
- [ ] **Q8 — Where drafts for the owner are kept (wish 9c).** Options: (a) Central's memory repository under `drafts/<date>-<slug>.md`, listed on the waiting list of 18 group C; (b) the archive of deliverable 37. Recommendation: (a), after 18 group C lands the waiting list.
- [ ] **Q9 — Overlap with 18 group C slices 13–14.** They are not built; slice 14 (session tasks) will likely touch the job runner, prompt and hook that slices 1, 2, 6 and 8 touch. Options: build those now and let 18C rebase, or hold slice 8 until 18C slice 14 is merged. Recommendation: build 1–7 and 9 now; hold 8 if the owner starts slice 14 first.
