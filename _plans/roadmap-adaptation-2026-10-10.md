# Zyggy roadmap adaptation — 2026-10-10 (v2)

Written by Zyggy (Central) from a review of every daily note and inbox line from 2026-09-30 to 2026-10-10,
plus the owner's requests in the conversation of 2026-10-10. For the project manager agent of the Zyggy
project. Deliverable numbers below are proposals (the template is at deliverable 36/36b); renumber freely.

**v2 (same day):** X posting removed (owner decision); the secrets family moved down; the order now minimises
effort and rework and puts the high-ROI, low-cost items first. Deliverable numbers are kept as stable
identifiers, so they are no longer in build order — section 2 gives the order.

Everything in this document is a proposal for the owner's own project; nothing has been changed on Central.

---

## 1. Why

In eleven days of use, the owner asked for things Zyggy could not deliver in four families:

1. **Actions outside the current tools** — mail with attachments, Billit, spam triage, X posts, the RIZIV mailbox.
2. **Unattended work the owner defines himself** — "every morning this week, a report on the new items on
   site X", "watch Y and alert me when Z", a daily news summary. Today only two fixed timers exist (brief, dream),
   both wired by hand on the workstation.
3. **Learning from mistakes** — corrections become preference lines, but errors (a wrong tax rule on 10-09, a
   misattributed quote the same day) leave no trace and can repeat.
4. **Secrets** — Zyggy refuses or drops every secret it meets. The owner wants them kept, in Azure Key Vault,
   referenced from memory, and usable by Zyggy without ever entering a conversation.

The owner's words (2026-10-10): "I would also like that you can learn and correct yourself. I also expect that
you are more proactive and can run new things I ask you in background and on schedule." / "What I would like
is that when you encounter a secret (from a file or on web) instead of dropping or refusing it you store it in
keyvault with link to it."

## 2. Proposed deliverables, in build order

Ordering rules used: (a) small items with a daily or monthly payoff first; (b) changes to one component are
grouped so it is touched once (the brief, the dream pass, the binary's secret layer); (c) anything that will be
an *instance* of a later mechanism waits for the mechanism (news summary and website watch wait for Tasks)
rather than being built twice; (d) secrets come after the features that do not need them, and Billit follows
secrets because it cannot exist without a credential.

| Order | # | Deliverable | Family | Unlocks | Size | Depends on |
|---|---|---|---|---|---|---|
| 1 | 51 | Parser fixes: Luhn check before refusing card-like numbers; images in mail bodies | 1 | correct invoice reading (DKV 10-05, Frank 10-06) | S | — |
| 2 | 43 | Mail Drafts with attachments and a named recipient | 1 | the monthly timesheet + invoice mail | S | — |
| 3 | 44 + 47 | Brief, one pass: `suspected spam` class with an "I can do: move" item, and a "Noticed" section | 1, 2 | spam handling, proactive findings | S+S | — |
| 4 | 41 + 50 | Dream pass, one pass: Lessons (`correct` skill, `lessons/`, review step) and the Zyggy todo file | 3 | learning; the todo lines become a file | M+S | — |
| 5 | 38 | Tasks: owner-defined scheduled and background runs (channel = a line in the brief) | 2 | news summary, website watch, weekly self-audit, alert reports | L | — |
| 6 | 42 | Alert channel step 2: a mail Draft to the owner (step 3 push where available) | 2 | "alert me when …" off the brief's schedule | S | 38 |
| 7 | 46 | Standing permissions for routine actions | 2 | archive/move without a prompt each time | S | 44 helps |
| 8 | 49 | LinkedIn stats intake | 1 | post metrics in memory | S | — |
| 9 | 53 + 52 | File intake folder; proposal outbox | — | fewer failed uploads; faster template changes | S+S | — |
| 10 | 37 | Secrets: Azure Key Vault, policies, references, consumers | 4 | any new integration | M | — |
| 11 | 45 | Billit read-only connector | 1 | invoice checks, Billit in the brief and in tasks | M | 37, 38 |
| 12 | 39 | Secrets: OneDrive drop-folder import | 4 | entering secrets from the phone | S | 37 |
| 13 | 40 | Secrets: scanner with quarantine (capture instead of refuse) | 4 | the owner's capture request | M | 37, 51 |
| — | — | Work-boundary rules, laptop jobs, RIZIV mailbox | 1 | already planned | — | — |

Removed: **48 X (Twitter) posting** — owner decision 2026-10-10. The posting plan's X side stays manual; the
`linkedin-posting.md` memory file should say so.

Why the secrets family sits at 10–13 although the owner chose Key Vault: nothing in items 1–9 needs a new
credential (they run on the Microsoft grant, the brief and the dream pass that already exist), so building 37
first would delay nine small, daily-use items for a foundation only Billit uses today. 37 is still built as one
piece before 45, so Billit is not wired to a one-off credential and reworked later. The existing three
credential files move into the vault as part of 37, not before.

What waits for Tasks (38) on purpose, to avoid building it twice: the morning news summary (10-09 request), the
website watch, the weekly self-audit of 41. Each is a task file, not a feature.

---

## 3. Deliverable 37 — Secrets in Azure Key Vault

**Decision by the owner (2026-10-10):** Azure Key Vault; entering secrets on the VM is not practical.

### Principle

Zyggy (the model) handles **references, never values**. A value that enters the conversation is in the
transcript and in remote-control traffic, i.e. exposed. The `zyggy` binary is the only thing that reads a
value, exactly as it does today for the Microsoft certificate, the LinkedIn token and the GitHub token.

### Components

- One Key Vault for Zyggy only (`kv-zyggy-central` or similar), nothing else in it.
- Central's VM gets a **managed identity** with `Key Vault Secrets User` (read). `Secrets Officer` only for
  the verbs that write (39 import, 40 scanner), scoped to that vault.
- `instance/secrets.json` — vault name and one **policy per secret**, no values:
  `name`, `consumers` (`http` | `mcp` | `git` | `run` | `browser`), `hosts`, `verbs` (`GET` …), `program`
  (for `run`), `unattended` (bool), `expires`, `origin` (`owner` | `found` | `third-party`), `status`
  (`active` | `quarantine`).
- **Consumers** in the binary (the only ways a secret is used):
  - `zyggy http --secret <name> <method> <url>` — the binary adds the header/query parameter, returns the
    response scrubbed of any secret value.
  - `.mcp.json` `headersHelper` — per-connection credential, as `m365` does today.
  - `askpass` — git over HTTPS, as `github-clone` does.
  - `zyggy run --secret NAME=<ref> -- <program>` — only a program named in the policy; stdout/stderr scrubbed.
  - `zyggy browser login <site>` — the binary types the credentials into the headless browser; **attended
    only**, per-site policy, dedicated accounts where possible; never the owner's bank, Microsoft, Google or
    GitHub accounts (API routes exist for those).
- `zyggy secret policy add|show|list|revoke` — `show` prints name, version, created date, tags; never a value.
- Guard: `secret-guard: refused: <reason>` for any use outside the policy; one audit row per use in
  `~/.local/state/zyggy/secrets/uses.jsonl` (secret, consumer, host, attended, session id, time).
- Settings deny for the model: `az keyvault *`, `az account get-access-token`, `bw`, `op`, `keepassxc-cli`,
  reads under `~/.config/zyggy/`.
- Expiry warnings in the brief ("secret X expires in 14 days"), as for the certificate.
- Runbook entries: "Add a secret" (Portal path), "Secret refused", "Revoke a secret", "Reconcile uses with the
  Key Vault audit log", "Managed identity missing".

### Entering a secret, way 1 (this deliverable)

The owner adds it in the Azure Portal or the Azure mobile app (Key Vault → Secrets → Generate/Import), then
tells Zyggy the name and policy in the conversation; Zyggy runs `zyggy secret policy add`, which checks the
secret exists and writes the policy. The value never touches Zyggy at all.

### Rule changes

`security.md` "Never store": passwords/keys/tokens stay forbidden in memory, files and notes; the sentence
gains "they go to Key Vault through the `zyggy secret` verbs only". A secret pasted into the conversation is
refused by `remember` as today **and** Zyggy tells the owner to rotate it (the transcript has it).

---

## 4. Deliverable 38 — Tasks: owner-defined scheduled and background runs

**Owner's examples:** "deliver me this week every morning a report about the new items appearing on this
website"; "monitor this and send me an alert when …"; the daily news summary (IT, Belgian and international
news, finance, freelancers, taxation of management companies — stated 2026-10-09).

### Components

- `tasks` skill: the owner describes the task in the conversation; Zyggy writes
  `~/.local/state/zyggy/tasks/<id>.json` — `id`, `title`, `schedule` (cron, Europe/Brussels), `prompt`,
  `sources` (URLs, mailbox folders, a Billit query), `condition` (for alerts), `start`, `end` (default 7
  days for "this week"; no task is open-ended without an explicit end), `channel` (see 42), `secrets` (names
  from 37, must allow `unattended`), `state` (what was seen last run). The owner confirms the task card;
  Zyggy then runs `zyggy task install <id>`, which creates the systemd timer (`zyggy-task-<id>.timer`).
- `zyggy task list|show|pause|resume|delete|run-now <id>`; `/tasks` in the conversation shows them.
- Runner: `zyggy task run <id>` → `claude -p --no-session-persistence --permission-mode auto` under
  `ZYGGY_HOOKS=off`, with the task's prompt and its state file; headless browser, **public sites only** (no
  cookies, no logins — the existing unattended rule), `http` consumer for APIs with an unattended policy.
  "New items" = diff against the state file; the runner stores the new state only after a successful run.
- Output: `~/.local/state/zyggy/tasks/<id>/report-<date>.md`, 0600, kept 14 days; the brief shows a one-line
  summary per task and `zyggy task show <id> <date>` prints a report. Alerts go through 42.
- Guardrails: run cap per task (one run at a time, max N pages and M minutes), the sources are data (a page
  that tells the run to do something is reported in the report, never followed), no sending/posting ever from
  a task, no `remember` from a task (facts proposed in the report, the owner confirms), one journal line per
  run in `tasks/journal.jsonl`, failures surface in the brief ("task X failed: …").
- Runbook entries: "Task run failed", "Task refused" (policy), "Too many tasks" (cap), "Remove a task".

### First instances

1. Morning news summary (the 10-09 request) — daily 06:00, public news sites, section in the brief.
2. A website watch ("new items on site X this week") — the owner's example.
3. Spam triage (44) — the brief's new `suspected spam` class plus an "I can do: move N mails" item; or a task
   that lists candidates. Moves still need the owner's prompt unless 46 grants a standing permission.

---

## 5. Deliverable 39 — Secrets: OneDrive drop-folder import

For entering secrets from a phone note instead of the Portal.

- Folder `OneDrive/Zyggy/secrets-inbox/`, one file per secret, file name = secret name, value = the only line.
- `zyggy secret import` (attended only, denied under `ZYGGY_HOOKS=off`): the binary lists the folder through
  its own Graph access, reads each file in memory, writes it to Key Vault, prints `name, version, sha256
  prefix` — never the value. Files > 4 KB or with more than one line are refused.
- A file whose name has no policy is skipped unless the owner gives the policy in the same turn.
- The OneDrive grant stays `read`, so the binary cannot delete the files: Zyggy reminds the owner, and the
  brief warns while files remain ("2 files still in secrets-inbox").
- The folder is a dead end for the model: `search-onedrive-files`, `list-folder-files`,
  `download-bytes-to-file` refuse any path under it (same path filter as the mail guard).

---

## 6. Deliverable 40 — Secrets: scanner with quarantine

**Owner's request:** when Zyggy encounters a secret in data (a file, a web page, a mail), store it in Key
Vault with a link to it, instead of dropping or refusing it.

### Where the capture happens

The value must be swapped for a reference **before the content reaches the model**. Three cases:

| Source | Capture point | Outcome |
|---|---|---|
| Mail bodies and attachments, OneDrive files (`zyggy m365 parse`), GitHub clones, task fetches | the binary / the parse step | clean: value never seen |
| Browser pages in a live session (`playwright`) and other MCP tool results | a tool-result rewriting hook **if Claude Code supports it** (to verify in the build), else routing fetches through the binary; otherwise today's rule stays (report, don't store) | capture or report |
| Already in front of the model (pasted, or shown before the filter) | none | store anyway with tag `exposure: transcript`, tell the owner to rotate |

### Behaviour

- Patterns: the existing `secret-patterns.txt` plus a Luhn check (51), `password:`/`.env` forms, IBANs.
- Each hit → Key Vault as `found-<date>-<source>-<4 hex>` with tags `source` (sender+date, path or URL),
  `pattern`, `found`, `origin` (`owner` | `third-party`), `status: quarantine`, `exposure`.
- In the text the model receives, the value becomes `secret://found-…`.
- Memory line (safe by construction): `[observed] <date>: <source> contained a <pattern>, stored as
  secret://found-… (quarantine)`.
- Quarantine has **no policy**: no consumer accepts it. The owner names it ("that's `billit-api-key`, host
  `api.billit.be`, read only") or discards it in the conversation; anything still in quarantine after 14 days
  is deleted and the brief says so.
- `origin: third-party` (a client's mail, someone else's repository) is flagged in the brief: "should this be
  here at all?" — the owner's rule that RIZIV confidential data lives only in his Zyggy memory is respected
  (a reference in memory, a vault he owns), but he decides.
- Audit row per capture with the source, so "where did this come from" is always answerable.
- The `Stop` hook and `remember` keep refusing secrets stated in the conversation; those go through the
  `exposure: transcript` route, never into a memory line.

---

## 7. Deliverable 41 — Lessons: learning and self-correction

Today a correction becomes a `[stated]` preference line (works for rules); a mistake leaves no trace.

- `correct` skill ("that's wrong, …", "no, Geoffrey, …"): like `remember`, but records both the right fact
  and what Zyggy got wrong, into `inbox/correct-<date>.md`.
- New memory category `business/lessons/`, `private/lessons/` (filed by the dream pass): one line per lesson —
  what was said, what was right, the rule drawn. The `index` digest section lists the most recent lessons at
  session start.
- Dream-pass step: scan `daily/` for denied prompts, "nothing was published", "no, Geoffrey", retractions
  ("one correction: …") and propose lessons; file the confirmed ones.
- Weekly self-audit (a task from 38 once Tasks exist, Sunday; until then on request through the `dream` skill): a short note "what I got wrong this week, what changed", the
  owner confirms or strikes each lesson in the conversation.
- Memory rule change: a third category of writer for `lessons/` (the `correct` skill via inbox, as for
  `remember`); tags stay `[stated]`/`[observed]`.

---

## 8. Deliverable 42 — Alert channel

For "alert me when …". Three steps, cheapest first:

1. A line in the next morning brief (free, with 38).
2. A Draft in the owner's Digiverse mailbox (`create-shared-mailbox-draft` is allowed already), readable on
   the phone; subject `Zyggy alert: <task>`. Unattended allowed because a Draft to the owner is not a send.
3. A push notification through the remote-control session where available (Claude Code's `PushNotification`),
   for attended sessions.
4. Later: Telegram (already on the roadmap as "does not exist yet").

---

## 9. Smaller deliverables (43–53, 48 removed), one paragraph each

- **43 Drafts with attachments** — `create-shared-mailbox-draft` extended (or a new verb through the binary)
  to a named recipient with OneDrive files attached; stays a Draft, the owner sends. First use: the monthly
  timesheet + invoice mail to MBX-Kapture-Admin (10-05).
- **44 Spam triage** — a `suspected spam` class in the brief (the spoofed AWS mails of 10-05 are the example)
  with an "I can do: move N mails to Suspected spam" item; the folder is created by the owner once.
- **45 Billit read-only** — `http` consumer with `billit-api-key` (37), host `api.billit.be`, `GET` only;
  invoice status in the brief; "check the invoices" in a conversation. Owner's todo of 10-05.
- **46 Standing permissions** — `instance/standing.json`: actions the owner pre-approves ("archive
  newsletters older than 30 days", "move DMARC reports to Dmarc"); the guard allows them without a prompt,
  every use still logged; the owner edits the file on the workstation.
- **47 "Noticed" in the brief** — short-term findings Zyggy made without being asked (certificate wrong on
  vandiest.biz 10-05, DMARC `p=none` 10-08, a tax due): a section next to "For the long run".
- **49 LinkedIn stats intake** — the owner pastes views/reactions/comments; a small verb stores them under
  `business/areas/linkedin-posting.md` per post; no scraping (LinkedIn terms).
- **50 Zyggy todo file** — the dream pass maintains `private/areas/zyggy-todo.md` from "todo" lines; `/todo`
  shows it. Replaces the loose "Zyggy todo (missing feature)" inbox lines of 10-05.
- **51 Parser fixes** — Luhn check before refusing card-like numbers (Medicard numbers on the DKV invoice,
  10-05); OCR or a model pass on images in mail bodies (Frank Energie consumption chart, 10-06).
- **52 Proposal outbox** — `~/.local/state/zyggy/outbox/` for specs and diffs Zyggy writes; the workstation
  pulls it (rsync/scp by the owner), so template changes no longer travel as chat attachments.
- **53 File intake** — a known folder on Central for files the owner sends from the phone or claude.ai
  (template, images), listed by `/inbox`; the 10-08 template upload failed the first time for lack of this.

---

## 10. Cross-cutting rule changes (for `zyggy-core`)

- `security.md`: secrets paragraph (37, 40); tasks never send/post/`remember` (38); logins attended only,
  dedicated accounts, named exclusions (37 `browser`).
- `memory.md`: `lessons/` category and the `correct` skill (41); `tasks/` reports are state files, not memory.
- `operations.md`: the `zyggy task`, `zyggy secret` verbs and exit codes; `ZYGGY_HOOKS=off` behaviour for
  tasks (runs allowed, actions denied); new runbook entries.
- `instance.md` (Central): vault name, managed identity, task cap, alert channel in use.

## 11. Open questions for the owner

1. Vault: a new one for Zyggy only (recommended), or an existing Digiverse vault with a `zyggy/` prefix?
2. Alert channel for the first version: brief line only, or also the mail Draft?
3. Task defaults: end date for "this week" = 7 days; max runs per day per task; max tasks (suggest 10).
4. Browser logins: allow at all in this phase, or defer with the work-boundary rules?
5. Third-party secrets found in client mail: store in quarantine and ask (proposed), or never store?
6. Billit before or after the secrets layer: the order above says after (one credential path, no rework); if
   Billit is wanted sooner, 37 can be split into a minimal "vault + `http` consumer + policy add" first slice.
