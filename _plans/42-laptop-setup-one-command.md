# Plan: 42 — Laptop setup in one command — "As the owner, on either laptop, whatever was installed there before, I want one run of `install.ps1` from the unpacked release folder to leave the laptop ready as a node — the new `zyggy` first on PATH, `node.json` usable, each missing secret named with the exact command that sets it, the `zyggy-core` checkout, the user-scope Claude Code settings and the `remember`/`archive` skills in place, and the same run on an already-wired laptop changing nothing — so that `zyggy context` and `zyggy archive` work in the next laptop session with no hand step and every later release is one command"

> **Precondition (not a plan step).** The uncommitted installer bugfix of 2026-11-02 (`deploy/windows/install.ps1`: `-Source`/`-NodeJson` resolved in the body through `[Environment]::GetFolderPath`, `Get-Optional` for missing `node.json` properties, the status line = the first `node` line; `tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs`: the four regression facts) **must be committed by the owner before Step 1 starts** — the spec's baseline is that state, and Step 7 edits both files on top of it. The executor checks `git status --short deploy/windows/install.ps1 tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs` is empty before starting Step 1 and stops if it is not.
>
> **Scheduling note.** Branch `feature/42-laptop-setup-one-command` from `main` in `zyggy`; the sibling template repository `zyggy-core` (`D:\source\zyggy-core`) gets its own branch `feature/42-node-wiring`, **created by the owner**, for Step 9; the private instance repository `zyggy-geoffrey` (`D:\source\zyggy-geoffrey`) is edited on its main line in Step 9 (one runbook file). All three repositories are left **uncommitted** at every 🛑 HUMAN GATE for the owner's review (auto-memory "Owner reviews before commit"); the agent never commits, pushes or tags. The founding-spec wording W42-1..W42-5 was pasted by the orchestrating session on 2026-11-04 (AC-25) — the founding spec is current and is the contract this plan builds against.

## Overview

After this plan a laptop in any earlier state — nothing installed, a pre-installer spike copy in `%LOCALAPPDATA%\zyggy\bin`, or an older release — becomes a wired node with one `install.ps1` run, and every later release is the same one run. The script (PowerShell 5.1, rule R1 glue only) takes an older `zyggy` folder off the **user** PATH when it holds nothing but `zyggy*` files (files kept, never the machine PATH), stops an old-folder node that would hold port 4711, puts the programs folder first in its own session's `$env:Path`, and calls the new verb once: `zyggy setup --config <node.json> --tenant <org> --machine <name> [--sign-with <org>/<n>] --core-path <dir>`. The verb (`Zyggy.Core.Setup` + `src/Zyggy.Cli/Commands/SetupCommand.cs`) completes an existing `node.json` backup-first (only `claude.path` and `signWith`; a `claude.path` naming a missing file is replaced only when a `claude.exe` is found), clones or fast-forwards `zyggy-core` on `main` through `IProcessRunner` with `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`, merges `env.ZYGGY_SECRET_PATTERNS` and the three deny rules into the user-scope `settings.json` additively (backup first, invalid JSON left untouched, `hooks` never touched), copies the `remember` and `archive` node skills (a differing folder is moved to `<LocalAppData>/zyggy/backup/<stamp>/skills/<name>/` first), and names every missing secret with the exact `zyggy secret set` line — fingerprints and names only, never a value. One line per item, `setup: <item>: <outcome>[ - <detail>]`, exit 0 / 2 / 3 / 4 / 6 with priority 3 > 4 > 6; a second run prints only `already done` lines and rewrites nothing. The script branches on the exit code: 3 or 4 `Fail` before the task, 6 registers and starts the task and ends `install: incomplete - see the setup lines above` with exit 1. The node `Stop`/`SessionEnd` hooks stay a manual step (owner, 2026-11-04). The template (`zyggy-core` `node/README.md`, bats) and the public and private `install-node.md` are reduced to the one command plus the manual hooks step and the manual fallback, with one runbook entry per refusal.

Implements `_specs/42-laptop-setup-one-command.md` (owner approved 2026-11-04; zero Open Questions; Q1–Q6 binding: no Git Credential Manager route, hooks manual, `zyggy-core` follows `main` by fast-forward, template work is on `main`, verb name `setup`, a broken `claude.path` may be replaced). Founding-spec sections: §3 (Node instance, `setup` in the Cli verbs, Skills `remember`/`archive`, Hooks), §5 (credentials per machine), §8 (Secrets, Isolation, Work boundary), §9 design rules (`ISecretStore`, `IProcessRunner`, no static state, secrets never in `node.json`), §10 Laptops / Claude Code side / Upgrade path as amended by W42-1..W42-4, §11 `install-node.md` (W42-5), §13 MVP-scope decision.

**Reference pattern** (read across all layers; mirrored here): **deliverable 09's secret verbs** — `src/Zyggy.Core/Secrets/Verbs/SecretListVerb.cs` (a `public sealed class` Core verb over `ISecretStore` + `VerbIo`, `RowsAsync` reading `ListAsync` then `GetAsync` with `CryptographicOperations.ZeroMemory`, `SecretFingerprint.Of`, the `SecretStoreUnavailableException` → one line + `SecretVerbExit.StoreUnavailable`), `SecretVerbExit` (public exit constants in Core), `src/Zyggy.Cli/Commands/SecretCommand.cs` (`BusHost.LoadConfiguration` → `BusHost.BuildServices(configuration, verbose: false, environment, configure)` → `services.GetRequiredService<ISecretStore>()` → verb `RunAsync(…, io, ct)`), `tests/Zyggy.Integration/Cli/SecretListRemoveCommandTests.cs` (temp `node.json` over `secrets.store: file`, `SecretSweep.AssertAbsent`); **deliverable 10's installer** — `deploy/windows/install.ps1` (`Step`, `Fail`, `Get-Optional`, `Stop-Node`, `Invoke-Native`, the numbered steps, the template markers), `tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs` (static invariants over `Text`/`Code`, `ParamBlock()`, the counted `Invoke-Native` and `Stop-Node` lines), `DeployAssets`, `NodeJsonTemplateTests`; **deliverable 28/04's git** — `src/Zyggy.Core/Git/GitClient.cs` (`GitClientOptions.Environment`, `RunAsync(repository, arguments, stdin, environment, ct)`, `GIT_TERMINAL_PROMPT=0` always added, failures as `GitResult`, never exceptions), `tests/Zyggy.Core.Tests/Infrastructure/RecordingProcessRunner.cs` (`On(subCommand, …)`, `CallsOf`); **deliverable 08/12's CLI harness** — `tests/Zyggy.Integration/Cli/CliHarness.cs` (`ScratchDirectory`, `NodeJson.Write`, `TestHost.Hooks()` = `HookedProcessRunner`, `ZyggyCliInProcess.RunAsync(args, environment, configure, stdin, ct)` → `(Exit, VerbConsole)`, `GitHubBusOptions.GitEnvironment` as the git isolation hook), `tests/Zyggy.Integration/Infrastructure/MemoryRepoFixture.cs` (a local bare repository + clone with its own `GIT_CONFIG_GLOBAL`, identity `zyggy (test)`, `core.autocrlf false`); **deliverable 41's template step** — `_plans/41-archive-from-any-node.md` Step 13 (sibling-repo step shape, bats in the container, hygiene sweeps `hygiene_paths`/`hygiene_principal`/`hygiene_words` of `tests/repo.bats`); **the reader** — `src/Zyggy.Core/Configuration/NodeConfigurationReader.cs` (`ResolvePath` reading `LOCALAPPDATA`/`XDG_CONFIG_HOME`/`HOME` from the injected environment before `Environment.GetFolderPath`; `JsonDocumentOptions` with `CommentHandling = Skip`), `src/Zyggy.Core/Verbs/ProgramLocator.cs` (`OnPath`, `UserProgram`).

**Phase**: 42 does not close a §12 phase; no `Gates/P<n>_*.cs` file is added. Planner rule 15 is applied as in plan 41: the last slice carries the documentation, the runbook rows and the owner-run definition-of-done check (AC-26, AC-27) at its 🛑 HUMAN GATE.

**What this plan deliberately is not** (spec Out of Scope / Defer): no Git Credential Manager route and no token-kind check (Q1); no hook install, change or removal (Q2 — `hooks` is never written); no import or migration of the spike file store; **nothing deleted** (old binaries, old folders, backups, the spike store stay); no `zyggy init`, join token, Docker/Bicep; no clone of the private bus repository; no `agentCore.path`/`pinnedSha` in `node.json` (15); no `discover --link` (15); no change to Central's install (`setup` refuses `central`); no Pester and no test that runs `install.ps1`, touches the real `~/.claude`, the real `claude`, GitHub or the real Credential Manager; **no new NuGet package** (`Directory.Packages.props` unchanged — `SourceHygieneTests.Packages_RowCountPinned` stays at 24, AC-23); no `.claude/zyggy-min-version` bump in `zyggy-core` (the spec does not ask for one; the skills are unchanged).

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — `zyggy setup` completes `node.json` and names the secrets | 1, 2 | the setup contract exists: `node.json` is completed backup-first (`claude.path`, `signWith`), refused on mismatch / invalid JSON / `central`, every needed secret is named present-with-fingerprint or missing-with-command, the spike store is named, exit 0 / 2 / 3 / 4 / 6 by priority (1); `zyggy setup` runs in process on a temp profile with a file secret store and prints those lines, no secret value anywhere (2) | 🛑 after Step 2 (⚠️ secrets; ⚠️ owner file `node.json` rewritten; ⚠️ public API; line grammar R1) |
| B — `zyggy setup` clones or fast-forwards `zyggy-core` | 3, 4 | the five checkout states are handled through a recorded `IProcessRunner` with the two no-prompt variables on every git call (3); against a local bare `zyggy-core` stand-in the folder is cloned, a second run says `already up to date`, an unreachable remote / a taken folder / another origin exit 6 with git's first stderr line (4) | 🛑 after Step 4 (⚠️ work boundary: a public clone on the employer's laptop; git never prompts) |
| C — `zyggy setup` wires the user-scope Claude Code settings and the skills, and a second run changes nothing | 5, 6 | `settings.json` is created or merged additively (env + three deny rules, `hooks` untouched, invalid JSON left untouched, BOM/empty handled), the two skills are copied with a backup outside the skills directory, `--claude-dir`/`CLAUDE_CONFIG_DIR` honoured (5); the whole verb runs end to end on a temp profile + bare `zyggy-core`: first run wires everything, the second run prints only `already done` lines and rewrites nothing, `SecretSweep` over every stream and file (6) | 🛑 after Step 6 (⚠️ owner file `settings.json` that governs every Claude Code session; ⚠️ work boundary §8; ⚠️ secrets AC-22) |
| D — `install.ps1` upgrades a laptop from any earlier state in one run | 7 | the script removes an older `zyggy` folder from the user PATH (only when it holds only `zyggy*` files, never the machine PATH, nothing deleted), stops an old-folder node, puts the programs folder first in the session PATH, calls `zyggy setup` once by absolute path and branches on 3 / 4 / 6, prints the reopen line last; `-CorePath`; every spec-10 invariant still holds | 🛑 after Step 7 (⚠️ PATH change on the owner's machines; ⚠️ R1 shell budget; ⚠️ script cannot be run in tests) |
| E — Definition of done: documentation, template, runbooks, owner-run checks | 8, 9 | the Cli/Core/deploy READMEs carry `setup` and one row per failure mode, hygiene facts pin placeholders and the package count (8); `zyggy-core` `node/README.md` says `install.ps1` wires env, deny rules and skills while the hooks stay manual, bats updated, the public and private `install-node.md` reduced to the one command (9) | 🛑 after Step 9 — **definition of done** (owner-run AC-26, AC-27) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally; the `zyggy-core` side (Step 9) is proven by its bats suite in the local container (`MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "git config --global --add safe.directory /w; trap '' PIPE; bats tests/"`).

<!--
Decomposition strategy: vertical slices, not horizontal layers. Slice A proves the verb's contract (node.json completion, secret
report, exit codes) through ISecretStore (InMemorySecretStore) and a fake clock on temp files, then wires `zyggy setup` in process
with a file secret store. Slice B proves the zyggy-core checkout through RecordingProcessRunner, then wires real git against a
local bare stand-in. Slice C proves the settings merge and the skill copy on temp directories, then wires the whole verb end to
end (idempotence, secret sweep). Slice D is the PowerShell glue, proven statically as deliverable 10 did. Slice E is the
definition of done. Never the real claude, never GitHub, never a real PowerShell install, never the real ~/.claude in any test.
-->

---

## Shared rules for this plan

- **Identity in every test, fixture, README and runbook line**: tenant `acme`, user `alice`, machines `central` / `home-laptop` / `work-laptop`, key `acme/1` (`TestKeys.Acme1`, `TestKeys.Secret1`), the fake clock at `2026-11-04T09:30:00Z` (backup stamp `20261104T093000Z`) unless a test says otherwise, the test PAT `TestKeys.TestPat`. Placeholder paths only: `C:/agents/zyggy-core`, `C:/OneDrive - Example/...`, `<instance-dir>`, `<LocalAppData>`. No employer name, real user, host, token, fingerprint, GUID, `/Users/`, `C:\Users\`, `/home/` or `geoffrey` anywhere (`SourceHygieneTests`, `InstallScriptTests.Script_LfNoTrailingWhitespaceNoSecretNoIdentifier`, `zyggy-core` `repo.bats` hygiene sweeps, AC-28). Evidence with real paths goes only into the private instance runbook, and only by the owner (AC-26).
- **Every decision in Core, every rendering in Cli** (08/12's rule): `SetupCommand` reads options, resolves the three directories through `SetupPaths` (Core), builds the request, calls `SetupVerb.RunAsync`, returns its exit. No wording and no file logic is born in `Zyggy.Cli`.
- **The line grammar** (one place: `SetupLine.Render()`): `setup: <item words>: <outcome words>[ - <detail>]` on **stdout** (errors too — the installer's one `Invoke-Native` capture shows them in order). Item words: `node.json`, `zyggy-core`, `settings` (both `SettingsEnv` and `SettingsDeny`; the detail names `env.ZYGGY_SECRET_PATTERNS` or `permissions.deny`), `skill remember`, `skill archive`, `signing key <org>/<n>`, `github-pat`, `spike file store`. Outcome words: `done`, `already done`, `left as is`, `missing`, `failed`. No colours, no secret, `\n` endings.
- **Owner files are backup-first, additive and idempotent.** A backup `<file>.bak-<yyyyMMddTHHmmssZ>` is written only right before the file changes; writes go to `<file>.tmp-<stamp>` in the same directory then `File.Move(tmp, file, overwrite: true)`; JSON is re-serialised with two-space indentation (`JsonSerializerOptions { WriteIndented = true }`); a run that changes nothing writes nothing (content **and** last-write time unchanged, AC-21).
- **Nothing is deleted.** Not by the verb, not by the script: old binaries, old folders, the spike file store, replaced skills (moved to the backup folder), `.bak-` files.
- **Secrets**: `setup` reads names and fingerprints only (`ISecretStore.ListAsync` + `GetAsync`, the copy zeroed as `SecretListVerb` does). No value on stdout, stderr, a log line, a written file, a process argument or an environment variable; `SecretSweep.AssertAbsent` runs over every captured stream, every `HookedProcessRunner` call and every file the run wrote (AC-22). `setup` never reads Git Credential Manager or any other source.
- **Git never waits for a human under `setup`**: every git call carries `GIT_TERMINAL_PROMPT=0` (added by `GitClient`) and `GCM_INTERACTIVE=never` (`GitClientOptions.Environment`); the clone is anonymous. `setup` sets no proxy; git's own `http.proxy` serves it.
- **Paths**: no bus path is touched (the verb never opens the bus checkout); every setup path is built in one place, `src/Zyggy.Core/Setup/SetupPaths.cs`.
- **Data, never instructions**: the verb never calls `IModelRunner`, never reads `ZYGGY_HOOKS`, never shows a file to a model (asserted: no process named `claude` in the harness's calls).
- **Three repositories, nothing committed**; the executor reports per repository at each gate.

### Assumptions (reviewer: confirm or correct at the gate named)

- **A1 — ⚠️ Line grammar (Gate A).** The spec's Contracts fix `setup: <item words>: <outcome words>[ - <detail>]` while some AC rows quote shorter lines (`setup: node.json: added claude.path (backup <file>)`, `left as is (<value>)`, `present (fingerprint <fp>)`). This plan follows the Contracts grammar everywhere and puts the AC wording in the detail: `setup: node.json: done - added claude.path (backup <file>)`; `setup: node.json: done - claude.path <old> did not exist; set to <new> (backup <file>)`; `setup: node.json: already done`; `setup: settings: left as is - ZYGGY_SECRET_PATTERNS is <value>`; `setup: signing key acme/1: already done - present (fingerprint <fp>)`; `setup: signing key acme/1: missing - run: zyggy secret set hmac/1 --hex`; `setup: github-pat: missing - run: zyggy secret set github-pat`; `setup: zyggy-core: already done - already up to date (<path>)`. AC-21's "every line is `already done` or `already up to date`" then holds as "every line's outcome word is `already done`". The owner may swap the words at Gate A; only `SetupWire` and the test expectations change.
- **A2 — Order and stop rule (Gate A).** Items run and print in `SetupItem` order: `NodeJson`, `ZyggyCore`, `SettingsEnv`, `SettingsDeny`, `SkillRemember`, `SkillArchive`, `SecretSigningKey`, `SecretGitHubPat`, `SpikeFileStore`. `--machine central` → the one line `setup: node.json: failed - setup is for laptops; Central is installed by install-node.md`, exit 2, nothing read or written. A `NodeJson` failure (exit 3) **stops the run**: nothing else is touched (AC-9/AC-10 "file untouched, nothing touched" and the node cannot start anyway). From `ZyggyCore` on every item runs; the exit is the worst by priority (4 when the signing key is missing, else 6 when any item `Failed` or `github-pat` is `Missing`, else 0). The signing key named is the completed file's `signWith`.
- **A3 — `node.json` completion scope (Gate A).** Only `claude.path` and `signWith` are ever written; `secrets`, `workHours`, `devRoots`, `log`, `maxConcurrentJobs`, `archive` never (AC-11). `claude.path` is added when `claude` is absent, `claude` is an object without `path`, or `path` is `null`; it is replaced only when it names a file that does not exist **and** a `claude.exe` was found (`ClaudeLocator.Find`: `ProgramLocator.OnPath("claude.exe", environment)`, then `<UserProfile>/.local/bin/claude.exe`; only `claude.exe`, never `claude.cmd`). The written value is absolute with forward slashes. When nothing is found and `claude.path` is absent or broken: `setup: node.json: already done - claude.path not set; no claude.exe found (the installer's claude.path pre-flight names the remedy)` when the file otherwise changed nothing, and the script's existing pre-flight `Fail` catches it. Comments: detected by walking the bytes with a `Utf8JsonReader` whose `CommentHandling = Allow` and looking for `JsonTokenType.Comment`; when present the detail ends `; comments kept only in the backup`. Equality for AC-7 "every other property JSON-equal": `JsonNode.DeepEquals` after removing the two keys from both sides.
- **A4 — The secret store is built from the completed file (Gate A).** `SetupVerb` receives `Func<NodeConfiguration, ISecretStore> stores` — the Cli's factory builds `BusHost.BuildServices(configuration, verbose: false, environment, configure)` once the file is complete and disposes it at the end; tests pass `_ => inMemoryStore`. A `SecretStoreUnavailableException` from the store becomes `setup: signing key <id>: failed - secret store unavailable: <detail>` (and the same for `github-pat`), exit 6 — never a throw to the Cli.
- **A5 — Directories (Gate A).** `SetupPaths.ResolveClaudeDir(option, environment)` = `--claude-dir`, else `CLAUDE_CONFIG_DIR`, else `<UserProfile>/.claude`; `SetupPaths.ResolveUserProfile(environment)` = `USERPROFILE`, else `HOME`, else `Environment.GetFolderPath(UserProfile)`; `SetupPaths.ResolveLocalAppData(environment)` = `LOCALAPPDATA` (read on every OS, the same test hook `NodeConfigurationReader.ResolvePath` uses), else `Environment.GetFolderPath(LocalApplicationData)`; `SetupPaths.DefaultCorePath()` = `C:/agents/zyggy-core` on Windows, `<UserProfile>/agents/zyggy-core` elsewhere. Backups of skills: `SetupPaths.SkillBackup(localAppData, stamp, name)` = `<LocalAppData>/zyggy/backup/<stamp>/skills/<name>`; the spike store: `SetupPaths.SpikeStore(localAppData, tenant)` = `<LocalAppData>/zyggy/secrets/<tenant>` (the `FileSecretStore` default root, checked for existence only — nothing inside is read).
- **A6 — The checkout decision (Gate B).** `CoreCheckout.ApplyAsync(corePath, remote, ct)` in order: the folder is absent → `clone --branch main <remote> <corePath>` run in the parent directory (created when absent) → `done - cloned <remote> at <sha12>` (the sha from `rev-parse HEAD`). The folder exists → `rev-parse --show-toplevel` must succeed and equal `corePath` (else `failed - <corePath> is not a git checkout`) → `config --get remote.origin.url` must equal `remote` (else `failed - <corePath> has origin <url>, expected <remote>`) → `status --porcelain=v1 -z --untracked-files=all` non-empty → `left as is - dirty working copy` → `symbolic-ref --short -q HEAD` ≠ `main` → `left as is - on branch <b>` → `pull --ff-only origin main`: success with stdout containing `Already up to date` → `already done - already up to date (<corePath>)`; success otherwise → `done - fast-forwarded to <sha12>`; failure whose stderr mentions `fast-forward` or `divergent`/`diverging` → `left as is - diverged from origin/main`; any other failure → `failed - <first non-empty stderr line>` (a start failure: `failed - git could not be started`). The files are used as they are after `left as is`; after `failed` the settings env value still points at `<core>/.claude/hooks/secret-patterns.txt` and the skills copy runs only when `<core>/node/skills/<name>/` exists, else `skill <name>: failed - zyggy-core not available`.
- **A7 — The settings merge (Gate C).** The file is read as bytes; a UTF-8 BOM is stripped and never written back; an empty or whitespace-only file is `{}` (with a backup, since it changes); `JsonNode.Parse(…, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })` — a `JsonException`, a non-object root, an `env` that is not an object, or a `permissions.deny` that is not an array → `setup: settings: failed - not valid JSON; left untouched` (one line, exit 6, no deny line). `env.ZYGGY_SECRET_PATTERNS` absent → set to `<core>/.claude/hooks/secret-patterns.txt` (forward slashes); present and equal → `already done`; present and different → `left as is - ZYGGY_SECRET_PATTERNS is <value>` and, when that file does not exist, `failed - ZYGGY_SECRET_PATTERNS is <value>, which does not exist; left as is`. `permissions.deny`: ordinal set union, our three rules appended in order after the existing entries; a rule differing in case or spacing counts as different (ours is added). Every other key, value and array element keeps its value and order (`JsonObject` preserves insertion order). `hooks` is neither read for a decision nor written. One backup per run, before the first change; one write.
### Spec ambiguities resolved by this plan (reviewer: confirm at the gate named)

- **R1 — Line grammar (Gate A)** = A1: the Contracts grammar wins over the shorter AC quotes; the AC wording sits in the detail.
- **R2 — Stop rule (Gate A)** = A2: `central` and a `node.json` refusal stop the run before any other item; a missing secret never stops it (the lines for the other items are still useful to the owner).
- **R3 — Machine-PATH copies (Gate D).** AC-2 asks for a warning when an old copy "sits on the machine PATH", but `Script_WritesNothingMachineWide` forbids the literal `'Machine')` (reading the machine target would use it). The script therefore warns for any `$env:Path` entry holding `zyggy.exe` that is **not** in the user PATH — which is the machine PATH or the running session — with the AC-2 wording, and never reads or writes the `'Machine'` target.
- **R4 — Secret store construction (Gate A)** = A4: the verb completes `node.json` first and builds the store from the completed file through a factory, because the store kind is read from that file.
- **R5 — Skills when `zyggy-core` failed (Gate C)** = A6/A8: the skill items run only when `<core>/node/skills/<name>/` exists (a `left as is` checkout, or a failed pull over an existing clone), else `failed - zyggy-core not available`; the settings env value is written either way so a later successful run needs no second merge.
- **R6 — `pull --ff-only` and "diverged" (Gate B)** = A6: the spec's `pull --ff-only` is used as written; a diverged history is recognised from git's refusal text (`fast-forward`/`divergent`) rather than from a separate `rev-list` count.
- **R7 — `<LocalAppData>` on Linux (Gate A)** = A5: the `LOCALAPPDATA` variable is honoured on every OS as the test hook (the same hook `NodeConfigurationReader.ResolvePath` already uses), then `Environment.GetFolderPath(LocalApplicationData)` (`~/.local/share` on Linux, where `FileSecretStore`'s default root also lives).

- **A8 — The skill copy (Gate C).** `<core>/node/skills/<name>/` → `<claude-dir>/skills/<name>/`; "same bytes" = the same relative file set and SHA-256 per file (empty folders ignored); different or absent → when the target exists it is **moved** (`Directory.Move`, falling back to copy + delete-of-the-copied-source across volumes) to `SetupPaths.SkillBackup(localAppData, stamp, name)`, then the source tree is copied; the line is `done - copied from <source> (backup <backup>)` or `done - copied from <source>` when nothing existed.

---

## Step 1 — Complete an old `node.json` backup-first and name every needed secret: `claude.path` added or (when it names a missing file and a `claude.exe` is found) replaced, `signWith` added from `--sign-with` or refused, tenant/machine mismatch, invalid JSON and `central` refused with nothing touched, an already-complete file left alone with no write; the signing key and `github-pat` reported present-with-fingerprint or missing-with-the-command, the spike file store named; exit 0 / 2 / 3 / 4 / 6 by priority — all through `InMemorySecretStore`, a fake clock and temp directories

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Setup/SetupRequest.cs` *(create)*: `public sealed record SetupRequest(string ConfigPath, TenantId Tenant, MachineName Machine, KeyId? SignWith, string CorePath, Uri CoreRemote, string ClaudeDir, string UserProfile, string LocalAppData, DateTimeOffset Now)` with `public const string DefaultCoreRemote = "https://github.com/zyggy-org/zyggy-core.git";` and `public string BackupStamp => Now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);`.
- `src/Zyggy.Core/Setup/SetupItem.cs` *(create)*: `public enum SetupItem { NodeJson, ZyggyCore, SettingsEnv, SettingsDeny, SkillRemember, SkillArchive, SecretSigningKey, SecretGitHubPat, SpikeFileStore }`.
- `src/Zyggy.Core/Setup/SetupOutcome.cs` *(create)*: `public enum SetupOutcome { Done, AlreadyDone, LeftAsIs, Missing, Failed }`.
- `src/Zyggy.Core/Setup/SetupLine.cs` *(create)*: `public sealed record SetupLine(SetupItem Item, SetupOutcome Outcome, string? Detail)` + `public string Render(KeyId? signWith)` (A1 grammar; the signing-key item words carry the key id).
- `src/Zyggy.Core/Setup/SetupWire.cs` *(create)*: `internal static class SetupWire { ItemWords(SetupItem, KeyId?); OutcomeWords(SetupOutcome); }`.
- `src/Zyggy.Core/Setup/SetupExit.cs` *(create)*: `public static class SetupExit { Ok = 0; Usage = 2; NodeConfig = 3; SigningKeyMissing = 4; Incomplete = 6; public static int Worst(IReadOnlyList<SetupLine> lines) }` (priority 3 > 4 > 6 > 0; `SigningKeyMissing` when `SecretSigningKey` is `Missing`; `Incomplete` when any line is `Failed` or `SecretGitHubPat` is `Missing`).
- `src/Zyggy.Core/Setup/SetupPaths.cs` *(create)*: A5's resolvers (`ResolveClaudeDir`, `ResolveUserProfile`, `ResolveLocalAppData`, `DefaultCorePath`, `SkillBackup`, `SpikeStore`, `Backup(file, stamp)`, `Temp(file, stamp)`, `SecretPatterns(corePath)`, `SkillSource(corePath, name)`, `SkillTarget(claudeDir, name)`, `SettingsFile(claudeDir)`).
- `src/Zyggy.Core/Setup/ClaudeLocator.cs` *(create)*: `internal static string? Find(IReadOnlyDictionary<string, string?> environment, string userProfile)` (A3).
- `src/Zyggy.Core/Setup/NodeJsonCompletion.cs` *(create)*: `internal static class NodeJsonCompletion { public static NodeJsonResult Apply(SetupRequest request, string? claudeExe); }` with `internal sealed record NodeJsonResult(SetupLine Line, NodeConfiguration? Configuration)` — read bytes → comment scan (A3) → `JsonNode.Parse` (`JsonException` → `failed - node.json is not valid JSON: <message>`) → `tenant`/`machine` compared with the request (`failed - node.json names tenant <t> and machine <m>; the installer was given <T> and <M>`) → the two completions → nothing changed: `NodeConfigurationReader.Read(path)` must accept it (`failed - <reader message>`), `already done` → changed: backup, temp write, replace, re-read through `NodeConfigurationReader.Read` (a refusal after the write: restore the backup over the file, `failed - <reader message>`), `done - <what> (backup <file>)[; comments kept only in the backup]`.
- `src/Zyggy.Core/Setup/SecretReport.cs` *(create)*: `internal static class SecretReport { public static Task<IReadOnlyList<SetupLine>> ReportAsync(ISecretStore store, SetupRequest request, KeyId signWith, SecretStoreKind storeKind, CancellationToken ct); }` — `ListAsync` once, `GetAsync` per needed name for the fingerprint (zeroed), the three lines (A1), the spike line only when `SetupPaths.SpikeStore` exists and `storeKind == Os`: `setup: spike file store: left as is - a spike file store exists at <dir>; its keys are retired (rotate-hmac.md step 0) and not imported`.
- `src/Zyggy.Core/Setup/SetupVerb.cs` *(create)*: `public sealed class SetupVerb(IProcessRunner processes, TimeProvider clock, IReadOnlyDictionary<string, string?> environment, IReadOnlyDictionary<string, string?> gitEnvironment, Func<NodeConfiguration, ISecretStore> stores)` with `public async Task<int> RunAsync(SetupRequest request, VerbIo io, CancellationToken ct)` — A2's order; in this step the `ZyggyCore`, `Settings*` and `Skill*` items are **not yet implemented** (they print nothing); Steps 3 and 5 add them.
- `src/Zyggy.Core/README.md` *(modify)*: a `Setup/` folder row (the public types above).
- `tests/Zyggy.Core.Tests/Setup/SetupTestFiles.cs` *(create)*: a scratch-directory helper that writes a `node.json` from a JSON string, a fake `claude.exe` file (executable bit on Linux) in a `bin/` folder, and builds a `SetupRequest` for `acme`/`home-laptop` with the fake clock.
- `tests/Zyggy.Core.Tests/Setup/NodeJsonCompletionTests.cs`, `SecretReportTests.cs`, `SetupVerbTests.cs`, `SetupExitTests.cs`, `SetupPathsTests.cs`, `SetupLineTests.cs`, `ClaudeLocatorTests.cs` *(create)*.

**Seams**: `ISecretStore` faked (`InMemorySecretStore`, `ThrowingSecretStore` for the unavailable case); `TimeProvider` faked (`FakeTimeProvider` at `2026-11-04T09:30:00Z`); `IProcessRunner` = `RecordingProcessRunner` (no git call may happen in this step: `Calls` empty); the file system under a temp directory.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Setup/NodeJsonCompletionTests.cs`
  - `Apply_NoClaudeSection_ClaudeExeOnPath_BackupWrittenPathAddedForwardSlashes_EveryOtherPropertyJsonEqual` (AC-7: `node.json.bak-20261104T093000Z` exists with the original bytes; `claude.path` absolute with `/`; `JsonNode.DeepEquals` over the rest; line `done - added claude.path (backup …)`)
  - `Apply_NoClaudeSection_ClaudeExeOnlyInUserProfileLocalBin_Added` (AC-7, the second place)
  - `Apply_NoClaudeSection_OnlyClaudeCmdFound_NotAdded_AlreadyDoneWithTheRemedyDetail` (Edge Cases: `claude.cmd` is not accepted)
  - `Apply_NoSignWith_OptionGiven_AddedWithBackup` / `Apply_NoSignWith_NoOption_FailedExitThreeRemedy_FileUntouchedNoBackup` (AC-8: detail `signWith missing; run install.ps1 again with -SignWith acme/<n>`)
  - `Apply_TenantOrMachineDiffers_FailedBothValuesNamed_FileUntouchedNoBackup` (`[Theory]`: tenant, machine; AC-9)
  - `Apply_NotValidJson_FailedReaderMessage_FileUntouchedNoBackup` / `Apply_ReaderRefusesAfterCompletion_FailedReaderMessage_BackupRestored` (AC-10: e.g. a `bus` section missing → the reader's `bus section missing`)
  - `Apply_AlreadyComplete_AlreadyDone_NoWriteNoBackup_LastWriteTimeUnchanged` (AC-11)
  - `Apply_DefaultedKeysAbsent_NeverAdded` (AC-11: no `secrets`, `workHours`, `devRoots`, `log`, `maxConcurrentJobs`, `archive` after completion)
  - `Apply_ClaudePathNamesMissingFile_ClaudeExeFound_ReplacedWithOldAndNewInTheLine` / `Apply_ClaudePathNamesMissingFile_NoneFound_LeftAsIs` (AC-12, Q6)
  - `Apply_FileWithComments_BackupKeepsThem_RewrittenWithout_LineSaysSo` (Edge Cases)
  - `Apply_SecretsStoreFile_NotChanged` (Edge Cases: an owner value)
- Unit test file: `tests/Zyggy.Core.Tests/Setup/SecretReportTests.cs`
  - `ReportAsync_BothPresent_TwoAlreadyDoneLinesWithFingerprints_NoSpikeLine` (AC-19; fingerprints equal `SecretFingerprint.Of(TestKeys.Secret1)` / of the PAT)
  - `ReportAsync_SigningKeyMissing_MissingLineNamesSecretSetHmacNHex` / `ReportAsync_OnlyPatMissing_MissingLineNamesSecretSetGithubPat` (AC-19)
  - `ReportAsync_SpikeFolderPresentAndStoreOs_OneInfoLine_NothingReadFromIt` (AC-19: the folder holds a file whose content is a needle; `SecretSweep` over the lines; the file's last-access-independent proof: its bytes are a needle and never appear)
  - `ReportAsync_SpikeFolderPresentAndStoreFile_NoSpikeLine`
  - `ReportAsync_StoreUnavailable_FailedLinesWithDetail_NoThrow` (A4)
  - `ReportAsync_NoValueInAnyLine_SecretSweep` (AC-22)
- Unit test file: `tests/Zyggy.Core.Tests/Setup/SetupVerbTests.cs`
  - `RunAsync_MachineCentral_ExitTwoOneLine_NothingReadOrWritten` (AC-20)
  - `RunAsync_NodeJsonRefused_ExitThree_StopsBeforeSecrets_StoreNeverAsked` (A2)
  - `RunAsync_CompleteFileSigningKeyMissing_ExitFour_LinesInItemOrder` (priority)
  - `RunAsync_OnlyPatMissing_ExitSix` / `RunAsync_AllPresent_ExitZero_NoGitCallInThisStep`
  - `RunAsync_SigningKeyNamedIsTheCompletedFilesSignWith` (`--sign-with acme/2` added → `signing key acme/2`)
- Unit test files: `SetupExitTests.cs` (`Worst_` theory over the combinations: 3 beats 4 beats 6 beats 0), `SetupLineTests.cs` (`Render_EveryItemAndOutcome_Grammar` theory, A1), `SetupPathsTests.cs` (`ResolveClaudeDir_OptionThenVariableThenProfile`, `ResolveLocalAppData_VariableThenFolder`, `DefaultCorePath_WindowsCAgents_ElseProfileAgents`, `Backup_StampSuffix`), `ClaudeLocatorTests.cs` (`Find_OnPath`, `Find_UserProfileLocalBin`, `Find_None_Null`, `Find_ClaudeCmdOnly_Null`).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Zyggy.Core.Tests.Setup"`

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- The files above. `SetupVerb` prints each line as it is produced (`io.Out.WriteLineAsync(line.Render(signWith))`), collects them, returns `SetupExit.Worst(lines)`.

**Contract impact**: ⚠️ **public API** (`Zyggy.Core.Setup`: `SetupRequest`, `SetupItem`, `SetupOutcome`, `SetupLine`, `SetupExit`, `SetupVerb` — `.claude/skills/add-public-api` applies: XML docs on every public member). ⚠️ **`node.json` as an owner file is rewritten** (only `claude.path`/`signWith`, backup first; no schema key added). ⚠️ **secrets** (names and fingerprints only). No §4–§7 contract changes.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`); the named tests pass; `NodeConfigurationReaderTests`, `NodeJsonTemplateTests` pass unedited; `SourceHygieneTests.Packages_RowCountPinned` still 24.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — `zyggy setup --config … --tenant acme --machine home-laptop [--sign-with acme/<n>] [--core-path …] [--claude-dir …] [--core-remote …]` runs in process on a temp profile with a file secret store: the `node.json` and secret lines of Step 1 appear on stdout in order, the exit code is the verb's, `--machine central` exits 2, a missing `--tenant` is a usage error, the three directories resolve from the options and the environment, and no secret value reaches any stream, file or process argument

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/SetupCommand.cs` *(create)*: options `--config` (`BusHost.ConfigOption`), `--tenant` (required), `--machine` (required), `--sign-with`, `--core-path` (default `SetupPaths.DefaultCorePath()`), `--claude-dir`, `--core-remote` (default `SetupRequest.DefaultCoreRemote`, description "for tests and forks"); refuses a bad tenant/machine/key id with `zyggy: <option>: <detail>` on stderr, exit 2 (the one Cli-side check, shape only, as `SecretCommand` checks `SecretName.TryParse`); builds the `SetupRequest` (`ConfigPath` = `NodeConfigurationReader.ResolvePath(--config, environment.Variables)`; the directories through `SetupPaths`; `Now` from `TimeProvider.System` or the configured provider); A4's store factory over `BusHost.BuildServices`; the git environment from `IOptions<GitHubBusOptions>.Value.GitEnvironment` of a throw-away provider built with `configure` (the harness's isolation hook; empty in production); `IProcessRunner` from that provider (the harness's `HookedProcessRunner`); returns `SetupVerb.RunAsync`.
- `src/Zyggy.Cli/CliApplication.cs` *(modify)*: `SetupCommand.Create(environment, io, configure)` after `VerifyCommand` (W42-2 order: `… verify, setup …`).
- `tests/Zyggy.Integration/Setup/SetupHarness.cs` *(create)*: `internal sealed class SetupHarness : IAsyncDisposable` — `ScratchDirectory` with `profile/` (`USERPROFILE`/`HOME`), `profile/.claude/` or a separate `claude-config/` for `CLAUDE_CONFIG_DIR`, `localappdata/` (`LOCALAPPDATA`), `bin/claude.exe` (an empty file, executable bit on Linux) on a `PATH` of only that folder, `node.json` written by `NodeJson.Write(..., secretsStore: "file", secretsRoot: …)` or from a raw JSON string (the old-spike shapes), a file secret store seeded with `acme/1` and the PAT (as `CliHarness.CreateAsync` does), `TestHost.Hooks()`; `RunAsync(string[] args, …)` → `ZyggyCliInProcess.RunAsync` with that environment and `Configure` (git environment + the recorded runner); `WrittenFiles()` (every file under the scratch root with content and `LastWriteTimeUtc`) and `SweepAsync()` (`SecretSweep.AssertAbsent` over stdout, stderr, every `Hooks.Calls` spec text and every written file).
- `tests/Zyggy.Integration/Setup/SetupCommandTests.cs` *(create)*.
- `src/Zyggy.Cli/README.md` *(modify)*: the `setup` verb row (options, lines, exit codes); the troubleshooting rows come in Step 8.

**Seams**: `ISecretStore` wired (the file store over the scratch root — the P0 store, never the Credential Manager); `IProcessRunner` = `HookedProcessRunner` (nothing is run in this step; `Calls` empty); `TimeProvider` real (the backup stamp is asserted by pattern `\.bak-\d{8}T\d{6}Z$`).

**RED**:
- Integration test file: `tests/Zyggy.Integration/Setup/SetupCommandTests.cs`:
  - `Setup_OldSpikeNodeJsonWithoutClaudeOrSignWith_SignWithGiven_CompletedWithBackup_SecretLinesPresent_ExitZeroOrSix` (happy path: the two `node.json` keys added, backup beside it, `signing key acme/1: already done - present (fingerprint <fp of Secret1>)`, `github-pat: already done - present …`; exit 0 in this step because no other item exists yet — the assertion is "exit is `SetupExit.Ok`" and is revisited in Step 6)
  - `Setup_SignWithAbsentAndNotGiven_ExitThree_FileUntouched` (failure path, AC-8)
  - `Setup_TenantDiffers_ExitThree_BothValuesNamed` (AC-9)
  - `Setup_MachineCentral_ExitTwo_OneLine` (AC-20)
  - `Setup_SigningKeyMissingFromStore_ExitFour_LineNamesSecretSetHmac1Hex` (AC-19; the store holds only the PAT)
  - `Setup_MissingTenantOption_UsageExitTwo` (System.CommandLine's own error line, exit 2)
  - `Setup_NoSecretValueAnywhere_Sweep` (AC-22 over the happy path)
  - Harness: `SetupHarness` (temp profile; no bare repo yet).
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SetupCommandTests"`

**GREEN**:
- The files above. `CliApplication` registration only; `RawVerbs` unchanged.

**Contract impact**: **CLI surface** `zyggy setup` (spec Contracts; W42-2 verb list). No `node.json` schema key; no package.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `zyggy setup --help` (in process, `ZyggyCliTests` style) lists the seven options; `SourceHygieneTests.Src_Cli_NoProcessHttpClientGitHubClaudeOrSecretStoreReference` still green (the Cli names no store or process type).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A: `zyggy setup` completes `node.json` and names the secrets *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `NodeJsonCompletionTests`, `SecretReportTests`, `SetupVerbTests`, `SetupExitTests`, `SetupLineTests`, `SetupPathsTests`, `ClaudeLocatorTests`, `SetupCommandTests` green; the executor shows one real stdout of `zyggy setup` on the harness's old-spike `node.json` (the exact lines) and the `.bak-` file beside it.
- [x] Contract review: the line grammar (A1) and the owner's choice of words; the order and stop rule (A2); the completion scope (A3, AC-7..AC-12 one by one); the exit priority 3 > 4 > 6 (Contracts); the option list and defaults (CLI surface); the store factory (A4); the directory resolution (A5, AC-16). *(Owner approved 2026-11-04 with the A1 wording as built; reader refusal checks the completed temp file first, so a refusal leaves no backup — AC-10 literally.)*
- [x] ⚠️ Risk review: no secret value in any stream, file or argument (`Setup_NoSecretValueAnywhere_Sweep`, `ReportAsync_NoValueInAnyLine_SecretSweep`); `node.json` rewritten only with a backup and only the two keys; nothing deleted; the new public types carry XML docs; nothing committed in any repository.
- [x] User approved — implementation may continue past this gate

---

## Step 3 — Clone or fast-forward `zyggy-core` at `--core-path` through a recorded `IProcessRunner`: absent → `clone --branch main`, clean on `main` → `pull --ff-only` (`already up to date` or `fast-forwarded`), dirty / another branch / diverged → `left as is` with the reason, a folder that is not a checkout or has another `origin` → `failed` exit 6, an unreachable remote → `failed` with git's first stderr line; every git call carries `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never` and runs in the right working directory

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Setup/CoreCheckout.cs` *(create)*: `internal sealed class CoreCheckout(GitClient git)` with `public Task<SetupLine> ApplyAsync(string corePath, Uri remote, CancellationToken ct)` (A6); the `GitClient` is built by `SetupVerb` as `new GitClient(processes, new GitClientOptions { Environment = gitEnvironment + ["GCM_INTERACTIVE"] = "never", Timeout = TimeSpan.FromMinutes(5) }, clock)`.
- `src/Zyggy.Core/Setup/SetupVerb.cs` *(modify)*: the `ZyggyCore` item after `NodeJson` (A2).
- `tests/Zyggy.Core.Tests/Setup/CoreCheckoutTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Setup/SetupVerbTests.cs` *(modify — the core line now appears; the "no git call" test becomes "git called only for zyggy-core")*.

**Seams**: `IProcessRunner` faked (`RecordingProcessRunner.On("clone" | "rev-parse" | "config" | "status" | "symbolic-ref" | "pull", …)`); `TimeProvider` faked.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Setup/CoreCheckoutTests.cs`
  - `ApplyAsync_FolderAbsent_CloneBranchMainInParent_DoneWithSha` (arguments exactly `clone --branch main <remote> <corePath>`, `WorkingDirectory` = the parent, which is created)
  - `ApplyAsync_CleanOnMain_PullFfOnly_AlreadyUpToDate_AlreadyDone` / `ApplyAsync_CleanOnMain_PullFfOnly_Advanced_DoneFastForwarded`
  - `ApplyAsync_Dirty_LeftAsIsDirty_NoPull` / `ApplyAsync_OnAnotherBranch_LeftAsIsBranchNamed_NoPull` / `ApplyAsync_Diverged_LeftAsIsDiverged` (`[Theory]` over the three reasons; `CallsOf("pull")` empty for the first two)
  - `ApplyAsync_NotACheckout_FailedUntouched` / `ApplyAsync_OtherOrigin_FailedBothUrlsNamed` (AC-18; no `clone`, no `pull`)
  - `ApplyAsync_RemoteUnreachable_FailedFirstStderrLine` (`clone` → `Fail(128, "fatal: unable to access 'https://…': Could not resolve host\n")`; the detail is that first line only) and `ApplyAsync_GitCannotStart_FailedCouldNotBeStarted`
  - `ApplyAsync_EveryGitCall_NoPromptVariablesAndNoCredentialSource` (every `Calls` spec: `Environment["GIT_TERMINAL_PROMPT"] == "0"`, `Environment["GCM_INTERACTIVE"] == "never"`, no `GIT_ASKPASS`, no argument containing `credential`; AC-18)
- Unit test file: `tests/Zyggy.Core.Tests/Setup/SetupVerbTests.cs` (append): `RunAsync_CoreFailed_ExitSix_SecretsStillReported`, `RunAsync_LinesInItemOrder_NodeJsonThenCoreThenSecrets`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~CoreCheckoutTests|FullyQualifiedName~SetupVerbTests"`

**GREEN**:
- The files above. No `GitClient` method is changed; `CoreCheckout` uses `RunAsync(repository, arguments, null, ct)` for `clone` (repository = the parent) and the existing `RevParseAsync`, `ConfigGetAsync`, `StatusPorcelainAsync`, `CurrentBranchAsync`, plus `RunAsync(corePath, ["pull", "--ff-only", "origin", "main"], null, ct)`.

**Contract impact**: none (§9: git only through `IProcessRunner`, never a shell, never a prompt). ⚠️ **work boundary**: a public-repository clone on the employer's laptop (spec Behaviors; reviewed at Gate B).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `GitClientTests` pass unedited; `SourceHygieneTests.Src_GitHubLiterals_OnlyInGitHubBusProvider` still green (the `zyggy-core` URL literal lives only in `SetupRequest.DefaultCoreRemote` — add that file to the test's allowed list if the literal rule catches `github.com`).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — Against a local bare `zyggy-core` stand-in (`--core-remote <bare path>`), `zyggy setup` clones the template into the temp profile's `agents/zyggy-core`, a second run says `already up to date`, a commit pushed to the stand-in is fast-forwarded by the next run, a dirty checkout is left as is, and an unreachable remote, a taken folder and another `origin` end in exit 6 with git's first stderr line — real git, no prompt, no credential helper

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/CoreTemplateFixture.cs` *(create)*: the `MemoryRepoFixture` shape — a bare `core.git` + a seeding clone with its own `GIT_CONFIG_GLOBAL`, identity `zyggy (test)`, `core.autocrlf false`; `SeedAsync` commits and pushes `node/skills/remember/SKILL.md`, `node/skills/archive/SKILL.md` (front matter `name:` + two placeholder lines), `.claude/hooks/secret-patterns.txt` (three synthetic pattern lines), `node/README.md` (one line); `PushChangeAsync(relativePath, content)` for the fast-forward case; `GitEnvironment` (the isolation variables) exposed for `SetupHarness.Configure`.
- `tests/Zyggy.Integration/Setup/SetupHarness.cs` *(modify)*: owns a `CoreTemplateFixture`; `CoreRemote` = the bare path as a `file://` URI or absolute path; `CorePath` = `profile/agents/zyggy-core`.
- `tests/Zyggy.Integration/Setup/SetupCoreCheckoutTests.cs` *(create)*.

**Seams**: `IProcessRunner` wired (real git via `HookedProcessRunner`, which records); `ISecretStore` wired (file store); `TimeProvider` real.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Setup/SetupCoreCheckoutTests.cs`:
  - `Setup_CoreAbsent_ClonedOnMain_SkillsAndPatternsPresent_LineDoneCloned` (happy path: `git -C <core> symbolic-ref --short HEAD` = `main`, `config --get remote.origin.url` = the remote, the three seeded files exist)
  - `Setup_SecondRun_AlreadyUpToDate_NoNewCommit_NoFileRewritten` (AC-18/AC-21 for this item)
  - `Setup_RemoteAdvanced_FastForwarded_NewFileAppears`
  - `Setup_CoreDirty_LeftAsIsDirty_FileKept_ExitZero` (a local edit in the clone survives)
  - `Setup_RemoteUnreachable_ExitSix_FirstStderrLineInTheDetail` (failure path: `--core-remote` = a non-existent path)
  - `Setup_CorePathIsAPlainFolder_ExitSix_Untouched` / `Setup_CorePathHasAnotherOrigin_ExitSix_Untouched`
  - `Setup_EveryGitCall_NoPromptVariables_NoClaudeProcess` (over `Hooks.Calls`: `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never` on every `git` spec; no spec whose file name contains `claude`)
  - Harness: `SetupHarness` + `CoreTemplateFixture` (local bare repository; never GitHub).
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SetupCoreCheckoutTests"`

**GREEN**:
- Only what the integration tests expose (expected: the `file://`/absolute-path remote comparison in `CoreCheckout` — compare `config --get remote.origin.url` with the request's remote **as git stores it**: trimmed, ordinal; the test passes the same string it gave `--core-remote`).

**Contract impact**: none beyond Step 3.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `SetupCommandTests` of Step 2 still green (their `--core-path` now points at the harness's path and the core line is part of the expected output).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B: `zyggy setup` clones or fast-forwards `zyggy-core` *(covers Steps 3–4)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `CoreCheckoutTests`, `SetupCoreCheckoutTests` green; the executor shows the recorded git argument vectors of one clone run and one second run.
- [x] Contract review: the five states and their lines (A6 against AC-18); `main` by fast-forward only (Q3, W42-4); `--core-remote` documented as "for tests and forks"; the default `--core-path` per OS. *(Owner approved 2026-11-04: up-to-date by HEAD comparison, checkout identity by `--show-prefix`, an empty folder is cloned into, `LC_ALL=C` added to the setup git environment so the diverged text is always English.)*
- [x] ⚠️ Risk review: every git call carries the two no-prompt variables and no credential source (§5/§8: a public clone needs none); no proxy set by `setup`; a `left as is` checkout is never reset or stashed; nothing committed in any repository.
- [x] User approved — implementation may continue past this gate

---

## Step 5 — Wire the user-scope Claude Code settings and the two skills on temp directories: `settings.json` created with exactly `env.ZYGGY_SECRET_PATTERNS` and the three deny rules when absent, merged additively with a backup when present (existing keys, values and order kept, `deny`/`allow` never shortened, `hooks` never touched, an existing other `ZYGGY_SECRET_PATTERNS` left as is and failed only when its file is missing, invalid JSON / empty file / BOM handled), `remember` and `archive` copied from `<core>/node/skills/` with a differing folder moved to `<LocalAppData>/zyggy/backup/<stamp>/skills/<name>/` first; `--claude-dir` / `CLAUDE_CONFIG_DIR` decide the target

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Setup/ClaudeSettingsMerge.cs` *(create)*: `internal static class ClaudeSettingsMerge { public static IReadOnlyList<SetupLine> Apply(string settingsFile, string patternsFile, string stamp); public static readonly string[] DenyRules = ["Bash(zyggy secret*)", "Bash(zyggy context --stop-hook*)", "Bash(zyggy context --flush*)"]; }` (A7).
- `src/Zyggy.Core/Setup/SkillInstaller.cs` *(create)*: `internal static class SkillInstaller { public static SetupLine Apply(SetupItem item, string source, string target, string backup); internal static bool SameBytes(string a, string b); }` (A8).
- `src/Zyggy.Core/Setup/SetupVerb.cs` *(modify)*: the `SettingsEnv`, `SettingsDeny`, `SkillRemember`, `SkillArchive` items between `ZyggyCore` and the secrets (A2); the skill source exists check (A6's last sentence).
- `tests/Zyggy.Core.Tests/Setup/ClaudeSettingsMergeTests.cs`, `SkillInstallerTests.cs` *(create)*; `SetupVerbTests.cs` *(modify — full item order; `CLAUDE_CONFIG_DIR` vs `--claude-dir`)*.

**Seams**: none beyond the file system under temp directories; `TimeProvider` faked for the stamp; `ISecretStore` faked; `IProcessRunner` recorded.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Setup/ClaudeSettingsMergeTests.cs`
  - `Apply_FileAbsent_CreatedWithExactlyEnvAndThreeDenyRules_TwoDoneLines_NoBackup` (AC-13: the written JSON parsed and compared key by key; two-space indentation; no BOM)
  - `Apply_FilePresent_BackupWritten_ExistingKeysValuesAndOrderKept_EnvAdded_DenyUnioned` (AC-13: a settings file with `model`, `env.OTHER`, `permissions.allow`, `permissions.deny` holding one of ours and one foreign rule, `hooks` with the owner's entries — after the merge the property order is unchanged, `allow` identical, `deny` = existing + the two missing in our order, `hooks` byte-identical as a sub-tree)
  - `Apply_HooksNeverAddedChangedOrRemoved` (`[Theory]`: no `hooks` → still none; owner hooks → identical)
  - `Apply_EnvAlreadyOurs_DenyAlreadyPresent_AlreadyDone_NoWriteNoBackup_LastWriteTimeUnchanged` (AC-21 for this item)
  - `Apply_EnvOtherValueWhoseFileExists_LeftAsIs_ExitZeroOutcome` / `Apply_EnvOtherValueWhoseFileIsMissing_Failed` (AC-14)
  - `Apply_NotValidJson_OneFailedLine_FileUntouchedNoBackup` / `Apply_RootNotAnObject_Failed` / `Apply_NotWritable_FailedWithWriteError_BackupNotLeftBehind` (AC-15; the unwritable case through a read-only file attribute on Windows / mode 0444 on Linux)
  - `Apply_EmptyFile_TreatedAsEmptyObject_WithBackup` / `Apply_Utf8Bom_ParsedAndWrittenWithoutBom` (Edge Cases)
  - `Apply_DenyRuleDifferingInCaseOrSpacing_OursAdded` (Edge Cases)
- Unit test file: `tests/Zyggy.Core.Tests/Setup/SkillInstallerTests.cs`
  - `Apply_TargetAbsent_Copied_DoneLineNamesSource` / `Apply_SameBytes_AlreadyDone_NothingWritten_LastWriteTimesUnchanged` / `Apply_DifferentBytes_ExistingMovedToBackupOutsideSkillsDir_ThenReplaced_LineNamesBackup` (AC-17: the backup path starts with `<LocalAppData>/zyggy/backup/<stamp>/skills/` and nothing named `remember` remains under `<claude-dir>/skills/` but the new copy)
  - `Apply_SourceMissing_FailedZyggyCoreNotAvailable`
  - `SameBytes_ExtraFileInTarget_Different` / `SameBytes_IdenticalTrees_True`
- Unit test file: `tests/Zyggy.Core.Tests/Setup/SetupVerbTests.cs` (append): `RunAsync_AllItems_LinesInEnumOrder`, `RunAsync_ClaudeDirOption_WinsOverVariable_WinsOverProfile` (AC-16; the settings file and skills land in the chosen directory), `RunAsync_CoreFailedAndAbsent_SkillsFailed_SettingsEnvStillWritten_ExitSix`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ClaudeSettingsMergeTests|FullyQualifiedName~SkillInstallerTests|FullyQualifiedName~SetupVerbTests"`

**GREEN**:
- The files above. `System.Text.Json.Nodes` only (framework); no package.

**Contract impact**: ⚠️ **owner file `settings.json`** (user scope; governs every Claude Code session on the laptop): additive merge, backup first, invalid JSON refused, `hooks` never written (Q2). ⚠️ **work boundary §8**: what lands on the employer's laptop is one env variable, three deny rules (a control that only restricts the model) and two skills. No §4–§7 change.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `grep -n "hooks" src/Zyggy.Core/Setup/ClaudeSettingsMerge.cs` shows only the XML comment that says the key is never touched (no code path names it).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — The whole verb end to end on a temp profile with a bare `zyggy-core` stand-in and a file secret store: the first `zyggy setup` run completes the spike-shaped `node.json`, clones the template, creates `settings.json`, copies both skills and reports both secrets present (exit 0, nine lines in order); the second identical run prints only `already done` lines, creates no backup and rewrites no file (content and last-write time unchanged); with `github-pat` missing the exit is 6 and the line names `zyggy secret set github-pat`; with an invalid `settings.json` the file is untouched and the exit is 6; `CLAUDE_CONFIG_DIR` redirects the settings and skills; no secret value appears in any stream, file or process argument

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Setup/SetupEndToEndTests.cs` *(create)*.
- `tests/Zyggy.Integration/Setup/SetupHarness.cs` *(modify)*: `SnapshotAsync()` (relative path → (SHA-256, `LastWriteTimeUtc`) over the profile, the claude dir, `localappdata/` and `node.json`); `SeedSettingsAsync(json)`; `SeedSkillAsync(name, content)` into the claude dir.
- `tests/Zyggy.Integration/Setup/SetupCommandTests.cs` *(modify)*: the Step 2 happy-path exit assertion becomes exit 0 with the full nine lines.

**Seams**: everything wired as in Steps 2 and 4 (file store, real git against the bare stand-in, recorded runner); `TimeProvider` real.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Setup/SetupEndToEndTests.cs`:
  - `Setup_FirstRun_SpikeNodeJson_AllNineLinesInOrder_ExitZero_FilesWired` (happy path: `node.json` completed with backup; `<core>` cloned; `settings.json` = exactly the two items; `skills/remember/SKILL.md` and `skills/archive/SKILL.md` byte-equal to the template; both secret lines `already done - present (fingerprint …)`)
  - `Setup_SecondRun_OnlyAlreadyDoneLines_ExitZero_NoBackup_SnapshotUnchanged` (AC-21: the snapshot before and after the second run equal, including last-write times; no `.bak-` file and no `backup/` folder created)
  - `Setup_ExistingSettingsWithOwnerHooks_MergedAdditively_HooksByteIdentical_BackupBeside` (AC-13)
  - `Setup_GithubPatMissing_ExitSix_TaskWouldStillStartLineNamesSecretSetGithubPat` (failure path, AC-19)
  - `Setup_SettingsNotValidJson_ExitSix_FileUntouched_OtherItemsDone` (AC-15)
  - `Setup_ClaudeConfigDirSet_SettingsAndSkillsGoThere` / `Setup_ClaudeDirOption_WinsOverVariable` (AC-16)
  - `Setup_SkillDiffersFromTemplate_MovedToLocalAppDataBackup_Replaced` (AC-17)
  - `Setup_SpikeFolderPresent_InfoLine_FolderUntouched` (AC-19: the folder's file bytes and times unchanged)
  - `Setup_NoSecretValueAnywhere_Sweep_IncludingEveryWrittenFile` (AC-22)
  - Harness: `SetupHarness` + `CoreTemplateFixture`.
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SetupEndToEndTests"`

**GREEN**:
- Only what the scenario exposes (expected: none beyond Steps 1–5).

**Contract impact**: none beyond Steps 1–5.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green on the executor's machine (Windows) — the executor notes that the same tests must pass on `ubuntu-latest` (AC-23): path separators, the executable bit on the fake `claude.exe`, `file://` remote; `SecretCommandInProcessTests`, `SecretListRemoveCommandTests` pass unedited.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C: `zyggy setup` wires the user-scope settings and the skills, and a second run changes nothing *(covers Steps 5–6)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `ClaudeSettingsMergeTests`, `SkillInstallerTests`, `SetupVerbTests`, `SetupEndToEndTests` green; the executor shows the nine lines of a first run and the nine lines of the second run on the harness, and the `settings.json` written over a file that held owner hooks (before/after).
- [ ] Contract review: A7 (the merge rules against AC-13/AC-14/AC-15 and the Edge Cases rows), A8 (the skill copy and its backup location against AC-17), AC-16, AC-21 (the snapshot), the Configuration table (an existing env value always wins; deny union only; `hooks` never written).
- [ ] ⚠️ Risk review: `settings.json` governs every Claude Code session on the laptop — backup first, additive, refused when invalid; the work-laptop content is only the env variable, the three deny rules and the two skills (§8, spec Behaviors); `SecretSweep` over every stream and file; nothing deleted (the replaced skill sits under `<LocalAppData>/zyggy/backup/`); nothing committed in any repository.
- [ ] User approved — implementation may continue past this gate

---

## Step 7 — `install.ps1` upgrades a laptop from any earlier state in one run: step 2 stops an old-folder `zyggy-node.exe` with the same wait-then-force pattern, removes an older `zyggy` folder from the **user** PATH only when every file in it matches `zyggy*` (files kept; a mixed folder or a copy not on the user PATH gets one warning line; an unwritable user PATH a warning, never a stop), puts the programs folder first in the session's `$env:Path`; step 6 calls `zyggy setup` exactly once through `Invoke-Native` by the absolute `$zyggyExe` with `--config`, `--tenant`, `--machine`, `--core-path` and `--sign-with` when given, `Fail`s on exit 3 or 4 before the task, registers and starts the task on exit 6 and ends `install: incomplete - see the setup lines above` with exit 1; `-CorePath` (default `C:\agents\zyggy-core`) in the parameter order of AC-6; the old `secret list` regex check is gone; the last line of a successful run says that terminals, VS Code windows and Claude Code sessions opened before must be reopened — every spec-10 invariant still holds, proven statically

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `deploy/windows/install.ps1` *(modify)*: header comment (the nine steps of the spec's sequence; the spike-store line now comes from `zyggy setup`, `rotate-hmac.md` step 0 stays named); `param(…)` gains `[string] $CorePath = 'C:\agents\zyggy-core'` between `$BusLocalPath` and `$Source`; a helper `Stop-NodeProcesses($processes)` holding the `Wait-Process -Timeout 30` then `Stop-Process -Force` pattern, called by `Stop-Node` (which keeps the single `Stop-ScheduledTask` line) and by the new old-folder stop; step 2 (new, before `Copy-Item`): `$oldDirs` = every user-PATH entry other than `$programs` holding `zyggy.exe` or `zyggy-node.exe` (`Select-Object -Unique`); an old node = `Get-Process -Name zyggy-node` whose `Path` parent is in `$oldDirs` → `Stop-NodeProcesses`, line `stopped the older zyggy-node in <dir> (it would hold port 4711)`; a stop that fails → `Fail "the older zyggy-node (pid <id>) in <dir> could not be stopped" "Stop-Process -Id <id> -Force, then run again"`; after the user-PATH add: per `$oldDirs` entry — only `zyggy*` files (`Get-ChildItem -File | Where-Object { $_.Name -notlike 'zyggy*' }` empty) → every occurrence removed from the `'User'` PATH inside `try { … } catch { Step "warning: could not remove $dir from the user PATH: $($_.Exception.Message)" }`, line `removed <dir> from the user PATH (an older zyggy; its files are kept - delete the folder by hand when you no longer need it)`; otherwise `Step "warning: <dir> holds an older zyggy next to other files and shadows the new one; move the zyggy files out or edit the user PATH by hand (rundll32 sysdm.cpl,EditEnvironmentVariables)"`; every `$env:Path` entry holding `zyggy.exe` that is **not** in the user PATH (the machine PATH or this session; no `'Machine')` read — see R3) → `Step "warning: <dir> holds an older zyggy and is not on the user PATH (the machine PATH needs whoever administers this machine)"`; then `$env:Path = $programs + ';' + ((($env:Path -split ';') | Where-Object { $_ -ne '' -and $_ -ne $programs -and $_ -notin $removed }) -join ';')`, line `session PATH: <programs> first`; step 5 loses the `secret list` block (`Invoke-Native` count stays 3: `git --version`, `setup`, `status`); step 6 (new): `$setupArgs = @('setup', '--config', $NodeJson, '--tenant', $Tenant, '--machine', $Machine, '--core-path', $CorePath)` + `--sign-with` when `-SignWith` was given; `$setupOutput = Invoke-Native $zyggyExe $setupArgs; $setupExit = $LASTEXITCODE; Write-Host $setupOutput.TrimEnd()`; `3` → `Fail 'zyggy setup refused node.json (see the setup lines above)' 'restore node.json from its .bak-… copy or fix the key named, then run again'`; `4` → `Fail "the signing key is not in this user's store" "zyggy secret set hmac/<n> --hex  (paste the tenant's key at the hidden prompt; compare fingerprints with zyggy secret list; where the key comes from: rotate-hmac.md)"`; any other non-zero but 6 → `Fail "zyggy setup exited $setupExit" 'read the setup lines above'`; `$setupIncomplete = ($setupExit -eq 6)`; step 7 `claude.path` pre-flight unchanged but its remedy names the two places looked in (`… on the PATH or at <UserProfile>\.local\bin\claude.exe`); steps 8–9 unchanged; after the status line: `Step 'reopen: terminals, VS Code windows and Claude Code sessions opened before this install still have the old PATH and environment - close and reopen them (VS Code: all windows)'`; then `if ($setupIncomplete) { Step 'incomplete - see the setup lines above'; exit 1 }`.
- `tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs` *(modify)*: see RED.
- `deploy/README.md` *(modify)*: the `windows/install.ps1` row gains `-CorePath` and the `zyggy setup` call; "Upgrade Central before the laptops" paragraph gains "a laptop upgrade is the same one `install.ps1` run (deliverable 42)".
- `.github/workflows/ci.yml` *(unchanged — AC-23; the existing "Parse install.ps1" step covers the edited script)*.

**Seams**: none (the script runs in no test; static invariants).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Deploy/InstallScriptTests.cs`
  - `Script_ParametersMatchTheContract` *(modify)*: `names.Should().Equal("Tenant", "Machine", "SignWith", "BusLocalPath", "CorePath", "Source", "NodeJson", "Uninstall")` + `block.Should().Contain("$CorePath = 'C:\\agents\\zyggy-core'")` (AC-6)
  - `Script_Step2_RemovesAnOlderZyggyFolderFromTheUserPathOnlyWhenItHoldsOnlyZyggyFiles_NeverDeletesIt` (AC-1/AC-2: `Code` contains `-notlike 'zyggy*'`, the exact removed-line text, the two warning texts, `rundll32 sysdm.cpl,EditEnvironmentVariables`; no `Remove-Item` line names `$dir`/`$old`; the removal sits inside a `try` with the `could not remove` warning; `Script_WritesNothingMachineWide` unchanged and green)
  - `Script_Step2_StopsAnOldFolderNodeThroughTheWaitThenForceHelper_BeforeTheCopy` (AC-3: `function Stop-NodeProcesses` exists and is the only place with `Wait-Process`/`Stop-Process -Force`; `Stop-Node` and the old-folder stop both call it; the old-folder stop's line index < `Copy-Item` index; `Stop-Process -Id` only in the `Fail` remedy)
  - `Script_EveryTaskStopWaitsForTheNodeProcessAndEndsASurvivor` *(modify)*: the helper assertions move to `Stop-NodeProcesses`; `Stop-ScheduledTask` still a single line inside `Stop-Node`; `Stop-Node` still called twice
  - `Script_Step2_PutsTheProgramsFolderFirstInTheSessionPath_AndTheReopenLineIsLastOnSuccess` (AC-4: `$env:Path = $programs + ';'` present; the reopen `Step` is the last `Step` call in `Code` outside the `if ($setupIncomplete)` block; the `incomplete` line + `exit 1` inside it)
  - `Script_CallsSetupOnceThroughInvokeNativeByAbsolutePath_WithTheOptions_AndBranchesOnThreeFourAndSix` (AC-5: exactly one line containing `'setup'`, through `Invoke-Native $zyggyExe`; the option tokens `--config`, `--tenant`, `--machine`, `--core-path`, `--sign-with`; `-eq 3` and `-eq 4` branches reach `Fail` **before** `Register-ScheduledTask` by index; `$setupIncomplete` read **after** `Start-ScheduledTask`; `'incomplete - see the setup lines above'` present)
  - `Script_NoSecretListParsing` (AC-5: `Code` contains neither `'secret', 'list'` nor `\ssigns`; `Script_NativeOutputIsReadThroughOneHelper…` *(modify)*: the count message becomes "git --version, setup and status")
  - `Script_PreflightNamesTheTwoRefusalRemedies` *(modify)*: tokens unchanged plus `.local\bin\claude.exe` (the two places)
  - `Script_DownloadsNothing`, `Script_NeverElevates…`, `Script_NeverTakesAPassword`, `Script_WritesNothingMachineWide`, `Script_PowerShell51Only…`, `Script_LfNoTrailingWhitespaceNoSecretNoIdentifier`, `Script_ChecksSha256SumsBeforeCopying`, `Script_RegistersTheTaskAsTheContractTable`, `Script_ActionRunsTheNodeHidden…`, `Script_UninstallLeaves…`, the four bugfix facts — **unedited and green** (AC-6)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~InstallScriptTests"`

**GREEN**:
- The script edits above; LF, no BOM, no trailing whitespace, PowerShell 5.1 syntax only (no `??`, `?.`, `&&`, `||`, ternary).

**Contract impact**: **§10 Laptops** (W42-1, already pasted) — the installer's new behaviour; the `install.ps1` parameter list (spec Contracts). ⚠️ **PATH change on the owner's machines** (user PATH only, only a `zyggy*`-only folder, nothing deleted). ⚠️ **R1**: the new shell is only the PATH cleanup, the old-node stop and one exit-code branch; no JSON handling.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; the CI parse step's command run locally: `pwsh -c "$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseFile('deploy/windows/install.ps1',[ref]$t,[ref]$e)|Out-Null;$e.Count"` prints `0`; `git ls-files --eol deploy/windows/install.ps1` shows `i/lf`.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D: `install.ps1` upgrades a laptop from any earlier state in one run *(covers Step 7)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `InstallScriptTests` green (the new facts and every spec-10 fact unedited); the executor shows the full diff of `deploy/windows/install.ps1` and the parse result.
- [ ] Contract review: AC-1..AC-6 line by line against the script text (the exact removed-line wording; the two warnings; the reopen line last; `--sign-with` only when `-SignWith` was given; 3/4 before the task, 6 after it with exit 1); the parameter order; R3 (machine-PATH copies detected without reading the `'Machine'` target).
- [ ] ⚠️ Risk review: the user PATH is written only to remove a `zyggy*`-only folder and to add the programs folder; `'Machine')` never appears; no `Remove-Item` names an old folder; no download, no elevation, no password; `Invoke-Native` is the only native capture; the new shell stays within R1's budget; nothing committed in any repository.
- [ ] User approved — implementation may continue past this gate

---

## Step 8 — Every new failure mode has its row and every new surface its documentation in `zyggy`: the Cli README's `setup` row and "Troubleshooting — Setup" table (old zyggy still on PATH, old node holds port 4711, `node.json` refused by setup, GitHub token missing, spike file store left on the laptop, Claude settings not valid JSON — left untouched, zyggy-core clone fails behind the proxy, zyggy-core path taken, zyggy-core not updated, PATH or settings not seen), the managed-settings sentence in "Context: configuration error", the Core README `Setup/` row, `deploy/README.md`; hygiene facts pin placeholder identities in every file this plan added and the package count unchanged

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/README.md` *(modify)*: the `setup` verb row (Step 2) completed with the nine item lines and the exit codes; a `## Troubleshooting — Setup` table with the rows named in the step title, each with the spec's remedy (Failure modes table); the `ZYGGY_SECRET_PATTERNS is not set` row of "Troubleshooting — Context" gains "check `/status` in Claude Code for managed (enterprise) settings; `setup` never writes managed files" and "the laptop install sets it" becomes "`install.ps1` (`zyggy setup`) sets it"; the "Secrets: file store on Windows" row points at the `spike file store` setup line.
- `src/Zyggy.Core/README.md` *(modify)*: the `Setup/` folder row (Step 1) extended with the item list and the exit priority.
- `deploy/README.md` *(modify)*: as Step 7 plus the sentence "the laptop runbook is one command: `zyggy-core/runbooks/install-node.md` 'Laptop'".
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `Readmes_SetupDocumented` (every row title above in the Cli README; `| \`setup --config` row; `Setup/` in the Core README; `-CorePath` and `zyggy setup` in `deploy/README.md`); `SetupFiles_NonDefaultIdentityOnly` (every file this plan created under `src/Zyggy.Core/Setup/`, `src/Zyggy.Cli/Commands/SetupCommand.cs`, `tests/Zyggy.Core.Tests/Setup/`, `tests/Zyggy.Integration/Setup/`, `tests/Zyggy.Integration/Infrastructure/CoreTemplateFixture.cs`: no `geoffrey`, no mail address, no GUID literal, no `/srv/`, `/home/`, `/Users/`, `C:\Users\`; the labels `acme`, `home-laptop` present); `Packages_RowCountPinned` unchanged (24, AC-23); `Deploy_AndReadmes_NoRealIdentifierNoAbsoluteHomeNoSkipPermissions` green over the edited files.

**Seams**: none (documentation and static facts).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs`: `Readmes_SetupDocumented`, `SetupFiles_NonDefaultIdentityOnly`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SourceHygieneTests"`

**GREEN**:
- The documentation edits above.

**Contract impact**: §11 (W42-5, already pasted) — the documentation side; no code contract.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `git diff Directory.Packages.props` empty; `git diff .github/workflows/ci.yml` empty.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 9 — The `zyggy-core` template and both install runbooks say what the installer now does: `node/README.md` "Wiring" states that `install.ps1` (`zyggy setup`, deliverable 42) writes `ZYGGY_SECRET_PATTERNS`, the three deny rules and both skills while the `Stop`/`SessionEnd` hook entries stay the documented **manual** step, every "installed by deliverable 15" wording is gone (the `archive` section included), the runbook entries gain one row per setup refusal; `node.bats` pins it; the public `runbooks/install-node.md` and the private instance `runbooks/install-node.md` ("Laptop" and "Upgrade") are reduced to the one command plus the manual hooks step and the manual fallback, placeholders only; bats and the hygiene sweeps are green

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(repository `D:\source\zyggy-core`, branch `feature/42-node-wiring`; repository `D:\source\zyggy-geoffrey`, main line)*:
- `zyggy-core/node/README.md` *(modify)*: intro — "the laptop install (deliverable 15) wires these files" → "`install.ps1` (`zyggy setup`, deliverable 42) wires the env variable, the deny rules and the skills; the hooks are wired by hand as below"; `## Wiring (installed by deliverable 15)` → `## Wiring` with two sub-parts: **written by `install.ps1`** (`env.ZYGGY_SECRET_PATTERNS`, `permissions.deny`, the two skills under `~/.claude/skills/`; the JSON example kept whole) and **manual** (the `hooks` block — "`setup` never installs, changes or removes hook entries (owner decision 2026-11-04)"; the two `bash C:/agents/zyggy-core/node/hooks/*.sh` lines as today); "Copy `node/skills/remember/` to `~/.claude/skills/remember/`" → "`install.ps1` copies it; by hand: copy …"; the `archive` section's "(installed by deliverable 15)" removed the same way; `## Runbook entries` gains **Old zyggy still on PATH**, **Old node holds port 4711**, **`node.json` refused by setup**, **GitHub token missing**, **Spike file store left on the laptop**, **Claude settings not valid JSON — left untouched**, **zyggy-core clone fails behind the proxy**, **zyggy-core path taken**, **zyggy-core not updated**, **PATH or settings not seen** (the spec's Failure-modes remedies, placeholders only), and **Context: configuration error** gains the managed-settings sentence.
- `zyggy-core/runbooks/install-node.md` *(modify)*: "## Laptop" → prerequisites (Git for Windows, Claude Code logged in, the bus clone with `core.autocrlf false` and one `push --dry-run`) then **one command** `.\deploy\windows\install.ps1 -Tenant acme -Machine home-laptop -SignWith acme/<n>`; the parameter table gains `-CorePath` (`C:\agents\zyggy-core`); "What the lines mean" rewritten for the new steps (old-copy lines, `setup:` lines, the reopen line, `incomplete`); the secrets step becomes "when a `setup:` line says `missing`, run the command it names, then run the script again"; the **manual hooks step** (`node/README.md` "Wiring", manual part) and the **manual fallback** (wire `node/README.md` by hand when `setup` keeps failing); "### Upgrade" laptop bullet → "the same one run; it also pulls `zyggy-core` and refreshes changed skills with a backup"; one entry per refusal `zyggy setup` can print (pointing at `node/README.md` "Runbook entries").
- `zyggy-geoffrey/runbooks/install-node.md` *(modify)*: the same "Laptop" and "Upgrade" rewrite (W42-5), placeholders only — the owner adds real evidence himself after AC-26.
- `zyggy-core/tests/node.bats` *(modify)*: the README test `node README: documents the Stop and SessionEnd wiring…` gains the needles `install.ps1`, `deliverable 42`, `zyggy setup`, `manual` and `! grep -qF 'installed by deliverable 15'`; a new test `node README: lists the setup runbook entries` over the ten row titles above; the archive README test's `~/.claude/skills/archive/` needle kept.
- `zyggy-core/tests/fixtures/zyggy-stub.sh` *(modify)*: `"setup "*` added to the known commands (the README documents `zyggy setup`; `repo.bats`' documented-command checks, if any, stay green).

**Seams**: none (documentation; the bats stub).

**RED**:
- Template test file: `zyggy-core/tests/node.bats` (modify/append): the two tests above.
- Failing-run command: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "git config --global --add safe.directory /w; trap '' PIPE; bats tests/node.bats tests/repo.bats"`

**GREEN**:
- The files above.

**Contract impact**: §11 `install-node.md` (W42-5); §3/§10 hooks unchanged (Q2). Public hygiene (AC-28): placeholders only in both public repositories; `repo.bats`' `hygiene_paths` (no `/Users/`, `C:\…` outside the allowed forms), `hygiene_principal`, `hygiene_words` over `node/` and `runbooks/` green.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: the full bats suite green in the container (`bats tests/`); `grep -rn "deliverable 15" D:/source/zyggy-core/node/README.md` empty; `grep -nE "/Users/|C:\\\\Users|/home/" D:/source/zyggy-core/runbooks/install-node.md D:/source/zyggy-geoffrey/runbooks/install-node.md D:/source/zyggy-core/node/README.md` empty; `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + `dotnet format Zyggy.slnx --verify-no-changes` still green in `zyggy` (nothing changed there in this step).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice E: definition of done *(covers Steps 8–9)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: `SourceHygieneTests` green; the full `dotnet test Zyggy.slnx` green on the executor's machine; the bats suite green in the container; the CI run on the branch (`windows-latest`, `ubuntu-latest`: build, format, tests, the script parse, both publishes, the smoke run) green once the owner pushes (AC-23).
- [ ] Contract review: every Failure-modes row of the spec has its runbook entry in the README/runbook the spec names (AC-24); `node/README.md` says the installer writes env, deny rules and skills and the hooks stay manual, no "deliverable 15" wording left; the Cli README `setup` row; `Directory.Packages.props` unchanged; AC-25 already applied (W42-1..W42-5 in the founding spec, listed in the orchestrating session's journal).
- [ ] ⚠️ Risk review: public hygiene in `zyggy` and `zyggy-core` (placeholders only, AC-28); the private runbook holds placeholders until the owner adds his own evidence; nothing committed by the agent in any of the three repositories.
- [ ] Owner-run check (AC-26, after the owner tags and installs the next release — Central first per `deploy/README.md`): on a laptop with an older install (the work laptop at the owner's choice), one `install.ps1` run from the new release folder in a fresh PowerShell window — during the run no hand step besides the secret lines the run itself names; afterwards `zyggy secret list` shows both secrets, `zyggy context -- "<fact>"` and `zyggy archive …` succeed from a **new** Claude Code session, no secret value appeared on screen; a second run prints only `already done` lines. Evidence with real paths goes only into the private instance runbook.
- [ ] Owner-run check (AC-27, forwarded from 12 via 15): on a laptop wired by 42, one `zyggy context` fact reported `OK` by `zyggy verify` on Central; on a laptop where the owner wired the hooks by hand per `node/README.md` "Wiring" (manual part), one hand-run session end leaves one context commit on the bus and an empty spool. The "install 0.11.0 on the home laptop" check is superseded.
- [ ] User approved — implementation may continue past this gate
