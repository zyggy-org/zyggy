# 0001 — Transport A and Central VM

Status: open. VM soak section (deliverable 02) closed 2026-10-05 by owner decision — unfinished checks carried to
33's Central evidence (plan 33 Steps 21–22); 05 references that evidence and no longer collects the day-7 result
itself. Transport section open; the record is closed by deliverable 05.

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
| Remote control resumed after `systemctl restart claude-remote` (≥ 2 restarts by day 7) | **2 of 2** — (1) after the reboot the log shows `2026-09-30T05:44:21Z resuming 6ba6d03b-203e-4a60-9df5-f31ce469f875`; asked from claude.ai "Which word did I ask you to remember?" it answered *pineapple*. (2) 27's deliberate restart `sudo systemctl restart claude-remote` at 2026-09-30 14:05:00 UTC (after the one-token wrapper edit `--name Zyggy`, decision 0002): log `2026-09-30T14:05:00Z resuming 6ba6d03b-203e-4a60-9df5-f31ce469f875`, unit active; the *pineapple* question after it is recorded in 0002 AC-5 | Day 0 partial (the ≥ 2 restarts are in; day-7 confirmation open): carried to 33's Central evidence (owner decision 2026-10-05) — VM-C2; 33 Step 21's server restart counts as a further restart → 0002 §33 VM-C2, 2026-10-07, pass: restarts since day 0 — 2026-09-30 05:44:21 and 14:05:00 (left), 2026-10-03 18:05 and 2026-10-04 06:08 and 06:18:51 UTC (0002 §23 AC-25), 2026-10-07 06:25:16 UTC `resuming d2bfd758…` (0002 §35 Switch-on) — ≥ 2, the session in use resumed each time it was checked |
| Headless `claude -p` runs (≥ 3 on day 0, ≥ 7 by day 7, all exit 0) | 2 of 3: 2026-09-29 19:01:01 UTC `exit 0` `OK` (manual start); 2026-09-30 00:05:01 UTC `exit 0` `OK` (timer, `total_cost_usd` 0.0785). Next timer run 06:00 UTC (rescheduled by the reboot) | Day 0 partial (2 of 3): carried to 33's Central evidence (owner decision 2026-10-05) — VM-C3: count the `soak.jsonl` lines with `exit 0` since day 0 (≥ 7); the dream's nightly `claude -p` runs on Central are further evidence → 0002 §33 VM-C3, 2026-10-07, partial: `claude-soak.timer` still runs every 6 h (last 2026-10-07 18:04:20 UTC, next 2026-10-08 00:03:53 UTC: ≥ 30 runs since day 0 by schedule); the `exit` count in `soak.jsonl` could not be read from the laptop (classifier; owner command in 0002 §33); the dream's nightly runs 2026-10-06 and 2026-10-07 each committed (`924275d`, `f556a7b`) |
| Monthly cost forecast ≤ €60 | **pending** — forecast is reliable after 1–2 days. Backup vault `zyggy-backup` LRS, `central` protected with `DefaultPolicy` (first backup `IRPending` on 2026-09-30 05:33 UTC). Budget: not found via `az consumption budget list` at subscription or resource-group scope — owner to confirm where it was created | Pending: carried to 33's Central evidence (owner decision 2026-10-05) — VM-C4: monthly cost forecast ≤ €60 and the location of the budget alert → 0002 §33 VM-C4, 2026-10-07: Cost Management forecast for October 2026 (subscription scope, `az rest`, read-only): actual €12.06 month-to-date, forecast €49.30 ≤ €60 — pass; budget alert: none at resource-group scope (`az consumption budget list -g zyggy-central` empty) — the owner names it, else "fail: no alert" |
| 4 GB RAM sufficient (no OOM kill, swap barely used) | 2026-09-30 05:33 UTC: 753 MB used, 3.1 GB available, swap 0 of 2 GB, 0 kernel OOM lines. After reboot (about 05:46 UTC): 660 MB used, swap 0 | Day 0 pass; day-7 re-reading carried to 33's Central evidence (owner decision 2026-10-05) — VM-C5 (now with the dream, the m365 MCP server and the backfills running) → 0002 §33 VM-C5, 2026-10-07 ~18:2x UTC (after the 2026-10-04 backfills, five dream nights, the m365 server and two brief runs): 1,095 MB used of 3,862, swap 292 KB used of 2 GB, 0 kernel OOM lines since 2026-09-29 — pass |
| Nothing listens on the internet (runbook step 12 #5) | `central-nsg`: 0 custom rules (2026-09-29) | Day 0 pass; day-7 re-check carried to 33's Central evidence (owner decision 2026-10-05) — VM-C6 (also: the m365 MCP server listens on loopback only, 0002 §23 AC-25) → 0002 §33 VM-C6, 2026-10-07: `central-nsg` 0 custom rules; `ss -tlnp`: `127.0.0.1:47365` (`node`, the m365 server) and the resolver on loopback, `sshd` on 22 (reached over Tailscale; no NSG rule opens it), `tailscaled` on the Tailscale address — pass |

Fallback if auth does not survive: `claude setup-token` (one-year token) delivered to the units as
`CLAUDE_CODE_OAUTH_TOKEN` via `LoadCredential=`; if that fails too, §13 Q3 reopens.

## Transport A through the corporate proxy (deliverable 04)

- [ ] `api.github.com` polling with a bearer PAT — Central / home laptop / work laptop
- [ ] `git fetch` / `pull` over HTTPS — Central / home laptop / work laptop
- [ ] `git push` over HTTPS with a PAT via credential helper — Central / home laptop / work laptop
- [ ] SSH on port 22 (expected blocked on the work laptop)
