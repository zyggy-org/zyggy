# 0001 — Transport A and Central VM

Status: open (P0 soak running). Closed by deliverable 05.

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

| Check | Day 0 evidence | Day 7 result (05) |
|-------|----------------|-------------------|
| Authenticated after VM reboot | **pass** — `sudo reboot` 2026-09-30, up 05:44:12 UTC (pending kernel updates applied, `reboot-required` cleared); `claude-remote` active 05:44:21 UTC with 0 restarts and answered the owner through the Max login. First headless run after the reboot: timer at 06:00 UTC | |
| Remote control resumed after `systemctl restart claude-remote` (≥ 2 restarts by day 7) | **1 of 2** — after the reboot the log shows `2026-09-30T05:44:21Z resuming 6ba6d03b-203e-4a60-9df5-f31ce469f875`; asked from claude.ai "Which word did I ask you to remember?" it answered *pineapple*. One more deliberate restart + question needed by day 7 | |
| Headless `claude -p` runs (≥ 3 on day 0, ≥ 7 by day 7, all exit 0) | 2 of 3: 2026-09-29 19:01:01 UTC `exit 0` `OK` (manual start); 2026-09-30 00:05:01 UTC `exit 0` `OK` (timer, `total_cost_usd` 0.0785). Next timer run 06:00 UTC (rescheduled by the reboot) | |
| Monthly cost forecast ≤ €60 | **pending** — forecast is reliable after 1–2 days. Backup vault `zyggy-backup` LRS, `central` protected with `DefaultPolicy` (first backup `IRPending` on 2026-09-30 05:33 UTC). Budget: not found via `az consumption budget list` at subscription or resource-group scope — owner to confirm where it was created | |
| 4 GB RAM sufficient (no OOM kill, swap barely used) | 2026-09-30 05:33 UTC: 753 MB used, 3.1 GB available, swap 0 of 2 GB, 0 kernel OOM lines. After reboot (about 05:46 UTC): 660 MB used, swap 0 | |
| Nothing listens on the internet (runbook step 12 #5) | `central-nsg`: 0 custom rules (2026-09-29) | |

Fallback if auth does not survive: `claude setup-token` (one-year token) delivered to the units as
`CLAUDE_CODE_OAUTH_TOKEN` via `LoadCredential=`; if that fails too, §13 Q3 reopens.

## Transport A through the corporate proxy (deliverable 04)

- [ ] `api.github.com` polling with a bearer PAT — Central / home laptop / work laptop
- [ ] `git fetch` / `pull` over HTTPS — Central / home laptop / work laptop
- [ ] `git push` over HTTPS with a PAT via credential helper — Central / home laptop / work laptop
- [ ] SSH on port 22 (expected blocked on the work laptop)
