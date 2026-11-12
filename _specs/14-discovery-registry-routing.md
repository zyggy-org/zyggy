# Spec: 14 — The discovery registry and routing by project name

> Founding-spec sections: §1 Goals (project registry), §3 Components (Node "Publish registry", Cli `discover`, Core "registry model", skills `delegate`/`discover`), §4 Repository layout, Write rules, "Registry file", §5 Poll loop items 2–3, §6 Pre-flight steps 1–2 and Concurrency, §7 Dream step 5 and `list_nodes`, §8 Work boundary, Injection, Secrets, §9 Solution structure (`Registry/`, `DiscoveryTimer`, `discover`), Packages, design rules, §10 `node.json` (`devRoots`, `workHours`, `role`), §11 Logging (phase `discover`), Health endpoint (registry subset), Alerts "Node silent", Runbooks, §12 Definition of done, §13 Decisions (MVP scope, tenancy shape, HMAC envelopes, stateless nodes), §14 Policy as data. Roadmap entry: `_plans/ROADMAP.md` #14 (hand-off brief of 2026-11-07, ten open points).

## User Story

**As** Central (the owner's `delegate` skill in 15, the Hub's `list_nodes` in 21),
**I want** every node to publish what it can run — its Claude Code projects with their subagents and skills, plus a liveness stamp — and to accept a job that names a project by that registry name,
**So that** a job is routed by speciality and name rather than by a machine-specific path, and the P2 gate's first part (registry published, a job routed by name) passes.

**As** a Node (home laptop, work laptop, Central's own node),
**I want** to resolve a job's `project` against my own latest scan and to refuse any path outside my `devRoots`,
**So that** nothing on the bus — signed or not — can steer a run into a folder I did not discover myself.

---

## Acceptance Criteria

Legend: (U) unit test in `tests/Zyggy.Core.Tests`; (I) integration test in `tests/Zyggy.Integration` against the bare bus repository and `tools/fake-claude`; (G) the P2 gate scenario `tests/Zyggy.Integration/Gates/P2_RegistryWorktreeSweep.cs`, part 1; (O) owner-run check at the plan's final gate; (D) documentation.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 (U) | A temporary tree with two `devRoots`; under them: a folder with `.claude/` and a git repository, a folder with `.claude/` that is not a git repository, a folder without `.claude/`, a hidden folder (`.cache`) with `.claude/`, a file, a symbolic link (or junction) to a folder with `.claude/`, and a project nested two levels down (`<root>/org/repo/.claude/`) | `DiscoveryScanner.ScanAsync(devRoots, Full)` | Exactly the direct children holding a `.claude/` directory are projects (the non-git one included: the registry describes Claude Code projects, pre-flight checks git at run time); the hidden folder, the file, the link and the nested project are not; symbolic links are never followed; projects are ordered by `name` ordinal; nothing under a project other than `.claude/agents/*.md`, `.claude/skills/*/SKILL.md` and `CLAUDE.md` is opened. |
| AC-2 (U) | A project with `.claude/agents/env-debugger.md` (front matter `description: "Diagnoses failing environments"`), `.claude/agents/plain.md` (no front matter), `.claude/agents/bad.md` (unparseable front matter), `.claude/agents/with space.md`, `.claude/skills/deploy-staging/SKILL.md` (description of 170 characters containing a tab and a `\u0007`), `.claude/skills/empty/` (no `SKILL.md`) | the scan | `agents` = `[{env-debugger, "Diagnoses failing environments"}, {plain, ''}, {bad, ''}]` — `with space` is skipped (name not `[A-Za-z0-9._-]+`, the same rule as pre-flight's agent check); `skills` = `[{deploy-staging, <text with the control characters removed, then cut to exactly 150 characters without splitting a surrogate pair>}]`; `empty` is skipped; both lists ordered by `name` ordinal; nothing throws. |
| AC-3 (U) | Projects whose `CLAUDE.md` is (a) `# Title\n\n> quote\n- item\n\`\`\`\nfence\n\`\`\`\nMulti-tenant booking SaaS, .NET/Blazor\n`, (b) headings only, (c) absent, (d) 1 MiB of text | the scan | `summary` = (a) `Multi-tenant booking SaaS, .NET/Blazor`; (b) and (c) the key is omitted; (d) only the first 64 KiB are read and the rule applies to them. The summary is treated like a description (control characters removed, 150 characters). |
| AC-4 (U) | A substituted `GitClient` (through `IProcessRunner`) answering, per project: (a) `symbolic-ref refs/remotes/origin/HEAD` → `refs/remotes/origin/main`; (b) no `origin/HEAD` but `refs/heads/main` exists; (c) neither, `refs/heads/master` exists; (d) none of those, current branch `feature/x`; (e) detached HEAD and none of the above | the scan | `default_branch` = (a) `main`, (b) `main`, (c) `master`, (d) `feature/x`, (e) key omitted. The owner checking out a feature branch in a repository with `origin/HEAD` does not change the file (no flapping commits). |
| AC-5 (U) | Folders `calizr`, `My_App`, `Client Portal.v2`, `--`, `ÄÖ` | the scan | Names `calizr`, `my-app`, `client-portal-v2`; `--` and `ÄÖ` derive to the empty label and are skipped with a `NameUnusable` notice naming the folder (the path, never a file's content). A derived name is at most 63 characters (`Label` rule). |
| AC-6 (U) | `devRoots: ["C:/src", "C:/work"]`, folders `C:/src/calizr` and `C:/work/Calizr` both with `.claude/` | the scan | Neither is published; one `DuplicateName` notice names `calizr` and both paths. (A job for `calizr` on that machine is `unknown_project` until the owner renames or narrows `devRoots`.) |
| AC-7 (U) | `devRoots` with one entry that does not exist and one whose listing throws `UnauthorizedAccessException` (substituted file system access or an ACL in the temporary tree), plus one good root | the scan | The good root's projects are returned; one `RootMissing` and one `RootUnreadable` notice; no exception; `Elapsed` is set. |
| AC-8 (U) | 501 project folders under one root; a project with 101 agents | the scan | 500 projects (ordinal order) and a `ProjectCapReached` notice; 100 agents and an `EntryCapReached` notice. A scan longer than 30 s (fake clock advanced inside a substituted directory walk) adds a `SlowScan` notice. |
| AC-9 (U) | Secret patterns loaded from `tests/golden/secret-patterns/secret-patterns.txt`; an agent description and a `CLAUDE.md` summary each containing a sample from `secret-samples.txt` | the scan | The description is `''`, the summary key is omitted, one `SecretRedacted` notice per field naming the pattern and the project — never the matched text; a benign sample passes unchanged. |
| AC-10 (U) | The scanner constructed without secret patterns (`null`) and `detail: full` | the scan | The scan behaves as `detail: names` (AC-11) and returns one `PatternsUnavailable` notice: fail closed to less data, never a crash and never unscanned text on the bus. |
| AC-11 (U) | `detail: names` | the scan | Every project entry carries only `name` and `default_branch`; no `path`, `agents`, `skills`, `summary`, and nothing under `.claude/` or `CLAUDE.md` is read. |
| AC-12 (U) | A `RegistryDocument` with every field, including `work_hours` from a `WorkHoursSettings`, and one with the minimum (Central: no `work_hours`, no projects, no `health`) | `Serialize()` twice; `Parse(Serialize(d))` | Byte-identical output; top-level keys in ordinal order (`health`, `last_seen`, `machine`, `os`, `projects`, `schema`, `tenant`, `work_hours`), keys ordinal at every level, `\n` line endings, UTF-8 without BOM, no `---` delimiter, no `null` scalars (absent keys omitted, descriptions `''`), `schema: 1`; the parsed document equals the original. Serialisation goes through the solution's one YAML emitter (`FrontMatterEmitter`), the parser applies the §4 strictness (no anchors, aliases, tags, directives or second document; a mapping with unique string keys). |
| AC-13 (U) | Two documents that differ only in `last_seen` and `health`; two that differ in one description | `ContentEquals` | `true`; `false`. |
| AC-14 (U) | `Parse` over: a file with `schema: 2`; a non-mapping root; `tenant` not a label; a project without `name`; a description of 151 characters; 50 KiB of unrelated YAML | `Parse` | Each is a refusal with a one-word field and a short detail (`schema`, `root`, `tenant`, `projects[0].name`, `projects[0].agents[0].description`, …); `Parse` never throws on input. A missing optional key is not a refusal. |
| AC-15 (U) | `RegistryPublisher` over a substituted bus (the bus's current file bytes injected) and `FakeTimeProvider`; `lastSeenHours: 6` | first publish; same content 1 h later; same content 6 h 1 min later; changed content 2 h after that; a clock 1 h earlier than the file's `last_seen` | `Published` (file written with `last_seen` = now, `health` = the given verdict); `Unchanged` (nothing written); `Published` with `last_seen` refreshed and the rest byte-identical; `Published` with both the content and `last_seen` refreshed; `Unchanged`. A file on the bus that does not parse counts as absent (first-publish path) and is logged once. |
| AC-16 (U) | `DiscoveryTimer` with a substituted scanner and publisher, `FakeTimeProvider`, `intervalMinutes: 60` | the host starts; the clock advances 59 min, then 1 min, then 3 h | One scan at start whose completion the loop awaits before `ResumeClaimedAsync`; none at 59 min; one at 60 min; three more over the 3 h; a scan still running when the next is due is not overlapped (the due one is skipped, counted once in the log at Debug); a scan that throws is logged once at Warning (exception type only) and the timer keeps its schedule. After every scan `RegistrySnapshot.Current` is the new document, even when publishing failed. |
| AC-17 (U) | `RegistrySnapshot` holding `calizr → C:/src/calizr`; `devRoots: ["C:/src", "/home/alice/src"]`; a substituted `GitClient` | `PreFlight.CheckAsync` for `project` = `calizr`; `unknown`; `Calizr`; `C:/src/calizr`; `C:/src2/x`; `C:/Src/calizr` (Windows only); `/home/alice/src/x/..`; `/home/alice/src/x` with `devRoots: []` | `Ok(C:/src/calizr)`; `Rejected(unknown_project, "unknown name")`; `"unknown name"` (not a label); `Ok`; `"outside devRoots"` (a sibling prefix is not a root); `Ok` on Windows (case-insensitive drive paths, ordinal elsewhere); `"outside devRoots"` after normalisation or refused by shape; `"outside devRoots"` — the P1 path-only clause is retired. After resolution the existing checks still run in order: a resolved folder removed after the scan → `"missing"`, not a git repository → `"not a git repository"`, no `.claude/` → `"no .claude"`; `agent` is checked under the resolved path exactly as in 06. The runner's reply body stays `unknown_project: <check>` and the model never starts. |
| AC-18 (U) | `JobDispatcher` with the same snapshot; two jobs naming `calizr` by name, one naming its path | admission while the first runs | The second and third are `Deferred(ProjectBusy)`: a name resolves to the same project key as its path; a name the snapshot does not know is admitted (the runner answers `unknown_project`, as in 07). |
| AC-19 (I) | The node host (`NodeHostHarness`, tenant `acme`, machine `home-laptop`) with `devRoots` naming a temporary root holding two scratch projects | the host starts and completes its first scan | Exactly one commit `acme registry home-laptop` on `main` adding `BusPaths.Registry(acme, home-laptop)`; the file parses with `tenant: acme`, `machine: home-laptop`, `os` of the test machine, both projects with `path`, `default_branch: main` and their agents; `last_seen` within the test's window; one Information line `registry published: 2 project(s)` with `phase=discover`. |
| AC-20 (I) | AC-19's host | the timer's next scan over an unchanged tree; then a third scratch project is added and the timer fires again | No commit for the unchanged scan (one Debug line `registry unchanged`); one commit for the changed tree; `git log` of `main` shows exactly two registry commits. |
| AC-21 (I) | A checkout with the node stopped | `zyggy discover` twice; then `zyggy discover --dry-run` after adding a project; then `zyggy discover --config <bad>` | First run: one commit, stdout `registry: 2 project(s), published`, exit 0; second: no commit, `registry: 2 project(s), unchanged`, exit 0; `--dry-run`: the full document on stdout, nothing written, no commit, exit 0; the bad config: `zyggy: configuration error: …`, exit 3. A push that still fails after the retry rule: stderr `zyggy: registry: push failed: <redacted detail>`, exit 1, the commit left local (the node's next write pushes it, §5). |
| AC-22 (I) | A checkout holding `registry/home-laptop.yaml` and `registry/central.yaml` | `zyggy verify` | Both are `skipped` ("registry file (unsigned in v1)"), the verdict is unchanged by their presence; `zyggy status` is unaffected. |
| AC-23 (I) | The sender pushes a foreign commit (`AdvanceMainFromSenderAsync`) right before the node's publish | the publish | The push is rejected once, the existing pull-and-retry path lands the registry commit on top, one Warning `registry: push retried` per streak; no second registry commit. |
| AC-24 (G) | The host from AC-19; `zyggy submit --to home-laptop --project <scratch name> --body …` from the sender | the loop | The job is claimed, the fake `claude` runs with the resolved project folder as its working directory (the harness's capture file appears there), and a signed `done` report lands on `main`; the commit subjects show `claim`, `registry`, `report`. |
| AC-25 (G) | The same host; `zyggy submit --project no-such-project` | the loop | `status: rejected`, `reason: unknown_project`, body `unknown_project: unknown name`; no capture file, no transcript — the model never started. |
| AC-26 (I) | The host running; the sender rewrites `registry/home-laptop.yaml` on `main` so that the scratch project's `path` points at a second folder with a different capture file; a job names the project | the next pull and the job | The node runs in the folder its own scan found, never in the tampered path; its next liveness or content publish restores its own file. (The bus copy is never an input to execution — §8 rule as an AC.) |
| AC-27 (I) | The sender commits a registry file of another machine (`registry/work-laptop.yaml`) and one under `tenants/other-org/registry/` | the node's next tick | The node pulls, processes no job, writes nothing, logs nothing at Information (§5 item 2); the foreign tenant's file is byte-identical on `main` and never opened. |
| AC-28 (I) | A `node.json` with `machine: central`, no `devRoots`, no `workHours` (plus the pickup variables the Central host needs) | the host starts | One commit `acme registry central` with `projects: []`, no `work_hours` key, `health` present: Central publishes like every node; `role` is not read. |
| AC-29 (U/I) | The node's `/health` body (from `HealthServer`) and `HealthRules.Evaluate(body, now, poll, <own version>, url)` | a publish | `health` = the level lowercased (`green`, `amber`, `red`); `zyggy discover` takes the value from `GET http://localhost:<port>/health` through the existing `HealthClient` when a node answers, else keeps the bus file's value, else omits the key. |
| AC-30 (U) | The scanner's and publisher's log output captured | every scenario above | No log line contains a description, a summary or a `CLAUDE.md` line: folder paths, names, counts, pattern names and notice kinds only. The publish log is one line per state change: Information on `Published`, Debug on `Unchanged`, Warning once per failure streak on `Failed`/`Busy`, Information when the streak ends. |
| AC-31 (I) | Every registry path in the code | review + the integration suite with tenant `acme` | Produced by `BusPaths.Registry`/`RegistryFolder`; `BusRepository.PublishRegistryAsync` refuses (caller bug) a document whose `tenant`/`machine` differ from the bus identity; the write goes through the existing one-commit write path and the checkout write lock; nothing reads `tenants/<other>/registry/`. |
| AC-32 (D) | The repo docs | the deliverable is done | `src/Zyggy.Core/README.md` (`Registry/` row; the "Jobs: unknown_project" runbook row rewritten; new runbook rows of the Failure-modes table), `src/Zyggy.Node/README.md` ("Not here yet" minus discovery; the `discovery` section), `src/Zyggy.Cli/README.md` (`discover` row: the verb and `--dry-run`, `--link` marked 15; the `devRoots` row "enforced"; the `discovery` keys), `tests/Zyggy.Integration/README.md` (the P2 gate file); the founding-spec amendments A14-1…A14-6 pasted by the orchestrating session once approved; `PROTOCOL.md` stays the placeholder (15 copies §4). |
| AC-33 (O) | The home laptop and Central after a restart of both nodes | the owner's check at the plan's final gate | `tenants/<org>/registry/home-laptop.yaml` and `registry/central.yaml` visible on the bus with plausible content; one real `zyggy submit --project <name>` from Central runs by name on the home laptop and its report shows in `zyggy status --id`; `zyggy discover --dry-run` on the home laptop lists the expected projects. |

---

## Decision Table

The ten open points of the roadmap entry are closed in the rows marked **(P1)…(P10)**.

| Founding-spec item (section) | Verdict | Target type / library | Justification |
|------------------------------|---------|-----------------------|---------------|
| Automatic discovery of every Claude Code project, subagent and skill per machine (§1, §3 Node "Publish registry") | **Keep** | `Zyggy.Core.Registry.DiscoveryScanner`, `Zyggy.Node.DiscoveryTimer` | The P2 gate's first part; `delegate` (15) and `list_nodes` (21) consume it. |
| Scan source: "scan dev roots" for folders with `.claude/` (§3, §4) **(P1)** | **Keep**, precised | `DiscoveryScanner.ScanAsync(devRoots, detail)` — direct children of each root, hidden folders and symbolic links skipped | Depth 1 matches the §10 example (`C:/src/calizr`), bounds the walk (no `node_modules`, no recursion into clones) and needs no knob; a nested layout is handled by listing the inner folder as a root. A `node.json` list, exclude or alias of projects is **Defer**: the founding spec never had it, no gate needs it, and every alias is a second name to keep consistent. |
| Registry `name` when the folder name is not a label (§4 `project: calizr`, 08 `ProjectReference`) **(P1)** | **Reshape** | Derived label: lowercase, every run of characters outside `[a-z0-9]` → one `-`, trimmed, ≤ 63; empty → skipped with a notice | Skipping `MyApp` would make a common Windows folder unroutable with no remedy but renaming it on disk; derivation is deterministic, one line, and the collision it can cause is caught by the tie rule. |
| Two projects deriving the same name on one machine **(P2)** | **Reshape** (rule closed) | Neither published; one `DuplicateName` notice with both paths | "First root wins" silently routes a job to one of two folders; a suffix invents a name no folder has. Refusing both is the only choice that never runs a job in the wrong place; the owner fixes it once. |
| The same name on two machines **(P2)** | **Keep** | Registry file per machine; pre-flight resolves only against its own scan; `delegate` picks the machine first (`to`), then the name | §4: the file is per machine, the envelope already carries `to`; a name is scoped to its machine by construction. |
| "A registry name is resolved against `registry/<me>.yaml`" (§6 step 1) **(P5)** | **Reshape** | `RegistrySnapshot` — the node's own last scan, in memory; the bus copy is never read for resolution; the resolved path is additionally required to lie under `devRoots` | The bus copy of an unsigned file is writable by any machine with push rights and by a diverged checkout; a name must never steer a run into a folder this node did not discover. The scan runs before any job is admitted, so the snapshot is always at least as fresh as the file. → A14-3 (OQ-2). |
| "every file HMAC-signed" applied to the registry (§4 opening sentence; 08 deferred the policy) **(P5)** | **Reshape** | Unsigned plain YAML document; `BusAuditor` keeps skipping it | Nothing executes from the bus copy (row above); signing would need a second canonical form, golden files, a verify path and a `delegate` that verifies — all to protect a routing hint whose worst-case tampering yields `unknown_project` on the node. → A14-1 (OQ-1). |
| Registry file fields `tenant`, `machine`, `os`, `last_seen`, `work_hours`, `projects[name, path, default_branch, agents, skills, summary]` (§4 example) **(P7, P9)** | **Keep**, reshaped in form | `RegistryDocument`, `RegistryProject`, `RegistryEntry`; `schema: 1` added (§14); `health` added (§11); `work_hours` structured as in `node.json` (`timezone`, `days`, `from`, `to`) instead of a free-text line; keys ordinal, optional keys omitted | `schema` is the §14 constraint applied to a shared contract read by 15, 21, 22; a structured `work_hours` needs no compaction rule (`Mon-Fri` vs `Mon,Wed`) and is what 22 will evaluate; ordinal keys are the envelope's canonical discipline reused. → A14-1. |
| `health` subset in the registry (§11 "every 6 h as `last_seen` and `health`") **(P7)** | **Keep** | `health: green \| amber \| red` = `HealthRules.Evaluate` over the node's own `/health` body, lowercased; in the liveness group with `last_seen` | One verdict function (08's rule) — the registry never re-derives "green"; the reasons stay in `/health`. |
| Liveness: `last_seen` at most every 6 h; commit only when content differs (§4) **(P3)** | **Keep** | `RegistryPublisher` + `RegistryDocument.ContentEquals` (ignores `last_seen`, `health`); `discovery.lastSeenHours` (6) | The whole bus-noise budget for Central's 1 s poll: ≤ 4 liveness commits per machine per day plus real changes. A file whose `last_seen` is in the future (clock skew) is treated as fresh. |
| Staleness of a registry file for routing **(P3)** | **Keep** (no new rule) | — | Routing on the node never reads the file (P5), so a stale or missing file cannot misroute; what a stale `last_seen` means for Central is 22's alert and 15's `delegate` wording. |
| A revoked machine's registry file (§11 `revoke-machine.md`) **(P3)** | **Keep**, runbook amended | — | The runbook moves `nodes/<machine>/` and must also `git rm` `registry/<machine>.yaml`, else `delegate` keeps offering a dead machine. → A14-6; no code. |
| Regenerate on service start and hourly (§4) | **Keep** | `DiscoveryTimer` (`BackgroundService`), `TimeProvider`, `discovery.intervalMinutes` (60); the loop awaits the first scan before `ResumeClaimedAsync` | A job must never be rejected `unknown name` because the first scan has not finished; the hourly rescan is the §4 text. |
| `zyggy discover` on demand (§3 verb list, §9) — in 14 or 15 **(P4)** | **Keep** in 14 | `zyggy discover [--dry-run] [--config] [--verbose]` | Rule R1: the verb is .NET; 15's `discover` skill is Markdown that calls it, and `--link` (symlinking shared skills) stays in 15 as the roadmap says. `--dry-run` is the owner's inspection for the gate check and costs one flag. |
| `agents.md` refreshed from the registry — `zyggy dream agents` (§7 step 5) **(P6)** | **Defer** | — | No P2 gate needs it; `delegate` (15) reads `registry/*.yaml` directly per §4 and never `agents.md`; the existing `agents.md` holds owner-stated lines the dream must not drop, so a generated section needs a memory-model design of its own; the Hub's `list_nodes` (21) is the on-demand equivalent. → OQ-3. |
| `list_nodes` reads `registry/*.yaml` (§7) | **Defer** to 21 | `RegistryDocument.Parse` is the reader it will use | The format and the parser are fixed here; the tool is 21. |
| `delegate` skill (§3) | **Defer** to 15 | — | Consumer of the format; Markdown in `zyggy-core`. |
| §5 item 2: a registry commit wakes every poller and produces no work | **Keep** (nothing to build) | AC-27 | The listener already lists only its own `jobs/`; the AC proves the absence of side effects. |
| §6 step 1 as amended: path accepted only under `devRoots` + exists + git + `.claude/`; P1 clause retired (O18) | **Keep** | `PreFlight.CheckAsync(job, git, snapshot, devRoots, ct)`; check words `unknown name`, `outside devRoots` added to the fixed list | O18 decided; `NodePolicy.DevRoots` already read. Existing tests and the `NodeHostHarness` must list their scratch root in `devRoots` (planner note). |
| §6 step 2 `agent` check | **Keep** unchanged | — | Runs under the resolved path. |
| §6 Concurrency: one job per project | **Reshape** | `JobDispatcher` resolves a name through the snapshot for its project key | 07 admitted name jobs without the busy rule because names did not resolve; now they do. |
| Central's own registry file; `role: central` (§10) **(P8)** | **Keep**, uniform | Every node publishes; Central's `devRoots` is normally empty → `projects: []`; `role` is not read | One code path; the file still carries Central's `last_seen`/`health` for 21 and 22. Central-local jobs by path (17's sweep) need a `devRoots` entry on Central — 17's configuration, not special code. |
| What the work laptop publishes (§8 Work boundary; 20) **(P9)** | **Reshape** | `discovery.detail: full \| names` in `node.json`; `names` = `name` + `default_branch` only, nothing under `.claude/` or `CLAUDE.md` read | §13 Q1 allows summaries on the personal repo, but project paths, agent descriptions and a `CLAUDE.md` line of the employer's projects are harvested content; 20 sets `names` without a format change. `summary` and descriptions are derived content under §8 and are omitted at that level. |
| Injection: descriptions and summaries rendered into Central's prompts later (§8) | **Keep** | Control characters removed, 150-character cap, `''` when absent; the §4 amendment says "data, never instructions" | The consumers (15, 21) inherit a bounded, control-free string. |
| Secrets: the scanner must never publish a line matching the secret patterns (§8; 12's `ZYGGY_SECRET_PATTERNS`) | **Keep**, fail-closed | `SecretPatterns.TryMatch` per published string; a match → `''`/omitted + notice; no patterns registered → `names` detail + notice | The Node host registers no patterns today (only `context`, `archive` and Central's pickup do); degrading to names is the safe default on a laptop whose scheduled task lacks the variable. The node and the verb register the patterns through the existing `AddSecretPatterns` when the environment names them. |
| `Registry/ RegistryDocument, DiscoveryScanner` (§9) | **Keep** | `Zyggy.Core.Registry` namespace: `RegistryDocument`, `RegistryProject`, `RegistryEntry`, `RegistryOs`, `DiscoveryScanner`, `ScanResult`, `ScanNotice`, `RegistrySnapshot`, `RegistryPublisher`, `DiscoveryOptions`, `DiscoveryDetail` | Names as the founding spec lists them, plus the holder and publisher the two triggers share. |
| YAML serialisation (§9 Packages: `YamlDotNet` "serializer with sorted keys") **(P10)** | **Library** (already referenced) | `YamlDotNet` 18.1.0 — present in `Directory.Packages.props`; reuse `FrontMatterEmitter` (the one emitter) and the front-matter parsing core | No new package; one emitter in the solution; the registry's canonical rules are the envelope's. |
| `devRoots` (§10) | **Keep**, now enforced | `node.json` → `NodePolicy.DevRoots` through `IPolicySource` | O18; stays `node.json` under the MVP-scope decision (§14 `dev_roots` policy not built). |
| `workHours` (§10) → `work_hours` | **Keep** | From `NodeConfiguration.WorkHours`; omitted when absent | Central has none; 22 evaluates "inside its work hours". |
| Phase `discover` logging; once per state change (§11) | **Keep** | `LoggerMessage` lines listed under Behaviors | §11 rule. |
| `/health` gains a discovery field (§11 field list; brief) | **Defer** | No change to `NodeHealth` | The §11 list is verbatim and shared by 07/08/21/22; the registry file and the log already show the last publish. |
| "Node silent" alert (§11) | **Defer** to 22 | — | 22 evaluates `last_seen`. |
| `bus-conflict.md` "re-run discover" (§11) | **Keep** | `zyggy discover` | The runbook's remedy now exists. |
| Policy as data, `dev_roots` per machine (§14) | **Defer** (§14, MVP-scope decision) | — | Not built; `IPolicySource` shape kept. |
| `schema: 1` on the shared contract (§14) | **Keep** | `RegistryDocument.Schema = 1`; `Parse` refuses a higher major | The brief's risk line; the same rule as the envelope. |
| Registry audit in `zyggy verify` (tenant/machine of the file vs its path) | **Defer** | `BusAuditor` keeps the `skipped` entry | DoD only requires `verify` to keep passing; auditing a routing hint adds code with no gate. |

---

## Contracts

### Types and interfaces

Namespace `Zyggy.Core.Registry` unless stated. No new seam: the five §9 seams stay as they are; the bus write goes through `BusRepository` over `IBusProvider`, git through `GitClient`/`IProcessRunner`, time through `TimeProvider`.

```csharp
public sealed record RegistryDocument(
    TenantId Tenant, MachineName Machine, RegistryOs Os,
    DateTimeOffset? LastSeen,                 // the liveness group …
    HealthLevel? Health,                      // … (null = key omitted)
    WorkHoursSettings? WorkHours,             // null = key omitted (Central)
    IReadOnlyList<RegistryProject> Projects)  // ordered by Name ordinal
{
    public const int Schema = 1;
    public byte[] Serialize();                                        // the one emitter, keys ordinal, "\n", UTF-8 no BOM
    public static RegistryParse Parse(ReadOnlyMemory<byte> bytes);     // never throws on input
    public bool ContentEquals(RegistryDocument other);                // ignores LastSeen and Health
}
public sealed record RegistryParse(RegistryDocument? Document, string? Field, string? Detail);   // Document != null xor Field != null

public sealed record RegistryProject(
    string Name,                               // the derived label
    string? Path,                              // absolute, "/" separators as written by the scanner; null under Names
    string? DefaultBranch,                     // null = omitted
    IReadOnlyList<RegistryEntry> Agents,       // empty under Names
    IReadOnlyList<RegistryEntry> Skills,
    string? Summary);                          // null = omitted

public sealed record RegistryEntry(string Name, string Description);   // Description: control characters removed, ≤ 150 chars, "" when absent

public enum RegistryOs { Windows, Linux, Macos, Other }                 // wire: lowercase
public enum DiscoveryDetail { Full, Names }                             // node.json: "full" | "names"

public sealed class DiscoveryOptions                                    // IOptions<DiscoveryOptions>, filled from node.json "discovery"
{
    public int IntervalMinutes { get; set; } = 60;     // 1–1440
    public int LastSeenHours { get; set; } = 6;        // 1–168
    public DiscoveryDetail Detail { get; set; } = DiscoveryDetail.Full;
    public int MaxProjects { get; set; } = 500;        // code default, no node.json key
    public int MaxEntriesPerProject { get; set; } = 100;
    public TimeSpan SlowScanThreshold { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class DiscoveryScanner(GitClient git, SecretPatterns? patterns, DiscoveryOptions options, TimeProvider clock, ILogger<DiscoveryScanner> log)
{
    public Task<ScanResult> ScanAsync(IReadOnlyList<string> devRoots, DiscoveryDetail detail, CancellationToken ct);   // never throws for a file-system or git failure
}
public sealed record ScanResult(IReadOnlyList<RegistryProject> Projects, IReadOnlyList<ScanNotice> Notices, TimeSpan Elapsed, DiscoveryDetail EffectiveDetail);
public sealed record ScanNotice(ScanNoticeKind Kind, string Subject, string? Detail);   // Subject: a root, a folder path, a name or a pattern name — never file content
public enum ScanNoticeKind { RootMissing, RootUnreadable, DuplicateName, NameUnusable, ProjectCapReached, EntryCapReached, SecretRedacted, PatternsUnavailable, SlowScan }

/// The node's own latest scan — the only source pre-flight and the dispatcher resolve a name from. Thread-safe; empty until the first scan.
public sealed class RegistrySnapshot
{
    public RegistryDocument? Current { get; }
    public void Replace(RegistryDocument document);
    public bool TryResolve(string name, out string path);   // label shape required; Names-detail entries (no path) never resolve
}

public enum PublishOutcome { Published, Unchanged, Busy, Failed }
public sealed class RegistryPublisher(BusRepository bus, DiscoveryOptions options, TimeProvider clock, ILogger<RegistryPublisher> log)
{
    /// Reads the bus copy, applies the liveness rule, writes one commit when needed. `health` null keeps the bus copy's value.
    public Task<PublishOutcome> PublishAsync(RegistryDocument content, HealthLevel? health, CancellationToken ct);
}
```

`Zyggy.Core.Bus.BusRepository` gains two members (same write path, write lock, pull-and-retry and redaction as every other write):

```csharp
public Task<byte[]?> ReadOwnRegistryAsync(CancellationToken ct);                       // BusPaths.Registry(me) from the checkout; null when absent
public Task<WriteOutcome> PublishRegistryAsync(byte[] bytes, CancellationToken ct);    // subject "<tenant> registry <machine>"; AssertMayWrite(BusFolder.Registry)
```

`Zyggy.Core.Jobs.PreFlight.CheckAsync(JobFields job, GitClient git, RegistrySnapshot registry, IReadOnlyList<string> devRoots, CancellationToken ct)` — the §6 order: (1) a non-rooted `project` must be a label and resolve through the snapshot, else `Rejected(UnknownProject, "unknown name")`; a rooted `project` is normalised (`GetFullPath`, trailing separator trimmed) and must equal a `devRoots` entry or lie under one with a separator boundary (case-insensitive on Windows, ordinal elsewhere), else `Rejected(UnknownProject, "outside devRoots")`; (2) the resolved path: `missing`, `not a git repository`, `no .claude` as in 06; (3) `agent` as in 06. `PreFlightResult.Rejected.Check` is one of the eight fixed words. `JobRunner` passes `policy.DevRoots`; `JobDispatcher` takes the snapshot for its project key.

`Zyggy.Node.DiscoveryTimer : BackgroundService` — runs `scan → snapshot.Replace → publish` at start and every `IntervalMinutes` on the injected clock; exposes `Task FirstScan` that `PollLoop` awaits after `BusListener.StartAsync` succeeded and before `ResumeClaimedAsync`; never overlaps two scans; `health` from the `HealthServer`'s current body through `HealthRules.Evaluate` with the node's own version. Registered in `AddNodeServices`/`NodeHost.Build` next to the loop; the CLI composition root registers the scanner, snapshot and publisher without the timer.

### Envelope / file formats

The envelope (§4) is unchanged; `PROTOCOL.md` stays the placeholder. The registry file is a plain YAML document at `BusPaths.Registry(tenant, machine)`, unsigned (OQ-1), written only by its machine. Example as `Serialize()` emits it (quoting as the emitter's §4 ladder decides):

```yaml
health: green
last_seen: 2026-09-27T14:00:00Z
machine: home-laptop
os: windows
projects:
  - agents:
      - description: 'Diagnoses failing environments: config, secrets, connectivity, startup errors'
        name: env-debugger
      - description: Writes and verifies EF Core migrations against staging
        name: db-migrator
    default_branch: main
    name: calizr
    path: C:/src/calizr
    skills:
      - description: Builds, tags and deploys the current branch to staging
        name: deploy-staging
    summary: Multi-tenant booking SaaS, .NET/Blazor
schema: 1
tenant: acme
work_hours:
  days: [Mon, Tue, Wed, Thu, Fri]
  from: '08:00'
  timezone: Europe/Brussels
  to: '19:00'
```

Rules: keys in ordinal order at every level; block mappings, flow sequences of scalars, block sequences of mappings; `\n` endings; UTF-8 without BOM; no `---`; timestamps `YYYY-MM-DDTHH:MM:SSZ`; `tenant`/`machine` labels; optional keys (`health`, `work_hours`, `path`, `default_branch`, `summary`) omitted when absent; `agents`/`skills` always present (`[]` under `names`); `description` always present (`''`). Under `discovery.detail: names` a project is `{default_branch?, name, agents: [], skills: []}`. Commit subject `<tenant> registry <machine>`.

### CLI surface

`zyggy discover [--dry-run] [--config <path>] [--verbose]` — loads `node.json` (exit 3 on refusal, the usual line), runs the checkout checks of `BusHost.CheckCheckoutAsync`, registers the secret patterns from the environment when present (absence is not an error: the scan degrades to `names` and prints one stderr note), scans `devRoots` with `discovery.detail`, then: without `--dry-run`, publishes and prints `registry: <n> project(s), published|unchanged` (exit 0), or `zyggy: registry: push failed: <detail>` (exit 1), or `zyggy: registry: checkout busy` (exit 1, another zyggy process holds the write lock); with `--dry-run`, prints the serialised document to stdout and the notices to stderr, writes nothing, exit 0. Notices (duplicates, unusable names, missing roots, redactions) go to stderr as one line each in every mode. `--link` is 15.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `devRoots` | `node.json` → `NodePolicy.DevRoots` via `IPolicySource` | `[]` (nothing discovered; every path job → `outside devRoots`) | `node.json` only in v1; a §14 policy could only narrow it |
| `discovery.intervalMinutes` | `node.json` → `DiscoveryOptions.IntervalMinutes` | `60` (1–1440) | `node.json` only; the timer, not the CLI |
| `discovery.lastSeenHours` | `node.json` → `DiscoveryOptions.LastSeenHours` | `6` (1–168) | `node.json` only; both triggers |
| `discovery.detail` | `node.json` → `DiscoveryOptions.Detail` | `full` | `node.json` only; `names` on the work laptop (20); no patterns registered forces `names` for that scan |
| scan caps (500 projects, 100 entries, 30 s slow-scan) | code (`DiscoveryOptions`, tests may change) | as listed | none in `node.json` |
| secret patterns | environment (`ZYGGY_SECRET_PATTERNS`, else derived as `SecretPatternsLocation.Resolve`) | none → `names` | the Node host and the verb both call `AddSecretPatterns` when the environment names a file |

The `NodeConfigurationReader` validates the `discovery` section like `workHours` (object, ranges, `detail` one of two words) and the secret-key refusal applies to it as to every section.

---

## Behaviors & Conventions

- **Scan** — direct children of each `devRoots` entry that are directories, not hidden (`.`-prefixed), not reparse points/symbolic links, and contain a `.claude/` directory; ordered by derived name; `path` written with `/` separators as the normalised full path. Override: none (list a nested folder as its own root).
- **Name** — derived label; collisions on one machine exclude all colliders; a name is scoped to its machine. Override: none.
- **Agents / skills** — `.claude/agents/<name>.md` and `.claude/skills/<name>/SKILL.md`; name = file/folder name, must match `[A-Za-z0-9._-]+`; `description` = the front-matter scalar `description` (a non-scalar or unparseable front matter → `''`), control characters (Unicode category Cc, plus U+2028/U+2029) removed, cut to 150 characters without splitting a surrogate pair. Override: none.
- **Summary** — first non-empty line of `CLAUDE.md` (first 64 KiB) that does not start with `#`, `>`, `-`, `*`, `+`, a digit followed by `.`/`)`, or a fence; same cleaning as a description. Override: `discovery.detail: names` omits it.
- **`default_branch`** — `refs/remotes/origin/HEAD` target, else `main` if that branch exists, else `master`, else the current branch, else omitted; obtained through `GitClient` only.
- **Liveness** — `last_seen` and `health` refreshed on every commit; a commit happens iff the content differs (`ContentEquals` false) or the bus copy's `last_seen` is older than `lastSeenHours` or absent; a `last_seen` in the future counts as fresh. `health` on the node = `HealthRules.Evaluate` over its own `/health` body; `zyggy discover` reads the local node's `/health` when it answers, else keeps the bus value, else omits.
- **Triggers** — the node: start (awaited before claims resume) and every `intervalMinutes`; `zyggy discover`: on demand, same scanner and publisher, same write lock (`BusCheckoutLock` write lock; the node's listen lock is not taken). The node's snapshot is not refreshed by a CLI run; the next timer scan does that.
- **Resolution** — pre-flight and the dispatcher resolve only against `RegistrySnapshot`; a `names`-detail snapshot has no paths, so on such a machine every name is `unknown name` and jobs must name a path under `devRoots` (the work laptop's mode; 20 may revisit).
- **Secrets** — every published string is tested with `SecretPatterns.TryMatch`; a match is replaced by `''` (description) or omitted (summary) and produces one notice naming the pattern; without registered patterns the scan runs as `names`.
- **Logging** (phase `discover`, `tenant`/`machine` enriched; no job id) — `registry published: {Count} project(s)` (Information, per commit), `registry unchanged` (Debug), `registry: push retried` (Warning, once per streak), `registry: push failed: {Detail}` / `registry: checkout busy` (Warning, once per streak; Information `registry: publishing again` when it ends), one Warning per notice kind when its subject set changes between scans (`discovery: duplicate name {Name}: {Paths}`, `discovery: root missing {Root}`, `discovery: root unreadable {Root}`, `discovery: name unusable for {Folder}`, `discovery: {Kind} cap reached`, `discovery: secret pattern {Pattern} redacted in {Project}`, `discovery: secret patterns not registered; publishing names only`, `discovery: scan took {Seconds} s`), `discovery: scan skipped, previous still running` (Debug), `discovery: scan failed {ExceptionType}` (Warning). Never a description, summary or `CLAUDE.md` text.
- **Tenancy** — the document's `tenant`/`machine` are the bus identity; `PublishRegistryAsync` refuses a mismatch as a caller bug; no code reads another tenant's registry folder.
- **Data, never instructions** — the §4 amendment states it; 15's prompt template and 21's tool render registry strings inside data fences.

---

## Failure modes

| Situation | Observable outcome | Runbook entry (Core README unless stated) |
|-----------|--------------------|-------------------------------------------|
| Registry push rejected (non-fast-forward) | pull-and-retry (§5 rule) lands the commit; `registry: push retried` once per streak | `bus-conflict.md` (existing; "re-run discover" now means `zyggy discover`) |
| Push still failing after retries / remote unreachable | `registry: push failed: <redacted>` once per streak; the commit stays local and goes out with the node's next write; the timer retries next interval; `zyggy discover` exits 1 | "Registry: push failed" — check `zyggy probe reach`; the node's next write carries it |
| Checkout write lock held by another zyggy process | `PublishOutcome.Busy`; `registry: checkout busy` once per streak; the scan result still replaces the snapshot; retried next interval; CLI exits 1 | "Registry: checkout busy" — a `zyggy` verb or the node is writing; retry |
| A `devRoots` entry missing or unreadable | notice + one Warning per state change; the other roots are published; a job for a project under that root → `unknown_project: unknown name` | "Discovery: root missing / unreadable" — fix the path or the ACL in `node.json`; restart or `zyggy discover` |
| A project in the registry whose folder is gone at run time | `status: rejected`, `reason: unknown_project`, body `unknown_project: missing`; the next scan drops it from the file | "Jobs: unknown_project" (rewritten): the four checks and the two resolution forms |
| Job names an unknown name / a non-label / a `names`-only machine | `unknown_project: unknown name` | same row |
| Job names a path outside `devRoots` (or `devRoots` empty) | `unknown_project: outside devRoots` | same row — add the root to `devRoots` or submit by name |
| Two folders derive the same name | neither published; Warning with both paths; jobs by that name `unknown name` | "Discovery: duplicate name" — rename a folder or narrow `devRoots` |
| Folder name derives to an empty label | skipped; Warning naming the folder | "Discovery: name unusable" — rename the folder |
| More than 500 projects / 100 entries | first N in ordinal order; Warning | "Discovery: cap reached" — narrow `devRoots` |
| A scan longer than 30 s | Warning with the duration; the scan completes | "Discovery: slow scan" — a root on a network drive or thousands of children; narrow `devRoots` |
| A description or summary matches a secret pattern | `''`/omitted; Warning naming the pattern and the project | "Discovery: secret pattern redacted" — remove the secret from the project file |
| Secret patterns not registered on the node | that scan publishes `names` detail; Warning once | Node README "discovery": set `ZYGGY_SECRET_PATTERNS` in the service's environment (the laptop task / the systemd unit) |
| The bus copy of the own file does not parse (edited by hand, tampered) | treated as absent: re-published on the next publish; Warning once | "Registry: file rewritten" — expected; nothing to do |
| The scanner throws (bug) | `discovery: scan failed <type>` once; the previous snapshot stays; next interval retries | "Discovery: scan failed" — `--verbose` log, open an issue |
| First scan not finished when a job arrives | impossible by contract: the loop awaits `FirstScan` before resuming claims | — |
| `discovery` section invalid in `node.json` | node exit 3 / CLI exit 3 with `configuration error: discovery.<key>: <detail>` | Cli README configuration table |

---

## Dependencies

| Package | License | Why |
|---------|---------|-----|
| `YamlDotNet` 18.1.0 (already in `Directory.Packages.props`) | MIT | Emits the registry through the existing `FrontMatterEmitter` and parses it with the same strictness; no bespoke YAML. |

No new package. Everything else is the framework (`System.IO`, `TimeProvider`, `BackgroundService`, `IOptions<T>`, `System.CommandLine` already referenced).

---

## Deliberate deviations from the founding spec

Each is a proposed amendment the orchestrating session pastes once the owner approves the matching Open Question; the technical-analyst never edits the founding spec. **Applied 2026-11-07** by the orchestrating session after the owner approved OQ-1..OQ-3 at the spec gate ("approve"): A14-1, A14-3, A14-4, A14-5 and A14-6 are in the founding spec; A14-2 and §3 need no text change.

- **A14-1 (§4, OQ-1)** — opening sentence: replace "every file HMAC-signed" with "every envelope HMAC-signed; the registry file is unsigned, derived state (see Registry file)". Replace the "Registry file" example and paragraph with the example under Contracts and this text:

  > `discover` — the node service at start and every hour (`discovery.intervalMinutes`), and `agentbus discover` on demand — scans the direct children of every `devRoots` entry for folders holding `.claude/` and regenerates this file. The file is a plain YAML document, not an envelope and not signed: it is derived, re-creatable state that no machine executes from — a node resolves job names against its own scan (§6), and Central's `delegate` reads the files of its own tenant only as routing hints; registry files of other tenants are never read. It carries `schema: 1`; keys are in ordinal order at every level and lines end with `\n` (the envelope's canonical discipline); optional keys are omitted when absent. `last_seen` and `health` (the §11 verdict, `green | amber | red`) form the liveness group: a commit happens when the rest of the content differs from the file on the bus, or when `last_seen` is older than 6 hours (`discovery.lastSeenHours`); every commit refreshes both. A project's `name` is its folder name lowercased with every run of other characters replaced by one hyphen; two folders of one machine that derive the same name are both left out and logged. Each agent and skill entry carries the `description` front matter of its `.claude/agents/<name>.md` or `.claude/skills/<name>/SKILL.md`, copied verbatim with control characters removed and truncated to 150 characters (`''` when absent), so that `delegate` routes by speciality rather than by name (O24, decided 2026-09-28); `summary` is the first non-empty line of the project's `CLAUDE.md` that is not a heading, list item, quote or fence, treated the same way. With `discovery.detail: names` (the work laptop, §8) a project entry carries only `name` and `default_branch`. No published string matches a secret pattern, and every one of them is data, never instructions, wherever it is rendered later.

- **A14-2 (§4 Write rules)** — no text change; the registry write reuses the existing rule "a machine writes its own … `registry/<machine>.yaml`".
- **A14-3 (§6 step 1, OQ-2)** — replace with: "1. Resolve `project`. A registry name (a label, `[a-z0-9-]`) is resolved against the node's own latest discovery scan — the scan that produced its `registry/<me>.yaml`, held in memory by the node service; the bus copy of that file is never an input to execution. An absolute path is accepted only when it lies under a `devRoots` entry of `node.json` (§10). The resolved path must exist, be a git repository and contain `.claude/`. Anything else → report `status: rejected`, reason `unknown_project` (the P1 path-only rule ended with deliverable 14)."
- **A14-4 (§7 step 5, OQ-3)** — replace with: "5. Reserved: `agentbus dream agents` is not built (decision of 2026-11-07, deliverable 14); the Hub's `list_nodes` (deliverable 21) serves the registry summary and `agents.md` stays owner-stated."
- **A14-5 (§10 `node.json` example)** — after `"devRoots": […],` insert `"discovery": { "intervalMinutes": 60, "lastSeenHours": 6, "detail": "full" },   // detail: full | names — names publishes project names and default branches only (the work laptop, §8)`; in "Work laptop" append ", `discovery.detail: names`".
- **A14-6 (§11 `revoke-machine.md`)** — append "and `git rm` its `tenants/<org>/registry/<machine>.yaml`" to the move sentence.
- **§3 Components** — no change: the Node row already says "Publish registry"; the `discover` skill row stays (15 writes the Markdown).

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| `devRoots` lists the same root twice, or a root and its own child | Roots are de-duplicated after normalisation; a root that is a child of another root is scanned once as a child project, not as a root (its own children are not projects). |
| A root is itself a Claude Code project (`<root>/.claude/`) | Not a project (only children are); list its parent as the root instead. Documented in the Cli README `devRoots` row. |
| A project folder is a git worktree (`.git` is a file) | Published; `default_branch` through git; pre-flight's `rev-parse --git-dir` accepts it as in 06. |
| `CLAUDE.md` has CRLF endings or a BOM | The BOM is skipped, `\r` is a control character and is removed. |
| An agent file with `description:` spanning several lines (block scalar) | The parsed scalar text with its line breaks removed (they are control characters) before the 150-character cut. |
| The node's clock jumps forward by a day | At most one liveness commit (older than 6 h); backward: none until 6 h have really passed. |
| Two machines publish in the same second | Different files; `pull --rebase` never conflicts on content (§4). |
| `zyggy discover` while the node service is mid-publish | The write lock serialises them; the loser reports `Busy` and exits 1 (CLI) or retries next interval (node). |
| Job names a path that is exactly a `devRoots` entry | Accepted when it passes the three checks (a root may be a project for path-form jobs; by name it is not published). |
| Job names a registry name that is also an existing relative folder of the checkout | Names never touch the file system before resolution; the snapshot is the only lookup. |
| Snapshot entry from a `names`-only scan | `TryResolve` false → `unknown name`. |
| The owner renames a project folder between two scans while a job is claimed | The run already holds the resolved path; the next scan publishes the new name. |

---

## Out of Scope

- `agents.md` refresh / `zyggy dream agents` (deferred, OQ-3); `list_nodes` (21); the `delegate` and `discover` skills, `zyggy discover --link`, `PROTOCOL.md` content (15); the "Node silent" alert (22); worktrees and `zyggy run` (16 — a CLI run resolves names by rescanning or reading its own snapshot; 16 decides).
- Signing or auditing the registry file (`verify` keeps skipping it); a `node.json` list, exclusion or alias of projects; recursive scanning or a depth knob; a `/health` discovery field; reading the registry of another tenant or machine on a node.
- Policy-governed `dev_roots`, signed `policy.yaml`, tighten-only merge (§14, MVP-scope decision); DLP rules for registry content (19 — the `names` detail is 14's lever for 20).
- Publishing anything a scan did not read: no `CLAUDE.md` body, no README, no file lists, no git remotes or commit data beyond `default_branch`.
- The work laptop's configuration itself (20 sets `detail: names`); Central's `devRoots` for its own path-form jobs (17's configuration).
- Any change to the envelope, the state machine, `BusPaths`, `RunFailureReason` (no new value: `unknown_project` carries two new check words in the body only) or `NodeHealth`.

---

## Open Questions — all resolved 2026-11-07

The owner approved the spec at the 🛑 spec gate on 2026-11-07 ("approve"), taking the recommendation of every question. Zero Open Questions remain; the text below is kept as the record of each decision.

- [x] **OQ-1 — The registry file is unsigned, and its format changes (A14-1).** Found: §4 opens with "every file HMAC-signed" and shows the registry example without `schema`, `health`, with a free-text `work_hours` and human key order; 08 deferred the signing decision to 14 and `BusAuditor` already skips `registry/*.yaml` as "unsigned in v1". Why it matters: §4 is normative and `PROTOCOL.md` (15) must match it verbatim; the format is read by 15, 21 and 22. Options: (a) unsigned plain YAML, routing on the node from its own scan, Central treats the file as a hint — this spec; (b) sign it with a second canonical form and golden files, verify it in `verify`, `delegate` and `list_nodes`, re-sign on every liveness refresh. Recommendation: **(a)** — nothing executes from the bus copy (AC-26 proves it), so (b) adds a canonical form, four verifiers and a key dependency for a tampering whose worst case is a job the node answers `unknown_project`. Approve A14-1 wording (with the `schema: 1`, `health`, structured `work_hours`, ordinal keys, derived names, `detail: names`). **Resolved: (a), approved 2026-11-07.**
- [x] **OQ-2 — Resolution from the node's own scan, not from `registry/<me>.yaml` (A14-3).** Found: §6 step 1 says "resolved against `registry/<me>.yaml`"; the file in the checkout is unsigned and any push-capable machine or a diverged checkout can rewrite it (brief's risk "an unsigned file steering execution"). Why it matters: a shared contract (§6 pre-flight input) and the §8 boundary. Options: (a) in-memory `RegistrySnapshot` from the node's own scan, plus the `devRoots` guard — this spec; (b) read the bus copy and verify every resolved path lies under `devRoots` and exists (the tampered path could still be any folder under a root). Recommendation: **(a)**; approve A14-3 wording. **Resolved: (a), approved 2026-11-07.**
- [x] **OQ-3 — `zyggy dream agents` leaves 14 (A14-4).** Found: §7 step 5 and the roadmap Goal put it in 14; `agents.md` in the memory repository is an owner-stated file (`tests/golden/digest/27/memory/acme/alice/agents.md`), the filing prompt forbids the model to target it, and `delegate` (15) reads `registry/*.yaml` directly. Why it matters: it changes the roadmap entry's Goal and a §7 step. Options: (a) defer — reserve the step, let 21's `list_nodes` serve the summary and revisit if a session ever needs the fleet in its SessionStart digest; (b) build it now as a generated block in `agents.md` under a marker, committed by the dream's committer, with rules for coexisting with `[stated]` lines; (c) move it to 21 as a sibling of `list_nodes`. Recommendation: **(a)** (with (c) as the place to revisit) — no gate needs it and the memory-model design is not 14's round trip. Approve A14-4 and the roadmap Goal/Scope trim (project-manager). **Resolved: (a), approved 2026-11-07.**
