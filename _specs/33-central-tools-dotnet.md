# Spec: 33 — Central tools in .NET: Microsoft 365 and `remember` as `zyggy` verbs (P0b)

> Founding-spec sections: §1 Scope, Non-goals (no sending without a per-action permission prompt, D7), Constraints (application credential); §3 Components (`Zyggy.Cli` verbs; skills rows `morning-brief`/`mail-backfill`/`files-backfill`/`m365`, `remember`; Hooks); §6 "Central executing its own jobs"; §7 File format and Rules; §8 Secrets (Graph certificate row), Isolation, Injection, Data protection, Work boundary; §9 Solution structure, Packages, Design rules (five seams, `IProcessRunner`, no static state), Publish; §10 Central, Upgrade path; §11 Alerts (Graph row); §12 Definition of done, agent split; §13; §14 (memory path shape, tenant-namespaced secrets). Roadmap entry `_plans/ROADMAP.md` #33, its hand-off brief of 2026-10-05, rule R1, O36, the parallel-run constraints.
>
> Repository inputs read: `_specs/23-m365-mail-onedrive.md` (D7/D8 contracts, AC-1..AC-55), `_specs/27-…` (remember contract), `_specs/31-…`, `_specs/32-…`, `_specs/28-central-dream-local.md` (foundation contracts, ledger, exit table, units), `_plans/decisions/0002-central-productive.md` (sections 23, 28, Settings, Tools, Credentials); the 21 scripts of `D:\source\zyggy-core\.claude\` and the bats suites (`m365.bats` 94 cases, `remember.bats` 15, `repo.bats` 54; `clone.bats` 39 and `inventory.bats` 32 for the split-off part); the instance `zyggy-geoffrey` (`.mcp.json`, `instance/settings.local.json`, `instance/zyggy.json`, the three m365/dream units); this repository's `src/Zyggy.Core/{Processes,Models,Memory,Secrets,Git,Dream}`, `src/Zyggy.Cli`, `tests/Zyggy.Integration/Infrastructure/ZyggyCli.cs`, `Directory.Packages.props`. Claude Code docs checked 2026-10-05: `code.claude.com/docs/en/headless` (slash commands in `-p`, `permission_denials`, stdin), `code.claude.com/docs/en/mcp` (`headersHelper`: run through a shell, 10 s, re-run on connect and after 401/403, credential-looking variables removed from its environment).
>
> **Split (owner decision 3, 2026-10-05):** the GitHub tools (`github inventory`, `github clone`, the askpass hand-off) moved unchanged into `_specs/34-central-github-tools-dotnet.md`, built right after 33. Everything GitHub in the earlier draft of this spec now lives there; this spec only provides the shared foundation 34 builds on (credential store, "replace the environment" process option, fact-line writer, secret-pattern use, source-hygiene test).
>
> Status: **Approved 2026-10-05 — zero Open Questions; planner-ready.** The eight Open Questions were decided by the owner on 2026-10-05 (verbatim: "I accept all your recommendations") — see the Decisions log. The founding-spec amendments W33-1..W33-8 are accepted and pending application by the owner (section "Founding-spec amendments"). Decision 7 carries one placeholder for the result of a check the main session is running on Central; it does not block the planner (both outcomes are specified).

## Current state (2026-10-05)

| Item | State |
|------|-------|
| `zyggy` binary | 0.1.5 pinned on Central (`/opt/zyggy/0.1.5/zyggy`, symlink `/usr/local/bin/zyggy`, `instance/zyggy.json`); verbs `dream`, `dream request`, `dream status`, `memory digest`, `--version`; exit table of 28 (0 ok, 1 no run, 2 usage, 3 configuration, 4 locked / unknown section, 5 aborted, 6 failed, 7 push deferred) |
| Foundation from 28 (reused, not rebuilt) | `IProcessRunner`/`ProcessRunner` (environment **additions** only — no way to clear the inherited environment), `IModelRunner`/`ClaudeCodeCliRunner` (prompt on stdin, stream-json, no `--disallowedTools` list, denial **count** only), `MemoryPaths`, `MemoryFileWriter` (render/parse — not byte-preserving for appends), `SecretPatterns` (the `secret-patterns.txt` semantics in .NET), `ContactDetailPatterns` (the dream's rule — differs from `facts.sh`'s phone rule), `GitClient`, `VersionPin`, `DreamConfiguration` (env resolution: principal, `ZYGGY_TIMEZONE`, `ZYGGY_INSTANCE_DIR`, `ZYGGY_SECRET_PATTERNS`, `ZYGGY_STATE_DIR`), `CliEnvironment`, `ISecretStore` + `FileSecretStore` (hex files under `<root>/<tenant>/<name>`) |
| Shell to migrate in 33 (template `zyggy-core`, mirrored in `zyggy-geoffrey`) | The m365 and `remember` scripts: 18 of the 21 scripts, about 3,900 of the 4,374 lines (inventory in the roadmap entry). `inventory.sh`, `clone.sh`, `askpass.sh` (about 480 lines) are 34's. |
| What Central runs today | `.mcp.json` (template and instance) = loopback HTTP + `headersHelper` `mcp-auth-header.sh` (D8). `zyggy-m365-mcp.service` → `mcp-server.sh`. `mcp-wrapper.sh` (old stdio launcher) is referenced by nothing; 23 kept the file only because its token-lifetime checks were never run (23 AC-52 already required its removal). The brief unit exists in the instance but is **not installed** (brief off). |
| Unused variable | `ZYGGY_M365_ORIGIN` is still set by `m365-lib.sh` for every model run; nothing reads it since D7 removed `propose.sh`. |

---

## User Story

**As** the owner,
**I want** every Microsoft 365 and `remember` tool on Central to be the tested `zyggy` binary instead of about 3,900 lines of bash, behaving exactly as today — same answers, same files, same refusals, same calls to Graph —
**So that** rule R1 holds (complex shell gone), the security controls (the guard before every send or move, the certificate client assertion, the per-connection token, the secret-pattern refusal) are one tested implementation each, and the next deliverables (34, 29, 30, 11–13) build on .NET instead of on scripts.

**As** Central (machine role),
**I want** one binary with one place for each data-protection rule and each credential read,
**So that** a fix or a rule change is made once, tested under `TreatWarningsAsErrors` in CI on both runners, and installed with a pin and a checksum.

---

## Acceptance Criteria

Evidence kinds: **U** unit test (`tests/Zyggy.Core.Tests`, substitutes, `FakeTimeProvider`, tenant `acme` / user `alice`); **I** integration test (`tests/Zyggy.Integration`: the built `zyggy` binary or in-process services with real `IProcessRunner`, `tools/fake-claude`, fake `markitdown` / fake MCP server executables, stubbed `HttpMessageHandler`); **T** template/instance CI (bats + shellcheck, `zyggy` stub); **C** recorded on Central in `_plans/decisions/0002-central-productive.md` section 33, **agent-run unless marked owner-run**. No test calls Graph, GitHub, the real `claude`, the Softeria server or MarkItDown.

### A. Foundation additions (shared code changes additively only while 28 is open)

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | The solution | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on `windows-latest` and `ubuntu-latest`; publish both RIDs | All green; both single-file binaries smoke-run `zyggy --version`; a `v*` tag produces the artefacts with `SHA256SUMS` (28's release path, unchanged) (CI) |
| AC-2 | 28's unit and integration suites | Every commit of 33 that touches `Processes/`, `Models/`, `Memory/`, `Git/`, `Secrets/` or the command-line host | 28's tests pass **unchanged** (no edited or deleted 28 assertion); every change to those types is an addition (new optional member, new type) (U + I + review at each slice gate) |
| AC-3 | `ProcessSpec` with the new "replace the environment" option | A child is started | The child's environment is exactly the given variables (nothing inherited); the default stays "inherit + additions" (U on `ProcessStartInfo`; I with a probe child printing its environment). Consumers: the MCP server launch when supervised (AC-25) and 34's git clone. |
| AC-4 | The credential store (an `ISecretStore` implementation, read-only) | `m365-app-key` is read | `$CREDENTIALS_DIRECTORY/m365-app-key` first (source "credentials directory"), else the key file (source "file"). Refused with exit 3 and the shell's message when not a regular file, mode ≠ 0600 (0400 allowed for the credentials-directory copy), owner ≠ the running user, empty, or no PEM private-key header; read at every use, never cached, never logged, the buffer cleared after use; write and remove are not supported; a tenant other than the configured principal's is refused (U + I on Linux). The store maps secret names to files through one table so 34 adds `github-read-token` additively. |
| AC-5 | `instance/m365.json` | Any m365 verb loads it | Validation is rule-for-rule `zy_m365_load_config` (full, and the base subset for `cert-init`): GUIDs, UPN, time zone, language tag, `cert.*`, `drives.*` forms, `sites_granted` non-empty when `sites` is, `actions` block (enabled ⊆ {send, upload, move}, unique; integers; extensions; `write_drive_id` when upload is enabled), the obsolete `consent` block refused, the numeric caps of `brief`, `mail_backfill`, `files_backfill`; first problem → exit 3 `configuration error: <same text>` before any request; certificate expiry ≤ 30 days → one stderr warning naming runbook 13 "Rotate the certificate"; expired → exit 3 (U, one case per bats case of "graph: misconfiguration") |
| AC-6 | Every verb of this spec | Called with an unknown option, a missing or extra argument | Exit code and usage line per that verb's table in Contracts (owner decision 5: each script's codes kept for its verb); the guard and the action log exit 2 on any argument (hook contract) (U + I) |
| AC-7 | The hook-path verbs (`m365 guard`, `m365 log`, `m365 auth-header`, `memory remember`, `m365 facts`, `m365 state`, `m365 parse`) | Started | They build no generic host (as `memory digest`); `auth-header` finishes within 8 s including one retry (U with fake clock; I timing on Linux) |

### B. Memory: `remember`, fact lines, the one refusal

| # | Given | When | Then |
|---|-------|------|------|
| AC-8 | `zyggy memory remember [--scope …] [--tag …] [--source …] -- <fact>` | The 15 cases of `remember.bats` | Same outcomes: `inbox/remember-<local date>.md`, line `- [<tag>] <date><hint><provenance>: <fact>`, fact collapsed to one line, ≤ 1,000 characters, scope `general\|project:<name>\|machine`, `observed` needs `--source`, secret pattern → exit 2 `refused: matches secret pattern <name>` (never the value), stdout `remembered: <path>` + the line, `ZYGGY_HOOKS=off` → exit 0 and no output; files byte-equal to the template's expected files (U golden + I) |
| AC-9 | An existing inbox file | A fact writer appends | Every existing byte is preserved except the value of an `updated:` line inside the front matter; a new file gets exactly `---\nname: …\ndescription: …\nupdated: <date>\n---\n` with today's texts (no YAML quoting); written as a temporary file and renamed; never git (U golden) |
| AC-10 | The dream's ledger hashes fact lines | `remember`, `facts` and the brief's memory line write lines for the same input as the shell | The lines are **byte-identical** to the shell's (golden fixtures shared with the template suite before its deletion) (U). 34 adds the inventory's lines to the same golden set. |
| AC-11 | `secret-patterns.txt` | `remember`, `facts`, `verify`, `parse`, the Graph/MarkItDown error-text guards | All use 28's `SecretPatterns` loaded from the same file (missing → exit 3, fail closed); a shared sample fixture yields the same pattern name in the .NET suite and in the template's remaining bash (`stop.sh` and, until 34, the two GitHub scripts, via `lib.sh`) (U + T) |
| AC-12 | `zyggy m365 facts --kind … --source … [--max n]` with candidate lines on stdin | The `facts.bats` cases | Same validation order and reasons (empty, non-letter start, e-mail address, URL, secret pattern `<name>`, phone — the `facts.sh` phone rule, not 28's `ContactDetailPatterns`), control characters removed, whitespace collapsed, cut at 240 characters with `…`, duplicates dropped against the file and the input, `--max` → lines up to the cap written and exit 5, the stderr counts line byte-equal, stdout empty, refused text never echoed; files byte-equal `expected/m365-facts-*.md` (U golden + I) |

### C. Graph core and the application identity

| # | Given | When | Then |
|---|-------|------|------|
| AC-13 | A throw-away RSA key and certificate | A client assertion is built | Header `{"alg":"PS256","typ":"JWT","x5t#S256":<b64url sha256 of the DER>}` (default) or `{"alg":"RS256","typ":"JWT","x5t":<b64url sha1>}`; claims `aud` = the tenant's v2 token endpoint, `iss` = `sub` = client id, `jti` UUID-v4 shaped, `nbf` = `iat` = now, `exp` = now + 300 s; the PSS (salt = digest length) or PKCS#1 v1.5 signature verifies against the certificate and a tampered payload does not (U) |
| AC-14 | The token request | Minted | One POST to `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token` with `client_credentials`, scope `https://graph.microsoft.com/.default`, the jwt-bearer assertion type, the client id and the assertion — no secret; `AADSTS700024` → exit 6 naming the clock skew; any other 4xx → exit 6 `auth failed (<error>) — runbook 13 "Certificate rejected"`; no `access_token` → 6; the key file's mtime unchanged (U) |
| AC-15 | Graph requests | Any read | HTTPS only; hosts `graph.microsoft.com/v1.0` and the login host only; `/users/<mailbox>`, `/drives`, `/sites` — never `/me`; GET only besides the token POST; redirects not followed; a `@odata.nextLink` outside the Graph base → exit 6, never followed; 60 s per request; 429/503 retried up to 5 times after `Retry-After` seconds, else 2^n s; one re-mint on 401, a second 401 → 6; 403 → exit 6 `forbidden (<code>) — runbook 13 "Scope or grant missing"`; tolerated statuses per operation as in `graph.sh`; a transport error text matching a secret pattern is withheld (U, stub handler, fake clock) |
| AC-16 | The read operations | Run against the `tests/fixtures/graph` shapes | Mail folders (top level + one level of children, `excluded` from `mail_backfill.exclude_folders`), drives (OneDrive + granted sites, first id kept, exclusions by id or name), drive files (every delta page from the root, paths rebuilt from folder items, last occurrence wins, deleted items and folders dropped, `modified` truncated to seconds + `Z`, sorted by modified then id, > 2,000 pages → 6, 403/404 → refused 5 `drive <id>: <status> (…)`), Drafts since `<ISO>` (`$filter`, `$select`, `$top=50`, plain-text preference), message sender (lower-cased `from`, `replyTo[]`, `conversationId`), item exists / item kind (`exists\|absent`, `folder\|file\|absent`) — same results as `graph.sh` (U) |
| AC-17 | `zyggy m365 check [--counts] [--other-mailbox <upn>] [--drive <id>]` | Run | stdout byte-equal `expected/m365-check.txt` (state dir normalised); `--counts` folder lines and `zyggy-drafts <k>`; another mailbox readable (2xx) → `SCOPE NOT ENFORCED`, exit 5; `--drive` → `granted\|not granted\|not found`; stderr `key: file\|credentials directory` (U + I) |
| AC-18 | `zyggy m365 token-test [--key new] [--alg PS256\|RS256]` | Run | Mints one token and prints `token ok: <n> bytes, expires <UTC>` — **never the token**; `--key new` uses the pending `.new` pair; failures as AC-14 (U + I) |
| AC-19 | `zyggy m365 cert-init [--rotate\|--commit]` | Run | `ZYGGY_HOOKS=off` → exit 5 refused, nothing created; key exists → exit 5; creates an RSA 2048 key (PKCS#8 PEM, 0600, directory 0700) and a self-signed certificate (0644, CN = `cert.subject`, valid `cert.days`); stdout sha1/sha256 thumbprints, expiry date and path, never the key; `--rotate` writes `.new` files, refused while a rotation is pending; `--commit` swaps, without `.new` → exit 4; the existing key written by `openssl` (`BEGIN PRIVATE KEY`, and `BEGIN RSA PRIVATE KEY`) is read unchanged; an EC key → exit 3 (U + I) |

### D. Consent guard, action log, Draft audit (security controls — never softened)

| # | Given | When | Then |
|---|-------|------|------|
| AC-20 | `zyggy m365 guard` with PreToolUse JSON on stdin | 23's full guard matrix (send: clean / attachment / Bcc / HTML / body over cap / recipients over cap / `saveToSentItems: false` / foreign `userId` / malformed address / from-sender-replyTo / unknown message or body field / a key present twice in different case / unexpected top-level argument; upload: allowed new-file form / existing target / item-id form / foreign drive / bad name / extension / content over cap / not base64 / parent not a folder; move: `deleteditems`, `archive`, `inbox`, a non-excluded folder id, `recoverableitemsdeletions`, `purges`, unknown folder, excluded folder, foreign `userId`, malformed id; action disabled in `actions.enabled`) | Clean → exit 0, **no output** (the `ask` rule then prompts); each violation → exactly `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"m365-guard: refused: <reason>"}}` with the shell's reason text; body keys read case-insensitively; at most two Graph reads per call, reads only; never "allow", never "ask"; never a token, body or file content in any output; runs whatever `ZYGGY_HOOKS` says; a non-action tool → exit 0, no output (U table + I through the binary) |
| AC-21 | The guard | Its own failure: input not hook JSON, configuration error, principal missing, a Graph read failing (500, 403), an internal exception | Exit 2 with one stderr line `m365-guard: …`, no stdout (fail closed); the template launcher turns a missing binary or any other non-zero exit into exit 2 (U + I + T) |
| AC-22 | `zyggy m365 log` with PostToolUse JSON | A send, an upload, a move; an error result; another tool | One row `{ts, session_id, tool, summary, status}` appended per action (summary: recipients + subject ≤ 120 + body length / drive + parent + name + decoded size / message id → destination; control characters flattened; never the body or content), `status` `ok` or `error: <code>` (MCP `isError` or a Graph error object in the result text), `actions.jsonl` 0600 in the 0700 state dir, appended under an exclusive lock; another tool → no row; failure to record → exit 2 one stderr line (U + I) |
| AC-23 | `zyggy m365 verify <date> <window-start>` | The `verify.bats` cases | Same audit: kinds brief / reply / other, recipients to ∪ cc ∪ bcc lower-cased, reply allowed only to the sender/replyTo of a replied id recorded for `<date>` in the same conversation, text above Outlook's quote separator only for replies, URL / e-mail address (not in the brief) / secret pattern flags, one brief exactly, at most `reply_cap + 1` Drafts; receipt `brief-<date>.json` 0600 byte-equal the fixtures (no body text); stdout `audit ok` (exit 0) or `audit FLAGGED: <reasons in Draft order>` (exit 5); Graph failure → exit 6 and no receipt; reads only (U + I) |

### E. MCP server launch and the per-connection token (D8)

| # | Given | When | Then |
|---|-------|------|------|
| AC-24 | `zyggy m365 auth-header` (the `headersHelper`) | Run as Claude Code runs it (shell, no argument, no stdin, credential-looking variables removed) | stdout exactly one line `{"Authorization":"Bearer <token>"}`, stderr empty, exit 0, one journal line `zyggy-m365: token minted` (via `logger -t zyggy-m365`, never fatal if `logger` is absent); failure → stdout empty, stderr exactly `m365: token refresh failed — runbook 13 "Certificate rejected"`, journal `token refresh failed: <reason>`, one retry unless configuration/usage, whole run < 8 s; the token never on stderr, in the journal, in an argument list or a file (U + I) |
| AC-25 | `zyggy m365 mcp-server` (unit `ExecStart`) | Started | The pinned `ms-365-mcp-server` found on `PATH` or `$HOME/.local/bin`, whose real path is under `$HOME/.local` and executable (else exit 3); it runs with exactly the argv `--org-mode --http 127.0.0.1:<port> --http-local-file-tools --no-dynamic-registration` and exactly the ten contracted variables (`PATH`, `HOME`, `LC_ALL`, `NODE_OPTIONS`, `MS365_MCP_CLIENT_ID`, `MS365_MCP_TENANT_ID`, `MS365_MCP_ORG_MODE`, `MS365_MCP_USE_KEYTAR`, `MS365_MCP_TOKEN_CACHE_PATH`, `ENABLED_TOOLS` = the anchored allowlist built from the template's tool data files), no token; port from `ZYGGY_M365_PORT` (default 47365, 1024–65535 else exit 3); the download root `~/.cache/zyggy-m365-downloads` exists 0700 (created if missing) else exit 3; the verb never mints a token and never opens the key; SIGTERM from systemd reaches the server and its exit code is the unit's (I with a fake server executable logging argv, environment names and `token=absent`) |
| AC-26 | `zyggy m365 mcp-server --probe` | Against a running (fake) server | Unauthenticated POST → 401 else exit 6; then, with a header minted in-process (never argv, never a file), `initialize` and `tools/list` → the offered tools equal the allowlist exactly (extra or missing → exit 6 naming up to five); prints `tools: <n>` + names, `listen: 127.0.0.1:<port>`, `env:` the server's variable names; no token in the output (I) |
| AC-27 | The m365 tool partition (plain data files in the template — owner decision 8; Contracts) | Loaded | `enabled ∪ excluded` = the pinned server's tool list, disjoint, sorted, unique; `actions ⊆ enabled`; the brief, mail-backfill and files-backfill allow/deny lists computed from them equal today's lists with every script rule replaced by its verb rule (golden of the three argument lists); the binary carries no built-in copy (U + T) |

### F. State and document parsing

| # | Given | When | Then |
|---|-------|------|------|
| AC-28 | `zyggy m365 state get\|set\|reset <key> [<arg>] [<value>]` | The `state.bats` cases | Same keys (`mail-watermark`, `backfill-watermark <folder>`, `drive-token <drive>`, `files-backfill-watermark <drive>`, `replied <date>`), file names, value grammars (ISO, cursor `<ISO>[\|<item-id>]`, message id), absent `mail-watermark` → now − 24 h, `replied` appends each id once, atomic 0600 files in a 0700 dir, unknown key or bad value → 4, the removed D6 verbs → 4; files written by the shell are read unchanged (U + I) |
| AC-29 | `zyggy m365 parse <file>` with `ZYGGY_M365_RUN_DIR` | The `parse.bats` cases | Containment checked on the path as written and on the resolved path; a symlink inside is removed (never its target), anything outside refused (5) and left alone; types docx xlsx pptx pdf txt md csv json html htm (any case); size over the smaller `file_max_bytes` → 5; MarkItDown via `IProcessRunner`, found on `PATH` or `$HOME/.local/bin`, 120 s timeout, 2 GiB address-space cap; control characters removed, every secret-pattern line replaced by `[line withheld: matches secret pattern <name>]`, cut at the smaller `file_text_cap_bytes` with `[cut at <n> bytes]`; stderr `parse: <name> <n> lines, <w> withheld[, cut at <n> bytes]`; failure → 6 with the first error line (secret-guarded, ≤ 200 chars); the input deleted in every case once known to be inside (U + I with a fake `markitdown`) |

### G. Model-run verbs: morning brief and the two backfills

| # | Given | When | Then |
|---|-------|------|------|
| AC-30 | `zyggy m365 brief` | The `brief.bats` cases | Pre-flight in the shell's order (configuration, `claude` present, token minted — fails fast, receipt or a brief Draft of today → `brief <date>: already created` exit 0, Inbox id, drive ids); a 0700 run directory under the download root; one model run through `IModelRunner` with working directory = the checkout, the brief allow/deny lists, `--permission-mode auto`, `--permission-prompts none`, no session persistence, turns/budget/model from `brief.*`, `ZYGGY_HOOKS=off` and `ZYGGY_M365_RUN_DIR` in the child's environment, no credential in it, no terminal, the `/morning-brief …` prompt delivered per decision 7; result checked (error, no result, malformed, over turns or budget → exit 6 recorded in `brief.jsonl`); counts line and denial tool names read; the audit (AC-23); one memory line through the shared writer (also under `ZYGGY_HOOKS=off`); `brief.jsonl` row 0600; last stdout line byte-equal `expected/m365-journal-*.txt`; SIGTERM → child stopped, run directory removed, exit 143, no receipt (U + I with fake-claude) |
| AC-31 | `zyggy m365 mail-backfill [--folder <name>] [--reset]` | The `mail-backfill.bats` cases | `ZYGGY_HOOKS=off` → exit 5 before anything; principal keys taken from `.claude/settings.local.json` `env` when unset; folders minus exclusions, each looped newest first from its watermark until a batch lists 0 messages; per-batch model run with the mail-backfill lists and caps, the `/mail-backfill …` prompt delivered per decision 7; checkpoint `mail-backfill.json` (0600) rewritten after each completed batch; totals caps (`budget_usd_total`, `max_facts`, `max_messages`) → exit 5, checkpoint intact; a watermark that did not move stops that folder (exit 5 at the end); SIGINT/SIGTERM → child stopped, exit 130/143, the next run prints `resuming folder <name> from <watermark>`; `--folder`, `--reset`; progress and counts lines byte-equal (U + I) |
| AC-32 | `zyggy m365 files-backfill [--drive <name>] [--reset]` | The `files-backfill.bats` cases | Same as the shell: drive pre-check and listing 403/404 → skipped and counted forbidden; the walk after the cursor `<ISO>\|<item-id>` (strictly greater; a plain-ISO cursor resumes at its own second); skip classes type / size / path (incl. control characters and the fence tag) without a model run; prompt `/files-backfill <drive> <run-dir> <n>` + the file lines inside `<zyggy-m365-data>`, delivered per decision 7; the cursor moves only when the model's counts line confirms the batch; run directory removed after each batch and on a signal; caps; counts line byte-equal (U + I) |
| AC-33 | The checkpoint and watermark files the shell wrote on Central (fixtures in their exact shape) | A backfill verb runs | It reads them as they are and continues: completed folders/drives are skipped, an unfinished one resumes at its watermark/cursor; nothing is re-done (U + I) |
| AC-34 | `ZYGGY_INSTANCE_DIR` set | `brief`, `mail-backfill` or `files-backfill` starts with a binary whose version or hash differs from `instance/zyggy.json` | Exit 3 (`version_mismatch`) before any request or model run — the dream's rule (U) |
| AC-35 | `src/` | Source hygiene test | Nothing outside the `IModelRunner` implementation names `claude`; nothing outside the Graph adapter names a Graph or login host; no tenant, mailbox, site or account literal; no test resolves the real `claude`, `markitdown` or `ms-365-mcp-server` (U). The test is written so 34 adds the GitHub host rule as one more row. |

### T. Template and instance

| # | Given | When | Then |
|---|-------|------|------|
| AC-36 | The `zyggy-core` template after 33 | Reviewed by `repo.bats` | No m365 or `remember` script remains except the launchers listed in Contracts, each ≤ 30 lines, no parsing, no branching on data, with its R1 reason in the README; the removed scripts' bats suites are gone; the m365 and `remember` skills, rules, `settings.json` and `.mcp.json` name `zyggy` verbs only; `lib.sh` holds only what `stop.sh`, `inventory.sh` and `clone.sh` use (34 trims it to `stop.sh`'s needs); the GitHub scripts and their bats suites are unchanged; shellcheck clean (T) |
| AC-37 | `settings.json` | `repo.bats` | `permissions.ask` = the three action tools; `permissions.deny` = the path rules + `Bash(zyggy m365 auth-header*)`, `Bash(zyggy m365 token-test*)`, `Bash(zyggy m365 cert-init*)`, `Bash(zyggy m365 mcp-server*)`, `Bash(zyggy m365 brief*)`, `Bash(zyggy m365 mail-backfill*)`, `Bash(zyggy m365 files-backfill*)` (the successors of the `graph.sh` deny) + one rule per excluded tool read from the tool data files; PreToolUse/PostToolUse matcher unchanged, wired to the guard/log launchers (T) |
| AC-38 | The template's minimum binary version (data file) and the instance pin | Instance CI | Fails when `instance/zyggy.json` pins a version below the template's minimum; template CI drives launchers and documented commands against a `zyggy` stub (no binary download); both CI runs green (T) |
| AC-39 | `security.md`, `AGENTS.md`, `operations.md`, README, the m365 and `remember` skills, runbook sections 13–14 and every `remember.sh` mention | Reviewed; `/doctor prompt-audit` on Central | Script names replaced by verbs; exit-code tables per verb; the D7/D8 wording unchanged in substance; audit clean (T + C) |

### C. Central (installed only after 28's third night is recorded)

| # | Given | When | Then |
|---|-------|------|------|
| AC-40 | 28's third consecutive night recorded in 0002 | Install | Tagged release, `SHA256SUMS` checked, `/opt/zyggy/<version>/zyggy` root:root 0755, previous version kept, symlink switched, `instance/zyggy.json` bumped; **then** the template and instance pulled (never the template before the binary); the brief unit (still not enabled) and `zyggy-m365-mcp.service` point at `/usr/local/bin/zyggy m365 …`; `instance/settings.local.json` `env` gains `ZYGGY_INSTANCE_DIR`; the server restarted once; the next nightly dream commits and pushes on the new version (C) |
| AC-41 | The installed binary | Agent-run checks | `zyggy m365 check`; one backfill invocation that reads the shell's checkpoint and either reports done or resumes where the shell stopped (interrupted once and resumed if work remains); `zyggy m365 mcp-server --probe` = the allowlist; **out-of-policy send refused (decision 1):** a `claude -p` probe on Central asking for a send with a Bcc (hidden recipient) to the owner's own address → the guard's refusal is the recorded denial reason, nothing is sent (no new item in Sent Items), no `actions.jsonl` row; one `remember` from a session (C) |
| AC-42 | Brief and live-action evidence (decisions 1 and 2) | Once each | **Attended brief (agent-run, decision 2):** the agent runs `zyggy m365 brief` once by hand on Central on the new binary — one "Zyggy — morning brief" Draft (possibly reply Drafts) in the Drafts folder, `audit ok`, the journal line; the timer stays off and the unit stays not enabled. **Live send (owner-run, decision 1):** from the phone the owner asks Zyggy to send a short mail to the owner's own second address, sees exactly one permission prompt, allows it once; the agent then checks one new row in `actions.jsonl` (`status: ok`, recipients = that address) written by the new code. A mail question after more than 95 minutes idle is answered without any owner action (C) |
| AC-43 | Central after the install | Secret sweep (23 AC-23/AC-29 scope: memory, both checkouts, settings, units, `~/.local/state/zyggy`, journal incl. `-t zyggy-m365`, `~/.claude.json`, `~/.claude/debug/`, transcripts, credential paths, `ps -eo args` during a token refresh) | No token, key or JWT anywhere; no token in any argument list (C) |
| AC-44 | Runbook | Reviewed at the plan gate | Sections 13–14 and the `remember` mentions updated: every script command replaced by its verb; install / upgrade / rollback / "binary missing or wrong version" reused from 14 with the m365 consequences (guard blocks every action, `headersHelper` fails, brief and backfills exit 3/127); new entries for the failure modes below (C) |
| AC-45 | This spec | At the gate | The founding-spec amendments W33-1..W33-8 are accepted by the owner (decision 4, 2026-10-05) and applied to `_specs/00 …` by the owner; 0002 section 33 has dated rows for AC-40..AC-43 (C) |

---

## Decision Table

| Item (founding spec / roadmap / script) | Verdict | Target type / library | Justification |
|---|---|---|---|
| R1: complex shell → `zyggy` verbs (roadmap rule, §9) | **Keep** | `Zyggy.Core.M365`, `Zyggy.Core.Memory` (remember/fact writer), verbs in `Zyggy.Cli` | Owner decision; one tested implementation per control. |
| GitHub tools (`inventory.sh`, `clone.sh`, `askpass.sh`; roadmap #33 scope) | **Defer → 34** | `_specs/34-central-github-tools-dotnet.md` | Owner decision 3 (2026-10-05): the Microsoft 365 part carries the security-critical rewrite and the daily value; the GitHub part shares only the credential store and the secret check and loses nothing by waiting a few days. |
| `graph.sh` read verbs `mail-folders`, `drives`, `drive-files`, `drafts-since`, `message-sender`, `item-exists`, `item-kind` as command-line verbs | **Reshape** | Internal methods of `GraphReader`; no CLI surface | Their only callers (guard, verify, brief, backfills) become in-process; a verb without a caller is surface to document and secure for nothing. Outputs still parity-tested at method level. |
| `graph.sh token` (prints a bearer) | **Reshape** | `zyggy m365 token-test` (prints size and expiry, never the token); the only token-printing verb is `auth-header` (D8 contract) | Runbook uses it only as `token \| wc -c`; printing a bearer to stdout is a needless leak path. |
| `graph.sh check`, `cert-init` | **Keep** | `zyggy m365 check`, `zyggy m365 cert-init` | Used by the `m365` skill and runbook 13 (status, yearly rotation); `graph.sh` cannot be deleted while rotation needs it. |
| Certificate client assertion with `openssl` + `jq` + `curl` | **Reshape** (BCL) | `RSA.SignData` (PSS / PKCS#1), `X509Certificate2` thumbprints, `System.Text.Json`, `HttpClient` | ~80 lines of BCL; the header shape is asserted by parity tests, which a library would hide. |
| MSAL (`Microsoft.Identity.Client` 4.90.1, MIT, monthly releases) for the assertion | **Rejected** | — | Maintained and small, but adds two packages for one POST, controls the assertion header itself (the parity tests pin PS256 + `x5t#S256` and RS256 + `x5t`) and maps errors its own way (the runbook keys on `AADSTS700024`, `invalid_client`). |
| `Microsoft.Graph` SDK / `Azure.Identity` | **Rejected** | — | Large generated SDK for seven GET shapes and one POST; single-file size and trimming cost; the roadmap brief prefers the BCL. |
| Retry/throttling (`graph.sh http`) | **Reshape** (BCL) | A small retry loop in the Graph adapter over `TimeProvider` | Exact parity semantics (Retry-After else 2^n, 5 attempts, tolerated statuses returned after the last) are simpler to own than to configure in `Microsoft.Extensions.Http.Resilience`/Polly, which would add packages. |
| curl's `--proto =https`, cleared environment, no redirects, 60 s | **Keep** (as rules) | `HttpClient` with HTTPS-only URIs, `AllowAutoRedirect = false`, `UseProxy = false`, 60 s timeout | Same properties in-process; no credential ever crosses a process boundary. |
| `m365-lib.sh` configuration validation | **Keep** | `M365Configuration` (+ base subset) | Policy as data: `instance/m365.json` stays the file the tools read. |
| `m365-lib.sh` tool partition arrays (generated from `tests/fixtures/m365/*-tools.txt`) | **Reshape** | Plain data files in the template (`.claude/skills/m365/tools/…`), read by the binary at run time and asserted by `repo.bats`; no copy built into the binary | Owner decision 8 (2026-10-05): one source for the server's `ENABLED_TOOLS`, the run allow/deny lists and the 328 per-tool settings deny rules; a server upgrade is a template change, not a binary release. |
| Key lookup `$CREDENTIALS_DIRECTORY` then file, with mode/owner/header checks (`zy_m365_read_key`) | **Keep** (behind the seam) | `CredentialFileSecretStore : ISecretStore` (§9 "Systemd (LoadCredential)" + file), read-only | §9: nothing reads a secret except through `ISecretStore`; the existing key file stays where it is (moving it is out of scope); `m365-app-key` is tenant-scoped through the configured principal. 34 adds `github-read-token` to the same store. |
| `m365-guard.sh` (PreToolUse, fail closed) | **Keep** | `zyggy m365 guard` + a thin launcher | Security control (§8 Injection, D7); behaviour identical. Launcher reason: Claude Code blocks a call only on exit 2 — a missing binary (127) or a runtime crash would otherwise fail **open** into the permission prompt without the guard's refusals. |
| `m365-log.sh` (PostToolUse) | **Keep** | `zyggy m365 log` + a thin launcher | Reconciliation with the tenant audit log depends on it; same launcher reason (exit 2 tells Claude the row was not written). |
| `verify.sh` | **Keep** | `zyggy m365 verify` (also called in-process by the brief) | Post-run Draft audit (§8 Injection); owner can run it by hand. |
| `mcp-auth-header.sh` | **Keep** | `zyggy m365 auth-header`, named directly in `.mcp.json` (no launcher) | D8; `headersHelper` runs through a shell, so a missing binary fails the connection — fail closed by construction (docs checked). |
| `mcp-server.sh` | **Keep** | `zyggy m365 mcp-server [--probe]` as the unit's `ExecStart` | The unit needs validated config, the allowlist and the exact environment; the verb replaces itself with the server (or supervises it and forwards SIGTERM — planner's choice, same observable contract). |
| `mcp-wrapper.sh` (old stdio launcher) | **Defer → delete** | — | Referenced by nothing (both `.mcp.json` use HTTP); 23 AC-52 already required its removal; it needs `graph.sh token`, which goes. Rollback = previous template commit. |
| `state.sh` | **Keep** | `zyggy m365 state` | Called by the model in brief/backfill runs and by the backfills; file names and grammars are shared contracts. |
| `facts.sh` | **Keep** | `zyggy m365 facts` over the shared fact writer | Data-protection validator (O34); its phone rule differs from 28's `ContactDetailPatterns` — kept as is for parity (lines are hashed by the dream). |
| `remember.sh` (pulled in from 11) | **Keep** | `zyggy memory remember` over the same fact writer | Roadmap: one implementation of the refusal and the inbox line format. |
| `lib.sh` `zy_secret_match` used by `stop.sh` (and, until 34, by `inventory.sh`/`clone.sh`) | **Defer** (12; GitHub users → 34) | `lib.sh` trimmed to what `stop.sh` and the two GitHub scripts use | `stop.sh` moves in 12; a per-turn binary call or pulling 12 forward adds nothing to 33's safety. The two refusal implementations are held equal by one shared sample fixture (AC-11). |
| `parse.sh` | **Keep** (MarkItDown stays external) | `zyggy m365 parse`; MarkItDown via `IProcessRunner` under `prlimit` | R1 allows an external tool with no .NET equivalent; `prlimit` (util-linux) replaces `ulimit -v`, which .NET cannot set on a child. |
| `brief.sh` | **Reshape** | `zyggy m365 brief` (whole run in .NET, the unit runs the binary; no launcher) | Its pre/post logic is the complex part; the model call goes through `IModelRunner` (§9: nothing else may start `claude`). |
| `mail-backfill.sh`, `files-backfill.sh` | **Keep** | `zyggy m365 mail-backfill`, `zyggy m365 files-backfill` | Same orchestration, checkpoint formats unchanged (compatibility with the files on Central). |
| `ZYGGY_M365_ORIGIN` in every model run | **Defer → drop** | — | No reader since D7 removed `propose.sh`. |
| `IModelRunner` request for the m365 runs | **Reshape** (additive) | New optional members: a disallowed-tools list; denial tool names in the result; a prompt-on-the-command-line switch **only if** the Central check of decision 7 shows that a `/skill` prompt on stdin is not expanded | The m365 runs need MCP, slash commands and explicit deny lists, which 28's dream did not; additive so 28's tests hold (AC-2). Owner decision 7 (2026-10-05). |
| `IProcessRunner` environment | **Reshape** (additive) | `ProcessSpec` option "replace the environment" | The MCP server must start with exactly ten variables (AC-25) and 34's git clone with an allowlisted environment (32's `env -i`); built once here. |
| Test-only environment knobs (`ZYGGY_NOW`, `ZYGGY_M365_STUB`, `ZYGGY_RETRY_SCALE`, `ZYGGY_PARSE_TIMEOUT`) | **Reshape** | DI options and `FakeTimeProvider` in tests; not read by the binary | A production binary that honours test switches is an attack surface; .NET tests do not need them. |
| `ZYGGY_HOOKS=off` refusals (`cert-init`, backfills) and acceptances | **Keep** | Same per verb | §8: unattended runs never back-fill, never rotate. |
| Exit codes per script | **Keep** (per verb) | Contracts table; parse errors → 4 for these verbs | Owner decision 5 (2026-10-05): no reader changes meaning, the old tests port one to one; the binary already has command-specific codes. |
| Template ↔ binary versioning | **Keep** (minimal) | A one-line minimum-version data file in the template; instance CI compares with the pin | The template now depends on a binary from another repository. |
| Template CI against the real binary | **Rejected** | `zyggy` stub in template CI | The binary repository is private; behaviour is proven in this repository's CI; the template proves wiring. |
| Pin check before model runs (28) | **Keep** (extended) | `brief`, `mail-backfill`, `files-backfill` check `instance/zyggy.json` | Same rule as the dream: no unattended model run on an unpinned binary. |
| Logging | **Keep** (28's choice) | stdout/stderr contracts exactly as the scripts; journald through the unit; `logger` for the helper | No Serilog (07). |
| Windows | **Defer** | Verbs compile; Linux-only behaviour (modes, `prlimit`, exec) exits 3 on Windows | Roadmap non-goal. |

---

## Contracts

### Namespaces (in `Zyggy.Core`; public types per `.claude/instructions/public-api.md`; most stay `internal`)

| Namespace | Types (names indicative) |
|---|---|
| `Zyggy.Core.M365` | `M365Configuration` (+ loader/validator), `M365Paths` (state dir `<ZYGGY_STATE_DIR>/m365`, download root, key/cert paths), `ClientAssertion`, `GraphTokenClient`, `GraphReader` (internal adapter interface + implementation), `M365ToolPartition`, `GuardPolicy` + `GuardDecision`, `ActionLog`, `DraftAudit` + `AuditReceipt`, `M365State`, `DocumentParser`, `BriefRun`, `MailBackfill`, `FilesBackfill`, `McpServerLauncher`, `HeaderHelper`, `CertificateInit` |
| `Zyggy.Core.Memory` (additions) | `FactLineWriter` (byte-preserving append, front matter for new files), `RememberRequest`, `FactValidator` (`facts.sh` rules) |
| `Zyggy.Core.Secrets` (addition) | `CredentialFileSecretStore : ISecretStore` (read-only; one name → file table, `m365-app-key` in 33) |
| `Zyggy.Core.Processes` (addition) | `ProcessSpec` option to replace the environment |
| `Zyggy.Core.Models` (additions) | `ModelRunRequest.DisallowedTools`; `ModelRunResult.PermissionDenialTools`; a prompt-on-the-command-line switch only under the decision 7 fallback |

`Zyggy.Core.GitHub` and the `GitClient` clone methods are 34's.

Seams: no new §9 seam. `GraphReader` is an internal adapter behind an interface (for tests), the only code that names the Graph and login hosts — founding-spec amendment W33-5 (accepted). Secrets only through `ISecretStore`; MarkItDown, `logger`, `prlimit`, the MCP server and `claude` only through `IProcessRunner` / `IModelRunner` (the server launch is the one start outside `IProcessRunner` if the planner chooses exec — it is a process replacement, not a child).

### CLI surface (System.CommandLine 2.0.11, registered in `CliApplication`)

| Command | Replaces | Stdout / stderr | Exit codes |
|---|---|---|---|
| `zyggy memory remember [--scope general\|project:<name>\|machine] [--tag stated\|observed] [--source <text>] -- <fact…>` | `remember.sh` | `remembered: <path>` + line | 0 kept · 2 refused (secret) · 3 · 4 usage; `ZYGGY_HOOKS=off` → 0, no output |
| `zyggy m365 check [--counts] [--other-mailbox <upn>] [--drive <id>]` | `graph.sh check` | status line, drive/folder lines; stderr `key: <source>`, expiry warning | 0 · 3 · 4 · 5 · 6 |
| `zyggy m365 token-test [--key new] [--alg PS256\|RS256]` | `graph.sh token` (diagnostics) | `token ok: <n> bytes, expires <UTC>` | 0 · 3 · 4 · 6 |
| `zyggy m365 cert-init [--rotate\|--commit]` | `graph.sh cert-init` | thumbprints, expiry, path | 0 · 3 · 4 · 5 · 6 |
| `zyggy m365 auth-header` | `mcp-auth-header.sh` | one JSON line | 0 · 3 · 4 · 6 |
| `zyggy m365 mcp-server [--probe]` | `mcp-server.sh` | probe lines | 0 (or the server's code) · 3 · 4 · 6 |
| `zyggy m365 guard` (stdin hook JSON) | `m365-guard.sh` | nothing, or the deny JSON | 0 · 2 |
| `zyggy m365 log` (stdin hook JSON) | `m365-log.sh` | nothing | 0 · 2 |
| `zyggy m365 verify <YYYY-MM-DD> <YYYY-MM-DDTHH:MM:SSZ>` | `verify.sh` | `audit ok` / `audit FLAGGED: …` | 0 · 3 · 4 · 5 · 6 |
| `zyggy m365 state get\|set\|reset <key> [<arg>] [<value>]` | `state.sh` | the value (get) | 0 · 3 · 4 |
| `zyggy m365 facts --kind brief\|mail-backfill\|files-backfill --source <tag> [--max <n>]` (stdin) | `facts.sh` | stderr counts line | 0 · 3 · 4 · 5 |
| `zyggy m365 parse <file>` (`ZYGGY_M365_RUN_DIR`) | `parse.sh` | the text; stderr summary | 0 · 3 · 4 · 5 · 6 |
| `zyggy m365 brief` | `brief.sh` | progress, last line the journal line | 0 · 3 · 4 · 5 · 6 · 143/130 |
| `zyggy m365 mail-backfill [--folder <name>] [--reset]` | `mail-backfill.sh` | progress + counts line | 0 · 3 · 4 · 5 · 6 · 130/143 |
| `zyggy m365 files-backfill [--drive <name>] [--reset]` | `files-backfill.sh` | progress + counts line | 0 · 3 · 4 · 5 · 6 · 130/143 |

Exit codes are each script's codes for its verb (owner decision 5); they differ from the dream's table where the scripts did (usage 4, `remember` refusal 2).

Message texts (stderr first lines, refusal reasons, counts lines, progress lines) are the scripts' texts, with the script name prefix kept (`m365:`, `m365-guard:`, `facts:`, `parse:`, `mail-backfill:`, `files-backfill:`, `remember:`) and runbook names unchanged. One text change only: a usage line names the verb instead of the script.

### Configuration and file locations

| Key | Where | Default | Override rule |
|---|---|---|---|
| `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE` | env (27/28 names) | none → exit 3 (`TIMEZONE` → UTC) | Backfills only: unset keys taken from `<checkout>/.claude/settings.local.json` `env` (a set variable wins; no other key). |
| `ZYGGY_INSTANCE_DIR` | env; added to `instance/settings.local.json` `env` and to the units | else `$CLAUDE_PROJECT_DIR/instance`; else exit 3 | `m365.json`, `zyggy.json` live there (34 adds `github-inventory-exclude.txt`); the checkout = its parent. |
| `ZYGGY_M365_CONFIG` | env | `<instance dir>/m365.json` | tests and hand runs |
| `ZYGGY_SECRET_PATTERNS` | env (28) | `<instance dir>/../.claude/hooks/secret-patterns.txt` | missing → exit 3 |
| `ZYGGY_STATE_DIR` | env (28) | `~/.local/state/zyggy` | m365 state = `<state dir>/m365` (same path as today on Central) |
| key / certificate | `ZYGGY_M365_KEY_FILE`, `ZYGGY_M365_CER_FILE` | `${XDG_CONFIG_HOME:-~/.config}/zyggy/m365-app.{key,cer}` | credentials directory wins for the key; note the `headersHelper` environment drops `*KEY*` variables, so the helper always uses the defaults |
| `ZYGGY_M365_PORT` | env | 47365 | 1024–65535; `.mcp.json` and the unit must agree |
| `ZYGGY_CLAUDE_PATH` | env (28) | `claude` on `PATH` | the brief unit sets it |
| Tool partition | template `.claude/skills/m365/tools/{enabled,excluded,actions,auth}.txt` (+ the server version) — plain data files (owner decision 8) | — | template-owned data; changing it is a template commit with `repo.bats` |
| Minimum binary version | template `.claude/zyggy-min-version` | — | the instance pin must be ≥ it |
| Caps and policies | `instance/m365.json` (`brief`, `mail_backfill`, `files_backfill`, `actions`, `drives`) | as today | unchanged; constants of the scripts (retries 5, assertion 300 s, request 60 s, 2,000 delta pages, 240-character facts, MarkItDown 120 s / 2 GiB) stay constants in code — tightening them is a code change, as today |

Files and formats — **unchanged contracts** (read by skills, the dream, 23's evidence, runbook 13): `inbox/remember-<date>.md`, `inbox/m365-<kind>-<date>.md`; state dir `mail-watermark`, `backfill-<folder>.watermark`, `drive-<drive>.token`, `files-backfill-<drive>.watermark`, `replied-<date>.ids`, `actions.jsonl`, `brief.jsonl`, `brief-<date>.json`, `mail-backfill.json`, `files-backfill.json`; the run directories under `~/.cache/zyggy-m365-downloads`.

### Template after 33 (remaining shell, each with its R1 reason)

| File | Lines (target) | R1 reason |
|---|---|---|
| `.claude/hooks/session-start.sh` | ≤ 10 (28) | Hook launcher; a missing binary yields no section, the session continues. |
| `.claude/hooks/m365-guard.sh` | ≤ 10 | Hook launcher that turns a missing binary or any non-zero exit into exit 2 — the only code Claude Code treats as "block". |
| `.claude/hooks/m365-log.sh` | ≤ 10 | Same, for the action log. |
| `.claude/hooks/stop.sh` + `lib.sh` (trimmed) | as today | Scheduled for 12 (`zyggy context`); not complex-shell work of 33. |
| `.claude/skills/github-inventory/inventory.sh`, `.claude/skills/github-clone/{clone,askpass}.sh` | as today | Migrated by 34 (owner decision 3). |

Everything else under `.claude/skills/{m365,remember}/` is Markdown or data. `.mcp.json`: `"headersHelper": "zyggy m365 auth-header"`. Units (instance): `zyggy-m365-mcp.service` `ExecStart=/usr/local/bin/zyggy m365 mcp-server`; `zyggy-morning-brief.service` `ExecStart=/usr/local/bin/zyggy m365 brief` (timer stays disabled), both with `ZYGGY_INSTANCE_DIR` and the brief with `ZYGGY_CLAUDE_PATH`; hardening, `LoadCredential=` and paths unchanged.

### Parity table (written before any script is deleted; the plan ports each bats case or records it as obsolete with a reason)

| Script interface | Verb | Ported bats cases → .NET evidence (test classes indicative) | Earlier ACs re-proven |
|---|---|---|---|
| `remember.sh` args, line, file, exit 0/2/3/4, hooks-off | `memory remember` | `remember.bats` 15 → `RememberTests` (U golden), `RememberCommandTests` (I) | 27 AC-8, AC-9, AC-28 |
| `lib.sh` `zy_secret_match`, `zy_atomic_append`, `zy_collapse_line`, `zy_require_config` | shared .NET types | `SecretPatternsTests` (28) + shared sample fixture; `FactLineWriterTests` (U golden) | 27 refusal; 28 AC-11 |
| `facts.sh` | `m365 facts` | facts cases (4) → `FactValidatorTests`, `FactsCommandTests` | 23 AC-40..44 (facts) |
| `graph.sh` arg grammar, exit 4, removed D6 verbs | `m365 check/token-test/cert-init` | "unknown verbs" → `M365CommandParsingTests` | 23 AC-31 |
| `m365-lib.sh` config validation | `M365Configuration` | "misconfiguration", "cert.expires", "fixture validates" → `M365ConfigurationTests` | 23 AC-32 |
| `graph.sh` key lookup | `CredentialFileSecretStore` | "token reads `$CREDENTIALS_DIRECTORY`…" → `CredentialFileSecretStoreTests` (U + I) | 23 AC-33 |
| `graph.sh token` assertion, token POST, auth errors | `ClientAssertion`, `GraphTokenClient`, `m365 token-test` | "token", "assertion header", "RS256", "invalid_client" → `ClientAssertionTests`, `GraphTokenClientTests` | 23 AC-30, AC-17 (drill shape) |
| `graph.sh check`, read verbs, retries, curl rules | `GraphReader`, `m365 check` | "check", "check --counts", "mail-folders/drives", "drive-files", "429/503/401/403", "drafts-since/message-sender", "item-exists/item-kind", "env -i / proto / no key in argv", "stub refuses /me, DELETE…", "reads only" → `GraphReaderTests`, `GraphRetryTests`, `CheckCommandTests` | 23 AC-30, AC-34 |
| `graph.sh cert-init` | `m365 cert-init` | "cert-init", "existing key / --rotate / --commit", hooks-off → `CertificateInitTests` (U + I) | 23 AC-2 (shape) |
| `m365-guard.sh` | `m365 guard` + launcher | guard cases (3) → `GuardPolicyTests` (full matrix), `GuardCommandTests` (I), launcher bats (T) | 23 AC-35, AC-10..AC-12 (policy part) |
| `m365-log.sh` | `m365 log` + launcher | log case → `ActionLogTests` | 23 AC-36 |
| `verify.sh` | `m365 verify` | verify cases (4) → `DraftAuditTests`, `VerifyCommandTests` | 23 AC-39 |
| `mcp-auth-header.sh` | `m365 auth-header` | helper cases (2) → `HeaderHelperTests` (U), `AuthHeaderCommandTests` (I, timing) | 23 AC-49, AC-50 |
| `mcp-server.sh` (+ `--probe`) | `m365 mcp-server` | server, download root, http stub, probe cases → `McpServerLauncherTests` (fake server executable), `McpProbeTests` | 23 AC-51..AC-54, AC-48 |
| `mcp-wrapper.sh` | — (deleted) | wrapper cases → obsolete (stdio path retired; recorded) | 23 AC-52 (its removal) |
| tool partition arrays | template data + `M365ToolPartition` | partition cases (4) → `M365ToolPartitionTests` + `repo.bats` | 23 AC-37 |
| `state.sh` | `m365 state` | state cases (3) → `M365StateTests`, `StateCommandTests` | 23 AC-40 |
| `parse.sh` | `m365 parse` | parse cases (7) → `DocumentParserTests` (U), `ParseCommandTests` (I, fake `markitdown`) | 23 AC-41 |
| `brief.sh` | `m365 brief` | brief cases (8) → `BriefRunTests` (U), `BriefEndToEndTests` (I, fake-claude scenarios) | 23 AC-38, AC-13 (live: AC-42), AC-17 |
| `mail-backfill.sh` | `m365 mail-backfill` | mail-backfill cases (8) → `MailBackfillTests`, `MailBackfillEndToEndTests` (signal), `CheckpointCompatibilityTests` | 23 AC-19..AC-22, AC-42 |
| `files-backfill.sh` | `m365 files-backfill` | files-backfill cases (9) → `FilesBackfillTests`, `FilesBackfillEndToEndTests`, `CheckpointCompatibilityTests` | 23 AC-19..AC-22, AC-42 |
| settings/`.mcp.json`/hygiene contracts | template | `repo.bats` m365/remember rows rewritten for verbs | 23 AC-37, AC-45, AC-52 |

The `inventory.sh`, `clone.sh` and `askpass.sh` rows are in `_specs/34-central-github-tools-dotnet.md`.

---

## Behaviors & Conventions

- **Pure migration.** Every observable output, file and refusal of the scripts is reproduced; the only differences are listed under Deliberate deviations. No new capability. Override: none.
- **One refusal implementation in .NET.** Every .NET writer or output guard uses 28's `SecretPatterns` from the configured file; `stop.sh` (and the two GitHub scripts until 34) keep the bash copy, held equal by the shared sample fixture. Override: edit `secret-patterns.txt` (a commit), as today.
- **Fact lines are byte-identical** to the shell's; appends never re-render a file. Override: none (the dream hashes them).
- **Credentials are read at use** through `ISecretStore`, never cached, never placed in an argument list, a child's environment, a log, an exception message or a file; the single sanctioned token output is `auth-header`'s stdout to Claude Code (D8).
- **Hook-path verbs start fast** (no generic host, no Graph call unless the guard's policy needs one, at most two). Override: none.
- **Unattended rules** (`ZYGGY_HOOKS=off`): accepted by `check`, `token-test`, `auth-header`, `mcp-server`, `guard`, `log`, `verify`, `state`, `facts`, `parse`, `brief`; refused (exit 5) by `cert-init`, `mail-backfill`, `files-backfill`; `remember` exits 0 silently. Override: none (§8).
- **Model runs** (brief, backfills) go only through `IModelRunner`; their allow/deny lists come from the template's tool data plus the verb rules; the action tools are always denied there (D7). Override: caps and model in `instance/m365.json`.
- **Slash-command prompts (decision 7).** The `/morning-brief …`, `/mail-backfill …` and `/files-backfill …` prompts go on standard input (28's rule) if the check on Central shows that print mode expands a `/skill` command read from stdin; if it does not expand, these three runs — and only these — pass the prompt as the command-line argument, exactly as the scripts do today (the argument holds mailbox, folder or drive ids and, for the files backfill, file paths, visible to processes of the same user on the machine). The check result is recorded in the Decisions log (placeholder until the main session reports it). Override: none.
- **Install order on Central:** binary + pin first, then the template/instance pull, then the unit changes; rollback is the reverse (previous instance commit, previous binary — both kept). Override: none.
- **Parallel run with 28:** no installation or pin change on Central until 28's third night is recorded (a 28 fix excepted, and it goes first); shared code changes only additively with 28's suites green; the shell writers on Central are not replaced while nights are measured. The planner chooses additive-on-`main` or a branch.
- **Order with 34:** 34 starts after 33's Central evidence (AC-40..AC-43) is recorded; 33 leaves the GitHub scripts, their bats suites and the `lib.sh` functions they use untouched.

---

## Failure modes

| Situation | Observable outcome | Runbook entry |
|---|---|---|
| Binary missing on Central | Guard and log launchers exit 2 → every action blocked with `m365-guard: zyggy not found`; `headersHelper` fails → m365 tools unavailable; `remember`, skills' verbs → "command not found"; brief/mcp units fail `203/EXEC` | 14 "Binary missing or wrong version" (extended: m365 consequences) |
| Binary older than the template's minimum | Instance CI red before the pull; if forced: a verb missing → usage error (guard launcher → exit 2) | 14 "Binary missing or wrong version"; new "Template needs a newer binary" |
| Pin mismatch at a model-run verb | Exit 3 `version_mismatch` before any request | 14 |
| Key unreadable / wrong mode / missing | Exit 3 with the shell's message; helper → connection fails | 13 "Certificate rejected" (unchanged) |
| Graph auth failure, throttling, 403 | Exit 6 with the shell's message | 13 "Certificate rejected", "Throttling", "Scope or grant missing" (unchanged) |
| `prlimit` or `markitdown` missing | `parse` exit 3 naming it | 13 "MarkItDown" (extended) |
| `logger` missing | Helper works, no journal line | none (as today) |
| Guard crashes (runtime error) | Launcher → exit 2, action blocked | 13 "Guard refused / failed" (new wording) |
| Action log cannot append (lock or disk) | Exit 2 shown to Claude; the action already ran | 13 "Action without a log row" (reconcile with the audit log) |
| Backfill meets a checkpoint it cannot read | Exit 3 `… is not a checkpoint (… --reset starts again)` | 13 "Backfill resume" |
| `headersHelper` exceeds 8 s | Exit 6, connection fails, retried by Claude Code on the next call | 13 "Token refresh failed" |
| A slash-command prompt not expanded (stdin path chosen, behaviour changes in a later Claude Code) | The model answers the literal text; the brief's audit or the backfill's counts line check fails → exit 6 recorded | 13 "Brief or backfill run failed" (names the decision 7 fallback) |

---

## Dependencies

| Package | License | Why |
|---|---|---|
| none new | — | BCL: `HttpClient`, `System.Text.Json` source generation, `RSA`/`X509Certificate2`/`CertificateRequest`, `System.Security.Cryptography`, `PosixSignalRegistration`, `File.GetUnixFileMode`; existing: System.CommandLine 2.0.11, Microsoft.Extensions.* 10.0.12, YamlDotNet, Ulid |

Checked and rejected: `Microsoft.Identity.Client` 4.90.1 (MIT, maintained, 2 transitive packages — controls the assertion header and error mapping the parity tests pin); `Microsoft.Graph` / `Azure.Identity` (size, roadmap preference for the BCL); Polly / `Microsoft.Extensions.Http.Resilience` (packages for a 20-line loop with exact semantics); `Microsoft.Extensions.Http` (a short-lived CLI needs no `IHttpClientFactory`; tests inject the handler through DI). External programs (not packages): `markitdown` (pipx, MIT), `ms-365-mcp-server` (npm, MIT, pinned 0.157.2), `prlimit`/`logger` (util-linux, Ubuntu base).

---

## Deliberate deviations from the founding spec and the scripts (each behind an owner decision of 2026-10-05 or a decision above)

1. `graph.sh token` printing a bearer becomes `token-test` printing only size and expiry; the read verbs have no command-line surface.
2. `ZYGGY_M365_ORIGIN` dropped; test-only environment switches dropped from the binary.
3. MarkItDown's memory cap through `prlimit` instead of `ulimit -v`.
4. The brief and the backfills take their instance files from `ZYGGY_INSTANCE_DIR` (new key in the instance settings and units) instead of the script's own location.
5. The helper's timeout exit is 6 instead of 124.
6. The model-run verbs check the binary pin (28's rule), which the scripts could not.
7. `mcp-wrapper.sh` deleted without a .NET successor.
8. The m365 prompts on standard input instead of the command line — or, under the decision 7 fallback, no deviation (command line as today).
9. The GitHub part of roadmap #33 is delivered as 34 (decision 3); its deviations (no `gh`, the binary as askpass) are listed in `_specs/34-central-github-tools-dotnet.md`.

---

## Edge Cases

| Case | Expected behavior |
|---|---|
| Today's inbox file was created by the shell before the swap, appended by .NET after | Front matter kept byte for byte (only `updated:` value rewritten); lines identical in shape |
| A fact equal to one the shell wrote earlier today | Dropped as duplicate (`facts`), as today |
| `headersHelper` environment without `ZYGGY_M365_KEY_FILE` (Claude Code strips `*KEY*` names) | Default key path; works |
| Key in PKCS#1 (`BEGIN RSA PRIVATE KEY`) | Read; same assertion |
| Key is EC (`BEGIN EC PRIVATE KEY`) | Exit 3 (the shell failed later with exit 6) |
| `upload-file-content` call arrives at the guard | Checked by the upload rules (still excluded from the server; denied twice), as today |
| Guard receives an action tool while `actions.enabled` lacks it | Deny `action disabled for this instance` |
| Two Claude Code processes append to `actions.jsonl` at once | Exclusive advisory lock; rows never interleave |
| Signal during a backfill batch | Child tree killed, no checkpoint write, run directory removed, 130/143 |
| A plain-ISO files-backfill cursor written before 23's step 20a | Resumes at that second (whole tie group) |
| Windows build | Linux-only verbs exit 3 `not supported on this platform`; CI compiles and unit-tests the platform-neutral parts |

---

## Out of Scope

- New behaviour of any kind; changing a 23 decision (tool allowlist, token scopes, D7 consent by permission prompt, D8 header path, OneDrive only).
- The GitHub tools (`github inventory`, `github clone`, askpass), their adapter, the `GitClient` clone methods, the `github-read-token` entry of the credential store, runbook sections 11–12 and `restore-central.md`'s clone-cache line — all in `_specs/34-central-github-tools-dotnet.md`.
- `stop.sh`, the rest of `lib.sh` (12/13; the GitHub users of `lib.sh` → 34); `session-start.sh` (done in 28); the dream.
- Reimplementing the Softeria server or MarkItDown; the D8b ".NET proxy" fallback of 23.
- Switching the morning brief on (timer) — follow-up under 23; decision 2's single run is by hand.
- Repeating 23's full phone test list (decision 1 chose one live send).
- Moving or re-creating the application key.
- `zyggy init`, packages, image (26); the bus, the Hub (11, 21); Serilog (07); Windows behaviour of these verbs.
- Command-line verbs for the Graph read operations; a bearer-printing `token` verb; a built-in copy of the tool partition.
- Template CI downloading the real binary.

---

## Risk Areas (⚠️)

- **Security controls rewritten** — the guard, the client assertion, the per-connection token, the action log: parity tests written before deletion, the full guard matrix, a risk review at each slice gate, the fail-closed launchers, the agent's refusal probe and the owner's live send on Central (decision 1).
- **Secrets** — the key file is read by new code; `ISecretStore` checks (mode, owner, header); no token in argv/env/logs proven by spies and the Central sweep; Bash deny rules for the token-producing verbs (prefix rules can be bypassed by an absolute path — same posture as today with `graph.sh`).
- **Shared contracts** — fact lines (dream ledger), state/checkpoint/receipt/log files (skills, 23 evidence), `IModelRunner`/`ProcessSpec` (28, 06): additive only; golden fixtures.
- **Parallel run with 28** — install gated on the third night; 28 fixes first.
- **Template ↔ binary versioning** — minimum-version file + instance CI; install order binary → pull.
- **Hook latency** — a binary start per guarded call and per connection (well inside the 10–20 s timeouts).
- **Claude Code behaviour** — whether a prompt piped on stdin expands `/skill` commands is checked on Central (decision 7, fallback specified); the `headersHelper` shell execution and credential-variable stripping are documented (checked 2026-10-05).
- **Size** — about 3,900 lines of shell and about 109 bats cases to port (the GitHub 480 lines / 71 cases are 34's).
- **Work boundary** — unchanged: Central only, the owner's company tenant; nothing of the employer's tenant.

---

## Founding-spec amendments (accepted by the owner 2026-10-05, decision 4)

Accepted as written. Application to `_specs/00 - Personal Agent Platform — Technical Specification.md` is **pending — the owner applies them** (the analyst never edits the founding spec). W33-3, W33-4, W33-8 and the GitHub parts of W33-1, W33-5, W33-6, W33-7 describe 34's verbs; they are accepted here and referenced from `_specs/34-central-github-tools-dotnet.md`.

| # | Lands in | Accepted text |
|---|---|---|
| W33-1 (O36 a) | §3 Components, `AgentBus.Cli` row; §9 tree, `AgentBus.Cli/` line | verbs gain `memory remember`, `m365 check \| token-test \| cert-init \| auth-header \| mcp-server \| guard \| log \| verify \| state \| facts \| parse \| brief \| mail-backfill \| files-backfill`, `github inventory \| clone`. (The §3 intro sentence "… Markdown plus thin launchers that call `zyggy`" is already applied by 28.) |
| W33-2 (O36 b) | §8 Secrets, Graph certificate row, access column | "`zyggy m365` verbs only (the application identity mints one-hour tokens with a client assertion; reads only); Claude Code receives a token from `zyggy m365 auth-header` on every connection …" (replaces "`m365/graph.sh` only" and "a helper that calls `graph.sh`"). |
| W33-3 (O36 b) | §8 Secrets, GitHub read-token row; §8 Isolation | Access: "`zyggy github inventory` and `zyggy github clone` only (the token is sent as a bearer header to the GitHub API by the binary; git receives it only through the binary acting as a host-checked askpass helper that reads the file at prompt time)"; Isolation: "read only by the two `zyggy github` verbs" (replaces "the two skill scripts", "exported to their `gh` children"). Follows decision 6 (drop `gh`). |
| W33-4 (O36 b) | §11 `restore-central.md` | "empty it with `zyggy github clone --clean`". |
| W33-5 (O36 c) | §9 Design rules, after the five-seams sentence | "Adapters for owner services that are not seams (Microsoft Graph, the GitHub REST API) live behind internal interfaces in `Zyggy.Core` and are the only code that references those services; they are not pluggable edges." |
| W33-6 (O36 d) | §9 tree, `AgentBus.Core/` | add `M365/` (Graph identity and reads, guard, audit, state, document parsing, brief and backfill runs) and `GitHub/` (read adapter, inventory, clone, askpass). |
| W33-7 (O36 e) | §12 agent split | `core-dev` gains "`M365/`, `GitHub/`"; `infra-dev`'s "M365 MCP wiring" becomes "M365 MCP units and instance settings"; `skills-dev` keeps the skills and the hook launchers. |
| W33-8 | §3 Skills rows `github-clone` / §1 | "(…, git credential only through the `zyggy` binary as a host-checked askpass helper)". |

---

## Decisions log

All eight Open Questions were **decided by the owner on 2026-10-05** (verbatim: "I accept all your recommendations"):

1. **Live proof of the action path — (b).** The agent proves on Central that an out-of-policy send (a hidden Bcc recipient to the owner's own address) is refused by the guard and nothing is sent (AC-41); **and** the owner does one real send from the phone to the owner's own second address — one permission prompt, allowed once, and one row in `actions.jsonl` written by the new code (AC-42, owner-run).
2. **One morning-brief run on the new binary — (a).** The agent runs `zyggy m365 brief` once by hand on Central (attended, not the timer); it leaves a "Zyggy — morning brief" Draft (possibly reply Drafts); the timer stays off (AC-42).
3. **Split — (b).** 33 = memory + Microsoft 365; the GitHub sections moved unchanged into `_specs/34-central-github-tools-dotnet.md`, built right after 33. Until 34, the GitHub scripts keep working on the remaining `lib.sh`. The roadmap entry for 34 is recorded by the project manager.
4. **Founding-spec wording O36 (a)–(e) — accepted as written.** W33-1..W33-8 above; the owner applies them to `_specs/00 …`.
5. **Exit codes — (a).** Each script's codes kept for its verb (usage 4, `remember` refusal 2) (Contracts, AC-6).
6. **Drop `gh` — (a).** The binary calls the GitHub API itself; lands in 34 (W33-3 follows it).
7. **Slash commands on stdin — (a) with (b) as the accepted fallback.** Check on Central, then fall back to the command-line argument for those runs (`/morning-brief`, `/mail-backfill`, `/files-backfill`) if a `/skill` prompt on stdin does not expand; 28's stdin rule stays for every other run.
   **[PLACEHOLDER — decision 7 check result]** Result of the stdin slash-command check on Central: _pending — the main session replaces this sentence with "expanded (stdin kept, no command-line switch)" or "not expanded (command-line fallback for the three m365 runs)", plus the date and the Claude Code version checked._
8. **Tool allow/deny lists — (a).** Plain data files in the template, read by the binary at run time and checked by the template tests; no copy in the binary (AC-27).

## Open Questions

None. **Next action:** invoke the `planner` subagent with this spec to produce `_plans/33-central-tools-dotnet.md`. The founding-spec amendments are applied by the owner and do not block the planner; the decision 7 placeholder is filled by the main session (both outcomes are specified, so the planner can plan the optional switch as conditional).
