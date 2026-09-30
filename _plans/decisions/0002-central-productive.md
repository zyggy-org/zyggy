# 0002 — Central productive first (P0b)

Status: open. Closed when 30 is Done.

Spec: `_specs/27-central-identity-memory.md` · Plan: `_plans/27-central-identity-memory.md` · Runbook:
`runbooks/central-claude-config.md`. No key, token or variable value is ever pasted here — file names only; a
secret-shaped sample is quoted truncated (`ghp…`).

## P0b checklist

| # | deliverable | status | evidence |
|---|-------------|--------|----------|
| 27 | Central identity, memory repo and base plugin set | In progress | this file, section 27 |
| 28 | Nightly dream pass on Central without the bus | Not started | |
| 29 | Telegram as Central's chat channel | Not started | |
| 23 | Personal mail triage on Central (Gmail + Outlook.com) | Not started | |
| 30 | Social connectors on Central | Not started | |

## 27 — Central identity, memory repo, plugins

- Dates: config live 2026-09-30 ~13:20 UTC (instance clone 13:13, memory clone 13:16, settings installed; hooks answer by hand); wrapper `--name Zyggy` applied `<UTC>`; claude-remote restart `<UTC>` (counted in
  0001); session title after restart `<Zyggy | still central → Zyggy at fresh session <date>>`; seed commit `<sha>`;
  zyggy-core (template) `316e9ea` (2026-09-30); zyggy-geoffrey (instance) `2a94ea0` (2026-09-30, contains template `316e9ea`); first template update
  `<date, template sha → instance sha>`
- Before (2026-09-30 10:15 UTC, agent, `az vm run-command` read-only): `/srv/agent/central` holds only `.git`
  (0 commits, no remote) and `claude-remote.log`; no `AGENTS.md`, no `memory/`; `/srv/agent/home/.ssh` does not
  exist. Template `zyggy-core` = `316e9ea` (CI green, run 36697696055).

| Check (AC) | Criterion | Evidence (date, command, excerpt) | Result |
|------------|-----------|-----------------------------------|--------|
| AC-1 | No `CLAUDE.md`, `CLAUDE.local.md`, `.claude/CLAUDE.md` on or above `/srv/agent/central`, none in `~zyggy/.claude/` | 2026-09-30 ~13:30 UTC (owner, pasted): the AC-1 loop over `/srv/agent/central`, `/srv/agent`, `/srv`, `/` and `/srv/agent/home/.claude/CLAUDE.md` → 13 × `No such file or directory`; `claude doctor`: native 2.1.285, auto-update success 2026-09-29, no finding | pass |
| AC-2 | `/srv/agent/central` = `zyggy-geoffrey` clone at a recorded SHA containing the template SHA; only `origin`; clean tree; instance files tracked; scripts `-rwxr-xr-x` | 2026-09-30 13:20 UTC (agent, `az vm run-command`, read-only): `git remote -v` → only `origin git@github.com-zyggy-geoffrey:zyggy-org/zyggy-geoffrey.git`; HEAD `2a94ea01322a629cb4aaeb8b40a5273e1bc83c36`; `merge-base --is-ancestor 316e9ea HEAD` → 0; `status --porcelain` empty; `instance/settings.local.json` and `.claude/rules/instance.md` tracked; `lib.sh`, `session-start.sh`, `stop.sh`, `remember.sh` are `-rwxrwxr-x` (group write from the VM's umask 002; the executable bit is the requirement) | pass |
| AC-3 | `memory/` = `zyggy-geoffrey-memory` clone; `geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}`; `profile.md`, `preferences.md`, `agents.md`; seed commit | 2026-09-30 13:35 UTC (agent, read-only): `memory/` remote `git@github.com-zyggy-geoffrey-memory:zyggy-org/zyggy-geoffrey-memory.git`, log `5c579bb Memory layout`; dirs `geoffrey/geoffrey/{areas,auto,daily,inbox,people,topics}`; nothing outside `geoffrey/geoffrey/` but `README.md`. Identity files + seed commit: pending (step 8, `/seed-memory`) | layout pass; seed pending |
| AC-4 | Live `settings.local.json` installed from `instance/settings.local.json`: mode 600, ignored, `env`/`autoMemoryDirectory`/`enabledPlugins` diff empty | 2026-09-30 13:35 UTC (agent, read-only): `.claude/settings.local.json` mode `600` owner `zyggy`; `git check-ignore -v` lists `.claude/settings.local.json`, `memory` (`/memory/`), `claude-remote.log`; `diff` of `{env, autoMemoryDirectory, enabledPlugins}` vs `instance/settings.local.json` empty; `autoMemoryDirectory == <root>/<tenant>/<user>/auto` → `true`; `status --porcelain` empty. By hand with the file's `env`: `session-start.sh identity|index|daily` → 250/237/215 bytes (empty memory), exit 0 | pass |
| AC-5 | Wrapper `--name Zyggy`, one restart; `resuming <same id>`; `AGENTS.md loaded`; three template rules + `instance.md`; *pineapple* | | |
| AC-6 | Answer cites `profile.md`/`preferences.md` and names the digest; three `SessionStart` results | | |
| AC-7 | `claude -p --no-session-persistence`: three `hook_response` sections < 10,000 chars each, Σ ≤ 18,000; `OK`; no new session file | | |
| AC-8 | "Remember that …" → `[stated]` line in `inbox/remember-<date>.md` with front matter | | |
| AC-9 | Token refused naming `github-token`, not echoed, exit 2; nothing under `memory/` | | |
| AC-10 | `[observed]` Stop line per turn in `daily/<date>.md`; front matter once; `updated` = today | | |
| AC-11 | `-p` names `# Zyggy — Central` and the owner's first name; Stop line; no session file | | |
| AC-12 | `playwright@claude-plugins-official` in the template's `enabledPlugins` only; project scope; `/plugin`, `/mcp`; recorded; soak `exit 0` | 2026-09-30 13:38 UTC (owner install; agent read-only): `installed_plugins.json` → `playwright@claude-plugins-official`, scope `project`, projectPath `/srv/agent/central`, version `2a8ad9f74633`, gitCommitSha `2a8ad9f74633d10e3d9bb0660a03bfc6e50584b1` (marketplace snapshot, no `.git` — `rev-parse` not applicable); manifest author Microsoft, marketplace `source ./external_plugins/playwright`; `.mcp.json` = `npx @playwright/mcp@latest` (no `--headless`, no pinned version); `enabledPlugins` unchanged in content, but the install pretty-printed `.claude/settings.json` (tree dirty until the template ships the canonical layout — first template update). `/plugin`, `/mcp`, soak line, always-on tokens: pending | install half pass; rest pending |
| AC-13 | `/doctor prompt-audit` clean across `AGENTS.md`, rules incl. `instance.md`, `remember`, `seed-memory` | | |
| AC-14 | Auto memory `MEMORY.md` in `memory/geoffrey/geoffrey/auto/`; nothing new under `~/.claude/projects/-srv-agent-central/memory/` | | |
| AC-15 (keys half) | 2026-09-30 13:20 UTC (agent, read-only): `/srv/agent/home/.ssh/` holds `config`, `known_hosts`, `zyggy_zyggy-geoffrey_ed25519` (+`.pub`), `zyggy_zyggy-geoffrey-memory_ed25519` (+`.pub`), all `-rw------- zyggy`, directory `drwx------`; no key under `/home/azureadmin/.ssh`; org policy `deploy_keys_enabled_for_repositories` set to `true` via `gh api` the same day. Secret greps: pending (runbook step 9). | keys pass; greps pending |
| AC-15 | Exactly two deploy keys (instance read-only, memory read/write), none for `zyggy-core`; two aliases with `IdentitiesOnly yes`; secret greps empty | | |
| AC-16 | Runbook `central-claude-config.md` complete; 02 restore draft points to it | | |
| AC-17 | This record: status, P0b checklist, 22 dated rows, repository/plugin/settings/credential tables | | |
| AC-18 | Units and `claude-soak.sh` byte-identical; `claude-remote.sh` differs from 02 only by `--name Zyggy` on two lines; one restart | | |
| AC-33 | Headless Chromium: `Example Domain`; browser exits; `free -h` ≈ 300–500 MB; no OOM; `security.md` rule present | | |
| AC-34 | `d:\source\zyggy-geoffrey` differs from `upstream/main` only in instance-owned paths; template ships none | 2026-09-30 (agent, laptop): `git diff --name-only upstream/main HEAD` → `.claude/rules/instance.md`, `instance/settings.local.json`; `git merge-base --is-ancestor 316e9ea HEAD` → 0; instance `2a94ea0`; template `git ls-files` has no instance-owned path (Gate A) | pass (laptop half; VM half at AC-2) |
| AC-35 | First template update: laptop merge without conflict → push → VM `--ff-only`; clean tree; SHAs and date | | |
| AC-36 | Central session listed as `Zyggy` after the restart (or recorded and re-checked at the next fresh session) | | |

## Repositories

| repo | role (template/instance/memory) | owner | visibility | laptop checkout + remotes | VM clone path | remote alias | key (name, scope) |
|------|---------------------------------|-------|------------|---------------------------|---------------|--------------|-------------------|
| `zyggy-org/zyggy-core` | template | zyggy-org (created 2026-09-30, first push `f6010c9`, CI green from `316e9ea`) | private | `d:\source\zyggy-core` (`origin` → `zyggy-core`) | not present | none | none |
| `zyggy-org/zyggy-geoffrey` | instance | zyggy-org (created empty 2026-09-30 with `gh`, `ZYGGY_HYGIENE_FORBIDDEN` set) | private | `d:\source\zyggy-geoffrey` (`origin` → `zyggy-geoffrey`, `upstream` → `zyggy-core`) | `/srv/agent/central` | `github.com-zyggy-geoffrey` | `zyggy_zyggy-geoffrey_ed25519`, read-only |
| `zyggy-org/zyggy-geoffrey-memory` | memory | zyggy-org (created empty 2026-09-30 with `gh`) | private | `d:\source\zyggy-geoffrey-memory` (`origin` → `zyggy-geoffrey-memory`) | `/srv/agent/central/memory` | `github.com-zyggy-geoffrey-memory` | `zyggy_zyggy-geoffrey-memory_ed25519`, read/write |

## Plugins

| name@marketplace | version | marketplace commit | author | always-on tokens | purpose | added |
|------------------|---------|--------------------|--------|------------------|---------|-------|
| `playwright@claude-plugins-official` | `2a8ad9f74633` (marketplace commit `2a8ad9f74633d10e3d9bb0660a03bfc6e50584b1`); server `npx @playwright/mcp@latest` (resolved version recorded at step 9) | external — Microsoft's Playwright MCP server, listed by Anthropic (`external_plugins/playwright`) | `claude plugin details`: pending | browser for web tasks and the 23 Outlook-Web fallback | 2026-09-30 13:38 UTC, project scope, `/srv/agent/central` |

- Scope: project (template `enabledPlugins`, OQ-6); MCP servers/hooks it adds: `<…>`; headless mechanism: `<…>`;
  browser: Chromium `<build>`, headless, one at a time; RAM reading: `<…>`; auto-update: on (marketplace default).

## Settings

| file | key | value | why |
|------|-----|-------|-----|
| `instance/settings.local.json` → `.claude/settings.local.json` | `env.ZYGGY_MEMORY_ROOT` | `/srv/agent/central/memory` | memory root for the hooks and `remember` |
| `instance/settings.local.json` → `.claude/settings.local.json` | `env.ZYGGY_TENANT` | `geoffrey` | principal (§13 v1) |
| `instance/settings.local.json` → `.claude/settings.local.json` | `env.ZYGGY_USER` | `geoffrey` | principal (§13 v1) |
| `instance/settings.local.json` → `.claude/settings.local.json` | `env.ZYGGY_TIMEZONE` | `Europe/Brussels` | daily file names and dates |
| `instance/settings.local.json` → `.claude/settings.local.json` | `autoMemoryDirectory` | `/srv/agent/central/memory/geoffrey/geoffrey/auto` | Claude Code auto memory inside the memory repository |
| `zyggy-core` `.claude/settings.json` (template) | `enabledPlugins` | `{"playwright@claude-plugins-official": true}` | OQ-6 |
| GitHub Actions repository variable, `zyggy-core` | `ZYGGY_HYGIENE_FORBIDDEN` | set (value not pasted); CI run 36697696055 green 2026-09-30, 95 tests, word test ran | AC-30 (c), AC-32 |
| GitHub Actions repository variable, `zyggy-geoffrey` | `ZYGGY_HYGIENE_FORBIDDEN` | set (value not pasted); first CI run 36715290305 green 2026-09-30, 95 tests, word test ran | AC-30 (c), AC-32 |

## Credentials on Central

| what | where | scope | rotation/revocation |
|------|-------|-------|---------------------|
| `zyggy-geoffrey` deploy key | `/srv/agent/home/.ssh/zyggy_zyggy-geoffrey_ed25519` (0600, `zyggy`) | read-only, `zyggy-org/zyggy-geoffrey` | revoke on GitHub, re-issue per runbook step 1c |
| `zyggy-geoffrey-memory` deploy key | `/srv/agent/home/.ssh/zyggy_zyggy-geoffrey-memory_ed25519` (0600, `zyggy`) | read/write, `zyggy-org/zyggy-geoffrey-memory` | revoke on GitHub, re-issue per runbook step 1c |

No `zyggy-core` key on Central.

## Deviations from the founding spec / brief

From the spec's "Deliberate deviations from the founding spec and the hand-off brief", verbatim:

- **`/srv/agent/central` is the `zyggy-core` checkout** (§10 "checked out once per machine" + "`.claude/` from agent-core"; brief: separate checkout + symlink/copy) — symlinked rules do not load without approval and the Edit tool refuses symlinks (docs); a copy needs a sync script. Decision Table row; accepted by the owner 2026-09-30 (amendments (a)/(f)). **Amended 2026-09-30 (template/instance split): it is now the checkout of the instance `zyggy-geoffrey`, whose history contains the template; founding-spec text (a)/(e)/(f)/(g) and §3 Components get the re-amendments accepted in OQ-5.**
- **(amended 2026-09-30: template/instance split) Three repositories instead of two** (§3 Components lists only `agent-core`; roadmap 27 DoD "the memory repo and `zyggy-core` each have a first commit and a remote") — owner decision; founding-spec and roadmap wording accepted in OQ-5.
- **(amended 2026-09-30: template/instance split) `.claude/settings.local.json` has a committed reference copy in the instance** (founding spec §11 (g) "rewrite `.claude/settings.local.json`") — restore installs it instead of rewriting it; the live file stays untracked because Claude Code writes approvals into it.
- **(amended 2026-09-30: naming) The 02 wrapper changes by one token** (brief/AC-18: "units untouched, no wrapper change") — `--name central` → `--name Zyggy` on both paths of `/srv/agent/bin/claude-remote.sh`, owner decision; applied before the single AC-5 restart, so the restart count is unchanged; the 02 runbook heredocs (`runbooks/central-vm-setup.md` lines 201, 234, 240) and `runbooks/central-vm-steps9-10.sh` lines 23, 29 are updated to match (and the prose at `central-vm-setup.md` lines 37, 204 that names `--name central`).
- **(amended 2026-09-30: naming) Repository names** `zyggy-geoffrey` / `zyggy-geoffrey-memory` in organisation `zyggy-org` instead of the working names `zyggy-central` / `zyggy-memory` used by the founding-spec amendments (a)–(g) and OQ-2/OQ-5 — owner decision; the owner's founding-spec edit and the roadmap use the new names.
- **(amended 2026-09-30: template/instance split) No owner-name literal in the template's hygiene test** (Slice A / previous AC-30 "no match for `geoffrey`") — replaced by structural checks plus the `ZYGGY_HYGIENE_FORBIDDEN` CI variable.
- **Stop hook writes one line per turn to `daily/`, not a session summary to `inbox/`** (§3 Hooks, §7 inbox line; brief DoD) — `Stop` is per turn; mechanical note; `daily/` is what the next session sees. Accepted by the owner 2026-09-30 (amendment (b)); the roadmap's 27 DoD line is changed to `daily/` by the orchestrator.
- **Digest delivered by three hook invocations** (§7 says "the `SessionStart` hook") — forced by the 10,000-character per-hook cap; content and budget unchanged. Amendment (d), accepted 2026-09-30.
- **`autoMemoryDirectory` in the project-local settings**, not `~/.claude/settings.json` (brief) — keeps the soak directory and future projects on the default; principal in one file.
- **Plugins at project scope**, not `--scope user` (brief) — keeps plugins out of the soak runs and makes the committed `enabledPlugins` the record.
- **One plugin (`playwright`) instead of the brief's candidate set** — owner's decision 2026-09-30 over the spec's recommendation of none; `chrome-devtools-mcp`, `claude-md-management`, `hookify`, `security-guidance`, `skill-creator`, `session-report`, `context7`, `microsoft-docs` exist in the official marketplace and are simply not chosen ("add on demand under the rule").
- **No `anthropics/skills` clone into `~/.claude/skills/`** (roadmap) — wrong layout for that path; marketplace install when 23 needs document skills.
- **Deploy keys instead of a `LoadCredential=`-fed PAT** for the two repositories (brief lists both) — §8's own default for git; usable interactively by `zyggy`; repo-scoped. Confirmed by the owner 2026-09-30.
- **Runbook in a new file** `runbooks/central-claude-config.md` rather than a section of the 02 runbook (brief allows either).
- **`remember` scope vocabulary limited to 03's `general | project:<name> | machine`** (brief/§7 `scope` unspecified) — one vocabulary for the Hub tool, the CLI and the context envelope.

Owner answers (spec "Decisions log", 2026-09-30):

| # | Question | Decision |
|---|----------|----------|
| OQ-1 | Plugin selection | **One plugin: `playwright@claude-plugins-official`**, project scope, headless Chromium, recorded per the mechanism (version, marketplace commit, external author, always-on tokens, purpose "browser for web tasks and the 23 Outlook-Web fallback"), Chromium installed with the `install-deps` half as root, RAM note (≈ 300–500 MB, one browser at a time), rule "no logged-in sites in unattended runs until 18–20". `chrome-devtools-mcp` and `microsoft-docs` exist in the official marketplace (URL-sourced entries; the former is installed on the owner's laptop) and are not chosen; everything else "add on demand under the rule". |
| OQ-2 | Repositories and authentication | `zyggy-core` + `zyggy-memory`, under the same GitHub organisation as the bus repository, both private; memory pushed by the dream pass only; two deploy keys (core read-only, memory read/write). |
| OQ-3 | Founding-spec amendments | (a)–(g) accepted as written, including Stop → one `[observed]` line per turn in `daily/<date>.md` and "the working directory is the checkout"; applied to the founding spec and to the roadmap's 27 DoD line by the orchestrator; (i) added for the plugin. |
| OQ-4 | Seeding interview and language | English memory files; the owner answers in the language of the moment; `preferences.md` records the conversational language; the six question blocks confirmed as written. |
| OQ-5 | Founding-spec and roadmap wording for the split | **Accepted as written (owner, 2026-09-30)**: re-amendments (a′) §3 Components + Central agent instance, (e′) §8 instance deploy key replacing the `agent-core` key, (f′) §10, (g′) §11 `restore-central.md`, (k) note for nodes/§9/§14, and the roadmap 27 done-line "`zyggy-core` (template), `zyggy-central` (instance) and the memory repo each have a first commit and a remote; the template's and the instance's CI are green". The owner edits the founding spec; the project-manager updates the roadmap line. |
| OQ-6 | `playwright` in the template or instance-level | **Stays in the template's `enabledPlugins`** (owner, 2026-09-30). Rule: a plugin the template's rules or skills depend on is template-level; an instance-only plugin is enabled at local scope and recorded in `instance/settings.local.json` `enabledPlugins`; an instance switches a template plugin off with `false` there. No Slice A change to `settings.json`, `AGENTS.md` or `security.md`. |
| Naming | Repository and session names | Owner decision, not an open question: organisation `zyggy-org`; instance `zyggy-geoffrey`, memory `zyggy-geoffrey-memory` (replacing `zyggy-central` / `zyggy-memory` in the rows above); VM names unchanged; remote-control session titled `Zyggy` via `--name Zyggy` in the 02 wrapper, applied before the AC-5 restart; resume-path effect recorded by AC-36. |
| OQ-7 | Visibility of the `zyggy-core` template | **Private for now, publishable by construction** (AC-30 keeps it principal-free); flipped later as an owner decision (e.g. at 26) together with a licence, removing the pointers into the private `zyggy` repository (`PROTOCOL.md`, `tests/README.md`) and a push-protection dry run for the synthetic secret samples. |

Deviations found during execution: none yet.

## Costs

27: none. (28 fills the per-run cost.)
