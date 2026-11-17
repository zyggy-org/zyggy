# Spec: 53 — A job's branch gets a meaningful name and a description

## Status

- 2026-11-17: spec written in an unattended job run (the owner could not be interviewed). Not approved. Every gap is under Open questions with a recommendation; questions marked **HIGH** must be answered before slice 1 (Q1 at the latest before slice 4).
- 2026-11-17: `spec-critic` agent (separate, fresh context) verdict **not ready**: 4 blocking, 4 improvements. Fixed: push test now compares with `origin/<branch>` so kept commits are pushed later (AC-7b); refusal state move, per-segment `.lock`, case-insensitive default-branch and `zyggy/` checks, ref-name clash, failed fetch, stale worktree, YAML-quoting golden; registry read at submit stated as new work with a skip rule; slice 2 split (now 6 slices); AC-8/AC-9 given the provisional concrete reason; AC-14 and AC-17 scoped; constraint "named branches only"; amendments marked conditional. Still open by nature: the HIGH questions Q1–Q4 (the critic's first blocker) need the owner.

## Goal

**As** the owner (and Central acting for him), **I want** to give a worktree job a branch name and a one-line description, **so that** the branch the node pushes says what it holds, and the slices of one deliverable land one after another on one named branch instead of on ten `zyggy/<job id>` branches nobody can tell apart.

What happens today (verified in the code, see Context): `zyggy submit --worktree` always runs on `zyggy/<job id>`, the node's commit message is `zyggy job <id>`, and the report names only that branch. On 2026-11-17 the project repository collected more than ten such branches in one morning, and PRs #44 and #45 carry the title "Zyggy/01 m7 …".

A job without a branch name keeps today's behavior exactly.

## Context

- **Roadmap entry**: `_plans/ROADMAP.md` #53 (slug `53-job-branch-name`); owner request and priority of 2026-11-17.
- **Founding-spec sections**: §4 Bus protocol (job example, "Mandatory fields per type", unknown fields, signature); §6 Job execution (Pre-flight step 4, Report assembly); §9 design rules (closed `reason` enum, `IProcessRunner`, `EnvelopeSigner.Canonicalize` and goldens); §3 / §9 CLI verb list (`submit`); §5 Git operations (commit identity, no force-push).
- **Reference feature**: worktree execution (`_specs/16-worktree-execution.md`) — closer than the discovery registry: this feature changes exactly the worktree run, its node commit, its push and its report lines that 16 built, and follows its Fake → Wire → owner-asks slice shape. The envelope-field part follows the `worktree` field 16 added to `zyggy submit`.
- **Related work**: central supervision (`_specs/18-central-supervision.md`) specifies a job field `base` ("runs on `zyggy/<id>` started from `base`; also pushed to `base` when that is a fast-forward"). That field is **not on `main`** (verified 2026-11-17: no `base` in the job parser, the envelope writer or the submitter on `main`; the work exists only on an unpushed local branch of 18). Its overlap with this feature is question Q2 (HIGH).
- **Facts verified in the code on `main` (963feb0), 2026-11-17, files read:**
  - The worktree run always uses branch `zyggy/<id>`; the node's commit message is `zyggy job <id>`; the push is `refs/heads/<b>:refs/heads/<b>` to `origin`, never forced; after a successful push, and when the branch has no commits, the local branch is deleted with `branch -D`; after a failed push it is kept and the report becomes `failed` / `git_error`.
  - An existing local `zyggy/<id>` is reused as it stands (no fetch into it, no fast-forward); a new one starts at `origin/<default>`, else the local default branch. The default branch comes from git at run time (`origin/HEAD`, else `main`, else `master`, else the current branch).
  - `git worktree add` of a branch that is already checked out in another worktree (for instance the owner's checkout) fails; today that ends as `failed` / `git_error`, body `git_error: worktree_add`, before the model starts.
  - `files_changed` and the "ahead" test compare the base commit with the final tip; `diff_ref` is the pushed tip SHA.
  - §4: "every field not listed here is unknown: preserved, signed, never interpreted". A node built before this feature therefore **accepts** a job carrying the new fields, verifies its signature, ignores the fields, and runs on `zyggy/<id>`.
  - The report parser accepts any snake_case `reason` token; it does not check membership of the closed enum. A Central on an older version can therefore read a report with a new reason member.
  - The job submitter's read-back refuses any front-matter key outside its list of §4 job keys, so the submitter must learn the new keys in the same slice that writes them.
  - The registry publishes each project's `default_branch`; `zyggy submit` has the bus checkout and can read it before sending.
  - This repository's own guard (`.claude/hooks/guard.mjs`) refuses `git commit`, `git push`, `git checkout`, `git switch` and similar commands from any session, including a job's model; job 01M7MSQWSV2ACHMRTSQZ636QRF hit it on 2026-11-17. Zyggy's own tool hook additionally allows only the job's `allowed_tools`. **So the node, not the job's model, creates, names, commits and pushes the branch.** The model is never asked to do any of it.
  - Golden files for the job envelope exist (`job.md`, `job.canonical`, `job.sig`, and a non-canonical variant).
- **Not verified**: whether a Hub `submit_job` tool exists yet (the Hub host is still empty per `CLAUDE.md`); §7 says it takes "envelope fields", so it would accept the new fields when built. Out of scope here.

## Constraints

- ⚠️ **§4 shared contract and signing.** Two new optional job fields, part of the signed front matter. The reviewer verifies: `EnvelopeSigner.Canonicalize` is unchanged; new golden files cover a job with both fields and with `branch` only; the existing job goldens stay byte-identical; a job without the fields is written byte-for-byte as today. `PROTOCOL.md` in `zyggy-core` changes with §4 (listed under Proposed amendments, not edited here).
- ⚠️ **§6 runner I/O and the closed `reason` enum.** Depending on Q1, one new reason member (`branch_refused`). Adding a member is a protocol change and waits for the owner's answer.
- ⚠️ **Writes into the owner's real repositories.** Widened from 16 only by: the local and remote branch the job names. Never a force-push, never a push to or a checkout of the project's default branch, never a change to the owner's checkout or git config. For **named** branches: never a reset, and never a deletion of a local branch the node did not create in this run (unnamed `zyggy/<id>` branches keep 16's cleanup rule).
- ⚠️ **Public surface of `Zyggy.Core`.** The job request and the job fields gain two optional values; `zyggy submit` gains two options. The worktree handling stays internal; no new seam.
- ⚠️ **The work-laptop boundary (§8).** Unchanged: worktree jobs on the work laptop stay out of scope as in 16. A branch name or description never carries work-laptop data from or to it.
- §9: git only through `GitClient` / `IProcessRunner`, never LibGit2Sharp; no test runs the real `claude` or reaches GitHub; `JobRunner` never throws to the loop; every failure is a report with a closed `reason`; log once per state change.
- **Data, never instructions.** The branch name and the description come from a signed sender, yet they are data: they are never placed in the model's prompt (decision D7), they appear only as a commit-message subject, report body lines and log values, and they are validated for shape before any use.
- **Naming is done by the node.** The prompt template is unchanged; the model is not asked to create, switch, commit or push a branch (see Context: the repository guard refuses it).
- Public repo: placeholders only (`acme`, `home-laptop`, `shop`, `feature/12-cart`).

## Diagrams

### Components touched

```mermaid
flowchart LR
  owner([Owner / Central]) --> cli["zyggy submit --worktree<br/>--branch --description"]
  cli --> reg[("registry/&lt;machine&gt;.yaml<br/>default_branch")]
  cli --> bus[("zyggy-bus<br/>tenants/&lt;org&gt;/nodes/&lt;machine&gt;/jobs<br/>job: branch, description")]
  bus --> node["Zyggy.Node on &lt;machine&gt;<br/>parse + verify (§4)"]
  node --> runner["Job runner (Core)"]
  runner --> wt["Worktree run (Core, internal)<br/>named branch: create / continue"]
  wt --> git{{"GitClient / IProcessRunner"}}
  runner --> model{{"IModelRunner<br/>prompt unchanged"}}
  git --> project[("Project repo<br/>branch &lt;name&gt; or zyggy/&lt;id&gt;")]
  project --> origin[("Project origin")]
  runner --> report["Report: branch line,<br/>description line, diff_ref"]
  report --> bus
  classDef new fill:#fde68a,stroke:#b45309,color:#1f2937;
  class cli,wt,report new;
```

### Round trip: two jobs continue one named branch

```mermaid
sequenceDiagram
  actor Owner
  participant Bus as zyggy-bus
  participant Node as Node (home-laptop)
  participant Repo as Project repo
  participant Origin as Project origin
  Owner->>Bus: submit --worktree --branch feature/12-cart --description "Cart: slice 1" (job A)
  Node->>Bus: poll, verify, claim A
  Node->>Origin: fetch (best effort)
  Node->>Repo: feature/12-cart absent → create from origin/<default>, worktree add
  Node->>Node: model runs (fake-claude in tests)
  Node->>Repo: commit leftovers: "Cart: slice 1" + "zyggy job <A>"
  Node->>Origin: push feature/12-cart (new branch, no force)
  Node->>Bus: report A: branch line, description line, diff_ref
  Owner->>Bus: submit --worktree --branch feature/12-cart --description "Cart: slice 2" (job B)
  Node->>Bus: poll, verify, claim B
  Node->>Origin: fetch
  Node->>Repo: worktree on feature/12-cart at origin's tip (fast-forward only)
  Node->>Node: model runs
  Node->>Repo: commit leftovers: "Cart: slice 2" + "zyggy job <B>"
  Node->>Origin: push feature/12-cart (fast-forward, no force)
  Node->>Bus: report B
```

### Where the run starts (Prepare)

```mermaid
flowchart TD
  start[Job with worktree: true] --> named{branch set?}
  named -- no --> today["Today's rule: zyggy/id<br/>(spec 16, unchanged)"]
  named -- yes --> dflt{"branch = project's<br/>default branch (from git)?"}
  dflt -- yes --> refuse1["refused before the model<br/>reason per Q1"]
  dflt -- no --> fetch[fetch origin, best effort]
  fetch --> where{"local branch? remote branch?"}
  where -- neither --> create["create from origin/&lt;default&gt;<br/>else local default"]
  where -- remote only --> fromremote["create local from origin/&lt;branch&gt;"]
  where -- local only --> uselocal["use local as it stands"]
  where -- both --> rel{"local vs origin/&lt;branch&gt;"}
  rel -- equal or local behind --> ff["fast-forward local to remote"]
  rel -- local ahead --> keep["use local (kept after a failed push)"]
  rel -- diverged --> refuse2["refused before the model<br/>reason per Q1, nothing changed"]
  create --> add{"worktree add"}
  fromremote --> add
  uselocal --> add
  ff --> add
  keep --> add
  add -- branch checked out elsewhere / fails --> refuse3["failed / git_error: worktree_add"]
  add -- ok --> run[model runs in the worktree]
```

### After the model stops (Finish), named branch

```mermaid
flowchart TD
  run[Model run ended: done, failed or timeout] --> left{Uncommitted changes?}
  left -- yes --> commit["Node commits:<br/>subject = description, else 'zyggy job id'"]
  left -- no --> tip
  commit -- fails --> keepwt["failed / git_error, worktree kept"]
  commit --> tip{"Tip not yet on origin?"}
  tip -- no --> clean["Remove worktree; delete local branch<br/>only if the node created it in this run"]
  tip -- yes --> push["Push branch to origin, no force"]
  push -- ok --> ok["Remove worktree; delete local branch only if created in this run<br/>diff_ref = pushed SHA"]
  push -- rejected (another job pushed first) or no origin --> kept["Keep local branch<br/>failed / git_error, body: not pushed"]
```

### Slice map

```mermaid
flowchart LR
  s1["1 Fake: a signed job carries<br/>branch + description (goldens)"] --> s2["2 Fake: the node creates or<br/>continues the named branch and pushes it"]
  s2 --> s3["3 Fake: the description reaches<br/>the commit and the report"]
  s3 --> s4["4 Fake: every refusal and race<br/>ends in a report"]
  s4 --> s5["5 Wire: real git, bare origin,<br/>two jobs on one branch"]
  s5 --> s6["6 Owner asks for it:<br/>zyggy submit --branch, production host"]
```

## Behavior

### Envelope and bus

Two new optional job fields (subject to Q3 for their names and shapes):

| Field | Shape | Meaning |
| --- | --- | --- |
| `branch` | A git branch name of 1–100 characters from `[A-Za-z0-9._/-]`; no empty segment, no segment starting with `.` or `-`, no segment ending in `.lock`, no `..`, not ending in `/` or `.`, not `HEAD`, not starting with `zyggy/` in any letter case (that prefix stays reserved for job-id branches). | The branch the job runs on and pushes. Absent → today's `zyggy/<id>`. |
| `description` | One line of 1–200 characters, no control characters, not only spaces. | What the branch holds after this job. Absent → today's commit message and report. |

- `branch` is valid only with `worktree: true`. A job with `branch` and `worktree: false`, or with a field of the wrong shape, is rejected at parse time as `invalid_field` (existing §4 rejection: the file moves to `jobs/rejected/` with a `rejected` report), before any model call.
- `description` may be given without `branch`: the job then runs on `zyggy/<id>` as today, and only the commit message and the report change (decision D4).
- No new bus path, envelope type or state move. The report gains no field; the branch and description appear as body lines (decision D5).
- The job goldens get three new cases: both fields; `branch` only; a `description` that needs YAML quoting (it contains `: `, ` #`, a leading `-` and both quote characters) and still signs, parses and reads back unchanged. The existing goldens are unchanged.
- **Run-time refusal** (default branch, diverged): the report is written and the job file moves exactly as for the runner's existing refusal before the model (`unknown_project`): no new state move.

### The named-branch run (Prepare)

Pre-flight and the project lock are unchanged and come first. For a job with `branch`:

1. **Default branch.** The project's default branch is determined from git at run time, as in 16. If `branch` equals it, compared without regard to letter case (Windows and macOS refs are case-insensitive on disk), the job is refused before the model starts (reason per Q1), nothing is fetched into or created on that branch.
2. **Fetch** `origin` once, best effort, as in 16. When the fetch fails, the rules below use the refs already present (a stale or absent `origin/<branch>`); a branch that in fact exists on the remote then fails at the push (no force), ending `failed` / `git_error` with the commits kept locally.
3. **Pick the start point** (Q5, recommendation applied):
   - neither a local nor a remote branch of that name → create it from `origin/<default>`, else the local default branch (the same base as 16);
   - only `origin/<branch>` → create the local branch at that commit;
   - only a local branch → use it as it stands;
   - both, and the local branch equals or is an ancestor of `origin/<branch>` → move the local branch forward to `origin/<branch>` (fast-forward only, never a reset);
   - both, and `origin/<branch>` is an ancestor of the local branch (a commit kept after a failed push) → use the local branch;
   - both, diverged → refused before the model starts (reason per Q1); neither branch is touched; the body names both tips.
4. **Create the worktree** on that branch in `<nodeDir>/worktrees/<tenant>/<id>`, as in 16 (stale worktree records are pruned first). If git refuses — the branch is checked out elsewhere (the owner's checkout, a worktree another job left behind), or the name clashes with an existing ref (`a` against `a/b`) — the job ends `failed` / `git_error`, body `git_error: worktree_add` — today's outcome and runbook row, extended with these causes.
5. The start tip (the commit the branch held when the worktree was created) is remembered; `files_changed` compares it with the final tip.

### The run

Unchanged. The model's prompt does not mention the branch or the description (D7); the model works in the worktree as today.

### Finish

Always, whether the run ended `done`, `failed` or `timeout`, as in 16, with these differences for a job with `branch` or `description`:

- **Node commit message**: with `description`, the subject line is the description and the body is `zyggy job <id>`; without it, `zyggy job <id>` as today. Identity and skipped hooks as in 16.
- **Pushed or not**: the branch is pushed when its final tip is not already on `origin` — that is, when `origin/<branch>` is absent and the tip differs from the default-branch base, or when the tip is ahead of `origin/<branch>`. So commits kept locally after an earlier failed push are pushed by the next job on that branch even if that job adds nothing. `files_changed` lists the paths changed between the start tip and the final tip (this job's work, at most 200, as in 16); when the push also carries earlier kept commits, the body says `includes <n> earlier unpushed commits`.
- **Push**: once, to the same name on `origin`, never forced. Git refuses a non-fast-forward itself; a refused push (another job or person pushed first) ends `failed` / `git_error` with the local branch kept, as in 16.
- **Local branch afterwards**: deleted only when this run created it (step 3, first two cases) and the push succeeded or the branch did not move. A local branch that existed before the run is never deleted.
- **Behind the default branch** (Q6, recommendation applied): the node does not merge or rebase the default branch into the named branch. When the pushed branch is behind `origin/<default>`, the report body says so in one line, so the owner sees that a merge or an update is due.

### Report body lines

After the summary, as in 16, the result lines become, for a named branch:

- `branch <name> pushed at <sha>` / `branch <name> kept locally, not pushed: <step>` / `worktree kept at <path>: <step>` — 16's lines with the named branch instead of `zyggy/<id>`;
- `description: <description>` — when the job carries one, right after the branch line (also for an unnamed job with a description, and also when the branch did not move);
- `branch <name> is <n> commits behind origin/<default>` — only when n > 0 and the branch was pushed.

A job without `branch` and `description` produces exactly today's body.

### CLI

`zyggy submit ... --worktree --branch <name> [--description <text>]`

- `--branch <name>`: sets `branch`. Requires `--worktree` (Q7, recommendation: refuse without it rather than imply it). Refused before signing, exit 2 with a one-line message, when the name breaks the shape rule or equals (without regard to letter case) the target project's `default_branch` in the target machine's registry entry as read from the bus checkout. This registry read is new work (the submitter reads no registry today). When the target machine has no registry file, the project is not in it (a path-named project, for instance), or the entry has no `default_branch`, the check is skipped and the node decides at run time. The node checks again in every case; the registry check only saves a round trip.
- `--description <text>`: sets `description`; refused before signing, exit 2, when it breaks the shape rule.
- Without both options: the job is byte-for-byte as today. Other verbs, exit codes and output unchanged.

### Configuration

None.

### Failure modes

| Situation | Observable outcome | Runbook entry |
| --- | --- | --- |
| `branch` or `description` has the wrong shape, or `branch` without `worktree: true` | Parse-time rejection `invalid_field`, file moved to `jobs/rejected/`, `rejected` report to the sender | "Jobs: branch name refused" — fix the name; `zyggy submit` would have refused it |
| `branch` is the project's default branch | Refused before the model, reason per Q1, body names the default branch; nothing fetched into, created or pushed | same |
| Local and remote branch have diverged | Refused before the model, reason per Q1, body names both tips; neither branch touched | "Jobs: named branch diverged" — on that machine: compare `git log <name>...origin/<name>`, keep one side by hand (merge or delete the local branch), resubmit |
| The branch is checked out elsewhere (owner's checkout, a worktree another job left behind), or its name clashes with an existing ref (`a` against `a/b`) | `failed` / `git_error`, body `git_error: worktree_add` | "Jobs: git_error before the run" (16), extended with: switch the owner's checkout away from that branch; `git worktree list` and remove the stale worktree; choose a name that does not clash |
| Fetch failed and the named branch exists only on the remote | Run starts from the default branch; the push is refused (no force) → `failed` / `git_error`, commits kept locally | "Jobs: branch not pushed" (16) |
| Push refused (another job or person pushed first), or no `origin` | `failed` / `git_error`, body `branch <name> kept locally, not pushed: push`; local branch kept with the commits | "Jobs: branch not pushed" (16); on a race: `git pull --ff-only`/merge on that machine, then push or resubmit |
| An older node receives a job with the new fields | Fields ignored (§4 unknown fields); the job runs on `zyggy/<id>`; its report names `zyggy/<id>` | "Jobs: report names zyggy/<id> although --branch was given" — upgrade that node |

### Edge cases

| Case | Expected behavior |
| --- | --- |
| Two named jobs for the same project on one machine | One at a time (project lock), so the second starts from the first's pushed tip |
| Two named jobs for the same branch on two machines | Both may run; the second push is refused (no force) and ends `failed` / `git_error` with its commits kept locally on that machine |
| The model commits its own work (in a project whose guard allows it) and leaves nothing uncommitted | No node commit, so the description is not in any commit; it is in the report (Q4) |
| The model changes nothing on a named branch | `done`, `files_changed: []`, no `diff_ref`, no push; a branch the run created locally is deleted; an existing branch is untouched; the description line is still in the body |
| The named branch exists only on `origin` and was merged into the default branch meanwhile | Continued from its remote tip; the report line "behind origin/<default>" may appear; the owner decides whether to start a new branch |
| A job is re-run after a restart (same id) | Step 3 applies again; a local branch it created in the interrupted run now exists, so it is used (or fast-forwarded) as it stands |
| `branch` names a branch the owner works on by hand | Allowed (it is not the default branch); continued only by fast-forward; if it is checked out in the owner's checkout the job fails before the model |
| `--description` with an unnamed job | Runs on `zyggy/<id>`; commit subject and body line carry the description |

## Decisions

| Item | Verdict | Why |
| --- | --- | --- |
| Branch `zyggy/<id>` for every worktree job (§6 step 4) | **Reshape**: `branch` when given, else `zyggy/<id>` | The owner's request; a job without a name keeps today's behavior |
| D1 — who names, commits and pushes | **The node**, never the model | The repository guard and the tool hook refuse `git commit`/`git push` to the model (seen in job 01M7MSQWSV2ACHMRTSQZ636QRF); the node already commits and pushes in 16 |
| D2 — validation in two places | Shape at parse time (`invalid_field`) and in `zyggy submit`; default branch in `zyggy submit` (registry) and at run time (git, authoritative) | The registry copy can be stale; the node decides |
| D3 — `zyggy/` prefix reserved | Refused as a `branch` value | Keeps job-id branches and named branches apart, so 16's cleanup rule never touches a named branch |
| D4 — description without a branch | Allowed | Improves even unnamed job branches at no cost |
| D5 — where the description lives | Node commit subject + report body line; no new report field, no git branch-description config (local only, never pushed) | No report contract change; readable on the remote and on the bus |
| D6 — delete a local named branch after the run | Only when this run created it | Never remove a branch the owner or an earlier run owns |
| D7 — prompt | Unchanged; branch and description not shown to the model | The model does not need them; keeps sender-supplied text out of the prompt |
| Start from the remote tip (Q5) | Fast-forward only, diverged → refused | Never lose commits, never force |
| Behind the default branch (Q6) | Report line only; no merge, no rebase | A merge can conflict unattended; a rebase needs a force-push |
| Pull request with the description (Q4) | **Defer** | Needs the GitHub API with a project-repo credential, which no node holds; §9 keeps GitHub behind `IBusProvider` |
| Hub `submit_job` | Out of scope | The Hub is not built; §7 already says it takes envelope fields |

No new package.

## Acceptance criteria

Legend: (U) unit test in `tests/Zyggy.Core.Tests`, git and the model substituted; (Gd) golden-file test; (I) integration test in `tests/Zyggy.Integration` with real git, a bare repository as the project's `origin`, a local bare bus and `tools/fake-claude`; (O) owner-run on a real machine at release.

| # | Criterion | Check |
| --- | --- | --- |
| AC-1 | A job with `branch` and `description` is written, signed, parsed back and passes the submitter's read-back with both fields, also when the description needs YAML quoting; a job without them is byte-for-byte as before (existing goldens unchanged) | (Gd) three new goldens; (U) submitter read-back with the quoting case; existing job goldens pass unchanged |
| AC-2 | A job whose `branch` or `description` breaks the shape rule, or that has `branch` without `worktree: true`, is rejected at parse time with `invalid_field` and never reaches the runner | (U) one case per rule, including `zyggy/x`, `Zyggy/x`, `HEAD`, `a..b`, `x.lock`, `a.lock/b`, 101 characters, a control character in `description` |
| AC-3 | A named job whose branch exists nowhere runs on a new branch of that name started at `origin/<default>` (else local default), and pushes it without force | (U) command sequence; (I) branch on the bare origin, its parent is the default tip |
| AC-4 | A named job whose branch exists on `origin` continues from the remote tip: a local branch that is behind is fast-forwarded first; a local branch that is ahead is used as it stands | (U) each case; (I) a second job continues the first job's pushed branch |
| AC-5 | The node's commit carries the description as subject and `zyggy job <id>` in the body; without a description the message is `zyggy job <id>` | (U); (I) message read from the origin |
| AC-6 | The report body carries the branch line with the named branch, the `description:` line when given, and the "behind origin/<default>" line only when the pushed branch is behind | (U); (I) |
| AC-7 | `files_changed` and the push decision compare the start tip with the final tip, so a continued branch lists only this job's changes | (U); (I) the second job lists only its own file |
| AC-7b | Commits kept locally on a named branch after a failed push are pushed by the next job on that branch, even when that job changes nothing, and its body says how many earlier commits it carried | (U); (I) an origin that refuses the first push, then accepts |
| AC-8 | A named job whose `branch` equals the project's default branch (in any letter case) is refused before the model starts with `status: rejected`, `reason: branch_refused` (provisional: Q1 recommendation; with Q1(a) it becomes `failed` / `git_error`); no fetch into, branch creation on, or push to the default branch happens | (U) the model is never called and no write command runs; (I) the origin's default branch is unchanged |
| AC-9 | Diverged local and remote named branches → refused before the model with the same status and reason as AC-8; both tips unchanged | (U); (I) |
| AC-10 | A push refused because the remote moved (race) ends `failed` / `git_error`; the local branch keeps the commits; no command of the run carries `--force`, `-f`, `+<ref>` or `--force-with-lease` | (U) every git command of a named run inspected; (I) a second clone pushes first |
| AC-11 | A local named branch that existed before the run is never deleted; one the run created is deleted after a successful push or when it did not move | (U); (I) |
| AC-12 | A job without `branch` and `description` behaves exactly as today (spec 16 AC-1 … AC-14 stay green unchanged) | (U), (I) existing worktree tests unchanged |
| AC-13 | The model's prompt for a named job is identical to the prompt of the same job without the fields | (U) rendered prompts compared |
| AC-14 | No git or validation failure of the run throws to the loop; each run-time row of the Failure modes table (all rows but the first, a parse-time rejection covered by AC-2) ends in its stated report, with a reason from the closed enum as amended by Q1 | (U) one test per run-time row |
| AC-15 | `zyggy submit --worktree --branch <name> --description <text>` writes a signed job that `zyggy verify` accepts; `--branch` without `--worktree`, a malformed name, or the target project's registry default branch exits 2 and writes nothing; with no registry entry for the target project the job is sent and the node decides | (I) against a local bare bus, one case each |
| AC-16 | Through the production host and the CLI: two `zyggy submit --branch feature/12-cart` jobs in a row produce one branch on the project's origin with both jobs' commits, two `done` reports naming it, and the owner's checkout unchanged | (I) round trip through the production DI and the `zyggy` verb |
| AC-17 | The Core README runbook has the entries named in the Failure modes table; the Cli README documents `--branch` and `--description` | Source hygiene test for the runbook headings and for both option names in the Cli README |
| AC-18 | On this laptop with the real `claude`: a `--branch docs/<scratch>` job against a scratch repository with a GitHub remote ends `done`, the branch is visible on GitHub with the description as commit subject; a second job with the same branch adds one commit on top | (O) at release, evidence in Status |

## Slices

Each slice starts test first: its tests are written and seen failing for the expected reason before production code (for behavior that already holds, the test is seen failing by breaking the code on purpose, then restored). Each ends with the Verification Commands green and one Status line.

| # | Slice | What it proves | Criteria | Review check |
| --- | --- | --- | --- | --- |
| 1 | ⚠️ A signed job can carry a branch name and a description, and every node and the submitter read them the same way (Fake) | Envelope shape, parse-time refusals, signing goldens, the submitter's read-back. Gate: Q2 and Q3 answered before this slice; the owner reads the goldens | AC-1, AC-2 | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Envelope\|FullyQualifiedName~JobSubmitter"` |
| 2 | ⚠️ The node creates or continues the named branch and pushes it without force (Fake) | Prepare start-point rules, the push test against `origin/<branch>`, local-branch cleanup; unnamed jobs unchanged | AC-3, AC-4, AC-7, AC-7b, AC-11, AC-12 (unit) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Worktree"` |
| 3 | The description reaches the node's commit and the report, never the prompt (Fake) | Commit message, report body lines, behind-default line, prompt unchanged | AC-5, AC-6, AC-13 (unit) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Worktree\|FullyQualifiedName~JobPrompt"` |
| 4 | ⚠️ Every refusal and race on a named branch ends in a report, never in a force-push (Fake) | Default branch, diverged, checked out elsewhere or clashing, push refused, failed fetch. Gate: Q1 answered before this slice | AC-8, AC-9, AC-10, AC-14 (unit) | `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Worktree"` |
| 5 | ⚠️ The same against real git: a bare origin, `fake-claude` changing files, two jobs landing on one branch, a kept commit pushed later, a refused default branch, a lost race (Wire) | Slices 2–4 hold with real git | AC-3–AC-11 (integration) | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Worktree"` |
| 6 | ⚠️ The owner asks for a named branch with `zyggy submit --branch --description` and gets it back through the production host; runbook and README | CLI options, the new registry read and its skip rule, the round trip of AC-16, runbook rows | AC-15, AC-16, AC-17 | `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Submit"` |

Slices 1–5 follow this repository's Fake → Wire convention (`write-spec` skill): the owner validates the contract shape (goldens, unit outcomes) before anything is wired, and first uses the feature in slice 6. Review-check filters name test areas as they exist today; the builder may narrow them.

AC-18 is owner-run at release (the `ship` skill runs it on this laptop where it can). No phase gate: this is a follow-up to P2, which is closed.

## Out of scope

- Opening, updating or describing a pull request (Q4, deferred).
- Merging or rebasing the default branch into a named branch; deleting remote branches; cleaning up old `zyggy/<id>` branches.
- Protected-branch lists beyond the default branch (for example `release/*`).
- Showing the branch in `zyggy status`; the Hub `submit_job` tool; a branch default in `node.json`.
- Mandate jobs and their `base` field (spec 18), except as decided in Q2.
- Named branches on the work laptop.

## Open questions

Each with options and a recommendation. **HIGH** = hard to undo, changes the shared bus contract, touches security, or costs money.

- [ ] **Q1 — Which `reason` a refused branch gets (HIGH: §6/§9 closed enum, bus contract).** Found: shape errors are parse-time `invalid_field` rejections already (no change). Run-time refusals (default branch, diverged branches) have no fitting member; the report parser accepts any snake_case token, so an older Central reads a new member. Options: (a) `failed` / `git_error` — no contract change, but no git command failed and the owner cannot tell a refusal from a broken remote; (b) new member `branch_refused`, `status: rejected` (like `unknown_project`, a refusal before the model) — §9 enum and `PROTOCOL.md` amended; (c) `rejected` / `invalid_field` — reuses a §4 rejection reason inside the runner, against §9's split between parse-time and runner reasons. **Recommendation: (b)**, body line naming the rule (`default branch`, `diverged from origin`). The "checked out elsewhere" case stays `git_error` (git did fail).
- [ ] **Q2 — How this relates to the `base` field of central supervision (HIGH: bus contract, hard to undo once both are released).** Found: spec 18 defines `base` (run on `zyggy/<id>` started from `base`, then fast-forward `base`); not on `main`, unreleased. Both features push to a named branch by fast-forward only. Options: (a) keep both: `base` for mandate jobs, `branch` for owner jobs — two fields with nearly one meaning; (b) one field: spec 18's mandate jobs set `branch` instead of `base`, its fast-forward rule becomes this spec's continue rule, and spec 18 is amended before its group B is released; (c) build this feature on top of `base` by renaming it `branch`. **Recommendation: (b)**, decided before slice 1, and 18's next slice waits for it; the mandate check ("`base` outside `branches`") then applies to `branch`.
- [ ] **Q3 — Field names and shapes (HIGH: signed bus contract, hard to undo).** Options: (a) `branch` + `description` as specified; (b) `branch` + `branch_description`, to avoid confusing it with the job body; (c) a lowercase-only name rule (`[a-z0-9._/-]`). **Recommendation: (a)** with the mixed-case rule above (`description` reads naturally and matches the item envelope's one-line `description`); 200 characters for the description, 100 for the name.
- [ ] **Q4 — Does the description also go into a pull request when one exists? (HIGH: security — a new credential with write access on project repositories.)** Found: the node holds credentials for the bus only; the project push uses the machine's own git credentials; §9 allows GitHub only behind `IBusProvider`. Options: (a) defer; the owner opens PRs, the commit subject is pre-filled by GitHub from it; (b) update the PR body when a PR for the branch exists (needs a PR-scoped token per project and a new adapter); (c) have Central open/update the PR with its read token extended to write. **Recommendation: (a)**, a later roadmap entry if wanted.
- [ ] **Q5 — Does a job continuing a named branch first fast-forward it from the remote?** Options: (a) yes, fast-forward only; diverged → refused (as specified); (b) no, use the local branch as it stands (today's `zyggy/<id>` rule) — a second machine would then start from stale code and its push fail; (c) reset local to remote — loses kept commits. **Recommendation: (a).**
- [ ] **Q6 — What happens when the named branch has diverged from the default branch (seen today: a job could not fast-forward because `main` had moved).** Found: a named branch continues from its own tip, so `main` moving never blocks its push; the divergence only matters at merge time. Options: (a) nothing automatic; one report line "behind origin/<default> by n" (as specified); (b) the node merges the default branch into the named branch before the run, refusing on conflict — unattended merges, an extra merge commit per job; (c) rebase — needs a force-push, excluded. **Recommendation: (a).** A branch *diverged from its own remote* is Q5/AC-9.
- [ ] **Q7 — `--branch` without `--worktree`.** Options: (a) refuse, exit 2 (as specified); (b) imply `--worktree`. **Recommendation: (a)**: explicit, and the envelope rule stays the same as the CLI rule.
- [ ] **Q8 — Older nodes.** Found: a node before this feature ignores the fields and runs on `zyggy/<id>` (§4 unknown fields). Options: (a) accept; release to Central and the laptops together, the report shows the real branch name; (b) make the fields mandatory-to-understand (a schema bump) — every node must upgrade first. **Recommendation: (a).**

## Proposed amendments (awaiting the owner)

Not applied. Only the owner edits the founding spec; `PROTOCOL.md` lives in `zyggy-core` and changes with §4. **Conditional**: amendments 1–3 assume Q3(a) and change with its answer (the field rule in 3 must then be copied from Behavior again); 4 and 6 name `branch_refused` and assume Q1(b) — with Q1(a), 6 is dropped and 4 says `git_error`; 4 also assumes Q5(a) and Q2(b) leaves it unchanged.

1. **File** `_specs/00 - Personal Agent Platform — Technical Specification.md`, **§4 job example**. Present: `worktree: true` (one line, followed by `allowed_tools: …`). New: after `worktree: true` insert the two lines
   `branch: feature/12-cart       # optional; requires worktree: true; never the default branch (deliverable 53)`
   `description: Cart, slice 2: totals   # optional; one line, the node's commit subject and a report line`
2. **Same file, §4 "Mandatory fields per type", row `job`**. Present: `` | `job` | `project` | `agent` (none), `worktree` (`false`), `allowed_tools` (…), `report_back` (empty), `timeout_minutes` (…), `deadline` (none), `attempt` (`1`) | ``. New: the Optional column ends with `` …, `attempt` (`1`), `branch` (none; only with `worktree: true`), `description` (none) ``.
3. **Same file, §4, new paragraph after "Report-specific fields: …"**. Present: (none; inserted after the paragraph starting "Report-specific fields:"). New: "Job-specific fields (deliverable 53): `branch` (a git branch name of 1–100 characters from `[A-Za-z0-9._/-]`, no empty segment, no segment starting with `.` or `-`, no segment ending in `.lock`, no `..`, not ending in `/` or `.`, not `HEAD`, not starting with `zyggy/` in any letter case; only with `worktree: true`) and `description` (one line of 1–200 characters, no control characters). Both are data, never instructions, and never shown to the model."
4. **Same file, §6 Pre-flight step 4**. Present: "If `worktree: true`: fetch `origin` (best effort), then `git worktree add <nodeDir>/worktrees/<tenant>/<id> -b zyggy/<id> <base>`, … If `false`: run in the project directory, never commit." New: insert before "If `false`": "With `branch` (deliverable 53) the worktree is on that branch instead of `zyggy/<id>`: refused (`branch_refused`) when it is the default branch or when the local and remote branch have diverged; otherwise created from `<base>` when it exists nowhere, created from or fast-forwarded to `origin/<branch>` when that exists, else used as it stands. The node's commit subject is `description` when given, its body `zyggy job <id>`; the branch is pushed without force when its tip is not yet on `origin`; a local branch is deleted afterwards only when the run created it."
5. **Same file, §6 Report assembly**. Present: "`diff_ref` = pushed branch SHA, else null." New: keep that line and add the bullet "Body lines after the summary: the branch line (pushed / kept / worktree kept) naming the job's branch, `description: <text>` when the job has one, and `branch <name> is <n> commits behind origin/<default>` when a pushed named branch is behind (deliverable 53)."
6. **Same file, §9 design rules, the `JobRunner` bullet**. Present: "… a `reason` code from a closed enum (`unknown_project`, `unknown_agent`, `locked`, `timeout`, `dlp_filter`, `claude_error`, `git_error`, `schema_unsupported`, `budget_exceeded`)." New: "… a `reason` code from a closed enum (`unknown_project`, `unknown_agent`, `locked`, `timeout`, `dlp_filter`, `claude_error`, `git_error`, `schema_unsupported`, `budget_exceeded`, `branch_refused`)."
7. **File** `PROTOCOL.md` (repository `zyggy-core`), **job fields and reason list** (mirror of §4/§9; not read in this run, other repository). Present: the job field table and the reason list as published. New: the same additions as amendments 1–3 and 6, word for word.
