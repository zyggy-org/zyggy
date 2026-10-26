# Spec: 09 — HMAC keys and the GitHub PAT from the OS secret stores

> Founding-spec sections: §4 Signature ("Shared secret per `key_id` … Stored in each machine's secret store (§8). Rotation: add `<tenant>/2`, accept both for 7 days, drop `<tenant>/1`"); §5 Credentials per machine and Git operations ("the code never handles credentials itself"); §8 Secrets table (HMAC keys, GitHub PAT read; the P0 file-store paragraph, O20), Isolation, Audit; §9 folder map `Secrets/` ("ISecretStore (keys namespaced by tenant) + Windows (CredentialManager), Linux (libsecret / file 0600), Systemd (LoadCredential)"), Packages (`Meziantou.Framework.Win32.CredentialManager`), design rules (five seams, tenant-scoped lookups, no static state, secrets never in `appsettings.json`/`node.json`); §10 `node.json` (`signWith`, `secrets`), Central (`LoadCredential=`), Laptops (service as the interactive user "needed for … Credential Manager access"); §11 Runbooks `rotate-hmac.md`, `revoke-machine.md`, `add-tenant.md`; §12 Definition of done; §13 decisions (HMAC-signed envelopes, per-machine credentials; tenancy shape; MVP scope); §14 Pluggable edges (`ISecretStore`, one implementation per OS), Identifiers (secret names `zyggy/<tenant>/…`). Roadmap entry: `_plans/ROADMAP.md` #09 (overview row line 107; entry lines 285–295 with the items forwarded from 04 F5, 06/07 and 08 ex-Q3; O20 line 781). Project inputs honoured: `_specs/04-transport-a-proxy-proof.md` (ex-OQ2: `FileSecretStore` on all three machines, `zyggy secret set` pulled forward as a thin slice, the bus key is a new secret), `_specs/07-node-service-loop.md` (`AddNodeServices` is the one composition root; `KeyMissing` → exit 6, logged once), `_specs/08-cli-submit-status-verify.md` (ex-Q3: `unknown_key` counts as drift and names the key; "whether retired keys stay in the store as verify-only is recorded as a question for 09's spec"), `_plans/decisions/0001-transport-and-vm.md` (F5; per-machine auth path: Git Credential Manager on both laptops, deploy key over SSH on Central), `memory/long-term/mvp-scope-decision.md`, `memory/long-term/repo-public-hygiene.md`, roadmap rule R1 (tools are .NET), `.claude/instructions/public-api.md`.
>
> Status: **approved by the owner on 2026-10-24 — zero Open Questions; planner-ready.** Owner, verbatim: "approve all" (every recommendation of Q1–Q7 taken). Each decision is folded into the tables below and marked *decided 2026-10-24 (ex-Qn)*; the "Decisions taken at the spec gate" table at the end records them; the founding-spec wording the orchestrating session applies is under "Founding-spec amendments (approved wording)". Nothing in this file edits the founding spec.

## Current state (verified against the repo on 2026-10-24, branch `feature/08-cli-submit-status-verify`)

| Roadmap item | State today | Decision for 09 |
|---|---|---|
| The seam | `Zyggy.Core.Secrets.ISecretStore` — `GetAsync(tenant, name)` (null when absent, never throws for absence), `SetAsync` (atomic replace, empty refused), `RemoveAsync` (bool). `SecretName` = segments of `[a-z0-9-]` joined by `/`, ≤ 64 chars; the full name is `zyggy/<tenant>/<name>` (§8/§14). No enumeration member. | Kept; gains one member, `ListAsync(tenant)` (names only), so `zyggy secret list` and the runbooks can see what a machine holds without probing names. |
| Implementations | `FileSecretStore` (internal; one lowercase-hex line per file under `<root>/<tenant>/<name>`, 0600/0700 on Linux, owner-only DACL on Windows; no cache) registered by `AddFileSecretStore`; `InMemorySecretStore` (public, tests); `CredentialFileSecretStore` (internal; Central's m365/LinkedIn files and `$CREDENTIALS_DIRECTORY`, name-locked to `m365-app-key*` and `linkedin/*`, refuses every other name — 04 ex-OQ2 kept it that way). | `FileSecretStore` stays as the Linux persistent layer and the test/opt-in store; two new implementations (Windows Credential Manager; Linux "credentials directory, then file"); `CredentialFileSecretStore` untouched (different lifecycle, owner of the m365 key). |
| Who reads secrets | `EnvelopeSigner` (`hmac/<n>` for `signWith` and for any `key_id` it verifies — "Rotation is whatever the store holds: any `<tenant>/<n>` present is accepted"); `GitHubBusProvider` (`github-pat`, read on every request, `Authorization` header only); `CanSignAsync` = present and ≥ 32 bytes. | Unchanged. The rotation question is answered by what the store holds (Q1). |
| Who registers the store | `Configuration.ServiceCollectionExtensions.AddNodeServices` → `AddFileSecretStore(o => o.RootDirectory = configuration.Secrets.Root)`; `SecretCommand` builds its own two-line container with the same call. | Both call one new `AddSecretStore(configuration)` that selects per OS and `secrets.store`. |
| `node.json` | `secrets: { root }` only; any key naming a secret (`pat`, `token`, `hmac`, `secret`, `password`) refused except the exact top-level `secrets` section. | `secrets.store` added (`os` default, `file` opt-in); `root` keeps its meaning for the file layer. No other key. |
| Start-up without the key | `BusListener.StartAsync` → `BusStartupResult.KeyMissing`; `PollLoop` logs once `refusing to start: {Condition} — see README "Node: refused to start"` and exits 6 (`NodeExitCodes.Startup`); `submit`/`probe` exit 6 with `run: zyggy secret set hmac/<n> --hex`. | Kept; the condition text names the store and the exact command; a new `StoreUnavailable` outcome covers a store that exists but cannot be opened (a service without a logon session, an unreadable credentials directory). |
| `zyggy secret set` | Reads stdin to end (value never on argv), `--hex`, prints `stored <tenant>/<name> (<n> bytes)`; 04 AC-31 tests run the built binary with `secrets.root` pointing at a scratch directory. | Kept; gains the F5 items (hidden prompt on a terminal, `--generate`, fingerprint) and the siblings `list` and `remove`. The binary tests keep working through `secrets.store: file`. |
| Model process and secrets | `ClaudeCodeCliRunner` already removes `CREDENTIALS_DIRECTORY` from the `claude` child's environment; the file store directory is readable by any process of the same user, the model's included (06/07 note). | The note is answered per OS below (Behaviors, "Who can read a secret"). |
| Git credentials | `GitClient` never sees a credential; 0001: both laptops push over HTTPS through Git Credential Manager, Central over SSH with a deploy key. | No "credential helper fed from `ISecretStore`" (Q5): §5 says the code never handles the git credential and 0001 found no need. |
| Packages | 23 rows in `Directory.Packages.props`; `Meziantou.Framework.Win32.CredentialManager` is already named in §9 Packages but not referenced. | One new row: `Meziantou.Framework.Win32.CredentialManager` 3.0.6 (MIT). |

---

## User Story

**As** the owner of a tenant whose machines each hold one signing key and one repository-scoped GitHub token, **I want** every Zyggy process on a machine — the node service, `zyggy submit`/`status`/`verify`, the probe verbs — to read those secrets from the operating system's own store (Credential Manager on Windows; on the Central VM a 0600 file or the systemd credentials directory), to enter and inspect them with a `zyggy secret` verb that never shows a value by accident, and to rotate the HMAC key tenant-wide while every older signed file on the bus still verifies, **so that** no secret sits in a plain file on a laptop, the node refuses to run when its key is missing rather than failing later, the P1 walking skeleton on the real machines (10) starts from a store that is already the product's, and `rotate-hmac.md` is a procedure the owner can follow rather than a sentence.

---

## Acceptance Criteria

Tenant `acme`, machines `central` and `home-laptop`, keys `acme/1` and `acme/2`. **U** = unit (`tests/Zyggy.Core.Tests`; substitutes for the OS APIs, scratch directories, `FakeTimeProvider`; no real OS store), **W** = wire (OS-conditional, `[Fact(SkipUnless = …)]` plus trait `Category=OsStore`; round-trips a value through the real store under a unique test name and deletes it in `finally`; skipped on the other OS and when the store is not available), **I** = integration (`tests/Zyggy.Integration`, the CLI binary or in process against scratch roots with `secrets.store: file`; never the real `claude`, never GitHub), **D** = document, **CI** = pipeline.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 (U) | `WindowsCredentialManagerSecretStore` over a substituted `ICredentialVault` | `SetAsync(acme, hmac/1, 32 bytes)` / `GetAsync` / `RemoveAsync` / `ListAsync(acme)` | Write: target `zyggy/acme/hmac/1`, user name `zyggy`, secret = the lowercase hex of the bytes, persistence `LocalMachine`, type `Generic`; read: the hex decoded back to the same bytes; a target that does not exist → `null` (no exception); remove → `true` then `false`; list: the vault enumerated with filter `zyggy/acme/*`, names returned tenant-relative in ordinal order, a target of another tenant (`zyggy/other-org/hmac/1`) never returned; a vault that throws `Win32Exception` on read or write → `SecretStoreUnavailableException` whose message carries the Win32 code and text, never the value; a stored secret that is not hex → `InvalidDataException` naming the target. |
| AC-2 (U) | `LinuxSecretStore` over a scratch credentials directory (`CREDENTIALS_DIRECTORY` injected as a dictionary, as `CredentialFileSecretStore` does) and a scratch file root | `GetAsync(acme, hmac/1)` when `<credentials dir>/zyggy.acme.hmac.1` exists / when only `<root>/acme/hmac/1` exists / when both exist with different bytes / when neither exists; `SetAsync`; `RemoveAsync`; `ListAsync` | Credentials directory wins (its bytes are returned as stored — raw, not hex); the file layer is used only when the directory has no such entry; `null` when neither; `SetAsync` and `RemoveAsync` always act on the file layer (the directory is read-only; a name present only in the directory → `RemoveAsync` returns `false` and logs nothing); `ListAsync` is the union of both layers, each name once. On Linux (skip elsewhere): a credentials file whose mode is not 0400/0440/0600 or whose owner is neither the effective user nor root → `SecretStoreUnavailableException` naming the path and mode; a file layer entry not 0600 is still read (the file store never checked modes on read; unchanged). |
| AC-3 (U) | `AddSecretStore(configuration)` | Resolve `ISecretStore` with `secrets.store` absent on Windows / absent on Linux / `file` on either / `os` explicitly / `credential-manager` on Linux / `linux` on Windows | Windows default → `WindowsCredentialManagerSecretStore`; Linux default → `LinuxSecretStore`; `file` → `FileSecretStore` rooted at `secrets.root` or the per-OS default, plus one `Warning` log line at first resolution on Windows (`file secret store selected on Windows: spike and test use only; the product store is Credential Manager`); an explicit store that does not exist on this OS → `NodeConfigurationError.InvalidField` for `secrets.store` (exit 3 in every host, "not available on this platform"); exactly one `ISecretStore` registration; a test registration added afterwards still wins (07's rule). |
| AC-4 (U) | `NodeConfigurationReader` | `secrets: { store: "os" }`, `{ store: "file", root: "<path>" }`, `{ store: "vault" }`, `{ store: 1 }`, `{ root: "<path>" }` alone, `{ store: "file" }` without root, a `secrets.token` key | `SecretsSettings(Store = Os)`; `(File, root)`; `InvalidField secrets.store: one of os, file`; `InvalidField … must be a string`; `(Os, root)` (root kept for the Linux file layer, ignored by Credential Manager); `(File, null)` = per-OS default root; the secret-looking key refused as today. The README schema table has the row. |
| AC-5 (U) | `EnvelopeSigner` over an `InMemorySecretStore` holding `hmac/1` and `hmac/2`, a `node.json` `signWith: acme/2` | Sign a job; verify a file signed with `acme/1` (dated before the rotation); verify a file signed with `acme/1` after `RemoveAsync(hmac/1)` | The new job carries `key_id: acme/2`; the old file verifies (`acme/1` is present, so it is accepted — verify-only by construction, Q1 (a)); after removal it is `unknown_key` with the detail naming `acme/1` (08's `verify` prints `FAIL … unknown_key (acme/1)`). No code change in the signer; the test pins the behaviour `rotate-hmac.md` relies on. |
| AC-6 (U) | `BusListener.StartAsync` with a substituted store that throws `SecretStoreUnavailableException` / returns `null` for `hmac/1` | Start | `BusStartupResult.StoreUnavailable` / `KeyMissing`; nothing pulled, no lock taken. `PollLoop` logs exactly one `Error` `refusing to start: signing key acme/1 is not in the <store name>; run: zyggy secret set hmac/1 --hex — see README "Node: refused to start"` (or `… secret store unavailable: <detail> — see README "Secrets: store unavailable"`) and exits 6. The condition never contains a secret byte. |
| AC-7 (U) | `SecretSetVerb` (Core; the thin command calls it) with an injected `VerbIo` and a `StdinKind` of `Redirected` / `Terminal` | Redirected with `"abc\n"` → stores `abc`; redirected with `--hex` and `"0a0b\r\n"` → stores 2 bytes; terminal: the hidden-input reader is substituted and returns `"abc"` → stores `abc`, the prompt `value for acme/hmac/1 (input is hidden; press Enter when done): ` went to stderr, the value never to stdout or stderr; `--generate` → 32 bytes from `RandomNumberGenerator`, nothing read from stdin, stdout `stored acme/hmac/2 (32 bytes) fingerprint <8 hex>`; `--generate --show` → the same line followed by one line `value (hex, shown once; carry it to the other machines of the tenant): <64 hex>`; `--generate` with a value on stdin → exit 2 `--generate takes no input`; `--generate` for a name not under `hmac/` → exit 2 (`--generate is for hmac/<n> keys`); a value > 512 bytes → exit 2 `value is larger than 512 bytes`; a name already present → overwritten with a stderr `note: acme/hmac/1 replaced (previous fingerprint <8 hex>)`; `SecretSweep` over every stream in every case except the `--show` line, which is asserted to hold exactly the hex of the stored bytes. |
| AC-8 (U) | `SecretListVerb` / `SecretRemoveVerb` over an `InMemorySecretStore` with `hmac/1`, `hmac/2`, `github-pat` | `list` / `remove hmac/1` / `remove hmac/9` / `remove github-pat` | `list` prints the table (name, bytes, fingerprint, store name) in ordinal name order, the `signWith` key marked `signs`, every other `hmac/<n>` marked `verify-only`, and never a value (`SecretSweep`); `remove hmac/1` → `removed acme/hmac/1`, exit 0; `remove hmac/9` → `acme/hmac/9 is not in the <store name>`, exit 1; `remove` of the `signWith` key → exit 2 `acme/hmac/2 is this machine's signing key (signWith); change signWith first` and nothing removed; `remove --yes` is not needed (a remove is one explicit command naming one secret). |
| AC-9 (W, Windows) | The interactive user's Credential Manager | Round-trip `zyggy/acme-<guid>/hmac/1` (32 bytes) and `zyggy/acme-<guid>/github-pat` (a fake token) through the real `WindowsCredentialManagerSecretStore`; `ListAsync`; remove in `finally` | Bytes identical after the round trip; `cmdkey /list` is **not** invoked (no process); the entries are generic credentials with persistence `LocalMachine`; after `finally` nothing with the test prefix remains (`EnumerateCredentials("zyggy/acme-<guid>/*")` empty). Skipped when `Environment.UserInteractive` is false or when a first `WriteCredential` fails with `ERROR_NO_SUCH_LOGON_SESSION` (the skip reason names it). |
| AC-10 (W, Linux) | A scratch directory used as `CREDENTIALS_DIRECTORY` (0700) with a 0400 file `zyggy.acme.hmac.1`, and a scratch file root | The real `LinuxSecretStore` | Reads the credentials file; writes `github-pat` into the file layer with mode 0600 in 0700 directories; a 0644 credentials file → `SecretStoreUnavailableException` naming the mode; cleanup deletes the scratch tree. |
| AC-11 (I) | `home-laptop`'s `node.json` with `secrets: { store: "file", root: <scratch> }` (the built binary, as 04 AC-31) | `zyggy secret set hmac/1 --hex` with hex on stdin; `zyggy secret set hmac/2 --generate`; `zyggy secret list`; `zyggy secret remove hmac/2`; `zyggy secret set hmac/1 somevalue`; `set --generate github-pat` | As 04 AC-31 for `set`; `--generate` writes a 32-byte key file (0600 on Linux) and prints the fingerprint line only; `list` shows both keys with `hmac/1 signs`, `hmac/2 verify-only`, store `file`; `remove hmac/2` deletes the file; the value-as-argument form exits 2; `--generate` of a non-key name exits 2. `SecretSweep` over every stdout/stderr. On Windows the stderr additionally carries the one-line "file secret store on Windows" warning **once per run**, and the tests assert it is there (so the retirement rule is visible). |
| AC-12 (I) | 08's `Gates/P1_WalkingSkeleton.cs` and every bus integration test | Run with `secrets.store: file` written by `NodeJson.Write` | Unchanged results; no test resolves `WindowsCredentialManagerSecretStore` or touches `$CREDENTIALS_DIRECTORY`; a test that forgets `secrets.store` on Windows fails fast (the default would be Credential Manager and `NodeJson.Write` therefore always writes `store: file`). |
| AC-13 (U) | `src/` | Source hygiene (extends 07 AC-12 / 08 AC-6) | `Meziantou.Framework.Win32` is referenced only inside `src/Zyggy.Core/Secrets/Windows/`; `CredRead`/`CredWrite`/`advapi32` appear nowhere else; nothing outside `Zyggy.Core.Secrets` references `CREDENTIALS_DIRECTORY` except `ClaudeCodeCliRunner`'s existing removal; no `Console.` in the new Core verbs (all through `VerbIo`); no static mutable state; every new public type listed in `public-api.md`'s table. |
| AC-14 (D) | Documents | Review | `src/Zyggy.Cli/README.md`: the `secret set` / `secret list` / `secret remove` rows (options, output, exit codes), the `node.json` schema row `secrets.store`, the setup steps on each machine rewritten for the OS stores ("Windows: Credential Manager — nothing to install; Central: the 0600 file under `~/.local/share/zyggy/secrets` or the unit's `LoadCredential=`"), troubleshooting rows for every failure mode below; `src/Zyggy.Core/README.md` (the runbook home until 10): `Secrets/` row rewritten, "Troubleshooting — Secrets: file store" renamed "Secrets" and extended (Credential Manager, credentials directory, `store unavailable`, "Node: refused to start" gains the two conditions), and the **`rotate-hmac.md` procedure** and the **"retire the spike file store on Windows"** procedure as drafted under Contracts (10 moves them into `zyggy-core/runbooks/`); `Directory.Packages.props` gains one commented row; the founding-spec amendments A9-1, A9-2 and A9-3 below (approved 2026-10-24) pasted by the orchestrating session and listed in the journal. |
| AC-15 (CI) | Clean clone, Windows and Linux | `dotnet build`, `dotnet test` (the `W` tests skip on the runner that lacks the store — the Windows runner is interactive enough for Credential Manager; if not, the skip reason is recorded), `dotnet format --verify-no-changes`; `dotnet publish src/Zyggy.Cli` and `src/Zyggy.Node` for `win-x64` and `linux-x64`, single-file, self-contained; smoke `zyggy --version` | Green on both runners; the published size grows by less than 1 MB (the package is a thin P/Invoke layer, no native asset); `Directory.Packages.props` has exactly 24 rows. |
| AC-16 (U/I) | §8, §14 | Every test above | A secret value appears in no stdout/stderr/log/commit/exception message except the explicit `--show` line; the PAT is still read on every request from the selected store and travels only in the `Authorization` header (04 tests unchanged); every lookup is tenant-scoped (a Windows target or a credentials-directory name of another tenant is never read — AC-1/AC-2); `node.json` contains no secret (the reader's refusal unchanged); `--dangerously-skip-permissions` appears nowhere; the `claude` child never receives `CREDENTIALS_DIRECTORY` (existing test stays green). |

---

## Decision Table

| Founding-spec / brief item | Verdict | Target type / library | Justification |
|----------------------------|---------|-----------------------|---------------|
| §8/§9/§14 `ISecretStore` seam, tenant-scoped names `zyggy/<tenant>/<name>` | Keep (built in 03) + Reshape (one member) | `ISecretStore.ListAsync(TenantId)` added | `secret list`, the rotation runbook ("what does this machine hold?") and `revoke-machine.md` need to see names; probing `hmac/1..n` cannot find a stray key. Three implementations plus `CredentialFileSecretStore` (returns the fixed names that exist) implement it in a few lines each. |
| §8 "Windows Credential Manager" for the HMAC keys and the PAT on laptops; §9 Packages `Meziantou.Framework.Win32.CredentialManager` | Library | `WindowsCredentialManagerSecretStore` (internal) over `ICredentialVault` (internal, 4 methods) → `Meziantou.Framework.Win32.CredentialManager` 3.0.6 | The package is the §9 choice; MIT, no dependencies on `net10.0`, 3.6 M downloads, released 2026-10-21 (nuget.org, checked 2026-10-24); it is P/Invoke over `advapi32` `CredRead/CredWrite/CredDelete/CredEnumerate`, so it works in a single-file self-contained publish and removes ~150 lines of `CREDENTIALW` marshalling Zyggy would otherwise own. Its API takes the secret as a `string` with a 2 560-byte blob cap: Zyggy stores the lowercase hex of the bytes (the same encoding the file store uses), which caps a value at 640 raw bytes; the product cap is 512 bytes (Contracts). |
| DPAPI-encrypted file on Windows instead of Credential Manager (`System.Security.Cryptography.ProtectedData`, Microsoft, MIT) | Defer (fallback only) | — | Same user boundary as Credential Manager but leaves a file the model's file tools can read (the 06/07 note); Credential Manager is inspectable by the owner in the Control Panel and is what §8 names. Kept as the documented fallback if 10 finds the service account cannot open the vault — decided 2026-10-24 (ex-Q7). |
| §8 "`secret-tool` (libsecret) on Linux"; §9 "Linux (libsecret / file 0600)" | Defer (file 0600 chosen; libsecret not built) | `LinuxSecretStore` = credentials directory (read-only) → `FileSecretStore` layer (read/write) | The only Linux machine is the headless Central VM: libsecret needs a session D-Bus and an unlocked keyring daemon, which a systemd service and `runuser` sessions do not have (Arch wiki / DataLad knowledge base / NixOS discourse threads, checked 2026-10-24); making it work means a per-boot unlock with a password that itself has to live in a file. §9 already lists "file 0600" as the Linux alternative, `FileSecretStore` exists and is in use on Central since 04, and no .NET libsecret binding is both maintained and dependency-light (`Tmds.DBus`-based clients add a D-Bus stack). **Decided 2026-10-24 (ex-Q2)**: file 0600 plus the credentials directory; libsecret is a §14 item; amendment A9-2. |
| §8 "systemd `LoadCredential` for Central"; §9 "Systemd (LoadCredential)" | Reshape (one Linux store with two layers, not a third class) | `LinuxSecretStore` reading `$CREDENTIALS_DIRECTORY/zyggy.<tenant>.<name with / → .>` first | `CredentialFileSecretStore` proves the pattern (credentials directory, then a 0600 file; mode/owner checks). A separate `SystemdCredentialSecretStore` would need a composite on top of it anyway, because the CLI verbs on Central (`submit`, `verify`, `secret set`) run outside the unit and never see `$CREDENTIALS_DIRECTORY`. One class, ~80 lines, no new seam. **Decided 2026-10-24 (ex-Q6)**: the layer is built with the id mapping `zyggy.<tenant>.<name with / → .>`; whether `zyggy-node.service` uses `LoadCredential=` is 10's call. |
| Store selection at runtime | Keep | `AddSecretStore(NodeConfiguration)`; `secrets.store: os \| file` | §9 "selected at runtime" like `UseWindowsService()/UseSystemd()`. `file` stays available because the integration tests run the built binary (04 AC-31, 08's gate) and cannot inject a store; on Windows it logs one warning so the O20 retirement is visible — **decided 2026-10-24 (ex-Q4)**; amendment A9-3 extends the §10 comment. |
| §8/O20 "the file store retired on Windows and the spike keys rotated when the OS stores land" | Keep (procedure, no code) | Runbook section "Retire the spike file store" in `rotate-hmac.md`'s first execution | Carrying `hmac/1` and `github-pat` from the hex files into Credential Manager is `Get-Content <file> \| zyggy secret set <name> --hex` (the file holds hex); then the first rotation to `acme/2`; then the directory is deleted. A `secret migrate` verb would be code for one afternoon. |
| Node "refuses to start when the signing key is absent" | Keep (built in 04/07) + one outcome | `BusStartupResult.StoreUnavailable`; `SecretStoreUnavailableException` | `KeyMissing` exists and exits 6 with one log line; a store that cannot be opened (no logon session, unreadable credentials file) is a different runbook row and must not look like a missing key. |
| `zyggy secret set` (04 ex-OQ2) | Keep + Reshape (F5) | `SecretSetVerb` in Core (`VerbIo`), thin command: `--hex`, `--generate [--show]`, hidden prompt on a terminal, fingerprint in the output, 512-byte cap, replace note | F5 recorded three real slips (a token on the command line, Ctrl+Z on Linux, a key confused with a checksum). The hidden single-line prompt removes the end-of-input dance; `--generate` removes `openssl` from the Windows procedure (R1); the fingerprint lets the owner compare keys across machines without seeing them. `--show` is the one deliberate echo and exists because the key has to travel to the other machines — **decided 2026-10-24 (ex-Q3)**. |
| `zyggy secret list` (roadmap scope) | Keep | `SecretListVerb` | Names, byte counts, fingerprints, store name, `signs`/`verify-only` marks — never values. Needs `ListAsync`. |
| `zyggy secret remove` (not in the roadmap scope line; in §11 `rotate-hmac.md` "remove `<tenant>/1`" and `revoke-machine.md` "remove its HMAC key") | Keep (small) | `SecretRemoveVerb` → existing `RemoveAsync` | Without it the "drop" step of two runbooks is a Control Panel click on Windows and `rm` on Linux — outside the seam and untestable. One verb over an existing method; refuses to remove the `signWith` key. |
| 08 ex-Q3: retired keys verify-only after a rotation | Reshape (§4/§11 wording) | No code: the signer already accepts any `hmac/<n>` present; `signWith` alone chooses the signing key; `rotate-hmac.md` keeps `<tenant>/1` in every store | Dropping the old key makes every older file in the audited subtree (`archive/` included) fail `verify` forever (08). A "verify-only" flag would add no security: an attacker holding a retired key can backdate `created`, and §4 has no freshness rule, so the only real retirement is removal — which the owner does after a compromise, accepting the drift. **Decided 2026-10-24 (ex-Q1)**; amendment A9-1 rewords §4 and §11. |
| `rotate-hmac.md` (§11) | Keep (drafted here, 10 files it) | Contracts → "Runbook: rotate-hmac" | Roadmap scope; the procedure depends on this spec's verbs. |
| Roadmap scope "git credential helper fed from `ISecretStore` for HTTPS push" | Defer (recommend Out) | — | §5: "the code never handles credentials itself"; 0001 recorded Git Credential Manager on both laptops and the deploy key on Central as working; a helper would put the PAT in a child's stdout and make Zyggy a credential broker. **Decided 2026-10-24 (ex-Q5)**: not built; the project-manager trims the roadmap scope line. |
| §8 "GitHub PAT (read) — same store — HttpClient only" | Keep (built in 04) | `GitHubBusProvider` unchanged | It already reads `github-pat` through the seam on every request; swapping the store is invisible to it. |
| 06/07 note "the spike's file secret store is readable by the model process" | Reshape (answered per OS) | Behaviors → "Who can read a secret" | Windows: Credential Manager, no file; the model would have to run code (`CredRead`) as the owner — the same-user limit §8 accepts for laptops ("Nodes run as the interactive user's account"). Linux: the file layer is readable by the `zyggy` user's processes, the model's included; `LoadCredential=` on the node unit removes it from the model's reach only if the file layer is absent on that machine, which the CLI needs — so the mitigation is the existing template rule: a `Read` deny for the secrets directory (O32 pattern) and the runner's `CREDENTIALS_DIRECTORY` removal. Stated honestly in the runbook. |
| `CredentialFileSecretStore` (m365, LinkedIn) unified with the bus store | Defer | — | Different owners (`cert-init` writes the key), different names, PEM checks; unifying touches live Central paths for no P1 gain. |
| §14 "Tenancy": `zyggy/<tenant>/…` names, one tenant per machine | Keep | target names / credential ids carry the tenant | Costs nothing; the Windows filter `zyggy/<tenant>/*` and the credentials-directory prefix enforce the scope. |
| §14 `init --join`, encrypted credentials (`SetCredentialEncrypted=`, TPM), key escrow, secret sync between machines | Out | — | §14 / no gate. The key travels by the owner (`--show` once, or the hidden prompt on the receiving side). |

---

## Contracts

### Namespaces and visibility (public-api.md)

| Namespace | Public | Internal (visible to the test projects) |
|-----------|--------|------------------------------------------|
| `Zyggy.Core.Secrets` | `ISecretStore` (+ `ListAsync`), `SecretStoreUnavailableException`, `SecretStoreKind` (closed: `Os`, `File`), `SecretFingerprint` (static: `Of(ReadOnlySpan<byte>) → string`, 8 lowercase hex), `ServiceCollectionExtensions.AddSecretStore(NodeConfiguration)` (and the existing `AddFileSecretStore`) | `WindowsCredentialManagerSecretStore`, `ICredentialVault`, `MeziantouCredentialVault`, `LinuxSecretStore`, `SecretStoreNames` (the human names used in messages: `credential-manager`, `credentials-directory`, `file`, `memory`) |
| `Zyggy.Core.Secrets.Verbs` | — | `SecretSetVerb`, `SecretListVerb`, `SecretRemoveVerb`, `IHiddenInput` (one method: read one line without echo), `StdinKind` |
| `Zyggy.Core.Configuration` | `SecretsSettings(SecretStoreKind Store = Os, string? Root = null)` | reader changes |
| `Zyggy.Core.Bus` | `BusStartupResult.StoreUnavailable` (new member) | — |
| `Zyggy.Cli` | — | `SecretCommand` (`set`, `list`, `remove`) over the Core verbs through `VerbIo` (08 ex-Q9 shape) |

### Types

```csharp
namespace Zyggy.Core.Secrets;

public interface ISecretStore
{
    Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken ct);      // unchanged
    Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken ct);  // unchanged; value 1..512 bytes (ArgumentException outside)
    Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken ct);                      // unchanged
    Task<IReadOnlyList<SecretName>> ListAsync(TenantId tenant, CancellationToken ct);                   // NEW: names present for this tenant, ordinal order, never values
}

/// The store exists but cannot be used: no logon session for Credential Manager, an unreadable or mis-moded credentials file,
/// a vault API failure. Message = "<store name>: <detail>" — never a byte of a secret. Absence of a secret is never this exception (it is null).
public sealed class SecretStoreUnavailableException : Exception { … }

public enum SecretStoreKind { Os, File }                     // node.json `secrets.store`; wire words "os", "file"

public static class SecretFingerprint { public static string Of(ReadOnlySpan<byte> value); }   // first 8 lowercase hex chars of SHA-256(value); "fingerprint", never "hash", in owner-facing text

// Windows — internal
internal interface ICredentialVault                           // the 4 calls Zyggy makes; MeziantouCredentialVault forwards to CredentialManager.*
{
    string? Read(string target);                              // Password of the generic credential, null when absent
    void Write(string target, string userName, string secret, string comment);   // Generic, LocalMachine
    bool TryDelete(string target);
    IReadOnlyList<string> Enumerate(string filter);           // target names
}
[SupportedOSPlatform("windows")] internal sealed class WindowsCredentialManagerSecretStore : ISecretStore
{
    // target = "zyggy/<tenant>/<name>"; userName = "zyggy"; secret = lowercase hex of the bytes; comment = "zyggy secret store — managed by `zyggy secret`"
    // Win32Exception from the vault → SecretStoreUnavailableException("credential-manager: <code> <text>")
}

// Linux — internal
internal sealed class LinuxSecretStore : ISecretStore
{
    // ctor(IReadOnlyDictionary<string,string?> environment, FileSecretStore files, ownerOf/effectiveUser hooks as CredentialFileSecretStore)
    // Get: $CREDENTIALS_DIRECTORY/<CredentialId(tenant,name)> when the variable is set and the file exists (mode 0400|0440|0600, owner = me or root; raw bytes, no trailing-newline trimming) else files.Get
    // Set/Remove: files only. List: union. CredentialId = "zyggy." + tenant + "." + name.Replace('/', '.')   (neither TenantId nor SecretName segments contain '.', so the mapping is injective)
}
```

```csharp
namespace Zyggy.Core.Configuration;
public sealed record SecretsSettings(SecretStoreKind Store = SecretStoreKind.Os, string? Root = null);
```

```csharp
namespace Zyggy.Core.Bus;
public enum BusStartupResult { …, KeyMissing, ListenerRunning, StoreUnavailable /* NEW: the secret store cannot be opened; detail in the log line */ }
```

### Store selection

`AddSecretStore(configuration)`: `secrets.store`

| `store` | Windows | Linux |
|---------|---------|-------|
| absent / `os` | `WindowsCredentialManagerSecretStore` | `LinuxSecretStore` (credentials directory → file layer at `secrets.root` or `~/.local/share/zyggy/secrets`) |
| `file` | `FileSecretStore` at `secrets.root` or `%LOCALAPPDATA%\zyggy\secrets` + one `Warning` at first resolution | `FileSecretStore` (identical to today) |

`AddNodeServices` and `SecretCommand` both call it; `AddFileSecretStore` stays public for tests. The host that registers a store afterwards (tests) wins, as 07 decided.

### CLI surface

| Verb | Options and arguments | Output (stdout) | Exit codes |
|------|-----------------------|-----------------|------------|
| `zyggy secret set <name> [--hex] [--generate [--show]] [--config <path>]` | `<name>` tenant-relative (`hmac/<n>`, `github-pat`). Value: `--generate` (32 random bytes, `hmac/<n>` only; nothing read) **or** stdin: when stdin is a terminal, a hidden single-line prompt on stderr; when redirected, read to end, one trailing newline stripped (today's behaviour). `--hex` decodes the input. | `stored <tenant>/<name> (<n> bytes) fingerprint <8 hex>`; with `--show` a second line `value (hex, shown once; carry it to the other machines of the tenant): <hex>`; on replace, stderr `note: <tenant>/<name> replaced (previous fingerprint <8 hex>)` | 0 · 2 usage (value as argument, empty input, not hex, > 512 bytes, `--generate` with input or for a non-`hmac/` name, `--show` without `--generate`) · 3 configuration · 6 store unavailable |
| `zyggy secret list [--config <path>]` | — | `name  bytes  fingerprint  store  role` rows in ordinal name order; `role` = `signs` for `signWith`, `verify-only` for every other `hmac/<n>`, blank otherwise; `(none)` when empty | 0 · 3 · 6 |
| `zyggy secret remove <name> [--config <path>]` | — | `removed <tenant>/<name>` | 0 · 1 not present · 2 when `<name>` is the `signWith` key · 3 · 6 |

A value never appears on argv in any form; `--show` is the only line that ever prints a secret and says so. `secret` verbs resolve `node.json` as every bus verb does (`--config`, `ZYGGY_NODE_JSON`, per-user default) and need `tenant` (and `signWith` only for the role column and the `remove` guard).

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `secrets.store` | `node.json` | `os` | `file` opt-in (tests, spikes); an unknown word → exit 3; the Node, the CLI and the probe verbs read the same key |
| `secrets.root` | `node.json` (04) | per-OS default | the file layer's root (Linux `os`, and `file` everywhere); ignored by Credential Manager (documented, not an error) |
| `signWith` | `node.json` (04) | — | unchanged: the only thing that picks the signing key; verification accepts any `hmac/<n>` present |
| `CREDENTIALS_DIRECTORY` | process environment (systemd) | unset | read-only layer on Linux; never set by Zyggy; removed from the `claude` child (06) |
| Credential Manager target prefix `zyggy/`, user name `zyggy`, persistence `LocalMachine`; credential id prefix `zyggy.`; 512-byte cap; 8-hex fingerprint | code constants | as stated | none in v1 |

### Runbook: `rotate-hmac.md` (drafted here; 10 files it under `zyggy-core/runbooks/`; per ex-Q1: retired keys stay verify-only)

For one tenant; other tenants are unaffected. `<n>` is the current key number, `<n+1>` the new one.

1. On one machine of the tenant (Central): `zyggy secret set hmac/<n+1> --generate --show`. Copy the hex line to a channel you trust (never a repository, chat log or ticket); it is shown once.
2. On every other machine of the tenant: `zyggy secret set hmac/<n+1> --hex` — paste at the hidden prompt. `zyggy secret list` on each machine: the fingerprints of `hmac/<n+1>` must match everywhere.
3. On every machine: set `"signWith": "<tenant>/<n+1>"` in `node.json`; restart `zyggy-node`. From now on new files carry `key_id: <tenant>/<n+1>`; files signed with `<tenant>/<n>` keep verifying because the key is still present.
4. After 7 days: nothing to remove. `hmac/<n>` stays in every store as a verify-only key (`zyggy secret list` shows it as `verify-only`) so that `zyggy verify`, the node's start-up re-verification of `jobs/claimed/` and the dream's reading of `archive/` keep accepting the older files.
5. **Compromise variant** (a machine or a key leaked): do steps 1–3 immediately, then `zyggy secret remove hmac/<n>` on every machine. `zyggy verify` will report `unknown_key (<tenant>/<n>)` for every file signed before the rotation; that drift is expected and documented in the audit (08). Follow `revoke-machine.md` for the machine's PAT and deploy key.

**First execution — retire the spike file store (O20)**, on every Windows machine of the tenant, before step 1: `Get-Content "$env:LOCALAPPDATA\zyggy\secrets\<tenant>\hmac\1" | zyggy secret set hmac/1 --hex` and `Get-Content "$env:LOCALAPPDATA\zyggy\secrets\<tenant>\github-pat" | zyggy secret set github-pat --hex` (the spike files hold hex); `zyggy secret list` shows store `credential-manager` for both; remove `"store": "file"` from `node.json` if present; delete `%LOCALAPPDATA%\zyggy\secrets`. Then run steps 1–4 so the keys that once sat in files are no longer the signing keys. Central keeps its 0600 file store (ex-Q2) — nothing to migrate there.

### Envelope / file formats

Nothing on the bus changes: no field, no canonical-form rule, no state move, no `PROTOCOL.md` edit. `key_id` keeps the §4 form.

---

## Behaviors & Conventions

- **One store per process, chosen once** by `AddSecretStore` from `secrets.store` and the OS; nothing outside `Zyggy.Core.Secrets` knows which (AC-13). The signer, the bus provider, the probe verbs and the `secret` verbs all go through the seam.
- **Names are the §8 names everywhere**: Windows target `zyggy/<tenant>/<name>` (exactly the §8 string); Linux credentials directory `zyggy.<tenant>.<name with dots>` (systemd credential ids are file names and take no `/`); file layer `<root>/<tenant>/<name>`. `SecretName` and `TenantId` syntax guarantee both mappings are injective and traversal-free.
- **Encoding**: Credential Manager and the file layer hold lowercase hex; the credentials directory holds raw bytes (what `LoadCredential=` copies). A reader never trims or re-encodes the credentials-directory bytes — the owner writes the source file without a trailing newline (runbook row).
- **Absence is `null`, unavailability is an exception**: a missing secret is a normal outcome (`KeyMissing`, `no github-pat` as today); a store that cannot be opened is `SecretStoreUnavailableException` → `StoreUnavailable` at node start-up (exit 6, one log line with the README pointer), exit 6 in the CLI with one stderr line. The bus provider maps it to `Unreachable("secret store unavailable")` so the poll loop backs off instead of crashing (04's "failures are results" rule).
- **No cache, no key bytes in memory longer than a call**: as the file store today; a rotated key is visible on the next lookup; byte arrays are zeroed after use where the code owns them (`CryptographicOperations.ZeroMemory`, the `CredentialFileSecretStore` pattern).
- **Who can read a secret** (the 06/07 note, answered): on Windows the vault is per user; any process of the owner's account — the node, `zyggy`, and `claude` with its Bash tool — could call `CredRead`; no file exists for the model's `Read`/`Grep` tools. On Linux the file layer is a 0600 file of the `zyggy` user, readable by the model's file tools unless denied: the `zyggy-core` settings template keeps the `Read` deny rule for `~/.local/share/zyggy/secrets/` (O32 pattern; 15 ships it, 10 sets it on Central by hand), and `ClaudeCodeCliRunner` keeps removing `CREDENTIALS_DIRECTORY`. Neither OS gives a boundary stronger than the user account; §8 accepts that for nodes ("run as the interactive user's account"). The runbook says so plainly.
- **`secret set` input precedence**: `--generate` → nothing read; else terminal → hidden prompt (one line, Enter ends it, Ctrl+C aborts with exit 5 and nothing stored); else redirected stdin → read to end. The hidden reader is an injected `IHiddenInput` (console `ReadKey(intercept: true)` in production) so the verb is unit-testable.
- **Fingerprints, not values**: every verb that mentions a secret prints its fingerprint (`SecretFingerprint.Of`, 8 hex of SHA-256). Owner-facing text says "fingerprint" and "signing key", never "hash" (owner wording rule).
- **`list` marks roles** from `signWith`: `signs` for the configured key, `verify-only` for any other `hmac/<n>`; this is the whole of "verify-only" in v1 (ex-Q1): the signer signs with `signWith` only and verifies with whatever is present.
- **`remove` guards the signing key** (exit 2) so a rotation cannot strand a node; everything else is removable with one explicit command.
- **Rotation is a runbook, not a scheduler**: no automatic key expiry, no overlap timer in code; `rotate-hmac.md` carries the 7-day sentence as guidance for when the old key may be considered retired, and the key stays in the store.
- **Logging**: one `Information` line per `secret` verb (`stored {Tenant}/{Name} ({Bytes} bytes, fingerprint {Fingerprint})`, `removed …`); `Warning` once for the file store on Windows; `Error` once at node start-up for `KeyMissing`/`StoreUnavailable`; never a value, never per lookup.
- **Rule R1**: everything is .NET; the runbook's only shell is `Get-Content … | zyggy secret set` (reason: piping an existing hex file into the verb; no logic in the shell).
- **Public hygiene**: fixtures and this spec use tenant `acme`, machines `central`/`home-laptop`, key ids `acme/1`, `acme/2`; the `W` tests use `acme-<guid>` targets so a failed cleanup is identifiable and never collides with a real tenant.

---

## Failure modes

| Situation | Observable outcome | Runbook entry (Cli README "Troubleshooting — Secrets"; Core README until 10) |
|-----------|--------------------|-----------------------------------------------------------------------------|
| Signing key absent (`signWith` names a key the store lacks) | Node: `refusing to start: signing key <id> is not in the <store>; run: zyggy secret set hmac/<n> --hex — see README "Node: refused to start"`, exit 6; `submit`/`probe send`: exit 6 with the same hint (existing) | "Node: refused to start" row extended; "Bus: keys" row rewritten for the OS stores (same hex on every machine; compare fingerprints with `zyggy secret list`) |
| Key present but shorter than 32 bytes | `KeyMissing` (as today: `CanSignAsync` requires ≥ 32), hint line says `is unusable (shorter than 32 bytes)` | same row — re-enter with `--generate` |
| Credential Manager unavailable (`ERROR_NO_SUCH_LOGON_SESSION` 1312, `ERROR_NOT_FOUND` on write, access denied) | `SecretStoreUnavailableException("credential-manager: <code> <text>")` → node exit 6 `refusing to start: secret store unavailable: …`; CLI exit 6; bus provider `Unreachable("secret store unavailable")` with back-off | **new** "Secrets: store unavailable (Windows)" — the service must run as the user who stored the secrets (10's `install.ps1`), not LocalSystem; log on as that user once so the profile exists; if 1312 persists under the service, the DPAPI-file fallback of ex-Q7 is the next deliverable's work |
| Credentials-directory file wrong mode or owner | `SecretStoreUnavailableException("credentials-directory: <path> must be mode 0400, 0440 or 0600 (is <mode>)")` / `… must be owned by <user> or root` | **new** "Secrets: credentials directory" — `LoadCredential=` copies are root-owned 0400/0440 by systemd; a hand-made directory must match |
| Credentials-directory variable set but the directory missing | ignored (falls through to the file layer); `Debug` log once | same row |
| Secret stored in a non-hex form (Credential Manager edited by hand, a file written by another tool) | `InvalidDataException` naming the target/path; node exit 6 through `StoreUnavailable`; CLI exit 6 | "Secrets: corrupt value" — re-enter it with `zyggy secret set` |
| `secrets.store` unknown or not available on this OS | exit 3 `zyggy: configuration error: secrets.store: one of os, file` | existing `node.json` row + the schema table |
| File store selected on Windows | one `Warning` line per process; everything works | "Secrets: file store on Windows" — allowed for tests and spikes; the product store is Credential Manager; retire per `rotate-hmac.md` first execution |
| `secret set` value on the command line / empty / not hex / > 512 bytes / `--generate` misuse | exit 2, one stderr line, nothing stored | "Secrets: entering a value" — the hidden prompt, `--hex`, `--generate`, the cap |
| `secret set` replaces an existing value | stored; stderr `note: … replaced (previous fingerprint …)` | same row — a replaced `hmac/<n>` on one machine only breaks verification tenant-wide; compare fingerprints |
| `secret remove` of the `signWith` key | exit 2, nothing removed | "Secrets: rotation" → `rotate-hmac.md` |
| `secret remove` of an absent name | exit 1 | — |
| Old files fail `verify` with `unknown_key (<tenant>/<n>)` after a compromise rotation | 08's `FAIL` line, exit 6 (unchanged) | 08's "Verify: unknown key after rotation" row now points at `rotate-hmac.md` step 5 and says this is expected after a removal, never after a routine rotation |
| Fingerprints differ between machines | `invalid_signature` rejections on the receiver (04), alert in 22 | "Bus: keys" — `zyggy secret list` on each machine; re-enter the key whose fingerprint differs |

---

## Dependencies

| Package | Version | License | Why (what bespoke code it removes) |
|---------|---------|---------|------------------------------------|
| `Meziantou.Framework.Win32.CredentialManager` | 3.0.6 (nuget.org, released 2026-10-21; checked 2026-10-24) | MIT | Named in §9 Packages. `CredRead/CredWrite/CredDelete/CredEnumerate` with their `CREDENTIALW` marshalling, the 2 560-byte blob rule and the `Generic`/`LocalMachine` constants — about 150 lines of P/Invoke Zyggy would otherwise own and test blind. No dependencies on `net10.0`; pure P/Invoke to `advapi32`, so single-file self-contained publish needs no native asset; ~3.6 M downloads, maintained by the author of the `Meziantou.Analyzer` family. Referenced by `Zyggy.Core` only (the Node and the CLI get it transitively), used only under `Secrets/Windows/`, guarded by `[SupportedOSPlatform("windows")]` so the Linux build sees no call. |

No other package: `RandomNumberGenerator`, `SHA256`, `Console.ReadKey(intercept: true)`, `File.GetUnixFileMode` and the owner lookup already used by `CredentialFileSecretStore` are BCL or existing code. `System.Security.Cryptography.ProtectedData` (Microsoft, MIT, Windows-only) is **not** added; it is the recorded fallback of ex-Q7.

---

## Deliberate deviations from the founding spec

All decided by the owner on 2026-10-24 ("approve all"); the exact wording is under "Founding-spec amendments (approved wording)" below.

- §4 Signature and §11 `rotate-hmac.md` / `revoke-machine.md`: "drop `<tenant>/1`" / "remove `<tenant>/1` after 7 days" / "remove its HMAC key" → the old key stays as a verify-only key; removal is the compromise variant — ex-Q1, amendment A9-1.
- §8 Secrets row and §9 folder map: "`secret-tool` (libsecret) on Linux" → not built in v1; Linux = systemd credentials directory then 0600 file layer — ex-Q2, amendment A9-2.
- §10 `node.json` example: `secrets` gains `store` — ex-Q4, amendment A9-3 (comment only).
- Roadmap scope line "git credential helper fed from `ISecretStore` for HTTPS push" → not built — ex-Q5; a roadmap trim for the project-manager, no founding-spec text.

### Founding-spec amendments (approved wording)

> Approved by the owner 2026-10-24 ("approve all"). The orchestrating session pastes these into `_specs/00 - Personal Agent Platform — Technical Specification.md` and lists every edit in the journal — never the technical-analyst or the planner. Each block: exact section heading → exact current text → exact replacement text.

#### A9-1 — Retired HMAC keys stay verify-only (ex-Q1)

**Section:** `## 4. Bus protocol` → paragraph **Signature**, second bullet.

Current text:

```text
- Shared secret per `key_id`, where `key_id` = `<tenant>/<n>`; keys never cross tenants. Stored in each machine's secret store (§8). Rotation: add `<tenant>/2`, accept both for 7 days, drop `<tenant>/1`.
```

Replacement text:

```text
- Shared secret per `key_id`, where `key_id` = `<tenant>/<n>`; keys never cross tenants. Stored in each machine's secret store (§8). Rotation: add `<tenant>/2` to every store of the tenant and switch `signWith` to it; `<tenant>/1` stays in the stores as a verify-only key so that older files keep verifying; a key is removed only after a compromise, and `agentbus verify` then reports `unknown_key` for the files it signed (deliverable 09).
```

**Section:** `## 11. Observability and operations` → **Runbooks (`agent-core/runbooks/`)**, the `rotate-hmac.md` bullet.

Current text:

```text
- `rotate-hmac.md`: for one tenant, add `<tenant>/2` to all of that tenant's secret stores, set `signWith: <tenant>/2`, remove `<tenant>/1` after 7 days. Other tenants are unaffected.
```

Replacement text:

```text
- `rotate-hmac.md`: for one tenant, generate `<tenant>/2` on one machine (`agentbus secret set hmac/2 --generate --show`), enter it on every other machine of the tenant and compare fingerprints (`agentbus secret list`), set `signWith: <tenant>/2` everywhere; `<tenant>/1` stays verify-only and is removed (`agentbus secret remove hmac/1`) only after a compromise, accepting `unknown_key` drift on the files it signed. Other tenants are unaffected.
```

**Section:** `## 11. Observability and operations` → **Runbooks**, the `revoke-machine.md` bullet.

Current text:

```text
- `revoke-machine.md`: delete its PAT and deploy key, remove its HMAC key, move its `tenants/<org>/nodes/<machine>/` to `tenants/<org>/archive/`.
```

Replacement text:

```text
- `revoke-machine.md`: delete its PAT and deploy key, rotate the tenant's HMAC key per `rotate-hmac.md` (compromise variant — the key is per tenant, not per machine), move its `tenants/<org>/nodes/<machine>/` to `tenants/<org>/archive/`.
```

#### A9-2 — Linux secret store: systemd credentials directory, then a 0600 file; libsecret deferred (ex-Q2)

**Section:** `## 8. Work boundary, security and secrets` → **Secrets** table, first row.

Current text:

```text
| Bus HMAC keys (`<tenant>/1`, `<tenant>/2`) | Windows Credential Manager / `secret-tool` (libsecret) on Linux; systemd `LoadCredential` for Central; stored under `agentbus/<tenant>/hmac/<n>` | `AgentBus.Core.Secrets` abstraction (`ISecretStore`), keyed by tenant; never in `appsettings.json` |
```

Replacement text:

```text
| Bus HMAC keys (`<tenant>/1`, `<tenant>/2`) | Windows Credential Manager on laptops (target `agentbus/<tenant>/hmac/<n>`); on Linux the systemd credentials directory (`LoadCredential=`, id `agentbus.<tenant>.hmac.<n>`) when present, else a 0600 file under `~/.local/share/zyggy/secrets/<tenant>/hmac/<n>`; libsecret is a §14 item (no Linux laptop in v1) | `AgentBus.Core.Secrets` abstraction (`ISecretStore`), keyed by tenant; never in `appsettings.json` or `node.json`; entered with `agentbus secret set`, never shown except once by `--generate --show` |
```

**Section:** `## 9. .NET solution structure and technology choices` → the solution tree block, the `Secrets/` line.

Current text:

```text
      Secrets/              ISecretStore (keys namespaced by tenant) + Windows (CredentialManager), Linux (libsecret / file 0600), Systemd (LoadCredential)
```

Replacement text:

```text
      Secrets/              ISecretStore (keys namespaced by tenant) + Windows (CredentialManager), Linux (systemd credentials directory, then file 0600); libsecret is a §14 item
```

#### A9-3 — `node.json` `secrets.store` (ex-Q4; comment only)

**Section:** `## 10. Configuration and deployment` → the `node.json` example, the `secrets` line.

Current text:

```text
  "secrets": { "root": null }              // FileSecretStore root during P0 (§8); null = the per-OS default
```

Replacement text:

```text
  "secrets": { "store": "os", "root": null }   // store: os = Credential Manager on Windows, credentials directory then 0600 file on Linux; file = the P0 store (tests, spikes). root: the file layer's root; null = the per-OS default
```

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| Two tenants' secrets on one Windows machine (not v1) | Targets `zyggy/<tenant>/…` keep them apart; `ListAsync(acme)` filters `zyggy/acme/*` and never returns `zyggy/acme-2/…` (the filter is a prefix with the trailing `/`, so `acme` does not match `acme-2`) |
| A Credential Manager entry `zyggy/acme/hmac/1` created by hand with a non-hex password | `InvalidDataException` naming the target; the node refuses to start (`StoreUnavailable`), never signs with garbage |
| Credentials directory holds `zyggy.acme.hmac.1` **and** the file layer holds `acme/hmac/1` with other bytes | The directory wins; `secret list` shows the name once with store `credentials-directory`; `secret set hmac/1` writes the file layer and prints `note: the credentials directory still provides hmac/1 to the node` on stderr |
| `secret set` on Central inside the unit's environment (not expected) | Writes the file layer; the credentials directory is never written |
| `--generate` on a machine, then the owner forgets to carry the key | The other machines keep `signWith` on `<n>`; the generating machine's new files are `unknown_key` on them and rejected; `verify` there names `<tenant>/<n+1>`; the runbook's step 2 fingerprint check is the guard |
| `hmac/<n>` removed on one machine only | That machine rejects older files signed with it; others still verify them; `secret list` across machines shows the difference |
| `signWith` names `<tenant>/3` while the store has `hmac/1` and `hmac/2` | `KeyMissing`: the hint names `hmac/3` |
| Stdin is a terminal on a system without a console (service context running `secret set`) | `Console.IsInputRedirected` is true for a service's null stdin → read-to-end path → empty → exit 2 |
| A PAT with a trailing newline pasted at the hidden prompt | The prompt reads one line; Enter ends it; no newline is stored |
| Value exactly 512 bytes | Accepted; 513 refused |
| `secret list` with the node running concurrently | Read-only; no lock needed; Credential Manager and the file layer are safe for concurrent readers |
| Windows: `zyggy` run under a different user than the service | Different vault; `list` shows nothing; the "store unavailable (Windows)" row explains that secrets are per user |
| Linux: `ZYGGY_NODE_JSON` points at a `node.json` with `secrets.root` on a filesystem without Unix modes | `FileSecretStore` behaviour unchanged (04) |

---

## Out of Scope

- libsecret / `secret-tool` on Linux (ex-Q2); a DPAPI-file store on Windows (fallback only, ex-Q7); encrypted systemd credentials (`SetCredentialEncrypted=`, TPM); a `secret migrate`, `export` or `sync` verb; secret transfer between machines by Zyggy.
- Feeding git a credential (helper, askpass, `GIT_ASKPASS`) from `ISecretStore` (ex-Q5); any change to how `GitClient` authenticates.
- Automatic rotation, key expiry, an overlap timer, a "retired" flag or freshness rule on envelopes (ex-Q1); re-signing archived files.
- Changes to `CredentialFileSecretStore`, `cert-init`, the m365 or LinkedIn credential paths.
- The systemd unit, `install.ps1`, the Windows service account and `LoadCredential=` lines (10); filing the runbook under `zyggy-core/runbooks/` (10); the settings-template `Read` deny rule (15); alerts on signature failures (22).
- `PROTOCOL.md`, §4 envelope fields, `EnvelopeSigner.Canonicalize`, the golden files.
- Any `node.json` key beyond `secrets.store`.

---

## Open Questions

none — all seven resolved by the owner on 2026-10-24 ("approve all"); see the table below.

---

## Decisions taken at the spec gate (2026-10-24, owner: "approve all" — every recommendation taken)

| Q | Decision (Owner 2026-10-24: approve all — recommendation taken) | Resolved shape | Reason |
|---|------------------------------------------------------------------|----------------|--------|
| Q1 Retired keys after a rotation (08 ex-Q3) | Retired `hmac/<n>` keys stay in every store as **verify-only**; `signWith` alone picks the signing key; verification accepts any key present; removal (`zyggy secret remove`) is the compromise variant with documented `unknown_key` drift. No policy code. Amendment A9-1 (§4 Signature, §11 `rotate-hmac.md`, §11 `revoke-machine.md`). | AC-5, the `list` role column (`signs` / `verify-only`), the `remove` guard, `rotate-hmac.md` steps 4–5 | Dropping the key makes every pre-rotation file in the audited subtree (`archive/` included) fail `verify` forever and makes the node reject its own interrupted `jobs/claimed/`; a "retired since" marker adds code without security (`created` is signed by the same attacker-held key and §4 has no freshness rule); re-signing the archive rewrites the audit trail. |
| Q2 Linux store | `LinuxSecretStore` = systemd credentials directory (read-only, id `zyggy.<tenant>.<name with / → .>`, mode/owner checked) then the existing 0600 `FileSecretStore` layer (read/write). **libsecret not built**; §14 item. Amendment A9-2 (§8 Secrets row, §9 folder map). | Contracts → Types, AC-2, AC-10 | The only Linux machine is the headless Central VM: a Secret Service needs a session D-Bus and an unlocked keyring daemon (sources cited in the Decision Table), i.e. a per-boot unlock password in a file; no maintained, dependency-light .NET binding; the CLI verbs run by the owner outside any unit could not reach a keyring. |
| Q3 `--generate --show` | `zyggy secret set hmac/<n> --generate` stores 32 random bytes and prints the fingerprint only; with `--show` it prints the hex **once**, labelled, as the one deliberate echo of a secret in the product. | CLI surface, AC-7, AC-11, `rotate-hmac.md` step 1 | The key must travel to the other machines of the tenant; today `openssl rand -hex 32` shows it on the terminal anyway and `openssl` is not a given on Windows (R1); a file export would put a secret on disk again. |
| Q4 `secrets.store` | New `node.json` key `secrets.store: os \| file`, default `os`; `file` is the opt-in for tests and spikes and logs one `Warning` per process on Windows; `root` keeps its meaning for the file layer. Amendment A9-3 (§10 comment). | Contracts → Configuration / Store selection, AC-3, AC-4, AC-11, AC-12 | The 04/08 binary tests cannot inject a store and must never write the real Credential Manager; selection by `root` being set would be implicit and wrong on Central; a Node-only refusal would split the rule across hosts. |
| Q5 Git credential helper from `ISecretStore` | **Not built.** The roadmap scope line is trimmed by the project-manager; the Cli README states that git authenticates itself (Git Credential Manager on Windows, the deploy key on Central) and that the stored PAT serves the API poll only. | Decision Table row, Out of Scope, Notes 1 | §5 "the code never handles credentials itself"; 0001 recorded both laptops pushing through Git Credential Manager and Central over SSH; a helper would put the PAT in a child's stdout and make Zyggy a credential broker. |
| Q6 systemd credential id and `LoadCredential=` | The credentials-directory layer is built with the injective mapping `zyggy/<tenant>/<name>` → `zyggy.<tenant>.<name with / → .>`; whether `zyggy-node.service` uses `LoadCredential=` is decided by 10. | Contracts → Types (`LinuxSecretStore.CredentialId`), Behaviors → Names, Notes 2 | systemd credential ids are file names (no `/`); `.` occurs in neither `TenantId` nor `SecretName`, so the mapping cannot collide, unlike a hyphen mapping; ~30 lines on the `CredentialFileSecretStore` pattern; the CLI needs the file layer regardless. |
| Q7 Windows service and the user's Credential Manager | Risk accepted: the P1 gate cannot prove it; 10's user-executed checklist includes "service running as the interactive user → `/health` green → a report signed". Named fallback if `CredRead` fails under the service (`ERROR_NO_SUCH_LOGON_SESSION`): a DPAPI-protected file store (`System.Security.Cryptography.ProtectedData`, Microsoft, MIT) behind the same seam — **not** built now. | Failure modes → "store unavailable (Windows)", Dependencies, Notes 2 | §10 already requires the service to run as the interactive user for Claude Code auth and Credential Manager; building a second Windows store for one owner contradicts the MVP-scope decision; a DPAPI file as the primary store would leave a file the model's tools can read and contradict §8/§9. |

---

## Notes for the planner and the project-manager

1. **Roadmap scope trim** (ex-Q5, decided): remove "git credential helper fed from `ISecretStore` for HTTPS push" from the 09 entry; add `zyggy secret remove` to the scope line; the DoD gains "`rotate-hmac.md` drafted in the Core README, filed by 10".
2. **10 inherits** the owner-executed Credential Manager check (Q7), the choice of `LoadCredential=` for `zyggy-node.service` (Q6), the first execution of `rotate-hmac.md` on the home laptop (retire the spike file store), and the `Read` deny rule for `~/.local/share/zyggy/secrets/` on Central (with 15's template).
3. **Existing tests to touch**: `NodeJson.Write` (test helper) must emit `secrets.store: file`; `SecretSetCommandTests` gain the `--generate`, `list`, `remove` cases and the Windows warning assertion; `CredentialFileSecretStore` gains `ListAsync` (returns the fixed names that exist) with one unit test; the signer's rotation test (AC-5) is new but changes no production code.
4. **`W` tests and CI**: the Windows runner is an interactive session for `windows-latest` hosted agents, so AC-9 should run there; if it skips, the skip reason must be visible in the test output and recorded at the gate — never silently green.
5. **`public-api.md`**: `ISecretStore` gains a member (a breaking change for any external implementer — none exists; `InMemorySecretStore` is public and gains it too).
