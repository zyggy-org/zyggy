# zyggy-core and instance changes needed for spec 58 slice 4 — work mail content to Central's business side

Written by job 01M81D4DBW8FC3YC284KGNX7KK (2026-11-22). This repository does not touch `zyggy-core` or the instance repository;
the owner (or a job in those repositories) makes the changes below. The wording of items 3, 4 and 6 is the spec's
"Template rule changes" wording, approved with the spec; items 1, 2 and 7 are this slice's additions.

## 1. `zyggy-core`: `node/skills/work-mail-brief/SKILL.md` — replace step 3's "Never" line and add a share step

Replace the last "Never" bullet

```markdown
- Never put a mail's content into memory, a bus envelope or a report from this skill (slice 4 adds the filtered path to Central).
```

with

```markdown
- Never put a mail's content into memory, a bus envelope or a report except through `zyggy outlook share` (step 5).
```

and insert after step 4:

```markdown
5. Share what is worth keeping with Central's business memory, through the laptop's sensitive-data filter:
   `zyggy outlook share` with, on stdin, one entry per briefed mail worth keeping:
   `{"mails":[{"reference":"<item reference>","facts":["<fact>", "..."],"doubt":false}]}`.
   - A fact is one line (at most about 340 characters): what the mail is about and asks, its status, and — where it is worth
     keeping — the body or the relevant part of it ("Body: …"); split a long body over several facts.
   - Third parties: name, role and organisation only — leave out phone numbers, addresses and private details.
   - Set `"doubt": true` for any mail that may carry a national register number, patient personal information or medical
     information, or that you are not sure about: it then yields nothing for Central and is only counted on the laptop.
   - Never name an attachment in a fact (attachments never cross; such a mail is kept on the laptop).
   - The tool re-reads each mail and keeps on the laptop every mail with a national register number or a term of the shared
     sensitive list, whatever you wrote. It is best-effort: when in doubt, mark doubt.
   - Exit 3 "no sensitive-terms.txt": tell the owner the term list is missing on this laptop; share nothing.
```

## 2. `zyggy-core`: `security.md` — "Never store" exception (spec item 3, approved wording)

Replace "Never store secrets, credentials or mail bodies" with:

> Never store secrets, credentials or mail bodies — except work mail under deliverable 58: its content, bodies included, may be
> kept on Central on the business side only (`business/` memory and the business archive), never on the private side. A mail that
> may carry a national register number, patient personal information or medical information is never kept and never leaves the
> work laptop (the work boundary's never-list, the client's IT policy); a mail in doubt stays on the laptop and is only counted.
> The filter that enforces this is best-effort; the owner accepted that.

## 3. `zyggy-core`: `memory.md` — where work mail content is filed (spec item 4, approved wording)

Add:

> Work mail content from the work laptop (`nodes/work-laptop/context/`, deliverable 58) is filed on the business side only: facts,
> what a mail asks and its status under `business/<category>/` by the client, project or person it concerns (a person at most
> name, role and organisation, as for every third party); a mail body or part of it kept as reference goes with the fact or, when
> the owner wants the mail kept whole, into the business archive. Never under `private/`. A work-mail filing that targets
> `private/` is an error, not a judgement call.

## 4. `zyggy-core`: the dream's archive rule — exception for work mail (spec item 6, approved wording)

Wherever the template states "the dream never writes `archive/`" / "never archive a mail body", add:

> — except work mail from the work laptop (deliverable 58): the dream files its content, bodies included, into `business/` memory
> and into the business archive, kept long term; never on the private side, and never dropped as `not_owner_data` for being work
> data. Third parties' personal data beyond name, role and organisation is removed; national register numbers, patient data and
> medical information are never kept.

Note for the owner: in this slice the dream (in `zyggy`, filing prompt version 4) files work mail into `business/` memory; it does
not yet write the business archive itself (see the slice's open question). The rule wording above stays correct either way.

## 5. `zyggy-core` template CI (AC-15)

Add to the repo tests on the rule text: `security.md` holds "except work mail under deliverable 58" and "business side only";
`memory.md` holds "A work-mail filing that targets `private/` is an error"; the work boundary's never-list lines are unchanged.

## 6. The work boundary's never-list — no change (spec item 5).

## 7. Instance repository: the shared sensitive term list (D-Q9)

Add `sensitive-terms.txt` at the instance root beside `work-boundary.json` (the one list shared with the work-memory feature, 57):
one term per line, matched as a whole word, ignoring case; blank lines and lines starting with `#` are ignored. The terms are the
owner's to approve; this job proposes none (the client's words are not written in this public repository). Then copy it to the
work laptop's node directory as `<nodeDir>/sensitive-terms.txt` (by hand for now; `zyggy setup --instance-path` copies only
`work-boundary.json` today). Without it, `zyggy outlook share` refuses (exit 3) and shares nothing.
