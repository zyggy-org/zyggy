# Plan: 27 — Central identity, memory repo and base plugin set (P0b) — A Claude Code session in `/srv/agent/central` (remote control or headless `claude -p`) starts as Zyggy from `AGENTS.md`, receives the owner's memory digest in three capped `SessionStart` sections, keeps a stated fact through `remember`, leaves one `[observed]` line per turn in `daily/` through `Stop`, drives a headless browser through the `playwright` plugin — and every repository, key, setting and plugin is recorded so a fresh VM can be rebuilt

## Overview

After this deliverable the Central VM's working directory `/srv/agent/central` **is** a clone of the new private **instance** repository `zyggy-geoffrey` — the owner's clone of the distributable, principal-free **template** `zyggy-core` (`AGENTS.md` + `.claude/{settings.json,rules,hooks,skills}`, from the template's history) plus the instance-owned files `.claude/rules/instance.md` and `instance/settings.local.json`; no `CLAUDE.md` anywhere on or above it; no template remote or key on the VM — with the new private repository `zyggy-geoffrey-memory` cloned at `memory/` in the §7 layout for `geoffrey/geoffrey`, seeded by the owner through the `seed-memory` skill. Template updates travel laptop merge (`git pull upstream main` in `d:\source\zyggy-geoffrey`) → push → VM `git pull --ff-only`. Three `SessionStart` hook invocations (`session-start.sh identity|index|daily`) inject `profile.md`/`preferences.md`, `agents.md` + an index of `areas/`/`people/`/`topics/`, and the 7 newest `daily/` files, each hard-capped below Claude Code's 10,000-character hook-output limit; `stop.sh` appends one `[observed]` line per turn to `daily/<date>.md`; `remember.sh` appends `[stated]` facts to `inbox/remember-<date>.md` and refuses anything matching `secret-patterns.txt`; Claude Code's auto memory lands in `memory/geoffrey/geoffrey/auto/`; `playwright@claude-plugins-official` runs headless Chromium at project scope. It implements `_specs/27-central-identity-memory.md` (approved 2026-09-30; **amended and re-approved 2026-09-30: template/instance split**, OQ-5..OQ-7 decided, zero Open Questions; **amended 2026-09-30: naming** — organisation `zyggy-org`, instance `zyggy-geoffrey`, memory `zyggy-geoffrey-memory`, VM names unchanged, remote-control session titled `Zyggy` via `--name Zyggy` in the 02 wrapper; its Decision Table, Contracts — three repositories, instance-owned paths — and AC-1..AC-36 are binding; its section "Impact on the already-built Slice A" is Step 5b) against founding-spec §3 (Central agent instance, Skills, Hooks — amended (a)/(b)), §7 (Layout with `auto/`, File format, Context loading — amended (c)/(d), Rules), §8 (Secrets — amended (e), Isolation, Injection), §10 (amended (f)/(i)), §11 (`restore-central.md` — amended (g)), §13 Q3/Q4 (untouched) and §14 (tenancy shape kept).

**This deliverable builds no `Zyggy.*` code.** Nothing under `src/` or `tests/` of this repository changes; `Zyggy.slnx` is built once at the final gate only to prove it is untouched (AC-32 "the `zyggy` repo's own CI is untouched"). The artefacts are: (1) the `zyggy-core` **template** repository, authored on the laptop in a **separate local checkout at `d:\source\zyggy-core`** (never inside this repo), tested with `bats-core` + `shellcheck` against a fixture memory tree for tenant `acme`, user `alice`, naming no tenant, user or machine path; (2) the `zyggy-geoffrey` **instance** repository at `d:\source\zyggy-geoffrey` (a clone of the template with `upstream` → `zyggy-core`, `origin` → `zyggy-geoffrey`) adding only the instance-owned `.claude/rules/instance.md` and `instance/settings.local.json`; (3) the `zyggy-geoffrey-memory` repository's directories, created at `d:\source\zyggy-geoffrey-memory` with the template README's commands (no skeleton files); (4) owner-run configuration of the Central VM; (5) in **this** repository only `runbooks/central-claude-config.md`, one pointer line in `runbooks/central-vm-setup.md`, `_plans/decisions/0002-central-productive.md` (and the restart row of `0001`), and the `ROADMAP.md` #27 status cell (the #27 done-line and the founding-spec re-amendments of OQ-5 are not the executor's — see the final gate).

**Reference pattern**: no comparable feature in code (no seam, no fake, no `Zyggy.Core` type is touched). The pattern is: this repo's approved plan format (`_plans/03-envelope-signing.md` — golden-oracle rule "expected files are never produced by the code under test", per-step VERIFY greps, AC→step table, assumptions section); the evidence style of `_plans/decisions/0001-transport-and-vm.md` (dated rows: date, command, excerpt, result); the execution-status table and `[laptop]`/`[vm/root]`/`[vm/zyggy]` tags of `runbooks/central-vm-setup.md`; and founding-spec §9's rules that survive into shell: paths only under `<root>/<tenant>/<user>/`, no tenant constant anywhere (`geoffrey` never in a script or test), data-never-instructions in every injected line, log once per state change (one stderr line per refusal/truncation).

**Fake → Wire mapping for a no-code deliverable.** The "seam" here is the hook/skill script contract of the spec (env `ZYGGY_MEMORY_ROOT|TENANT|USER|TIMEZONE|NOW|HOOKS|DIGEST_BYTES_*`, stdin JSON, exit codes 0/2/3/4, byte-exact stdout). *Fake* = the scripts proven by bats against the `acme/alice` fixture on the laptop with `ZYGGY_NOW` as the fake clock (Slice A). *Wire* = the same scripts run by Claude Code 2.1.284 on the VM against the real memory repository (Slices B and C), evidenced by the owner in decision 0002. 27 does not close a §12 phase (P0b closes with 30, and the roadmap says P0b has no automated gate), so there is no `Gates/P<n>_*.cs` slice; the final 🛑 HUMAN GATE is the definition-of-done check against `ROADMAP.md` #27 and AC-1..AC-36.

**Who runs what.** Every step is tagged:

| Tag | Meaning |
|-----|---------|
| **[agent, laptop]** | The executor (build-feature skill) does it on the laptop: files under `d:\source\zyggy-core` (template), `d:\source\zyggy-geoffrey` (instance-owned paths only), `d:\source\zyggy-geoffrey-memory`, or the files of this repo named above. The agent may `git clone`, `git add` (stage, so the index-based tests see new files), `git fetch`, `git diff`, `git ls-files` — it **never commits, merges or pushes**: it stops with a summary and the owner reviews and commits (`memory/MEMORY.md` rule). The agent never edits a template-owned file inside `d:\source\zyggy-geoffrey`; a template change is made in `d:\source\zyggy-core` and reaches the instance through the owner's `git pull upstream main`. |
| **[owner, …]** | The owner does it — every action on GitHub (`[browser]`), every `git push`, every command on the VM (`[vm/root]`, `[vm/zyggy]`), every `claude` invocation on the VM, the one `systemctl restart claude-remote`, and every phone/claude.ai interaction with the `central` session. The plan gives the exact commands; the runbook (Step 6) carries the same commands for later rebuilds. |
| **[agent, read-only VM check]** | After the owner reports an owner step done, the agent may collect *read-only* evidence with `az vm run-command invoke -g zyggy-central -n central --subscription "Abonnement Visual Studio Enterprise" --command-id RunShellScript --scripts "bash -c '<commands>'" --query "value[0].message" -o tsv` (runs as root under `sh`, hence the `bash -c` wrapper — `memory/short-term.md` 2026-09-29). Only `ls`, `cat`, `git … remote -v/rev-parse/status/log`, `grep`, `systemctl cat/status`, `stat`, `free`, `journalctl`, `jq` — never `claude`, never `sudo -iu zyggy claude`, never `systemctl restart`, never a write. If `az` on this laptop is not logged into the personal subscription, the owner pastes the output instead. |

**The agent never** runs `claude` on the VM with `--remote-control` or `--permission-mode auto`, never restarts `claude-remote`, never touches `claude-soak.*`, `/srv/agent/bin/*.sh` or the units (AC-18), never creates a session file in `~zyggy/.claude/projects/-srv-agent-central/`, never writes a key or token anywhere.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — The `zyggy-core` **template** scripts behave to the contract and the template names no principal, proven on the laptop against the `acme/alice` fixture | 1–5, 5b | `session-start.sh` emits the three byte-exact, capped, self-labelled digest sections, injects nothing on a configuration error and warns about a shadowing `CLAUDE.md`; `remember.sh` keeps a stated fact and refuses every secret sample; `stop.sh` appends one bounded line per turn and never blocks; `AGENTS.md`, rules and skills exist and are ≤ 200 lines; README, `AGENTS.md` and the rules describe an **instance** checkout, the instance-owned paths and how to create and update an instance; the hygiene test checks every template-owned file structurally (no machine path, no literal principal) plus the optional CI word list `ZYGGY_HYGIENE_FORBIDDEN`, and exempts instance-owned paths; CI passes the variable | 🛑 after Step 5b (⚠️ secret patterns = data-protection control; ⚠️ hook wiring = the 11/13 drop-in contract; ⚠️ new dev dependencies `bats-core`, `shellcheck`; ⚠️ the instance-owned path list = the template↔instance contract; owner reads `AGENTS.md`, runs `/doctor prompt-audit` on the laptop, commits and pushes the template) |
| B — Central is the `zyggy-geoffrey` **instance** checkout (template history + instance files) with the memory repo, the keys, the installed settings and the plugin in place; `AGENTS.md` and the digest are visible in the remote-control session | 6–8 | The runbook and decision 0002 exist; `d:\source\zyggy-geoffrey` holds only instance-owned additions over the template (AC-34); `zyggy-geoffrey` and `zyggy-geoffrey-memory` exist on GitHub, both CIs green; two deploy keys on the VM (`zyggy-geoffrey` read-only, `zyggy-geoffrey-memory` read/write), none for `zyggy-core`; `/srv/agent/central` is the instance clone at a recorded SHA containing the template SHA; `.claude/settings.local.json` installed from `instance/settings.local.json`; `claude -p --no-session-persistence` shows three `hook_response` digest sections; after the one restart the remote session shows `no CLAUDE.md found; AGENTS.md loaded`, lists the template's three rule files and `instance.md`, and still answers *pineapple* | 🛑 after Step 8 (⚠️ two deploy keys = first credentials on the VM; ⚠️ the one restart counted in 0001; ⚠️ external plugin enabled) |
| C — Central is productive and updatable: memory seeded, `remember` and `Stop` round trip, the first template update reaches the VM, headless browser, everything recorded | 9–13 | `profile.md`/`preferences.md`/`areas/`/`people/`/`topics/`/`agents.md` seeded and pushed from the VM; "remember that …" lands in `inbox/`, a token is refused; every turn leaves a `daily/` line; `/doctor prompt-audit` clean incl. `instance.md`; auto memory inside the repo; a template change travels laptop merge → push → VM fast-forward with no conflict (AC-35); Playwright fetches `Example Domain` headless within the RAM budget; 0002 holds a dated row per AC; runbook complete with troubleshooting, update and restore | 🛑 after Step 13 — **final definition-of-done gate** (⚠️ work-boundary rule for the browser; ⚠️ no secret in any of the three repositories; founding-spec re-amendments = owner's edit; roadmap done-line = project-manager's) |

**PROVE loop for `zyggy-core` (replaces the `dotnet` triple for Steps 1–5b and for every later template or instance change).** The four checks, run from the repository root:

```bash
bats tests/                                                       # every .bats file, all green (one skip when ZYGGY_HYGIENE_FORBIDDEN is unset)
shellcheck -S style .claude/hooks/*.sh .claude/skills/*/*.sh tests/*.bash
jq . .claude/settings.json >/dev/null
git ls-files --eol | grep -v 'i/lf\|i/-text'                      # must print nothing (LF or binary only)
```

**Where they run on this laptop.** This laptop has **no WSL Ubuntu**; the PROVE loop runs in the Ubuntu 24.04 **podman** container image `zyggy-core-test` (bats 1.10, shellcheck 0.9, jq 1.7, tzdata, git — the same distribution as CI's `ubuntu-latest` and the VM), from the Bash tool (Git Bash, hence `MSYS_NO_PATHCONV=1`):

```bash
MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c 'bats tests/ && shellcheck -S style .claude/hooks/*.sh .claude/skills/*/*.sh tests/*.bash && jq . .claude/settings.json >/dev/null && ! git ls-files --eol | grep -v "i/lf\|i/-text"'
```

Variants: add `-e ZYGGY_HYGIENE_FORBIDDEN=geoffrey` so the word-list hygiene test runs instead of skipping (the owner's word list; the name travels on the command line only, never into a template file); mount `'D:\source\zyggy-geoffrey:/w'` for the instance (Step 6 onwards); a single file with `bats tests/repo.bats`. This is how Steps 1–5 were proven (85 tests green). WSL Ubuntu, where a machine has it, is an equivalent optional runner (`wsl -d Ubuntu -- bash -lc 'cd /mnt/d/source/zyggy-core && …'`); the container is the reference on this laptop.

On Windows the executor must additionally keep the executable bit in the index (`git update-index --chmod=+x .claude/hooks/*.sh .claude/skills/remember/remember.sh`) and LF endings (`.gitattributes` in Step 1); otherwise the VM clone fails AC-2 (`-rwxr-xr-x`) and the shebang breaks.

<!--
Decomposition strategy: vertical slices, not horizontal layers.
Each slice follows: Fake (behavior proven through the seam interfaces with substitutes, unit tests)
→ Wire (real edge: git via Process, HttpClient with mocked handler, fake-claude script, local bare repo;
integration tests). Name steps by what the system can do — never by which type is built.
Good: "Reject an envelope with a bad signature", "Claim a job from a local bare bus repo"
Bad: "Create Envelope record", "Add GitClient", "Build BusRepository"

Gate placement: steps run back-to-back WITHOUT user intervention — the executor stops ONLY
at a 🛑 HUMAN GATE block. Place one gate at the end of each vertical slice. Add an extra
gate only where earlier user judgment is essential (contract sign-off after a fake step,
or after a ⚠️ Risk Area step: signing, secrets, work boundary, shared contract, new package).
Never attach a gate to every step.

Gate slice (MANDATORY when the deliverable closes a §12 phase — planner rule 15): the LAST
slice scripts the phase gate scenario under tests/Zyggy.Integration/Gates/P<n>_<Name>.cs
(a working round trip through the real components against the bare repo + fake claude),
updates PROTOCOL.md / node.json schema / the spec where behaviour changed, and adds a
runbook entry for any new failure mode. Its RED failing-run command:
  dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P<n>_<Name>"
The slice's 🛑 HUMAN GATE covers it.

Deliverable 27 does not close a phase (P0b closes with 30, and P0b has no automated gate — owner-reviewed
evidence in _plans/decisions/0002-central-productive.md). No Gates/P<n>_*.cs. The final 🛑 HUMAN GATE
is the definition-of-done check against ROADMAP.md #27 and AC-1..AC-36.

Owner-run steps (7, 8, 9, 10, 11, 12) keep the RED/GREEN/VERIFY shape: RED = the pre-state check that
proves the behaviour is absent before the step; GREEN = the owner's commands; VERIFY = the evidence
commands whose excerpt becomes a dated row in decision 0002. The executor marks such a step Done only
after the owner reports it and the read-only evidence is in 0002.
-->

---

## Fixture and golden-oracle rules (shared by Steps 1–5)

Everything under `d:\source\zyggy-core/tests/` uses **tenant `acme`, user `alice`**; `geoffrey` and `/srv/agent` appear nowhere in `.claude/`, `tests/` (AC-30; a bats test enforces it). The fake clock is `ZYGGY_NOW=2026-09-30T10:00:00Z` with `ZYGGY_TIMEZONE=Europe/Brussels` (local date `2026-09-30`, local time `12:00`).

`tests/helpers.bash` (sourced by every `.bats` file, `load helpers`) provides: `setup_memory` (copies `tests/fixtures/memory/` to `$BATS_TEST_TMPDIR/memory` **in shuffled order** for the `daily/` files so mtime order ≠ name order, exports `ZYGGY_MEMORY_ROOT=$BATS_TEST_TMPDIR/memory ZYGGY_TENANT=acme ZYGGY_USER=alice ZYGGY_TIMEZONE=Europe/Brussels ZYGGY_NOW=2026-09-30T10:00:00Z CLAUDE_PROJECT_DIR=$BATS_TEST_TMPDIR/project`, creates an empty `$CLAUDE_PROJECT_DIR`); `setup_oversize_memory` (copies `tests/fixtures/memory-oversize/` — a 40 KB `profile.md`, seven 5 KB `daily/` files — and **generates** the 300 index files `areas/gen-000.md … gen-299.md` with front matter into the temp copy, so 300 near-identical files are not committed; assumption 3); `hook_json` (prints `{"session_id":"…","cwd":"…"}` for stdin); `assert_bytes_equal <file> <expected>` (`cmp` — bytes, never `diff -w`); `no_write_under <dir>` (a `find -newer` sentinel check).

Expected outputs under `tests/expected/` (`digest-identity.txt`, `digest-index.txt`, `digest-daily.txt`, `daily-after-two-stops.md`, `inbox-after-remember.md`) are **written by hand from the spec's "Digest section format", "Stop line" and `remember` contracts before the script exists**, the same rule as `tests/golden/README.md` in this repo: a RED test may be red because the expected file is wrong, and the only admissible fix is a hand re-derivation against the spec, recorded in the gate summary. Pasting script output into an expected file is forbidden. `tests/README.md` states this rule (spec "Tests").

Fixture memory tree `tests/fixtures/memory/acme/alice/` (AC-19): `profile.md`, `preferences.md`, `agents.md` (all with front matter, a few `[stated]` lines about Alice, no real person); `areas/{zyggy,house-move,marathon}.md`; `people/{bob,carol}.md`; `topics/{tea,tools}.md`; `daily/` ten files `2026-09-18.md … 2026-09-30.md` (skipping some days so "10 files" ≠ "10 consecutive days") plus a roll-up `2026-08.md` and a `notes.md` (both must be ignored); `inbox/{remember-2026-09-29.md,context-01J8Y.md}`; `auto/MEMORY.md`; `areas/.gitkeep`; and `topics/tools.md` **without** front matter (the `(no description)` and "emitted whole" cases). `tests/fixtures/stop-input.json` (AC-25): `session_id` `0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e`, `transcript_path`, `cwd`, `permission_mode: "auto"`, `stop_hook_active: false`, `last_assistant_message` of three lines whose first line is longer than 40 characters and contains doubled spaces and a tab, `stop_reason: "end_turn"`. `tests/fixtures/secret-samples.txt` — one positive sample per pattern name of `secret-patterns.txt`, `name<TAB>sample` (AWS `AKIA…`, `ghp_…`, `github_pat_…`, `sk-ant-…`, generic `sk-…`, `xoxb-…`, a Telegram `123456789:AAF…` token of the right length, a JWT `eyJ….eyJ…`, `-----BEGIN RSA PRIVATE KEY-----`, an IBAN with and without spaces, a 16-digit card number with spaces, `password: hunter2x`, `secret=…`, `token: …`, `api_key=…`) — all obviously synthetic, none a real credential. `tests/fixtures/benign-samples.txt` — `+32 470 12 34 56`, `2026-09-30`, order number `48201937`, `my password manager is 1Password`, `https://example.com/docs?id=42`.

---

## Step 1 — A session started in a `zyggy-core` checkout receives the `identity` digest section (`profile.md` + `preferences.md`, byte-exact, front matter stripped), receives nothing false when the principal or memory root is missing, and is warned when a `CLAUDE.md` above the working directory would shadow `AGENTS.md`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop] — new local repository `d:\source\zyggy-core` (`git init`, default branch `main`; the owner creates the GitHub remote in Step 7).

**Scope** *(all files touched by this step, relative to `d:\source\zyggy-core`)*:
- `.gitignore` *(create)* — `memory/`, `.claude/settings.local.json`, `*.log`, `node.json`, `.claude/zyggy.lock` (spec layout).
- `.gitattributes` *(create)* — `* text=auto eol=lf`, `*.sh text eol=lf`, `*.bats text eol=lf`, `*.bash text eol=lf`, `tests/expected/** -text`, `tests/fixtures/** -text` (expected/fixture bytes are frozen; assumption 1).
- `.claude/settings.json` *(create)* — exactly the Contracts JSON: `hooks.SessionStart[0]` with matcher `startup|resume|clear|compact` and three exec-form commands `${CLAUDE_PROJECT_DIR}/.claude/hooks/session-start.sh` with `args` `["identity"]`, `["index"]`, `["daily"]`, `timeout` 10 each; `hooks.Stop[0].hooks[0]` = `${CLAUDE_PROJECT_DIR}/.claude/hooks/stop.sh`, `timeout` 10; `enabledPlugins` = `{"playwright@claude-plugins-official": true}`; no other key. (The `index`/`daily` args and `stop.sh` are wired now so the file is the contract from the first commit; Steps 2 and 4 make them real.)
- `.claude/hooks/lib.sh` *(create, executable)* — shared functions: `zy_require_config` (exit 3 with one stderr line naming the first missing of `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, or the missing `<root>/<tenant>/<user>` directory; `ZYGGY_TIMEZONE` defaults to `UTC` and is validated against `/usr/share/zoneinfo/$ZYGGY_TIMEZONE` → exit 3 naming the variable when absent), `zy_hooks_off` (true when `ZYGGY_HOOKS=off`), `zy_now_utc` / `zy_local_date` / `zy_local_hhmm` (from `ZYGGY_NOW` via `date -d`, else the real clock; `TZ=$ZYGGY_TIMEZONE`), `zy_strip_front_matter <file>` (drop the block between a first line `---` and the next `---` line; a file without it is printed whole), `zy_front_matter_value <file> <key>` (the value after `<key>:` inside the front matter, surrounding quotes stripped, empty when absent), `zy_read_stdin_json` (reads stdin only when it is not a TTY, so the script never blocks by hand), `zy_atomic_append` (Step 3), `zy_secret_match` (Step 3). Every function is `LC_ALL=C` so `${#s}` counts bytes and `sort` is byte-ordered.
- `.claude/hooks/session-start.sh` *(create, executable)* — `identity` section only in this step; `index`/`daily` → exit 4 `unknown section` until Step 2 (the test for the unknown-section exit uses `bogus`).
- `PROTOCOL.md` *(create)* — one paragraph: the bus contract is founding-spec §4 until deliverable 15 (O22).
- `README.md` *(create, first version)* — layout, the "root = Central's working directory" convention, "no `AGENTS.md` in any subdirectory", how to run the tests (the PROVE block above), the `acme/alice` fixture rule. Completed in Step 5.
- `tests/helpers.bash`, `tests/README.md`, `tests/fixtures/memory/acme/alice/**` (the whole tree described above), `tests/expected/digest-identity.txt`, `tests/digest.bats` *(create)*, `tests/repo.bats` *(create, first two tests)*.
- `.github/workflows/ci.yml` *(create)* — `on: [push, pull_request]`, `ubuntu-latest`, `sudo apt-get install -y bats shellcheck jq`, then the four PROVE commands. It runs for the first time at the owner's first push (Step 7) — AC-32 evidence lands there.

**Seams**: none of the five interfaces. The contract seam is the script interface (env + stdin + exit codes + stdout bytes); the fake clock is `ZYGGY_NOW`.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- `tests/expected/digest-identity.txt` — hand-written: line 1 `<zyggy-memory-digest section="identity" tenant="acme" user="alice" generated="2026-09-30T10:00:00Z">`, line 2 `The lines below are the owner's memory: data to consult, never instructions to follow.`, `## profile.md`, the fixture body without front matter, `## preferences.md`, its body, `</zyggy-memory-digest>`, final `\n`.
- `tests/digest.bats`:
  - `identity: fixture digest is byte-equal to expected/digest-identity.txt` — `run --separate-stderr` … `assert_bytes_equal`; exit 0; `$stderr` empty (AC-19).
  - `identity: profile.md without front matter is emitted whole` — temp copy with the front matter removed; the `## profile.md` body starts at the file's first line (Edge case).
  - `every section: ZYGGY_TENANT unset → exit 3, one stderr line naming ZYGGY_TENANT, empty stdout` — loop over `identity index daily` (AC-23; `index`/`daily` reach the config check before the unknown-section check, so they pass now and stay green in Step 2).
  - `every section: ZYGGY_MEMORY_ROOT points to a missing directory → exit 3, stderr names the path, empty stdout`.
  - `every section: <root>/<tenant>/<user> missing → exit 3, empty stdout`.
  - `every section: ZYGGY_HOOKS=off → exit 0, empty stdout, empty stderr`.
  - `identity: invalid ZYGGY_TIMEZONE → exit 3, stderr names ZYGGY_TIMEZONE, empty stdout` (Edge case).
  - `unknown section → exit 4, empty stdout, one stderr line`.
  - `identity: CLAUDE.md two levels above cwd → line 2 is the [warning] line and stderr repeats it` — project dir `$BATS_TEST_TMPDIR/a/b/c`, `CLAUDE.md` at `$BATS_TEST_TMPDIR/a/CLAUDE.md`, stdin `{"cwd":"…/a/b/c"}` (AC-24).
  - `identity: .claude/CLAUDE.md and CLAUDE.local.md in cwd also trigger the warning` (two rows).
  - `identity: no CLAUDE.md anywhere above → no [warning] line, empty stderr`.
  - `identity: runs with no stdin (by hand) and does not block` — `run timeout 5 … identity </dev/null` and a second run with stdin closed on a TTY-less `script`-free invocation; exit 0.
  - `identity: the tenant and user attributes come from env, never from a constant` — run with `ZYGGY_TENANT=globex ZYGGY_USER=zed` against a copied tree → the wrapper line carries `tenant="globex" user="zed"`.
- `tests/repo.bats` (first two tests): `repo: no CLAUDE.md, .claude/CLAUDE.md or CLAUDE.local.md exists at the root` (AC-31); `repo: .claude/settings.json parses and holds exactly the contract wiring` — `jq -e` assertions: `keys == ["enabledPlugins","hooks"]`, `.hooks | keys == ["SessionStart","Stop"]`, `.hooks.SessionStart[0].matcher == "startup|resume|clear|compact"`, `.hooks.SessionStart[0].hooks | map(.args[0]) == ["identity","index","daily"]`, every `.command == "${CLAUDE_PROJECT_DIR}/.claude/hooks/session-start.sh"`, every `.timeout == 10`, `.hooks.Stop[0].hooks[0].command == "${CLAUDE_PROJECT_DIR}/.claude/hooks/stop.sh"`, `.enabledPlugins == {"playwright@claude-plugins-official": true}`.
- Failing-run command: `bats tests/digest.bats tests/repo.bats` — fails (scripts and settings absent).

**GREEN** *(minimal script/markdown to make RED pass)*:
- `session-start.sh`: `#!/usr/bin/env bash`, `set -euo pipefail`, `source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"`; `zy_hooks_off && exit 0`; `zy_require_config`; `section=${1:-}`; `case` `identity` → build the section into a variable (never stream: the cap logic of Step 2 needs the whole text), print wrapper line with `generated="$(zy_now_utc)"`, `[warning]` line when `zy_find_claude_md "$cwd_or_project_dir"` finds `CLAUDE.md`, `.claude/CLAUDE.md` or `CLAUDE.local.md` in the directory or any ancestor up to `/` (also echoed to stderr), the data sentence, `## profile.md` + stripped body, `## preferences.md` + stripped body, closing tag; `index`|`daily` → exit 4 for now; `*` → exit 4. Output always ends with the closing tag and `\n`.
- `cwd` comes from stdin JSON (`jq -r .cwd` when stdin is not a TTY and parses), else `CLAUDE_PROJECT_DIR`, else `$PWD`.
- The ordering "wrapper, then `[warning]`, then the data sentence" reconciles AC-24 ("the identity section starts with `[warning]`") with the Contracts ("the section's first line after the wrapper") — assumption 2.

**Contract impact**: ⚠️ this step fixes the digest section wire format and the hook wiring — the drop-in contract that deliverable 11's `zyggy memory digest --section` must honour (spec Finding 3). Reviewed at the Slice A gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: the `zyggy-core` PROVE block (bats · shellcheck · `jq .` · `git ls-files --eol`) — all green. Plus:
- `bats tests/digest.bats tests/repo.bats` → every test above passes; `identity: fixture digest …` compares bytes with `cmp` (no `diff`).
- `printf '{"cwd":"%s"}' "$PWD" | ZYGGY_MEMORY_ROOT=$PWD/tests/fixtures/memory ZYGGY_TENANT=acme ZYGGY_USER=alice ZYGGY_NOW=2026-09-30T10:00:00Z .claude/hooks/session-start.sh identity | cmp - tests/expected/digest-identity.txt` → silent.
- `git ls-files -s .claude/hooks/` → mode `100755` on both scripts (Windows: `git update-index --chmod=+x`).
- `grep -rn -e geoffrey -e /srv/agent .claude/ tests/` → no match.
- No `dotnet` command: this repository (`d:\source\zyggy`) is untouched by Steps 1–5 (`git -C d:\source\zyggy status --porcelain` shows nothing new from this step).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.
- Shell-specific: `shellcheck -S style` clean; every path built from `$ZYGGY_MEMORY_ROOT/$ZYGGY_TENANT/$ZYGGY_USER` in one helper (`zy_user_dir`), never re-concatenated elsewhere (the shell analogue of `BusPaths`/`MemoryPaths`).

---

## Step 2 — The session also receives the `index` section (`agents.md` + one description line per `areas/`/`people/`/`topics/` file) and the `daily` section (7 newest day files, oldest first), and no section can ever exceed its byte cap or the 10,000-character hook limit — truncation is at a line boundary, marked, and logged once

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop]

**Scope**:
- `.claude/hooks/session-start.sh` *(modify)* — `index`, `daily`, caps and truncation.
- `.claude/hooks/lib.sh` *(modify)* — `zy_cap_bytes <section>` (default 6000/4000/8000; env override `ZYGGY_DIGEST_BYTES_IDENTITY|INDEX|DAILY`, non-numeric → default, clamped to ≤ 9500), `zy_truncate_at_line <text> <cap>`.
- `tests/helpers.bash` *(modify)* — `setup_oversize_memory`.
- `tests/fixtures/memory-oversize/acme/alice/{profile.md,preferences.md,agents.md,daily/2026-09-24.md … 2026-09-30.md}` *(create; the 300 index files are generated by the helper)*.
- `tests/expected/digest-index.txt`, `tests/expected/digest-daily.txt` *(create, hand-written)*.
- `tests/digest.bats` *(modify)*.

**Seams**: the script contract; fake clock `ZYGGY_NOW`.

**RED**:
- `tests/expected/digest-index.txt` — wrapper (`section="index"`), data sentence, `## agents.md` + body, `## index`, then one line `- <relative path> — <description>` per `.md` under `areas/`, `people/`, `topics/` in `LC_ALL=C` path order (`areas/house-move.md`, `areas/marathon.md`, `areas/zyggy.md`, `people/bob.md`, `people/carol.md`, `topics/tea.md`, `topics/tools.md — (no description)`), closing tag. `.gitkeep`, `daily/`, `inbox/`, `auto/`, `profile.md`, `preferences.md`, `agents.md` absent.
- `tests/expected/digest-daily.txt` — wrapper (`section="daily"`), data sentence, the 7 newest `YYYY-MM-DD.md` by **name** (not mtime — the helper shuffles copy order) oldest → newest, each `## daily/<name>` + stripped body; `2026-08.md` and `notes.md` absent; closing tag.
- `tests/digest.bats` additions:
  - `index: fixture digest is byte-equal to expected/digest-index.txt` (AC-20); `index: a description with an em dash and quotes is emitted verbatim, quotes stripped` (Edge case); `index: files without a description say (no description)`.
  - `daily: fixture digest is byte-equal to expected/digest-daily.txt` (AC-21); `daily: file order follows the name even when mtimes disagree`; `daily: fewer than 7 files lists what exists`; `daily: no daily files → wrapper and data sentence only`; `daily: names not matching YYYY-MM-DD.md are ignored` (Edge cases).
  - `caps: identity over 6000 bytes → output ≤ 6000, ends with marker line then closing tag, cut at a line boundary, one stderr line` (AC-22) — `[digest truncated: profile.md — <n> bytes over cap 6000]` is the last line before `</zyggy-memory-digest>`; the line before the marker ends with `\n` and is a complete fixture line.
  - `caps: ZYGGY_DIGEST_BYTES_IDENTITY=3000 → output ≤ 3000`; `caps: override 12000 is clamped to 9500`; `caps: non-numeric override falls back to the default`.
  - `caps: index with 300 files → output ≤ 4000, marker names "N index lines"`.
  - `caps: daily 7 × 5 KB → output ≤ 8000, the oldest files are dropped first, then the oldest remaining is cut at a line boundary, marker names the cut file`.
  - `caps: every section on the oversize tree is < 10000 bytes and ends with the closing tag` (loop over the three sections, with and without the override).
  - `caps: no truncation → no marker line and empty stderr` (the normal fixture).
- Failing-run command: `bats tests/digest.bats` — the new tests fail (`index`/`daily` exit 4; no cap logic).

**GREEN**:
- `index`: `## agents.md` + stripped body; `## index`; `find areas people topics -type f -name '*.md' | LC_ALL=C sort`; per file `- <path> — <description or "(no description)">`.
- `daily`: `ls daily/ | grep -E '^[0-9]{4}-[0-9]{2}-[0-9]{2}\.md$' | LC_ALL=C sort | tail -n 7`; emit in that (ascending) order.
- Caps: build the section body; if `bytes > cap`, for `daily` drop whole files from the oldest until the remainder fits or one file is left, then `zy_truncate_at_line`; for `identity`/`index` `zy_truncate_at_line` on the whole body (marker names `profile.md`/`preferences.md` by which file the cut falls in, or `N index lines`); append the marker line; one `printf … >&2` per truncated section. The cap applies to the **whole section output including wrapper and marker** so the guarantee "< 10,000" is unconditional; measure with `LC_ALL=C; ${#text}`.

**Contract impact**: none beyond Step 1 (same section format; caps per the spec Configuration table).

**VERIFY**: the `zyggy-core` PROVE block green. Plus:
- `bats tests/digest.bats` → all pass; the three byte-equal tests use `cmp`.
- `for s in identity index daily; do … session-start.sh $s | wc -c; done` on the oversize temp tree → three numbers ≤ 6000 / 4000 / 8000.
- `git status --porcelain tests/expected` → unchanged after GREEN (or a hand re-derivation note for the gate).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 3 — The owner's stated fact is kept as a `[stated]` line in `inbox/remember-<date>.md` (front matter once, scope and provenance variants), and any fact that looks like a key, token, IBAN, card number or credential assignment is refused loudly without being echoed

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop]

**Scope**:
- `.claude/hooks/secret-patterns.txt` *(create)* — `name<TAB>ERE` per line, exactly the spec's v1 list: `aws-access-key`, `github-token`, `anthropic-key`, `generic-sk-key`, `slack-token`, `telegram-bot-token`, `jwt`, `private-key`, `iban`, `card-number`, `credential-assignment`; a leading `#` comment line explaining the format and that `iban`/`card-number` are matched after removing spaces/hyphens and `credential-assignment` case-insensitively (the flags are conveyed by a third column `flags` = `nospace`, `nospace-nohyphen`, `icase` — assumption 4).
- `.claude/hooks/lib.sh` *(modify)* — `zy_secret_match <text>` (prints the first matching pattern name, exit 0; exit 1 when none), `zy_collapse_line <text>` (CR/LF/tabs → single spaces, runs of spaces collapsed, trimmed), `zy_atomic_append <file> <front-matter-name> <front-matter-description> <line>` (create the file with front matter `name`, `description`, `updated: <local date>` when absent; else rewrite `updated:`; append the line; write `<file>.tmp` then `mv -f` — never a partial file), `zy_ensure_dir`.
- `.claude/skills/remember/remember.sh` *(create, executable)* — argument parsing `[--scope general|project:<name>|machine] [--tag stated|observed] [--source <text>] -- "<fact>"`.
- `.claude/skills/remember/SKILL.md` *(create)* — front matter `name: remember`, the spec's `description` verbatim; body per the Contracts (`"$CLAUDE_PROJECT_DIR"/.claude/skills/remember/remember.sh … -- "<fact>"` through the Bash tool, quote the output verbatim, exit 2 → say it was refused and name the pattern, never retry a rephrased version, exit 3 → point to the runbook, never edit memory files directly, only for facts the owner states in the conversation).
- `tests/fixtures/secret-samples.txt`, `tests/fixtures/benign-samples.txt`, `tests/expected/inbox-after-remember.md` *(create)*; `tests/remember.bats` *(create)*.

**Seams**: the script contract; fake clock.

**RED**:
- `tests/expected/inbox-after-remember.md` — hand-written: front matter `name: remember 2026-09-30`, `description: facts stated by the owner on 2026-09-30 (remember skill)`, `updated: 2026-09-30`, then the five lines produced by the AC-27 sequence in order: `- [stated] 2026-09-30: Marie prefers tea`, `- [stated] 2026-09-30 (project:zyggy): …`, `- [stated] 2026-09-30 (machine): …`, `- [stated] 2026-09-30: …` (`--scope general` adds no hint, same as the default — assumption 5), `- [observed] 2026-09-30 [session 2026-09-30]: …`.
- `tests/remember.bats`:
  - `remember: default → exit 0, stdout "remembered: <abs path>" then the line, file has the [stated] line` (AC-27).
  - `remember: the five AC-27 variants in sequence produce inbox-after-remember.md byte-exact, front matter once, updated rewritten`.
  - `remember: a fact with CR, LF and tabs is collapsed to one line`.
  - `remember: an embedded "- [stated]" prefix is stored as text inside one bullet` (Edge case).
  - `remember: empty fact / 1001-char fact / --scope people/marie / --scope project: / --scope "project:has space" / --tag observed without --source / --tag inferred → exit 4, one stderr usage line, nothing written` (AC-28 + Edge cases; a 1,000-char fact is accepted).
  - `remember: every positive secret sample → exit 2, empty stdout, stderr "refused: matches secret pattern <name>", the sample never appears in stdout/stderr/the tree, nothing written` (AC-29; loop over `secret-samples.txt`, the expected `<name>` is the first column).
  - `remember: every benign sample → exit 0 and appended`.
  - `remember: ZYGGY_TENANT unset → exit 3; ZYGGY_HOOKS=off → exit 0, no output, nothing written`.
  - `remember: the tmp file never survives a run` (`ls inbox/*.tmp` empty).
  - `remember: never invokes git` — `PATH` prefixed with a directory holding a `git` stub that writes a sentinel and exits 99; sentinel absent after the run.
- Failing-run command: `bats tests/remember.bats` — fails (`remember.sh` absent).

**GREEN**:
- `remember.sh`: `set -euo pipefail`, source `lib.sh`, `zy_hooks_off && exit 0`, `zy_require_config`, parse options (`getopts`-free manual loop because of `--long` options; everything after `--` is the fact), validate: fact non-empty after collapsing, ≤ 1,000 chars, scope in the vocabulary (`project:<name>` with `<name>` matching `^[a-z0-9][a-z0-9-]*$` — the 03 label syntax), tag in `stated|observed`, `observed` requires `--source`; `zy_secret_match "$fact"` → `refused: matches secret pattern <name>` on stderr, exit 2; build the line `- [<tag>] <date>[ (<scope>)][ [<source>]]: <fact>`; `zy_atomic_append` to `<user dir>/inbox/remember-<local date>.md`; print `remembered: <absolute path>` and the line; exit 0.

**Contract impact**: ⚠️ `secret-patterns.txt` is a data-protection control (§7 Rules) and the future source of truth for the Hub's `remember` refusal (11); the scope vocabulary equals 03's `ContextScopeKind` wire strings. Reviewed at the Slice A gate.

**VERIFY**: the `zyggy-core` PROVE block green. Plus:
- `bats tests/remember.bats` → all pass; the secret loop reports one assertion per line of `secret-samples.txt` (11 patterns, ≥ 14 samples).
- `cut -f1 .claude/hooks/secret-patterns.txt | grep -v '^#' | sort` equals `cut -f1 tests/fixtures/secret-samples.txt | sort -u` (every pattern has a sample — a bats test also asserts it).
- `grep -rn -e geoffrey -e /srv/agent .claude/ tests/` → no match; `git ls-files -s .claude/skills/remember/remember.sh` → `100755`.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — Every finished turn leaves one bounded `[observed]` line in `daily/<local date>.md`, never blocks the session, never echoes a secret, and stops at 150 lines a day with a single marker

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop]

**Scope**:
- `.claude/hooks/stop.sh` *(create, executable)*.
- `.claude/hooks/lib.sh` *(modify, if needed)* — `zy_count_hook_lines <file>` (lines starting with `- [observed] `).
- `tests/fixtures/stop-input.json` *(create)*; `tests/expected/daily-after-two-stops.md` *(create, hand-written)*; `tests/stop.bats` *(create)*.

**Seams**: the script contract; fake clock; `jq` for the stdin JSON.

**RED**:
- `tests/expected/daily-after-two-stops.md` — front matter `name: daily 2026-09-30`, `description: turn notes of 2026-09-30 written by the Stop hook`, `updated: 2026-09-30`, then twice `- [observed] 12:00 session 0b7c3d1e: <first non-empty line of the fixture message, whitespace collapsed>` (`ZYGGY_NOW` 10:00 UTC = 12:00 Europe/Brussels).
- `tests/stop.bats`:
  - `stop: two runs on the fixture input produce daily-after-two-stops.md byte-exact, exit 0 both times` (AC-25).
  - `stop: stdout is always empty` (every case below asserts `$output` empty).
  - `stop: a note longer than 240 characters is cut at 240 with "…"`.
  - `stop: the first non-empty line is used when the message starts with blank lines`; `stop: a message that is only a code fence uses the fence line` (Edge case).
  - `stop: stop_hook_active true → nothing written, exit 0`; `stop: empty or whitespace last_assistant_message → nothing, exit 0` (AC-26).
  - `stop: a message containing AKIAABCDEFGHIJKLMNOP → nothing written, exit 0, stderr "stop: note refused (pattern aws-access-key)" without the value` (AC-26).
  - `stop: every positive secret sample is refused and every benign sample appended` (AC-29 second half).
  - `stop: a daily file with 150 hook lines → the marker "- [observed] cap reached: no further hook lines today" appended exactly once; a further run appends nothing` (AC-26).
  - `stop: ZYGGY_HOOKS=off → exit 0, nothing`; `stop: ZYGGY_TENANT unset → exit 3, one stderr line`.
  - `stop: jq missing from PATH → exit 3, stderr "jq not found", nothing written` (Failure modes).
  - `stop: 23:30Z lands in tomorrow's Europe/Brussels file with time 01:30` (`ZYGGY_NOW=2026-09-30T23:30:00Z` → `daily/2026-10-01.md`).
  - `stop: an existing daily file without a front matter gets none added and its lines are kept` (files written by hand/skills are never rewritten — assumption 6).
  - `stop: never invokes git` (the stub-`git` sentinel from Step 3); `stop: no .tmp file survives`.
- Failing-run command: `bats tests/stop.bats` — fails (`stop.sh` absent).

**GREEN**:
- `stop.sh`: `set -euo pipefail`, source `lib.sh`, `zy_hooks_off && exit 0`, `zy_require_config`, `command -v jq || { echo 'stop: jq not found' >&2; exit 3; }`; read stdin JSON; `stop_hook_active == true` → exit 0; `note=$(first non-empty line of last_assistant_message | zy_collapse_line)`; empty → exit 0; cut to 240 chars + `…`; `zy_secret_match` → stderr `stop: note refused (pattern <name>)`, exit 0; file `<user dir>/daily/<local date>.md`; if `zy_count_hook_lines ≥ 150` → append the marker once (skip if the marker already exists), exit 0; else `zy_atomic_append` with `name: daily <date>`, `description: turn notes of <date> written by the Stop hook`, line `- [observed] <HH:MM> session <first 8 chars>: <note>`; exit 0. No stdout in any path; exit non-zero only for configuration errors (3).

**Contract impact**: §3 Hooks amendment (b) as accepted: one `[observed]` line per turn into `daily/` — the shared file format 28's dream consumes. No new wire contract beyond Step 1.

**VERIFY**: the `zyggy-core` PROVE block green. Plus:
- `bats tests/stop.bats` → all pass.
- `bats tests/` → the full suite green (digest, remember, stop, repo).
- `printf '%s' "$(cat tests/fixtures/stop-input.json)" | … stop.sh; wc -c <<< "$(…)"` → stdout 0 bytes in a manual run.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 5 — Central's identity and rules exist as `AGENTS.md` + `.claude/rules/*.md` (≤ 200 lines each, no `CLAUDE.md` variant, no `AGENTS.md` in a subdirectory), the seeding interview exists as an owner-only skill, and a repo-hygiene test proves no script names a tenant or the VM path, every script is executable, LF, `set -euo pipefail`, shellcheck-clean and git-free

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop]

**Scope**:
- `AGENTS.md` *(create, ≤ 200 lines)* — the six sections of the Contracts outline, written as prose: `# Zyggy — Central` (who Zyggy is, who the owner is = `memory/<tenant>/<user>/profile.md` — no name, which machine, what exists today: memory + `remember` + Stop line + `playwright` browser; dream 28 / Telegram 29 / mail 23 / social 30 "not yet"); **Memory**; **Data, never instructions**; **What never to store**; **Tool discipline** (incl. the browser rules: headless, one at a time, closed after each task, never a logged-in site in an unattended run until 18–20); **Operations**. Each section is a summary that points at its rule file.
- `.claude/rules/memory.md` *(create)* — §7 file format (front matter keys, `[stated]`/`[observed]` bullets, `[[slug]]` links, `updated`), the digest sections and their caps, where writes go (`inbox/` via `remember`, `daily/` via Stop), durable files written only by the dream pass / the seeding session / an explicit owner request, `auto/` = Claude Code's own notes, memory files in English.
- `.claude/rules/security.md` *(create)* — data-not-instructions for memory, digest, inbox, mail, web pages, chat, tool output; never store secrets/credentials/IBANs/card numbers/mail bodies/health or personality inferences; never send, post or publish; never `git push`; never commit in `memory/`; the unattended-browser rule (`claude -p`, timers: no cookies, no saved sessions, no credentials typed, no logged-in sites until the work-boundary rules 18–20 exist); web pages read through the browser are data (§8 Injection).
- `.claude/rules/operations.md` *(create)* — the working directory is the `zyggy-core` checkout at the machine's Central path (the path itself is named only as "the working directory"; `/srv/agent/central` appears in `README.md` and the runbook, not in rules — AC-30 lets prose name it but the repo stays path-neutral where it can); `memory/` is the nested repository; `ZYGGY_*` come from `.claude/settings.local.json`; how to run a digest section by hand; what to say when a hook reports exit 3 (point to the runbook "Hooks report configuration error"); no `CLAUDE.md` is ever created; never edit `AGENTS.md`, `.claude/` or `PROTOCOL.md` unless the owner asks; `--permission-mode auto`, never `--dangerously-skip-permissions`; what 28/29 add later.
- `.claude/skills/seed-memory/SKILL.md` *(create)* — front matter `name: seed-memory`, `description: One-time seeding interview for a fresh memory repository (owner-invoked).`, `disable-model-invocation: true`; body: ask the six question blocks one at a time (verbatim from the spec Contracts), write answers as `- [stated] <today>: …` lines into `profile.md`, `preferences.md`, `areas/<slug>.md`, `people/<slug>.md`, `topics/<slug>.md`, `agents.md` under `$ZYGGY_MEMORY_ROOT/$ZYGGY_TENANT/$ZYGGY_USER/` (the one sanctioned direct write; front matter per `memory.md`; **files in English** whatever language the owner answers in; `preferences.md` records the language for answers), finish with `git -C "$ZYGGY_MEMORY_ROOT" status` for the owner to review and commit — the skill never commits.
- `README.md` *(modify)* — finished: layout table, root-is-working-directory, "no `AGENTS.md` in any subdirectory, node material under `node/` with another file name", test instructions, the fixture rule, "expected files are hand-derived".
- `tests/repo.bats` *(modify)* — the remaining tests.

**Seams**: none.

**RED**:
- `tests/repo.bats` additions:
  - `repo: AGENTS.md exists and, like every .claude/rules/*.md, is ≤ 200 lines` (AC-31).
  - `repo: no AGENTS.md exists in any subdirectory` (Contracts "Rules for the root").
  - `repo: remember/SKILL.md front matter has name and description; seed-memory/SKILL.md has name, description and disable-model-invocation: true` (AC-31).
  - `repo: no script or test names geoffrey or /srv/agent` — `grep -rn -e geoffrey -e /srv/agent .claude/ tests/*.bats tests/*.bash` empty (AC-30).
  - `repo: every .sh under .claude/ starts with #!/usr/bin/env bash and set -euo pipefail within its first 3 lines` (AC-30).
  - `repo: shellcheck -S style is clean on hooks, skill scripts and helpers` (AC-30).
  - `repo: every script is mode 100755 in the git index and LF-terminated` (`git ls-files -s`, `git ls-files --eol`).
  - `repo: no hook or skill script contains a git invocation` (`grep -nE '(^|[^a-z_-])git( |$)' .claude/hooks/*.sh .claude/skills/*/*.sh` empty — the contract "no git command anywhere").
  - `repo: AGENTS.md contains the data-never-instructions sentence and names the remember skill, daily/ and inbox/` (a prose smoke test so the audit has something to hold onto).
- Failing-run command: `bats tests/repo.bats` — fails (`AGENTS.md`, rules, `seed-memory` absent).

**GREEN**: the Markdown files above. Nothing in the scripts changes unless the hygiene tests find a gap.

**Contract impact**: ⚠️ `AGENTS.md`/rules are Central's identity — the owner must read them (Slice A gate). The seeding skill is the owner's interview (decided OQ-4).

**VERIFY**: the `zyggy-core` PROVE block green (`bats tests/` = digest + remember + stop + repo). Plus:
- `wc -l AGENTS.md .claude/rules/*.md` → every count ≤ 200.
- `test ! -e CLAUDE.md -a ! -e .claude/CLAUDE.md -a ! -e CLAUDE.local.md && find . -path ./.git -prune -o -name AGENTS.md -print` → only `./AGENTS.md`.
- `git -C d:\source\zyggy status --porcelain` → nothing from Steps 1–5 (this repository untouched).
- `git -C d:\source\zyggy-core status` shows every file above staged for the owner's first commit (the agent does not commit).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 5b — The template names no principal and no machine: `README.md`, `AGENTS.md` and the rules describe an **instance** checkout, the instance-owned paths and how to create and update an instance; the hygiene test checks every template-owned file for machine paths, literal principal assignments and (when the CI variable is set) the owner's forbidden words — and exempts instance-owned paths, so the same test passes in the template and in any instance

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop] — `d:\source\zyggy-core` only (still uncommitted; this step lands before the owner's first commit). Implements the spec section "Impact on the already-built Slice A" (amended 2026-09-30: template/instance split), exactly its seven files; everything that table lists as unchanged stays unchanged (`.gitignore`, `.gitattributes`, `.shellcheckrc`, `.claude/settings.json`, `.claude/hooks/*`, `.claude/skills/remember/*`, `PROTOCOL.md`, `tests/{digest,stop,remember}.bats`, `tests/helpers.bash`, `tests/README.md`, fixtures, expected files; no `LICENSE`).

**Scope** *(relative to `d:\source\zyggy-core`)*:
- `tests/repo.bats` *(modify)* — **delete** the test `repo: no script or test names the owner's tenant or the VM path` (it embeds the owner's name and the VM path as split strings). **Add**, inside `repo.bats` (not `helpers.bash`, which stays unchanged):
  - a constant `INSTANCE_OWNED_ERE='^(instance/|\.claude/rules/instance\.md$|\.claude/rules/instance/|\.claude/skills/instance-[^/]+/)'` — the single source of the four instance-owned patterns of the spec Contracts;
  - `template_owned_files <root>` — `git -C <root> ls-files` (index, so staged files count) minus paths matching `INSTANCE_OWNED_ERE`, minus `tests/repo.bats` itself, keeping only existing regular files;
  - `hygiene_paths <root>` — prints `path:line` for every template-owned file line matching `/srv/`, `/home/`, `/Users/`, `/root/` or a Windows drive path `(^|[^A-Za-z0-9_])[A-Za-z]:\\` (the leading non-alphanumeric is required: `tests/digest.bats` contains the regex text `truncated:\ `, which a bare `[A-Za-z]:\\` would flag); `grep -I` so binary files are skipped;
  - `hygiene_principal <root>` — prints offenders under `.claude/`, `AGENTS.md`, `README.md` (template-owned only) matching `ZYGGY_(TENANT|USER)"?[[:space:]]*[=:][[:space:]]*"?[A-Za-z0-9]` — a literal value; placeholders (`<tenant>`), expansions (`$ZYGGY_TENANT`, `${ZYGGY_TENANT:-}`) do not match;
  - `hygiene_words <root> <csv>` — splits the comma-separated list, trims, drops empty items, and prints `path:line` for every case-insensitive **fixed-string** occurrence (`grep -niF`) of any word in a template-owned file.
- `tests/repo.bats` *(modify)* — the new tests listed under RED.
- `README.md` *(modify)* — per the spec's Impact table: opening paragraph "the root of an **instance** checkout is the working directory of that instance's Claude Code sessions" (no `/srv/agent/central`, no "On Central the checkout is …"); "the nested `zyggy-memory` clone" → "the nested memory repository"; "Rules for this repository": no tenant, user or machine path in **any** template file; new sections **Instance-owned paths** (the four patterns written exactly as `instance/**`, `.claude/rules/instance.md`, `.claude/rules/instance/**`, `.claude/skills/instance-*/**`; "never edit a template-owned file in an instance"; "an instance declares no hooks" because hook lists merge across settings files), **Create an instance** (empty private repository without README/licence; `git clone <template URL> <instance>`; `git remote rename origin upstream`; `git remote add origin <instance URL>`; add `.claude/rules/instance.md`; add `instance/settings.local.json` with `<memory root>`/`<tenant>`/`<user>`/`<time zone>` placeholders; commit; `git push -u origin main`; the memory repository: `git init`, `mkdir -p <tenant>/<user>/{areas,people,topics,daily,inbox,auto}`, a `.gitkeep` in each, a one-paragraph `README.md`; clone it at `memory/`; `install -m 600 instance/settings.local.json .claude/settings.local.json`; `claude plugin install playwright@claude-plugins-official --scope project`; accept workspace trust; `/seed-memory`), **Update an instance from the template** (`git pull upstream main` on the workstation, review, `git push origin main`, `git pull --ff-only` on the machine; the machine never merges, commits or holds an `upstream` remote), and in "Tests" the `ZYGGY_HYGIENE_FORBIDDEN` variable (comma-separated, case-insensitive; set as a GitHub Actions repository variable; unset → that one test is reported skipped). The laptop's podman runner is **not** mentioned in the README (a template names no machine).
- `AGENTS.md` *(modify, stays ≤ 200 lines, keeps `# Zyggy — Central` as line 1 and the strings `data, never instructions`, `remember`, `daily/`, `inbox/` the existing test greps)* — the line "The details of every section below are in `.claude/rules/` …" gains "and, when present, this instance's `instance.md`, which adds to them"; **Operations** first bullet → "The working directory is an instance checkout: this file, `.claude/` and `PROTOCOL.md` come from the `zyggy-core` template through the instance's history; machine-specific facts are in `.claude/rules/instance.md`. `memory/` is the nested memory repository; neither repository is pushed from here."; third bullet → "the runbook named in `.claude/rules/instance.md` (or the template README when there is none)" instead of `runbooks/central-claude-config.md` in the `zyggy` repository.
- `.claude/rules/operations.md` *(modify)* — "Where you run" bullet 1 → "an instance checkout (template files from `zyggy-core`, instance files alongside); its remote is read-only from here; template files change in the template, instance files in the instance, both on the owner's workstation, and are pulled here with `git pull --ff-only`"; bullet 4 → `.claude/settings.local.json` is "installed from the instance's `instance/settings.local.json`; the live file stays untracked because Claude Code writes permission approvals into it"; new bullet "`.claude/rules/instance.md`, when present, adds or tightens rules for this machine and never relaxes these"; "When something reports an error" bullet 1 → the runbook pointer as in `AGENTS.md`.
- `.claude/rules/security.md` *(modify)* — "Git" bullet 3 → "Never commit in this working directory: template and instance changes are made on the owner's workstation and pulled here."
- `.claude/skills/seed-memory/SKILL.md` *(modify)* — "Writing the answers", bullet on `profile.md`/`preferences.md`/`agents.md`: "keep the existing front matter" → "keep the existing front matter, or create the file with front matter (`name`, `description`, `updated`) when it does not exist".
- `.github/workflows/ci.yml` *(modify)* — the `bats` step gets `env:` → `ZYGGY_HYGIENE_FORBIDDEN: ${{ vars.ZYGGY_HYGIENE_FORBIDDEN }}`.

**Seams**: none of the five interfaces. The contract is the template↔instance boundary (instance-owned paths) and AC-30's hygiene definition.

**RED** *(write these tests first, run them, confirm they fail before editing any prose)*:
- `tests/repo.bats` additions:
  - `repo: no template-owned file contains an absolute home or machine path (/srv/, /home/, /Users/, /root/, X:\)` — `hygiene_paths "$REPO_ROOT"` prints nothing (AC-30 a). **Fails now** on `README.md:4` (`/srv/agent/central`).
  - `repo: no literal ZYGGY_TENANT/ZYGGY_USER assignment under .claude/, in AGENTS.md or README.md (placeholders allowed)` — `hygiene_principal "$REPO_ROOT"` prints nothing (AC-30 b).
  - `repo: no word of ZYGGY_HYGIENE_FORBIDDEN occurs in a template-owned file` — `[ -n "${ZYGGY_HYGIENE_FORBIDDEN//[ ,]/}" ] || skip "ZYGGY_HYGIENE_FORBIDDEN not set"`; then `hygiene_words "$REPO_ROOT" "$ZYGGY_HYGIENE_FORBIDDEN"` prints nothing (AC-30 c; an empty value — what GitHub expands for a missing variable — counts as unset).
  - `repo: the hygiene checks flag template-owned offenders and exempt instance-owned paths` — negative control: `git init -q "$BATS_TEST_TMPDIR/t"`; template-owned `README.md` with a planted `/srv/x` line and a `C:\Users\x` line, `.claude/rules/memory.md` with `ZYGGY_TENANT=bob`, `AGENTS.md` with the word `Zebulon`, `tests/digest.bats` with `truncated:\ ` and `.claude/hooks/x.sh` with `"${ZYGGY_TENANT:-}"` and `ZYGGY_USER: <user>`; instance-owned `instance/settings.local.json`, `.claude/rules/instance.md`, `.claude/rules/instance/extra.md`, `.claude/skills/instance-x/SKILL.md`, each carrying all three kinds of offence; `git -C … add -A` (no commit); assert `hygiene_paths` lists exactly `README.md` twice, `hygiene_principal` exactly `.claude/rules/memory.md`, `hygiene_words … zebulon` exactly `AGENTS.md` (case-insensitive), and no instance-owned path and neither the `truncated:\ ` nor the expansion/placeholder lines appear. **Fails now** (functions absent).
  - `repo: README.md documents the instance-owned paths, creating and updating an instance, and ZYGGY_HYGIENE_FORBIDDEN` — `grep -qF` for each of the four patterns as written above, for the headings `Instance-owned paths`, `Create an instance`, `Update an instance from the template`, for `git pull upstream main`, `git pull --ff-only`, `install -m 600 instance/settings.local.json .claude/settings.local.json`, and `ZYGGY_HYGIENE_FORBIDDEN`.
  - `repo: AGENTS.md and operations.md point to .claude/rules/instance.md; no template rule names a runbook path of the zyggy repository` — `grep -q 'instance.md'` in both; `! grep -n 'runbooks/' AGENTS.md .claude/rules/memory.md .claude/rules/security.md .claude/rules/operations.md` (the three template rule files **by name** — never the glob, because an instance's `instance.md` legitimately names its runbook).
  - `repo: operations.md installs settings.local.json from instance/settings.local.json and says instance.md never relaxes these rules` — `grep -qF 'instance/settings.local.json'` and `grep -q 'never relaxes'`.
  - `repo: security.md says template and instance changes are made on the owner's workstation` — `grep -q 'template and instance changes'`.
  - `repo: seed-memory creates a missing identity file with front matter` — `grep -q 'when it does not exist'` in `seed-memory/SKILL.md`.
  - `repo: ci.yml passes vars.ZYGGY_HYGIENE_FORBIDDEN to bats` — `grep -qF 'ZYGGY_HYGIENE_FORBIDDEN: ${{ vars.ZYGGY_HYGIENE_FORBIDDEN }}' .github/workflows/ci.yml`.
- Failing-run command: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c 'bats tests/repo.bats'` — the new tests fail (README path, missing functions, missing prose); the existing repo tests stay green.

**GREEN** *(minimal edits to make RED pass)*: the four functions and the constant in `repo.bats`; the prose edits of `README.md`, `AGENTS.md`, `operations.md`, `security.md`, `seed-memory/SKILL.md` as quoted in Scope (wording from the spec's Impact table, nothing else rewritten); the `env:` line in `ci.yml`. `git add` the changed files so the index-based tests see them; no commit.

**Contract impact**: ⚠️ the instance-owned path list (`INSTANCE_OWNED_ERE` + README) is the contract every instance relies on for conflict-free `git pull upstream main` (spec "Instance-owned paths"; changing it later is a template change); ⚠️ the hygiene definition is what keeps the template "publishable by construction" (OQ-7). Reviewed at the Slice A gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: the PROVE loop in the container — all green, `bats` reporting **one skipped** test (the word list, variable unset). Plus:
- The same run with `-e ZYGGY_HYGIENE_FORBIDDEN=geoffrey` → all green, **zero** skipped (the word-list test ran and passed).
- `git -C d:\source\zyggy-core grep -n -i geoffrey` → nothing in the whole index, `tests/repo.bats` included (the owner's name is written nowhere in the template, not even split); `git -C d:\source\zyggy-core grep -n -e '/srv/' -- ':!tests/repo.bats'` → nothing.
- `git -C d:\source\zyggy-core ls-files | grep -E '^(instance/|\.claude/rules/instance(\.md$|/)|\.claude/skills/instance-)'` → nothing (the template ships no instance-owned path — AC-34's template half).
- `wc -l AGENTS.md .claude/rules/*.md` → every count ≤ 200.
- A `git -C d:\source\zyggy-core ls-files -s` snapshot taken (into the scratchpad) before RED, compared after GREEN → only the blob IDs of the seven Scope files changed (nothing is committed yet, so `git diff --cached` cannot show this; hooks, the `remember` skill, fixtures, expected files, `helpers.bash` and the other `.bats` files are byte-identical).
- `git -C d:\source\zyggy status --porcelain` → nothing new from this step (this repository untouched).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (the `zyggy-core` template behaves to the contract and names no principal) *(covers Steps 1–5 and 5b)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: in the `zyggy-core-test` container over `d:\source\zyggy-core`, `bats tests/` green (executor pastes the summary line: number of tests, 0 failures, 1 skipped without the variable; 0 skipped with `-e ZYGGY_HYGIENE_FORBIDDEN=geoffrey`), `shellcheck -S style …` silent, `jq . .claude/settings.json` parses; the executor demonstrates by hand: (1) `session-start.sh identity` on the fixture is `cmp`-identical to `tests/expected/digest-identity.txt`; (2) with `ZYGGY_TENANT` unset it prints nothing and exits 3; (3) `remember.sh -- "my GitHub token is ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"` exits 2 with `refused: matches secret pattern github-token` and no file; (4) `stop.sh < tests/fixtures/stop-input.json` twice yields `daily-after-two-stops.md`; (5) every section on the oversize tree is < 10,000 bytes; (6) the hygiene negative control flags the planted template-owned offenders and none of the instance-owned ones.
- [x] Contract review: `.claude/settings.json` equals the spec's Contracts JSON (three `SessionStart` invocations in exec form with `${CLAUDE_PROJECT_DIR}`, matcher `startup|resume|clear|compact`, one `Stop`, `enabledPlugins` with exactly `playwright@claude-plugins-official` — kept in the template, OQ-6); the digest wrapper/section format, the Stop line and the `remember` line formats match the spec byte for byte (the expected files were hand-derived, not pasted from output — executor confirms); `secret-patterns.txt` lists the eleven v1 patterns; scope vocabulary = `general | project:<name> | machine`; exit codes 0/2/3/4 as the table; no script runs git; `ZYGGY_HOOKS=off` silences all three.
- [x] **Template role review** (amended 2026-09-30: template/instance split): the template **names no owner** — no tenant, user or machine path in any template-owned file, `README.md` and `AGENTS.md` included (`git grep -i geoffrey` empty over the whole index; no `/srv/` outside the hygiene test's own negative control); **instance-owned paths are absent** from the template (`git ls-files` shows none of `instance/**`, `.claude/rules/instance.md`, `.claude/rules/instance/**`, `.claude/skills/instance-*/**`) and the README lists exactly the four patterns the hygiene test exempts; the README's **Create an instance** and **Update an instance from the template** sections are followable by someone else with placeholders only; `AGENTS.md`/`operations.md` defer machine facts to `instance.md` and no template rule names a runbook path of the private `zyggy` repository. Owner notes the one remaining pointer: `.claude/skills/remember/SKILL.md` still names `runbooks/central-claude-config.md` "in the `zyggy` repository" — the spec lists `remember/*` as unchanged, so the plan leaves it; the owner decides whether it goes with OQ-7's later pointer clean-up (`PROTOCOL.md`, `tests/README.md`).
- [x] Owner reads `AGENTS.md` and the three rule files as Central's identity: the six sections, no owner name, data-never-instructions, the browser and unattended-run rules, the instance-checkout wording, ≤ 200 lines each. Owner runs the prompt audit **on the laptop** (not on the VM): in PowerShell `cd d:\source\zyggy-core; $env:ZYGGY_HOOKS='off'; claude` then `/doctor prompt-audit`; findings are fixed by the executor before the gate closes (the instance's `instance.md` is audited on the laptop at the Slice B gate, and the VM run in Step 10 is then expected clean, so no second `claude-remote` restart is needed). Expect harmless `SessionStart hook error` lines on Windows (bash hooks do not exec there) — they are not findings.
- [x] ⚠️ Risk review: `secret-patterns.txt` (data-protection control) — every pattern has a positive sample and the benign samples pass (false-positive risk of `card-number` on long digit strings is documented in the runbook "Remember refused" entry, Step 6); new dev dependencies `bats-core` (MIT) and `shellcheck` (GPL-3.0, CI tool only, nothing ships) accepted; the hygiene test is template-scoped and the owner's name appears in no file, only in the CI variable; no secret of any kind in the repository (the samples are synthetic); the `index`/`daily`/Stop wiring is the interface 11 and 13 must keep; the instance-owned path list is the template↔instance contract.
- [x] Owner commits `d:\source\zyggy-core` **as the template** (first commit on `main`; the agent never commits); then (spec runbook step 1: "`zyggy-core` already exists, pushed at the Slice A gate") `[browser]` creates the empty private repository `zyggy-org/zyggy-core` under the bus organisation, sets its Actions repository variable `ZYGGY_HYGIENE_FORBIDDEN` (tenant and user names), and `[laptop]` `git remote add origin <URL>; git push -u origin main`; the first Actions run is green and its bats log shows the word-list test **ran** (AC-32, template half — URL noted for 0002 in Step 7). *Progress note (2026-09-30, not a tick — the executor ticks gate boxes only after approval): the owner has committed and pushed `zyggy-core` to `https://github.com/zyggy-org/zyggy-core` (private), set `ZYGGY_HYGIENE_FORBIDDEN=geoffrey`, and CI is green at `316e9ea` with the word-list test run.*
- [x] User approved — implementation may continue past this gate

---

## Step 6 — The owner has an instance of the template ready to push (`d:\source\zyggy-geoffrey`: template history + `upstream` remote + only the instance-owned files), a memory repository built with the template README's commands, a runbook to configure, update and restore Central, and a decision record to fill; nothing on GitHub or the VM changes yet

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Tag**: [agent, laptop] — a new local instance `d:\source\zyggy-geoffrey`, a new local repository `d:\source\zyggy-geoffrey-memory`, and files in **this** repository (`d:\source\zyggy`). The agent clones, adds and stages; the owner commits and pushes in Step 7.

**Scope**:
- `d:\source\zyggy-geoffrey` *(create — the instance, spec Contracts "Creating the instance")*: `git clone https://github.com/zyggy-org/zyggy-core d:\source\zyggy-geoffrey` (or the SSH form the owner uses; the template exists since Gate A, CI green at `316e9ea`); `git -C d:\source\zyggy-geoffrey remote rename origin upstream`; `git -C d:\source\zyggy-geoffrey remote add origin <zyggy-geoffrey URL>` (`zyggy-org/zyggy-geoffrey`, same organisation and URL scheme as the template; the GitHub repository itself is created by the owner in Step 7 — adding the remote is configuration only, the agent never pushes). If the agent's shell cannot reach GitHub, `git clone d:\source\zyggy-core d:\source\zyggy-geoffrey` + the rename + `git remote set-url upstream <zyggy-core URL>` gives the same history (the SHA equals the pushed template commit). Then **only instance-owned files** are added (spec "Instance-owned paths"):
  - `.claude/rules/instance.md` *(create, ≤ 200 lines, no secret)* — `# This instance — Central`; machine role (Central, the owner's always-on Azure VM); the working directory `/srv/agent/central`; the repositories: this checkout is `zyggy-geoffrey` (read-only from the VM; changed on the owner's workstation and pulled with `git pull --ff-only`; template changes arrive through it), `memory/` is `zyggy-geoffrey-memory` (committed and pushed only by the dream pass from 28; in 27 by the owner's seed commit); neither is pushed from the VM by Claude; the principal (`geoffrey/geoffrey`) and the memory root come from `.claude/settings.local.json`, installed from `instance/settings.local.json`; where the runbook is: `runbooks/central-claude-config.md` in the owner's `zyggy` repository (entries "Hooks report configuration error", "Digest truncated", "Update Central from the template"); instance-only plugins: none in 27; closing sentence "This file adds to or tightens the template's rules; it never relaxes one (security rules never)."
  - `instance/settings.local.json` *(create)* — the spec's JSON verbatim (`env` with `ZYGGY_MEMORY_ROOT=/srv/agent/central/memory`, `ZYGGY_TENANT=geoffrey`, `ZYGGY_USER=geoffrey`, `ZYGGY_TIMEZONE=Europe/Brussels`; `autoMemoryDirectory=/srv/agent/central/memory/geoffrey/geoffrey/auto`); **no `enabledPlugins` key** (AC-12); 2-space indent, LF, final newline.
  - `git -C d:\source\zyggy-geoffrey add .claude/rules/instance.md instance/settings.local.json` — staged, not committed. No template-owned file is touched.
- `d:\source\zyggy-geoffrey-memory` *(create, new local repository)* — built with exactly the template README's "Create an instance" memory commands (the spec ships no skeleton tree): `git init -b main`; `mkdir -p geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}`; a `.gitkeep` in each of the six; `README.md` (one paragraph: layout `<tenant>/<user>/…`, v1 `geoffrey/geoffrey`, "written by Central's hooks, the seeding session and the dream pass; pushed by the dream pass only"). **No** `profile.md`/`preferences.md`/`agents.md` — `/seed-memory` creates them with front matter in Step 9 (Step 5b edit). Staged, not committed. `geoffrey` may appear here: this is the memory repository, not the template.
- `runbooks/central-claude-config.md` *(create, this repo)* — in the style of `runbooks/central-vm-setup.md`: header (what it builds; the three repositories and their roles as the spec's Repositories table; tags `[browser]`, `[laptop]`, `[vm/root]`, `[vm/zyggy]`, **[agent, read-only VM check]**), an **execution-status table** (one row per runbook step 1–10 below, state, notes), then the ten steps of the spec's "Runbook" section with exact commands: (1) `[browser]` create the empty private repositories `zyggy-geoffrey` (no README, no licence, so the first push is a fast-forward) and `zyggy-geoffrey-memory` under the bus organisation (`zyggy-core` exists since the Slice A gate); `[laptop]` create the instance from the template (the Step 6 commands above, then commit and `git push -u origin main`), push `zyggy-geoffrey-memory`; set the Actions repository variable `ZYGGY_HYGIENE_FORBIDDEN` in **both** `zyggy-core` and `zyggy-geoffrey` (value never pasted into a document); both CI runs green; `[vm/zyggy]` generate the two deploy keys (`ssh-keygen -t ed25519 -f ~/.ssh/zyggy_zyggy-geoffrey_ed25519 -N '' -C zyggy-geoffrey@central` and `… zyggy_zyggy-geoffrey-memory_ed25519 … -C zyggy-geoffrey-memory@central`), paste the public keys on GitHub (`zyggy-geoffrey`: **read-only**; `zyggy-geoffrey-memory`: **Allow write access**; **no key for `zyggy-core`**), seed `~/.ssh/known_hosts` from GitHub's published fingerprints (`https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints` — the `ssh-keyscan` output is accepted only if its fingerprint equals the published one), write `~/.ssh/config` verbatim from the spec (aliases `github.com-zyggy-geoffrey`, `github.com-zyggy-geoffrey-memory`, `IdentitiesOnly yes`), `chmod 700 ~/.ssh; chmod 600 ~/.ssh/*`; (2) convert `/srv/agent/central` in place into the **instance** clone: pre-check that the existing repository has no commits (`git log --oneline 2>/dev/null | wc -l` = 0) and that `claude-remote.log` stays untouched (ignored by `*.log`); `cd /srv/agent/central && git remote add origin git@github.com-zyggy-geoffrey:zyggy-org/zyggy-geoffrey.git && git fetch origin && git checkout -b main origin/main`; `git remote -v` shows only `origin`; record `git rev-parse HEAD` and the contained template SHA (`git merge-base --is-ancestor <template SHA> HEAD; echo $?` → 0); **never add an `upstream` remote on the VM**; (3) `git clone git@github.com-zyggy-geoffrey-memory:zyggy-org/zyggy-geoffrey-memory.git memory` (or, if the repository were empty, create it with the template README's commands); (4) `install -m 600 instance/settings.local.json .claude/settings.local.json`; `stat -c %a .claude/settings.local.json` → `600`; `git check-ignore -v .claude/settings.local.json memory claude-remote.log` lists all three; the AC-4 comparison `diff <(jq -S '{env, autoMemoryDirectory, enabledPlugins}' instance/settings.local.json) <(jq -S '{env, autoMemoryDirectory, enabledPlugins}' .claude/settings.local.json)` → empty; (5) the ancestor `CLAUDE.md` check (AC-1 loop verbatim) and `~/.local/bin/claude doctor` (read-only diagnostics; never `--bare`, `--safe-mode`, `--dangerously-skip-permissions`); (6) the `playwright` plugin at project scope — `claude plugin marketplace list`, `claude plugin install playwright@claude-plugins-official --scope project`, `claude plugin list`, `claude plugin details playwright`, `git -C ~/.claude/plugins/marketplaces/claude-plugins-official rev-parse HEAD`, `git diff --stat .claude/settings.json` and `git status --porcelain` (both empty — the entry arrives committed from the template), inspect `~/.claude/plugins/cache/claude-plugins-official/playwright/<version>/` (its `.mcp.json` / plugin manifest: does the server get `--headless`? record the finding; if headed, stop and record in 0002 — the fix is decided in Step 12), then Chromium: `cd` into that cache directory, `npx playwright --version`, `npx playwright install chromium` as `zyggy`, and as root `sudo npx playwright@<that version> install-deps chromium`; (7) `sudo systemctl restart claude-remote` **once**, then the verification sequence AC-5 → AC-7 (`-p` always with `--no-session-persistence`); (8) the seeding session `/seed-memory` in the remote-control session, `git -C memory diff`, `git -C memory add -A && git -C memory commit -m "seed <date>"`, `git -C memory push -u origin main` (the read/write key's first use), then AC-6, AC-8..AC-11, AC-13, AC-14; (9) AC-33 and the 0002 rows; (10) the first template update through the instance (AC-35). Then the sections: **Re-run the digest by hand** (`cd /srv/agent/central && set -a && . <(jq -r '.env | to_entries[] | "\(.key)=\(.value)"' .claude/settings.local.json) && set +a && .claude/hooks/session-start.sh identity | wc -c`, or `claude --init-only`), **Adding a plugin later** (the spec's mechanism: a **template** plugin = an `enabledPlugins` commit in `zyggy-core`, pulled through the instance, then `--scope project` on the VM; an **instance-only** plugin = `--scope local` on the VM plus the same entry in `instance/settings.local.json`, committed on the laptop and pulled; `"<plugin>": false` there switches a template plugin off; never at user scope on Central; review before install; how to pin auto-update), **Update Central from the template** (laptop: `git -C d:\source\zyggy-geoffrey pull upstream main`, review the merge, `git push origin main`; VM: `git -C /srv/agent/central pull --ff-only`; then `/clear` in the remote-control session and check `/context`; a hook or settings change is verified with the AC-7 `-p` run; if `instance/settings.local.json` changed, re-run the step-4 `install` and AC-4), **Troubleshooting** (one entry per row of the spec's Failure modes table, same titles — 26 rows, including "Template update conflicts", "VM checkout diverged", "Settings drift", "Instance CI red"), **Restore the instance and memory repositories on a fresh VM** (deploy keys for `zyggy-geoffrey` and `zyggy-geoffrey-memory` restored from the owner's password manager or re-issued; clone `zyggy-geoffrey` into `/srv/agent/central` and `zyggy-geoffrey-memory` into `memory/`; `install -m 600 instance/settings.local.json .claude/settings.local.json` — nothing is rewritten by hand; verification AC-4/AC-5/AC-7), and **What the agent may verify read-only** (the `az vm run-command` recipe).
- `runbooks/central-vm-setup.md` and `runbooks/central-vm-steps9-10.sh` *(modify — session name, amended 2026-09-30: naming)* — `--name central` → `--name Zyggy` in the wrapper heredoc (setup lines 234 resume path, 240 fresh path), the manual start (201), the prose (37, and 204 where it says what to drop if `--name` is rejected), and the steps-9–10 script (lines 23, 29). Only that token changes; the resulting heredoc is the reference the owner copies onto the VM in Step 8 and AC-18 diffs against. The agent never edits the VM wrapper.
- `runbooks/central-vm-setup.md` *(modify, one line)* — in "Draft: `restore-central.md`", add step 6: "Central Claude Code configuration (instance `zyggy-geoffrey`, memory `zyggy-geoffrey-memory`, deploy keys, settings, plugins): follow `runbooks/central-claude-config.md` → Restore the instance and memory repositories."
- `_plans/decisions/0002-central-productive.md` *(create, this repo)* — the spec's template: `# 0002 — Central productive first (P0b)`, `Status: open. Closed when 30 is Done.`, **P0b checklist** table with rows 27, 28, 29, 23, 30 (status, evidence), **27 — Central identity, memory repo, plugins** with the Dates line (blanks: config live, `claude-remote` restart, seed commit, `zyggy-core` (template) SHA, `zyggy-geoffrey` (instance) SHA, first template update `<date, template SHA → instance SHA>`, plus — amended 2026-09-30: naming — wrapper `--name Zyggy` applied `<UTC>` and session title after restart `<Zyggy | still central → Zyggy at fresh session <date>>`) and the evidence table pre-filled with one row per AC-1..AC-18, AC-33, AC-34, AC-35, AC-36 (22 rows; Check, Evidence, Result — blank), **Repositories** (three rows: `zyggy-core` template / `zyggy-geoffrey` instance / `zyggy-geoffrey-memory` memory; owner, visibility, laptop checkout + remotes, VM clone path — "not present" for the template, remote alias, key name and scope — "none" for the template), **Plugins**, **Settings** (the four `env` keys and `autoMemoryDirectory` with source `instance/settings.local.json`; the Actions variable `ZYGGY_HYGIENE_FORBIDDEN` per repository — "set", value not pasted), **Credentials on Central** (two deploy keys; "no `zyggy-core` key on Central"), **Deviations from the founding spec / brief** (the spec's list, verbatim, with the OQ-1..OQ-7 answers), **Costs** ("27: none").

**Seams**: none.

**RED** *(the pre-state)*: `Test-Path d:\source\zyggy-geoffrey`, `Test-Path d:\source\zyggy-geoffrey-memory`, `Test-Path d:\source\zyggy\runbooks\central-claude-config.md`, `Test-Path d:\source\zyggy\_plans\decisions\0002-central-productive.md` → all `False`; `Select-String -Path runbooks/central-vm-setup.md -Pattern 'central-claude-config'` → no match. (Documentation and repository-creation step: the behavioural checks are the container runs in VERIFY; manual verification of the documents is the accepted exception, planner rule 6.)

**GREEN**: the files and repositories above. The runbook's commands are copied from this plan's Steps 7–12 so the two never diverge (the runbook is the durable copy; the plan cites it).

**Contract impact**: ⚠️ `instance/settings.local.json` is the committed source of the principal on Central (spec "Instance configuration" row) — no secret, only the four `env` values and a path; the instance adds nothing outside the instance-owned paths (AC-34).

**VERIFY**:
- Instance shape: `git -C d:\source\zyggy-geoffrey remote -v` → `upstream` = the `zyggy-core` URL, `origin` = the `zyggy-geoffrey` URL; `git -C d:\source\zyggy-geoffrey rev-parse upstream/main` = the Gate A template SHA; `git -C d:\source\zyggy-geoffrey diff --cached --name-only upstream/main` → exactly `.claude/rules/instance.md` and `instance/settings.local.json` (AC-34 pre-check; the real AC-34 runs on committed history in Step 7).
- The template's tests pass **in the instance** with the principal present in instance files: `MSYS_NO_PATHCONV=1 podman run --rm -e ZYGGY_HYGIENE_FORBIDDEN=geoffrey -v 'D:\source\zyggy-geoffrey:/w' -w /w zyggy-core-test bash -c 'bats tests/ && shellcheck -S style .claude/hooks/*.sh .claude/skills/*/*.sh tests/*.bash && jq . .claude/settings.json >/dev/null && jq -e "has(\"enabledPlugins\") | not" instance/settings.local.json'` → all green, zero skipped (proves the exemption, AC-31 ≤ 200 lines incl. `instance.md`, and that the instance's first CI run will be green).
- `jq -S . instance/settings.local.json` equals `jq -S .` of the spec's JSON (executor compares against a here-string of the spec block).
- A README-built memory repository works without a skeleton: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -v 'D:\source\zyggy-geoffrey-memory:/m:ro' -w /w -e ZYGGY_MEMORY_ROOT=/m -e ZYGGY_TENANT=geoffrey -e ZYGGY_USER=geoffrey -e ZYGGY_TIMEZONE=Europe/Brussels zyggy-core-test bash -c 'for s in identity index daily; do .claude/hooks/session-start.sh $s </dev/null || exit 1; done; cp -r /m /tmp/m && ZYGGY_MEMORY_ROOT=/tmp/m .claude/skills/remember/remember.sh -- "test fact" && ZYGGY_MEMORY_ROOT=/tmp/m .claude/hooks/stop.sh < tests/fixtures/stop-input.json && ls /tmp/m/geoffrey/geoffrey/inbox /tmp/m/geoffrey/geoffrey/daily'` → three sections (identity = wrapper, data sentence, `## profile.md`, `## preferences.md`, closing tag; exit 0 each), `remember` and `stop` exit 0 and create their files in the **copy** only; afterwards `git -C d:\source\zyggy-geoffrey-memory status --short` still lists only `README.md` and the six `.gitkeep` files (the real tree was mounted read-only).
- `git -C d:\source\zyggy-geoffrey-memory ls-files --cached` → `README.md` + `geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}/.gitkeep`; no other `.md` (AC-3 layout half: nothing outside `geoffrey/geoffrey/` except `README.md`).
- A checklist grep over the runbook: `Select-String -Path runbooks/central-claude-config.md -Pattern 'Execution status|known_hosts|IdentitiesOnly|github.com-zyggy-geoffrey|zyggy_zyggy-geoffrey_ed25519|git remote rename origin upstream|git checkout -b main origin/main|merge-base --is-ancestor|install -m 600 instance/settings.local.json|autoMemoryDirectory|CLAUDE.local.md|--scope project|--scope local|install-deps|--no-session-persistence|/seed-memory|Re-run the digest by hand|Adding a plugin later|Update Central from the template|git pull upstream main|pull --ff-only|Troubleshooting|Restore the instance and memory repositories'` → every pattern matches at least once; `Select-String … -Pattern 'zyggy_zyggy-core_ed25519|github.com-zyggy-core'` → **no** match (no template key or alias on the VM); the Troubleshooting section has one `###`/bold entry per Failure-modes row of the spec (26 rows) — count them.
- `Select-String -Path runbooks/central-vm-setup.md -Pattern 'central-claude-config.md'` → 1 match in the restore draft.
- `_plans/decisions/0002-central-productive.md` has 22 evidence rows (`AC-1`…`AC-18`, `AC-33`…`AC-36`), three Repositories rows, and the section headings of the template.
- `Select-String -Path runbooks/central-vm-setup.md, runbooks/central-vm-steps9-10.sh -Pattern '--name central'` → no match; `-Pattern '--name Zyggy'` → matches at setup lines 37, 201, 234, 240 and script lines 23, 29; `git diff runbooks/central-vm-setup.md runbooks/central-vm-steps9-10.sh` shows only that token changed (plus line 204's prose and the restore pointer).
- `Select-String -Path runbooks/central-claude-config.md, d:\source\zyggy-geoffrey\instance\settings.local.json, d:\source\zyggy-geoffrey\.claude\rules\instance.md -Pattern 'ghp_|BEGIN .*PRIVATE KEY|AKIA'` → no match (no key material, only file names).
- The owner commits this repository's changes when ready; the `zyggy-geoffrey` and `zyggy-geoffrey-memory` commits are the owner's in Step 7 (the agent stops with the summary).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 7 — Three repositories exist on GitHub and the template's and the instance's CI are green; the laptop instance differs from the template only in instance-owned paths; `/srv/agent/central` is the `zyggy-geoffrey` clone (read-only key, no template remote or key) at a recorded SHA containing the template SHA, with `zyggy-geoffrey-memory` at `memory/` and `.claude/settings.local.json` installed from `instance/settings.local.json`; no `CLAUDE.md` exists on or above the working directory

- [x] Done *(checked by the executor when the owner reports the step and the read-only evidence is in 0002)*

**Tag**: [owner, browser + laptop + vm/zyggy] with [agent, read-only VM check] and [agent, laptop] read-only git (`fetch`, `diff`, `ls-files`) afterwards. Runbook steps 1–5.

**Scope** *(what changes, and where)*:
- GitHub: new private repositories `zyggy-org/zyggy-geoffrey` (created **empty** — no README, no licence) and `zyggy-org/zyggy-geoffrey-memory` (the bus organisation); deploy keys: `zyggy-geoffrey` **read-only**, `zyggy-geoffrey-memory` **write access**; **no deploy key on `zyggy-core`**; Actions repository variable `ZYGGY_HYGIENE_FORBIDDEN` on `zyggy-geoffrey` (the `zyggy-core` one was set at Gate A). `zyggy-core` is not changed by this step.
- Laptop (owner): `d:\source\zyggy-geoffrey` — commit the two staged instance files (e.g. `instance: Central rules and settings reference copy`), `git push -u origin main`; `d:\source\zyggy-geoffrey-memory` — commit the layout (`layout`), `git remote add origin …`, `git push -u origin main`.
- VM, as `zyggy`: `~/.ssh/{zyggy_zyggy-geoffrey_ed25519,zyggy_zyggy-geoffrey-memory_ed25519}` (0600) + `.pub`, `~/.ssh/known_hosts` (GitHub fingerprints), `~/.ssh/config` (the spec's two aliases `github.com-zyggy-geoffrey`, `github.com-zyggy-geoffrey-memory`, `IdentitiesOnly yes`); `/srv/agent/central` converted in place into the **instance** clone (`origin` → `git@github.com-zyggy-geoffrey:zyggy-org/zyggy-geoffrey.git`, `main` checked out, no `upstream`, `claude-remote.log` untouched and ignored); `/srv/agent/central/memory` = clone of `zyggy-geoffrey-memory`; `/srv/agent/central/.claude/settings.local.json` installed with `install -m 600 instance/settings.local.json .claude/settings.local.json` (0600, ignored, untracked).
- `_plans/decisions/0002-central-productive.md` *(modify, agent)* — rows AC-1, AC-2, AC-3 (layout half), AC-4, AC-15 (keys/config half), AC-32, AC-34; the Repositories and Credentials tables; `zyggy-core (template) <sha>` and `zyggy-geoffrey (instance) <sha>` on the Dates line.

**Seams**: none (real GitHub and real SSH — owner-run; no test may touch them).

**RED** *(pre-state, agent read-only check before the owner starts)*: `bash -c 'git -C /srv/agent/central remote -v; git -C /srv/agent/central log --oneline | wc -l; ls -la /srv/agent/central /srv/agent/home/.ssh 2>&1'` via `az vm run-command` → no remote, `0` commits, no `.ssh` keys, no `AGENTS.md`, no `memory/`, no `instance/`. On the laptop: `git -C d:\source\zyggy-geoffrey log --oneline upstream/main..HEAD` → empty (the instance files are staged, not yet committed). Recorded as the "before" excerpts.

**GREEN** *(owner, in this order — the runbook carries the same commands)*:
1. `[browser]` create the empty private `zyggy-org/zyggy-geoffrey` and `zyggy-org/zyggy-geoffrey-memory`; set `ZYGGY_HYGIENE_FORBIDDEN` on `zyggy-geoffrey`.
2. `[laptop]` in `d:\source\zyggy-geoffrey`: review `git diff --cached`, commit, `git push -u origin main`; watch the first `zyggy-geoffrey` Actions run → green, the word-list test **ran** (AC-32, instance half). In `d:\source\zyggy-geoffrey-memory`: commit, add `origin`, `git push -u origin main`.
3. `[vm/zyggy]` `ssh -t azureadmin@central` then `sudo -iu zyggy`: generate both keys, `cat ~/.ssh/*.pub`; `[browser]` add each as a deploy key on its repository (`zyggy-geoffrey`: leave "Allow write access" **unchecked**; `zyggy-geoffrey-memory`: checked); seed `known_hosts` (fingerprint compared against GitHub's published page); write `~/.ssh/config`; `chmod 700 ~/.ssh && chmod 600 ~/.ssh/*`; `ssh -T git@github.com-zyggy-geoffrey` and `ssh -T git@github.com-zyggy-geoffrey-memory` both answer "successfully authenticated … does not provide shell access".
4. `[vm/zyggy]` in-place conversion of `/srv/agent/central` (runbook step 2): pre-check 0 commits; `git remote add origin git@github.com-zyggy-geoffrey:zyggy-org/zyggy-geoffrey.git && git fetch origin && git checkout -b main origin/main`; `git rev-parse HEAD` noted; `git merge-base --is-ancestor <template SHA> HEAD; echo $?` → `0`; `ls -la .claude/hooks .claude/skills/remember` shows `-rwxr-xr-x` on the scripts (if not: the Windows executable bit was lost — fix in `d:\source\zyggy-core` with `git update-index --chmod=+x`, owner commits and pushes the template, then the Step 11 update path: `pull upstream main` in the instance, push, `git pull --ff-only` on the VM).
5. `[vm/zyggy]` `git clone git@github.com-zyggy-geoffrey-memory:zyggy-org/zyggy-geoffrey-memory.git memory`; `git -C /srv/agent/central status --porcelain` → empty (memory/ ignored).
6. `[vm/zyggy]` `install -m 600 instance/settings.local.json .claude/settings.local.json`; `git check-ignore -v .claude/settings.local.json memory claude-remote.log` lists all three; the AC-4 `diff` → empty.
7. `[vm/zyggy]` the AC-1 loop and `~/.local/bin/claude doctor` (read-only).

**Contract impact**: ⚠️ §8 Secrets (re-amendment (e′)) — the first credentials on the VM: two repo-scoped deploy keys, 0600, never under a repository, never in a file the agent reads back; the instance key is **read-only** and replaces the earlier-planned `zyggy-core` key; the template has no key and no remote on the VM. ⚠️ The in-place conversion keeps `WorkingDirectory=/srv/agent/central` and the session slug unchanged (§13 Q4 untouched). ⚠️ Instance/template path disjointness (AC-34) is what makes every later `git pull upstream main` conflict-free.

**VERIFY** *(agent, read-only via `az vm run-command` and read-only git on the laptop, then rows in 0002; the owner pastes anything the agent cannot reach)*:
- AC-1: `for d in /srv/agent/central /srv/agent /srv /; do ls -la $d/CLAUDE.md $d/CLAUDE.local.md $d/.claude/CLAUDE.md; done; ls -la /srv/agent/home/.claude/CLAUDE.md` → every line `No such file or directory`.
- AC-2: `cd /srv/agent/central && git remote -v; git rev-parse HEAD; git merge-base --is-ancestor <template SHA> HEAD; echo $?; git status --porcelain; git ls-files .claude/rules/instance.md instance/settings.local.json; ls -la AGENTS.md .claude/settings.json .claude/hooks .claude/skills .claude/rules .claude/rules/instance.md instance/settings.local.json PROTOCOL.md` (as root: prefix git with `runuser -u zyggy --` — see Notes) → exactly one remote, `origin` → `github.com-zyggy-geoffrey` (no `upstream`, no `zyggy-core` remote); HEAD equal to `git -C d:\source\zyggy-geoffrey rev-parse origin/main`; `0` from `merge-base`; empty status; both instance files tracked; `-rwxr-xr-x` on `session-start.sh`, `stop.sh`, `lib.sh`, `remember.sh`.
- AC-3 (layout): `git -C /srv/agent/central/memory remote -v; git -C /srv/agent/central/memory log --oneline; cd /srv/agent/central/memory && find . -path ./.git -prune -o -type d -print` → the remote, the layout commit, `./geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}`, nothing outside `geoffrey/geoffrey/` but `README.md`; no identity files yet (Step 9).
- AC-4: `stat -c %a /srv/agent/central/.claude/settings.local.json` → `600`; `git check-ignore -v .claude/settings.local.json` → ignored; `diff <(jq -S '{env, autoMemoryDirectory, enabledPlugins}' instance/settings.local.json) <(jq -S '{env, autoMemoryDirectory, enabledPlugins}' .claude/settings.local.json)` → empty; `jq -r '.autoMemoryDirectory'` equals `"\(.env.ZYGGY_MEMORY_ROOT)/\(.env.ZYGGY_TENANT)/\(.env.ZYGGY_USER)/auto"` computed with `jq` from the same file.
- AC-15 (keys): `ls -la /srv/agent/home/.ssh/; cat /srv/agent/home/.ssh/config` → exactly two private keys `zyggy_zyggy-geoffrey_ed25519` and `zyggy_zyggy-geoffrey-memory_ed25519`, `-rw-------` owned `zyggy`; two aliases with `IdentitiesOnly yes`; `ls /srv/agent/home/.ssh | grep -c zyggy-core` → `0`; the owner confirms on GitHub that the `zyggy-geoffrey` key is read-only and the `zyggy-geoffrey-memory` key read/write; the agent never `cat`s a private key.
- AC-32: the owner pastes the green Actions run URLs of `zyggy-core` (Gate A) and `zyggy-geoffrey` (this step), each log showing the word-list test ran.
- AC-34 *(agent, laptop, read-only)*: `git -C d:\source\zyggy-geoffrey remote -v` → `origin` → `zyggy-geoffrey`, `upstream` → `zyggy-core`; `git -C d:\source\zyggy-geoffrey fetch upstream`; `git -C d:\source\zyggy-geoffrey diff --name-only upstream/main HEAD` → only `.claude/rules/instance.md` and `instance/settings.local.json`; `git -C d:\source\zyggy-core ls-files | Select-String -Pattern '^(instance/|\.claude/rules/instance(\.md$|/)|\.claude/skills/instance-)'` → nothing.
- 0002: rows AC-1, AC-2, AC-3 (layout), AC-4, AC-15 (keys), AC-32, AC-34 dated; Repositories (three rows) and Credentials tables filled (key names, scopes, "rotation: revoke on GitHub, re-issue per runbook step 1"; "no `zyggy-core` key on Central").

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — The `playwright` plugin is installed at project scope on Central; a headless `claude -p --no-session-persistence` in `/srv/agent/central` receives the three digest sections as `hook_response` events and leaves no session file; after the one deliberate `claude-remote` restart the remote-control session reports `no CLAUDE.md found; AGENTS.md loaded`, lists the template's three rule files and the instance's `instance.md`, and still answers *pineapple*; the wrapper now names the session `Zyggy` (AC-36)

- [x] Done *(checked by the executor when the owner reports the step and the evidence is in 0002)*

**Tag**: [owner, vm/zyggy + vm/root + phone/claude.ai] with [agent, read-only VM check]. Runbook steps 6 (plugin half, not Chromium) and 7. The wrapper edit on the VM is the owner's; the agent only diffs it read-only.

**Scope**:
- VM: `~zyggy/.claude/plugins/cache/claude-plugins-official/playwright/<version>/` (downloaded by the install), `~zyggy/.claude/plugins/installed_plugins.json` (project scope entry), `~zyggy/.claude/settings.json` (only what `claude plugin marketplace` writes, nothing added by hand); `/srv/agent/central/.claude/settings.json` unchanged (`git diff` empty — the entry is committed in the template, OQ-6) and the instance tree clean (`git status --porcelain` empty); `instance/settings.local.json` carries no `enabledPlugins` in 27.
- `/srv/agent/bin/claude-remote.sh` (owner, root): `--name central` → `--name Zyggy` on the resume path and the fresh-start path — exactly the token the Step 6 runbook heredoc already shows (amended 2026-09-30: naming); applied **immediately before** the restart.
- `claude-remote.service`: one `systemctl restart` (units, `claude-soak.sh` and timer files untouched; the wrapper differs from its 02 version only by that token — relaxed AC-18).
- `_plans/decisions/0002-central-productive.md` *(modify, agent)* — rows AC-5, AC-7, AC-12 (install half), AC-36, `wrapper --name Zyggy applied <UTC>` and `session title after restart <…>` on the Dates line, the Plugins table (name, version, marketplace commit, author "external — Microsoft Playwright MCP server, listed by Anthropic", always-on tokens, purpose, "MCP servers/hooks it adds"), the Settings table, `config live <UTC>` and `claude-remote restart <UTC>` on the Dates line; `_plans/decisions/0001-transport-and-vm.md` *(modify, agent)* — the restart row gains "2 of 2 — 27's deliberate restart <UTC>, resumed `<id>`, pineapple" (spec Finding 7).

**Seams**: none.

**RED** *(pre-state, agent read-only)*: `bash -c 'cat /srv/agent/home/.claude/plugins/installed_plugins.json 2>&1; ls /srv/agent/home/.claude/plugins/cache/claude-plugins-official/ 2>&1; tail -c 300 /srv/agent/central/claude-remote.log'` → no `playwright` entry, no cache directory, log's last line is the 2026-09-30 05:44 resume.

**GREEN** *(owner)*:
1. `[vm/zyggy]` `cd /srv/agent/central && claude plugin marketplace list` (official marketplace present since 02 step 8; else `claude plugin marketplace add anthropics/claude-plugins-official`), `claude plugin install playwright@claude-plugins-official --scope project`, `claude plugin list`, `claude plugin details playwright`, `git -C ~/.claude/plugins/marketplaces/claude-plugins-official rev-parse HEAD`, `git diff --stat .claude/settings.json` and `git status --porcelain` (both empty), `jq '.' ~/.claude/plugins/installed_plugins.json`; inspect the plugin's cache directory for the MCP server definition and note whether `--headless` is passed (recorded in 0002; the Chromium install and the browser test are Step 12).
2. `[vm/zyggy]` AC-7 exactly: `ls -t ~/.claude/projects/-srv-agent-central/ | head -1` (note it), then `claude -p --no-session-persistence --output-format stream-json --verbose --include-hook-events --max-turns 1 --permission-mode auto "Reply with the word OK." > /tmp/ac7.jsonl`, then `ls -t ~/.claude/projects/-srv-agent-central/ | head -1` (unchanged), `jq -c 'select(.type=="hook_response" or .hook_event_name=="SessionStart") | {hook_event_name, exit: .exit_code, len: (.output|length), head: (.output|.[0:60]), tail: (.output|.[-24:])}' /tmp/ac7.jsonl` (field names to be confirmed against the actual event shape — record the real ones), `jq -r 'select(.type=="result") | .result' /tmp/ac7.jsonl` → `OK`. Expect three `SessionStart` responses whose outputs start with `<zyggy-memory-digest section="` and end with `</zyggy-memory-digest>`, each < 10,000 characters, Σ ≤ 18,000, none replaced by a file path/preview. With the README-built memory (no identity files yet), `identity` holds the two headings only, `index` the `## agents.md` heading and an empty index, `daily` nothing — the shape is what is verified now; content in Step 9.
3. `[vm/root]` wrapper edit, then at once the restart: `sudo sed -i 's/--name central /--name Zyggy /' /srv/agent/bin/claude-remote.sh` (or edit by hand), `grep -c -- '--name Zyggy' /srv/agent/bin/claude-remote.sh` → `2`, `grep -c -- '--name central' …` → `0`, `bash -n /srv/agent/bin/claude-remote.sh` → silent, `diff` against the Step 6 runbook heredoc → identical; then `sudo systemctl restart claude-remote` **(the one restart; note the UTC time of both)**; `systemctl is-active claude-remote` → `active` (if it loops: runbook "Wrapper edit"); `tail -3 /srv/agent/central/claude-remote.log` → `resuming 6ba6d03b-…` (the same id as before).
4. `[phone / claude.ai]` open the Central session (`Zyggy`, or still `central` if the resumed session keeps its title): the conversation shows `no CLAUDE.md found; AGENTS.md loaded: /srv/agent/central/AGENTS.md`; `/memory` and `/context` list `AGENTS.md`, the template's three `.claude/rules/*.md` **and** the instance's `.claude/rules/instance.md`, no `CLAUDE.md`; ask "Which word did I ask you to remember?" → *pineapple* (AC-5). Then the session list on the phone (Claude app → Code) and at claude.ai/code: the Central session is listed as **`Zyggy`** (AC-36); if it still shows `central`, record it — no extra restart; AC-36 is re-checked at the next fresh session the wrapper starts (runbook "Session title"; optional rename from the app).
5. `[vm/zyggy]` `tail -n 1 /srv/agent/soak/soak.jsonl | jq '{ts, exit}'` after the next timer run → `exit 0` (AC-12: the soak is unaffected by a project-scoped plugin; if the next run is hours away, record the most recent line and re-check in Step 12).

**Contract impact**: ⚠️ external plugin code (Microsoft's Playwright MCP server) now runs with the assistant's permissions on Central, project scope only — provenance recorded (§8, amendment (i)). ⚠️ one restart of the 02 soak's unit, counted in 0001. ⚠️ The one sanctioned change to a 02 file on the VM (the `--name Zyggy` token); a typo stops the remote session — hence `bash -n` and the heredoc `diff` before the restart.

**VERIFY** *(agent read-only + owner-pasted excerpts → 0002)*:
- AC-36: the owner pastes the session-list title (`Zyggy`, or `central` + the planned re-check); 0002 row and Dates line filled.
- AC-7: the `jq` excerpt with three hook responses (lengths, heads, tails), `OK`, and the unchanged newest-session file name before/after — pasted into 0002.
- AC-5: `tail -3 /srv/agent/central/claude-remote.log` (agent, read-only) shows the restart time and `resuming 6ba6d03b…`; the owner pastes the `AGENTS.md loaded` line and the `/memory` list.
- AC-12 (install half): `jq '.plugins | keys' ~zyggy/.claude/plugins/installed_plugins.json`-style excerpt (exact shape as found), `claude plugin list` output, marketplace commit SHA, always-on tokens, all in the 0002 Plugins table; `jq -e '.enabledPlugins' /srv/agent/central/.claude/settings.json` → exactly the one entry; `jq 'has("enabledPlugins")' /srv/agent/central/instance/settings.local.json` → `false`; `jq -e '.enabledPlugins // empty' /srv/agent/home/.claude/settings.json` → empty (no user-scope plugin); `git -C /srv/agent/central status --porcelain` → empty after the install.
- AC-18 (interim): `systemctl cat claude-remote claude-soak.service claude-soak.timer; cat /srv/agent/bin/claude-remote.sh /srv/agent/bin/claude-soak.sh` (agent, read-only) — byte-identical to the heredocs in `runbooks/central-vm-setup.md` **as updated in Step 6** (the agent diffs them locally); against the 02 version `claude-remote.sh` differs by exactly `--name central` → `--name Zyggy` on two lines (relaxed AC-18); `systemctl show claude-remote -p NRestarts` noted.
- 0001: the restart row updated to "2 of 2".

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (Central is the `zyggy-geoffrey` instance checkout; `AGENTS.md`, `instance.md` and the digest are visible) *(covers Steps 6–8)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: 0002 shows dated rows AC-1, AC-2, AC-3 (layout), AC-4, AC-5, AC-7, AC-12 (install half), AC-15 (keys), AC-32, AC-34 with excerpts; the remote-control session displayed `no CLAUDE.md found; AGENTS.md loaded: /srv/agent/central/AGENTS.md`, listed the three template rule files and `instance.md`, and answered *pineapple*; the AC-7 run produced three `hook_response` sections, no new session file; `zyggy-core` and `zyggy-geoffrey` CI green on GitHub, the word-list test ran in both; the template's tests passed in the instance in the container (Step 6) and the README-built memory tree served all three digest sections.
- [ ] Contract review (three repositories): `/srv/agent/central` HEAD equals `origin/main` of `zyggy-geoffrey` and contains the recorded `zyggy-core` SHA (both in 0002); the VM has only `origin` (no `upstream`, no `zyggy-core` remote, clone or key); `d:\source\zyggy-geoffrey` differs from the template only in `.claude/rules/instance.md` and `instance/settings.local.json` (AC-34) and the template lists no instance-owned path; the live `.claude/settings.local.json` was **installed** from `instance/settings.local.json` (mode 600, ignored, AC-4 `diff` empty) and holds exactly the four `env` keys and `autoMemoryDirectory` (no `ZYGGY_NOW`, no `ZYGGY_HOOKS`, no `enabledPlugins`); `memory/` layout matches §7 with `auto/`; the runbook `central-claude-config.md` has every section AC-16 names (ten steps, three repositories, instance creation, in-place conversion into the instance clone, "Update Central from the template", "Restore the instance and memory repositories") and the 02 runbook's restore draft points to it; 0002 has the template's sections with 22 AC rows and three Repositories rows; the 02 runbook and `central-vm-steps9-10.sh` say `--name Zyggy`, and the VM wrapper differs from them by nothing (AC-18 relaxed: one token on two lines vs the 02 version); the session list shows `Zyggy` (AC-36), or `central` recorded in 0002 with the re-check at the next fresh session.
- [ ] Owner runs the prompt audit on the **instance** on the laptop (covers `instance.md`, which did not exist at Gate A): in PowerShell `cd d:\source\zyggy-geoffrey; $env:ZYGGY_HOOKS='off'; claude` then `/doctor prompt-audit` → no contradiction between `instance.md` and the template's rules (it adds or tightens, never relaxes). A finding in `instance.md` is fixed by the executor in `d:\source\zyggy-geoffrey` (owner commits, pushes, `git pull --ff-only` on the VM); a finding in a template file is fixed in `d:\source\zyggy-core` and becomes the AC-35 change of Step 11.
- [ ] ⚠️ Risk review: deploy keys — two files, 0600, `zyggy`-owned, `IdentitiesOnly yes`; `zyggy-geoffrey` key **read-only** on GitHub, `zyggy-geoffrey-memory` key write (owner confirms in each repository's settings); **no key for `zyggy-core` on the VM**; `known_hosts` seeded from the published fingerprints, not a first-connection prompt; no key material in any repository, runbook or decision record (`grep` in Step 13 repeats it); the principal appears only in instance-owned files, the memory repository's directory names, this repository's runbook and 0002 — never in a template file; the plugin is external (Microsoft) — version, marketplace commit and always-on tokens recorded, project scope only, auto-update left on (*overturnable* — owner may decide to pin now); the restart is the one and only, recorded in 0001 as "2 of 2"; units and `claude-soak.sh` byte-identical to the 02 runbook, `claude-remote.sh` identical to the Step 6 heredoc and differing from the 02 version only by `--name Zyggy` on two lines (relaxed AC-18).
- [ ] Owner decides whether to add an `evolution/` ignore line to `zyggy-geoffrey-memory` (not in the spec's README commands; the evolve plugin writes journals into any repository a session opens in) and confirms no stray `evolution/` sits in the memory tree before the Step 9 seed commit.
- [ ] Owner commits this repository's changes (runbook, runbook `--name Zyggy` lines, 0001, 0002) when satisfied.
- [ ] User approved — implementation may continue past this gate

---

## Step 9 — The owner's memory is seeded through the `seed-memory` interview in the remote-control session, reviewed, committed and pushed from the VM with the read/write key; a fresh headless run answers from `profile.md`; Claude Code's auto memory lands inside the memory repository

- [ ] Done *(checked by the executor when the owner reports the step and the evidence is in 0002)*

**Tag**: [owner, phone/claude.ai + vm/zyggy] with [agent, read-only VM check]. Runbook step 8 (first half).

**Scope**:
- `zyggy-geoffrey-memory` on the VM: `geoffrey/geoffrey/profile.md`, `preferences.md`, `agents.md` — **created** by `/seed-memory` with front matter (`name`, `description`, `updated`), since the README-built repository ships none (Step 5b edit of the skill) — with bodies of `[stated] 2026-…` lines, in English, new `areas/<slug>.md`, `people/<slug>.md`, `topics/<slug>.md` with front matter; the seed commit `seed <date>` pushed to `origin/main`; `auto/MEMORY.md` created by Claude Code (uncommitted; the dream pass commits it from 28).
- `_plans/decisions/0002-central-productive.md` *(modify, agent)* — rows AC-3 (full), AC-6, AC-11, AC-14; `seed commit <sha>` on the Dates line.

**Seams**: none.

**RED** *(pre-state, agent read-only)*: `bash -c 'cd /srv/agent/central/memory && git log --oneline && ls -A geoffrey/geoffrey geoffrey/geoffrey/areas geoffrey/geoffrey/auto'` → one layout commit; `geoffrey/geoffrey/` holds only the six directories (no `profile.md`, `preferences.md`, `agents.md`); `.gitkeep` only inside them.

**GREEN** *(owner)*:
1. `[phone / claude.ai]` in `central`: `/seed-memory`; answer the six blocks (in any language; files are written in English); when the skill prints `git -C memory status`, review.
2. `[vm/zyggy]` `cd /srv/agent/central/memory && git diff && git status` — review every line: only `[stated] <date>` lines and front matter, no secret, no health/personality inference, no name outside `memory/`; `git add -A && git commit -m "seed $(date -u +%F)" && git push -u origin main` (first use of the read/write key).
3. `[phone / claude.ai]` ask "What do you know about me, and where does that knowledge come from?" → the answer cites `profile.md`/`preferences.md` facts and names the memory digest as the source (AC-6; the seeded facts arrive in this session's context only after a `/clear` or `/compact` re-runs `SessionStart` — do `/clear` first, which the matcher covers, and note it in 0002).
4. `[vm/zyggy]` AC-11: `claude -p --no-session-persistence --permission-mode auto "What is the first heading of your project instructions, and what is my first name?"` → names `# Zyggy — Central` and the owner's first name; `ls -t ~/.claude/projects/-srv-agent-central/ | head -1` unchanged; `tail -1 memory/geoffrey/geoffrey/daily/$(TZ=Europe/Brussels date +%F).md` shows the Stop line of that run.
5. `[phone / claude.ai]` AC-14: in a turn where Claude reports "Saved N memories" (or after asking it to note a working preference in its auto memory), `ls memory/geoffrey/geoffrey/auto/` shows `MEMORY.md`; `ls ~/.claude/projects/-srv-agent-central/memory/ 2>&1` shows nothing newer than the `settings.local.json` write time.

**Contract impact**: §7 File format (normative) is now populated for the real principal; nothing new.

**VERIFY** *(agent read-only → 0002)*:
- AC-3: `git -C /srv/agent/central/memory log --oneline; git -C /srv/agent/central/memory status --porcelain; find /srv/agent/central/memory -path '*/.git' -prune -o -type d -print` → seed commit on top of the layout commit, `auto/MEMORY.md` as the only untracked item, all six directories, `profile.md`/`preferences.md`/`agents.md` present; `git -C /srv/agent/central/memory rev-parse origin/main` equals HEAD (pushed).
- Front matter check: `head -6 /srv/agent/central/memory/geoffrey/geoffrey/{profile,preferences,agents}.md` show `name`, `description`, `updated`; `grep -c '^- \[stated\] ' …/profile.md` > 0; `grep -rnE '^- \[(inferred|guessed)\]' …/geoffrey/geoffrey` → nothing.
- AC-6, AC-11, AC-14 excerpts pasted by the owner; the agent confirms the `daily/<today>.md` line for the AC-11 run (`tail -3`) and the absence of a new session file (`ls -t | head -1` unchanged) read-only.
- Secret sweep on the seeded memory: `cut -f2 /srv/agent/central/.claude/hooks/secret-patterns.txt | grep -v '^#' > /tmp/pat; grep -rEn -f /tmp/pat /srv/agent/central/memory --exclude-dir=.git` → nothing.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — "Remember that …" lands as a `[stated]` line in `inbox/`, a token is refused by name without being echoed, every turn leaves its `[observed]` line in `daily/`, and `/doctor prompt-audit` finds no contradiction across `AGENTS.md`, the rules and the skills

- [ ] Done *(checked by the executor when the owner reports the step and the evidence is in 0002)*

**Tag**: [owner, phone/claude.ai + vm/zyggy] with [agent, read-only VM check]. Runbook step 8 (second half).

**Scope**:
- `zyggy-geoffrey-memory` on the VM: `geoffrey/geoffrey/inbox/remember-<date>.md` (one new line), `daily/<date>.md` (lines per turn) — uncommitted (Azure Backup covers them until 28's dream commits).
- `_plans/decisions/0002-central-productive.md` *(modify, agent)* — rows AC-8, AC-9, AC-10, AC-13.
- If AC-13 finds something — never by editing files on the VM, never by a restart: a finding in `.claude/rules/instance.md` → the executor fixes it in `d:\source\zyggy-geoffrey` (instance-owned path; staged), the owner commits and pushes, `git -C /srv/agent/central pull --ff-only` (owner), `/clear`, re-run; a finding in a **template** file (`AGENTS.md`, the three rule files, a skill) → the executor fixes it in `d:\source\zyggy-core` (staged, PROVE loop green in the container) and it travels as the AC-35 change of Step 11, after which the audit is re-run there.

**Seams**: none.

**RED** *(pre-state, agent read-only)*: `ls /srv/agent/central/memory/geoffrey/geoffrey/inbox/` → `.gitkeep` only.

**GREEN** *(owner, in the remote-control session)*:
1. "Remember that my favourite tea is Earl Grey." → Claude runs `remember.sh` and quotes `remembered: /srv/agent/central/memory/geoffrey/geoffrey/inbox/remember-<date>.md` + the line (AC-8).
2. "Remember that my GitHub token is ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" → Claude reports the refusal naming `github-token`, does not echo the token, does not retry (AC-9).
3. Any turn → `[vm/zyggy]` `tail -3 memory/geoffrey/geoffrey/daily/$(TZ=Europe/Brussels date +%F).md` shows `- [observed] HH:MM session <8 chars>: …` (AC-10); `head -5` shows the front matter once with `updated:` = today.
4. `/doctor prompt-audit` → no contradiction or missing-file finding across `AGENTS.md`, the template's three rule files, the instance's `instance.md` and the `remember`/`seed-memory` skills (AC-13); paste the result.

**Contract impact**: none.

**VERIFY** *(agent read-only → 0002)*:
- AC-8: `cat /srv/agent/central/memory/geoffrey/geoffrey/inbox/remember-<date>.md` → front matter (`name: remember <date>`, `description: facts stated by the owner on <date> (remember skill)`, `updated: <date>`) once, one line `- [stated] <date>: … Earl Grey …`.
- AC-9: `grep -rn 'ghp_' /srv/agent/central/memory` → nothing; the owner pastes Claude's refusal sentence and the Bash tool's exit code 2 from the transcript.
- AC-10: the `tail -3` excerpt; `grep -c '^- \[observed\] ' …/daily/<today>.md` ≥ number of turns so far; `grep -c '^---$' …/daily/<today>.md` = 2.
- AC-13: the audit output pasted — clean; or clean after an `instance.md` fix pulled with `--ff-only`; or the template finding recorded with its staged fix in `d:\source\zyggy-core` and the row marked "re-run in Step 11" (this step is then Done with AC-13 pending Step 11's re-run).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 11 — A template change reaches Central through the instance: it merges into `d:\source\zyggy-geoffrey` with `git pull upstream main` without a conflict, is pushed, and the VM fast-forwards to an instance SHA that contains the new template SHA, with a clean tree; the running session picks it up after `/clear`

- [ ] Done *(checked by the executor when the owner reports the step and the evidence is in 0002)*

**Tag**: [agent, laptop] (authors and proves the template change, staged) → [owner, laptop + vm/zyggy + phone/claude.ai] (commit, merge, push, pull) with [agent, read-only VM check] and read-only git on the laptop. Runbook step 10 (AC-35) and its section "Update Central from the template".

**Scope**:
- `d:\source\zyggy-core` *(modify, agent)* — **the change**: the AC-13 template fix staged in Step 10 when there is one; otherwise a deliberate one-line `README.md` change chosen at the time that keeps AC-30 green (for example, in "Tests" or the opening paragraph, the Claude Code version the template was last verified with — no machine path, no principal). Staged; PROVE loop green in the container (with `-e ZYGGY_HYGIENE_FORBIDDEN=geoffrey`).
- `zyggy-core` on GitHub (owner): commit, `git push origin main`; its CI green.
- `d:\source\zyggy-geoffrey` (owner): `git pull upstream main` — a merge commit on the laptop (the instance has its own commit on top of the template, so it is a true merge, not a fast-forward); review; `git push origin main`; its CI green.
- `/srv/agent/central` (owner, as `zyggy`): `git pull --ff-only`.
- `_plans/decisions/0002-central-productive.md` *(modify, agent)* — row AC-35 (both SHAs, date), the Dates line "first template update `<date, template SHA → instance SHA>`", and the AC-13 row completed if the change was the audit fix.
- `runbooks/central-claude-config.md` *(modify, agent)* — "Update Central from the template" and the status row of runbook step 10 adjusted to what actually happened.

**Seams**: none (real git over real remotes — owner-run; no test touches GitHub).

**RED** *(pre-state, agent read-only)*: laptop — `git -C d:\source\zyggy-geoffrey fetch upstream` then `git -C d:\source\zyggy-geoffrey log --oneline HEAD..upstream/main` → empty (the instance already contains the template); VM — `git -C /srv/agent/central rev-parse HEAD` = the Step 7 instance SHA. After the owner pushes the template change (GREEN 1) and before the merge: `git -C d:\source\zyggy-geoffrey log --oneline HEAD..upstream/main` lists exactly the new template commit, and on the VM `git merge-base --is-ancestor <new template SHA> HEAD; echo $?` → non-zero (the object is not even present) — the behaviour "the VM runs the new template" is absent.

**GREEN** *(agent then owner, in this order — the runbook carries the same commands)*:
1. `[agent, laptop]` stage the change in `d:\source\zyggy-core`, PROVE in the container; `[owner, laptop]` review, commit, `git push origin main`; `zyggy-core` Actions green.
2. `[owner, laptop]` `git -C d:\source\zyggy-geoffrey pull upstream main` → the merge completes with no conflict; `git -C d:\source\zyggy-geoffrey diff --name-only upstream/main HEAD` → still only the two instance-owned paths; `git push origin main`; `zyggy-geoffrey` Actions green.
3. `[owner, vm/zyggy]` `git -C /srv/agent/central pull --ff-only` → "Fast-forward"; `git status --porcelain` → empty.
4. `[owner, phone/claude.ai]` `/clear` in the `central` session, then `/context` (instruction files listed as before); if the change was the AC-13 fix, `/doctor prompt-audit` again → clean. A hook or settings change would additionally be verified with the AC-7 `-p` run (runbook), and a change of `instance/settings.local.json` with the step-4 `install` + AC-4 — neither applies to a README line.

**Contract impact**: ⚠️ first exercise of the template→instance→VM path (spec "Instance-owned paths", Behaviors "Template updates reach Central only as laptop merge → push → VM `git pull --ff-only`"); the VM never merges, commits or holds a template remote.

**VERIFY** *(agent read-only → 0002)*:
- AC-35 laptop: `git -C d:\source\zyggy-geoffrey log -1 --format='%H %P'` → a merge commit whose parents are the previous instance HEAD and the new template SHA; `git -C d:\source\zyggy-geoffrey rev-parse origin/main` equals HEAD (pushed); `git -C d:\source\zyggy-geoffrey diff --name-only upstream/main HEAD` → only the instance-owned paths (AC-34 still holds).
- AC-35 VM: `cd /srv/agent/central && git rev-parse HEAD` = the new instance SHA; `git merge-base --is-ancestor <new template SHA> HEAD; echo $?` → `0`; `git status --porcelain` → empty; `git remote -v` → still only `origin`; `ls -la .claude/hooks/*.sh` → still `-rwxr-xr-x`.
- The owner pastes both Actions run URLs (green) and, when applicable, the clean audit.
- 0002: AC-35 row dated with template SHA → instance SHA; AC-13 row final.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 12 — Central opens a web page through the `playwright` plugin with headless Chromium, answers `Example Domain`, closes the browser, and stays within the 4 GB RAM budget; the plugin is fully recorded (`/plugin`, `/mcp`, soak unaffected)

- [ ] Done *(checked by the executor when the owner reports the step and the evidence is in 0002)*

**Tag**: [owner, vm/zyggy + vm/root + phone/claude.ai] with [agent, read-only VM check]. Runbook step 6 (Chromium half) and step 9 (AC-33).

**Scope**:
- VM: Chromium for the plugin's Playwright version under `~zyggy/.cache/ms-playwright/` (installed as `zyggy`), system libraries via `install-deps` (root); nothing in `zyggy-core` or `zyggy-geoffrey` unless the headless finding of Step 8 requires it (see below).
- `_plans/decisions/0002-central-productive.md` *(modify, agent)* — rows AC-12 (complete), AC-33; Plugins table "browser: Chromium <version>, headless, one at a time"; the RAM reading.

**Seams**: none.

**RED** *(pre-state, owner)*: in the remote-control session, "Open https://example.com with the browser and tell me the page title" **before** Chromium is installed → the MCP tool returns a launch error naming the missing executable (the Failure-modes row "Playwright launch"); paste it as the "before" excerpt. (Skip this probe if the owner prefers; the RED then is `ls ~/.cache/ms-playwright/` → absent.)

**GREEN** *(owner)*:
1. `[vm/zyggy]` `cd ~/.claude/plugins/cache/claude-plugins-official/playwright/<version>/ && npx playwright --version && npx playwright install chromium` (note the Playwright and Chromium versions).
2. `[vm/root]` `sudo npx playwright@<same version> install-deps chromium` (the `--with-deps` half; the only root action of 27).
3. Headless: if Step 8's inspection showed the plugin's server is not started with `--headless`, **stop here and decide with the owner** (this is the ⚠️ decision point; the spec does not settle where the fix lives under the template/instance split): the runbook lists the options in order — (a) the plugin's own option/config mechanism if it has one, (b) an environment setting the server honours, (c) as a last resort a project-level `.mcp.json` at the root that starts `@playwright/mcp@<same version> --headless --browser chromium`, with the plugin's server disabled to avoid two browsers. Placement follows the split: something every instance needs (the template enables the plugin, OQ-6) is a **template** change delivered through the Step 11 path; an instance-only setting goes in an instance-owned path (`instance/settings.local.json`, then the step-4 `install` + AC-4). `.mcp.json` is **not** an instance-owned path, and spec Finding 8 leaves "template ships it vs becomes instance-owned" to 11/23 — so (c) needs an explicit owner decision recorded in 0002 before anything is written. If the plugin already runs headless, nothing to do.
4. `[phone / claude.ai]` "Open https://example.com with the browser and tell me the page title" → `Example Domain`; meanwhile `[vm/zyggy]` `free -h` during the fetch and `pgrep -a chrom` after the answer (no process left); `[vm/root]` `journalctl -k | grep -i 'out of memory'` → nothing (AC-33).
5. `[phone / claude.ai]` `/plugin` → Installed lists `playwright` (project); `/mcp` → its server connected (AC-12).
6. `[vm/zyggy]` `tail -n 1 /srv/agent/soak/soak.jsonl | jq '{ts, exit}'` → the run after the install has `exit 0`.

**Contract impact**: ⚠️ work-boundary (§8): the browser rule in `security.md` ("unattended runs never use logged-in sites until 18–20") is the only guard until the policy deliverables; the owner confirms it is in the loaded rules (`/memory` lists `security.md`).

**VERIFY** *(agent read-only → 0002)*:
- `ls /srv/agent/home/.cache/ms-playwright/` → one `chromium-<build>` directory (plus headless shell if the version ships it); `jq '.' /srv/agent/home/.claude/plugins/installed_plugins.json` version noted.
- The owner pastes: the `Example Domain` answer, the `free -h` line during the fetch (used memory delta ≈ 300–500 MB, swap unchanged), the empty `journalctl -k` grep, `/plugin` and `/mcp` screenshots or text, the soak line.
- `free -h` and `swapon --show` (agent, read-only, after the task) → swap use unchanged from the 02 day-0 reading; `pgrep -a chrom` → nothing.
- 0002: AC-12 and AC-33 rows dated; the Plugins table complete (name, version, marketplace commit, author, always-on tokens, purpose, MCP server it adds, Chromium build, headless mechanism, RAM reading).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 13 — A fresh VM can be rebuilt from the runbook and the decision record alone: every AC has a dated evidence row, no secret exists in any of the three repositories or either settings file, the 02 units are byte-identical, and the roadmap status marks 27 done

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the final 🛑 HUMAN GATE)*

**Tag**: [agent, laptop] with [agent, read-only VM check]. Files in this repository only (plus read-only checks of the three local repositories).

**Scope**:
- `_plans/decisions/0002-central-productive.md` *(modify)* — every row AC-1..AC-18, AC-33..AC-36 dated with command + excerpt + result; Dates line complete (`config live`, `claude-remote restart`, `seed commit`, `zyggy-core` (template) SHA, `zyggy-geoffrey` (instance) SHA, first template update); P0b checklist row 27 → "Done (evidence below)"; Deviations section final (incl. the three-repository, reference-copy and hygiene-variable deviations with OQ-5..OQ-7); Costs "27: none".
- `runbooks/central-claude-config.md` *(modify)* — execution-status table (ten rows) updated to what actually happened (dates, the headless finding, any fix, the first template update); Troubleshooting entries adjusted to the observed messages (e.g. the real Playwright launch error text, the real `hook_response` field names); "Restore the instance and memory repositories on a fresh VM" finalised.
- `_plans/ROADMAP.md` *(modify, the #27 status cell only)* — `Done 2026-…` with the evidence pointer. The #27 **done-line** (OQ-5 wording "`zyggy-core` (template), `zyggy-geoffrey` (instance) and the memory repo each have a first commit and a remote; the template's and the instance's CI are green") is the **project-manager's** edit and the change-log row the owner's — the executor touches neither.
- `memory/short-term.md` *(modify)* — one dated bullet each for the gotchas the executor met (executable bit from Windows, `hook_response` shape, headless mechanism) — the working-agent duty, not a plan artefact.

**Seams**: none.

**RED** *(pre-state)*: `Select-String -Path _plans/decisions/0002-central-productive.md -Pattern '\| AC-\d+ \|.*\|\s*\|\s*\|$'` → rows with empty evidence exist (AC-15 second half, AC-16, AC-17, AC-18); `Select-String -Path _plans/ROADMAP.md -Pattern '^\| 27 \|.*Planning|In progress'` → matches.

**GREEN**:
- AC-15 (second half), agent read-only on the VM: `cut -f2 /srv/agent/central/.claude/hooks/secret-patterns.txt | grep -v '^#' > /tmp/pat; grep -rEn -f /tmp/pat /srv/agent/central --exclude-dir=.git --exclude-dir=memory --exclude=secret-patterns.txt --exclude-dir=tests; grep -rEn -f /tmp/pat /srv/agent/central/memory --exclude-dir=.git; grep -En -f /tmp/pat /srv/agent/home/.claude/settings.json /srv/agent/central/.claude/settings.local.json; grep -rEn -f /tmp/pat /srv/agent/central/instance` → all empty; on the laptop the same patterns over the three local checkouts' tracked files outside `tests/` and `secret-patterns.txt` (`git -C d:\source\<repo> grep -nE -f <patterns> -- ':!tests' ':!.claude/hooks/secret-patterns.txt'` for `zyggy-core`, `zyggy-geoffrey`, `zyggy-geoffrey-memory`) → empty (the `card-number`/`iban` patterns may hit a legitimate long number in memory — if so, the owner decides: rephrase the fact or record a documented exception; never edit the pattern silently).
- AC-18, agent read-only: `systemctl cat claude-remote claude-soak.service claude-soak.timer; cat /srv/agent/bin/claude-remote.sh /srv/agent/bin/claude-soak.sh; systemctl show claude-remote -p NRestarts` → diffed locally against the heredocs in `runbooks/central-vm-setup.md` (as updated in Step 6) → identical, i.e. the wrapper differs from its 02 version only by the `--name Zyggy` token on two lines (relaxed AC-18); restart count consistent with 0001 ("2 of 2", the reboot's and 27's).
- AC-16/AC-17: the two documents completed as above.
- The same laptop-side grep over this repository: `Select-String -Path runbooks/central-claude-config.md, _plans/decisions/0002-central-productive.md -Pattern 'ghp_|github_pat_|sk-ant-|AKIA|BEGIN .*PRIVATE KEY|xox[abprs]-'` → no match (the AC-9 token appears only as the spec's obviously fake sample — if it is quoted in 0002, quote it as `ghp_…` truncated).

**Contract impact**: none.

**VERIFY**:
- `Select-String -Path _plans/decisions/0002-central-productive.md -Pattern '^\| AC-' | Measure-Object` → 22 rows, none with an empty Evidence or Result cell (`-Pattern '\|\s*\|\s*\|$'` → 0 matches within the AC table).
- `Select-String -Path runbooks/central-claude-config.md -Pattern '^\| \d+ ' | Measure-Object` → 10 status rows, all `done`.
- `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + `dotnet format Zyggy.slnx --verify-no-changes` — green **and** `git status --porcelain -- src tests Zyggy.slnx Directory.*.props global.json .github` → empty: this repository's code and CI are untouched by 27 (AC-32 second half).
- `git -C d:\source\zyggy-core status --porcelain` and `git -C d:\source\zyggy-geoffrey status --porcelain` → empty; `git -C d:\source\zyggy-core rev-parse HEAD` and `git -C d:\source\zyggy-geoffrey rev-parse HEAD` equal the latest template and instance SHAs in 0002 (Step 11's, or later ones listed in 0002 if a fix happened in Step 12); `git -C d:\source\zyggy-geoffrey diff --name-only upstream/main HEAD` → only instance-owned paths; the PROVE loop green once more in the container over both `d:\source\zyggy-core` and `d:\source\zyggy-geoffrey` (with `-e ZYGGY_HYGIENE_FORBIDDEN=geoffrey`).
- `git status --porcelain` in this repository lists only `runbooks/central-claude-config.md`, `runbooks/central-vm-setup.md`, `_plans/decisions/0001-transport-and-vm.md`, `_plans/decisions/0002-central-productive.md`, `_plans/ROADMAP.md`, `_plans/27-central-identity-memory.md`, `memory/short-term.md`, and the session journal — nothing else.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C — **definition of done for deliverable 27** *(covers Steps 9–13)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification (the roadmap DoD, each with a dated row in 0002): a fresh remote-control session shows the digest (`/memory`/`/context` list `AGENTS.md`, the template's rule files and `instance.md`, the log shows `no CLAUDE.md found; AGENTS.md loaded`, the three sections sum ≤ 18,000 bytes — AC-5/6/7); "remember that …" landed in `inbox/` as a `[stated]` line and the token was refused by name (AC-8/9); every turn appends one `[observed]` line to `daily/<date>.md` (AC-10); `claude -p --no-session-persistence` in the same directory loads the same `AGENTS.md`, answers from `profile.md` and leaves no session file (AC-11); `zyggy-core` (template), `zyggy-geoffrey` (instance) and `zyggy-geoffrey-memory` each have a first commit and a remote, the memory seed was pushed from the VM (AC-2/3); the instance differs from the template only in instance-owned paths (AC-34); the first template update merged on the laptop without conflict and fast-forwarded on the VM with a clean tree (AC-35); auto memory lives in `memory/geoffrey/geoffrey/auto/` (AC-14); Playwright fetched `Example Domain` headless within the RAM budget (AC-33); `/doctor prompt-audit` clean incl. `instance.md` (AC-13); template and instance CI green with the word-list test run (AC-32); the PROVE loop green in the container over both checkouts.
- [ ] Contract review: 0002 complete (22 AC rows, Dates line, three Repositories rows, Plugins/Settings/Credentials/Deviations/Costs sections); `runbooks/central-claude-config.md` has everything AC-16 lists (status table, ten steps, three repositories, instance creation on the laptop, in-place conversion into the instance clone, plugin/settings/credential tables, the ancestor check, "re-run the digest by hand", "adding a plugin later", "Update Central from the template", one troubleshooting entry per Failure-modes row, "Restore the instance and memory repositories") and the 02 runbook's restore draft points to it; `restore-central.md` is thereby covered (amendment (g′)); 0001's restart row reads "2 of 2".
- [ ] ⚠️ Risk review: no secret in `zyggy-core`, `zyggy-geoffrey` (incl. `instance/`), `zyggy-geoffrey-memory`, `~zyggy/.claude/settings.json`, `.claude/settings.local.json`, the runbook or 0002 (the AC-15 greps, both sides); the two deploy keys are the only credentials added and are repo-scoped (`zyggy-geoffrey` read-only, `zyggy-geoffrey-memory` read/write, none for `zyggy-core`); the browser rule for unattended runs is loaded (`security.md` in `/memory`) and the browser exits after each task; the plugin is the only third-party code added, at project scope, versions recorded, auto-update decision noted (*overturnable*); units and `claude-soak.sh` byte-identical, `claude-remote.sh` changed only by `--name Zyggy` (relaxed AC-18); the session is titled `Zyggy` (AC-36, or its recorded re-check); `Zyggy.slnx` untouched; `geoffrey` appears only in `zyggy-geoffrey-memory` directory names, the instance-owned files (`instance/settings.local.json`, `.claude/rules/instance.md`), the live `settings.local.json`, the runbook and 0002 — never in a template file, not even in a test.
- [ ] Forwarded findings acknowledged for 28/11/12/15/23/26/30/04/02 (spec "Findings forwarded", incl. the split's Findings 8–11: content placement, nodes, versioned package, runbook placement) — the owner notes any new one the execution surfaced (e.g. the real `hook_response` field names, the headless mechanism and where it had to live) in 0002's Deviations or in `ROADMAP.md` #28's entry.
- [ ] **Not the executor's edits** (spec OQ-5): the founding-spec re-amendments (a′) §3 Components + Central agent instance, (e′) §8 instance deploy key, (f′) §10, (g′) §11 `restore-central.md`, (k) note for nodes/§9/§14 are **the owner's edit** of `_specs/00 - Personal Agent Platform — Technical Specification.md` (on 2026-09-30 it does not yet mention `zyggy-geoffrey`); the `ROADMAP.md` #27 **done-line** ("`zyggy-core` (template), `zyggy-geoffrey` (instance) and the memory repo each have a first commit and a remote; the template's and the instance's CI are green") is **the project-manager's** update. The owner confirms both are done or scheduled.
- [ ] Owner commits this repository's changes and confirms `ROADMAP.md` #27 = Done; the next `/new-feature` is 28.
- [ ] User approved — deliverable 27 is done

---

## Acceptance-criteria → step map

| AC | Step(s) | Evidence |
|----|---------|----------|
| AC-1 no `CLAUDE.md` on the path | 7 | `ls` loop excerpt in 0002 |
| AC-2 instance clone at a SHA containing the template SHA, only `origin`, clean tree, instance files tracked, executable scripts | 7 (11, 13 re-check) | `remote -v`, `rev-parse`, `merge-base --is-ancestor`, `status`, `ls -la` |
| AC-3 memory clone, layout, seed commit | 6 (README-built tree), 7 (layout on VM), 9 (seed) | `find`, `git log` |
| AC-4 live `settings.local.json` installed from `instance/settings.local.json`, 600, ignored, three keys equal | 7 | `stat`, `check-ignore`, `diff` of `jq -S` |
| AC-5 wrapper `--name Zyggy`, then one restart; `AGENTS.md loaded`; rule files incl. `instance.md`; pineapple | 8 | log tail, session excerpt |
| AC-6 answer cites profile/preferences, three hook results | 9 | session excerpt |
| AC-7 three `hook_response` sections, `OK`, no session file | 8 | `jq` excerpt |
| AC-8 remember → inbox line | 10 | `cat` of the inbox file |
| AC-9 token refused, `github-token`, exit 2, not echoed | 10 | transcript excerpt, `grep ghp_` empty |
| AC-10 Stop line per turn, front matter once | 10 (also 9 step 4) | `tail -3`, `head -5` |
| AC-11 `-p` answers from `AGENTS.md` + `profile.md`, Stop line, no session file | 9 | command output |
| AC-12 plugin in the template's `enabledPlugins`, none in `instance/settings.local.json`, project scope, recorded, soak unaffected | 1 (template entry), 8 (install), 12 (complete) | `claude plugin list`, `jq`, `/plugin`, `/mcp`, soak line |
| AC-33 headless Chromium, `Example Domain`, RAM, rule in `security.md` | 12 (rule text: 5) | `free -h`, `journalctl`, answer |
| AC-13 `/doctor prompt-audit` clean incl. `instance.md` | 10 (laptop pre-runs at Gate A on the template and at Gate B on the instance; re-run in 11 if a template fix travelled) | audit output |
| AC-14 auto memory inside the repo | 9 | `ls auto/`, `/memory` |
| AC-15 exactly two keys (`zyggy-geoffrey` read-only, `zyggy-geoffrey-memory` rw), none for `zyggy-core`, aliases, no secret in any of the three repositories | 7 (keys), 13 (greps) | `ls -la`, `cat config`, greps |
| AC-16 runbook complete (three repositories, instance creation, update, restore) | 6 (draft), 11 (update section validated), 13 (final) | section grep, review |
| AC-17 decision 0002 | 6 (opened), 7–13 (rows) | 22 rows |
| AC-18 units untouched, wrapper changed only by `--name Zyggy` (two lines), one restart | 6 (runbook heredoc), 8 (edit + interim), 13 | `systemctl cat` diff |
| AC-36 session listed as `Zyggy` after the restart (or recorded + re-checked at the next fresh session) | 8 (13 re-check if needed) | session-list excerpt |
| AC-34 instance differs from the template only in instance-owned paths; template ships none | 5b (template half), 6 (staged pre-check), 7 (committed, `fetch upstream`), 11/13 (re-check) | `git diff --name-only upstream/main HEAD`, `git ls-files` |
| AC-35 first template update: laptop merge without conflict → push → VM `--ff-only`, clean tree | 11 | merge commit parents, `merge-base --is-ancestor`, `status --porcelain` |
| AC-19 identity digest byte-equal | 1 | `digest.bats` |
| AC-20 index section | 2 | `digest.bats` |
| AC-21 daily section | 2 | `digest.bats` |
| AC-22 caps, markers, clamp | 2 | `digest.bats` |
| AC-23 config errors → exit 3, empty stdout; `ZYGGY_HOOKS=off` | 1 | `digest.bats` (all sections), `remember.bats`, `stop.bats` |
| AC-24 `CLAUDE.md` warning | 1 | `digest.bats` |
| AC-25 two stops byte-equal | 4 | `stop.bats` |
| AC-26 Stop skips, refusal, cap marker | 4 | `stop.bats` |
| AC-27 remember variants byte-equal | 3 | `remember.bats` |
| AC-28 remember usage errors exit 4 | 3 | `remember.bats` |
| AC-29 secret samples refused / benign accepted, both scripts | 3 (remember), 4 (stop) | both `.bats` |
| AC-30 template-owned files: no machine path, no literal principal, no `ZYGGY_HYGIENE_FORBIDDEN` word (skipped when unset); instance-owned paths exempt; shellcheck, headers | 5 (shellcheck, headers), 5b (reshaped hygiene), 6 (passes in the instance) | `repo.bats` |
| AC-31 no `CLAUDE.md` variant, ≤ 200 lines (incl. an instance's `instance.md`), skill front matter, settings wiring | 1 (settings, no CLAUDE.md), 5 (rest), 6 (in the instance) | `repo.bats` |
| AC-32 CI green in `zyggy-core` and, unmodified, in `zyggy-geoffrey`, with `ZYGGY_HYGIENE_FORBIDDEN` passed from `vars`; `zyggy` CI untouched | 1 (file), 5b (`env:` line), Gate A (template first run), 7 (instance first run), 11 (both again), 13 (`dotnet` triple + `git status`) | Actions run URLs |

Every Decision-Table "Keep"/"Reshape" row maps to a step (template/instance split: 5b template edits, 6 instance creation and reference-copy settings, 7 VM conversion and keys, 11 update path); every "Defer"/"Library"/"Out of Scope"/"Rejected" row (Hub, `.mcp.json` for the Hub, dream, Telegram, mail/social, context envelopes, node-side variants, `anthropics/skills` clone, community dream plugins, commit hooks, per-turn model calls, versioned packaging, GitHub "Use this template", `git subtree`, memory inside the instance, a memory skeleton tree in the template, making `zyggy-core` public or adding a licence, a script that creates instances, instance-only plugins or skills, laptop/node instances) appears in no step.

## Assumptions (taken where the spec is silent)

1. **`.gitattributes` in `zyggy-core`** (not in the spec's layout) — required so a Windows-authored checkout stays LF and the fixture/expected bytes are never normalised; the executable bit is set in the index with `git update-index --chmod=+x`. Without it AC-2 (`-rwxr-xr-x`) and the shebang fail on the VM.
2. **`[warning]` line position**: wrapper line first, then `[warning]`, then the data sentence — reconciling AC-24 ("the identity section starts with `[warning]`") with the Contracts ("the section's first line after the wrapper"). The expected file freezes this; the executor confirms at Gate A.
3. **`tests/fixtures/memory-oversize/`** holds the 40 KB `profile.md` and the seven 5 KB daily files; the 300 index files are generated by `tests/helpers.bash` into the per-test temp copy rather than committed (same AC-22 coverage, no 300-file fixture churn).
4. **Pattern flags as a third column** in `secret-patterns.txt` (`nospace`, `nospace-nohyphen`, `icase`) so the "after removing spaces / case-insensitively" rules of the spec live in the data file, not in the script; the format stays `name<TAB>ERE[<TAB>flags]` and 11 reads the same file.
5. **`--scope general` emits no hint** (same as the default) — the spec's line format shows the hint only for `project:<name>` and `machine` is listed as a vocabulary value; the executor emits `(machine)` for `machine` and nothing for `general`. Frozen in `inbox-after-remember.md`; confirm at Gate A.
6. **A `daily/` file that already exists without front matter** (written by hand) is appended to without adding front matter — the hook never rewrites a file's head, only its `updated:` line when one exists.
7. *(Superseded 2026-09-30 by the spec: `enabledPlugins` is committed in the template — OQ-6 — and the VM holds only a read-only instance key, so the former assumption about authoring it on the laptop is now contract.)*
8. **The laptop `/doctor prompt-audit`** at Gate A (template) and Gate B (instance, covering `instance.md`) is added so the VM audit (AC-13) is expected clean without a second `claude-remote` restart; a text fix after the restart is applied by `git pull --ff-only` + `/clear` (SessionStart re-runs; instruction files reload with the session), never by a restart and never by editing files on the VM.
9. **`hook_response` event field names** in AC-7 are recorded as found on 2.1.284 (the spec verified the event exists, not its exact JSON keys); the runbook's `jq` filter is adjusted to the real shape and noted in 0002.
10. **Headless mechanism of the `playwright` plugin** is unknown until Step 8's inspection; Step 12 contains the decision point and the options in the runbook's order, rather than prescribing one now; where the fix lives (template vs instance-owned path) is decided there by the owner.
11. *(Now fact, spec naming amendment:)* the template is on GitHub before Step 6 — `https://github.com/zyggy-org/zyggy-core`, private, CI green at `316e9ea`, `ZYGGY_HYGIENE_FORBIDDEN=geoffrey` set; Step 6 clones the instance from that URL (or from the local template with `set-url`, same SHA).
19. **`.gitignore` in `zyggy-geoffrey-memory`.** The spec's README commands for the memory repository create no `.gitignore`, so Step 6 does not add one; see Notes ("evolution/") for why the owner may want `evolution/` ignored there — an owner decision, not planned.
20. **Repository names inside the template.** `zyggy-geoffrey` contains the forbidden word `geoffrey`, so it must never appear in a template file (the README uses `<instance>`/`zyggy-<owner>` placeholders); it appears only in the instance's `instance.md`, this repository's runbook, 0002 and the plan.
12. **Hygiene word matching** (AC-30 c) is a case-insensitive **fixed-string substring** match (`grep -iF`) — the stricter reading of "none of its words occurs"; an empty variable (GitHub expands a missing `vars.*` to an empty string) is treated as unset → `skip`.
13. **Windows drive-path pattern** (AC-30 a) requires a non-alphanumeric character (or line start) before the drive letter, so the regex text `truncated:\ ` in `tests/digest.bats` is not a false positive; the negative-control test pins this.
14. **The hygiene helpers live in `tests/repo.bats`**, not `tests/helpers.bash`, because the spec lists `helpers.bash` as unchanged; the instance-owned patterns are one constant there and are asserted verbatim in the README (the spec: "changing the list is a template change (README + the hygiene test's exclusion pattern)").
15. **Staging is allowed, committing is not.** The index-based tests (`git ls-files` for executable bits and for template-owned files) need new files staged; the agent `git add`s in `d:\source\zyggy-core`, `d:\source\zyggy-geoffrey` and `d:\source\zyggy-geoffrey-memory` and never commits, merges or pushes.
16. **`d:\source\zyggy-geoffrey-memory` holds only `README.md` and six `.gitkeep`s** (the README commands, no skeleton — spec "Memory layout skeleton in the template: Defer / not shipped"); the identity files first appear through `/seed-memory` in Step 9. Until then the digest shows headings only — the scripts already yield "the heading only" for a missing file (`add_file` in `session-start.sh`), and Step 6 VERIFY runs all three sections plus `remember`/`stop` against that tree in the container.
17. **AC-35's change** is the Step 10 template fix when AC-13 found one in a template file; otherwise a one-line `README.md` change picked at execution time (content not prescribed by the spec). An `instance.md`-only fix does not count as a template update.
18. **`ROADMAP.md`**: the executor sets only the #27 status cell in Step 13 (as the previous plan did); the done-line is the project-manager's (OQ-5).

## Conflicts found between the spec, the founding spec and the repository

- None blocking. The founding spec already contains amendments (a)–(g) and (i) (checked: §7 layout line `auto/`, §7 Context loading paragraph with the three caps). It does **not** yet contain the split's re-amendments (a′)/(e′)/(f′)/(g′)/(k) (no occurrence of `zyggy-geoffrey` or "instance repository" on 2026-09-30) — the owner's edit (OQ-5), listed at the final gate.
- `ROADMAP.md` #27's done-line still reads "the memory repo and `zyggy-core` each have a first commit and a remote" — the OQ-5 wording is the project-manager's update, listed at the final gate; the executor does not edit it.
- `ROADMAP.md` #27's **Goal** line still says the Stop hook "writes a one-line session summary to `inbox/`" (OQ-3 changed it to `daily/`); flagged to the project-manager with the done-line at the final gate.
- `.claude/skills/remember/SKILL.md` in the template still points to `runbooks/central-claude-config.md` "in the `zyggy` repository" while the spec lists `remember/*` as unchanged and removes such pointers only from `AGENTS.md`/rules; left as the spec says and raised at the Slice A gate.
- `ROADMAP.md` "Reprioritisation" research table still says "Central gets one `CLAUDE.md` … `AGENTS.md` only as a tool-neutral file imported by `CLAUDE.md`" — superseded by O28 in the same section's decisions paragraph; historical text, not edited.
- `CLAUDE.md` of this repository describes `IPolicySource` = signed `policy.yaml` while the founding spec now says `node.json` in v1 (`memory/short-term.md` 2026-09-29) — unrelated to 27, not touched.

## Notes for the executor (things the planner found while reading the repo and spec)

- **Four working directories.** Steps 1–5b and every template fix happen in `d:\source\zyggy-core`; instance-owned files (and only those) in `d:\source\zyggy-geoffrey`; the memory directories in `d:\source\zyggy-geoffrey-memory`; Steps 6, 11 and 13 write the files of this repository. Never create template or instance content under `d:\source\zyggy`, and never edit a template-owned file inside `d:\source\zyggy-geoffrey` — that would break AC-34 and make the next `git pull upstream main` conflict.
- **bash on this laptop = the podman container.** This laptop has no WSL Ubuntu; every PROVE run and every hook demonstration uses `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\<repo>:/w' -w /w zyggy-core-test bash -c '…'` from the Bash tool (image: Ubuntu 24.04, bats 1.10 — supports `run --separate-stderr` and `$BATS_TEST_TMPDIR` —, shellcheck 0.9, jq 1.7, tzdata, git). Extra env with `-e NAME=value`, extra mounts with `-v 'D:\…:/m:ro'`. WSL Ubuntu is an optional equivalent on machines that have it. On a Windows bind mount every file looks executable, so the tests cannot catch a missing executable bit — that is why `repo.bats` checks `git ls-files -s` (`100755`) instead.
- **Owner's word list on the command line only.** Pass `-e ZYGGY_HYGIENE_FORBIDDEN=geoffrey` to the container; never write the owner's name into any file of `d:\source\zyggy-core` (not in a test, not split, not in a comment) — `git grep -i geoffrey` over the template's index must stay empty.
- **Instance-owned patterns, exactly.** `instance/**`, `.claude/rules/instance.md`, `.claude/rules/instance/**`, `.claude/skills/instance-*/**` — the same four in `INSTANCE_OWNED_ERE`, the README, AC-34's checks and the runbook. Any test that globs `.claude/rules/*.md` for template-only content (e.g. "no runbook path") must name the three template rule files instead, because an instance's `instance.md` legitimately names its runbook and principal.
- **Root-run git on the VM.** `az vm run-command` runs as root; git refuses a repository owned by `zyggy` ("detected dubious ownership") — use `runuser -u zyggy -- git -C /srv/agent/central …` (or `git -c safe.directory='*' -C …`) for the read-only checks. Never `git config --global` anything on the VM.
- **Naming (amended 2026-09-30: naming).** Repositories are named after the owner, machines keep "central": instance `zyggy-geoffrey` (`d:\source\zyggy-geoffrey`, alias `github.com-zyggy-geoffrey`, key `zyggy_zyggy-geoffrey_ed25519`, read-only), memory `zyggy-geoffrey-memory` (`d:\source\zyggy-geoffrey-memory`, alias `github.com-zyggy-geoffrey-memory`, key `zyggy_zyggy-geoffrey-memory_ed25519`, read/write), organisation `zyggy-org`. `/srv/agent/central`, the VM `central`, the slug `-srv-agent-central` and the resource group are unchanged. The session display name becomes `Zyggy` (wrapper `--name Zyggy`, owner's edit right before the Step 8 restart).
- **Template `.gitignore` anchors `/memory/`.** A bare `memory/` once ignored `tests/fixtures/memory/` too; keep the leading slash in any future `.gitignore` edit (template change).
- **`evolution/` must stay ignored.** The owner's user-level evolve plugin writes journals into whatever repository a session opens in. The template ignores `evolution/`, so the instance inherits it; the memory repository's README-built tree has no `.gitignore` (the spec's commands create none) — tell the owner at the Slice B gate so they can add `evolution/` there themselves, and check `git -C d:\source\zyggy-geoffrey-memory status` / on the VM for a stray `evolution/` before the seed commit.
- **Prove on tracked files only before a gate.** CI sees only what is committed; run bats on an index-only copy: `git checkout-index -a --prefix=/tmp/clean/` inside the container, then `cd /tmp/clean && bats tests/` (for `repo.bats`' git checks, run it where `git ls-files` works, i.e. the mounted repo). An untracked helper or fixture otherwise makes the laptop green and CI red.
- **GitHub's `ubuntu-latest` quirks.** `/usr/bin/X11` is a symlink to `.` and `/bin` is `/usr/bin` — recursive `find`/`grep` over system paths loops or doubles; never walk `/usr/bin` or rely on `/bin` vs `/usr/bin` differences in tests.
- **The VM never merges.** `git pull --ff-only` is the only git write the owner runs in `/srv/agent/central`; there is no `upstream` remote there. If a pull refuses, follow the runbook entry "VM checkout diverged" — never commit on the VM.
- **Bytes, not characters.** Put `LC_ALL=C` at the top of `lib.sh` so `${#s}` counts bytes and `sort` is byte-ordered (the index order must match the hand-written expected file); measure caps on the complete section text; `head -c` then drop the trailing partial line for the line-boundary cut.
- **Never stream a section.** Build the whole text in a variable, cap it, then `printf '%s'` once — otherwise a truncated section could be half-emitted when the cap triggers.
- **Stdin without blocking.** `if [ ! -t 0 ]; then input=$(cat); fi` — by hand (a TTY) the scripts must not wait; in the hook (a pipe) they read the JSON. For `stop.sh`, `jq -r '.last_assistant_message // ""'` and `.stop_hook_active == true`.
- **Dates.** `date -u -d "$ZYGGY_NOW" +%FT%TZ` for `generated`; `TZ=$ZYGGY_TIMEZONE date -d "$ZYGGY_NOW" +%F` / `+%H:%M` for the local date and time (GNU date; the VM is Ubuntu). GNU `date` silently falls back to UTC on an unknown `TZ`, hence the `/usr/share/zoneinfo/<tz>` check for the exit-3 edge case.
- **Atomic append.** `cat "$file" > "$tmp"` (or the new front matter), rewrite the `updated:` line with `sed`, `printf '%s\n' "$line" >> "$tmp"`, `mv -f "$tmp" "$file"` — same directory so `mv` is a rename; `trap 'rm -f "$tmp"' EXIT` so no `.tmp` survives.
- **The `git` stub test.** Prepend `$BATS_TEST_TMPDIR/bin` (holding an executable `git` that touches a sentinel and exits 99) to `PATH` for the run; the scripts must not call git even indirectly (`zy_*` helpers use no `git rev-parse` for paths — the memory root comes from env only).
- **`secret-patterns.txt` with ERE and `\b`.** GNU `grep -E` supports `\b`; test each pattern with `grep -E` exactly as the script applies it (`nospace`: `tr -d ' '`; `nospace-nohyphen`: `tr -d ' -'`; `icase`: `grep -Ei`). Apply the raw text to every pattern and the transformed text only to the flagged ones, so a benign phone number `+32 470 12 34 56` (10 digits) never matches `card-number` (13–19).
- **Fixture facts are about Alice at Acme**, invented; no real person, place or account. `secret-samples.txt` values are synthetic and must not accidentally be a valid token shape that a scanner flags on GitHub push protection — keep the GitHub sample as the spec's `ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789` (push protection checks real tokens, not shapes; if a push is blocked, the owner records it and the sample is split with a shell concatenation in the fixture loader).
- **On the VM, never** run `claude` yourself; every `claude` line in Steps 8–12 is the owner's. Read-only evidence goes through `az vm run-command` (root, `sh`): wrap in `bash -c '…'`, and remember `sudo -iu zyggy` is unnecessary as root — use absolute paths under `/srv/agent/home` and `/srv/agent/central`.
- **After `settings.local.json` exists**, any `claude` start in `/srv/agent/central` (including the owner's `-p` checks) runs the hooks and appends a Stop line — expected; the `-p` checks must carry `--no-session-persistence` so the Q4 wrapper never resumes them (spec Finding 1; runbook "Wrong session resumed").
- **`/clear` re-runs `SessionStart`** (matcher includes `clear`), which is how the seeded memory reaches the already-running remote session in Step 9 without a restart.
- **Chromium as root vs `zyggy`.** `npx playwright install chromium` must run as `zyggy` (it writes `~/.cache/ms-playwright/`); `install-deps` as root only installs apt packages — do not run `install` as root or the browser lands in root's cache.
- **Keep `memory/` uncommitted after Step 9 except the seed**: `inbox/` and `daily/` lines stay uncommitted until 28's dream pass (spec commit discipline); Azure Backup covers the gap.
