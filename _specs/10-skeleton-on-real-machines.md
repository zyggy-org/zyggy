# Spec: 10 — Walking skeleton on real machines: the node under systemd on Central and as a logon task on the home laptop, real `claude -p`

> Founding-spec sections: §10 Configuration and deployment (`node.json`; Central: systemd units, "Secrets via `LoadCredential=` in the units", Claude Code auth; Laptops: `install.ps1 -Tenant <org> -Machine <name>`, the Windows service "running as the current user (needed for Claude Code auth and Credential Manager access)", `Delayed Start`, the optional remote-control task; Upgrade path: stop / replace / start, `--version` and `/health` report the version, `/opt/zyggy/<version>/` pinned in `instance/zyggy.json`); §5 Transport (Git operations: "the code never handles credentials itself"; Credentials per machine table; "Revoking one machine never affects the others"; `main` force-push/deletion blocked; `add-tenant.md`); §8 Secrets table (HMAC keys row as amended by A9-2; the P0 file-store paragraph, O20), Isolation ("Central runs as an unprivileged user", "Nodes run as the interactive user's account", "`--permission-mode auto` is used everywhere; `--dangerously-skip-permissions` is never used", the `Read` deny rule pattern of O32); §11 Health endpoint (`version`, `tenant`, …), Runbooks (`bus-conflict.md`, `rotate-hmac.md` as amended by A9-1, `restore-central.md`, `revoke-machine.md`, `add-tenant.md`); §12 Definition of done (gate scenario, runbook entry per new failure mode, "`node.json` schema and this spec updated where behaviour changed"); §13 Q3 (Central hosting and cost), Q4 (remote-control wrapper — verified in 02, `_plans/decisions/0001-transport-and-vm.md`), decisions (HMAC-signed envelopes with per-machine credentials; .NET 10 single-file binaries; MVP scope); §14 as narrowed (`init --join`, Central image, `diagnose` not built). Roadmap entry: `_plans/ROADMAP.md` #10 (lines 301–313: Goal, Scope, the five items inherited from 09 (a)–(e), "What 09 leaves to 10", the hand-off brief); roadmap rule R1 (tools are .NET; shell only for the unit launcher and `install.ps1` with the reason stated). Project inputs honoured: `_plans/decisions/0001-transport-and-vm.md` (per-machine auth path: deploy key over SSH on Central, Git Credential Manager on both laptops; F1 branch protection unavailable, risk accepted "to be revisited before real jobs … travel over the bus"; F2 checkout lock; F4 `core.autocrlf`; `/opt/zyggy/<version>/` + `/usr/local/bin/zyggy` on Central), `_specs/07-node-service-loop.md` (hosting: `UseSystemd()` sends `READY=1`; exit codes 0 / 3 / 6 / 1; "systemd `Restart=`/`RestartSec=` and Windows recovery settings are 10's unit files"; health port 4711 never changes), `_specs/08-cli-submit-status-verify.md` (`zyggy status` green / amber / red / down, exits 0 / 1 / 6 / 6; the runbook directory → 10), `_specs/09-os-secret-stores.md` (ex-Q6 `LoadCredential=` → 10; ex-Q7 service-context Credential Manager check → 10; `rotate-hmac.md` draft; the "retire the spike file store" paragraph; the `secret set` note "the credentials directory still provides …"), `memory/repo-public-hygiene.md` (this repository and `zyggy-core` are public; operational runbooks with real identifiers live in the private instance repository), `memory/owner-facing-wording.md` (PowerShell 5.1 snippets; "signing key", never "hash").
>
> Status: **approved by the owner on 2026-10-26 — zero Open Questions; planner-ready.** Q1 reworked by the owner (no Windows service: the owner is not an administrator on every laptop — a per-user scheduled task at logon is the Windows mechanism), Q2 rejected as recommended (both Linux layers stay usable; `LoadCredential=` ships as a documented option), Q3–Q7 accepted as recommended. Each decision is folded into the tables below and marked *Owner 2026-10-26*; the "Decisions taken at the spec gate" table records them; the founding-spec wording the orchestrating session applies is under "Founding-spec amendments (approved wording)". Nothing in this file edits the founding spec.

## Current state (verified against the repo on 2026-10-26, `main` at 085f129 after PR #20)

| Roadmap item | State today | Decision for 10 |
|---|---|---|
| The service binary | `zyggy-node` (single-file, win-x64 103.7 MB / linux-x64 101.2 MB) with `AddWindowsService()` + `AddSystemd()` already registered in `NodeHost.Build` (both no-ops outside their manager); `--config <path>`, `ZYGGY_NODE_JSON`, else `%LOCALAPPDATA%\zyggy\node.json` / `~/.config/zyggy/node.json`; exit 0 / 3 / 6 / 1; Serilog file under `<nodeDir>/logs/` plus console (journald). | No change to the host. On Windows the binary runs as a plain console process inside a per-user scheduled task; the Windows-service lifetime stays registered and idle. 10 adds the unit file, the install script, the runbooks and the owner-executed proof. |
| CI artefacts | `ci.yml` publishes `zyggy` and `zyggy-node` per RID into `artifacts/<rid>/` with `SHA256SUMS` (sha256sum format) and smoke-runs `--version`; release tags build both runners; Central downloads release assets itself and checks `sha256sum -c` (0001). | The deploy assets (`deploy/`) ship inside the same artefact and are listed in `SHA256SUMS`. |
| Central layout | `zyggy` at `/opt/zyggy/<version>/zyggy` root:root 755, `/usr/local/bin/zyggy` symlink, version pinned in the instance repository's `instance/zyggy.json` and checked by `zyggy dream` / the m365 runs (`VersionPin`, `BinaryPin`); user `zyggy` without sudo, home outside `/home`; `claude` under `~/.local/bin/`; units `claude-remote`, `zyggy-dream.*`, `zyggy-m365-mcp`, the brief timer — all hand-written in the private runbooks. | `zyggy-node` lands next to `zyggy` under `/opt/zyggy/<version>/` with `/usr/local/bin/zyggy-node`; the unit is the sixth hand-installed unit, from a file shipped with the release; no pin check in the node (below). |
| Secrets | 09: Credential Manager on Windows (`zyggy/<tenant>/<name>`), `LinuxSecretStore` = `$CREDENTIALS_DIRECTORY/zyggy.<tenant>.<name with / → .>` then the 0600 file layer; `node.json` `secrets.store: os` default; the node refuses with one of two lines (key absent / store unavailable); the home laptop still holds the spike keys in `%LOCALAPPDATA%\zyggy\secrets\` (`secrets.store: file`). | Central: the file layer by default, `LoadCredential=` as a shipped drop-in option (Q2, owner). Laptop: the logon task runs in the owner's interactive session, so Credential Manager is the same vault `zyggy secret set` wrote to (Q1, owner). The first `rotate-hmac.md` execution retires the spike store with the compromise variant (Q3). |
| Transport | 0001: Central polls with a Contents-read PAT and pushes over SSH with a write deploy key; the home laptop polls with its own Contents read+write PAT and pushes over HTTPS through Git Credential Manager; `.gitattributes` `* -text` in the bus; `core.autocrlf false` on the Windows checkout; no ruleset on `main` (F1, risk accepted 2026-10-15). | Verified, not rebuilt (AC-14); F1 accepted until the hardening deliverable, date in 0001 (Q5). |
| Gate | `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` parts 1 and 1b prove submit → real host → fake claude → report → `status` green → `verify` clean against the bare repository. | The physical half is owner-executed against the two real machines; 10 adds no gate code. |
| Runbooks | Drafted as README tables: Core README "Troubleshooting — Bus" (`bus-conflict.md`), "— Node", "— Secrets", "Runbook draft: rotate-hmac.md"; Cli README "Per-machine setup for the transport proof" (foreground `probe listen`, no unit). | The runbook directory `zyggy-core/runbooks/` is created with placeholder-only texts; the owner's evidence goes to one private page in the instance repository (Q4). |
| The second laptop | The work laptop (deliverable 20) is a machine on which **the owner is not an administrator**: no service can be installed there, ever. | The Windows mechanism chosen here must already work unelevated, so 20 reuses `install.ps1` unchanged. |
| `Directory.Packages.props` | 24 rows. | Unchanged: 24 rows. |

---

## User Story

**As** the owner of tenant `acme` with a Central VM and a home laptop (and, later, a work laptop on which I am not an administrator), **I want** `zyggy-node` to run unattended on both machines — a systemd unit under the unprivileged `zyggy` user on Central, a per-user scheduled task in my own logon session on the laptop, installed without administrator rights — reading their keys from the operating system's own stores, so that `zyggy submit --to home-laptop --project <path>` typed on Central makes the real `claude -p` run in one of my projects on the laptop and the signed report shows up in `zyggy status` on Central, **so that** the P1 walking skeleton exists on real hardware, every later deliverable (memory, dream, discovery, the work laptop) installs onto a node that is already proven, and the five items 09 handed over — the Credential Manager check under the unattended node, the `LoadCredential=` choice, the first key rotation, the `Read` deny rule, the runbook directory — are each decided or executed and recorded.

---

## Acceptance Criteria

Tenant `acme`, machines `central` and `home-laptop`, keys `acme/1` (the spike key) and `acme/2` (the first rotation). Kinds: **U** = unit (`tests/Zyggy.Core.Tests` or `tests/Zyggy.Integration`, text and parser checks on the deploy assets, no service manager, no Task Scheduler), **I** = integration (bare repository, in-process host, substitutes; never the real `claude`, never GitHub, never a real service manager, Task Scheduler or OS store), **D** = document, **CI** = pipeline, **O** = **owner-executed on the real machine**, evidence (command, redacted output, UTC time) recorded in the plan's final HUMAN GATE and in the private evidence page `instance/runbooks/10-walking-skeleton.md` of the owner's instance repository (Q4). An **O** row can be checked only by the owner; the planner lists it in the checklist of the last gate, never as a step the executor can tick.

| # | Kind | Given | When | Then |
|---|------|-------|------|------|
| AC-1 | U | `deploy/systemd/zyggy-node.service` and `deploy/systemd/zyggy-node.service.d/credentials.conf.example` as shipped | The deploy-asset test reads them | The unit is exactly the text under Contracts → "systemd unit": `Type=notify`, `User=zyggy`, `WorkingDirectory=~`, `ExecStart=/usr/local/bin/zyggy-node`, `Restart=on-failure`, `RestartPreventExitStatus=3 6`, `TimeoutStopSec` ≥ the host's 20 s shutdown timeout; the unit itself carries **no** `LoadCredential=`/`SetCredential=` line and no `Environment=` naming `pat`, `token`, `hmac`, `secret` or `password`; the drop-in example carries exactly two `LoadCredential=` lines whose ids equal `LinuxSecretStore.CredentialId(acme, hmac/2)` and `(acme, github-pat)` (`zyggy.acme.hmac.2`, `zyggy.acme.github-pat`) and whose source paths use the `<zyggy-home>` placeholder; neither file holds an absolute home path (`/home/`, `/srv/`) or an owner identifier (`SecretSweep` and the placeholder rule); LF endings. The test is a golden comparison of each file against itself plus the invariant list, so a hand edit that drops a guard fails the build. |
| AC-2 | U | `deploy/windows/install.ps1` as shipped | The deploy-asset test reads it | No `#Requires -RunAsAdministrator`; never calls `New-Service`, `sc.exe`, `Set-Service`, never names `LocalSystem`, `LocalService`, `NetworkService`, `NT AUTHORITY`, never takes or prompts for a password (`Get-Credential`, `PSCredential`, `-Password` absent); registers the task with `-LogonType Interactive` and `-RunLevel Limited`; the task action runs the node hidden with `--config "<node.json path>"` and `GCM_INTERACTIVE=never` set in the node's own process only; writes nothing under `%ProgramFiles%`, `HKLM` or `C:\Windows`; contains no `Invoke-WebRequest`/`curl` (the release is downloaded by the owner, checked against `SHA256SUMS`); `SecretSweep` clean; LF endings (`.gitattributes`; PowerShell 5.1 reads both). |
| AC-3 | U | The `node.json` template embedded in `install.ps1` (between the markers `# node.json-template-begin` / `-end`), rendered for tenant `acme`, machine `home-laptop`, bus path `C:/agents/bus` | `NodeConfigurationReader.Read` | Accepted: `tenant acme`, `machine home-laptop`, `signWith` from the `-SignWith` parameter, `bus.remote https://github.com/zyggy-org/zyggy-bus.git`, `secrets.store os`, `workHours` present (Europe/Brussels, Mon–Fri, 08:00–19:00), `claude.path` absolute, `devRoots` empty; no key the reader refuses as secret-looking; `secrets.root` absent. |
| AC-4 | U | `deploy/` and the runbook texts in this repository | Hygiene sweep (extends 09 AC-13) | No real host name, user name other than `zyggy`, VM admin name, token id, fingerprint, mailbox or employer name; machines are `central` / `home-laptop`, tenant `acme`, home `<zyggy-home>`; `--dangerously-skip-permissions` appears nowhere in `src/`, `deploy/` or the runbooks except in the sentence that forbids it. |
| AC-5 | CI | Clean clone, both runners | `ci.yml` | `deploy/systemd/zyggy-node.service`, `deploy/systemd/zyggy-node.service.d/credentials.conf.example`, `deploy/windows/install.ps1` and `deploy/README.md` are copied into `artifacts/<rid>/deploy/` and listed in `SHA256SUMS` (relative names, sha256sum format); build, format, test and the two single-file publishes stay green; `Directory.Packages.props` has exactly 24 rows. |
| AC-6 | D | `zyggy-core/runbooks/` (Q4) | Review | Six files exist with the outlines under Contracts → "Runbooks": `install-node.md` (new; the two-OS install, the logon task, the credentials-directory option and when to choose it, the URL/port rows, the two refusal lines the node can print and what to do), `restore-central.md` (completed for the node unit), `revoke-machine.md`, `add-tenant.md` (with the 2026-10-13 first execution and its F1 deviation recorded, placeholders only), `rotate-hmac.md` (09's draft verbatim plus the "Executions" record), `bus-conflict.md` (the Core README "Troubleshooting — Bus" rows). Each names the machine role that runs every command (`[central/zyggy]`, `[central/<vm-admin>]`, `[home-laptop/user PowerShell]` — there is no elevated shell on a laptop). The Core README's "Runbook draft: rotate-hmac.md" section becomes a pointer; the Cli README's "Per-machine setup for the transport proof" is rewritten as "Per-machine setup" (the `probe listen` foreground step removed; the logon task named); the Node README's "installed by deliverable 10" sentences name the unit and the task; `deploy/README.md` says what the assets are and that the runbooks hold the procedure. |
| AC-7 | D | `_specs/00 …` §8, §10, §11 | After this gate | The approved replacement wording (A10-1..A10-5 below) is pasted by the orchestrating session and listed in the journal (never by the analyst or the planner); the `node.json` schema table in the Cli README needs no new key (10 adds none). |
| AC-8 | O | Central, release `v<x>` of this deliverable downloaded by the VM and checked with `sha256sum -c` | `[central/<vm-admin>]` installs `/opt/zyggy/<x>/zyggy-node` (root:root 755) and `/usr/local/bin/zyggy-node`, copies `zyggy-node.service` to `/etc/systemd/system/` (no drop-in for the first run), `daemon-reload`, `enable --now` | `systemctl is-active zyggy-node` = `active`, `systemctl show -p NRestarts` = 0 after 10 minutes, `journalctl -u zyggy-node` shows the host's start lines and no `refusing to start`; `[central/zyggy] zyggy status` prints `health: green`, exit 0, with `zyggy-node <x>` and tenant `acme`; `curl -s localhost:4711/health` shows `"version":"<x>"`, `"tenant":"acme"`, `"machine":"central"`; `ss -tlnp` shows 4711 on `127.0.0.1` only; `zyggy secret list` shows `hmac/<n>` with store `file`. |
| AC-9 | O | Central's `node.json` (`~/.config/zyggy/node.json` of `zyggy`) | `grep -niE 'pat|token|hmac|secret|password' node.json` | Only the `"secrets": { "store": "os" }` section matches; `signWith`, `bus.remote` (the SSH alias form), `claude.path` (absolute), no `workHours` (always inside), `secrets.store: os`. A copy with the local path values replaced by `<…>` is the evidence. |
| AC-10 | O | Home laptop, the release extracted to a folder, the bus checkout at `C:\agents\bus` from 04, Git for Windows, Claude Code installed and logged in as the owner; **a plain, unelevated user PowerShell** | `[home-laptop/user PowerShell] .\install.ps1 -Tenant acme -Machine home-laptop -SignWith acme/1` | No UAC prompt at any point; binaries under `%LOCALAPPDATA%\Programs\Zyggy\`, that folder on the **user** PATH, `node.json` left untouched when present (printed) or written from the template; pre-flight passes (`git --version`, the checkout holds `tenants\acme\`, `zyggy secret list` shows `hmac/1 … signs`, `claude.path` exists); the scheduled task `zyggy-node` exists for the owner's account with trigger "at log on of <user>" delayed 30 s, logon type Interactive, run level Limited, hidden, one instance, no time limit, restart on failure 3 × 1 min; the task is started by the script and `Get-ScheduledTask zyggy-node` shows `Running`; no console window stays visible; `zyggy status` prints `health: green`, exit 0; `/health` shows the version, `acme`, `home-laptop`. |
| AC-11 | O | AC-10 done; the laptop rebooted once and the owner logged on | `Get-ScheduledTask zyggy-node`; `zyggy status` | `Running` without a manual start within 30 s + one poll of the logon; `health: green` within 3 × the idle interval; the Serilog file under `%LOCALAPPDATA%\zyggy\logs\` has one start block per logon, no `refusing to start`; while the owner is logged off nothing runs (expected: §8 ties the node to the interactive session). |
| AC-12 | O | **Inherited (a) — Credential Manager under the unattended node.** The task of AC-10 running in the owner's interactive session, the keys in Credential Manager (after AC-15 step 0), no `secrets.store: file` | The round trip AC-13 | The report on `main` is signed by `home-laptop` with `key_id: acme/2` → `CredRead` worked from the task. By construction the task holds the owner's interactive token (the same logon session `zyggy secret set` used), so `ERROR_NO_SUCH_LOGON_SESSION` (1312) — a session-0 / service-logon failure — is not expected and the DPAPI file store of 09 ex-Q7 is **no longer a named fallback**. Should 1312 appear anyway, it is recorded as a finding and investigated as a `bugfix` item before any new store is considered. |
| AC-13 | O | **The physical half of the P1 gate.** Both nodes green; a project of the owner's choice on the laptop (a git repository with `.claude/`, inside `devRoots` or not — 14 enforces roots); the body a read-only task (for example "List the top-level folders of this repository and say in two sentences what it is") | `[central/zyggy] zyggy submit --to home-laptop --project <C:/path/to/project> "<body>"` | `submitted <id> to home-laptop (…)` exit 0; within two poll intervals the laptop claims it (`claim` commit by `zyggy (home-laptop)`), the real `claude -p` runs with the defaults (`claude.maxTurns`, `defaultTimeoutMinutes`, allowed tools `Read, Grep, Glob`, `--permission-mode auto`, never `--dangerously-skip-permissions`); a `report` commit with `status: done`, `cost_usd`, `num_turns`, `model` filled; `[central/zyggy] zyggy status --id <id>` prints `state reported by home-laptop` and the body; the transcript exists at `%LOCALAPPDATA%\zyggy\runs\acme\<id>.jsonl` on the laptop; `zyggy verify` exit 0 on both machines; `zyggy status` still green on both. The ids, the UTC times of the three commits and the sender-side latency are the evidence (no body text needed). |
| AC-14 | O | **Transport, as 0001 recorded** | GitHub settings pages (no values recorded) | Central: one fine-grained PAT, resource `zyggy-org`, repository `zyggy-bus` only, Contents **read**; write deploy key `central` on `zyggy-bus` only. Home laptop: one fine-grained PAT, `zyggy-bus` only, Contents read and write, held by Git Credential Manager and by `zyggy/acme/github-pat`; no deploy key. No credential of either machine reaches any other repository. Expiry dates noted in the private evidence page. F1 (no ruleset on `main`): the acceptance is extended to the hardening deliverable (20) with today's date written into 0001's F1 row (Q5). |
| AC-15 | O | **Inherited (c) — first execution of `rotate-hmac.md`, compromise variant (Q3)** | The runbook, in order | Step 0 on the laptop (user shell): `hmac/1` and `github-pat` carried from the spike files into Credential Manager (`zyggy secret list` shows store `credential-manager`), `"store": "file"` gone from `node.json`, `%LOCALAPPDATA%\zyggy\secrets\` deleted; steps 1–3: `acme/2` generated on Central (`--generate --show`, the hex carried over a channel the owner trusts), entered on the laptop at the hidden prompt, fingerprints equal on both machines, `signWith: acme/2` on both, the unit restarted and the task restarted; step 5 (compromise variant): `zyggy secret remove hmac/1` on Central and on the laptop, then **one owner commit on `main`** removing the probe-era files of `tenants/acme/` (message `acme cleanup P0 probes`; history keeps them; `verify` walks the working tree); AC-13 is run **after** this and its report carries `key_id: acme/2`; `zyggy verify` exit 0 on both machines at the end. The work laptop is not touched: its `acme/1` is now useless and it receives `acme/2` in 20. The "Executions" row of `rotate-hmac.md` records the variant and why. |
| AC-16 | O | **Inherited (d) — the `Read` deny rule on Central** | `[central/zyggy]` adds to `~/.claude/settings.json` → `permissions.deny`: `Read(~/.local/share/zyggy/secrets/**)` and `Edit(~/.local/share/zyggy/secrets/**)` | `claude doctor` reports no rejected entry; an interactive session asked to read `~/.local/share/zyggy/secrets/acme/hmac/2` is refused by the permission system (the refusal line is the evidence, never the content); the rule is user-scoped so it applies to every project and to the node's `claude -p` children; 15's template later ships the same rule. |
| AC-17 | O | **Restart and refusal behaviour** | Central: `systemctl restart zyggy-node`; then with `signWith` temporarily set to `acme/9`: `systemctl restart zyggy-node`; restore. Laptop: `Stop-ScheduledTask zyggy-node`; `zyggy status`; `Start-ScheduledTask zyggy-node`; then with `signWith` temporarily `acme/9`: `Start-ScheduledTask`; restore | Central: green again within two polls; the refusal shows `Active: failed (Result: exit-code)`, main process exited `status=6`, exactly one `refusing to start: signing key acme/9 is not in the file …` line, `NRestarts` unchanged (no loop); after the restore, `active`. Laptop: `health: down — no node service answering …` exit 6 while stopped, green after the start; the refusal shows Last Run Result `0x6`, Task Scheduler's restart-on-failure re-runs it at most three times a minute apart (at most four `refusing to start` lines in the log), then the task is `Ready` (not running) and `status` is down; after the restore and `Start-ScheduledTask`, green. The claim-resume behaviour of 07 after a `Stop-ScheduledTask` during a run (a kill, not a graceful stop) is observed once: the claim stays in `jobs/claimed/`, the next start resumes it. |
| AC-18 | O | **Inherited (b) and (e), §13 Q3** | At the gate | (b) recorded: Central runs on the file layer; the `LoadCredential=` drop-in is shipped, documented in `install-node.md` and **not** enabled for the gate (the owner may enable it afterwards per the runbook — the overlap note of `zyggy secret set` then applies); (e) `rotate-hmac.md` filed in `zyggy-core/runbooks/` with its first-execution record; the Central cost line of the month (Cost Management forecast, as 0002 §33 VM-C4) and the laptop's run cost from the report's `cost_usd` noted — informational under the Max subscription. |
| AC-19 | I | `Gates/P1_WalkingSkeleton.cs`, the Node integration tests, the 09 OS-store tests | Full test run on both runners | Unchanged results; 10 adds no test that needs a service manager, Task Scheduler, a real store, GitHub or the real `claude`. |

---

## Decision Table

| Founding-spec / brief item | Verdict | Target | Justification |
|----------------------------|---------|--------|---------------|
| §10 Central: `zyggy-node.service` with `Restart=always` | Keep + Reshape | `deploy/systemd/zyggy-node.service` — `Type=notify`, `Restart=on-failure`, `RestartPreventExitStatus=3 6`, `RestartSec=30` | `Restart=always` would loop a start-up refusal (a missing key, a bad `node.json`, a rebase in progress) every few seconds and write the "refusing to start" line each time, against §11 "log once per state change"; 07 fixed the exit codes exactly so a unit can tell "needs a human" (3, 6) from "crashed" (1, signal). `Type=notify` because 07's host already sends `READY=1` through `UseSystemd()`. **Owner 2026-10-26: accepted (Q6, A10-1).** |
| Generating the unit file / the Windows registration from Zyggy (brief: "prefer generating … golden-file tested, but challenge whether a verb is worth it") | Defer (not built) | Static files in `deploy/`, invariants tested (AC-1/2/3) | The unit has no per-machine value: `User=zyggy` is the §10 convention, `WorkingDirectory=~` and systemd's `$HOME` make `~/.config/zyggy/node.json` resolve, the binary is a fixed symlink. A generator would own a template, a verb, a test that compares the template with itself, and a second way to produce the same 25 lines next to the five hand-written Central units. The Windows side needs a real Task Scheduler to prove anything, which no test may touch; a `zyggy-node install` verb would therefore be untestable code. The real test is the owner's AC-8/AC-10. |
| §10 "Secrets via `LoadCredential=` in the units" for the bus secrets (inherited b) | Keep as an option + Reshape | The unit ships without `LoadCredential=`; `deploy/systemd/zyggy-node.service.d/credentials.conf.example` ships the two lines; the file layer is the default | **Owner 2026-10-26 (Q2, recommendation rejected): both layers stay usable — "why not use the bus secrets through the credentials directory".** Analyst's finding kept as the documentation: on Central the same user must sign **outside** the unit (`zyggy submit`, `verify`, `secret list` run as `zyggy` from a shell and never see `$CREDENTIALS_DIRECTORY`), so the file layer serves every shell command whatever the unit does; the credentials directory wins inside the unit (09), and `zyggy secret set` prints `note: the credentials directory still provides <name> to the node` when both hold a name. `install-node.md` says when to choose which (Contracts → Runbooks). A10-2. |
| §10 Laptops: "registers `AgentBus.Node` as a Windows service running as the current user … sets `Delayed Start`" | Reshape | **A per-user scheduled task `zyggy-node` at the user's logon** (interactive logon type, limited run level, hidden, 30 s delay, one instance, no time limit, restart on failure 3 × 1 min) — **no Windows service at all** | **Owner 2026-10-26 (Q1 reworked): the owner is not an administrator on the work laptop; the Windows mechanism must need no elevation.** One mechanism, not two: a service needs administrator rights, the account's password, the "Log on as a service" right and runs in session 0 where the user's Credential Manager is not guaranteed (1312); a logon task needs none of those, runs in the very session that holds the Claude Code login, the browser and the vault (§8 "Nodes run as the interactive user's account (needed for Claude Code auth and the browser session)"), and is the only mechanism that works on both laptops — so `install.ps1`, the runbook, the ACs and 20 have one path. Costs accepted: the node runs only while the owner is logged on (Central is the always-on machine; a laptop's `claude` and browser need that session anyway); a stop is a kill, not a graceful stop (07's claim resume covers it); a PowerShell window flashes for an instant at logon (the hidden host). Keeping the service "where the owner has admin" was rejected: two registrations to document and test for one owner, and the home laptop gains nothing from session 0. A10-3. |
| Windows restart story | Reshape | Task Scheduler "restart on failure" 3 × 1 min; `MultipleInstances=IgnoreNew`; `StartWhenAvailable` | Replaces the SCM recovery actions. A refusal (exit 3/6) re-runs at most three times a minute apart, then the task rests and `zyggy status` says down — bounded, unlike `Restart=always`, and at most four log lines. A crash is retried the same way; a logon always starts it afresh. |
| `install.ps1` | Keep (shell, R1 reason stated) + Reshape (unelevated, parameters) | `deploy/windows/install.ps1` — PowerShell 5.1; **no elevation**; `-Tenant`, `-Machine`, `-SignWith`, `-BusLocalPath` (default `C:\agents\bus`), `-Source` (default the script's folder), `-NodeJson` (default `%LOCALAPPDATA%\zyggy\node.json`), `-Uninstall` | R1 reason: the installer runs before any Zyggy binary is in place, copies files, edits the user's PATH and registers a Task Scheduler task — things a test can never exercise and the owner wants to read line by line. Every step is per-user (`%LOCALAPPDATA%\Programs\Zyggy`, HKCU PATH, the user's own task); a step that is refused by the system (a policy forbidding per-user tasks) stops the script with the reason — it never asks for elevation (owner's rule). It writes `node.json` only when absent (the laptop has one from 04) and runs the pre-flight that leads the owner through the node's two refusal states (09's hand-over). |
| §10 "`%ProgramFiles%\AgentBus`, adds to PATH, creates `C:\agents\{bus,node,agent-core}`" | Reshape | `%LOCALAPPDATA%\Programs\Zyggy\` (the per-user programs folder; no elevation); **user** PATH; `C:\agents\bus` only | `%ProgramFiles%` needs elevation; `node` is `%LOCALAPPDATA%\zyggy` since 06/07 (`nodeDir` default); `agent-core` is 15's checkout. A10-3. |
| 09 ex-Q7 service-context Credential Manager check (inherited a) and its DPAPI fallback | Keep (prove) + Defer (fallback dropped) | AC-12 | The 1312 risk was a session-0 risk; a logon task runs with the interactive token, the same logon session `zyggy secret set` used, so the check is expected to pass and is still recorded. The DPAPI-protected file store is no longer a named fallback (its trigger no longer exists); a surprise 1312 is a finding, not a store. |
| Hidden console for the task | Reshape (documented PowerShell host) | Action = `powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "…"` | `zyggy-node` is a console binary; a task with the interactive logon type would show its window. `-WindowStyle Hidden` is a documented parameter; `conhost.exe --headless` is not documented and a `WinExe` build would silence the terminal use of 07. The command sets `GCM_INTERACTIVE=never` in that process only and ends with `exit $LASTEXITCODE` so Task Scheduler sees the node's own exit code. |
| §10 "Home laptop additionally gets a scheduled task to start `claude remote-control --name home` at logon (optional)" | Defer | — | Optional in §10, no gate needs it; the owner's remote-control sessions run on Central (02). **Owner 2026-10-26: dropped (Q6, A10-3).** |
| §10 Work laptop line (`dlp.enabled`, `devRoots`, mail set-up) | Out | — | Deliverable 20 — which now inherits an installer that needs no administrator. |
| §10 Claude Code side (`.mcp.json`, the `CLAUDE.md` line, `discover --link`, `autoMemoryDirectory`) | Out | — | 11, 14, 15, 27 (done). |
| §10 Upgrade path: "download the release, stop service, replace, start; `--version` and the health endpoint report the version" | Keep (runbook) | `install-node.md` → "Upgrade" (both OSes); `/opt/zyggy/<version>/` + symlink switch on Central; `install.ps1` re-run on the laptop (it stops the task, replaces, starts) | Already the practice of 04/28 on Central; `/health.version` and the `zyggy status` version-skew amber rule (08) show a half-done upgrade. |
| §10 "`instance/zyggy.json`" pin applied to `zyggy-node` | Defer (no pin check in the node) | — | The pin guards the dream and the m365 runs that act on the owner's data; the node's binary is checked by `sha256sum -c` at install, its version is in `/health`, and `zyggy status` flags node/CLI skew. A pin check in the node would be new Core code for one owner; revisit with 26. |
| §10 "`selfUpdate: true`, upgrade by a job of project `agentbus-self`" | Defer | — | §14-grade automation; 26. |
| §5 Credentials per machine; "Revoking one machine never affects the others" | Keep (verify) | AC-14; `revoke-machine.md` | Built by hand in 04; 10 confirms the scope of each credential and writes the revocation procedure. |
| §5 "`main` has force-pushes and branch deletion blocked" (F1: unavailable on the free plan) | Keep as a rule; acceptance extended | 0001 F1 row updated with today's date and "until 20" | **Owner 2026-10-26 (Q5): accepted until the hardening deliverable.** In P1 the bus carries the owner's own projects' job envelopes and read-only run summaries; the write credentials are the owner's two machines; the history is mirrored on three clones; the bus code has no force path. |
| §5 Direct fast path (Tailscale `ssh home-laptop zyggy run`) | Out | — | 16. |
| §8 Isolation: Central unprivileged; `--permission-mode auto`; never `--dangerously-skip-permissions` | Keep | AC-4, AC-8 (`User=zyggy`, `NoNewPrivileges=yes`), AC-13 | Built in 06 (the runner) and 02 (the user); 10 proves them on the real machines. |
| §8 / 09: the `Read` deny rule for Central's secrets directory (inherited d) | Keep | AC-16, user-scoped `~/.claude/settings.json` on Central | 09's "Who can read a secret" states the mitigation honestly; user scope is the one place that reaches every project and the node's own `claude -p` children; the rule is `deny`, which applies without the trust step. 15 ships the same text in the template. **Owner 2026-10-26: accepted (Q7, A10-4).** |
| §8 O20 "keys rotated and the file store retired on Windows when the OS stores land" (inherited c) | Keep (compromise variant) | AC-15 | **Owner 2026-10-26 (Q3): the compromise variant with the cleanup commit.** The only moment the real retirement of the spike keys costs nothing is now, before a real job exists; a retained key would still verify new envelopes (09 ex-Q1). |
| §11 Runbooks `bus-conflict.md`, `rotate-hmac.md`, `restore-central.md`, `revoke-machine.md`, `add-tenant.md` | Keep (filed) | `zyggy-core/runbooks/` with placeholders; the owner's evidence in the private instance repository | **Owner 2026-10-26 (Q4): accepted.** §11 names them; their drafts exist in the READMEs; 08 forwarded "the runbook directory" to 10; the public repositories hold only placeholders (hygiene rule). |
| `install-node.md` (roadmap 07: "10 … owns the unit files, `install.ps1` and the URL/port runbook") | Keep (new file) | Contracts → "Runbooks" | The install procedure is §10 text today; a runbook with the owner-role prefixes, the two refusal states and the credentials-directory choice is what 09 asked 10 for. **Owner 2026-10-26: accepted (Q7, A10-5).** |
| §11 Health endpoint `version`, `tenant` | Keep (prove) | AC-8, AC-10 | The roadmap's definition of done names it. |
| §12 "gate scenario scripted under `Gates/`" | Keep (exists) | AC-19 | Parts 1 and 1b are the automated half; the physical half cannot be a test by the project's own rule (no real `claude`, no GitHub, no service manager). |
| §13 Q4 remote-control wrapper "verify in the installed version during P1" | Out (done) | — | 0001 VM-C2: resumed on every restart checked. |
| §13 Q3 cost | Keep (record) | AC-18 | One line at the gate; the budget alert question stays with 0002. |
| Pester tests for `install.ps1` | Defer (not built) | xunit invariants (AC-2/3) | R1 (tools and tests are .NET); a Pester run would add a tool chain to CI for a 150-line script whose real proof is AC-10. |
| Git prompting under an unattended node | Keep (new, small) | Task command sets `GCM_INTERACTIVE=never` for the node process; unit `Environment="GIT_SSH_COMMAND=ssh -o BatchMode=yes"` | Git Credential Manager "permits or disables interaction with the user, such as GUI or TTY prompts. If interaction is required but has been disabled, an error is returned" (GCM `environment.md`). In the interactive session a GCM window would sit open until the owner noticed, blocking the loop's write; with the variable set the write fails fast into 04's "push deferred" / "pull failed" rows. Scoped to the node process so the owner's own `git` keeps prompting. `GitClient` already sets `GIT_TERMINAL_PROMPT=0`; the SSH equivalent on Central is `BatchMode=yes`. |

---

## Contracts

### Deploy assets (new directory `deploy/` at the repository root, shipped in `artifacts/<rid>/deploy/`)

| File | Purpose |
|------|---------|
| `deploy/systemd/zyggy-node.service` | The Central unit, installed by hand per `install-node.md`; no placeholders; file layer by default. |
| `deploy/systemd/zyggy-node.service.d/credentials.conf.example` | The optional drop-in that feeds the bus secrets through `LoadCredential=` (Q2); placeholders `<zyggy-home>`; copied to `/etc/systemd/system/zyggy-node.service.d/credentials.conf` and edited by the owner when chosen. |
| `deploy/windows/install.ps1` | The laptop installer / uninstaller; PowerShell 5.1; unelevated; registers the logon task. |
| `deploy/README.md` | Five lines: what the files are, where the procedure lives, the hygiene rule. |

#### systemd unit (exact text; AC-1 pins it)

```ini
[Unit]
Description=Zyggy node (bus poller and job runner)
Documentation=https://github.com/zyggy-org/zyggy
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=600
StartLimitBurst=5

[Service]
Type=notify
User=zyggy
Group=zyggy
WorkingDirectory=~
ExecStart=/usr/local/bin/zyggy-node
Restart=on-failure
RestartSec=30
RestartPreventExitStatus=3 6
TimeoutStopSec=30
KillMode=mixed
Environment="GIT_SSH_COMMAND=ssh -o BatchMode=yes"
NoNewPrivileges=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
```

#### Credentials drop-in example (exact text; AC-1 pins the two ids)

```ini
# Optional (deliverable 10, spec 09 ex-Q6): feed the bus secrets to the node through systemd's credentials directory.
# Copy to /etc/systemd/system/zyggy-node.service.d/credentials.conf, replace <zyggy-home>, then: systemctl daemon-reload && systemctl restart zyggy-node.
# Inside the unit the credentials directory wins; `zyggy submit`, `verify` and `secret` from a shell keep using the 0600 file layer.
# The ids follow zyggy.<tenant>.<name with / replaced by .>; the source may also be a root-owned 0600 file, e.g. under /etc/credstore/.
[Service]
LoadCredential=zyggy.acme.hmac.2:<zyggy-home>/.local/share/zyggy/secrets/acme/hmac/2
LoadCredential=zyggy.acme.github-pat:<zyggy-home>/.local/share/zyggy/secrets/acme/github-pat
```

- `node.json` is found at `~/.config/zyggy/node.json` of `zyggy` (systemd sets `$HOME` for `User=` services; `WorkingDirectory=~` is the user's home). No `ZYGGY_NODE_JSON`, no `--config`: the unit stays free of machine paths.
- `claude` is not on a unit's `PATH`: Central's `node.json` sets `claude.path` to the absolute path (`<zyggy-home>/.local/bin/claude`). No `Environment=PATH=` line.
- `Type=notify`: 07's host sends `READY=1` after the hosted services started and `STOPPING=1` on stop; no `WatchdogSec` (the host sends no watchdog pings).
- `Restart=on-failure` + `RestartPreventExitStatus=3 6`: exit 1 (fault) and signals restart after 30 s, at most 5 times in 10 minutes; exits 3 and 6 leave the unit `failed` for a human. `systemctl stop` is exit 0.
- `KillMode=mixed` + `TimeoutStopSec=30`: SIGTERM to the host (which drains runs within its 20 s `ShutdownTimeout` and kills `claude` trees itself), then SIGKILL to anything left.
- No sandboxing beyond `NoNewPrivileges` and `PrivateTmp`: jobs run `claude` in the `zyggy` user's project directories and the bus checkout; `ProtectHome`/`ProtectSystem=strict` would need a `ReadWritePaths=` list that changes with every project (deferred; noted in `install-node.md`).
- **Credentials directory (Q2, owner)**: the file layer holds the secrets the file-layer verbs wrote (`zyggy secret set`); with the drop-in active the unit additionally receives root-owned 0400/0440 copies under `$CREDENTIALS_DIRECTORY` and reads those first (09 AC-2). The two sources must hold the same bytes; after a rotation the owner updates the drop-in's `hmac/<n>` line (or points it at the new file) and restarts the unit; `zyggy secret list` shows the overlap and `zyggy secret set` prints the overlap note.

#### The Windows logon task (behaviour contract; `install.ps1` registers it, AC-10 proves it)

| Element | Value |
|---------|-------|
| Task name | `zyggy-node` (the user's own task; no folder, no elevation) |
| Trigger | At log on of the installing user, delay `PT30S` |
| Principal | `UserId` = the installing user, `LogonType Interactive`, `RunLevel Limited` |
| Action | `powershell.exe` with arguments `-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command "$env:GCM_INTERACTIVE='never'; & '<LocalAppData>\Programs\Zyggy\zyggy-node.exe' --config '<node.json>'; exit $LASTEXITCODE"` |
| Settings | `Hidden`; `MultipleInstances IgnoreNew`; `ExecutionTimeLimit` none; `RestartCount 3`, `RestartInterval` 1 min; `AllowStartIfOnBatteries`; `DontStopIfGoingOnBatteries`; `StartWhenAvailable` |
| Start / stop | `Start-ScheduledTask zyggy-node` / `Stop-ScheduledTask zyggy-node` (a stop terminates the process tree — a kill; the node's claim resume of 07 covers an interrupted run) |
| Exit codes | Task Scheduler's Last Run Result shows the node's code (0x0 / 0x3 / 0x6 / 0x1); a non-zero result triggers the restart-on-failure setting (at most three re-runs), after which the task rests until the next logon or a manual start |

#### `install.ps1` (behaviour contract, not a listing)

| Parameter | Default | Meaning |
|-----------|---------|---------|
| `-Tenant <org>` | required | `node.json` `tenant` |
| `-Machine <name>` | required | `node.json` `machine` |
| `-SignWith <tenant>/<n>` | required when `node.json` is written | `node.json` `signWith` |
| `-BusLocalPath <dir>` | `C:\agents\bus` | `node.json` `bus.localPath` (forward slashes in the file) |
| `-Source <dir>` | the script's folder | where `zyggy.exe`, `zyggy-node.exe`, `SHA256SUMS` are |
| `-NodeJson <path>` | `%LOCALAPPDATA%\zyggy\node.json` | written only when absent |
| `-Uninstall` | — | stop + unregister the task; leaves binaries, `node.json`, the checkout and the secrets |

Sequence (each step prints one line; the script stops at the first failure with the remedy; **no step elevates and no step asks to**): (1) verify `SHA256SUMS` for both binaries when the file is present; (2) stop the task if it is running (the upgrade path), copy both binaries to `%LOCALAPPDATA%\Programs\Zyggy\`, add that folder to the **user** PATH once (HKCU `Environment`); (3) create `-BusLocalPath`'s parent; (4) write `node.json` from the template when absent, else print `exists, left untouched`; (5) pre-flight: `git --version`; the checkout at `bus.localPath` is a repository holding `tenants\<tenant>\` (else the remedy: `git clone https://github.com/zyggy-org/zyggy-bus.git <path>` and `git config core.autocrlf false`); `zyggy secret list --config <node.json>` shows `hmac/<n>` with role `signs` (else the remedy is the node's own line: `zyggy secret set hmac/<n> --hex`) — when it prints the Windows file-store warning the script names `rotate-hmac.md` step 0; `claude.path` from `node.json` exists; (6) register or update the task of the table above with `Register-ScheduledTask -Force` for the current user; if the system refuses (`Access is denied` — a policy forbids per-user tasks), stop with the reason and name the no-code alternative (a `shell:startup` shortcut running the same command, without restart-on-failure; 20's concern); (7) `Start-ScheduledTask`; wait up to 30 s for `Running`; print the `zyggy status` line. The script never reads, prints or stores a password or a secret.

`node.json` template (laptop; between `# node.json-template-begin` and `# node.json-template-end` so AC-3 can parse it):

```json
{
  "tenant": "<tenant>",
  "machine": "<machine>",
  "signWith": "<signWith>",
  "bus": { "remote": "https://github.com/zyggy-org/zyggy-bus.git", "owner": "zyggy-org", "repo": "zyggy-bus", "branch": "main", "localPath": "<busLocalPath>" },
  "poll": { "activeSeconds": 10, "idleSeconds": 60, "safetyFetchMinutes": 15 },
  "workHours": { "timezone": "Europe/Brussels", "days": ["Mon", "Tue", "Wed", "Thu", "Fri"], "from": "08:00", "to": "19:00" },
  "devRoots": [],
  "maxConcurrentJobs": 2,
  "log": { "level": "Information" },
  "claude": { "path": "<userprofile>/.local/bin/claude.exe" },
  "secrets": { "store": "os" }
}
```

Central's `node.json` is written by hand per `install-node.md` (no installer on Central): `machine: central`, `bus.remote: git@github.com-zyggy-bus:zyggy-org/zyggy-bus.git`, `bus.localPath` the 04 checkout, **no** `workHours`, `claude.path` absolute, `secrets: { "store": "os" }`; `role` and `user` are added by 11 (the reader ignores them today).

### Runbooks (`zyggy-core/runbooks/`, public — placeholders only; every command prefixed with the machine and the user that runs it; the owner's filled-in evidence in `instance/runbooks/10-walking-skeleton.md` of the private instance repository)

| File | Outline |
|------|---------|
| `install-node.md` | **Central**: the release download and `sha256sum -c` as `<vm-admin>`; `/opt/zyggy/<version>/zyggy-node` and the symlink; `node.json` as `zyggy` (the keys above; `claude.path` absolute); the keys (`zyggy secret list` must show `hmac/<n> signs`; else the two refusal lines and their remedies); the unit install (`cp`, `daemon-reload`, `enable --now`); the checks of AC-8; **Bus secrets: file layer or credentials directory** — the default is the file layer (one source for the unit and every shell command); choose the drop-in when the unit's copy should be root-owned and read-only (a source under `/etc/credstore/` that the `zyggy` user — and so the model's `Bash` — cannot read, overwrite or delete), accepting two sources to keep equal, a drop-in edit on every rotation, and that `zyggy submit`/`verify`/`secret` from a shell still need the file-layer copy to sign; how to enable, verify (`systemctl show -p LoadCredential`, `zyggy secret list` inside and outside the unit, the overlap note) and disable it; **Upgrade** (stop, new `/opt/zyggy/<version>/`, symlink, start, `/health.version`); **Laptop (no administrator needed)**: Git for Windows and Claude Code logged in; the release folder; `install.ps1` parameters; what each pre-flight line means; the logon task (trigger, hidden window, restart on failure, `Start-`/`Stop-ScheduledTask`, that a stop is a kill and the claim resumes); **the second laptop** (unprivileged: the same script; the `shell:startup` alternative if per-user tasks are forbidden); **Credential Manager under the task** (AC-12: the interactive token; 1312 would be a finding); **URL and port**: `http://localhost:4711/health` is loopback only, never a firewall rule, never a URL ACL; `port 4711 in use` → `netstat -ano`/`ss -tlnp`, one node per machine; **Exit codes**: 0 / 3 / 6 / 1 and what systemd and Task Scheduler do with each (systemd: 3 and 6 no restart, 1 restarts; the task: any non-zero re-runs at most three times, then rests); `GCM_INTERACTIVE` and `BatchMode` and why; **Uninstall**. |
| `restore-central.md` | New VM from the volume snapshot as `<vm-admin>`; `claude login` check as `zyggy`; the two deploy keys and the bus deploy key with the `~/.ssh/config` aliases; `zyggy-core` into the Central working directory, the memory repository, `.claude/settings.local.json` and the `Read`/`Edit` deny rules in `~/.claude/settings.json`; the deliverable-27 verification; the 0600 secrets under `~/.local/share/zyggy/secrets/<tenant>/` re-entered with `zyggy secret set` (never restored from a copy of the file); the credentials drop-in re-created if it was in use; `zyggy verify`; the units in order (`claude-remote`, `zyggy-node`, dream, brief, m365); the clone cache not restored; `zyggy status` green. |
| `revoke-machine.md` | For a lost or compromised machine: (1) revoke its fine-grained PAT (laptops) and delete its deploy key (Central) on GitHub; (2) `rotate-hmac.md` compromise variant tenant-wide (the key is per tenant); (3) if the machine is still in hand: `install.ps1 -Uninstall` / `systemctl disable --now`, `zyggy secret remove` of every name, delete the checkout; (4) move `tenants/<org>/nodes/<machine>/` to `tenants/<org>/archive/` in one commit (`verify` treats `archive/` per 17 — until then the folder is skipped); (5) `zyggy verify` on a remaining machine; (6) the registry file of the machine (14) removed. Other machines keep running throughout (§5). |
| `add-tenant.md` | Create the private bus repository; the ruleset on `main` blocking force-push and deletion **where the plan allows it** (F1: not available on the free plan — recorded, accepted until 20); `.gitattributes` `* -text`; `PROTOCOL.md` (15 makes it verbatim), `tenants/<org>/nodes/<machine>/{jobs,jobs/claimed,jobs/rejected,reports,context}`, `registry/`, `archive/` with `.gitkeep`; `policy.yaml` unsigned until 18; per-machine credentials (PAT scoped to that repository; deploy key for Central); `zyggy secret set hmac/1 --generate --show` on one machine and `--hex` on the others; `memory/<org>/<user>/` (11); notifier (22). **First execution**: 2026-10-13 for `acme`/`zyggy-bus` with the F1 deviation, placeholders only. |
| `rotate-hmac.md` | 09's draft verbatim (steps 1–5 and "First execution — retire the spike file store"), plus a dated "Executions" section whose first row is AC-15 (compromise variant with the cleanup commit, and why: no real job on the bus yet). When the credentials drop-in is in use on Central, step 3 adds "update the drop-in's `hmac/<n+1>` line and `daemon-reload`". |
| `bus-conflict.md` | The Core README "Troubleshooting — Bus" rows (dirty working copy → the named stash; `RebaseInProgress` → `git rebase --abort` and report the file pair; `WrongBranch`/`NotARepository`/`NoTenantDirectory`; CRLF; push deferred; pull failed; rate limit; rejected envelopes) with the unit / task commands to stop and start around a repair on each OS. |

### Envelope / file formats

Nothing on the bus changes: no field, no canonical-form rule, no state move, no `PROTOCOL.md` edit, no new `BusPaths` entry.

### CLI surface

None. `zyggy` and `zyggy-node` gain no verb and no option; `zyggy-node --config` and `--version` (07) are what the unit and the task use.

### Configuration

| Key / setting | Where | Default | Override rule |
|---------------|-------|---------|---------------|
| `node.json` (every key) | per machine, outside every repository | as the Cli README schema | unchanged; 10 adds **no** key |
| `claude.path` | `node.json` | `claude` on `PATH` | **set absolute on both machines**: a unit's or a task's `PATH` is not the terminal's |
| `workHours` | `node.json` | absent = always inside | laptop: the template's Mon–Fri 08:00–19:00 (owner edits); Central: absent |
| `secrets.store` | `node.json` | `os` | `os` on both real machines; `file` is tests only after AC-15 |
| Unit `Restart`/`RestartSec`/`RestartPreventExitStatus` | `zyggy-node.service` | on-failure / 30 s / 3 6 | a drop-in `/etc/systemd/system/zyggy-node.service.d/*.conf` on the machine; never a repository edit for one machine |
| Bus secrets through `LoadCredential=` | drop-in `credentials.conf` (from the shipped example) | off (file layer) | the owner enables it per `install-node.md`; the file layer stays for the shell commands |
| Task trigger delay, restart count/interval, `GCM_INTERACTIVE` | the task (set by `install.ps1`) | 30 s, 3 × 1 min, `never` in the node process | re-run `install.ps1`; `taskschd.msc` for a one-off |
| Health port | `NodeHostOptions.HealthPort` | 4711 | never changed in production (07/08) |
| Claude Code `permissions.deny` for the secrets directory | `~/.claude/settings.json` of `zyggy` on Central | — | by hand in 10; 15's template thereafter |

---

## Behaviors & Conventions

- **One binary, two launchers, zero branches**: the unit and the task run the published `zyggy-node` unchanged; the systemd lifetime 07 registered activates itself under systemd; under the task the binary is a plain console process (the Windows-service lifetime stays idle). 10 ships the two registrations as files and proves them.
- **Nothing on a laptop needs an administrator**: per-user programs folder, user PATH, the user's own scheduled task, the user's Credential Manager, `%LOCALAPPDATA%\zyggy` for state. The same script serves the work laptop in 20.
- **Where secrets live on the real machines**: Central — the 0600 file layer of `LinuxSecretStore` under `~/.local/share/zyggy/secrets/acme/`, read by the unit and by every `zyggy` verb of the `zyggy` user; optionally, in addition, root-owned copies through the credentials drop-in that only the unit sees and that win inside it; the model's file tools are denied the directory (AC-16) and the runner strips `CREDENTIALS_DIRECTORY` (06). Laptop — Credential Manager of the owner's account, read by the task (same interactive session) and by `zyggy` in the owner's shell; no file after AC-15.
- **Who runs what** is written on every command: Central has two users (`zyggy` without sudo; `<vm-admin>` for `/opt`, `/etc/systemd`, `systemctl`); the laptop has one — the owner's plain shell — for everything.
- **A refusal is final until a human acts**: exit 3 and 6 leave the unit `failed`; on the laptop the task re-runs a refusal at most three times a minute apart and then rests; `zyggy status` shows `down`; the log holds one `refusing to start` line per attempt (at most four). A crash (exit 1, a signal) is retried: 30 s on systemd (five times in ten minutes, then `failed`), three times a minute apart by Task Scheduler; every logon starts the task afresh.
- **A stop on the laptop is a kill**: `Stop-ScheduledTask` terminates the process tree; a running `claude` dies with it; the claim stays in `jobs/claimed/` and the next start resumes it (07). The unit on Central stops gracefully (SIGTERM, 20 s drain).
- **Git never waits for a human under the node**: `GIT_TERMINAL_PROMPT=0` (GitClient), `GCM_INTERACTIVE=never` (the task's node process), `ssh -o BatchMode=yes` (unit). A missing credential becomes 04's `push deferred` / `pull failed` rows within seconds.
- **Upgrade = stop, replace, start**, by hand: Central switches the `/opt/zyggy/<version>/` symlinks for both binaries in one go; the laptop re-runs `install.ps1` from the new release folder. `zyggy status` goes amber (`node <v>, cli <v>`) while the two differ.
- **Install order on a fresh machine** (the runbook's spine, both OSes): binaries → `node.json` → bus checkout → keys (`zyggy secret set`, fingerprints compared with `zyggy secret list`) → Claude Code logged in as the node's user → the unit / the task → `zyggy status` green → one `zyggy submit` to itself (08 allows a self-addressed job) as the smoke test.
- **The first real `claude -p` is read-only by construction**: the defaults of `node.json` (`Read, Grep, Glob`, 60 turns, 30 min) and `--permission-mode auto`; the owner picks the project and a descriptive body. A later job may widen `allowed_tools` per job (§4), never the defaults, for the gate.
- **Evidence without identifiers**: this repository, `zyggy-core` and the plan record ids, times, exit codes, versions and redacted `node.json` copies; host names, token ids, fingerprints of real keys and the owner's project paths go to the private evidence page.
- **Rule R1**: the only shell of this deliverable is `install.ps1` (reason in the Decision Table) and the one-line `systemctl`/`cp`/`ln` commands of the runbook; no logic lives in shell beyond the exit-code hand-through of the task command; no new tool chain in CI.
- **Public hygiene**: `deploy/`, the runbooks and this spec use tenant `acme`, `central` / `home-laptop`, `<zyggy-home>`, `<vm-admin>`, `<project>`; AC-4 sweeps them.

---

## Failure modes

| Situation | Observable outcome | Runbook entry |
|-----------|--------------------|---------------|
| Unit refuses: `node.json` invalid, `signWith` missing (exit 3) | `systemctl status`: `failed (Result: exit-code)`, `status=3`; stderr line `zyggy-node: configuration error: <key>: <detail>` in the journal; no restart | `install-node.md` → "Exit codes"; the Cli README schema |
| Unit refuses: key absent / store unavailable / checkout condition / port in use (exit 6) | `failed`, `status=6`, one `refusing to start: …` line; no restart | `install-node.md` → "The two refusal lines"; Core README "Node: refused to start" |
| Unit crashes or is killed (exit 1, signal) | `activating (auto-restart)` after 30 s; after 5 in 10 min `failed` with `start-limit-hit` | `install-node.md` → "Exit codes": `journalctl -u zyggy-node -n 200`, then `systemctl reset-failed zyggy-node && systemctl start zyggy-node` |
| `Type=notify` and the host never reaches started (hangs before `READY=1`) | systemd waits `TimeoutStartSec` (default 90 s), then kills and counts a failure | same row: a hang before `READY=1` is a bug — the journal's last line names the phase |
| Credentials drop-in active but a source file missing or wrong mode | systemd fails the unit at start (`Failed to set up credentials`) or the node refuses `credentials-directory: <path> must be mode …` (exit 6) | `install-node.md` → "file layer or credentials directory": fix the path/mode or disable the drop-in |
| Credentials drop-in and file layer hold different bytes after a rotation | the unit signs with the directory's key, the shell commands with the file's; `invalid_signature` on the other machines for one of them | same row + `rotate-hmac.md` step 3 (update the drop-in); `zyggy secret list` inside/outside the unit shows the two fingerprints |
| Task refuses (exit 3/6) | Last Run Result `0x3`/`0x6`; Task Scheduler re-runs at most three times a minute apart; one `refusing to start` line per attempt; then `Ready` with `zyggy status` down (exit 6) | `install-node.md` → "Exit codes (task)": fix, then `Start-ScheduledTask zyggy-node` |
| Task's node crashes | re-run after 1 min, at most three times; the Serilog file shows the fault line | same row |
| Per-user task creation refused by policy (`Access is denied`) | `install.ps1` stops at step 6 with the reason; nothing half-registered | `install-node.md` → "The second laptop": the `shell:startup` alternative |
| A console window visible after logon | the task was registered without the hidden host (hand edit) | `install-node.md` → "The logon task": re-run `install.ps1` |
| Owner logged off / laptop asleep | nothing runs; `status` on Central shows the laptop's jobs `queued`; nothing is lost; the node starts at the next logon | `install-node.md` → "Laptop power and logon": expected; the alert for a silent node is 22 |
| Git Credential Manager has no credential for `github.com` under the task | `push deferred` / `pull failed` within seconds (no prompt, `GCM_INTERACTIVE=never`) | `bus-conflict.md` → "push deferred": run `git -C C:\agents\bus push --dry-run` in the owner's shell once so GCM stores the token, then `Start-ScheduledTask` |
| SSH host key or agent prompt on Central under the unit | `pull failed … Host key verification failed` (BatchMode) | `install-node.md` → "Central → keys": the `~/.ssh/config` alias and `known_hosts` from 04 |
| `claude.path` not found under the unit/task (`PATH` differs from the terminal's) | jobs fail `claude_error: not_found`; `claudeAuthOk: null`/`false` | `install-node.md` → "`claude.path` absolute"; Core README "Jobs: claude_error" |
| Claude Code not logged in for the node's user | `/health.claudeAuthOk: false`, `status` red `claude not logged in`; jobs fail `claude_error: auth` | Core README "Node: claude auth": `claude login` as that user |
| Port 4711 in use | exit 6 `health port 4711 in use` | `install-node.md` → "URL and port" |
| `install.ps1` pre-flight fails (no checkout, no key, no `claude`) | one line each with the remedy; nothing registered | `install-node.md` → "What each pre-flight line means" |
| `SHA256SUMS` mismatch | `install.ps1` stops before copying; Central's `sha256sum -c` fails | `install-node.md` → "Download": re-download; never install an unchecked binary |
| `Stop-ScheduledTask` during a run | the `claude` tree is killed; the claim stays in `jobs/claimed/`; the next start logs `resuming <n> claimed job(s)` | Core README "Node: interrupted run" |
| `acme/1` removed and a pre-rotation file left on `main` (AC-15 step 5 incomplete) | `zyggy verify` `unknown_key (acme/1)`, exit 6 | `rotate-hmac.md` step 5 and the "Executions" row: finish the cleanup commit |

Every row that is new with this deliverable (the unit and task behaviours, the drop-in, the policy refusal, the GCM/SSH prompts, the pre-flight lines) has its entry in `install-node.md` (§12).

---

## Dependencies

| Package | License | Why |
|---------|---------|-----|
| — | — | No new package. `Microsoft.Extensions.Hosting.Systemd` (07) provides the Linux lifetime; `systemctl`, `schtasks`/the `ScheduledTasks` module and `powershell.exe` are the operating systems' own. `Directory.Packages.props` stays at 24 rows (AC-5). |

Considered and not added: `System.Security.Cryptography.ProtectedData` (no longer a named fallback — AC-12); any PowerShell module (the `ScheduledTasks` module ships with Windows).

---

## Deliberate deviations from the founding spec

All decided by the owner on 2026-10-26; the exact wording is under "Founding-spec amendments (approved wording)".

- §10 Central unit `Restart=always` → `Restart=on-failure` with `RestartPreventExitStatus=3 6`, `Type=notify` (Q6, A10-1).
- §10 Central "Secrets via `LoadCredential=` in the units" → for the bus secrets both layers are usable: the file layer by default, `LoadCredential=` through a shipped drop-in as the owner's option (Q2 as decided by the owner, A10-2).
- §10 Laptops: the Windows **service** becomes a **per-user scheduled task at logon**, unelevated; `%ProgramFiles%\AgentBus` → `%LOCALAPPDATA%\Programs\Zyggy`; `C:\agents\{bus,node,agent-core}` → `C:\agents\bus`; `Delayed Start` → a 30 s logon delay; the optional remote-control task dropped (Q1 as reworked by the owner, Q6, A10-3).
- §8 Isolation: the Windows mechanism and the deny rule's scope named (Q7, A10-4).
- §11 Runbooks list gains `install-node.md` (Q7, A10-5).
- §5 "`main` … blocked": still not possible on the free plan — acceptance extended until 20 in 0001 F1 (Q5; no founding-spec text).

### Founding-spec amendments (approved wording)

> Decided by the owner 2026-10-26 (Q1 reworked, Q2 rejected as recommended, Q3–Q7 accepted). The coordinator shows the re-cut §10 wording to the owner before pasting; then the orchestrating session pastes these into `_specs/00 - Personal Agent Platform — Technical Specification.md` and lists every edit in the journal — never the technical-analyst or the planner. Each block: exact section heading → exact current text → exact replacement text.

#### A10-1 — Central unit restart policy (Q6)

**Section:** `## 10. Configuration and deployment` → **Central (Linux VM or container)**, the "systemd units" bullet.

Current text:

```text
- systemd units: `agentbus-node.service` (Restart=always), `claude-remote.service` (runs `claude remote-control --name central --spawn=same-dir --permission-mode auto` under `tmux` or as a plain service, Restart=always), `zyggy-dream.timer`, `zyggy-dream.path`, `zyggy-dream.service`, `agent-sweep.timer` (hourly, submits a sweep job whose body runs `agentbus sweep`: stale claims, retries, archive, alert conditions).
```

Replacement text:

```text
- systemd units: `agentbus-node.service` (`Type=notify`, `Restart=on-failure`, `RestartPreventExitStatus=3 6` — a start-up refusal waits for a human, a crash restarts after 30 s; shipped as `deploy/systemd/zyggy-node.service` with the release, deliverable 10), `claude-remote.service` (runs `claude remote-control --name central --spawn=same-dir --permission-mode auto` under `tmux` or as a plain service, Restart=always), `zyggy-dream.timer`, `zyggy-dream.path`, `zyggy-dream.service`, `agent-sweep.timer` (hourly, submits a sweep job whose body runs `agentbus sweep`: stale claims, retries, archive, alert conditions).
```

#### A10-2 — Bus secrets on Central: file layer by default, `LoadCredential=` as an option (Q2, owner's decision)

**Section:** `## 10. Configuration and deployment` → **Central (Linux VM or container)**, the "Secrets via `LoadCredential=`" bullet.

Current text:

```text
- Secrets via `LoadCredential=` in the units; container variant reads the same names from mounted files.
```

Replacement text:

```text
- Secrets via `LoadCredential=` in the units whose secret no command-line verb needs (the Graph key, the Telegram token). For the bus HMAC keys and the GitHub PAT both layers of the Linux store (§8) are usable: the `zyggy` user's 0600 file layer is the default — one source for the unit and for `agentbus submit`, `verify` and `secret` run from a shell — and the node unit can additionally load them through `LoadCredential=` (ids `agentbus.<tenant>.hmac.<n>`, `agentbus.<tenant>.github-pat`; the drop-in example `zyggy-node.service.d/credentials.conf.example` ships with the release) when the owner wants the unit's copy root-owned and read-only; inside the unit the credentials directory wins, the file layer serves every shell command, `agentbus secret set` says so when both hold a name, and `install-node.md` says when to choose which (deliverable 10). The container variant reads the same names from mounted files.
```

#### A10-3 — Laptops: unelevated install, a per-user logon task instead of a Windows service (Q1 as reworked by the owner, Q6)

**Section:** `## 10. Configuration and deployment` → **Laptops (Windows 11)**, the first two bullets.

Current text:

```text
- Install script `install.ps1 -Tenant <org> -Machine <name>`: copies binaries to `%ProgramFiles%\AgentBus`, adds to PATH, creates `C:\agents\{bus,node,agent-core}`, writes `node.json` with the tenant, registers `AgentBus.Node` as a Windows service running as the current user (needed for Claude Code auth and Credential Manager access), sets `Delayed Start`.
- Home laptop additionally gets a scheduled task to start `claude remote-control --name home` at logon (optional).
```

Replacement text:

```text
- Install script `install.ps1 -Tenant <org> -Machine <name> -SignWith <org>/<n>` (shipped as `deploy/windows/install.ps1` with the release; PowerShell 5.1; **no elevation** — the user is not an administrator on every laptop, so nothing in the install needs it and the script refuses rather than asks): checks `SHA256SUMS`, copies the binaries to `%LOCALAPPDATA%\Programs\Zyggy`, adds that folder to the user's PATH, creates `C:\agents\bus`'s parent, writes `node.json` only when absent, runs a pre-flight (git, the checkout's `tenants/<org>/`, the signing key via `agentbus secret list`, `claude.path`), and registers `AgentBus.Node` as a **per-user scheduled task `zyggy-node`** that starts at the user's logon (30 s delay) under the interactive logon type — the same session that holds the Claude Code login, the browser and the Credential Manager — with a hidden window, one instance, no time limit, restart on failure three times a minute apart and `GCM_INTERACTIVE=never` scoped to the node process; `-Uninstall` removes the task only. There is no Windows service: a service needs administrator rights, the account's password and a session-0 logon in which the user's Credential Manager is not guaranteed; the node therefore runs only while the user is logged on, and a stop is a kill that the node's claim resume (§5) covers. Node state lives under `%LOCALAPPDATA%\zyggy` (`nodeDir`); `agent-core` is created by deliverable 15.
```

(The second bullet — the optional remote-control task at logon — is deleted; remote control runs on Central.)

#### A10-4 — §8 Isolation: the Windows mechanism and the secrets-directory deny rule (Q1, Q7)

**Section:** `## 8. Work boundary, security and secrets` → **Isolation**, the second and third bullets.

Current text:

```text
- Nodes run as the interactive user's account (needed for Claude Code auth and the browser session) but with `settings.json` allowlists per project and the `PreToolUse` hook enforcing `allowed_tools`.
- `--permission-mode auto` is used everywhere; `--dangerously-skip-permissions` is never used.
```

Replacement text:

```text
- Nodes run as the interactive user's account (needed for Claude Code auth and the browser session; on Windows as a per-user scheduled task inside that logon session, installed without administrator rights — deliverable 10) but with `settings.json` allowlists per project and the `PreToolUse` hook enforcing `allowed_tools`.
- `--permission-mode auto` is used everywhere; `--dangerously-skip-permissions` is never used.
- On Central the model's file tools are denied the secrets directory (`Read` and `Edit` deny rules for `~/.local/share/zyggy/secrets/**` in the user-scoped Claude Code settings, so they hold for every project and for the node's own `claude -p` runs); the runner removes `CREDENTIALS_DIRECTORY` from the child's environment. Neither operating system gives a boundary stronger than the user account (deliverables 09, 10).
```

#### A10-5 — §11 Runbooks: `install-node.md` (Q7)

**Section:** `## 11. Observability and operations` → **Runbooks (`agent-core/runbooks/`)**, the first bullet.

Current text:

```text
- `bus-conflict.md`: dirty working copy or diverged branch on a node → stash, reset to `origin/main`, re-run discover.
```

Replacement text:

```text
- `install-node.md`: install, upgrade and remove `agentbus-node` on Central (systemd unit under `zyggy`; the bus secrets from the file layer by default or through the `LoadCredential=` drop-in) and on a laptop (`install.ps1` without administrator rights; a per-user scheduled task at logon); the exit codes 0 / 3 / 6 / 1 and what systemd and Task Scheduler do with each; the health URL is loopback only (never a firewall rule or URL ACL); the two start-up refusal lines and their remedies.
- `bus-conflict.md`: dirty working copy or diverged branch on a node → stash, reset to `origin/main`, re-run discover.
```

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| `install.ps1` re-run on an installed laptop (upgrade) | Stops the task, replaces both binaries, keeps `node.json`, re-registers the task with `-Force` (same settings), starts; idempotent |
| `install.ps1` run from an elevated shell by mistake | Works the same (the task, PATH and folders are still the user's own); the script prints that elevation was not needed |
| `node.json` present with `"store": "file"` (the spike) when `install.ps1` runs | Left untouched (the script never edits an existing file) but the pre-flight relays the Windows file-store warning that `zyggy secret list` emits and names `rotate-hmac.md` step 0 |
| The owner's account has no password (PIN / Windows Hello only) | Irrelevant: the task needs no password (interactive logon type) |
| Two Windows accounts on the laptop | Each account would have its own task, Credential Manager and `%LOCALAPPDATA%`; only the owner's account is installed; 4711 would collide if both ran (one node per machine) |
| A group policy forbids per-user scheduled tasks (the second laptop) | `install.ps1` stops at step 6 with the reason; the runbook's `shell:startup` alternative (same hidden command, no restart-on-failure) is 20's call |
| The laptop's `%USERPROFILE%` is on a roaming or redirected profile | `%LOCALAPPDATA%` is local by definition; unchanged |
| Central: `zyggy` user's `HOME` not set by the unit (older systemd) | Ubuntu 24.04 ships systemd 255 (sets `$HOME` for `User=`); the runbook's check is `systemctl show -p Environment` and the first journal line `node.json read from <path>` at `log.level: Debug` |
| Central reboots | `enable`d unit starts after `network-online.target`; `claude-remote` and the node are independent units |
| Credentials drop-in active and `zyggy secret set hmac/<n>` run from a shell | Writes the file layer and prints `note: the credentials directory still provides hmac/<n> to the node` (09); the unit keeps its copy until the drop-in is updated and the unit restarted |
| Both machines run jobs for the same `project` path string | Different machines, different file systems; the §6 project lock is per machine; nothing to do |
| A job submitted from Central to `central` | Allowed (08); Central's node runs `claude -p` as `zyggy` in a project under the `zyggy` home — the smoke test of the install order |
| `systemctl restart` during a run | The host drains up to 20 s, kills the `claude` tree, the claim stays in `jobs/claimed/` and is resumed on start (07) |
| `zyggy status` run by `<vm-admin>` on Central | Reads another user's `node.json` path → exit 3; the runbook says every `zyggy` command runs as `zyggy` |
| The work laptop (not a node until 20) still holds `acme/1` in its spike file store | After AC-15 step 5 its key verifies nowhere; 20 enters `acme/2` — stated in the "Executions" row |

---

## Out of Scope

- Any new `zyggy` or `zyggy-node` verb (`install`, `service`, `print-unit`), a unit/task generator, a `zyggy init` join flow (26), `zyggy diagnose` (26), a Central image or Bicep module (26), `selfUpdate` / the `agentbus-self` upgrade job.
- A Windows service in any form; a DPAPI-protected file store, libsecret, `SetCredentialEncrypted=`, any secret transfer by Zyggy (09 Out of Scope stands); changing how `GitClient` authenticates.
- The optional scheduled task for `claude remote-control --name home`; the direct Tailscale fast path (16); the work laptop's install (20 — it reuses the script); DLP (19); `policy.yaml` (18); the registry and `discover` (14); `zyggy-core` checkout, the `PreToolUse` hook and the settings template (15 — 10 only sets the deny rule by hand on Central); alerts for a silent node or `claudeAuthOk` (22); the sweep and `archive/` (17); cost aggregation (25).
- Systemd sandboxing beyond `NoNewPrivileges`/`PrivateTmp`; a Windows Event Log sink; a watchdog; a graceful stop of the task (a kill is documented).
- A pin check of `zyggy-node` against `instance/zyggy.json`; Pester or any non-.NET test tooling.
- Any `node.json` key, `PROTOCOL.md`, §4 field, `BusPaths` entry or `/health` field.
- Branch protection on `zyggy-bus` (accepted until 20; one dated row in 0001 F1).

---

## Open Questions

none — Q1 reworked and Q2 decided by the owner, Q3–Q7 accepted as recommended, all on 2026-10-26; see the table below.

---

## Decisions taken at the spec gate (2026-10-26)

| Q | Decision (Owner 2026-10-26) | Resolved shape | Reason |
|---|-----------------------------|----------------|--------|
| Q1 Windows mechanism and the Credential Manager fallback order | **Reworked by the owner**: no Windows service — the owner is not an administrator on the work laptop. The Windows mechanism is a **per-user scheduled task at logon** (interactive logon type, limited run level, hidden host, 30 s delay, one instance, restart on failure 3 × 1 min), installed by an **unelevated** `install.ps1` that refuses rather than asks for elevation. The service is dropped entirely (one mechanism to document and test; the home laptop gains nothing from session 0). The 1312 risk was a session-0 risk and disappears with the interactive token; the DPAPI file store is no longer a named fallback. Amendments A10-3, A10-4. | Contracts → "The Windows logon task", `install.ps1`; AC-2, AC-10..AC-12, AC-17; the "second laptop" row | A service needs administrator rights, the account's password and the "Log on as a service" right, none of which the owner has on every laptop; the task runs in the very session §8 ties the node to. Costs accepted: runs only while logged on, a kill instead of a graceful stop (claim resume covers it), a window flash at logon. |
| Q2 `LoadCredential=` for the bus secrets on Central | **Recommendation rejected**: both layers stay usable. The unit ships without `LoadCredential=` (file layer by default); the drop-in example `zyggy-node.service.d/credentials.conf.example` ships the two lines for `hmac/<n>` and `github-pat` as a documented option; `zyggy submit`/`secret` from a shell keep using the file layer; the `secret set` overlap note covers the two sources; `install-node.md` says when to choose which. Amendment A10-2. | Contracts → drop-in example, unit notes, `install-node.md` outline; AC-1, AC-8, AC-18 | The owner wants flexibility on Central ("why not use the bus secrets through the credentials directory"); the analyst's finding (the same user signs outside the unit, so the file layer stays) becomes the documentation of the trade-off, not a prohibition. |
| Q3 First rotation variant | **Accepted**: the compromise variant — after steps 1–3, `zyggy secret remove hmac/1` on both machines and one owner commit on `main` removing the probe-era files of `tenants/acme/`; `verify` clean afterwards; the work laptop receives `acme/2` in 20. | AC-15; `rotate-hmac.md` "Executions" | The only moment the real retirement of the spike keys costs nothing is before a real job exists; a retained key would still verify new envelopes. |
| Q4 Runbook home | **Accepted**: placeholder-only product runbooks in `zyggy-core/runbooks/` (public); the owner's evidence with real identifiers in one private page `instance/runbooks/10-walking-skeleton.md` of the instance repository. | AC-6, Contracts → Runbooks, the AC kind **O** definition | The founding spec's §11 home kept; the hygiene rule honoured; the roadmap brief's "private `zyggy-core`" sentence is for the project-manager to correct. |
| Q5 Branch protection (F1) | **Accepted**: the risk acceptance is extended until the hardening deliverable (20, the work laptop); today's date and the reason written into 0001's F1 row. No founding-spec change. | AC-14; `add-tenant.md` | P1 carries the owner's own projects' envelopes and run summaries; two owner machines hold the write credentials; three clones mirror the history; no force path in the bus code. |
| Q6 §10 wording | **Accepted, re-cut** for Q1/Q2: A10-1 (unit restart policy), A10-3 (laptops: unelevated install, logon task, `%LOCALAPPDATA%\Programs\Zyggy`, `C:\agents\bus`, the remote-control task dropped). | Founding-spec amendments | §12: the spec is updated where behaviour changed. |
| Q7 §11 / §8 additions | **Accepted**: A10-5 (`install-node.md` first in the runbook list) and A10-4 (the deny rule's user scope in §8 Isolation, now also naming the Windows mechanism). | Founding-spec amendments; AC-16 | §12: a runbook entry per new failure mode; the deny rule written where the rule is. |

---

## Notes for the planner and the project-manager

1. **Plan shape**: two slices. Slice A (automated, before any machine is touched): `deploy/` (unit, drop-in example, `install.ps1`, README) with its invariants tests (AC-1..AC-5), the runbook files in `zyggy-core/runbooks/` and the README edits (AC-6), the CI artefact step. Slice B is **owner-executed only**: one checklist in the final 🛑 HUMAN GATE with AC-8..AC-18 in the order Central install → laptop install (unelevated) → AC-15 step 0 (retire the spike store) → rotation steps 1–3 → step 5 (remove `acme/1`, cleanup commit) → AC-12/AC-13 round trip → AC-16 deny rule → AC-17 restarts → AC-14/AC-18 records. The executor can tick nothing in Slice B.
2. **Release**: Slice B needs a tagged release (`v<x>`) whose artefact carries `deploy/`; the owner's "release Step" pattern of 04/06 applies (tag, CI on both runners, `SHA256SUMS`, the instance pin for `zyggy` — the node has no pin).
3. **Roadmap corrections for the project-manager**: the 10 Goal/Scope say "Windows service … installed by `install.ps1`" → "per-user scheduled task at logon, installed without administrator rights"; "with `LoadCredential=`" → "file layer by default, `LoadCredential=` drop-in as an option"; "zyggy-bus branch protections" → "accepted until 20 (0001 F1)"; the brief's "private `zyggy-core`" sentence per Q4; `install-node.md` joins the runbook list; the DPAPI fallback line is removed; 20's entry gains "reuses `install.ps1` (no administrator needed)".
4. **Existing texts to touch**: Cli README "Per-machine setup" and the three "deliverable 10's `install.ps1`" / "service" mentions (Cli and Core README rows that say "run the service as the user who stored the secrets" → "the task runs in the owner's session"); Core README "Runbook draft: rotate-hmac.md" → pointer; Node README lines 3 and 19 ("Windows service" → "a per-user scheduled task"); `ci.yml` artefact and `SHA256SUMS` steps; `.gitattributes` for `*.ps1` (LF) if not already covered; 0001 F1 row.
5. **What is not in 10's code**: no change to `NodeHost`, `PollLoop`, `LinuxSecretStore`, `GitClient`, the health contract, the `node.json` reader; the Windows-service lifetime registration of 07 stays as it is (idle under the task).
6. **Coordinator's step before the paste**: show A10-2 and A10-3 (the re-cut wording) to the owner, then the orchestrating session applies A10-1..A10-5 and journals them.
