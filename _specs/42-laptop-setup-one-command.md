# Spec: 42 — Laptop setup in one command

> Founding-spec sections: §3 Components (Node instance, Skills `remember`/`archive`, Hooks `Stop`/`SessionEnd`), §5 Credentials per machine, §8 Secrets / Isolation / Work boundary, §9 design rules, §10 Laptops, Claude Code side and Upgrade path (as amended by A10-1..A10-5), §11 Runbooks, §13 (MVP-scope decision; "HMAC-signed envelopes, per-machine credentials"). Roadmap entry: `_plans/ROADMAP.md` #42 (approved at the roadmap gate 2026-11-02), tooling rule R1.
>
> Baseline: `deploy/windows/install.ps1` and `tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs` **as they are on 2026-11-04, with the uncommitted installer bugfix of 2026-11-02** (`-Source`/`-NodeJson` resolved in the body through `GetFolderPath`, `Get-Optional` for missing `node.json` properties, the status line is the first `node` line). That fix is not part of 42 and is assumed landed before 42's plan starts.
>
> Status: **spec gate passed 2026-11-04**. Every Open Question was answered by the owner (see "Decisions taken at the spec gate"). The spec is ready for the planner.

## Why this deliverable exists (the evidence, placeholders only)

On 2026-11-02 a laptop still running a pre-installer 0.8.1 spike was upgraded to v0.13.0 by hand in about twelve steps. Read against the code, the steps come from five causes. Only one of them is a per-release cost:

| What happened | Root cause found in the code | Does it come back with every release? |
|---|---|---|
| Old `zyggy.exe` / `zyggy-node.exe` in `%LOCALAPPDATA%\zyggy\bin` shadowed the new `%LOCALAPPDATA%\Programs\Zyggy` | The installer adds its folder at the **end** of the user PATH and never looks for other copies. | Once per spike laptop. It also catches a terminal started before the install. |
| "Secrets had to be reset" | The spike used the P0 **file** store (`%LOCALAPPDATA%\zyggy\secrets\<tenant>\…`, O20). A `node.json` without `secrets` selects `os` (`SecretsSettings` default), i.e. the Credential Manager, where nothing was stored. The Credential Manager targets `zyggy/<tenant>/<name>` **survive every reinstall**. | **No.** A laptop installed by 10 or later keeps its secrets across upgrades. Under O20 the spike keys were spike-only and were rotated (rotate-hmac.md step 0), so importing them would be wrong. |
| Installer crashed on the old `node.json` (no `claude`, no `secrets`) | A StrictMode property read; fixed by the 2026-11-02 bugfix. `secrets` absent already means `os`. `claude.path` absent fails the pre-flight with a remedy that is still a hand step. | Once per old `node.json`. |
| No `zyggy-core` checkout and no `ZYGGY_SECRET_PATTERNS`, so `zyggy context` / `zyggy archive` exit 3; skills copied by hand | Nobody installs the laptop side. 12 and 41 forwarded it to 15, and 15 to 42. | Once per laptop. **Every template change** (a skill update) after that. |
| The PATH change only showed up in new shells | A child process cannot change its parent's environment. VS Code keeps the environment it started with. | Every install that changes PATH. |

So what comes back every release is narrower than the roadmap entry suggests: **wire the laptop side once, keep it current, and never shadow the binary.** The per-release "re-enter every secret" pain was a one-time spike migration. 42 explains it (AC-19) and does not automate it.

---

## User Story

**As** the owner, on either laptop, whatever was installed there before,
**I want** one run of `install.ps1` from the unpacked release folder to leave the laptop ready as a node:

- the new `zyggy` first on PATH;
- `node.json` usable;
- each missing secret named with the exact command that sets it;
- the `zyggy-core` checkout, the user-scope Claude Code settings (`ZYGGY_SECRET_PATTERNS`, deny rules) and the `remember`/`archive` skills in place;
- and the same run on an already-wired laptop changing nothing,

**So that** `zyggy context` and `zyggy archive` work in the next laptop session with no hand step, every later release is one command, and 12's forwarded laptop-side checks can finally be run. The node `Stop`/`SessionEnd` hooks stay a manual step by owner decision (2026-11-04).

---

## Acceptance Criteria

Kinds:

- **U** — unit test: a static script invariant under `tests/Zyggy.Core.Tests/Deploy/`, or a Core unit test with substitutes.
- **I** — integration test in `tests/Zyggy.Integration`, on a temp profile directory and a local bare repository. Never the real `~/.claude`, never GitHub, never the real `claude`.
- **CI** — the pipeline.
- **D** — documents.
- **O** — owner-run on a real laptop.

| # | Kind | Given | When | Then |
|---|------|-------|------|------|
| AC-1 | U | A user PATH entry other than `%LOCALAPPDATA%\Programs\Zyggy` holds `zyggy.exe` or `zyggy-node.exe`, and every file in that folder matches `zyggy*` | `install.ps1` runs step 2 | That entry is removed from the **user** PATH (`'User'` target only) and from the current session's `$env:Path`. One line: `install: removed <dir> from the user PATH (an older zyggy; its files are kept - delete the folder by hand when you no longer need it)`. No `Remove-Item` names that folder. |
| AC-2 | U | The same, but the folder also holds files not matching `zyggy*`; or the copy sits on the **machine** PATH | step 2 | PATH is not changed. One warning line names the folder, says it shadows the new `zyggy` and how to fix it by hand. The script never writes the `'Machine'` target (the existing `Script_WritesNothingMachineWide` holds). |
| AC-3 | U | A `zyggy-node.exe` process of this user runs from a folder found by AC-1/AC-2 (an older node holding port 4711) | step 2, before the copy | It is stopped through the same wait-then-force pattern as `Stop-Node`, with one line. No file is deleted. |
| AC-4 | U | Step 2 has run | — | `$env:Path` of the running session has the programs folder in front of any remaining zyggy folder. The **last** line of a successful run says that terminals, VS Code windows and Claude Code sessions opened before the install still have the old PATH and environment and must be reopened. |
| AC-5 | U | The binaries are copied and `node.json` exists (written from the 10 template or kept) | The script calls the new verb | **The call:** exactly one `zyggy setup`, through `Invoke-Native`, by the **absolute** path `$zyggyExe`. Options: `--config`, `--tenant`, `--machine`, `--core-path`, plus `--sign-with` when `-SignWith` was given. **Exit 3 or 4:** `Fail` **before** the task is registered (as the signing-key refusal does today). **Exit 6:** the task is still registered and started, then the run ends with `install: incomplete - see the setup lines above` and exit 1. **Exit 0:** continue. The old `secret list` regex check is gone; `setup` owns it. |
| AC-6 | U | — | `Script_ParametersMatchTheContract` | Parameters, in order: `Tenant`, `Machine`, `SignWith`, `BusLocalPath`, `CorePath` (default `'C:\agents\zyggy-core'`), `Source`, `NodeJson`, `Uninstall`. Every existing invariant still holds: no elevation, no password, no `Read-Host`, no download cmdlet, PowerShell 5.1 syntax only, LF/no BOM, hygiene. `Script_DownloadsNothing` stays true: the script holds no new URL, because the verb clones `zyggy-core`. |
| AC-7 | U | `node.json` without `claude.path`; `claude.exe` found on the process PATH or at `<UserProfile>/.local/bin/claude.exe` | `zyggy setup` | A backup `node.json.bak-<yyyyMMddTHHmmssZ>` is written beside it first. `claude.path` is added (absolute, forward slashes). Every other property, value and array element is JSON-equal to before. Line: `setup: node.json: added claude.path (backup <file>)`. |
| AC-8 | U | `node.json` without `signWith` | `zyggy setup --sign-with acme/2` / without `--sign-with` | With the option: added with a backup, as in AC-7. Without it: exit 3 with the remedy `run install.ps1 again with -SignWith acme/<n>`, file untouched. |
| AC-9 | U | `node.json` whose `tenant` or `machine` differs from `--tenant` / `--machine` | `zyggy setup` | Exit 3, both values named, file untouched, no backup. |
| AC-10 | U | `node.json` not valid JSON, unreadable, or refused by `NodeConfigurationReader` after completion | `zyggy setup` | File untouched, no backup, exit 3, the reader's message. |
| AC-11 | U | `node.json` already complete | `zyggy setup` | No write, no backup. Line `setup: node.json: already done`. Keys whose absence has a working default (`secrets`, `workHours`, `devRoots`, `log`, `maxConcurrentJobs`, `archive`) are **never** added. |
| AC-12 | U | `claude.path` present but naming a file that does not exist | `zyggy setup` | When a `claude.exe` is found (AC-7's two places): a backup is written, the value is replaced, and one line names the old and the new value (`setup: node.json: claude.path <old> did not exist; set to <new> (backup <file>)`). When none is found: left as is, and the script's existing `claude.path` pre-flight `Fail` names the remedy. |
| AC-13 | U | `<claude-dir>/settings.json` absent / present and valid | `zyggy setup` | **Absent:** created with exactly the two items below. **Present:** backup `settings.json.bak-<stamp>` beside it, then an additive merge. **The two items:** `env.ZYGGY_SECRET_PATTERNS` = `<core>/.claude/hooks/secret-patterns.txt` when absent; the three deny rules `Bash(zyggy secret*)`, `Bash(zyggy context --stop-hook*)`, `Bash(zyggy context --flush*)` in `permissions.deny` when absent (set union). Every existing key, value and array element keeps its value and order. Nothing is removed from `deny` or `allow`. **`hooks` is never added, changed or removed.** |
| AC-14 | U | `env.ZYGGY_SECRET_PATTERNS` already set to another value | `zyggy setup` | Left as is. Line `left as is (<value>)`. The outcome counts as done when that file exists, and as failed (exit 6) when it does not. |
| AC-15 | U | `settings.json` not valid JSON, or not writable | `zyggy setup` | File untouched, no backup, `setup: settings: failed - not valid JSON; left untouched` (or the write error), exit 6. |
| AC-16 | U | `CLAUDE_CONFIG_DIR` set / `--claude-dir` given | `zyggy setup` | The settings and skills go to that directory; otherwise to `<UserProfile>/.claude`. |
| AC-17 | U+I | `<core>/node/skills/{remember,archive}/` | `zyggy setup` | Copied to `<claude-dir>/skills/remember/` and `…/archive/`. **Same bytes:** `already done`. **Different:** the existing folder is first moved to `<LocalAppData>/zyggy/backup/<stamp>/skills/<name>/`, then replaced. The backup is **outside** the skills directory, so Claude Code never loads a second `remember`. |
| AC-18 | I | `zyggy-core` at `--core-path`, in one of five states | `zyggy setup` | **Absent:** cloned from `--core-remote`. **Clean checkout on `main`:** `pull --ff-only` (`already up to date` when nothing changed). **Dirty, on another branch or diverged:** left as is with the reason; its files are used as they are. **An existing folder that is not a checkout or has another `origin`:** untouched, exit 6. **Remote unreachable:** exit 6 with git's first stderr line. Every git call goes through `IProcessRunner` with `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never` in the child's environment. |
| AC-19 | U | A store with the `signWith` key and/or `github-pat` present or missing; a spike file-store folder `<LocalAppData>/zyggy/secrets/<tenant>/` present while `secrets.store` is `os` | `zyggy setup` | **One line per needed secret:** `present (fingerprint <fp>)`, `missing - run: zyggy secret set hmac/<n> --hex`, or `missing - run: zyggy secret set github-pat`. **Signing key missing:** exit 4. **Only `github-pat` missing:** exit 6, because the node still runs on its 15-minute safety fetch. **Spike folder present:** one line `a spike file store exists at <dir>; its keys are retired (rotate-hmac.md step 0) and not imported`; nothing read from it, nothing deleted. `setup` never reads Git Credential Manager or any other credential source. |
| AC-20 | U | `node.json` `machine` is `central` | `zyggy setup` | Exit 2 `setup is for laptops; Central is installed by install-node.md`, nothing touched. |
| AC-21 | I | A temp profile wired by a first `zyggy setup` run | A second identical run | Every line is `already done` or `already up to date`. Exit 0. No file under the temp profile, in `node.json` or in the skills is rewritten (content and last-write time unchanged). No backup is created. |
| AC-22 | U | A substitute store holding known secret values | Any `setup` run | No value appears on stdout, stderr, a log line, a written file or a process argument. `SecretSweep` runs over every captured stream and every file the test run wrote. |
| AC-23 | CI | Clean clone, both runners | `ci.yml` | Build, format, the full test suite, the script parse on both runners, both single-file publishes and the smoke run stay green. `Directory.Packages.props` gains **no** row. |
| AC-24 | D | — | After the build | **`zyggy-core` `node/README.md`:** "Wiring" says `install.ps1` writes `ZYGGY_SECRET_PATTERNS`, the deny rules and both skills (deliverable 42). The `Stop`/`SessionEnd` hook entries stay documented there as the **manual** step (owner decision 2026-11-04). The "installed by deliverable 15" wording is gone, including in the `archive` section. **`node.bats`:** the README test is updated to match. **Install runbooks:** `deploy/README.md`, `zyggy-core/runbooks/install-node.md` (public) and the private instance `runbooks/install-node.md` ("Laptop" and "Upgrade") are reduced to the one command, plus the manual hooks step and the manual fallback. **Runbook entries:** one for every failure row below. **Cli README:** gains `setup`. |
| AC-25 | D | The owner approved W42-1..W42-5 at this gate | After the gate | The orchestrating session pastes them into the founding spec and lists each edit in the journal. The analyst and the planner never do. |
| AC-26 | O | A laptop on an older install (the work laptop at the owner's choice) | One `install.ps1` run from the new release folder in a fresh PowerShell window | **During the run:** no hand step besides the secret lines the run itself names. **After it:** `zyggy secret list` shows both secrets; `zyggy context -- "<fact>"` and `zyggy archive …` succeed from a **new** Claude Code session; no secret value appeared on screen. **A second run:** prints only `already done` lines. Evidence with real paths goes only into the private instance runbook. |
| AC-27 | O | Forwarded from 12 via 15 (still pending) | On a laptop wired by 42 | One `zyggy context` fact reported `OK` by `zyggy verify` on Central. On a laptop where the owner wired the hooks by hand per `node/README.md` "Wiring": one hand-run session end leaves one context commit on the bus and an empty spool. The "install 0.11.0 on the home laptop" check is superseded. |
| AC-28 | U | Every new test, fixture and document line in `zyggy` and `zyggy-core` | Hygiene sweep (as 10 AC-4) | Placeholders only (`acme`, `alice`, `home-laptop`, `work-laptop`, `<instance-dir>`). No employer name, real user, host, token, fingerprint or path under a real profile. |

---

## Decision Table

| Founding-spec / roadmap item | Verdict | Target type / library | Justification |
|---|---|---|---|
| Scope 1: detect an older `zyggy` on the user PATH, take its folder off, never delete, never the machine PATH | **Keep** (in PowerShell) | `install.ps1` step 2 | Root cause of the shadowing. It stays shell under R1 for a reason a verb cannot get around: the script must change **its own session's** `$env:Path`, and a child process cannot do that. It must also run before the new binary is known to be the one PATH resolves. About 20 lines, no parsing beyond splitting PATH. |
| Scope 1 (risk): a folder with other content | **Keep** (warn only) | AC-2 | The roadmap risk is real: removing a shared folder breaks other tools. "Only `zyggy*` files" is a testable rule. |
| Old `zyggy-node.exe` still running from the old folder | **Keep** (added edge) | AC-3 | It would hold port 4711, and the new node would refuse with "health port in use". Stopping the owner's own process is reversible; deleting files is not, and stays forbidden. |
| PATH visible only in new shells | **Reshape** | `$env:Path` updated in-session, plus a final reopen line (AC-4) | The running window is fixed for free, because the script runs in the caller's process. Other windows cannot be reached, so the line says so instead of pretending. |
| Scope 2: complete an old `node.json` (backup, add missing keys, keep existing values) | **Reshape**: only the keys whose absence breaks the install (`claude.path`, `signWith`) | `zyggy setup` → `node.json` completion over `System.Text.Json.Nodes` | **What else exists:** `secrets.store: os` is the reader's default, so adding it changes nothing (`SecretsSettings`). `workHours`/`devRoots`/`log`/`archive` have working defaults; writing them would make choices for the owner. **Why these two:** without `claude.path` the pre-flight fails (a hand step today); without `signWith`, `context`/`archive` exit 3. **Why .NET (R1):** PowerShell 5.1's `ConvertTo-Json` truncates at depth 2 and re-escapes text, a known risk of corrupting an owner file. |
| Scope 2: "leave every existing value as it was" vs a broken `claude.path` | **Reshape** (owner approved 2026-11-04, Q6) | AC-12 | A `claude.path` naming a missing file is provably broken. It is replaced only when a `claude.exe` is found, with a backup and an old→new line. This removes the hand step seen in the evidence. |
| Scope 2: tenant/machine of an existing `node.json` vs the parameters | **Keep** (added refusal) | AC-9 | Today a mismatch passes silently, and the pre-flight checks the parameter's tenant folder, not the file's. A wrong-tenant node is a §14 tenancy fault. It is refused, never rewritten. |
| Creating `node.json` when absent (10's template in the script) | **Keep** (unchanged) | `install.ps1` step 4 | Proven by 10 AC-3. Moving it into the verb would churn a tested contract for no gain. The verb only completes the file. |
| Scope 3: guidance per missing secret via `zyggy secret list`, no value printed | **Reshape**: in-process in the verb, not by parsing `secret list` output | `zyggy setup` reads `ISecretStore.ListAsync` and fingerprints (`SecretFingerprint`) | The same data as `secret list`, without a PowerShell regex over console output (`-match '(?m)^hmac/\d+\s.*\ssigns'`). Names every missing secret, not only the first. |
| Scope 3: Git Credential Manager route for the GitHub token | **Defer**: rejected by the owner (2026-11-04, Q1) | — | Today's behaviour stays: `setup` names a missing `github-pat` with the existing `zyggy secret set github-pat` remedy. It never reads Git Credential Manager. No new token rule. |
| Importing the spike file store into the Credential Manager | **Defer** (never) | AC-19 info line | O20: spike keys are spike-only and rotated (rotate-hmac.md step 0). Importing would put a retired key into the product store. The info line explains the "missing" secrets instead. |
| Scope 4: clone or update `zyggy-core` at `C:\agents\zyggy-core` (a parameter with that default) | **Keep** (in the verb) | `zyggy setup` → git through `IProcessRunner` (`GitClient` patterns) | §3 Node instance "`agent-core` checked out"; §9/§10 one checkout per machine. The repository is public, so no credential is needed. In .NET it is testable against a local bare repository (AC-18), and the script keeps "downloads nothing". |
| Pin `zyggy-core` to the release vs follow `main` | **Decided: follow `main`, fast-forward only** (owner 2026-11-04, Q3) | AC-18, W42-4 | `zyggy-core` has no release tags. §9's pinned SHA (`agentCore.pinnedSha`) belongs to 15 (P2). |
| Scope 4: merge `ZYGGY_SECRET_PATTERNS` into `~/.claude/settings.json` `env` (backup, merge, never overwrite) | **Keep** (in the verb) | settings merge over `System.Text.Json.Nodes.JsonNode` | Required: `context`/`archive` fail closed without it. A merge is logic, so R1 puts it in .NET. `JsonObject` keeps property order. Invalid JSON leaves the file untouched (roadmap risk: "a bad merge breaks every session"). |
| Scope 4/6: deny rules `Bash(zyggy secret*)`, `Bash(zyggy context --stop-hook*)`, `Bash(zyggy context --flush*)` | **Keep**: default on every laptop | same merge, set union | A security control (§8). Additive deny rules only restrict the model and never weaken an existing rule, so they are safe on the employer's laptop. |
| Scope 4/6: `Stop` (`--stop-hook`) and `SessionEnd` (`--flush`) hooks | **Defer**: stays manual by owner decision (2026-11-04, Q2: "leave it like now") | `node/README.md` "Wiring" (manual) | `setup` neither installs, changes nor removes hook entries. The owner wires them by hand where he wants them, as documented today. |
| Scope 4: install the `remember` and `archive` skills | **Keep** (both, every laptop) | copy into `<claude-dir>/skills/` | 41 decision 2 allows archiving from every machine. `remember` carries its own work clause. |
| Skills by copy vs by link | **Decided: copy** | AC-17 | A link (junction) to the checkout would follow `git pull` without a re-run. But `discover --link` (15) is not built, and a junction is a second mechanism to test and document. A copy, with its backup outside the skills directory, is enough; a re-run refreshes it. |
| Scope 5: a second run changes nothing and says "already done" per item | **Keep** | AC-21 | The roadmap's definition of done. The binaries and the task are re-applied identically, as today. |
| Scope 6 (moved from 15): the laptop side of the 12 template | **Keep** for env, deny rules and skills; **hooks stay manual** (owner, Q2) | AC-13, AC-17, AC-24 | The non-hook parts are what `context`/`archive` need. The hook part is the documented manual step. |
| Scope 6: owner-run checks forwarded from 12 | **Keep** | AC-27 | Still pending. They only become possible once a laptop is wired. The session-end check runs where the owner wired the hooks by hand. |
| Where the logic lives (open point 1) | **Decided: one new verb `zyggy setup`** (name approved 2026-11-04, Q5). The script keeps installer glue only. | `Zyggy.Core` setup area + `Zyggy.Cli` command | R1 rules 1–2: JSON completion, the settings merge, the git clone, the file copy with backup and the secret report are logic, so they are .NET and unit-tested on temp directories. The script's remaining shell: checksums, the task, the user PATH and session PATH (reason above), one call, one exit-code branch. |
| Central gets the same "from any state" treatment (open point 6) | **Defer** | `setup` refuses `central` (AC-20) | One machine, hand-installed per `install-node.md`, with systemd and a different `node.json`. MVP-scope decision: no code for a case that happens once. |
| `zyggy init --join <token>` / reproducible install (§14) | **Defer** (stays cut, 26) | — | §13 MVP-scope decision. `setup` creates no tenant, issues no credential and contacts no Central. |
| Cloning the private bus repository in the installer | **Defer** | existing remedy line | It needs the owner's interactive Git Credential Manager sign-in. The current remedy (`git clone … ; core.autocrlf false`) stays. |
| `agentCore.path` / `pinnedSha` written into `node.json` | **Defer** to 15 | — | No reader exists. `--core-path` / `-CorePath` carries the path for 42. |
| Pester / a PowerShell run in tests | **Defer** (as 10) | static xunit invariants | R1 and the repository rule: tests of the script stay static. |

Verdict counts: Keep 12, Reshape 5, Library 0 (framework features only: `System.Text.Json.Nodes`, `IProcessRunner`), Defer 8, plus 3 design decisions on open points (where the logic lives, copy vs link, `main` vs pin).

---

## Contracts

### Types and interfaces (`Zyggy.Core.Setup`, new; no new seam)

No new seam. The verb uses the existing `ISecretStore` (a seam) and `IProcessRunner` (git). It needs nothing from `IBusProvider`, `IModelRunner`, `INotifier` or `IPolicySource`. Names and shapes, not implementations:

- `SetupRequest` (record):
  - `ConfigPath`;
  - `Tenant` (the expected `TenantId`);
  - `Machine` (the expected `MachineName`);
  - `SignWith` (`KeyId?`);
  - `CorePath` (absolute);
  - `CoreRemote` (default `https://github.com/zyggy-org/zyggy-core.git`);
  - `ClaudeDir`, resolved as `--claude-dir`, else `CLAUDE_CONFIG_DIR`, else `<UserProfile>/.claude`;
  - `Now`, from `TimeProvider`, for backup stamps.
- `SetupItem` (closed enum, in the order of the output lines): `NodeJson`, `ZyggyCore`, `SettingsEnv`, `SettingsDeny`, `SkillRemember`, `SkillArchive`, `SecretSigningKey`, `SecretGitHubPat`, `SpikeFileStore`. An absent spike store prints nothing.
- `SetupOutcome` (closed enum): `Done`, `AlreadyDone`, `LeftAsIs`, `Missing`, `Failed`.
- `SetupLine` (record): `Item`, `Outcome`, `Detail`. The detail never holds a secret value, only paths and fingerprints.
- `SetupExit` (constants):
  - `Ok = 0`;
  - `Usage = 2`, including a `central` machine;
  - `NodeConfig = 3`: `node.json` missing, unreadable or invalid; a tenant/machine mismatch; `signWith` absent and not given. The node cannot start.
  - `SigningKeyMissing = 4`;
  - `Incomplete = 6`: `zyggy-core`, settings or a skill failed; an env value points to a missing file; `github-pat` is missing.

  When several apply, the priority is 3, then 4, then 6.
- Backups:
  - `<file>.bak-<yyyyMMddTHHmmssZ>` beside `node.json` and `settings.json`;
  - skills under `<LocalAppData>/zyggy/backup/<stamp>/skills/<name>/`;
  - made only when the file is about to change. Writes go to a temp file, then replace the original in the same directory.

### CLI surface

`zyggy setup --config <node.json> --tenant <org> --machine <name> [--sign-with <org>/<n>] [--core-path <dir>] [--claude-dir <dir>] [--core-remote <url>]`

- Default `--core-path`: `C:/agents/zyggy-core` on Windows; `~/agents/zyggy-core` elsewhere, tested only at unit level (there is no Linux laptop in v1).
- `--core-remote` exists so the integration test can point at a local bare repository. It is documented as "for tests and forks".
- Output: one line per `SetupItem` that applies, `setup: <item words>: <outcome words>[ - <detail>]`, on stdout. Errors also go to stdout, so the installer's single `Invoke-Native` capture shows them in order. No colours, no secret. PowerShell 5.1 renders it unchanged.
- Exit codes: `SetupExit` above.
- `zyggy secret` is unchanged.

`install.ps1` parameters: as in 10, plus `-CorePath <dir>` (default `C:\agents\zyggy-core`). Sequence; steps marked **(new)** or **(changed)** come from 42:

1. Checksums.
2. Stop the task, and **(new)** stop an old-folder node. Copy. Add the programs folder to the user PATH. **(new)** Remove old-copy folders (AC-1/2). **(new)** Update `$env:Path`.
3. Create the bus checkout's parent.
4. Write `node.json` when absent.
5. Pre-flight: git, the checkout.
6. **(new)** `zyggy setup`, then branch on its exit code.
7. `claude.path` pre-flight.
8. The task.
9. Start, then the status line, then **(new)** the reopen line.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `-CorePath` / `--core-path` | installer parameter / verb option | `C:\agents\zyggy-core` | Per run. Not stored in `node.json` until 15. |
| `claude.path` | `node.json` | added by `setup` when absent, or when it names a missing file and a `claude.exe` is found | The owner's value wins once it names an existing file. |
| `signWith` | `node.json` | from `-SignWith` when absent | The existing value always wins. |
| `env.ZYGGY_SECRET_PATTERNS` | `<claude-dir>/settings.json` (user scope) | `<core>/.claude/hooks/secret-patterns.txt` | An existing value always wins (AC-14). Managed (enterprise) Claude Code settings outrank user settings and are never touched. |
| `permissions.deny` (three rules) | same file | added | Union only. `setup` never removes them. |
| `hooks` | same file | never written by `setup` | Manual, per `node/README.md` "Wiring" (owner, 2026-11-04). |
| `CLAUDE_CONFIG_DIR` | process environment | unset → `<UserProfile>/.claude` | `--claude-dir` wins over it. |

No key is added to the `node.json` schema, and no package is added.

---

## Behaviors & Conventions

- **Owner files are backup-first, additive and idempotent.**
  - A file is backed up only right before it changes; a run that changes nothing writes nothing.
  - JSON is re-serialised with two-space indentation.
  - **Comments in `node.json` are lost** on the rare run that changes it (the reader allows comments). The backup keeps them, and the line says so when the original had any.
- **Nothing is deleted.** Old binaries, old folders, the spike file store and replaced skills all stay (the skills as backups). The owner deletes them by hand.
- **Secrets**: `setup` reads names and fingerprints only. It never reads a credential helper, and never puts a value on the console, in a log, a file, an argument or an environment variable.
- **Git never waits for a human** under `setup` (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`). The `zyggy-core` clone is anonymous: a public repository needs no credential.
- **Proxy**: git's own `http.proxy` (already set on the work laptop for the bus, §5 item 7) serves the `zyggy-core` clone. `setup` sets none.
- **Work laptop (§8)**: what `setup` writes there is user-scope Claude Code configuration — one env variable, three deny rules, two skills — plus a clone of a public repository.
  - No hooks.
  - No employer data leaves the machine through `setup`.
  - The settings write affects every Claude Code session on that laptop. That is why the merge is additive and refuses invalid JSON.
- **Rule R1**: the script's new shell is only:
  - the PATH cleanup (reason: its own session's environment, before the binary is trusted);
  - the old-node stop;
  - one exit-code branch.

  Every other new behaviour lives in `zyggy setup`. `install.ps1` must not grow JSON handling.
- **Upgrade = the same command.** A re-run stops the task, copies, re-wires (pulls `zyggy-core`, refreshes changed skills with a backup) and restarts the task. The order across machines is unchanged: Central first (`deploy/README.md`).

---

## Risks (known and accepted)

- **The GitHub token from Git Credential Manager.** The `github-pat` a laptop holds may be an account-wide login taken from Git Credential Manager, not the per-machine, repository-scoped token §5 describes. The owner knows this and accepted it at the spec gate on 2026-11-04 (Q1). 42 changes nothing about it.

---

## Failure modes

| Situation | Observable outcome | Runbook entry (laptop section of `install-node.md` plus `node/README.md`) |
|---|---|---|
| Old copy found, its folder holds other files (or sits on the machine PATH) | Warning line; PATH unchanged; the run continues. | "Old zyggy still on PATH": move the old files out, or edit the user PATH by hand (`rundll32 sysdm.cpl,EditEnvironmentVariables`, no administrator needed). The machine PATH needs whoever administers that machine. |
| User PATH not writable | Warning `could not remove <dir> from the user PATH: <reason>`; the run continues. | same entry |
| Old node process cannot be stopped | `Fail` with the process id and a `Stop-Process` remedy. | "Old node holds port 4711" |
| `node.json` unreadable / not valid JSON / refused by the reader | Exit 3; file untouched; the reader's message. | "`node.json` refused by setup": restore from a `.bak-…`, or fix the named key. |
| `node.json` tenant or machine differs from the parameters | Exit 3; file untouched. | same entry: rerun with the file's values, or fix the file deliberately. |
| `signWith` absent and `-SignWith` not given | Exit 3 with the remedy. | same entry |
| No `claude.exe` found | `claude.path` not added or replaced; the existing pre-flight `Fail` "claude.path … does not exist". | the existing "claude.path" remedy, now naming the two places looked in |
| Signing key missing | Exit 4 → `Fail` before the task, with the exact `secret set` line. | the existing "signing key" remedy, plus a "Where the tenant key comes from" pointer to `rotate-hmac.md` |
| `github-pat` missing | Exit 6; the task still starts and polls by safety fetch every 15 minutes. | "GitHub token missing": `zyggy secret set github-pat` (hidden prompt). |
| Spike file store present | Info line only. | "Spike file store left on the laptop" → `rotate-hmac.md` step 0; delete the folder by hand. |
| `settings.json` not valid JSON / not writable | Exit 6; file untouched; the task still starts. | "Claude settings not valid JSON — left untouched": fix or restore the file, then rerun. |
| `ZYGGY_SECRET_PATTERNS` set to another file that is missing | Exit 6; left as is. | same entry: correct or remove the value, then rerun. |
| `zyggy-core` clone/pull fails (proxy, offline) | Exit 6, with git's first stderr line; the task still starts. | "zyggy-core clone fails behind the proxy": `git config --global http.proxy`, then rerun. |
| `--core-path` exists but is not a checkout, or has another origin | Exit 6; untouched. | "zyggy-core path taken": rerun with `-CorePath`, or move the folder. |
| `zyggy-core` dirty / on another branch / diverged | `left as is (<reason>)`; its files are used as they are. | "zyggy-core not updated": `git -C <core> status`, restore it, then rerun. |
| A skill folder differed | `done`, with the backup path. | none (expected on a template update) |
| Open terminals / VS Code / Claude Code sessions keep the old PATH or environment | The final reopen line. | "PATH or settings not seen": close and reopen. VS Code must be fully restarted (all windows). |
| Employer-managed Claude Code settings override the user settings | `zyggy context` still exits 3 after a clean `setup`. | "Context: configuration error" gains: check `/status` in Claude Code for managed settings; `setup` never writes managed files. |

---

## Dependencies

| Package | License | Why |
|---------|---------|-----|
| — (none) | — | `System.Text.Json.Nodes` (framework) does the JSON edits. `IProcessRunner` and the existing git patterns run git. `ISecretStore` and `SecretFingerprint` already exist. `TimeProvider` stamps the backups. Nothing is added to `Directory.Packages.props` (AC-23). |

Template precondition: settled as a fact (owner, 2026-11-04, Q4). `zyggy-core` `origin/main` holds `node/skills/remember` and `node/skills/archive` (PR #12 merged 2026-11-02). No precondition is left for the plan.

---

## Deliberate deviations from the founding spec

Each rests on an owner answer at the spec gate of 2026-11-04:

- **§10 Laptops:** the installer now also cleans old copies off the user PATH and completes `node.json` (`claude.path`, `signWith`). Through `zyggy setup` it clones `zyggy-core` and wires the user-scope settings and the skills. §10 currently says "`agent-core` is created by deliverable 15" (W42-1).
- **§3 / §9:** the verb lists gain `setup` (W42-2; Q5).
- **§10 Claude Code side:** laptops get the node skills by copy into the user scope, not via `discover --link` (W42-3).
- **§9 Versioning:** until 15, the laptop checkout follows `main` by fast-forward instead of a pinned SHA (W42-4; Q3).
- **§11:** `install-node.md` describes the one-command install and upgrade (W42-5).

The node `Stop`/`SessionEnd` hooks in §3 are unchanged: they stay installed by hand per `agent-core/node/README.md` (Q2). §5 is unchanged (Q1).

### Founding-spec wording approved with this spec (W42-n; the orchestrating session pastes it)

- **W42-1 — §10 Laptops, first bullet.** Replace the final sentence "Node state lives under `%LOCALAPPDATA%\zyggy` (`nodeDir`); `agent-core` is created by deliverable 15." with:
  "Node state lives under `%LOCALAPPDATA%\zyggy` (`nodeDir`). The same script upgrades a laptop from any earlier install in one run (deliverable 42). It takes an older `zyggy` folder off the **user** PATH when that folder holds nothing else (files kept). It completes an existing `node.json` with a timestamped backup: only `claude.path` and `signWith`, and it replaces a `claude.path` only when the file it names does not exist and a `claude.exe` was found. Then it calls `agentbus setup`. That verb clones or fast-forwards `agent-core` at `C:\agents\zyggy-core`. It merges `ZYGGY_SECRET_PATTERNS` and the deny rules into the user-scope `~/.claude/settings.json` (backup first, additive, refused when the file is not valid JSON). It installs the `remember` and `archive` node skills, and names every missing secret with the command that sets it, never a value. The node `Stop` and `SessionEnd` hooks stay a manual step (`agent-core/node/README.md`). A second run changes nothing."
- **W42-2 — §3 `AgentBus.Cli` row and §9 `AgentBus.Cli/` line.** Insert `setup` after `verify` in both lists. §3: "`… discover`, `verify`, `setup`, `dream` …". §9: "`… | discover | verify | setup | run | …`".
- **W42-3 — §10 Claude Code side, second bullet.** Replace "`agent-core` is checked out once per machine; projects symlink or copy the shared skills into `.claude/skills/` via `agentbus discover --link`." with:
  "`agent-core` is checked out once per machine. On a laptop, `agentbus setup` copies the node skills (`agent-core/node/skills/remember`, `archive`) into the user-scope `~/.claude/skills/` (deliverable 42). Projects symlink or copy the shared skills into `.claude/skills/` via `agentbus discover --link` (15)."
- **W42-4 — §9 Versioning and compatibility, second bullet.** Prepend "From deliverable 15:" and append "Until then a laptop's checkout follows `main` by fast-forward at each install (deliverable 42)."
- **W42-5 — §11 Runbooks, `install-node.md` bullet.** After "(`install.ps1` without administrator rights; a per-user scheduled task at logon)" insert "; the laptop install and every upgrade are one `install.ps1` run, with the manual hooks step and the manual wiring of `agent-core/node/README.md` as the fallback, and one entry per refusal `agentbus setup` can print".

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| Fresh laptop, nothing installed, bus not cloned | Step 5 `Fail` with the existing clone remedy (unchanged). `setup` never runs before the checkout exists. |
| Old copy folder earlier on the user PATH than the programs folder, and removable | Removed (AC-1). The programs folder is the only zyggy on PATH afterwards. |
| The same folder appears twice on the user PATH | Every occurrence is removed. |
| `node.json` with comments and a missing `claude.path` | Backup with comments, rewritten file without them; the line says "comments kept only in the backup". |
| `node.json` with `secrets.store: file` (a spike) | Not changed (an owner value). The existing file-store warning line plus AC-19's spike line. |
| `claude` found only as `claude.cmd` (npm) | Not used: only `claude.exe` is accepted. The pre-flight remedy applies. |
| `settings.json` with a UTF-8 BOM | Parsed, and written back without the BOM (Claude Code reads both). |
| `settings.json` is an empty file | Treated as `{}`, with a backup. |
| `settings.json` already holds the owner's hooks (or ours, wired by hand) | Untouched: `setup` never touches `hooks`. |
| A `deny` rule string differs from ours only in case or spacing | Treated as different, and ours is added (duplicates are harmless; deny never weakens). |
| `<claude-dir>/skills/remember` is the owner's own skill of that name | Moved to the backup folder and replaced; the line names the backup. With one owner, the template reserves the name. |
| Run from pwsh 7, which starts Windows PowerShell 5.1 | Covered by the bugfix baseline (`GetFolderPath`). |
| Two installs at once | Not guarded: one owner, one window. Backups are timestamped to the second, and the temp-then-replace write keeps each file whole. |

---

## Out of Scope

- Central's install and upgrade (stays hand-run per `install-node.md`). `setup` refuses `central`.
- Installing, changing or removing the node `Stop`/`SessionEnd` hooks (owner, 2026-11-04: they stay manual per `node/README.md` "Wiring").
- Any Git Credential Manager route for the GitHub token, any check of a token's kind or scope, and any change to the §5 credential rules (owner, 2026-11-04).
- `zyggy init`, join tokens, Docker/Bicep, and `agent-core` as a versioned package (§14, 26).
- Importing or migrating the spike file store. Deleting any old file, folder, backup or store.
- Cloning the private bus repository; issuing or rotating GitHub tokens.
- Owned by 15: `discover --link`, project-scope skills, `PreToolUse`, the verbatim `PROTOCOL.md`, `agentCore.pinnedSha`.
- DLP on context envelopes from the work laptop (19/20).
- Writing `workHours`, `devRoots`, `archive.*` or any other `node.json` key that has a default.
- Editing managed (enterprise) Claude Code settings.
- Linux or macOS laptops, beyond unit-level path defaults.
- Pester, or any test that runs `install.ps1`, a real `~/.claude`, the real `claude`, GitHub or the real Credential Manager.

---

## Open Questions

None. All six were answered at the spec gate; see below.

## Decisions taken at the spec gate (2026-11-04)

Owner's answer, verbatim: "1. no leave it like that 2. leave it like now 3. okay for main 4. okey 5 okay". Q6 was confirmed "yes" through the coordinator the same day.

| # | Question | Owner's answer (2026-11-04) | Effect on this spec |
|---|----------|-----------------------------|---------------------|
| Q1 | Offer a Git Credential Manager route for `github-pat` (fine-grained tokens only), plus a §5 token rule? | "no leave it like that": **rejected**. Today's behaviour stays. | No `--from-git`, no token-kind filter, no §5 wording (former W42-4 dropped). `setup` only names a missing `github-pat` with `zyggy secret set github-pat` and never reads Git Credential Manager (AC-19). The account-wide-token concern is listed under Risks as known and accepted. |
| Q2 | Node `Stop`/`SessionEnd` hooks: default, opt-in, or never on the work laptop? | "leave it like now": `setup` does not install, change or remove hooks. | No `-ContextHooks` switch; hooks stay manual per `node/README.md` "Wiring" (AC-13, AC-24, AC-27). Former W42-5 dropped. Scope item 6 keeps env, deny rules and skills. |
| Q3 | `zyggy-core` checkout: follow `main` or pin to the release? | "okay for main" | Fast-forward on `main` until 15 (AC-18, W42-4). |
| Q4 | Is the 12/41 template work on `zyggy-core` `main`? | "okey". Settled as a fact: `origin/main` holds `node/skills/archive` and `node/skills/remember` (PR #12 merged 2026-11-02). | No precondition for the plan. |
| Q5 | Name of the verb | "okay": **`zyggy setup`** | CLI surface, W42-2. |
| Q6 | May `setup` replace a `claude.path` that names a missing file? | **Yes**: only when a `claude.exe` is found, with a backup and an old→new line. | AC-12, W42-1. |
