# deploy/ — what a release ships besides the two binaries (deliverable 10)

| File | Purpose |
|------|---------|
| `systemd/zyggy-node.service` | Central's unit: `Type=notify`, user `zyggy`, restart on failure only (exits 3 and 6 stay failed for a human), no machine path, no credential line. |
| `systemd/zyggy-node.service.d/credentials.conf.example` | Optional drop-in: feed the bus secrets to the node through systemd's credentials directory (`LoadCredential=`); the 0600 file layer stays the default. |
| `windows/install.ps1` | The laptops' installer: per user, no administrator rights, registers the `zyggy-node` logon task. The one shell script of the product (rule R1: it runs before any binary is trusted; its invariants are tested statically). |
| `README.md` | This file. |

The procedure — download, checksums, `node.json`, keys, unit or task, checks — is `zyggy-core/runbooks/install-node.md`; the files here are what it copies. CI places this folder in `artifacts/<rid>/deploy/` and lists every file in `SHA256SUMS` by its relative name (`sha256sum -c` on Central, `Get-FileHash` in the installer).

Hygiene: these files are public. Only placeholders — tenant `acme`, machines `central` and `home-laptop`, `<zyggy-home>`, `<vm-admin>` — never a real identifier, host, mailbox or value.
