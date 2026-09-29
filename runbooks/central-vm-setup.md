# Runbook — Central VM setup and 7-day soak (deliverable 02)

Builds the Central VM from founding spec §10 / §13 Q3–Q4 / §8 and starts the P0 soak. Owner-executed; no Zyggy
binaries are involved (`zyggy-node.service` arrives in deliverable 10). When `agent-core` exists this file moves to
`agent-core/runbooks/`; `restore-central.md` (§11) is drafted at the end.

Commands marked **[laptop]** run in PowerShell on your laptop with the Azure CLI (`az`); **[vm/root]** run on the VM
as your admin user with `sudo`; **[vm/zyggy]** run as the unprivileged `zyggy` user (`sudo -iu zyggy`).

## Execution status (update as you go)

Last update 2026-09-29, work laptop (Azure Cloud Shell, PowerShell, personal Microsoft account, subscription
*Abonnement Visual Studio Enterprise*, spending limit on, ~€88 credit left this period).

| Step | State | Notes |
|------|-------|-------|
| 1 Tailscale key | done | tailnet of account **`geobarteam@`**; the one-off key is consumed |
| 2 VM | done | rg `zyggy-central`, `westeurope`, `Standard_B2als_v2`, TrustedLaunch, 2 × 32 GB Premium_LRS, NSG `central-nsg` with **no** rules (the auto-added `default-allow-ssh` was deleted), public IP 52.137.8.215 (outbound only) |
| 3 Tailscale | done | `central` = **100.80.12.47**, `--ssh` on. The home laptop is **not yet** in the tailnet |
| 4–6 disk, packages, user | done | run from Cloud Shell via `az vm run-command` (a script runs under `sh`: `set -eux`, no `pipefail`) |
| 7–8 Claude install, login, first run | done (owner-reported) | done in the Azure **Serial Console**; `azureadmin` now has a console-only password (portal → Reset password), kept in the owner's password manager |
| 9–10 services + soak timer | **next** | `runbooks/central-vm-steps9-10.sh` bundles both; not run yet |
| 11 backup + budget | todo | vault LRS before the first backup; budget €60 |
| 12 day-0 checks | todo | |
| 13 decision record, day 0 | todo | starts the 7-day clock |

**Resume on the home laptop:**

1. Install Tailscale, sign in with the `geobarteam` account; `tailscale status` lists `central`.
2. `ssh azureadmin@central` (or `ssh azureadmin@100.80.12.47`); confirm step 8 left a session to resume:
   `sudo ls -t /srv/agent/home/.claude/projects/-srv-agent-central/`.
3. Steps 9–10: copy the script over and run it as root —
   `scp runbooks/central-vm-steps9-10.sh azureadmin@central:/tmp/ && ssh azureadmin@central 'sudo sh /tmp/central-vm-steps9-10.sh'`
   (or follow sections 9–10 below by hand). Expect `claude-remote` active, the log showing `resuming <id>`, the soak
   line with `"exit":0`. If Claude rejects `--name` together with `--remote-control`, drop `--name central` from the
   wrapper (`/srv/agent/bin/claude-remote.sh`) and record it.
4. Then steps 11–13 below. Update this table as you go.

## 0. Before you start

- Azure subscription with the monthly credit, `az login` done on the laptop. **Cost target: under €60/month** (€80
  available). See "Cost" below.
- A Tailscale account (free personal plan) with Tailscale installed on the home laptop.
- The Claude Max account you want Central to use.
- About 1½ hours. Decide the region once (below: `westeurope`) and keep it for backup and disks.

### Cost

West Europe retail prices (Azure Retail Prices API, pay-as-you-go, 730 h/month, checked 2026-09-29):

| Item | Price | Per month |
|------|-------|-----------|
| VM **Standard_B2als_v2** (2 vCPU, 4 GB), Linux | €0.0371/h | €27.08 |
| 2 × Premium SSD P4 (32 GB): OS + `/srv/agent` | €4.99 each | €9.97 |
| Standard static public IPv4 (outbound only) | €0.0043/h | €3.14 |
| Azure Backup, VM protected instance (> 50 GB of disks) | €8.59 | €8.59 |
| Backup storage, LRS, ~10–20 GB used + instant-restore snapshots | €0.0192/GB | ~€1–2 |
| **Total** | | **≈ €50** |

**Deviation from §13 Q3.** The spec decided on Standard_B2as_v2 (8 GB). It costs €0.0742/h = €54.17/month for the
VM alone, ≈ €77 all in — over the €60 target. B2als_v2 has the same 2 vCPUs with 4 GB; the soak workload (one
remote-control session, a `claude -p` every 6 h) fits, and a 2 GB swap file (step 5) covers peaks. The soak also
records memory use (step 12), so the choice is measured, not assumed. Record the deviation in the decision record;
the spec itself is yours to amend. If 4 GB turns out too small, `az vm resize --size Standard_B2as_v2` is a reboot
away (then ≈ €77).

## 1. Tailscale auth key [browser]

1. Tailscale admin console → **Settings → Keys → Generate auth key**.
2. One-off: not reusable, not ephemeral, pre-approved, expiry 1 day (the minimum), no tags. Copy it; it is used once in step 3 and
   then expires. Never commit it.
3. **Access controls**: make sure Tailscale SSH is allowed for your user to `central` (the default policy allows
   your own devices; if you edited the ACL, add an `ssh` rule for `autogroup:member` → `autogroup:self`).

## 2. Create the VM [laptop]

No inbound port is opened (§8). The VM gets a Standard public IP **only for outbound traffic**: new Azure virtual
networks no longer have default outbound access, and a NAT gateway would cost more than the VM. The NSG has no
inbound allow rule, so nothing on the internet can reach it; you log in through Tailscale.

```powershell
$rg = "zyggy-central"; $loc = "westeurope"; $vm = "central"

az group create -n $rg -l $loc

az network nsg create -g $rg -n "$vm-nsg"            # default rules only: all inbound from Internet denied

az vm create -g $rg -n $vm `
  --image Canonical:ubuntu-24_04-lts:server:latest `
  --size Standard_B2als_v2 `
  --admin-username azureadmin --generate-ssh-keys `
  --os-disk-size-gb 32 --storage-sku Premium_LRS `
  --data-disk-sizes-gb 32 `
  --public-ip-sku Standard --nsg "$vm-nsg" --nsg-rule NONE `
  --security-type TrustedLaunch
```

`--nsg-rule NONE` matters: without it `az vm create` adds `default-allow-ssh` (port 22 from the internet) even to an
existing NSG. Check every argument landed (Azure spells the property `diskSizeGB`):

```powershell
az vm show -g $rg -n $vm --query "{size:hardwareProfile.vmSize, security:securityProfile.securityType}" -o table
az disk list -g $rg --query "[].{name:name, gb:diskSizeGB, sku:sku.name}" -o table          # 2 disks, 32 GB, Premium_LRS
az network nsg rule list -g $rg --nsg-name "$vm-nsg" -o table                               # must print nothing
```

If the NSG shows `default-allow-ssh`: `az network nsg rule delete -g $rg --nsg-name "$vm-nsg" -n default-allow-ssh`.

Disk layout: the §13 Q3 64 GB Premium SSD is split into a 32 GB OS disk and a 32 GB **data disk for `/srv/agent`**,
so the persistent volume (memory, bus checkout, `~/.claude`) can be snapshotted and re-attached to a fresh VM on its
own (`restore-central.md`). If you prefer one 64 GB disk, drop `--data-disk-sizes-gb` and skip step 4.

## 3. Join Tailscale without opening SSH [laptop]

`az vm run-command` executes as root through the Azure agent, so port 22 never needs to be open:

```powershell
$key = Read-Host "Tailscale auth key"      # the one-off key from step 1
az vm run-command invoke -g $rg -n $vm --command-id RunShellScript --scripts `
  "curl -fsSL https://tailscale.com/install.sh | sh && tailscale up --auth-key=$key --ssh --hostname=central"
```

Check: `tailscale status` on the laptop lists `central`; then `ssh azureadmin@central` works (Tailscale SSH).
From here on everything runs over that SSH session.

## 4. Mount the data disk at `/srv/agent` [vm/root]

```bash
lsblk -o NAME,SIZE,TYPE,MOUNTPOINT          # the data disk is the 32G disk with no partitions
DISK=$(readlink -f /dev/disk/azure/scsi1/lun0) && echo $DISK   # Azure's stable name for data disk LUN 0
sudo parted -s $DISK mklabel gpt mkpart agent ext4 0% 100%
sleep 2 && sudo mkfs.ext4 -L agent ${DISK}1
sudo mkdir -p /srv/agent
echo 'LABEL=agent /srv/agent ext4 defaults,nofail 0 2' | sudo tee -a /etc/fstab
sudo mount -a && df -h /srv/agent
```

## 5. Base packages, Node 22, unattended upgrades [vm/root]

```bash
sudo apt-get update && sudo apt-get -y upgrade
sudo apt-get -y install git tmux jq unattended-upgrades
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt-get -y install nodejs && node --version      # v22.x

# 2 GB swap: the VM has 4 GB of RAM (see "Cost"); swap absorbs peaks instead of the OOM killer
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
free -h
```

Unattended security upgrades are on by default on Ubuntu 24.04; leave automatic reboot **off** so reboots happen
when you choose (the soak includes one deliberate reboot).

## 6. Unprivileged `zyggy` user [vm/root]

The user's home is on the persistent volume, so `~/.claude` (the OAuth token and sessions) survives a VM rebuild.
No sudo, no cloud CLI, no kubeconfig (§8 Isolation).

```bash
sudo useradd --create-home --home-dir /srv/agent/home --shell /bin/bash zyggy
sudo mkdir -p /srv/agent/central /srv/agent/soak
sudo chown -R zyggy:zyggy /srv/agent
sudo chmod 700 /srv/agent /srv/agent/home
```

## 7. Install Claude Code and log in [vm/zyggy]

The native installer needs no Node and installs per user into `~/.local/bin` (that is,
`/srv/agent/home/.local/bin/claude`, on the persistent volume). Node 22 from step 5 is there for the MCP servers
Central gets later (§3).

```bash
sudo -iu zyggy
curl -fsSL https://claude.ai/install.sh | bash
~/.local/bin/claude --version
~/.local/bin/claude          # first start: choose the claude.ai (subscription) login, sign in with the Max account
```

Over SSH the browser callback cannot reach the VM: press `c` to copy the login URL, open it on the laptop, and paste
the code Claude shows back into the terminal. Then type `/status`, check the account is the Max subscription, and
`/exit`.

This is the §10 "one interactive `claude login`": the OAuth credentials now live under `/srv/agent/home/.claude/`.
**Do not use `claude setup-token` yet.** It creates a one-year token for `CLAUDE_CODE_OAUTH_TOKEN`, which would hide
exactly what the soak measures (does the normal login stay valid unattended?). It is the recorded fallback if the
soak fails on auth (step 13).

## 8. First interactive run in the Central directory [vm/zyggy]

Remote control and the trust / auto-mode prompts must be accepted once by hand in `/srv/agent/central`, otherwise the
service in step 9 would hang on a prompt nobody sees.

```bash
cd /srv/agent/central
git init -q .                                     # the memory repo arrives later; a repo keeps Claude's git tools happy
~/.local/bin/claude --help | grep -iE -A2 'remote-control|--name|--resume|permission-mode'
tmux new -s first
~/.local/bin/claude --remote-control --name central --permission-mode auto
```

- Check the `--help` output first: if `--name` is not accepted together with `--remote-control` in the installed
  version, drop it here and in the wrapper (the session then gets a default name) and note it in the decision record.
- Accept the trust prompt for `/srv/agent/central` and any auto-mode confirmation.
- On your phone or at claude.ai/code the session `central` appears. Send it one message, e.g. "Remember the word
  *pineapple*." — this is the conversation the resume test uses.
- `/exit`, then `exit` tmux. Check a session file exists:
  `ls -t ~/.claude/projects/-srv-agent-central/*.jsonl | head -1`

## 9. `claude-remote.service` with the Q4 resume wrapper [vm/root]

Wrapper (§13 Q4): resume the newest Central session with remote control; if there is none or the resume fails, start
a fresh one.

```bash
sudo mkdir -p /srv/agent/bin
sudo tee /srv/agent/bin/claude-remote.sh >/dev/null <<'EOF'
#!/usr/bin/env bash
# §13 Q4: resume the newest Central session with remote control; fall back to a fresh session.
set -u
claude="$HOME/.local/bin/claude"
cd /srv/agent/central
sessions="$HOME/.claude/projects/$(pwd | sed 's/[^a-zA-Z0-9]/-/g')"
newest=$(ls -t "$sessions"/*.jsonl 2>/dev/null | head -n 1)
if [ -n "$newest" ]; then
  id=$(basename "$newest" .jsonl)
  echo "$(date -u +%FT%TZ) resuming $id"
  "$claude" --resume "$id" --remote-control --name central --permission-mode auto && exit 0
  echo "$(date -u +%FT%TZ) resume of $id failed (exit $?); starting a fresh session"
fi
exec "$claude" --remote-control --name central --permission-mode auto
EOF
sudo chmod 755 /srv/agent/bin/claude-remote.sh
```

The resumed session is interactive, so the unit runs it inside a detached `tmux` session (§10 allows either). When
Claude exits, the tmux session and server end, and `Restart=always` starts it again.

```bash
sudo tee /etc/systemd/system/claude-remote.service >/dev/null <<'EOF'
[Unit]
Description=Claude Code remote control for Central (§10, §13 Q4)
After=network-online.target tailscaled.service
Wants=network-online.target

[Service]
Type=forking
User=zyggy
WorkingDirectory=/srv/agent/central
ExecStart=/usr/bin/tmux new-session -d -s claude-remote '/srv/agent/bin/claude-remote.sh 2>&1 | tee -a /srv/agent/central/claude-remote.log'
ExecStop=/usr/bin/tmux kill-session -t claude-remote
Restart=always
RestartSec=15

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable --now claude-remote
systemctl status claude-remote --no-pager
```

Watch it live: `sudo -iu zyggy tmux attach -t claude-remote` (detach with `Ctrl-b d`, never `/exit` — that ends the
session and the service restarts it).

## 10. `claude-soak.timer` — headless `claude -p` every 6 hours [vm/root]

Runs in its own directory so its sessions never become "the newest Central session" for the wrapper. One line of
JSON per run in `/srv/agent/soak/soak.jsonl`. This is the mechanism deliverable 13 reuses for the dream job.

```bash
sudo tee /srv/agent/bin/claude-soak.sh >/dev/null <<'EOF'
#!/usr/bin/env bash
# One headless run for the P0 soak; appends {ts, exit, result} to soak.jsonl.
set -u
cd /srv/agent/soak
ts=$(date -u +%FT%TZ)
out=$(timeout 300 "$HOME/.local/bin/claude" -p "Reply with exactly the word OK." \
      --output-format json --max-turns 1 --permission-mode auto 2>&1)
rc=$?
if jq -e . >/dev/null 2>&1 <<<"$out"; then result=$out; else result=$(jq -Rn --arg s "$out" '$s'); fi
jq -cn --arg ts "$ts" --argjson exit "$rc" --argjson result "$result" '{ts:$ts, exit:$exit, result:$result}' >> soak.jsonl
exit "$rc"
EOF
sudo chmod 755 /srv/agent/bin/claude-soak.sh

sudo tee /etc/systemd/system/claude-soak.service >/dev/null <<'EOF'
[Unit]
Description=P0 soak: one headless claude -p run
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
User=zyggy
WorkingDirectory=/srv/agent/soak
ExecStart=/srv/agent/bin/claude-soak.sh
EOF

sudo tee /etc/systemd/system/claude-soak.timer >/dev/null <<'EOF'
[Unit]
Description=P0 soak every 6 hours

[Timer]
OnCalendar=*-*-* 00/6:00:00
RandomizedDelaySec=5min
Persistent=true

[Install]
WantedBy=timers.target
EOF
sudo systemctl daemon-reload
sudo systemctl enable --now claude-soak.timer
sudo systemctl start claude-soak.service            # one run now
tail -n 1 /srv/agent/soak/soak.jsonl | jq '{ts, exit, is_error: (.result.is_error? // null), result: (.result.result? // .result)}'
```

Summary at any time: `jq -r '[.ts, .exit, (.result.is_error? // "n/a")] | @tsv' /srv/agent/soak/soak.jsonl`.
The exact fields of `--output-format json` are not fully documented; the script keeps the whole object, so nothing is
lost if they differ.

## 11. Azure Backup (daily) and budget alert [laptop / portal]

```powershell
az backup vault create -g $rg -n zyggy-backup -l $loc
# LRS storage halves the backup storage price versus the GRS default; must be set before the first backup
az backup vault backup-properties set -g $rg -n zyggy-backup --backup-storage-redundancy LocallyRedundant
az backup protection enable-for-vm -g $rg --vault-name zyggy-backup --vm $vm --policy-name DefaultPolicy
```

`DefaultPolicy` = one backup a day, 30-day retention; it covers the OS and the `/srv/agent` data disk.

Budget (portal is simplest): **Cost Management → Budgets → Add**, scope = resource group `zyggy-central`, monthly,
amount **€60** (the target; €80 is what is available). Alerts: 80 % actual (€48), 100 % forecasted (€60), to your
e-mail.

Compare the forecast after 1–2 days with the ≈ €50 in "Cost" (step 0). If it runs above €60, the levers in order:
data disk to Standard SSD E4 (−€3), backup retention 30 → 7 days (a few €), OS disk to Standard SSD E4 (−€3).
Record the number either way.

## 12. Day-0 checks — the definition of done for 02

Do these in order and paste the evidence into the decision record (step 13).

| # | Check | How | Pass when |
|---|-------|-----|-----------|
| 1 | Auth survives a reboot | `sudo reboot`; after ~2 min `ssh azureadmin@central`, then `systemctl status claude-remote claude-soak.timer --no-pager` and `sudo systemctl start claude-soak.service` | both units active; the new soak line has `exit 0` |
| 2 | Remote control resumes after a restart (Q4) | `sudo systemctl restart claude-remote`; open `central` on phone / claude.ai; ask "Which word did I ask you to remember?" | it answers *pineapple*; `claude-remote.log` shows `resuming <id>` |
| 3 | Three consecutive headless runs | `sudo systemctl start claude-soak.service` three times (or wait for the timer) | the last three lines of `soak.jsonl` have `exit 0` |
| 4 | Cost within budget | portal → Cost analysis → forecast for the month (reliable after 1–2 days) | forecast ≤ €60 |
| 5 | Nothing listens on the internet | `az network nsg rule list -g $rg --nsg-name central-nsg -o table` | no custom inbound rule |
| 6 | 4 GB is enough | during a soak run: `free -h`; afterwards `journalctl -k \| grep -i 'out of memory'` | swap barely used; no OOM kill |

## 13. Open the decision record and start the soak clock

Create `_plans/decisions/0001-transport-and-vm.md` in the repo (05 completes it; 04 fills the transport part):

```markdown
# 0001 — Transport A and Central VM

Status: open (P0 soak running). Closed by deliverable 05.

## VM soak (deliverable 02)

- Day 0: <YYYY-MM-DD HH:MM UTC>  ·  Claude Code version: <claude --version>  ·  VM: Standard_B2als_v2, westeurope
- Deviations: VM size B2als_v2 (4 GB) instead of §13 Q3's B2as_v2 (8 GB) to stay under €60/month (owner,
  2026-09-29); <others, e.g. --name not accepted with --remote-control>

| Check | Day 0 evidence | Day 7 result (05) |
|-------|----------------|-------------------|
| Authenticated after VM reboot | | |
| Remote control resumed after `systemctl restart claude-remote` (≥ 2 restarts by day 7) | | |
| Headless `claude -p` runs (≥ 3 on day 0, ≥ 7 by day 7, all exit 0) | | |
| Monthly cost forecast ≤ €60 | | |
| 4 GB RAM sufficient (no OOM kill, swap barely used) | | |

Fallback if auth does not survive: `claude setup-token` (one-year token) delivered to the units as
`CLAUDE_CODE_OAUTH_TOKEN` via `LoadCredential=`; if that fails too, §13 Q3 reopens.

## Transport A through the corporate proxy (deliverable 04)

- [ ] `api.github.com` polling with a bearer PAT — Central / home laptop / work laptop
- [ ] `git fetch` / `pull` over HTTPS — Central / home laptop / work laptop
- [ ] `git push` over HTTPS with a PAT via credential helper — Central / home laptop / work laptop
- [ ] SSH on port 22 (expected blocked on the work laptop)
```

During the 7 days: do at least one more `systemctl restart claude-remote` + resume test (05 needs ≥ 2), and glance at
`soak.jsonl` daily. Any `exit` ≠ 0 or an auth error is the evidence the soak exists to find — record it, do not fix it
silently.

## Draft: `restore-central.md` (§11)

1. Restore from Azure Backup (portal → vault `zyggy-backup` → the VM → **Restore VM** → create new) — or create a
   fresh VM with steps 2–3 and attach a disk restored from the latest `/srv/agent` recovery point.
2. Steps 4 (mount only: the fstab line, `mount -a`), 5 and 9–10 on the new VM; the `zyggy` user must get the same
   UID so the files on `/srv/agent` stay its own (`sudo useradd --uid <old uid> ...`).
3. `sudo -iu zyggy ~/.local/bin/claude` → `/status`: still logged in? If not, log in again (step 7).
4. `systemctl status claude-remote claude-soak.timer`; run check 2 from step 12.
5. From deliverable 10 on: `zyggy verify` for every tenant present, then start `zyggy-node.service`.
