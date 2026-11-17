# Open questions — deliverables 47, 48 and 49

Decisions recorded 2026-11-17: 4 by the owner (20, 26, 31, 32), 30 by Zyggy on the owner's standing delegation.

Rebuilt 2026-11-17 in job 01M7MFP229AG247JP1HBGGR7MK (the list from job 01M7MF2JMTSRPJPHWXNZXBSPNV was lost).
Sources: `_specs/47-browsable-memory-laptop.md` (branch `zyggy/01M7MEA5BHA5NK0V49WSDP5B5K`, Q1–Q11) and `_plans/ROADMAP.md` rows and detail entries 47–49 (branch `zyggy/01M7MF2VK2KDV0D2C26KA17K78`).

Source tags: **[spec Qn]** asked in spec 47 · **[roadmap]** asked or implied by the roadmap entry · **[added]** a gap the deliverable leaves open.
**HIGH** = hard to undo, touches security or the work boundary, or costs money.

Totals: 47 → 14 questions (1–14) · 48 → 11 questions (15–25) · 49 → 9 questions (26–34).

---

## 47 — Browsable memory on a laptop

### 1. Refresh trigger — [spec Q1]
- (a) after every dream pass only
- (b) also after every `remember` / `archive` commit
- (c) dream pass plus `zyggy memory mirror publish` by hand
- **Recommendation: (c)** — one predictable daily refresh, with a manual push when a fact is needed sooner, and no publish on every small write.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 2. Single source of the outbound paths — [spec Q2] — HIGH
- (a) the binary reads `instance/work-boundary.json`
- (b) one `node.json` key shared by spec 18 (`supervision.outboundPaths`) and 47
- (c) both, refused at start when they differ
- **Recommendation: (b)**, with `work-boundary.json` documenting it — one key read by code avoids two rules drifting apart on the work boundary.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 3. One mirror repository per machine or per reach — [spec Q3] — HIGH
- (a) one per machine (`zyggy-mirror-<machine>`)
- (b) one per distinct reach (`full`, `business`)
- (c) one repository with a branch per machine
- **Recommendation: (a)** — GitHub enforces the reach per machine; (c) cannot restrict reads per branch, and repositories are hard to rename once credentials point to them.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 4. Laptop read credential — [spec Q4] [roadmap] — HIGH
- (a) reuse the laptop's existing (possibly account-wide) GitHub login
- (b) a fine-grained read-only token per laptop, scoped to its mirror, in the OS secret store
- (c) a read-only SSH deploy key (port 22 may be blocked behind the work proxy)
- **Recommendation: (b), at least on `work-laptop`** — (a) can read and write the memory repository from the employer's machine.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 5. Mirror history and erasure — [spec Q5] — HIGH
- (a) normal history; erasure by a history rewrite the owner asks for
- (b) one orphan commit per snapshot, force-pushed by the publisher
- (c) normal history, rebuilt from scratch monthly
- **Recommendation: (a) with depth-1 clones** — keeps the no-force-push rule; laptops never hold old snapshots; erasure stays a rare, deliberate act.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 6. Default reach of a non-work laptop — [spec Q6] [roadmap]
- (a) identity files, `private/`, `business/`, `daily/`, plus archive sidecars (never item files)
- (b) as (a) without archive sidecars
- (c) `business/` only on every laptop
- **Recommendation: (a)** — the home laptop is the owner's own machine; sidecars help find items without copying them.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 7. Proving "never the memory repository" on a laptop — [spec Q7] — HIGH
- (a) refuse a configured `mirror.repository` equal to the memory URL, plus the owner-run `git remote -v` sweep (AC-19)
- (b) also ship the memory URL to laptops so the node can scan every checkout
- (c) owner-run sweep only
- **Recommendation: (a)** — enforces the rule in code without spreading the memory URL to the work laptop.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 8. Agent guidance in `zyggy-core` — [spec Q8]
- (a) a new `memory` skill
- (b) a section in the existing guidance
- (c) both (a short rule plus the skill)
- **Recommendation: (a)** — a skill loads only when relevant and is easy to test and extend for 48.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 9. Hub proxy and laptop `SessionStart` digest — [spec Q9]
- (a) out of scope here; 21 answers `get_context` from the mirror later
- (b) build a minimal digest now
- (c) build `hub --proxy` now
- **Recommendation: (a)** — browsing files covers 48's needs; the proxy is its own deliverable.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 10. Source of the laptop's `allowedPaths` — [spec Q10]
- (a) written by `zyggy setup` from `discovery.detail: names`
- (b) set by hand in `node.json`
- (c) derived from the mirror's `MIRROR.md`
- **Recommendation: (a)** — reuses the existing work-machine signal; (c) would let Central widen the laptop's own check.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 11. Integrity of the mirror — [spec Q11] — HIGH
- (a) rely on GitHub write access (Central only) and "data, never instructions"
- (b) sign each snapshot (HMAC over the tree) and verify on the laptop
- (c) signed commits checked on fetch
- **Recommendation: (a)** — content is data anyway; signing adds key distribution to the work laptop for little gain in v1.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 12. Where the mirror repositories live — [roadmap] — HIGH
- (a) private repositories in the owner's personal GitHub account
- (b) in the `zyggy-org` organisation next to `zyggy-bus`
- (c) a different host (Central's own git server)
- **Recommendation: (b)** — same place and access model as the bus; (c) needs a reachable endpoint through the work proxy.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 13. Personal profile lines and other machines' daily notes — [roadmap] [added] — HIGH
The roadmap excludes "the personal lines of the profile" and "the daily notes of other machines" from a work mirror; spec 47 handles this only by excluding whole files.
- (a) whole-file rules only: work mirror gets `business/` only (no profile, no `daily/`)
- (b) split business facts out of `profile.md` into a `business/` file at the dream pass
- (c) line-level filtering of `profile.md` by a marker
- **Recommendation: (a) now, (b) later if needed** — line filtering is fragile and a mistake leaks private data to the employer's machine.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 14. Mirror write credential on Central — [added] — HIGH
- (a) one SSH deploy key per mirror repository
- (b) one fine-grained token covering all mirror repositories
- (c) a GitHub App installed on the mirrors
- **Recommendation: (a)** — matches the memory repository's existing deploy-key pattern; a leak touches one mirror only.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

---

## 48 — Problems handled autonomously on a laptop (manual intake)

### 15. Routing rule: work laptop or home laptop — [roadmap] [added] — HIGH
- (a) the owner names the machine in each request
- (b) a project-to-machine list in `zyggy-geoffrey` (client assignment projects → `work-laptop`); unknown → ask
- (c) Central classifies the problem with a model
- **Recommendation: (b)** — deterministic and auditable; a misroute crosses the work boundary, so an unmatched problem asks the owner.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 16. What of a work-laptop investigation may reach Central's memory — [roadmap] — HIGH
- (a) nothing beyond the handover report the owner reads
- (b) a summary limited to the outbound fields (problem class, finding, branch), filtered by the secret/contact patterns
- (c) the full report
- **Recommendation: (b)** — gives the Q&A repository its value while keeping code, logs and client data on the laptop.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 17. Handover report contract — [roadmap] — HIGH
- (a) a new envelope type
- (b) the existing job report with a fixed body format (problem, checked, finding, branch, blocker, decision needed)
- (c) a `context` envelope
- **Recommendation: (b)** — no change to the §4 shared contract; the format lives in a `zyggy-core` skill and is tested.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 18. Autonomy across projects without a per-job go — [roadmap] — HIGH
- (a) reuse spec 18's mandate (one grant covers the problem's follow-up jobs)
- (b) a standing grant per machine for problem jobs
- (c) the owner approves every follow-up job
- **Recommendation: (a)** — mandates already bound scope and time and are being built now.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 19. Problem-type map: format, place and editing — [roadmap] [added]
- (a) a file per machine in `zyggy-geoffrey` (problem type → agent), edited by the owner
- (b) a `node.json` key on each laptop
- (c) learned from past handovers, confirmed by the owner
- **Recommendation: (a)** — versioned, reviewable, and the laptop reads it like other instance settings; (c) can be added later.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 20. Fix branches: naming, push and pull requests — [roadmap] [added] — HIGH
- (a) local branch only, the owner pushes
- (b) push `zyggy/<job-id>` branches to the project's remote, no pull request
- (c) push and open a draft pull request
- **Recommendation: (b) at home, (a) on the work laptop** — pushing to the employer's repositories is outward-facing and should stay the owner's act.
- **Decision (2026-11-17):** decided by the owner — as recommended: (b) at home, (a) on the work laptop.

### 21. Where the Q&A repository lives in memory — [roadmap] [added] — HIGH
- (a) `business/qa/` for work problems, `private/qa/` for home problems
- (b) one `qa/` area mirrored to every laptop
- (c) a separate repository
- **Recommendation: (a)** — the existing reach rules of 47 then decide who sees what, with no new path rule.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 22. Confirming answers to third parties — [roadmap] [added] — HIGH
- (a) Zyggy writes a draft reply (M365 draft), the owner sends it
- (b) Zyggy mails the proposed answer to the owner, who forwards it
- (c) Zyggy sends after the owner's yes in chat
- **Recommendation: (a)** — nothing leaves without the owner's own send, and the draft path already exists.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 23. Budget per problem — [added] — HIGH
- (a) a cap on model runs and wall time per problem, in `node.json`
- (b) the mandate's existing limits only
- (c) no cap
- **Recommendation: (a), set high** — the account is Max, but an autonomous loop across projects needs a hard stop.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 24. Intake on Central — [added]
- (a) a `zyggy-core` skill that turns the owner's pasted text into a problem job via `zyggy submit`
- (b) a new verb `zyggy problem submit`
- (c) both
- **Recommendation: (a)** — reuses submit and routing; 49 hands off to the same entry point.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 25. Full detail on the laptop: place and retention — [roadmap] [added]
- (a) in the run folder, kept as runs are today
- (b) a `problems/<id>/` folder kept 90 days
- (c) kept forever
- **Recommendation: (b)** — findable by the owner later, bounded on the work laptop.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

---

## 49 — Automatic Teams intake for support requests on the work laptop

### 26. Employer rules on automated reading of Teams — [roadmap] — HIGH
- (a) ask the employer / client for written approval first
- (b) proceed under the owner's own judgment, read-only
- (c) drop browser automation; manual intake only
- **Recommendation: (a)** — browser automation on the employer's tenant may breach its policy; the cost of finding out late is high.
- **Decision (2026-11-17):** decided by the owner — (b) proceed under the owner's own judgment, read-only; no written approval is asked from the client.

### 27. Watcher form (rule R1) — [roadmap] — HIGH
- (a) a `zyggy` verb in .NET using Playwright for .NET (new NuGet package)
- (b) a Node.js Playwright script
- (c) a PowerShell script driving the browser
- **Recommendation: (a)** — the owner prefers .NET tools and it ships in the single binary path; the new package is a ⚠️ risk-area decision.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 28. Reading technique — [added] — HIGH
- (a) Playwright on a separate persistent browser profile signed in by the owner
- (b) Microsoft Graph channel messages (needs tenant admin consent)
- (c) a browser extension in the owner's normal profile
- **Recommendation: (a)** — matches the roadmap, needs no tenant consent, and is isolated from the owner's normal browsing.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 29. What counts as a new request, and dedup across restarts — [roadmap] [added]
- (a) top-level channel posts only, keyed by message id, state file on the laptop
- (b) posts and replies
- (c) posts with a keyword or tag
- **Recommendation: (a)** — replies are follow-ups of a running problem; message ids give exactly-once hand-off.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 30. Polling, sign-in loss and page changes — [roadmap] [added]
- (a) poll every 5 minutes in working hours; on sign-in loss or unknown page structure stop and notify the owner
- (b) continuous watch
- (c) on demand only
- **Recommendation: (a)** — bounded load, and a loud stop beats silently missed requests.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 31. Passive capture of other channels and pings — [roadmap] [added] — HIGH
- (a) text only, in a local folder on the work laptop, 30 days, never sent anywhere
- (b) longer retention with a search index
- (c) no passive capture
- **Recommendation: (a)** — gives the owner the reference the roadmap asks for while limiting stored colleague data.
- **Decision (2026-11-17):** decided by the owner — the work laptop builds a local store (files) of other channels and direct pings, readable only by the agent on that laptop; relevant, non-confidential information from it is sent back to Central to be kept in memory. Retention is not decided.

### 32. Automatic start of an investigation — [added] — HIGH
- (a) each new request starts 48's flow at once
- (b) requests queue and the owner confirms each with one tap
- (c) start at once only for request types in the problem-type map
- **Recommendation: (b) at first, then (c)** — every start costs model runs and acts on outsiders' text.
- **Decision (2026-11-17):** decided by the owner — each new request is queued and its investigation starts at once, without confirmation. When it has a proposal, the laptop asks Zyggy on Central to send it to the owner by mail, for reading only; the mail goes to the owner himself and to nobody else. The owner answers in the Zyggy console and nothing goes out to anyone else before that answer. This needs a rule change the spec of 49 must name: today Central sends mail only on the owner's request in a conversation, after a permission prompt, and never because of something that arrives from a work machine.

### 33. Prompt-injection guard on support requests — [roadmap] [added] — HIGH
- (a) text as data only, size limit, stripped markup, no links followed
- (b) as (a) plus a sender allow-list
- (c) as (a) plus a pre-screen run flagging suspicious requests
- **Recommendation: (a)** — anyone in the channel can write; the existing "data, never instructions" rule plus limits covers v1.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

### 34. What leaves the work laptop from a captured request — [roadmap] — HIGH
- (a) nothing beyond 48's handover summary
- (b) the request text sent to Central for the owner to read
- (c) the full captured thread
- **Recommendation: (a)** — keeps 49 inside 48's boundary rule; the owner reads the original on the laptop.
- **Decision (2026-11-17):** as recommended — decided by Zyggy on the owner's standing delegation.

---

HIGH: 2, 3, 4, 5, 7, 11, 12, 13, 14, 15, 16, 17, 18, 20, 21, 22, 23, 26, 27, 28, 31, 32, 33, 34.
