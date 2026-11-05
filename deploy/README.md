# deploy/ — what a release ships besides the two binaries (deliverable 10)

| File | Purpose |
|------|---------|
| `systemd/zyggy-node.service` | Central's unit: `Type=notify`, user `zyggy`, restart on failure only (exits 3 and 6 stay failed for a human), no machine path, no credential line. |
| `systemd/zyggy-node.service.d/credentials.conf.example` | Optional drop-in: feed the bus secrets to the node through systemd's credentials directory (`LoadCredential=`); the 0600 file layer stays the default. |
| `systemd/zyggy-node.service.d/pickup.conf.example` | **Required on Central** from deliverable 13: the memory principal and secret patterns the node's pickup needs (`ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE`, `ZYGGY_SECRET_PATTERNS`; `<instance-dir>` replaced on install). Without it the node on Central exits 3 with `configuration error: pickup: <detail>`; laptops ignore it. From deliverable 41 it also carries `Environment=ZYGGY_INSTANCE_DIR=<instance-dir>/instance` (where the pickup reads `archive.json` and `m365.json`) and `LoadCredential=m365-app-key:<zyggy-home>/.config/zyggy/m365-app.key` (the Graph application key, so the pickup can download OneDrive-route archive items read-only). Without those two lines the node still starts, but archive items from the laptops wait on the bus (one `[ERR]` line in the log). |
| `windows/install.ps1` | The laptops' installer: per user, no administrator rights, registers the `zyggy-node` logon task. The one shell script of the product (rule R1: it runs before any binary is trusted; its invariants are tested statically). From deliverable 42 it also takes an older `zyggy` folder off the user PATH (only when it holds nothing but `zyggy*` files; nothing is deleted), stops an older node running from such a folder, puts the programs folder first in its own session's PATH, and calls `zyggy setup --config <node.json> --tenant <org> --machine <name> --core-path <dir> [--sign-with <org>/<n>]` once: `-CorePath` (default `C:\agents\zyggy-core`) is where `setup` clones or fast-forwards `zyggy-core`. Setup exit 3 or 4 stops the install before the task; exit 6 still registers and starts the task, then ends with `install: incomplete - see the setup lines above` and exit 1. |
| `README.md` | This file. |

The procedure — download, checksums, `node.json`, keys, unit or task, checks — is `zyggy-core/runbooks/install-node.md`; the files here are what it copies. CI places this folder in `artifacts/<rid>/deploy/` and lists every file in `SHA256SUMS` by its relative name (`sha256sum -c` on Central, `Get-FileHash` in the installer).

Central's `node.json` (deliverable 13) polls every second and has no `workHours` (always active):

```json
"poll": { "activeSeconds": 1, "idleSeconds": 60, "safetyFetchMinutes": 15 }
```

**Upgrade Central before the laptops** (deliverable 41): a laptop's `zyggy archive` writes `items/` envelopes only a Central with the new release files. Install the release on Central first, add the two drop-in lines above, then `systemctl daemon-reload && systemctl restart zyggy-node`; only then upgrade the laptops. A laptop item sent to an older Central just waits on the bus until Central is upgraded. A laptop upgrade is the same one `install.ps1` run (deliverable 42).

On a laptop, the laptop runbook is one command: `zyggy-core/runbooks/install-node.md` "Laptop" — `install.ps1 -Tenant acme -Machine home-laptop -SignWith acme/<n> [-CorePath C:\agents\zyggy-core]`; it runs `zyggy setup` (the Cli README's "Troubleshooting — Setup" has one row per line it can refuse with). The `Stop`/`SessionEnd` hook entries stay a manual step (`zyggy-core/node/README.md` "Wiring").

Hygiene: these files are public. Only placeholders — tenant `acme`, user `alice`, machines `central` and `home-laptop`, `<zyggy-home>`, `<instance-dir>`, `<vm-admin>` — never a real identifier, host, mailbox or value.
