# Steps 9-10 of runbooks/central-vm-setup.md in one script. Run as root with sh (az vm run-command, or: sudo sh <file>).
set -eux
# Precondition from step 8: a Central session exists to resume
ls -t /srv/agent/home/.claude/projects/-srv-agent-central/*.jsonl | head -n 1 || echo "WARNING: no Central session yet - the wrapper will start a fresh one"

# Step 9: Q4 resume wrapper + claude-remote.service
mkdir -p /srv/agent/bin
cat > /srv/agent/bin/claude-remote.sh <<'EOF'
#!/usr/bin/env bash
# §13 Q4: resume the newest Central session with remote control; fall back to a fresh session.
# Claude must own the tmux terminal: piping its output (e.g. to tee) makes it switch to --print mode and exit.
# Only the wrapper's own lines go to the log.
set -u
claude="$HOME/.local/bin/claude"
log=/srv/agent/central/claude-remote.log
note() { echo "$(date -u +%FT%TZ) $*" | tee -a "$log"; }
cd /srv/agent/central
sessions="$HOME/.claude/projects/$(pwd | sed 's/[^a-zA-Z0-9]/-/g')"
newest=$(ls -t "$sessions"/*.jsonl 2>/dev/null | head -n 1)
if [ -n "$newest" ]; then
  id=$(basename "$newest" .jsonl)
  note "resuming $id"
  "$claude" --resume "$id" --remote-control --name Zyggy --permission-mode auto
  rc=$?
  [ "$rc" -eq 0 ] && exit 0
  note "resume of $id failed (exit $rc); starting a fresh session"
fi
note "starting a fresh session"
exec "$claude" --remote-control --name Zyggy --permission-mode auto
EOF
cat > /etc/systemd/system/claude-remote.service <<'EOF'
[Unit]
Description=Claude Code remote control for Central (§10, §13 Q4)
After=network-online.target tailscaled.service
Wants=network-online.target

[Service]
Type=forking
User=zyggy
WorkingDirectory=/srv/agent/central
ExecStart=/usr/bin/tmux -L claude-remote new-session -d -s claude-remote /srv/agent/bin/claude-remote.sh
ExecStop=/usr/bin/tmux -L claude-remote kill-session -t claude-remote
Restart=always
RestartSec=15

[Install]
WantedBy=multi-user.target
EOF

# Step 10: headless soak run every 6 hours
cat > /srv/agent/bin/claude-soak.sh <<'EOF'
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
cat > /etc/systemd/system/claude-soak.service <<'EOF'
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
cat > /etc/systemd/system/claude-soak.timer <<'EOF'
[Unit]
Description=P0 soak every 6 hours

[Timer]
OnCalendar=*-*-* 00/6:00:00
RandomizedDelaySec=5min
Persistent=true

[Install]
WantedBy=timers.target
EOF
chmod 755 /srv/agent/bin/claude-remote.sh /srv/agent/bin/claude-soak.sh

systemctl daemon-reload
systemctl enable --now claude-remote.service
systemctl enable --now claude-soak.timer
systemctl start claude-soak.service || echo "soak run failed - see soak.jsonl"
sleep 5
systemctl --no-pager --lines=0 status claude-remote.service claude-soak.timer || true
tail -n 5 /srv/agent/central/claude-remote.log || true
tail -n 1 /srv/agent/soak/soak.jsonl | jq -c '{ts, exit, is_error: (.result.is_error? // null), result: (.result.result? // .result)}'
