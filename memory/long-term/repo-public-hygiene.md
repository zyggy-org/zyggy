---
name: repo-public-hygiene
description: zyggy and zyggy-core are public repos — never write real identifiers, secrets or operational runbooks into them.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
`zyggy` and `zyggy-core` are PUBLIC GitHub repos; `zyggy-geoffrey` (the instance repo, under `instance/`) is PRIVATE and is where real identifiers and operational docs belong. Never write a real tenant/app/object/site id, subscription, VM admin username, hostname, IP, cert thumbprint, mailbox, or private-repo name into `zyggy`/`zyggy-core` — use placeholders (`<company-tenant-id>`, `<vm-admin>`, …). VM/operational runbooks live at `zyggy-geoffrey` `instance/runbooks/`, not in `zyggy`'s own `runbooks/` (moved there 2026-10-08 for this reason; see also the "where do instance docs live" note in CLAUDE.md). CI secrets carrying owner-specific values must be GitHub `secrets.*` (masked), never `vars.*` (printed in step logs). Also never name the owner's employer anywhere in either public repo (code, prompts, fixtures, commits) — write "the employer" / "the work laptop"; fixture slugs use `work-*`.

**Why:** a real clone/backfill run once put a Groq key and an Azure Storage key into a transcript via an ordinary doc, and an early pass wrote the employer's name across docs/tests/commits; both needed a redaction sweep afterward.
**How to apply:** before writing or editing any file in `zyggy` or `zyggy-core` (docs, fixtures, runbook entries, commit messages), check it contains only placeholders.
