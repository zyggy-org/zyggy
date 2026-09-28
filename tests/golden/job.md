---
agent: env-debugger
allowed_tools: [Read, Grep, Glob, Bash(dotnet *), Bash(kubectl get *)]
attempt: 1
created: 2026-09-27T14:05:00Z
deadline: 2026-09-29T18:00:00Z
from: central
id: 01J8Y3N7Q2X9Z4A5B6C7D8E9F0
key_id: acme/1
priority: normal
project: calizr
report_back: [summary, diff, files_changed]
schema: 1
sig: hmac-sha256:48ea5247d0f3caf56d9a03bd9d4eab8c4783b83beed3fabe0c60c479fbd10e8e
tenant: acme
timeout_minutes: 30
to: home-laptop
type: job
worktree: true
---
Investigate why staging returns HTTP 502 on /api/bookings since Friday.
Do not change code; report root cause and a proposed fix.
