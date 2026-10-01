# Runbook — Central Claude Code configuration: instance, memory, plugins (deliverable 27)

Turns the Central VM built by `runbooks/central-vm-setup.md` (deliverable 02) into a productive Claude Code
workspace: `/srv/agent/central` becomes a clone of the private **instance** repository `zyggy-geoffrey` (the
`zyggy-core` template plus instance-owned files), the memory repository `zyggy-geoffrey-memory` sits at `memory/`,
three `SessionStart` hooks inject the memory digest, the `Stop` hook writes one `[observed]` line per turn, the
`remember` skill keeps stated facts, and the `playwright` plugin gives Central a headless browser. Source of truth:
`_specs/27-central-identity-memory.md` and `_plans/27-central-identity-memory.md` (Steps 7–13 carry the same
commands; this file is the durable copy). Evidence goes into `_plans/decisions/0002-central-productive.md`.
Deliverable 31 adds section 11: a read-only GitHub token on Central and the owner-invoked `/github-inventory` skill
(source of truth `_specs/31-central-github-read-inventory.md` and `_plans/31-central-github-read-inventory.md`).

No key, token or variable value is ever written in this file, in 0002 or in any repository — only file names.

## The three repositories

All three are private, under the GitHub organisation `zyggy-org`.

| Repository | Role | Laptop checkout (remotes) | On the VM | Deploy key on the VM | Who pushes |
|-----------|------|---------------------------|-----------|----------------------|-----------|
| `zyggy-core` | **Template**: `AGENTS.md`, `.claude/{settings.json,rules,hooks,skills}`, `PROTOCOL.md`, `README.md`, `tests/`, CI; names no tenant, user or machine path | `d:\source\zyggy-core` (`origin` → `zyggy-core`) | **not present** | **none** | The owner, from the laptop |
| `zyggy-geoffrey` | **Instance** (the owner's Central): the template's full history + instance-owned files only (`.claude/rules/instance.md`, `instance/settings.local.json`) | `d:\source\zyggy-geoffrey` (`origin` → `zyggy-geoffrey`, `upstream` → `zyggy-core`) | `/srv/agent/central` (the 02 empty repository converted in place); only remote `origin` | `~/.ssh/zyggy_zyggy-geoffrey_ed25519`, **read-only** | The owner, from the laptop (Central never pushes it) |
| `zyggy-geoffrey-memory` | **Memory**, §7 layout `geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}` | `d:\source\zyggy-geoffrey-memory` (layout commit; later review only) | `/srv/agent/central/memory` (ignored by the instance checkout) | `~/.ssh/zyggy_zyggy-geoffrey-memory_ed25519`, **read/write** | 27: the owner (seed commit, on the VM); 28+: the dream pass |

Instance-owned paths (the template never ships them; the instance never edits anything else): `instance/**`,
`.claude/rules/instance.md`, `.claude/rules/instance/**`, `.claude/skills/instance-*/**`. Template updates travel
laptop merge (`git pull upstream main` in `d:\source\zyggy-geoffrey`) → push → VM `git pull --ff-only`. The VM
never merges, commits, pushes or holds an `upstream` remote.

Names: organisation `zyggy-org`; VM `central` in resource group `zyggy-central` (subscription *Abonnement Visual
Studio Enterprise*); user `zyggy`, home `/srv/agent/home`; the remote-control session is titled `Zyggy`
(`--name Zyggy` in the 02 wrapper).

## Tags

**[browser]** GitHub web UI (or the phone / claude.ai for the remote-control session); **[laptop]** PowerShell on
the owner's laptop; **[vm/root]** on the VM as `azureadmin` with `sudo`; **[vm/zyggy]** on the VM as `zyggy`
(`ssh -t azureadmin@central` then `sudo -iu zyggy`); **[agent, read-only VM check]** evidence the agent may collect
without changing anything (see "What the agent may verify read-only").

Never on Central: `--bare`, `--safe-mode`, `--dangerously-skip-permissions`; a `claude -p` in `/srv/agent/central`
**always** carries `--no-session-persistence` (otherwise the Q4 wrapper resumes it — "Wrong session resumed").
Always start Claude in `/srv/agent/central`, never in `memory/` (hooks are per working directory).

## Execution status (update as you go)

| Step | State | Notes |
|------|-------|-------|
| 1 Repositories, instance, keys, SSH config | done 2026-09-30 | template `zyggy-core` exists (CI green at `316e9ea`, `ZYGGY_HYGIENE_FORBIDDEN` set); 2026-09-30: empty private `zyggy-org/zyggy-geoffrey` and `zyggy-org/zyggy-geoffrey-memory` created with `gh` (agent), `ZYGGY_HYGIENE_FORBIDDEN` set on `zyggy-geoffrey`; instance pushed `2a94ea0`, CI green (run 36715290305); memory pushed `5c579bb`; keys and SSH config pending (owner) |
| 2 `/srv/agent/central` → instance clone | done 2026-09-30 13:13 UTC | instance `2a94ea0`, contains template `316e9ea`; only `origin`; clean tree (agent verified read-only) |
| 3 Memory clone at `memory/` | done 2026-09-30 13:16 UTC | `5c579bb`, layout dirs present (agent verified read-only) |
| 4 Install `settings.local.json` | done 2026-09-30 | mode 600, ignored, AC-4 diff empty (agent verified read-only) |
| 5 No `CLAUDE.md` on the path, `claude doctor` | done 2026-09-30 | AC-1 loop all absent; `claude doctor` clean, 2.1.285 (owner pasted) |
| 6 `playwright` plugin + Chromium | done 2026-09-30 | plugin `2a8ad9f74633` project scope; headless via template env; Chromium 1247 / CfT 155.0.8059.12 for playwright-core 1.64.0-alpha; AC-33 pass |
| 7 Wrapper `--name Zyggy`, the one restart, AC-5/AC-7/AC-36 | done 2026-09-30 | AC-7 pass; wrapper `--name Zyggy`; restart 14:05 UTC resumed `6ba6d03b`; AC-5 pass; AC-36: resumed session keeps `central`, `Zyggy` expected at the next fresh session |
| 8 Seeding session, seed commit, remember/Stop checks | done 2026-09-30 | seed `9032236` pushed; AC-6/8/9/10/11/13/14 pass; audit clean after `3e8034d`; synced-skills conflicts are a claude.ai setting |
| 9 Headless browser check (AC-33), 0002 rows, final sweeps | done 2026-09-30 | AC-33 pass; secret sweeps 0 hits (VM + three repos); AC-18 units identical; 0002 complete |
| 10 First template update through the instance (AC-35) | done 2026-09-30 | four template updates reached Central via the instance (0f5b349, caea383, 3e8034d, cc5447b), each `pull --ff-only`, clean tree |
| 11a Create the token `zyggy-central-read` | done 2026-10-01 | "No expiration" offered and applied (owner); `--check` confirms no expiration |
| 11b `gh` from GitHub's apt repository | done 2026-10-01 | `gh` 2.102.0 from `cli.github.com/packages stable/main` (agent verified read-only 07:46 UTC) |
| 11c Token file, AC-2 checks | done 2026-10-01 | 700/600 `zyggy`, 93 bytes, not logged in, no helper, no `GH_TOKEN`, units clean (owner pasted; agent verified read-only 07:46 UTC) |
| 11d Template `ca1aa80` + instance `instance.md` reach the VM | done 2026-10-01 | instance merge `9d32213` (template `ca1aa80` + `instance.md` `d69d019`), VM fast-forwarded; untracked `.playwright-mcp/` only |
| 11e `--check` (AC-4) | done 2026-10-01 | `login geobarteam, 68 repositories visible, 0 excluded, rate limit 5000/5000, no expiration`, exit 0; memory status unchanged |
| 11f Attended run, rerun, refusal, sweeps, GitHub-side checks, audit, cost | done 2026-10-01 | 68 lines; rerun replaced; refusal exit 5; sweeps clean (card-number false positives in transcripts, token shape 0); 42 requests | |
| 11g 0002 section 31 complete | done 2026-10-01 | 15 AC rows pass; CI 36843807079 / 36844404107 green |

## 1. Repositories, instance, deploy keys, SSH config [browser] [laptop] [vm/zyggy]

**1a [browser]** In `zyggy-org`, create two **empty private** repositories — no README, no licence, no
`.gitignore`, so the first push is a fast-forward: `zyggy-geoffrey` and `zyggy-geoffrey-memory`. (`zyggy-core`
exists since the Slice A gate.) In `zyggy-geoffrey` → Settings → Secrets and variables → Actions → Variables, set the
repository variable `ZYGGY_HYGIENE_FORBIDDEN` exactly as in `zyggy-core` (both repositories must have it; the value
is never pasted into a document).

**1b [laptop]** Create the instance from the template (skip what plan Step 6 already did — check with
`git -C d:\source\zyggy-geoffrey remote -v`):

```powershell
git clone https://github.com/zyggy-org/zyggy-core d:\source\zyggy-geoffrey
cd d:\source\zyggy-geoffrey
git remote rename origin upstream
git remote add origin https://github.com/zyggy-org/zyggy-geoffrey
# add ONLY the instance-owned files: .claude/rules/instance.md and instance/settings.local.json
git add .claude/rules/instance.md instance/settings.local.json
git diff --cached --name-only upstream/main    # exactly the two files
git commit -m "instance: Central rules and settings reference copy"
git push -u origin main
git fetch upstream
git diff --name-only upstream/main HEAD         # AC-34: only instance-owned paths
```

`instance/settings.local.json` (2-space indent, LF, final newline; **no `enabledPlugins`** in 27):

```json
{
  "env": {
    "ZYGGY_MEMORY_ROOT": "/srv/agent/central/memory",
    "ZYGGY_TENANT": "geoffrey",
    "ZYGGY_USER": "geoffrey",
    "ZYGGY_TIMEZONE": "Europe/Brussels"
  },
  "autoMemoryDirectory": "/srv/agent/central/memory/geoffrey/geoffrey/auto"
}
```

Watch the first `zyggy-geoffrey` Actions run: green, and the word-list hygiene test **ran** (not skipped). Same for
`zyggy-core` (AC-32).

Push the memory repository (built with the template README's commands: `README.md` + six `.gitkeep`, no identity
files):

```powershell
git -C d:\source\zyggy-geoffrey-memory ls-files          # README.md + geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}/.gitkeep
git -C d:\source\zyggy-geoffrey-memory status --short    # no stray evolution/ directory
git -C d:\source\zyggy-geoffrey-memory commit -m "layout"
git -C d:\source\zyggy-geoffrey-memory remote add origin https://github.com/zyggy-org/zyggy-geoffrey-memory
git -C d:\source\zyggy-geoffrey-memory push -u origin main
```

**1c [vm/zyggy]** Two deploy keys (no passphrase: the units run unattended), public halves only on screen. First
`sudo -iu zyggy` and check `whoami` prints `zyggy` — keys generated as `azureadmin` land in the wrong home directory
(2026-09-30). Before adding them on GitHub, the organisation must allow deploy keys: `zyggy-org` → Settings →
Repository → Deploy keys → "Enable deploy keys for repositories in this organization" (off by default on new
organisations; `gh api orgs/zyggy-org --jq .deploy_keys_enabled_for_repositories` shows the state, and an org admin can
flip it with `gh api -X PATCH orgs/zyggy-org -f deploy_keys_enabled_for_repositories=true` — done 2026-09-30; a read
right after the PATCH can still show `false` for a few seconds):

```bash
ssh-keygen -t ed25519 -f ~/.ssh/zyggy_zyggy-geoffrey_ed25519 -N '' -C zyggy-geoffrey@central
ssh-keygen -t ed25519 -f ~/.ssh/zyggy_zyggy-geoffrey-memory_ed25519 -N '' -C zyggy-geoffrey-memory@central
cat ~/.ssh/zyggy_zyggy-geoffrey_ed25519.pub ~/.ssh/zyggy_zyggy-geoffrey-memory_ed25519.pub
```

**[browser]** Repository → Settings → Deploy keys → Add deploy key:

- `zyggy-geoffrey`: title `central`, the `zyggy_zyggy-geoffrey_ed25519.pub` line, **"Allow write access" unchecked**
  (read-only).
- `zyggy-geoffrey-memory`: title `central`, the `zyggy_zyggy-geoffrey-memory_ed25519.pub` line, **"Allow write
  access" checked**.
- **No key for `zyggy-core`** — the template is never on the VM.

**1d [vm/zyggy]** Seed `known_hosts` from GitHub's published fingerprints, never from a first-connection prompt.
Open `https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/githubs-ssh-key-fingerprints`,
then:

```bash
ssh-keyscan -t ed25519,ecdsa,rsa github.com > /tmp/github-hostkeys
ssh-keygen -lf /tmp/github-hostkeys          # every SHA256:… must equal the published one — otherwise stop
cat /tmp/github-hostkeys >> ~/.ssh/known_hosts && rm /tmp/github-hostkeys
```

**1e [vm/zyggy]** `~/.ssh/config`, verbatim:

```
Host github.com-zyggy-geoffrey
  HostName github.com
  User git
  IdentityFile ~/.ssh/zyggy_zyggy-geoffrey_ed25519
  IdentitiesOnly yes
Host github.com-zyggy-geoffrey-memory
  HostName github.com
  User git
  IdentityFile ~/.ssh/zyggy_zyggy-geoffrey-memory_ed25519
  IdentitiesOnly yes
```

```bash
chmod 700 ~/.ssh && chmod 600 ~/.ssh/*
ssh -T git@github.com-zyggy-geoffrey          # "successfully authenticated … does not provide shell access"
ssh -T git@github.com-zyggy-geoffrey-memory   # same
ls -la ~/.ssh/                                 # two private keys, -rw------- zyggy (AC-15)
```

Private keys also go into the owner's password manager (restore) — copy them over the Tailscale SSH session by
hand; never into a repository, a runbook or 0002.

## 2. Convert `/srv/agent/central` in place into the instance clone [vm/zyggy]

Pre-check (the 02 step 8 left an empty repository and `claude-remote.log`, which stays untouched and ignored by
`*.log`):

```bash
cd /srv/agent/central
git log --oneline 2>/dev/null | wc -l         # must be 0 — otherwise stop
git remote -v                                  # nothing
ls -la                                         # .git, claude-remote.log, nothing else of note
```

Conversion:

```bash
cd /srv/agent/central
git remote add origin git@github.com-zyggy-geoffrey:zyggy-org/zyggy-geoffrey.git
git fetch origin
git checkout -b main origin/main
git remote -v                                  # only origin (never add an upstream remote on the VM)
git rev-parse HEAD                             # record in 0002 (instance SHA)
git merge-base --is-ancestor <template SHA> HEAD; echo $?   # 0 — record the template SHA in 0002
git status --porcelain                         # empty (claude-remote.log ignored)
ls -la .claude/hooks .claude/skills/remember   # -rwxr-xr-x on session-start.sh, stop.sh, lib.sh, remember.sh
```

If the scripts are not executable, the executable bit was lost on Windows: fix in `d:\source\zyggy-core` with
`git update-index --chmod=+x .claude/hooks/*.sh .claude/skills/remember/remember.sh`, commit and push the template,
then "Update Central from the template". Never `chmod` on the VM (dirty tree).

## 3. Clone the memory repository at `memory/` [vm/zyggy]

```bash
cd /srv/agent/central
git clone git@github.com-zyggy-geoffrey-memory:zyggy-org/zyggy-geoffrey-memory.git memory
git -C memory log --oneline                    # the layout commit
git config --global user.name "zyggy (central)"      # Central commits memory/ on the owner's request (2026-09-30);
git config --global user.email "central@zyggy.org"   # without an identity the first commit fails ("tell me who you are")
cd memory && find . -path ./.git -prune -o -type d -print   # ./geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}
cd /srv/agent/central && git status --porcelain            # empty (memory/ ignored)
```

If the repository were empty, create it with the template README's "Create an instance" memory commands
(`mkdir -p geoffrey/geoffrey/{areas,people,topics,daily,inbox,auto}`, a `.gitkeep` in each, one-paragraph
`README.md`). No identity files: `/seed-memory` creates them in step 8.

## 4. Install `.claude/settings.local.json` from the instance [vm/zyggy]

```bash
cd /srv/agent/central
install -m 600 instance/settings.local.json .claude/settings.local.json
stat -c %a .claude/settings.local.json                                 # 600
git check-ignore -v .claude/settings.local.json memory claude-remote.log   # all three listed
diff <(jq -S '{env, autoMemoryDirectory, enabledPlugins}' instance/settings.local.json) \
     <(jq -S '{env, autoMemoryDirectory, enabledPlugins}' .claude/settings.local.json)   # empty (AC-4)
jq -r '.autoMemoryDirectory == "\(.env.ZYGGY_MEMORY_ROOT)/\(.env.ZYGGY_TENANT)/\(.env.ZYGGY_USER)/auto"' .claude/settings.local.json   # true
```

The file holds the principal and a path, no secret. Never add `ZYGGY_NOW` or `ZYGGY_HOOKS` to it. From now on every
`claude` start in `/srv/agent/central` runs the hooks and appends a Stop line — expected.

## 5. No `CLAUDE.md` on the path; `claude doctor` [vm/zyggy]

```bash
for d in /srv/agent/central /srv/agent /srv /; do ls -la $d/CLAUDE.md $d/CLAUDE.local.md $d/.claude/CLAUDE.md; done; ls -la /srv/agent/home/.claude/CLAUDE.md
```

Every line must be `No such file or directory` (AC-1). Then `~/.local/bin/claude doctor` (read-only diagnostics;
workspace trust for `/srv/agent/central` was accepted in 02 step 8).

## 6. The `playwright` plugin at project scope, Chromium headless [vm/zyggy] [vm/root]

**6a Plugin (with plan Step 8)** — the `enabledPlugins` entry arrives committed from the template; the install only
downloads it:

```bash
cd /srv/agent/central
claude plugin marketplace list                 # claude-plugins-official present (else: claude plugin marketplace add anthropics/claude-plugins-official)
claude plugin install playwright@claude-plugins-official --scope project
claude plugin list                             # playwright, scope project, version
claude plugin details playwright               # always-on token cost → 0002
jq '.' ~/.claude/plugins/installed_plugins.json   # version + gitCommitSha = marketplace commit → 0002 (the marketplace dir is a snapshot without .git; `git rev-parse` there fails — 2026-09-30)
git diff --stat .claude/settings.json          # empty — the template ships the file in Claude Code's own layout (jq --indent 2); if not empty: "Settings drift"
git status --porcelain                         # empty
ls ~/.claude/plugins/cache/claude-plugins-official/playwright/
```

Inspect `~/.claude/plugins/cache/claude-plugins-official/playwright/<version>/` (its `.mcp.json` / plugin manifest).
Finding 2026-09-30 (version `2a8ad9f74633`): `.mcp.json` = `npx @playwright/mcp@latest`, **headed by default**, no
pinned version. Decided (owner, 2026-09-30): the template's `.claude/settings.json` carries
`env.PLAYWRIGHT_MCP_HEADLESS=true`, `PLAYWRIGHT_MCP_BROWSER=chromium`, `PLAYWRIGHT_MCP_ISOLATED=true` (the server
reads `PLAYWRIGHT_MCP_*` between its config file and CLI flags); it reaches Central with "Update Central from the
template" and is proven by the AC-33 launch. If a later plugin version changes its launch, re-check here; the
options, in this order, were: (a) the plugin's own option/config mechanism, if any; (b) an
environment setting the server honours; (c) last resort, a project-level `.mcp.json` at the root starting
`@playwright/mcp@<same version> --headless --browser chromium` with the plugin's server disabled (never two
browsers). Placement follows the split: something every instance needs is a **template** change delivered through
"Update Central from the template"; an instance-only setting goes in `instance/settings.local.json` (then the step-4
`install` + AC-4). `.mcp.json` is not an instance-owned path — option (c) needs an owner decision recorded in 0002
first.

**6b Chromium (with plan Step 12)** — `install` as `zyggy` (writes `~/.cache/ms-playwright/`), `install-deps` as
root (apt packages only; never `install` as root):

```bash
# [vm/zyggy]
cd ~/.claude/plugins/cache/claude-plugins-official/playwright/<version>/
npx playwright --version                       # note the Playwright version
npx playwright install chromium                # note the Chromium build
# [vm/root]
sudo npx playwright@<same version> install-deps chromium
```

## 7. Wrapper `--name Zyggy`, the one restart, first verification [vm/zyggy] [vm/root] [browser]

**7a [vm/zyggy] AC-7 — headless run, three digest sections, no session file** (can run before the restart):

```bash
cd /srv/agent/central
ls -t ~/.claude/projects/-srv-agent-central/ | head -1        # note it
claude -p --no-session-persistence --output-format stream-json --verbose --include-hook-events --max-turns 1 --permission-mode auto "Reply with the word OK." > /tmp/ac7.jsonl
ls -t ~/.claude/projects/-srv-agent-central/ | head -1        # unchanged
jq -c 'select(.type=="system" and .subtype=="hook_response") | {hook_name, exit: .exit_code, len: (.output|length), head: (.output|.[0:60]), tail: (.output|.[-24:])}' /tmp/ac7.jsonl
jq -r 'select(.type=="result") | .result' /tmp/ac7.jsonl     # OK
```

Expect three `SessionStart` responses whose outputs start with `<zyggy-memory-digest section="` and end with
`</zyggy-memory-digest>`, each < 10,000 characters, total ≤ 18,000, none replaced by a file path/preview. Field names
confirmed on 2.1.285 (2026-09-30): `type` = `system`, `subtype` = `hook_started` | `hook_response`, `hook_name` =
`SessionStart:startup` (or `Stop`), `exit_code`, and the text in `output` (plus `stdout`, `stderr`, `outcome`). Before seeding, `identity` holds the two headings only, `index` the `## agents.md` heading and an empty
index, `daily` nothing.

**7b [vm/root] Wrapper edit, then at once the restart** — the one sanctioned change to a 02 file; a typo stops the
remote session, hence `bash -n` and the `diff` first. Run the whole block as root (`sudo -i`): the wrapper and the
log belong to `zyggy` and are not world-readable, so `grep`/`bash -n`/`tail` as `azureadmin` fail with
"Permission denied" (2026-09-30):

```bash
sudo sed -i 's/--name central /--name Zyggy /' /srv/agent/bin/claude-remote.sh
grep -c -- '--name Zyggy' /srv/agent/bin/claude-remote.sh     # 2
grep -c -- '--name central' /srv/agent/bin/claude-remote.sh   # 0
bash -n /srv/agent/bin/claude-remote.sh                        # silent
# diff against the claude-remote.sh heredoc in runbooks/central-vm-setup.md step 9 → identical
date -u                                                        # note: wrapper applied <UTC>
sudo systemctl restart claude-remote                           # THE one restart — note the UTC time
systemctl is-active claude-remote                              # active (if it loops: "Wrapper edit")
tail -3 /srv/agent/central/claude-remote.log                   # resuming 6ba6d03b-… (the same id as before)
systemctl show claude-remote -p NRestarts
```

Record "2 of 2 — 27's deliberate restart <UTC>, resumed `<id>`, pineapple" in 0001's restart row.

**7c [browser] AC-5, AC-36** — open the Central session on the phone (Claude app → Code) or at claude.ai/code:

- the conversation shows `no CLAUDE.md found; AGENTS.md loaded: /srv/agent/central/AGENTS.md`;
- `/memory` and `/context` list `AGENTS.md`, the template's `.claude/rules/{memory,security,operations}.md` **and**
  the instance's `.claude/rules/instance.md`, no `CLAUDE.md`;
- "Which word did I ask you to remember?" → *pineapple*;
- the session list shows the Central session as **`Zyggy`** (AC-36). If it still shows `central`, record it — no
  extra restart; re-check at the next fresh session the wrapper starts ("Session title").

**7d [vm/zyggy]** After the next soak timer run: `tail -n 1 /srv/agent/soak/soak.jsonl | jq '{ts, exit}'` → `exit 0`
(AC-12: the project-scoped plugin does not reach the soak directory). Also, for AC-12:
`jq -e '.enabledPlugins' /srv/agent/central/.claude/settings.json` (exactly the one entry),
`jq 'has("enabledPlugins")' /srv/agent/central/instance/settings.local.json` (`false`),
`jq -e '.enabledPlugins // empty' ~/.claude/settings.json` (empty — no user-scope plugin).

## 8. Seeding session, seed commit, remember and Stop checks [browser] [vm/zyggy]

Decided 2026-09-30 (memory commits on request): after the interview Central asks whether to commit; on the owner's
yes it stages the seeded durable files, commits `seed <date>` and pushes — the owner reviews `git -C memory diff`
first. Central never commits memory unasked or unattended; the instance checkout is never committed from the VM.

**8a [browser]** In the remote-control session: `/seed-memory`; answer the six blocks (any language; files are
written in English). The skill creates `profile.md`, `preferences.md`, `agents.md` with front matter (`name`,
`description`, `updated`) and `areas/`, `people/`, `topics/` files, then prints `git -C memory status`.

**8b [vm/zyggy]** Review every line (only `[stated] <date>` lines and front matter, no secret, no health or
personality inference), check for a stray `evolution/`, then commit and push — the read/write key's first use:

```bash
cd /srv/agent/central/memory
git diff && git status
git add -A && git commit -m "seed $(date -u +%F)" && git push -u origin main
git rev-parse HEAD origin/main                  # equal → seed commit SHA to 0002
```

**8c [browser]** `/clear` (re-runs `SessionStart`), then AC-6: "What do you know about me, and where does that
knowledge come from?" → cites `profile.md`/`preferences.md` facts and names the memory digest.

**8d [vm/zyggy]** AC-11:

```bash
cd /srv/agent/central
ls -t ~/.claude/projects/-srv-agent-central/ | head -1
claude -p --no-session-persistence --permission-mode auto "What is the first heading of your project instructions, and what is my first name?"
ls -t ~/.claude/projects/-srv-agent-central/ | head -1       # unchanged
tail -1 memory/geoffrey/geoffrey/daily/$(TZ=Europe/Brussels date +%F).md   # the Stop line of that run
```

Expect `# Zyggy — Central` and the owner's first name.

**8e [browser]** AC-8: "Remember that my favourite tea is Earl Grey." → Claude quotes
`remembered: /srv/agent/central/memory/geoffrey/geoffrey/inbox/remember-<date>.md` and the `- [stated] <date>: …`
line. AC-9: "Remember that my GitHub token is " followed by the spec's AC-9 sample (a fake token of `ghp`,
underscore, 36 letters and digits — not reproduced here) → Claude reports the refusal naming `github-token`, does not
echo it, does not retry; the transcript shows `remember.sh` exit code 2; `grep -rn` for that prefix under `memory/`
finds nothing.

**8f [vm/zyggy]** AC-10: `tail -3 memory/geoffrey/geoffrey/daily/$(TZ=Europe/Brussels date +%F).md` shows
`- [observed] HH:MM session <8 chars>: …`; `head -5` shows the front matter once with `updated:` = today.

**8g [browser]** AC-13: `/doctor prompt-audit` → no contradiction or missing-file finding across `AGENTS.md`, the
three template rule files, `instance.md` and the `remember`/`seed-memory` skills ("Prompt audit findings"). AC-14:
after Claude reports "Saved N memories" (or after asking it to note a working preference in its auto memory),
`ls memory/geoffrey/geoffrey/auto/` shows `MEMORY.md`; `ls ~/.claude/projects/-srv-agent-central/memory/` shows
nothing newer than the step-4 install.

`inbox/` and `daily/` lines stay uncommitted after the seed until 28's dream pass; Azure Backup covers them.

## 9. Headless browser (AC-33), 0002 rows, final sweeps [browser] [vm/zyggy] [vm/root]

**9a [browser]** "Open https://example.com with the browser and tell me the page title" → `Example Domain`.
Meanwhile **[vm/zyggy]** `free -h` during the fetch (one browser ≈ 300–500 MB, swap unchanged) and
`pgrep -a chrom` after the answer (nothing left); **[vm/root]** `journalctl -k | grep -i 'out of memory'` → nothing.
Then `/plugin` → Installed lists `playwright` (project); `/mcp` → its server connected (AC-12). `/memory` lists
`security.md` (the "no logged-in sites in unattended runs" rule).

**9b [vm/zyggy]** Secret sweep (AC-15, second half) — all greps empty:

```bash
cut -f2 /srv/agent/central/.claude/hooks/secret-patterns.txt | grep -v '^#' > /tmp/pat
grep -rEn -f /tmp/pat /srv/agent/central --exclude-dir=.git --exclude-dir=memory --exclude=secret-patterns.txt --exclude-dir=tests
grep -rEn -f /tmp/pat /srv/agent/central/memory --exclude-dir=.git
grep -En -f /tmp/pat ~/.claude/settings.json /srv/agent/central/.claude/settings.local.json
grep -rEn -f /tmp/pat /srv/agent/central/instance
```

**9c** AC-18: `systemctl cat claude-remote claude-soak.service claude-soak.timer; cat /srv/agent/bin/claude-remote.sh
/srv/agent/bin/claude-soak.sh` diffed against the heredocs in `runbooks/central-vm-setup.md` → identical (the
wrapper differs from its 02 version only by `--name Zyggy` on two lines).

**9d** Fill every row of `_plans/decisions/0002-central-productive.md` (date, command, excerpt, result).

## 10. First template update through the instance (AC-35) [laptop] [vm/zyggy] [browser]

The change: the AC-13 template fix if the audit found one in a template file, otherwise a one-line `README.md`
change in `d:\source\zyggy-core` (no machine path, no principal). Then follow "Update Central from the template"
below and record in 0002: the merge commit's parents (`git -C d:\source\zyggy-geoffrey log -1 --format='%H %P'`),
the new template SHA → new instance SHA, the date; on the VM `git merge-base --is-ancestor <new template SHA> HEAD;
echo $?` → `0`, `git status --porcelain` empty, `git remote -v` still only `origin`, `ls -la .claude/hooks/*.sh`
still `-rwxr-xr-x`.

## 11. GitHub read access and the repository inventory (deliverable 31) [browser] [laptop] [vm/root] [vm/zyggy]

Gives Central one read-only GitHub credential and the owner-invoked `/github-inventory` skill, which writes one
`[observed]` line per repository the owner's account owns into
`memory/geoffrey/geoffrey/inbox/github-inventory-<date>.md`. Source of truth: `_specs/31-central-github-read-inventory.md`
and `_plans/31-central-github-read-inventory.md` (Steps 6–7 carry the same commands; this section is the durable
copy). Evidence goes into 0002, section 31.

The credential: a **fine-grained personal access token** `zyggy-central-read`, resource owner = the owner's personal
account `geobarteam`, repository access **All repositories**, permissions **Contents: Read-only + Metadata:
Read-only**, **No expiration** (366-day custom date only if the page refuses), in **one** file
`/srv/agent/home/.config/zyggy/github-read-token` (dir 0700, file 0600, `zyggy`). Only `inventory.sh` reads it and
hands it to `gh` as `GH_TOKEN` in each child's environment. Never `gh auth login`, never a settings `env` key, never a
git credential helper, never `LoadCredential=` in 31. Repositories of `zyggy-org` and of the employer are owned by
organisations and are unreachable through it by construction.

Every VM block starts with `ssh -t azureadmin@central`; for `[vm/zyggy]` then `sudo -iu zyggy` and **`whoami` →
`zyggy`** (work done as `azureadmin` lands in the wrong home).

### 11a. Create the token [browser]

GitHub → Settings → Developer settings → Personal access tokens → **Fine-grained tokens** → Generate new token:

- Token name `zyggy-central-read`; Resource owner = **your personal account** (`geobarteam`, not `zyggy-org`).
- Expiration → **No expiration** — *expect* it to be offered (a personal-account token accesses no organisation, so
  no organisation policy applies). If the page does not offer it or refuses, choose **Custom**, a date 366 days ahead,
  and **note the date** (then "Re-issue the GitHub read token" is a yearly step and `instance.md` says
  "expires on `<date>`").
- Repository access → **All repositories**.
- Permissions → Repository permissions → **Contents: Read-only** → *expect* **Metadata: Read-only** to appear as
  mandatory. No other repository permission, **no Account permission**.
- Generate token → copy the value **once** into the password manager; close the page. The value goes into no file on
  the laptop, no chat, no runbook, no record.

*Expect afterwards* on the Fine-grained tokens list: `zyggy-central-read`, "All repositories", 2 permissions, "Never
expires" (or the date), "Last used: Never". Record in 0002 (AC-1): the date, whether "No expiration" was offered,
the shown expiry text, name, scope "All repositories of `geobarteam`", permissions — never the value.

### 11b. Install `gh` from GitHub's apt repository [vm/root]

As `azureadmin` with `sudo`; the four documented steps (never the `universe` package — 2.45.x is broken):

```bash
sudo mkdir -p -m 755 /etc/apt/keyrings
out=$(mktemp) && wget -nv -O"$out" https://cli.github.com/packages/githubcli-archive-keyring.gpg && sudo install -m 644 "$out" /etc/apt/keyrings/githubcli-archive-keyring.gpg && rm -f "$out"
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | sudo tee /etc/apt/sources.list.d/github-cli.list > /dev/null
sudo apt update && sudo apt install -y gh
gh --version; apt-cache policy gh | head -5
```

*Expect*: `gh version 2.<xx>.<y> (20xx-xx-xx)` above 2.46; `apt-cache policy gh` shows `Installed: 2.<xx>.<y>-…` and
`*** … https://cli.github.com/packages stable/main` as the winning source (not `noble/universe`). The version line goes
into 0002's "Tools on Central" table (AC-3).

### 11c. Write the token file [vm/zyggy]

`sudo -iu zyggy`, `whoami` → `zyggy`. Paste the first line, then paste the token value at the silent prompt and
press Enter (`read -rs` echoes nothing; the value reaches no command line and no history):

```bash
umask 077; mkdir -p ~/.config/zyggy; read -rs t; printf '%s' "$t" > ~/.config/zyggy/github-read-token; unset t
stat -c '%a %U' ~/.config/zyggy ~/.config/zyggy/github-read-token; wc -c < ~/.config/zyggy/github-read-token
ls ~/.config/gh 2>&1; gh auth status; echo "gh auth status exit $?"
git config --global --get-regexp credential; echo "credential helper exit $?"
grep -l GH_TOKEN ~/.claude/settings.json /srv/agent/central/.claude/settings.json /srv/agent/central/.claude/settings.local.json /srv/agent/central/instance/settings.local.json; echo "grep exit $?"
systemctl show claude-remote claude-soak.service -p Environment
```

*Expect*: `700 zyggy` and `600 zyggy`; a byte count of **93** (a fine-grained token: an 11-character prefix + 22 + 1 + 59 characters;
another count means a partial paste — redo the first line); `ls: cannot access '/srv/agent/home/.config/gh': No such
file or directory`; `You are not logged into any GitHub hosts. To log in, run: gh auth login` and `gh auth status exit
1`; `credential helper exit 1`; `grep exit 1` (no file lists `GH_TOKEN`); two `Environment=` lines without any token
variable (AC-2). **Never** run `gh auth login`, `gh auth setup-git` or `cat` on the token file.

### 11d. The template and the instance reach the VM [laptop] [vm/zyggy]

`[laptop]` "Update Central from the template" (below) for the template commit carrying the skill, the rule and the
tests, plus the instance commit carrying the `instance.md` "## GitHub" section — both pushed, both Actions runs green
with the word-list test run (AC-15). Then `[vm/zyggy]`:

```bash
cd /srv/agent/central && git pull --ff-only && git status --porcelain && git rev-parse HEAD
ls -la .claude/skills/github-inventory/ && head -6 .claude/skills/github-inventory/SKILL.md && grep -c '## GitHub' .claude/rules/security.md .claude/rules/instance.md
```

*Expect*: `Fast-forward`, an empty status, the new instance SHA (= `git -C d:\source\zyggy-geoffrey rev-parse
origin/main`); `-rwxrwxr-x … inventory.sh` and `SKILL.md`; the front matter with `disable-model-invocation: true`;
`1` for both rule files.

### 11e. `--check` [vm/zyggy]

In a throw-away shell (the "Re-run the digest by hand" idiom), so the `ZYGGY_*` variables do not linger:

```bash
cd /srv/agent/central && bash -c 'set -a; . <(jq -r ".env | to_entries[] | \"\(.key)=\(.value)\"" .claude/settings.local.json); set +a; .claude/skills/github-inventory/inventory.sh --check; echo "exit $?"'
git -C memory status --porcelain
```

*Expect*: one line `github-inventory: login geobarteam, <N> repositories visible, 0 excluded (instance list), rate
limit <remaining>/5000, no expiration` (or `token expires <date>` matching the token page) and `exit 0`; `N` equals
the number of repositories on your GitHub profile → Repositories (all visibilities — `affiliation=owner`; if the
counts differ, note both and read "No repositories visible" / "GitHub token rejected" before going on); the memory
status unchanged from before (AC-4). Record the exact header text `--check` printed for the expiry. Then `/clear` in
the remote session (the rules changed) — no restart.

### 11f. The attended run and its checks [browser] [vm/zyggy]

1. **AC-5 `[browser]`** in the `Zyggy` session: `/clear`, then `/github-inventory`. *Expect*: Claude runs the script
   once through the Bash tool and answers with `inventory: /srv/agent/central/memory/geoffrey/geoffrey/inbox/github-inventory-<date>.md`,
   the counts line `<n> repositories listed (<N> visible, cap 200), 0 excluded (instance list), <s> skipped (secret
   pattern), <r> README reads, login geobarteam`, and shows the repository lines as data (it may summarise them; it
   must not act on any of them). Glance over the purposes for anything phrased as an instruction.
2. **`[vm/zyggy]` the file checks**:

   ```bash
   cd /srv/agent/central && f=memory/geoffrey/geoffrey/inbox/github-inventory-$(TZ=Europe/Brussels date +%F).md
   head -5 "$f"; grep -c '^- \[observed\] ' "$f"; grep -vcE '^- \[observed\] [0-9]{4}-[0-9]{2}-[0-9]{2} \[github-inventory [0-9]{4}-[0-9]{2}-[0-9]{2}\]: [^ ]+/[^ ]+ \((private|public), (owner|collaborator)(, fork)?(, archived)?\) — .+ — pushed ([0-9]{4}-[0-9]{2}-[0-9]{2}|never) — .+$' "$f"
   sed -n 's/^- \[observed\] [^:]*: //p' "$f" | LC_ALL=C.UTF-8 awk '{ if (length($0) > 240) bad++ } END { print bad+0 " facts over 240 characters" }'
   sed -n 's/^- \[observed\] [^:]*: \([^ ]*\) .*/\1/p' "$f" | sort | uniq -d | wc -l
   git -C memory status --porcelain
   ```

   *Expect*: front matter `---`, `name: github inventory <date>`, `description: GitHub repositories visible to the
   read-only token on <date> (github-inventory skill)`, `updated: <date>`, `---`; the line count = `min(N − X, 200)`
   from 11e (plus 1 when `N > 200`, the marker); the "not matching" count = `5` (the five front-matter lines — a
   marker line, if any, is checked by eye); `0 facts over 240 characters`; `0` duplicates; the memory status shows
   **only** `?? geoffrey/geoffrey/inbox/github-inventory-<date>.md` and today's `daily/<date>.md` (plus whatever was
   listed before); nothing under `areas/`, `people/`, `topics/`, `profile.md`, `preferences.md`, `agents.md`.
3. **AC-12 `[browser]`**: `/github-inventory` again in the same session → the same counts line. `[vm/zyggy]`: re-run
   block 2 → same count, `0` duplicates; `grep -c '^---$' "$f"` → `2`; `grep -c "^updated: $(TZ=Europe/Brussels date +%F)$" "$f"`
   → `1`; `find memory -name '*.tmp*'` → nothing; the memory status → the same one inventory file (+ `daily/`).
4. **AC-13 `[vm/zyggy]` — the unattended shape is refused**:

   ```bash
   cd /srv/agent/central && ls -t ~/.claude/projects/-srv-agent-central/ | head -1
   ZYGGY_HOOKS=off claude -p --no-session-persistence --permission-mode auto "/github-inventory"
   ls -t ~/.claude/projects/-srv-agent-central/ | head -1; git -C memory status --porcelain; stat -c %Y memory/geoffrey/geoffrey/inbox/github-inventory-$(TZ=Europe/Brussels date +%F).md
   ```

   *Expect*: Claude reports `github-inventory: refused: unattended run (ZYGGY_HOOKS=off)`, exit 5, and says it will
   not retry or try another way; the newest session file name unchanged; the memory status unchanged; the inventory
   file's mtime unchanged from block 3 (no `daily/` line either: the Stop hook is off too — expected).
5. **AC-7 `[vm/zyggy]` — the sweeps**:

   ```bash
   cut -f2 /srv/agent/central/.claude/hooks/secret-patterns.txt | grep -v '^#' > /tmp/pat
   grep -rEn -f /tmp/pat /srv/agent/central --exclude-dir=.git --exclude-dir=memory --exclude=secret-patterns.txt --exclude-dir=tests; echo "instance tree: $?"
   grep -rEn -f /tmp/pat /srv/agent/central/memory --exclude-dir=.git; echo "memory: $?"
   grep -En -f /tmp/pat ~/.claude/settings.json /srv/agent/central/.claude/settings.local.json; echo "settings: $?"
   grep -rEn -f /tmp/pat /srv/agent/central/instance; echo "instance/: $?"
   grep -lE -f /tmp/pat ~/.claude/projects/-srv-agent-central/*.jsonl; echo "transcripts: $?"
   rm /tmp/pat
   ```

   *Expect*: every `grep` prints nothing and each `echo` shows `1`. A `card-number`/`iban` hit on a legitimate long
   number in memory is the owner's decision (rephrase or a documented exception; never edit a pattern silently). A hit
   in a transcript means the token value was shown in a session — "Revoke the GitHub read token" **now** and re-issue.
6. **AC-6 `[browser]`**: Fine-grained tokens → `zyggy-central-read` → "Last used" = today; Settings → Security log,
   today's window → no `repo.*`, `issues.*`, `pull_request.*`, `git.push` events in the run window (reads are not
   logged as events); the three most recently pushed repositories → no new commit, issue, comment or star.
7. **AC-8 `[browser]`**: `/doctor prompt-audit` in the session → no contradiction across `AGENTS.md`, the rules
   (incl. `instance.md`) and the three skills. A finding is fixed where the text lives (instance file →
   `d:\source\zyggy-geoffrey`; template file → `d:\source\zyggy-core`, then "Update Central from the template"), never
   on the VM. `[vm/zyggy]` `grep -n github-inventory .claude/rules/security.md AGENTS.md` → the section and the bullet.
8. **AC-14 `[browser]`**: `/cost` after the AC-5 turn (or `total_cost_usd` of a `claude -p --no-session-persistence
   --permission-mode auto --output-format json "/github-inventory"` run, which replaces the same-day file — acceptable);
   then the 11e `--check` again → `remaining` dropped by about `1 + ⌈N/100⌉ + <r>` plus the `--check` calls.

### 11g. Record [laptop]

Fill 0002 section 31: the Dates line, one dated row per AC-1..AC-15, the Tools table `gh` row, the Credentials row
(no expiration or the date), "Repository scope" (`N`, exclusions), Costs; set this section's status rows to done.

### Run the inventory by hand [vm/zyggy]

```bash
cd /srv/agent/central && bash -c 'set -a; . <(jq -r ".env | to_entries[] | \"\(.key)=\(.value)\"" .claude/settings.local.json); set +a; .claude/skills/github-inventory/inventory.sh --check'
cd /srv/agent/central && bash -c 'set -a; . <(jq -r ".env | to_entries[] | \"\(.key)=\(.value)\"" .claude/settings.local.json); set +a; .claude/skills/github-inventory/inventory.sh --max 500'
```

`--check` writes nothing; the run (default cap 200, `--max 1..500`) replaces today's inventory file. Never with
`ZYGGY_HOOKS=off` (exit 5 by design), never through `gh` directly.

### Re-issue the GitHub read token [browser] [vm/zyggy]

Only on suspicion of compromise, on a repository-access change the token page cannot edit in place, or — with the
366-day fallback — once a year before the date `--check` shows. **Never a scheduled rotation otherwise** (owner
decision 2026-09-30). Regenerate the token on its page (or create a new one exactly as 11a and delete the old one),
same repository access and permissions; copy the value once into the password manager; overwrite the file with the
11c first line (`umask 077; … read -rs t; …`); the 11c checks; 11e `--check` → exit 0; a dated 0002 row.

### Revoke the GitHub read token [browser] [vm/zyggy]

When in doubt, revoke first: GitHub → Fine-grained tokens → `zyggy-central-read` → **Delete** (immediate: every call
returns 401). Then `[vm/zyggy]` 11e `--check` → exit 6 with `gh: Bad credentials (HTTP 401)` (GitHub rejects it);
`shred -u ~/.config/zyggy/github-read-token`; `--check` → exit 3 `token file … not found`. Check Settings → Security log for
write events in the suspected window (none are possible with read-only permissions); a dated 0002 row. The two deploy
keys, `~/.ssh/config` and the later bus PAT are **not** touched. Re-issue per 11a/11c only if the inventory is still
wanted.

### Exclude repositories from the inventory [laptop] [vm/zyggy]

What enters memory is narrowed on Central, not on GitHub. `[laptop]` add one `owner/name` per line to
`d:\source\zyggy-geoffrey\instance\github-inventory-exclude.txt` (`#` comments and blank lines allowed, matched
case-insensitively; a malformed line makes the script exit 3), commit, push (instance CI green); `[vm/zyggy]`
`git -C /srv/agent/central pull --ff-only`; 11e `--check` shows `<X> excluded (instance list)`. Never edit the file on
the VM. Update 0002 "Repository scope" with the list and the date. This is also the answer to "Extend the repository
selection" in `instance.md`: with **All repositories** every new repository the account owns is already visible; only
exclusions change.

### Extend the repository selection

Nothing to do on GitHub: the token has **All repositories**, so a repository the account creates later is visible at
the next run. A repository missing from the inventory is on the exclusion list (`--check` shows `<X> excluded`),
owned by an organisation (unreachable by construction — never read another way), or past the cap ("Inventory
truncated"). To drop one, "Exclude repositories from the inventory".

### Narrow the token's repository access [browser]

Only if the owner later wants the token itself back to "Only select repositories". Open `zyggy-central-read` on the
Fine-grained tokens page: if it offers **Edit** for repository access, change it in place; otherwise regenerate or
re-issue ("Re-issue the GitHub read token"). Record in 0002 which the page offered, and update `instance.md` ("can
read every repository that account owns" no longer holds) in `d:\source\zyggy-geoffrey`.

## Re-run the digest by hand

```bash
cd /srv/agent/central && set -a && . <(jq -r '.env | to_entries[] | "\(.key)=\(.value)"' .claude/settings.local.json) && set +a && .claude/hooks/session-start.sh identity | wc -c
```

Same for `index` and `daily`; each must print < 10,000 (caps 6,000 / 4,000 / 8,000 bytes). Without the `| wc -c` it
prints the section. Run it in a throw-away shell (`exit` afterwards) so the `ZYGGY_*` variables do not linger.
Alternative: `claude --init-only` runs the `SessionStart` hooks and exits.

## Adding a plugin later

Only through this sequence plus a dated row in 0002 (name@marketplace, version, marketplace commit, author —
Anthropic preferred, external flagged — always-on tokens, purpose, MCP servers/hooks it adds). Review a plugin that
adds hooks or MCP servers in `~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/` **before** installing it.

- **Template plugin** (the template's rules or skills depend on it): add `"<name>@claude-plugins-official": true`
  to `enabledPlugins` in `d:\source\zyggy-core\.claude\settings.json`, PROVE, commit, push; "Update Central from the
  template"; then on the VM `claude plugin install <name>@claude-plugins-official --scope project` (`git diff
  .claude/settings.json` stays empty).
- **Instance-only plugin**: on the VM `claude plugin install <name>@claude-plugins-official --scope local`; add the
  same `enabledPlugins` entry to `d:\source\zyggy-geoffrey\instance\settings.local.json`, commit, push, pull on the
  VM (AC-4 detects a missing copy).
- **Switch a template plugin off on this instance**: `"<name>@<marketplace>": false` in `instance/settings.local.json`,
  then the same commit → push → pull → step-4 `install`.
- **Never at user scope on Central** (the soak directory must not inherit it).
- **Auto-update** is left on (marketplace default). To pin after a breaking update:
  `extraKnownMarketplaces["claude-plugins-official"].autoUpdate=false` in `~/.claude/settings.json` of `zyggy`,
  recorded in 0002.

## Update Central from the template

```powershell
# [laptop]
git -C d:\source\zyggy-geoffrey pull upstream main              # a merge; conflict-free by the instance-owned-paths rule
git -C d:\source\zyggy-geoffrey log -1 --format='%H %P'         # review the merge
git -C d:\source\zyggy-geoffrey diff --name-only upstream/main HEAD   # still only instance-owned paths
git -C d:\source\zyggy-geoffrey push origin main                # zyggy-geoffrey Actions green
```

```bash
# [vm/zyggy]
git -C /srv/agent/central pull --ff-only                        # "Fast-forward"
git -C /srv/agent/central status --porcelain                    # empty
```

Then `/clear` in the remote-control session and check `/context` (instruction files listed as before). A hook or
settings change is additionally verified with the step-7a (AC-7) `-p` run; if `instance/settings.local.json`
changed, re-run the step-4 `install` and the AC-4 `diff`. Never restart `claude-remote` for an update, never edit
files on the VM.

## Troubleshooting

### Hooks report configuration error

Hook exits 3, no digest section, `SessionStart hook error` with the stderr line; `remember` reports exit 3. Check
that `.claude/settings.local.json` exists and has the four `env` keys — re-install it from
`instance/settings.local.json` (step 4); check workspace trust (`claude doctor`); fallback `env` in
`~/.claude/settings.json`.

### Memory root missing

Exit 3 as above: `<root>/<tenant>/<user>` does not exist (memory repository not cloned, wrong principal). Clone or
seed per steps 3–4; check `ZYGGY_TENANT`/`ZYGGY_USER` against the directory names.

### AGENTS.md not loaded

A `CLAUDE.md`/`CLAUDE.local.md`/`.claude/CLAUDE.md` exists on or above `/srv/agent/central`: `AGENTS.md` silently not
loaded, the identity section starts with the `[warning] CLAUDE.md found at …` line, `/memory` lists a `CLAUDE.md`.
Run the step-5 AC-1 loop, delete the stray file, restart (a restart is counted in 0001).

### Digest truncated

A section ends with `[digest truncated: …]` and one stderr line. Shrink `profile.md`/`preferences.md`, let the dream
(28) roll up `daily/`, or raise the cap with `ZYGGY_DIGEST_BYTES_*` in `instance/settings.local.json` `env`
(≤ 9,500; then commit, push, pull, step-4 `install`).

### Section replaced by a file path

`hook_response` shows a path plus a preview: a hook output exceeded 10,000 characters, so the clamp failed. Fix the
override (≤ 9,500), re-run step 7a (AC-7).

### Hook timeout

Output discarded, session continues without the section. Check `du -sh memory/`; raising `timeout` is a change to
the committed `.claude/settings.json`, i.e. a `zyggy-core` commit pulled through the instance.

### jq missing

`stop.sh` exits 3 with `jq not found`; nothing written. `sudo apt-get install -y jq` (step 5 of the 02 runbook).

### Stop note refused

stderr `stop: note refused (pattern <name>)`, nothing written. Expected behaviour; nothing to fix.

### Daily cap reached

The daily file holds the marker `- [observed] cap reached: no further hook lines today`, then silence for the day.
Expected on a very chatty day; the dream rolls it up.

### Remember refused

`remember.sh` exits 2 on a legitimate fact (false positive, e.g. a long order number); Claude tells the owner;
nothing written. Store the fact without the offending token, or add an exception to `secret-patterns.txt` with a test
in `zyggy-core` (a template change) — never edit the pattern silently. A GitHub token pasted into the conversation is
refused by the `github-token` pattern; if it was a real value, "Revoke the GitHub read token" (section 11).

### Hooks silent

No `hook_response` events in AC-7; project hooks and `enabledPlugins` inactive: workspace trust not accepted for
`/srv/agent/central`. Start `claude` interactively once in the directory and accept trust (02 step 8).

### Plugin error

`/plugin` → Errors tab, `claude plugin list` status; the session continues. Disable it at project scope (or
`"playwright@claude-plugins-official": false` in `instance/settings.local.json` for this instance only) and record
it in 0002.

### Playwright launch

The MCP tool returns a launch error naming the missing executable/library, or `Missing X server`; no page fetched.
Re-run `npx playwright install chromium` as `zyggy` from the plugin's cache directory and `install-deps` as root
(step 6b) for the **same** Playwright version; verify the server's headless option (step 6a); record the version
pair (plugin ↔ Chromium) in 0002.

### Browser RAM

Swap use in `free -h`, an OOM line in `journalctl -k`, the MCP server killed. One browser at a time; close it after
each task; if recurrent, `az vm resize` to `Standard_B2as_v2` per the 02 cost note (a reboot, ≈ €77/month).

### Deploy key

`Permission denied (publickey)` on `git fetch`/`pull`/`push`; nothing else affected. Check the `~/.ssh/config` alias
(`github.com-zyggy-geoffrey` / `github.com-zyggy-geoffrey-memory`) matches the remote URL (`git remote -v`), key mode
0600 and owner `zyggy`, and that the key is attached to the right repository with the right access (instance
read-only, memory write). `ssh -vT git@github.com-zyggy-geoffrey` shows which key is offered.

### Template update conflicts

`git pull upstream main` in `d:\source\zyggy-geoffrey` stops with a conflict; nothing reaches the VM.
`git diff --name-only upstream/main HEAD` lists the template-owned file the instance edited (or the instance-owned path
the template started shipping). `git merge --abort`, move the instance change into an instance-owned path (or back
out the template change), merge again.

### VM checkout diverged

`git pull --ff-only` on the VM refuses (`Not possible to fast-forward` / `Your local changes … would be overwritten`);
the VM stays at the old SHA. `git status` on the VM; restore tracked files (`git restore .`); never commit on the VM;
if a commit exists, save anything unexpected for the owner, then `git reset --hard origin/main`.

### Settings drift

The AC-4 `diff` (step 4) is non-empty (hand edit, a local-scope plugin install not copied back); behaviour follows the
live file. Copy the intended change into `instance/settings.local.json` on the laptop, commit, push, pull on the VM,
re-run the step-4 `install`. `permissions` entries written by Claude Code are expected and not drift.

### Instance CI red

Red hygiene tests in `zyggy-geoffrey` Actions: a template-owned file was edited in the instance (move the change to the
template), an instance rule file exceeds 200 lines, or an instance skill script breaks the script rules
(shebang, `set -euo pipefail`, shellcheck, no git). `ZYGGY_HYGIENE_FORBIDDEN` unset shows as one skipped test, not
red — but set it (step 1a).

### Prompt audit findings — `instance.md` contradicts a template rule

`/doctor prompt-audit` finding (AC-13); Claude may follow either. The instance rule may add or tighten, never relax —
rewrite `.claude/rules/instance.md` in `d:\source\zyggy-geoffrey`, commit, push, `git pull --ff-only` on the VM,
`/clear`, re-run the audit.

### Auto memory

Claude Code cannot save memories or saves them elsewhere; AC-14 fails. Check `autoMemoryDirectory` in
`.claude/settings.local.json` (step 4), that the directory exists and is owned `zyggy:zyggy`.

### Session title

After the restart the resumed session is still titled `central`; everything else works. Expected if `--name` does not
retitle a resumed session on this version: record it in 0002; the next fresh session started by the wrapper is titled
`Zyggy` (AC-36). Optional, per the docs: rename it from claude.ai or the app. No extra restart.

### Wrapper edit

`claude-remote.service` restarts in a loop after the `--name Zyggy` edit; no remote session. `diff` the wrapper against
the `claude-remote.sh` heredoc in `runbooks/central-vm-setup.md`, fix, `bash -n`, `sudo systemctl restart
claude-remote` (counted in 0001).

### Wrong session resumed

A `claude -p` without `--no-session-persistence` in `/srv/agent/central` became the newest session file; the Q4
wrapper resumed it at the next restart and the remote conversation appears "lost". `claude --resume <the remote id>`
once by hand, or delete the stray `.jsonl` in `~/.claude/projects/-srv-agent-central/`; always use the flag.

### Disk full

Atomic append fails, hook exits non-zero, session continues. `df -h /srv/agent`; roll up daily files; resize the Azure
data disk. `github-inventory` behaves the same way: its `mv` fails, the `.tmp` file is removed, no torn inventory file.

### Prompt audit findings — `AGENTS.md` vs a rule or a plugin skill

`/doctor prompt-audit` reports a contradiction between `AGENTS.md` and a rule or a plugin skill (AC-13 fails). Fix the
text where it lives — a template file in `d:\source\zyggy-core` (then "Update Central from the template"), an instance
file in `d:\source\zyggy-geoffrey` — and re-run.

### GitHub inventory: configuration error

`github-inventory` exits 3 with one stderr line; nothing written, `gh` never called. Branches by the line:

- **Token file** (`token file … not found` / `is empty` / `must be mode 0600 (is …)` / `must be owned by zyggy` / `is
  not a regular file`): re-create it as `zyggy` with the 11c first line (`umask 077`), or `chmod 600` /
  `chown zyggy:zyggy` it; it lives in `/srv/agent/home/.config/zyggy/`, never in root's or `azureadmin`'s home (`whoami`
  → `zyggy` first). `ZYGGY_GITHUB_TOKEN_FILE` is for tests and hand runs only — never in a settings file.
- **Exclusion file** (`exclusion file … line <n> is not owner/name`): fix `instance/github-inventory-exclude.txt` on the
  laptop (one `owner/name` per line), commit, push, `git pull --ff-only` on the VM. Never edit it on the VM.
- **`gh not found` / `jq not found`**: 11b (or `apt install jq`); see "gh from universe".
- **`configuration error: …`** (a `ZYGGY_*` variable, the memory directory): "Hooks report configuration error".

### gh from universe

`gh` is missing, or `apt-cache policy gh` shows `noble/universe` 2.45.x as installed (its API calls fail with a
deprecation error → exit 6). `sudo apt remove gh`, then 11b; `apt-cache policy gh` must show
`https://cli.github.com/packages stable/main` as the winning source. Record the new version in 0002's Tools table.

### GitHub token rejected

`github-inventory` exits 6: `GitHub request failed (<gh's first stderr line>) — see runbook "GitHub token rejected"`;
nothing written. By the line:

- **HTTP 401 `Bad credentials`**: the token was deleted, regenerated elsewhere or (366-day fallback) expired. Look at
  the Fine-grained tokens page (present? expiry?); "Re-issue the GitHub read token". If you did not delete it, treat it
  as a compromise: "Revoke the GitHub read token".
- **HTTP 403** (not a rate limit): the token's repository access or permissions were changed on GitHub — compare with
  11a; re-issue with the 11a settings.
- **HTTP 403/429 `API rate limit exceeded`**: "GitHub rate limit".
- **Network** (`dial tcp …`, DNS, TLS, `connection refused`): as `zyggy`, `curl -sI https://api.github.com | head -1`
  → `HTTP/2 200`; otherwise check the VM's outbound network (NSG, DNS) before retrying. Nothing to fix on the token.

### GitHub rate limit

Exit 6 with `API rate limit exceeded`. A run needs about `1 + ⌈N/100⌉ + <README reads>` requests (≤ ~300) of the
5,000/h; something else used the budget. `--check` shows `rate limit <remaining>/<limit>` (it fails too while the limit
is exhausted); wait for the hourly reset and run again.

### Inventory refused: unattended run

`github-inventory: refused: unattended run (ZYGGY_HOOKS=off)`, exit 5, nothing written. Expected: the skill runs only
when the owner invokes it in a conversation (until the work-boundary rules of 18–20). A timer or `claude -p` with
`ZYGGY_HOOKS=off` is meant to be refused; never work around it.

### Inventory truncated

The file ends with `inventory truncated: <n> of <total> repositories listed (most recently pushed first)` and stderr
says so: the account owns more than the cap (200). Run by hand with `--max <up to 500>` ("Run the inventory by hand"),
or raise `ZY_INVENTORY_CAP` in the template (a template change), or exclude repositories.

### Repository skipped (secret pattern)

stderr `github-inventory: <owner/repo> skipped (secret pattern <name>)`, counted in `<s> skipped`. The description or
README line looked like a secret; the repository is left out, the value never echoed. Expected. If it really holds a
secret, fix the description/README on GitHub (and rotate that secret); if it is a false positive, leave it or exclude
the repository.

### README not read

stderr `github-inventory: <owner/repo>: README not read (<gh's line>)`; the line says `(no description)`; the run
continues. A 5xx or unreadable README; a missing README (404) is silent. Expected; nothing to fix — a description on
GitHub avoids the README read altogether.

### No repositories visible

`inventory: no repositories visible to the token` (and `0 repositories visible` from `--check`); nothing written, an
existing same-day file kept. Causes: the account owns no repository; the token's repository access was narrowed to an
empty selection; the resource owner is wrong (an organisation's repositories need a token owned by that organisation —
not wanted here); every repository is on the exclusion list (`--check` shows `<X> excluded`). Compare with the
profile's Repositories count and the token page.

### Instruction found in repository text

Claude reports that an inventory line (a description or README line) reads like an instruction. Expected: the line is
data (fenced, bounded, sanitised) and is not followed. Tell the owner; optionally add the repository to
`instance/github-inventory-exclude.txt` ("Exclude repositories from the inventory").

### gh not logged in

The session shows `To get started with GitHub CLI, please run: gh auth login` (or `gh auth status` → not logged in).
Expected and intended: `gh` is never logged in on Central; the message appears only when `gh` was called directly,
which the rules forbid — only `inventory.sh` calls it, with `GH_TOKEN` per call. Never run `gh auth login`; if Claude
called `gh` itself, run `/doctor prompt-audit` and fix the rule wording where it lives.

### A `github` plugin was installed by mistake

`/mcp` shows the `github` plugin's server failing auth (it needs `GITHUB_PERSONAL_ACCESS_TOKEN`, which does not
exist on Central). Disable it at the scope it was installed (`claude plugin uninstall github@claude-plugins-official
--scope <scope>`, or `false` in `enabledPlugins`), record it in 0002, never define the variable.

### GitHub token suspected leaked

The file's mode changed, the VM may be compromised, or the value appeared in a transcript (the AC-7 sweep hit). The
script shows nothing — a non-expiring token keeps working until revoked. Go to "Revoke the GitHub read token" (section
11) **now**: delete it on GitHub first, then shred the file, then re-issue.

## Restore the instance and memory repositories on a fresh VM

After `restore-central.md` steps 1–5 of the 02 runbook (VM, disk, packages, `zyggy` user, Claude login, units):

1. **[vm/zyggy]** Deploy keys: restore `zyggy_zyggy-geoffrey_ed25519` and `zyggy_zyggy-geoffrey-memory_ed25519`
   (+ `.pub`) from the owner's password manager into `~/.ssh/`, or re-issue them (step 1c; revoke the old keys on
   GitHub first). `known_hosts` (step 1d), `~/.ssh/config` (step 1e), `chmod 700 ~/.ssh && chmod 600 ~/.ssh/*`,
   both `ssh -T` checks.
2. **[vm/zyggy]** If `/srv/agent/central` survived on the restored disk, `git -C /srv/agent/central status` and
   `git pull --ff-only`; otherwise clone the instance and the memory:

   ```bash
   git clone git@github.com-zyggy-geoffrey:zyggy-org/zyggy-geoffrey.git /srv/agent/central
   cd /srv/agent/central
   git clone git@github.com-zyggy-geoffrey-memory:zyggy-org/zyggy-geoffrey-memory.git memory
   ```

   Uncommitted `inbox/`/`daily/` lines exist only on the backed-up disk until 28 pushes them — prefer the restored
   disk's `memory/` when it is newer than `origin/main`.
3. **[vm/zyggy]** `install -m 600 instance/settings.local.json .claude/settings.local.json` — nothing is rewritten by
   hand.
4. **[vm/zyggy]** `claude plugin install playwright@claude-plugins-official --scope project` and the Chromium half
   (step 6b) if `~/.claude` or `~/.cache/ms-playwright` were not restored; accept workspace trust once if asked.
5. Verification: AC-1 (step 5), AC-4 (step 4 `diff`), AC-7 (step 7a), AC-5 (step 7c) after the units start.
6. **[vm/root] [vm/zyggy]** GitHub read access: `gh` per 11b if the package is gone; restore
   `~/.config/zyggy/github-read-token` from the password manager with the 11c first line (or re-issue per 11a/11c);
   the 11c checks; 11e `--check` → exit 0.

## What the agent may verify read-only

The agent never runs `claude` on the VM, never restarts a unit, never writes a file, never `cat`s a private key. It
collects evidence with:

```powershell
az vm run-command invoke -g zyggy-central -n central --subscription "Abonnement Visual Studio Enterprise" --command-id RunShellScript --scripts "bash -c '<commands>'" --query "value[0].message" -o tsv
```

The script runs as root under `sh` — hence the `bash -c` wrapper; use absolute paths (`/srv/agent/home`,
`/srv/agent/central`). Git refuses a `zyggy`-owned repository for root ("detected dubious ownership"): run git as
`runuser -u zyggy -- git -C /srv/agent/central …` (or `git -c safe.directory='*' -C …`); never `git config --global`
on the VM. Allowed: `ls`, `cat` (not keys), `stat`, `grep`, `jq`, `free`, `swapon --show`, `df`, `pgrep`,
`journalctl`, `systemctl cat|status|show|is-active`, and git `remote -v`, `rev-parse`, `status`, `log`,
`merge-base --is-ancestor`, `ls-files`, `check-ignore`. Example:

```powershell
az vm run-command invoke -g zyggy-central -n central --subscription "Abonnement Visual Studio Enterprise" --command-id RunShellScript --scripts "bash -c 'runuser -u zyggy -- git -C /srv/agent/central remote -v; runuser -u zyggy -- git -C /srv/agent/central rev-parse HEAD; ls -la /srv/agent/home/.ssh'" --query "value[0].message" -o tsv
```

GitHub read access (section 11): `stat` and `wc -c` of `/srv/agent/home/.config/zyggy` and its token file are
allowed — its content is never read, printed or grepped for; `gh --version`, `gh auth status` (as `zyggy`, via
`runuser`) and `apt-cache policy gh` are allowed; never `gh api` or any other `gh` call that uses the token.

If `az` on the laptop is not logged into the personal subscription, the owner runs the commands and pastes the
output instead.
