# 0001 — Transport A and Central VM

Status: closed 2026-10-15 by owner decision ("you can close 2 & 3"): transport A confirmed on the evidence below
(deliverable 04 and patch 0.8.1); deliverable 05 was not built separately — no `Gates/P0_*` scenario, no latency
target (O21). VM soak section (deliverable 02) closed 2026-10-05 by owner decision — unfinished checks carried to
33's Central evidence (plan 33 Steps 21–22).

Owner decision 2026-10-05, verbatim: "Before you implement I permit you to break and finish open work of preceding
plans here in .Net so that we've in the end a clean slate with all finished work. In anyway the open items in other
plans are all nearly finished with last test, you can test this later in the .net version."

## VM soak (deliverable 02)

- Day 0: 2026-09-29 19:07 UTC (both units healthy)  ·  Day 7: 2026-10-06  ·  Claude Code version: 2.1.284  ·
  VM: Standard_B2als_v2, westeurope
- Deviations:
  - VM size B2als_v2 (4 GB) instead of §13 Q3's B2as_v2 (8 GB) to stay under €60/month (owner, 2026-09-29).
  - `--name central` is accepted together with `--remote-control` in 2.1.284; no change to the wrapper.
  - The step 9 unit needed two fixes before it ran (both now in `runbooks/central-vm-setup.md`): (1) its own tmux
    socket (`tmux -L claude-remote`), because the owner's step 8 tmux server took the session and systemd
    restart-looped; (2) no pipe on Claude's stdout, because `| tee` made Claude switch to `--print` mode and exit.
    The error lines in `claude-remote.log` before 2026-09-29 19:07:14 UTC come from these two failures, not from the
    soak.
  - Steps 7–8 were first reported done but the VM showed `zyggy` not logged in; redone over Tailscale SSH on
    2026-09-29.

| Check | Day 0 evidence | Day 7 result (collected by 33's Central steps, referenced by 05) |
|-------|----------------|-------------------|
| Authenticated after VM reboot | **pass** — `sudo reboot` 2026-09-30, up 05:44:12 UTC (pending kernel updates applied, `reboot-required` cleared); `claude-remote` active 05:44:21 UTC with 0 restarts and answered the owner through the Max login. First headless run after the reboot: timer at 06:00 UTC | Day 7 (still authenticated on 2026-10-06): carried to 33's Central evidence (owner decision 2026-10-05) — VM-C1 → 0002 §33 VM-C1, 2026-10-07, partial: `claude-remote` active since the 35 Step 16 restart (06:25:16 UTC, 0 unit restarts since) and answering the owner's phone that morning (0002 §35 AC-53 owner 2); the headless `claude -p` of that day: `claude-soak.timer` last ran 18:04:20 UTC and `zyggy-dream` 01:00 UTC, but the exit codes in `soak.jsonl` could not be read from the laptop (the auto-mode classifier refused the read; owner command in 0002 §33); the owner's "no `/login` since day 0" pending |
| Remote control resumed after `systemctl restart claude-remote` (≥ 2 restarts by day 7) | **2 of 2** — (1) after the reboot the log shows `2026-09-30T05:44:21Z resuming <rc-session-id>`; asked from claude.ai "Which word did I ask you to remember?" it answered *pineapple*. (2) 27's deliberate restart `sudo systemctl restart claude-remote` at 2026-09-30 14:05:00 UTC (after the one-token wrapper edit `--name Zyggy`, decision 0002): log `2026-09-30T14:05:00Z resuming <rc-session-id>`, unit active; the *pineapple* question after it is recorded in 0002 AC-5 | Day 0 partial (the ≥ 2 restarts are in; day-7 confirmation open): carried to 33's Central evidence (owner decision 2026-10-05) — VM-C2; 33 Step 21's server restart counts as a further restart → 0002 §33 VM-C2, 2026-10-07, pass: restarts since day 0 — 2026-09-30 05:44:21 and 14:05:00 (left), 2026-10-03 18:05 and 2026-10-04 06:08 and 06:18:51 UTC (0002 §23 AC-25), 2026-10-07 06:25:16 UTC `resuming d2bfd758…` (0002 §35 Switch-on) — ≥ 2, the session in use resumed each time it was checked |
| Headless `claude -p` runs (≥ 3 on day 0, ≥ 7 by day 7, all exit 0) | 2 of 3: 2026-09-29 19:01:01 UTC `exit 0` `OK` (manual start); 2026-09-30 00:05:01 UTC `exit 0` `OK` (timer, `total_cost_usd` 0.0785). Next timer run 06:00 UTC (rescheduled by the reboot) | Day 0 partial (2 of 3): carried to 33's Central evidence (owner decision 2026-10-05) — VM-C3: count the `soak.jsonl` lines with `exit 0` since day 0 (≥ 7); the dream's nightly `claude -p` runs on Central are further evidence → 0002 §33 VM-C3, 2026-10-07, partial: `claude-soak.timer` still runs every 6 h (last 2026-10-07 18:04:20 UTC, next 2026-10-08 00:03:53 UTC: ≥ 30 runs since day 0 by schedule); the `exit` count in `soak.jsonl` could not be read from the laptop (classifier; owner command in 0002 §33); the dream's nightly runs 2026-10-06 and 2026-10-07 each committed (`924275d`, `f556a7b`) |
| Monthly cost forecast ≤ €60 | **pending** — forecast is reliable after 1–2 days. Backup vault `zyggy-backup` LRS, `central` protected with `DefaultPolicy` (first backup `IRPending` on 2026-09-30 05:33 UTC). Budget: not found via `az consumption budget list` at subscription or resource-group scope — owner to confirm where it was created | Pending: carried to 33's Central evidence (owner decision 2026-10-05) — VM-C4: monthly cost forecast ≤ €60 and the location of the budget alert → 0002 §33 VM-C4, 2026-10-07: Cost Management forecast for October 2026 (subscription scope, `az rest`, read-only): actual €12.06 month-to-date, forecast €49.30 ≤ €60 — pass; budget alert: none at resource-group scope (`az consumption budget list -g zyggy-central` empty) — the owner names it, else "fail: no alert" |
| 4 GB RAM sufficient (no OOM kill, swap barely used) | 2026-09-30 05:33 UTC: 753 MB used, 3.1 GB available, swap 0 of 2 GB, 0 kernel OOM lines. After reboot (about 05:46 UTC): 660 MB used, swap 0 | Day 0 pass; day-7 re-reading carried to 33's Central evidence (owner decision 2026-10-05) — VM-C5 (now with the dream, the m365 MCP server and the backfills running) → 0002 §33 VM-C5, 2026-10-07 ~18:2x UTC (after the 2026-10-04 backfills, five dream nights, the m365 server and two brief runs): 1,095 MB used of 3,862, swap 292 KB used of 2 GB, 0 kernel OOM lines since 2026-09-29 — pass |
| Nothing listens on the internet (runbook step 12 #5) | `central-nsg`: 0 custom rules (2026-09-29) | Day 0 pass; day-7 re-check carried to 33's Central evidence (owner decision 2026-10-05) — VM-C6 (also: the m365 MCP server listens on loopback only, 0002 §23 AC-25) → 0002 §33 VM-C6, 2026-10-07: `central-nsg` 0 custom rules; `ss -tlnp`: `127.0.0.1:47365` (`node`, the m365 server) and the resolver on loopback, `sshd` on 22 (reached over Tailscale; no NSG rule opens it), `tailscaled` on the Tailscale address — pass |

Fallback if auth does not survive: `claude setup-token` (one-year token) delivered to the units as
`CLAUDE_CODE_OAUTH_TOKEN` via `LoadCredential=`; if that fails too, §13 Q3 reopens.

## Transport A through the corporate proxy (deliverable 04)

- [x] `api.github.com` polling with a bearer PAT — **Central ok 2026-10-14** / **home laptop ok 2026-10-14** / **work laptop ok 2026-10-13**
- [x] `git fetch` / `pull` over HTTPS — Central n/a (SSH deploy key: `ssh` row ok 2026-10-14) / **home laptop ok 2026-10-14** / **work laptop ok 2026-10-13**
- [x] `git push` over HTTPS with a PAT via credential helper — Central n/a (pushes over SSH with the deploy key, `ssh` row ok 2026-10-14) / **home laptop ok 2026-10-14** / **work laptop ok 2026-10-13** (seed push + dry-run push)
- [x] SSH on port 22 (expected blocked on the work laptop) — **work laptop: blocked (timed out) 2026-10-13**, as expected; **home laptop: reached, auth failed (no key) 2026-10-14**; **Central: ok 2026-10-14 (deploy key)**

### Bus repository (deliverable 04, Step 16)

| Item | Evidence |
|------|----------|
| Repository | `zyggy-org/zyggy-bus`, private, created 2026-10-13 07:06 UTC by the owner |
| Seed | commit `f1088f8` "geoffrey seed", pushed **from the work laptop over HTTPS** through the corporate proxy with the owner's own account (Git Credential Manager) on 2026-10-13 — the first live evidence that the HTTPS push path works there; `git ls-tree -r --name-only main` (19 blobs) equals the `BusPaths` layout for `central`, `home-laptop`, `work-laptop` plus `PROTOCOL.md`, `registry/.gitkeep`, `archive/.gitkeep` and `.gitattributes` (`* -text`, the CRLF measure) — verified from the agent's laptop with the GitHub API |
| Ruleset | **not available**: rulesets (and classic branch protection) on a private repository need GitHub Pro / Team; the API answers `403 Upgrade to GitHub Pro or make this repository public`. Force-push and deletion on `main` are therefore not blocked in P0 — owner decision pending (accept and record, or upgrade the organisation plan); the bus never force-pushes by construction (`GitClient` has no force path, tested) |
| Tokens | Central: `central-bus` (Contents read-only, only `zyggy-bus`) 2026-10-14; work laptop: `zyggy-bus2026101301` (resource owner `zyggy-org`, only `zyggy-bus`, metadata read + Contents read and write, expires 2027-10-14) — an earlier token was pasted on a command line and deleted; home laptop: fine-grained token, resource owner `zyggy-org`, only `zyggy-bus`, Contents read and write, valid 366 days (created 2026-10-14, expires about 2027-10-15); `central-bus` (Contents read-only) pending |
| Deploy key | Central, 2026-10-14, owner-run as `zyggy`: `~/.ssh/zyggy_zyggy-bus_ed25519` (ed25519, comment `zyggy central bus`; home `/srv/agent/home`), added to `zyggy-bus` as deploy key `central` with write access (fingerprint `SHA256:L8Q1cx7qAyg+2frhOeToo6Tqa9pdaeTa1kmMryzdhMA`); `~/.ssh/config` host alias `github.com-zyggy-bus`; `ssh -T git@github.com-zyggy-bus` → "Hi zyggy-org/zyggy-bus! You've successfully authenticated" |

### Reachability matrix (deliverable 04, Step 18, AC-33)

| Machine | api (conditional GET with PAT) | fetch (HTTPS) | push (HTTPS, dry run, PAT via Git Credential Manager) | ssh (22) | proxy | when |
|---------|------|-------|------|-----|-------|------|
| work laptop (inside the corporate network, on a working day; `http.sslBackend schannel`, `credential.helper manager`, `http.proxy` none — **no settings pass needed**) | **ok** (answered) — second run; the first run (~08:30 UTC) was `unreachable (http 404)` because the fine-grained token `zyggy-bus2026101301` had been saved without a repository and without the Contents permission (GitHub drops the repository selection when no permission is set); fixed on the token page, no settings changed on the laptop | **ok** | **ok** (dry-run push authenticated through Git Credential Manager) | unreachable (connection timed out — port 22 blocked, as expected, O19) | api via `<corporate-proxy>:8080` (the system proxy, host redacted here); git rows `none` (git reaches GitHub through the same proxy without an explicit `http.proxy` setting) | 2026-10-13 ~09:00 UTC, second run; exit 0 |
| home laptop (home network, Git Credential Manager) | **ok** (answered) | **ok** | **ok** (dry-run push authenticated through Git Credential Manager) | auth failed (port 22 reached; no SSH key for GitHub on this machine — reachability proven, SSH is not this machine's auth path) | `none` on every row | 2026-10-14, first run; exit 0 |
| Central (Azure VM, unrestricted egress; user `zyggy`; no git `http.proxy`, `http.sslBackend`, `credential.helper`) | **ok** (answered) | auth failed (by design: no HTTPS credential on Central — "could not read Username … terminal prompts disabled") | auth failed (same; Central writes over SSH) | **ok** (deploy key via the `github.com-zyggy-bus` alias — Central's read **and** write path) | `none` on every row | 2026-10-14; exit 6, expected for this machine (the HTTPS rows are not its auth path) |

Facts (ex-OQ6): conditional `304` answers do not count against GitHub's primary rate limit; all personal access tokens of one account share its 5,000 requests per hour.

### Probe series from Central (deliverable 04, Steps 19–20, AC-34/AC-35)

Sender-side round trip (O21: measured on Central's clock only, from the push of the job to the observation of the verified report); poll interval 10 s ± 2 s on both sides; one `zyggy probe send --count 10 --interval 10 --timeout 90` per laptop with a fresh ledger (`--ledger ~/probe-official.jsonl`), 2026-10-15 ~07:00–07:14 UTC, the work laptop inside the corporate network on a working day.

| to | count | done | rejected | timeout | push_failed | p50 ms | p95 ms | min ms | max ms |
|----|------:|-----:|---------:|--------:|------------:|-------:|-------:|-------:|-------:|
| home-laptop | 10 | 10 | 0 | 0 | 0 | 13865 | 24602 | 10868 | 24602 |
| work-laptop | 10 | 10 | 0 | 0 | 0 | 22377 | 24898 | 13473 | 24898 |

Evidence on the bus (`zyggy-org/zyggy-bus`, checked from the agent's laptop with the GitHub API): 60 commits `geoffrey job … central→<laptop>` (author `zyggy (central)`), `geoffrey claim … central→<laptop>` and `geoffrey report … <laptop>→central` (authors `zyggy (home-laptop)` / `zyggy (work-laptop)`), the last at 07:13:34 UTC. The work laptop's claims and reports were pushed over HTTPS through the corporate proxy by the listener — the O19 path under real load. Reading: the round trip is dominated by the two 10 s poll waits (receiver, then sender); the work laptop adds about 8 s at the median for the proxy. The latency target is for 05 to propose (O21).

Before the official series (recorded for the findings, not for the numbers):
- A first attempt on 2026-10-14/15 showed `invalid_signature` on the work laptop: the three machines did not yet hold the same signing key (one was entered on Central after a suspended input, and the owner had been unsure whether a binary checksum had been used as the key). Fixed by one fresh key from `openssl rand -hex 32`, set on all three machines, fingerprints compared. The rejections behaved as specified: the probes moved to `jobs/rejected/` with rejection reports; the sender, which could not verify those reports, recorded `timeout` (finding F3).
- Probes sent from a laptop to itself while its own listener ran in the same checkout produced `pull failed: Cannot rebase onto multiple branches` (two processes running `pull --rebase` at once). Nothing was lost; the next round recovered (finding F2).

### Per-machine auth path actually used (input for 05's §5 table, A7)

| Machine | Polling (API) | git read | git write | SSH 22 |
|---------|---------------|----------|-----------|--------|
| Central | fine-grained PAT, Contents read-only, from the file secret store | SSH, write deploy key `central` via alias `github.com-zyggy-bus` | same deploy key over SSH | ok |
| Home laptop | own fine-grained PAT (Contents read and write) from the file store | HTTPS, Git Credential Manager | HTTPS, same PAT via Git Credential Manager | reached, no key (not used) |
| Work laptop | own fine-grained PAT (Contents read and write) from the file store, through the system proxy with default credentials | HTTPS through the corporate proxy, Git Credential Manager, `http.sslBackend schannel` (already the laptop's setting; no settings pass needed) | HTTPS through the proxy, same PAT via Git Credential Manager | blocked (timed out), as expected |

Fallback line: transport A passed every required check on all three machines, the work laptop included, so candidate C (an HTTPS API served by Central) is **not** needed; 05 records this and does not evaluate C.

### Findings for 05 and later deliverables

- **F1 — Branch protection unavailable**: rulesets and classic branch protection on a private repository need a paid GitHub plan (API `403`). Force-push and deletion on `main` are not blocked; the bus code has no force path (tested). **Owner decision 2026-10-15: risk accepted** ("branch protection: I accept the risk") — mitigations: no force path in the bus code (tested), signatures stop injected jobs, every clone holds the full history to restore `main`; to be revisited before real jobs or work content travel over the bus (option then: GitHub Team for `zyggy-org`). This deviates from the founding spec's §5 "`main` protection" sentence; 05 records it.
- **F2 — One checkout, two writers**: a machine that sends and listens at the same time runs two git writers in one checkout and can collide (`Cannot rebase onto multiple branches`). The Node service (07) must serialise git operations on the checkout; a cross-process lock plus a one-time pull retry is built as patch 0.8.1 (owner decision 2026-10-15: "patch").
- **F3 — Unverifiable report recorded as timeout**: when the receiver's rejection report cannot be verified by the sender (different keys), `probe send` records `timeout`, which hides a key mismatch in the stats. Better: an `unverified_report` outcome. **Fixed 2026-10-24** (bugfix before deliverable 08): `ProbeOutcome.UnverifiedReport` / wire `unverified_report`, recorded as soon as a report in reply to the probe is seen but does not verify (`unknown_key` / `invalid_signature`), its own `probe stats` column.
- **F4 — Windows line endings**: Git for Windows defaults to `core.autocrlf=true`, which rewrites envelope bytes; the bus repository carries `.gitattributes` (`* -text`) and each Windows checkout sets `core.autocrlf false`.
- **F5 — Owner-facing secret entry**: the value-on-the-next-line form of `zyggy secret set` caused three slips (a token on the command line, Ctrl+Z on Linux, a key confused with a checksum). Candidates for the secret-store deliverable (09): `--generate`, a key fingerprint check, and a hidden prompt when stdin is a terminal.

### Release and install (deliverable 04, Step 17)

| Item | Evidence |
|------|----------|
| Code | zyggy PR #15 (`de60bb8`, merged by the owner 2026-10-13); PR CI 38304597405 green (Linux job); tag `v0.8.0` on `de60bb8`; tag CI 38350564512 green on `windows-latest` and `ubuntu-latest` incl. publish + smoke (AC-38) |
| Release | `v0.8.0` with `zyggy` (linux-x64) SHA-256 `9c579b2f285192c8d86d3837adf3b31e4957f05fba3b29506b7dbce9d2dc28ce`, `zyggy.exe` (win-x64) SHA-256 `e7e3a690a0e63eedd0c4c5cb090709c60ccb21adfd00a9cb84c0e438160f4e6f`, `SHA256SUMS`; `sha256sum -c` on the laptop and on the VM |
| Central | 2026-10-13 05:2x UTC via `az vm run-command`: the VM downloaded the public assets itself, `/opt/zyggy/0.8.0/zyggy` root:root 755, `/usr/local/bin/zyggy` → 0.8.0, `zyggy --version` = `0.8.0+de60bb8…`; as `zyggy`: `probe --help` exit 0, `dream status` unchanged (nightly 01:01 UTC committed `8ddd63c`, pushed); `claude-remote` and `zyggy-m365-mcp` active, `NRestarts=0`, no unit restarted; timers unchanged (dream 2026-10-14 01:00 UTC, brief 04:30 UTC) |
| Instance pin | `instance/zyggy.json` → 0.8.0 + the linux-x64 hash: commit `1604971`, PR zyggy-geoffrey#2 opened 2026-10-13, **still open on 2026-10-14**; consequence: the 2026-10-14 01:00 UTC dream refused as designed — unit log `zyggy: configuration error: version_mismatch: version 0.8.0 is not the pinned 0.7.0`, exit 3, no model call, no commit (one night of filing missed). The owner merged PR #2 on 2026-10-14; checkout pulled on Central to `6f725cc` (`instance/zyggy.json` = 0.8.0 + hash `9c579b2f…`); catch-up `zyggy dream request` → 2026-10-14 06:32 UTC on-demand run committed `73f8311` (1 batch, 30 lines, 0 quarantined, 1.02 USD), pushed |
| Laptops | work laptop 2026-10-13: `zyggy --version` = `0.8.0+de60bb8…` (owner-reported); home laptop 2026-10-14: `zyggy --version` = `0.8.0+de60bb8…` (owner-reported) |

### Patch 0.8.1 (deliverable 04, Step 22 — finding F2)

2026-10-15: zyggy PR #16 (`581c0b4`, merged by the owner), PR CI green (Linux, after one test-helper race fixed); tag `v0.8.1`, tag CI 38705382141 green on both runners; release `v0.8.1` — `zyggy` (linux-x64) SHA-256 `49ed1c035de9f916fe180071960fc868a4c37d3e353a0e7bc4147875635da02d`, `zyggy.exe` (win-x64) SHA-256 `fe73c092cfabd1dde9395569e6ae781ac8800860c08c2e95814f976c10985048`. Instance pin PR zyggy-geoffrey#3 merged by the owner **first**; then in one `az vm run-command`: the VM downloaded and verified the asset, `/opt/zyggy/0.8.1/zyggy` root:root 755, checkout pulled to `b9d033b` (pin 0.8.1), symlink switched, `--version` `0.8.1+581c0b4…`, `probe --help` exit 0, `dream status` unchanged, `claude-remote` and `zyggy-m365-mcp` active with `NRestarts=0`, next dream 2026-10-16 01:00 UTC. Both laptops updated by the owner (owner-reported). Change: a cross-process lock file in the checkout's `.git` serialises every pull and write; a pull refused by a concurrent change is retried once; a second `zyggy probe listen` on the same checkout exits 6.

### First nightly dream on the new binary (deliverable 04, Step 21)

2026-10-15 01:01 UTC, Central, `zyggy` 0.8.0 (pin 0.8.0): `nightly committed: batches 1, lines 32, quarantined 0, remaining 0, cost 0.24 USD, commit b1f5c59, pushed yes`; the unit log has no error line; the offload unit ran after it (`Result=success`, 01:01:12 UTC). The additive git-client change runs on the live dream and offload paths without a change in behaviour. (The night before, 2026-10-14, the dream refused with `version_mismatch` because the pin was merged late; caught up the same morning with an on-demand run, `73f8311`.)
