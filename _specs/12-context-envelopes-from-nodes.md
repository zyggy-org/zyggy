# Spec: 12 — Nodes feed memory: `zyggy context` and the `Stop` hook emit signed context envelopes

> Founding-spec sections: §3 Components (Skills `remember` node side, Hooks `Stop`), §4 Bus protocol (Repository layout, Write rules, Envelope — context fields, Mandatory fields, Signature, Canonical form, State machine), §5 Transport (retry rule), §6 Invocation (the child's hooks), §7 Memory model (nodes never write durable memory; Rules), §8 Secrets, Isolation, Audit, §9 folder map (`Bus/`, `Cli` verbs), design rules, §10 `node.json`, Laptops, §11 Runbooks, §12 P1 gate, §13 decisions (single Central owner of memory, HMAC envelopes, MVP scope), §14 Tenancy. Roadmap entry: `_plans/ROADMAP.md` #12.

> **Reconciled with `main` (2026-10-28).** What exists: the context envelope model (`Envelope.CreateContext`, `ContextFields`, `ContextScope` with `project:<name> | machine | general`), `BusPaths.Context(tenant, machine)` and its parser, `EnvelopeSigner` + `EnvelopeWriter`, the sender shape of a signed write (`JobSubmitter` → `BusRepository.SubmitAsync` → `WriteAsync`: checkout lock, pull, add, commit, push with four attempts, `Pushed | Deferred | WriteFailed`), the bus-verb host (`BusHost`: `node.json`, `signWith`, checkout checks, exit codes 0/2/3/6/7), `ISecretStore` per OS with the signing key, `SecretPatterns` + `SecretPatternsLocation` + `RememberService` (the one line format and the one refusal path), `BusAuditor` (`zyggy verify`: a `context/` file whose `from` is not the folder's machine is `writer_mismatch`), `zyggy status` counting `context` per machine, and in `zyggy-core` the Central `stop.sh`, the `remember` skill, the launcher convention (≤ 10 lines, no `jq`/`sed`/`awk`), the `zyggy` stub for bats and the README rule that node-side material lives under `node/`. What does not exist: the `zyggy context` verb, a context write in `BusRepository`, a node-side hook and skill, and any laptop checkout of `zyggy-core` (deliverable 15 creates it — §10 "`agent-core` is created by deliverable 15"). This spec therefore ships the verb, the publish path, the template content under `node/` with its tests, and leaves the laptop wiring (settings, environment, `discover --link`) to 15.

## User Story

**As** the owner in a hand-run Claude Code session on the home laptop,
**I want** a fact I state ("remember that …") and a mechanical note of each session to travel to Central as a signed `context` envelope on the bus, without anything durable being written on the laptop,
**So that** laptop sessions feed the one memory Central owns (§7, §13 "nodes are stateless"), and the P1 walking-skeleton gate has its memory half ready for 13 to close: context envelope → dream ingest → `get_context` returns it.

---

## Acceptance Criteria

Legend: (U) unit test, (I) integration test against the local bare repository, (T) template test in `zyggy-core` (bats, `zyggy` stub), (D) documentation check.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | `home-laptop`'s `node.json` (tenant `acme`, `signWith: acme/1`, the node clone as `bus.localPath`, a scratch secret store holding `acme/1`) | `zyggy context --scope project:calizr "The staging database is restored from Sunday's snapshot"` | exit 0; stdout exactly two lines: `context <ULID> to central (tenants/acme/nodes/home-laptop/context/<ULID>.md)` and the stored line `- [stated] <UTC date> (project:calizr): The staging database is restored from Sunday's snapshot`; the file exists on `main` of the bare repository; one commit `acme context <ULID> home-laptop→central`; the sender's working copy is clean (I) |
| AC-2 | the file of AC-1 | parsed and verified with `acme/1` by the test | `schema: 1`, `type: context`, `tenant: acme`, `from: home-laptop`, `to: central`, `in_reply_to` absent/null, `priority: normal`, `scope: project:calizr`, `key_id: acme/1`, `sig: hmac-sha256:<64 hex>`, `created` within the test window, file name = `id`; the body is exactly the stored line plus one `\n`; no front-matter key outside the §4 list (I) |
| AC-3 | the file of AC-1, pulled into `central`'s checkout | `zyggy verify` with `central`'s `node.json` | the file is reported `OK` (no `writer_mismatch`, no `type_folder_mismatch`, no drift); summary exit 0 (I) |
| AC-4 | the same | `zyggy status` on `central` | the bus block counts `1` under `context` for `home-laptop` (I; the 08 format unchanged) |
| AC-5 | `zyggy context` without `--scope` | run | `scope: general`, the line carries no hint; `--scope machine` → `scope: machine`, hint ` (machine)`; `--scope project:<name>` requires `^[a-z0-9][a-z0-9-]*$`, otherwise exit 2 with one stderr line `zyggy: scope: invalid project name in --scope` (U) |
| AC-6 | a fact that is empty, whitespace only, or over 1,000 characters after whitespace collapse (a fact given as an argument wins and stdin is never read: Claude Code always redirects stdin; amended 2026-10-31) | run | exit 2, one stderr line `zyggy: fact: <detail>`, nothing signed, nothing written (U) |
| AC-7 | a fact matching a line of `secret-patterns.txt` (for example a GitHub token shape) | run | exit 5, one stderr line `zyggy: refused: matches secret pattern <name>`; the fact appears nowhere — not on stdout, stderr, in the checkout, in git or in a log line; the checkout is unchanged (U, I; `SecretSweep.AssertAbsent` over every output and the bare repository's log) |
| AC-8 | `ZYGGY_SECRET_PATTERNS` unset and underivable (no `ZYGGY_INSTANCE_DIR`, no `CLAUDE_PROJECT_DIR`), or the file missing or malformed | run | exit 3, one stderr line `zyggy: configuration error: <the SecretPatterns message>`; fail closed — no envelope is ever signed without the patterns (U) |
| AC-9 | `signWith` missing from `node.json`, or the key absent from the store, or the store unavailable | run | exit 3 `zyggy: configuration error: signWith missing` / exit 6 `zyggy: signing key acme/1 is not in the secret store; run: zyggy secret set hmac/1 --hex` / exit 6 `zyggy: secret store unavailable: …` — the 08 lines verbatim (U) |
| AC-10 | the remote unreachable (pull fails) | run | exit 6, `zyggy: context failed at pull: …`; nothing written, working copy as it was (I, the 08 `pull` failure path) |
| AC-11 | the push refused four times (another writer keeps the branch moving, the 08 `PushRetryTests` set-up) | run | exit 7, stdout line 1 ends with ` (committed locally, not yet pushed: the next bus write on this machine pushes it)`; the commit exists locally; the next bus write pushes it (I) |
| AC-12 | `node.json` whose `machine` equals the target (`central`) | run | exit 2, one stderr line `zyggy: to: this machine is central; use zyggy memory remember` — a Central session keeps its inbox route (U) |
| AC-13 | `ZYGGY_HOOKS=off` in the environment | `zyggy context "<fact>"`, `--stop-hook`, `--flush` | exit 0, nothing on stdout, nothing written or spooled (the unattended-run convention of every memory writer) (U) |
| AC-14 | the Claude Code `Stop` JSON on stdin (`session_id`, `cwd`, `stop_hook_active: false`, `last_assistant_message` with several lines, tabs and double spaces) | `zyggy context --stop-hook` | exit 0, nothing on stdout; one line `- [observed] <UTC date> (project:<name>) [session <first 8 chars> <HH:MM>Z]: <first non-empty line, whitespace collapsed, cut to 240 characters with …>` is appended to the spool `<nodeDir>/context/acme/<session_id>.md`; the spool's first line is `scope: project:<name>` where `<name>` is the lower-cased last segment of `cwd` when it matches `^[a-z0-9][a-z0-9-]*$`, else `scope: general`; no bus access, no git, no secret-store access (U — pure spool; the test asserts no process was started) |
| AC-15 | the same with `stop_hook_active: true`, or an empty `last_assistant_message`, or unparseable stdin, or a terminal on stdin | `--stop-hook` | exit 0, nothing written, nothing on stdout; one stderr line only for unparseable input (U) |
| AC-16 | a `last_assistant_message` whose first line matches a secret pattern | `--stop-hook` | exit 0 (a hook never disturbs the session), one stderr line `context: note refused (pattern <name>)`, nothing spooled; the text appears nowhere (U) |
| AC-17 | a spool already holding 150 hook lines | `--stop-hook` | the marker `- [observed] cap reached: no further hook lines this session` is appended once, one stderr line names the cap; further calls for that session append nothing (U) |
| AC-18 | a spool for session S with `scope: project:calizr` and three lines, and the Claude Code `SessionEnd` JSON for S on stdin | `zyggy context --flush` | exit 0; one signed context envelope lands in `tenants/acme/nodes/home-laptop/context/` with `scope: project:calizr` and the three lines as its body (one fact per line, in order); the spool file is deleted; stdout as AC-1 line 1; `zyggy verify` on `central` reports it `OK` (I — the DoD's "a Stop hook invocation does the same") |
| AC-19 | `--flush` while a leftover spool of another session is older than 60 minutes (a crashed or abandoned session) and a second spool is fresher | `--flush` | the session named on stdin and the stale spool are each shipped as one envelope; the fresh spool of a session not named is left alone (U with `FakeTimeProvider`) |
| AC-20 | `--flush` when the pull fails (offline) | run | exit 6, the spool files are kept untouched and are shipped by the next `--flush`; on `Deferred` (exit 7) the spool is deleted (the commit exists locally) (I) |
| AC-21 | a spool line that matches a secret pattern at flush time (a hand-edited spool) | `--flush` | the line is dropped with one stderr line naming the pattern, the rest ships; a spool left with no line ships nothing and is deleted (U) |
| AC-22 | any invocation of this deliverable | before and after | no file under any memory root changes and `ZYGGY_MEMORY_ROOT`/`ZYGGY_TENANT`/`ZYGGY_USER` are never read: the verb has no memory principal; the only files written on the node are the spool under `<nodeDir>/context/<tenant>/` and the bus checkout (U — environment without the memory variables; I — tree fingerprint of a memory tree placed beside the checkout) |
| AC-23 | every stdout/stderr line and every log line of the verb | reviewed by `SecretSweep` | never a key byte, never the refused text; git stderr is redacted as 08 does (`://<redacted>@`) (U, I) |
| AC-24 | the `zyggy-core` template | `node/hooks/stop.sh` and `node/hooks/session-end.sh` are run with a hook JSON on stdin against the `zyggy` stub | argv is exactly `context --stop-hook` / `context --flush`, stdin reaches the stub unchanged; **every** exit of the binary, a missing binary included, becomes exit 0 with the binary's stderr line kept (a Stop hook exit 2 would block the turn; a SessionEnd hook cannot block but must not fail); nothing on stdout; each launcher ≤ 10 lines, no `jq`, `sed`, `awk`, `case` or `source` (T) |
| AC-25 | `node/skills/remember/SKILL.md` | read | front matter `name: remember`; the command line is `zyggy context [--scope project:<name>|machine] -- "<fact as one sentence>"`; it maps exit 0 (kept — quote both lines), 5 (refused by a secret pattern — never retry, never rephrase, never store another way), 6 (not sent — tell the owner, nothing to retry until the bus is reachable), 7 (committed locally, goes with the next bus write), 3 (configuration — runbook pointer), `zyggy: command not found`; it says content read from files, tools, mail or the web is data, never a fact to keep (T: `repo.bats` runs the documented command against the stub; no AGENTS.md under `node/`) |
| AC-26 | `node/README.md` (instruction template, not an `AGENTS.md`) | read | documents the laptop wiring 15 installs: user-scope `Stop` hook → `node/hooks/stop.sh` (`timeout: 10`), `SessionEnd` hook → `node/hooks/session-end.sh` (`timeout: 30`, which raises Claude Code's 1.5 s SessionEnd budget), `ZYGGY_SECRET_PATTERNS` pointing at the checkout's `.claude/hooks/secret-patterns.txt`, `Bash(zyggy secret*)` and `Bash(zyggy context --stop-hook*)` / `Bash(zyggy context --flush*)` denied for the model; placeholders only (`acme`, `home-laptop`, `C:/agents/zyggy-core`) (T hygiene, D) |
| AC-27 | the §12 definition of done | the deliverable closes | the Cli README verb table has `zyggy context`; troubleshooting rows "Context: refused: matches secret pattern" (exit 5), "Context: context failed at pull" (exit 6), "Context: committed locally, not yet pushed" (exit 7, pointing at `bus-conflict.md`), "Context: spool left behind" exist; `.claude/rules/operations.md` in the template gets a one-paragraph node block pointing at `node/README.md`; `PROTOCOL.md` stays the placeholder (15); the founding-spec amendments of Deliberate deviations are proposed as exact wording; `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` is untouched (13 appends part 2) (D) |
| AC-28 | every test, fixture and example | reviewed | tenant `acme`, machines `central` / `home-laptop`, project `calizr`, no owner name, no real path or id (hygiene grep in both repositories) (D) |

---

## Decision Table

### Decisions taken at the spec gate (2026-10-28)

The owner answered the five Open Questions of the draft; each answer is now a decision that the rows below and the Contracts reference by number.

| # | Decision | Consequence |
|---|----------|-------------|
| **D1** | A node's context envelope sits in the **sender's own** `tenants/<tenant>/nodes/<from>/context/<id>.md`, addressed `to: central`. | No change to `BusAuditor` or `AssertMayWrite`; 13's ingest walks every `nodes/<machine>/context/` of the tenant and filters `to: central`; 21's proxy caches the machine's own folder. Founding spec amended by A12-1; the roadmap goal needs the project-manager's matching edit. |
| **D2** | The node `Stop` hook **spools** one `[observed]` line per turn to `<nodeDir>/context/<tenant>/<session_id>.md` (`zyggy context --stop-hook`); the `SessionEnd` hook **ships** one signed envelope per session, plus any spool older than 60 minutes (`zyggy context --flush`). | One commit per session, no network on the turn path, offline-safe, the §4 "one fact per line" body. The hook scope is `project:<lower-cased basename of cwd>` when it is a valid label, else `general`. The Node-service shipper and the "no node `Stop` hook" variants are not built. Founding spec amended by A12-2 and A12-5. |
| **D3** | A laptop finds `secret-patterns.txt` through **`ZYGGY_SECRET_PATTERNS` only** (set in the user-scope Claude Code settings `env` by 15); the existing `SecretPatternsLocation` resolver is unchanged and the verb fails closed (exit 3) when the file is unresolvable or malformed. | No `node.json` schema change, no embedded copy of the pattern list; `node/README.md` documents the wiring now. |
| **D4** | The explicit verb writes **`[stated]`** (the owner spoke; the HMAC proves which machine); the hook writes **`[observed]`**. | 13 copies the lines verbatim and never upgrades a tag; the tag describes the speech act, not the transport. Founding spec amended by A12-3. |
| **D5** | The runner sets **`ZYGGY_HOOKS=off`** in the environment of every `claude -p` job run. | The memory and context writers (`memory remember`, the Hub's `remember`, `context --stop-hook`, `context --flush`) do nothing inside jobs while the enforcement hooks still run; one production line in `JobRunner`, proven with the fake `claude`. Founding spec amended by A12-4. |

| Founding-spec item (section) | Verdict | Target type / library | Justification |
|------------------------------|---------|-----------------------|---------------|
| `agentbus context` verb (§3 CLI table, §9 map) | **Keep** | `ContextCommand` (System.CommandLine, `BusHost`-hosted like `submit`) in `Zyggy.Cli`; logic in `Zyggy.Core.Bus` | The gate's part 2 starts with it. One bus verb, one host: `node.json`, `--config`, `--verbose`, the 08 exit-code family. |
| Context envelope: `scope` mandatory, body one fact per line (§4) | **Keep** (exists) | `Envelope.CreateContext`, `ContextFields`, `ContextScope` | Built in 03; the verb only calls it. The body's line format is fixed below so 13 can copy lines verbatim. |
| Where the sender writes a context file (§4 layout comment "written only by `<machine>`" vs write rule "`nodes/<target>/context/` when the target is Central") | **Reshape** (**D1**) | `BusPaths.Context(tenant, me)` — the sender's own folder, `to: central` | The code already decided it twice: `BusAuditor` reports a `context/` file whose `from` is not the folder's machine as `writer_mismatch`, and `AssertMayWrite` allows a foreign machine's folder only for job submission. §7's proxy text ("the machine's own `nodes/<me>/context/` folder") and §8 audit ("`from`/`to` match the folder's machine") agree. Writing into `nodes/central/context/` would need an auditor exception and a second write-rule branch for nothing the round trip needs. The §4 sentence and §7 step 1 are amended (A12-1); the roadmap goal gets the project-manager's matching edit. |
| `to` = Central; `in_reply_to` null; `priority: normal`; file name = ULID (§4) | **Keep** | `MachineName.Central` (the §4 label `central`, a protocol constant, not a tenant default), `EnvelopeId.New(now)` | §4 names the folder `nodes/central/`; a `--to` option would be a knob with one legal value. Central itself is refused (AC-12) so `from == to` never happens. |
| Signature with the machine's `signWith` key from `ISecretStore` (§4, §8) | **Keep** (reuse) | `EnvelopeSigner.SignAsync` through `BusHost.BuildServices` | Security control; the only path that may touch the key. The model never sees it: the hook and the skill run `zyggy` as a separate process whose stdout holds no key material (AC-23). |
| Build → sign → serialise → re-parse → re-verify → compare (08's self-check) | **Reshape** | `ContextSubmitter` with the `JobSubmitter` shape (`BuildAsync` / `SubmitAsync`), returning the existing `BuildResult` / `SubmitResult` records | Same proof for the same reason (no bytes reach the bus that read back differently). A generic submitter over both types would need per-type diffing anyway; a sibling class is the smaller change. 21's `hub --proxy` calls this class. |
| Lock, pull, add, commit, push ×4 with jitter, `Deferred` (§5 retry rule) | **Library** (reuse) | `BusRepository.PublishContextAsync(signedContext)` over the private `WriteAsync`, subject `<tenant> context <id> <from>→<to>` | One write sequence in the product; the verb adds a 20-line caller, not a second sequence. |
| `remember` skill, node side: "emit a `context` envelope" (§3) | **Keep** | `zyggy-core/node/skills/remember/SKILL.md` calling `zyggy context` | Owner-stated facts from a laptop are the one high-value input. Lives under `node/` per the template README rule (no `AGENTS.md` below the root). The Central skill is untouched. |
| How a laptop `remember` chooses between the Hub and `zyggy context` (roadmap question) | **Keep** (decided by layout) | Central checkout: `.claude/skills/remember` → `zyggy memory remember`; laptop material: `node/skills/remember` → `zyggy context` | The choice is made by which skill directory a machine links (15's `discover --link`), never by the model at run time. When 21 ships `hub --proxy`, its `remember` tool calls `ContextSubmitter`; the skill route stays as the fallback. |
| `Stop` hook on nodes: "emit a `context` envelope so hand-run sessions still feed memory" (§3) | **Reshape** (**D2**) | `zyggy context --stop-hook` appends one `[observed]` line to a per-session spool under `<nodeDir>/context/<tenant>/`; `zyggy context --flush` (the `SessionEnd` hook) ships one envelope per session | A push per turn would be a commit per turn on the bus, a network round trip at every turn end, and nothing at all when offline (the write sequence fails at `pull` and writes nothing). §4's own "one fact per line" body is a per-session batch. The spool is node state like the transcripts under `<nodeDir>/runs/`, never memory (§7 holds, AC-22). |
| A hook must not call the model (§3, amended 30 September) | **Keep** | the note is the first non-empty line of `last_assistant_message`, cut to 240 characters — Central's `stop.sh` rule | Verified in the Claude Code hooks reference: `Stop` carries `last_assistant_message`, `stop_hook_active`, `cwd`, `session_id`; the transcript is not guaranteed complete at Stop time, so the field is used, never `transcript_path`. |
| `SessionEnd` hook (not in the founding spec) | **Reshape** (the shape of the §3 feature) | `node/hooks/session-end.sh` → `zyggy context --flush` | Verified in the hooks reference: `SessionEnd` exists, carries `session_id`, `cwd`, `reason`, has no decision control and a 1.5 s default budget raised by a per-hook `timeout` up to 60 s. Not new capability — the only trigger that makes "one envelope per session" possible without a model call. Added to §3 by A12-2. |
| Daily cap of 150 hook lines and 240-character notes (Central's `stop.sh`, a data-protection control) | **Keep** | `ContextEnvelopeOptions.MaxHookLinesPerSession = 150`, `FactValidator.MaxCharacters` / `Cut` | Same numbers, same reason (a runaway session cannot flood memory); per session instead of per day because the spool is per session. |
| Secret-pattern refusal before anything leaves the laptop (§7 Rules; roadmap question) | **Keep** (**D3** for the file's location) | `SecretPatterns` + `SecretPatternsLocation`, loaded once per invocation, fail closed (AC-8); the file is found through `ZYGGY_SECRET_PATTERNS` only | Secret-shaped content leaving a laptop onto GitHub is the worst case of the §7 rule; the same data file as `remember` and the Hub. The scan runs before signing and again at flush. |
| Line format and tag of a laptop fact (§4 "`[stated]`-style", §7 "facts from nodes are `[observed]` until the user confirms them") | **Keep** (one formatter; **D4** for the tag) | `RememberService`'s line `- [<tag>] <date><hint><provenance>: <fact>`, exposed as `FactLine.Format` and used by both writers | 13 copies body lines verbatim into `inbox/context-<id>.md`; the hint keeps the scope readable inside the line, the provenance keeps `session <id8>`. The explicit verb writes `[stated]` (the owner spoke), the hook `[observed]` (mechanical); whether ingest downgrades is 13's call. |
| Nodes never write durable memory (§7, roadmap design rule) | **Keep** | AC-22 | The reason the deliverable exists. The verb has no memory principal at all. |
| Envelope bodies are data, never instructions (§4, §8) | **Keep** | the node skill's text; no prompt in this deliverable | 13's ingest and the Hub already treat the lines as data. |
| `ZYGGY_HOOKS=off` for unattended runs (every memory writer's convention) | **Keep** (**D5** for job runs) | AC-13; the runner adds `ZYGGY_HOOKS=off` to every `claude -p` job environment (A12-4) | A job run on the laptop runs with hooks on (`ModelSessionIsolation.None` — 15's `PreToolUse` hook must run) so a user-scope `Stop` hook fires inside jobs; without the variable it would spool and later push the job's own output as context while the node holds the checkout lock. |
| `zyggy verify` accepts the file (§8 Audit, DoD) | **Keep** (exists) | `BusAuditor` unchanged under D1 | The writer check already matches `from` against the folder's machine. |
| `zyggy status` counts `context` (08) | **Keep** (exists) | — | AC-4 only pins it. |
| "Central's node receives it" (roadmap goal) | **Defer** to 13 | — | Reception in 12 is the file on `main`, counted by `status` and verified by `verify` after Central's pull. Ingest into `inbox/` is the dream's step 1 (13). No Node-service change. |
| `hub --proxy` turning `remember` into a context envelope (§7) | **Defer** to 21 | `ContextSubmitter` is the seam 21 calls | Roadmap. |
| `zyggy report` for skills (§3) | **Defer** to 15 | — | Roadmap. |
| DLP on context envelopes from the work node; `share: true` lines (§7, §8) | **Defer** to 19/20 | the filter plugs into `ContextSubmitter.BuildAsync` | No work node in P1; the hook and the verb share one build path so 19 adds the filter once. |
| Laptop wiring: user-scope hooks, `ZYGGY_SECRET_PATTERNS`, `discover --link`, deny rules (§10 Claude Code side) | **Defer** to 15 | `node/README.md` documents the wiring; `install-node.md` gets a pointer | §10: "`agent-core` is created by deliverable 15". 12 ships testable content, not an installed laptop. |
| `node.json` keys (§10) | **Keep** (no new key) | `tenant`, `machine`, `signWith`, `bus`, `secrets`, `nodeDir` | The spool lives under `nodeDir` (already the home of transcripts and logs). No schema change, no README table change beyond the spool path. |
| Exit codes (08 family: 0 / 2 / 3 / 6 / 7) | **Reshape** | add `5` = refused by a secret pattern | `remember` uses 2 for the refusal, but 2 is "usage" in the bus-verb family; the skill must tell "never retry" (5) from "fix the call" (2). 5 is already "refused by policy" for the `m365` and `linkedin` verbs. |
| `--tag` / `--source` options as `zyggy memory remember` has them | **Defer** | — | The skill passes `--scope` only; the hook sets its tag internally. Fewer knobs (the Hub's D4 precedent). |
| Several facts in one explicit invocation (stdin with many lines) | **Defer** | — | The explicit verb takes one fact (as `remember` does); the multi-line body is the flush's. 21's proxy may batch later. |
| Logging | **Keep** | stderr console logger of `BusHost`; `LoggerMessage` lines as `JobSubmitter`; never the fact text in a log line | Log once per state change; a refused fact is logged by pattern name only. |
| `PROTOCOL.md` (§4 "must match verbatim") | **Keep** (placeholder) | untouched | 15 publishes it; the §4 amendments below are the owner's. |
| Runbook entries (§11, §12) | **Keep** | Cli README troubleshooting rows; `bus-conflict.md` pointer; template `operations.md` node paragraph | Public, placeholder-only. |
| Tenancy (§14): `tenants/<org>/` path, `key_id` tenant, spool under `<nodeDir>/context/<tenant>/` | **Keep** | `BusPaths`, `KeyId`, `TenantId` on every call | Costs nothing; no tenant-only code. |

---

## Contracts

### Types and interfaces

Namespace `Zyggy.Core.Bus` (additions; `Model = Zyggy.Core.Envelope.Envelope`):

```
public sealed record ContextEnvelopeOptions
    int MaxFactChars = 1000; int MaxNoteChars = 240; int MaxHookLinesPerSession = 150;
    TimeSpan StaleSpoolAge = 60 min; int MaxBodyBytes = 65_536;
    string CapMarker = "- [observed] cap reached: no further hook lines this session";

public sealed record ContextFactError(string Field, string Detail);                 // the CLI prints `zyggy: <Field>: <Detail>`
public sealed record ContextRequest(ContextScope Scope, IReadOnlyList<string> Lines) // Lines: body lines without "\n", already formatted and scanned
    static ContextFactError? TryCreateStated(string? scope, string? fact, DateOnly utcDate, out ContextRequest? request);
                                                                                     // one line `- [stated] <date><hint>: <fact>`; fact collapsed, 1–1000 chars

public sealed class ContextSubmitter(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, TimeProvider clock, ILogger<ContextSubmitter> log)
    Task<BuildResult>  BuildAsync(ContextRequest request, CancellationToken ct);      // header: tenant = bus.Me.Tenant, from = bus.Me.Machine, to = MachineName.Central, created = now, id = EnvelopeId.New(now)
                                                                                     // refuses: a line matching a secret pattern → BuildRefused("fact", "matches secret pattern <name>") … see Behaviors
    Task<SubmitResult> SubmitAsync(ContextRequest request, CancellationToken ct);     // = BuildAsync + BusRepository.PublishContextAsync; never throws except cancellation

public sealed record ContextRefused(string PatternName) : SubmitResult;              // the secret refusal, distinct from SubmitRefused (self-check) — exit 5

// BusRepository (addition)
Task<WriteOutcome> PublishContextAsync(Model signedContext, CancellationToken ct);  // path = BusPaths.EnvelopeFile(BusPaths.Context(Me.Tenant, Me.Machine), id); AssertMayWrite(path, BusFolder.Context)
                                                                                     // subject `<tenant> context <id> <from>→<to>`; InvalidOperationException when unsigned, foreign tenant or from != Me

// MachineName (addition)
public static MachineName Central { get; }                                           // "central", the §4 label of the memory owner's node folder

public sealed class ContextSpool(string nodeDir, TenantId tenant, TimeProvider clock, ContextEnvelopeOptions options)
    string Directory { get; }                                                        // <nodeDir>/context/<tenant>/
    SpoolAppendResult Append(string sessionId, ContextScope scope, string line);     // creates `<sessionId>.md` with line 1 `scope: <scope>`; Appended | CapReached (marker written once) | AlreadyCapped
    IReadOnlyList<SpoolFile> Due(string? sessionId, DateTimeOffset now);             // the named session's file (any age) + every file whose last write is older than StaleSpoolAge
    void Delete(SpoolFile file);
public sealed record SpoolFile(string Path, string SessionId, ContextScope Scope, IReadOnlyList<string> Lines);

public static class StopHookInput                                                   // System.Text.Json, source-generated context
    static StopHookNote? Parse(Stream stdin);                                        // null when unparseable, stop_hook_active true, or no non-empty line
public sealed record StopHookNote(string SessionId, string Cwd, string FirstLine);  // FirstLine: CR removed, whitespace collapsed (TextCollapse.Line), cut by FactValidator.Cut
public static class SessionEndInput
    static string? SessionId(Stream stdin);                                          // null when unparseable or absent
```

Namespace `Zyggy.Core.Memory` (addition): `internal static class FactLine { static string Format(string tag, string hint, string source, string date, string fact); }` — the one line formatter `RememberService` and `ContextRequest` share (`- [<tag>] <date><hint>[ [<source>]]: <fact>`). `RememberService` keeps its behaviour byte for byte (existing tests).

Namespace `Zyggy.Cli.Commands` (addition): `ContextCommand` — ordering, option reading and rendering only, every decision in Core (08's rule for `submit`).

### Envelope / file formats

Context envelope written by this deliverable (fields in canonical order, values as `Envelope.CreateContext` emits them):

```
---
created: 2026-10-28T10:42:07Z
from: home-laptop
id: 01K8…
key_id: acme/1
priority: normal
schema: 1
scope: project:calizr
sig: hmac-sha256:…
tenant: acme
to: central
type: context
---
- [stated] 2026-10-28 (project:calizr): The staging database is restored from Sunday's snapshot
```

Path: `tenants/<tenant>/nodes/<from>/context/<id>.md` (D1). Body: one line per fact, `\n` terminated, UTF-8, no BOM, ≤ 64 KiB. Line grammar = `FactLine.Format`:

- explicit: `- [stated] <UTC date><hint>: <fact>` with hint `""` | ` (machine)` | ` (project:<name>)`;
- hook: `- [observed] <UTC date><hint> [session <id8> <HH:MM>Z]: <note>`;
- cap marker: `ContextEnvelopeOptions.CapMarker` (last line of a capped spool).

Spool file `<nodeDir>/context/<tenant>/<session_id>.md`: UTF-8, LF; line 1 `scope: <scope>`; following lines = body lines. Node state, never committed anywhere, deleted after `Pushed` or `Deferred`.

No envelope field, canonical-form rule or state-machine row changes. `PROTOCOL.md` stays the placeholder. The §4 write-rule sentence is amended (A12-1, D1).

### CLI surface

| Verb | Options and arguments | Output (stdout) | Exit codes |
|------|-----------------------|-----------------|------------|
| `zyggy context [--scope general\|project:<name>\|machine] [--] [<fact>] [--config <path>] [--verbose]` | `<fact>` positional (`--` ends options, as the skill writes it); when absent, read from stdin and collapsed to one line; both given → exit 2 | line 1 `context <ULID> to central (<repository-relative path>)` (+ the 08 deferral suffix); line 2 the stored line | 0 pushed · 2 argument refused / self-check refused / this machine is Central (`zyggy: <field>: <detail>`) · 3 configuration (`node.json`, `signWith`, checkout, secret patterns) · 5 refused by a secret pattern (`zyggy: refused: matches secret pattern <name>`) · 6 write failed (`lock`, `pull`, `sign`, `secret-store`, `add`, `commit`; 08's lines with `context` in place of `submit`) · 7 committed locally, push deferred |
| `zyggy context --stop-hook [--config <path>]` | the Claude Code `Stop` JSON on stdin | nothing | 0 always, except 3 for a configuration error (`node.json`, secret patterns); one stderr line for a refused note, the cap, or unparseable input |
| `zyggy context --flush [--config <path>] [--verbose]` | the `SessionEnd` JSON on stdin (optional: by hand, stale spools only) | one `context <ULID> …` line per envelope shipped | 0 nothing to ship or every due spool shipped (pushed or deferred) · 3 configuration · 6 the write sequence failed for at least one spool (its file kept) |

`--stop-hook`, `--flush` and `<fact>` are mutually exclusive (exit 2). `ZYGGY_HOOKS=off` → exit 0 and nothing in every mode. Every verb resolves `node.json` as 04 defined; stdout carries only the result lines.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `tenant`, `machine`, `signWith`, `bus.*`, `secrets.*` | `node.json` | as 04/09 | unchanged |
| `nodeDir` | `node.json` | per-OS default (`NodeConfiguration.EffectiveNodeDir`) | the spool directory `<nodeDir>/context/<tenant>/` derives from it; no separate key |
| `ZYGGY_SECRET_PATTERNS`, else `<ZYGGY_INSTANCE_DIR or $CLAUDE_PROJECT_DIR/instance>/../.claude/hooks/secret-patterns.txt` | environment | none | the existing `SecretPatternsLocation` rule, unchanged; unresolvable → exit 3 (D3: 15 sets the variable in the user-scope Claude Code settings `env`) |
| `ZYGGY_HOOKS` | environment | unset | `off` → every mode does nothing, exit 0 |
| `ContextEnvelopeOptions` (1,000 / 240 / 150 / 60 min / 64 KiB) | code, `init` properties | as listed | no environment or `node.json` knob; the caps are data-protection controls and change with a release, as the Central `stop.sh` constants do |
| Hook wiring on a laptop: `Stop` → `node/hooks/stop.sh` `timeout: 10`; `SessionEnd` → `node/hooks/session-end.sh` `timeout: 30` | user-scope Claude Code settings, installed by 15 | — | documented in `node/README.md`; a project may add nothing looser |

---

## Behaviors & Conventions

- `zyggy context "<fact>"` is synchronous and direct: build, sign, self-check, lock, pull, add, commit, push; the owner sees `context <id> …` only after the bytes are on the bus (or exit 6/7 saying why not) — override: none.
- The hook route never touches the bus: `--stop-hook` appends to the spool in milliseconds and needs no key, no network and no checkout; `--flush` at `SessionEnd` ships one envelope per due spool. A `Stop` hook therefore never adds a network round trip to a turn — override: none (D2).
- Both routes go through `ContextSubmitter.BuildAsync`: the secret scan (every line, pattern name only in any message), the length caps, `Envelope.CreateContext`, signing with `signWith`, the 08 round-trip self-check — override: none.
- The secret scan is fail-closed: no patterns file, no envelope (exit 3) — override: `ZYGGY_SECRET_PATTERNS`.
- Dates in body lines and `[session … HH:MMZ]` are UTC from the `TimeProvider` (the envelope's `created` is UTC; nodes have no memory time zone) — override: none.
- The hook's scope is `project:<lower-cased last segment of cwd>` when that is a valid project label, else `general`; the owner's explicit `--scope` is never derived — override: none in 12 (14's registry may refine the name).
- Nothing is read from `ZYGGY_MEMORY_ROOT` / `ZYGGY_TENANT` / `ZYGGY_USER`; the verb is a bus verb, its identity is `node.json` — override: none (§7).
- A Central machine is refused (AC-12): Central's own facts take the inbox route through `zyggy memory remember` — override: none.
- Log lines (stderr console logger, Information): `built context <id>` (Debug), `published context <id> to central`, the 08 deferred/failed lines with `context` as the action, `refusing context: matches secret pattern <name>`. Never the fact text — override: `--verbose`.
- Template launchers: `node/hooks/stop.sh` and `node/hooks/session-end.sh` are ≤ 10 lines, pass stdin through, and exit 0 whatever the binary does (one stderr line kept); `node/skills/remember/SKILL.md` is the laptop's `remember`; nothing under `node/` is loaded by Central's sessions — override: none (template README rule).
- `.claude/zyggy-min-version` of the template is unchanged (Central's instance does not need `zyggy context`); `node/README.md` states the first release that ships the verb for the laptop install (15).

---

## Failure modes

| Situation | Observable outcome | Runbook entry |
|-----------|--------------------|---------------|
| Fact empty, over 1,000 characters, invalid `--scope`, both stdin and positional, this machine is `central` | exit 2, `zyggy: <field>: <detail>`; nothing signed | Cli README "Context: `zyggy: <field>: <detail>`" (usage) |
| Fact or hook note matches a secret pattern | explicit: exit 5, `zyggy: refused: matches secret pattern <name>`; hook: exit 0, stderr `context: note refused (pattern <name>)`; flush: the line dropped, stderr names the pattern; the text appears nowhere | Cli README "Context: refused: matches secret pattern" — never retry, never rephrase, never store another way (the `remember` rule) |
| Secret patterns unresolvable or malformed | exit 3, `zyggy: configuration error: …`; fail closed in every mode | Cli README "Context: configuration error: secret patterns" → set `ZYGGY_SECRET_PATTERNS` (15 installs it) |
| `node.json` missing/invalid, `signWith` missing, checkout not a repository / wrong branch / no `tenants/<tenant>/` | exit 3, the 04/08 lines | existing rows |
| Key absent, store unavailable | exit 6, the 08/09 lines with the `zyggy secret set hmac/<n> --hex` hint | existing "Bus: keys" row |
| Checkout lock busy (the node is writing) | exit 6 `zyggy: the bus checkout is busy: …` after the 60 s lock timeout; nothing written; a spool is kept | Cli README "Context: the bus checkout is busy" → retry; the node's write finishes first |
| Pull fails (offline, auth) | exit 6 `zyggy: context failed at pull: …`; nothing written; a spool is kept and shipped by the next flush | Cli README "Context: context failed at pull" → `zyggy probe reach`, `git -C <bus.localPath> status` |
| Push refused four times | exit 7, the deferral suffix; the commit goes with the next bus write; a spool is deleted | Cli README "Context: committed locally, not yet pushed" → `bus-conflict.md` |
| Self-check refused (writer bug) | exit 2 `zyggy: refusing context: the envelope does not round-trip (<field>)`; nothing written | existing "does not round-trip" row (report it) |
| Hook stdin unparseable / `stop_hook_active` / empty message | exit 0, nothing spooled, one stderr line only for unparseable input | none (the session is never disturbed) |
| Spool cap reached | the marker once, stderr once per session | Cli README "Context: cap reached" (data-protection control; nothing to do) |
| Spool left behind (crash, offline at `SessionEnd`) | shipped by the next `--flush` once older than 60 minutes; visible as files under `<nodeDir>/context/<tenant>/` | Cli README "Context: spool left behind" → run `zyggy context --flush` by hand or wait for the next session end |
| `zyggy` missing on the laptop | launchers: exit 0 with `stop: zyggy not found` / `session-end: zyggy not found` on stderr; skill: "command not found" → tell the owner | `install-node.md` "Binary missing or wrong version" |

---

## Dependencies

| Package | License | Why |
|---------|---------|-----|
| — | — | **No new package.** `System.CommandLine` (verb), `System.Text.Json` source generation (hook JSON), `Microsoft.Extensions.*` (host, logging), the existing `Envelope`/`Bus`/`Secrets`/`Memory` code. `Directory.Packages.props` stays at its 24 rows. |

---

## Deliberate deviations from the founding spec

Approved at the spec gate (2026-10-28); each amendment names its decision. Only the owner edits `_specs/00 - Personal Agent Platform — Technical Specification.md`; the orchestrating session pastes the approved text below and lists every edit in the journal (the technical-analyst never touches the founding spec). "Before" quotes the current sentence verbatim (line numbers as of 2026-10-28); "After" is the replacement verbatim.

**Roadmap (project-manager's edit, not part of the founding spec):** the `_plans/ROADMAP.md` #12 entry says the envelope goes "to `tenants/<org>/nodes/central/context/`" (Goal, line 334) and "Write rules (sender writes `nodes/central/context/`)" (Spec sections, line 336). Under D1 both must read the sender's own folder: `tenants/<org>/nodes/<machine>/context/` with `to: central`. That edit belongs to the project-manager.

### A12-1 — a context envelope sits in the sender's own `context/` folder (D1)

**§4 Write rules, first bullet (line 132).**
Before:
> - A sender writes only into `nodes/<target>/jobs/` (and `nodes/<target>/context/` when the target is Central), and only inside its own tenant's prefix.

After:
> - A sender writes only into `nodes/<target>/jobs/`, and only inside its own tenant's prefix. A `context` envelope is addressed to Central (`to: central`) but written into the sender's own `nodes/<sender>/context/` folder, so that every `reports/` and `context/` file is written by the machine whose folder holds it (amended 28 October 2026, deliverable 12).

**§7 Dream pass, step 1 (line 373).**
Before:
> 1. `agentbus dream ingest`: fetch the bus; copy new `tenants/<org>/nodes/central/reports/` and `context/` files into that tenant/user's `inbox/` as one file each; move processed bus files to `tenants/<org>/archive/`. A file whose `tenant` field differs from the subtree it sits in is never ingested.

After:
> 1. `agentbus dream ingest`: fetch the bus; copy the new `reports/` and `context/` files addressed to Central (`to: central`) from every `tenants/<org>/nodes/<machine>/` of the tenant into that tenant/user's `inbox/` as one file each; move processed bus files to `tenants/<org>/archive/`. A file whose `tenant` field differs from the subtree it sits in is never ingested (amended 28 October 2026, deliverable 12; the sender writes its own folder, §4).

### A12-2 — the node `Stop` hook spools; `SessionEnd` ships one envelope per session (D2)

**§3 Hooks, the `Stop` bullet (line 101; the `SessionEnd` bullet is inserted directly after it).**
Before:
> - `Stop` (all): on Central, append one `[observed]` line per turn to `memory/<tenant>/<user>/daily/<date>.md` (mechanical: first line of the assistant's last message, secret patterns refused, daily cap; amended 30 September 2026 — `Stop` fires per turn and a hook must not call the model); on Nodes, emit a `context` envelope so hand-run sessions still feed memory.

After:
> - `Stop` (all): on Central, append one `[observed]` line per turn to `memory/<tenant>/<user>/daily/<date>.md` (mechanical: first line of the assistant's last message, secret patterns refused, daily cap; amended 30 September 2026 — `Stop` fires per turn and a hook must not call the model); on Nodes, append the same line to a per-session spool under `<nodeDir>/context/<tenant>/` (`agentbus context --stop-hook`; never the bus, never memory).
> - `SessionEnd` (Nodes): ship the session's spool as one signed `context` envelope, one fact per line, with `agentbus context --flush` (also ships spools older than an hour left by a crashed session), so hand-run sessions still feed memory with one commit per session (amended 28 October 2026, deliverable 12).

### A12-3 — the tag of a fact from a node (D4)

**§7 Rules that constrain the dream pass, second bullet (line 384).**
Before:
> - Facts from nodes are `[observed]` until the user confirms them in a session.

After:
> - Facts a node derives (reports, hook notes) are `[observed]` until the user confirms them in a session; a fact the owner states in a laptop session through the `remember` skill travels as `[stated]`, like one stated on Central (amended 28 October 2026, deliverable 12).

### A12-4 — job runs are unattended runs (D5)

**§6 Invocation, the comment line above the command (line 280).**
Before:
> `# working directory = <run dir>, set on the process (no --cwd); the rendered prompt is written to stdin`

After:
> `# working directory = <run dir>, set on the process (no --cwd); the rendered prompt is written to stdin; ZYGGY_HOOKS=off in the child's environment (an unattended run: the memory and context writers do nothing, the enforcement hooks still run)`

### A12-5 — `remember` skill row (follows D2)

**§3 Skills table, row `remember` (line 90).**
Before:
> | `remember` | Central, Nodes | Append a `[stated]` fact to `memory/<tenant>/<user>/inbox/` (Central) or emit a `context` envelope (Nodes). |

After:
> | `remember` | Central, Nodes | Append a `[stated]` fact to `memory/<tenant>/<user>/inbox/` (Central, `agentbus memory remember`) or emit a signed `context` envelope to Central (Nodes, `agentbus context`; the laptop variant of the skill lives under `agent-core/node/`). |

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| The same fact sent twice | two envelopes, two lines; de-duplication is the dream's job |
| `cwd` of the hook is a drive root or has a basename that is not a label (`My Project`) | `scope: general` |
| Two sessions in the same project at once | two spools (session ids differ); each ships at its own `SessionEnd`; the stale rule never touches a spool younger than 60 minutes |
| A long idle session (> 60 min without a turn) and another session ends | its spool is shipped early; the next turn starts a fresh spool for the same session — two envelopes for one session, harmless |
| `SessionEnd` with `reason: clear` or `resume` | the spool of that session ships (the session id ends); a new id starts a new spool |
| The spool directory does not exist | created on first append; `--flush` with no directory → exit 0 |
| `last_assistant_message` is only whitespace or starts with blank lines | the first non-empty line is used; none → nothing spooled |
| The hook note contains `[stated]` or `[observed]` text | kept as text inside the note; the line's tag is the hook's `[observed]` |
| A fact containing `\r\n` or tabs | collapsed to one line (`TextCollapse.Line`) before any check |
| The checkout is mid-rebase (a killed hook) | `--flush` fails at `pull` (exit 6), spool kept; `bus-conflict.md` restores the checkout |
| `node.json` `machine` is `central` on a machine whose role is node | refused (AC-12) — the label, not a role key, decides; `node.json` has no role property today |

---

## Out of Scope

- Ingest of context files into `inbox/`, archive moves, the dream's handling of `[observed]`/`[stated]` lines from nodes, `Gates/P1_WalkingSkeleton.cs` part 2 (13).
- The laptop install of hooks, environment and skills; `discover --link`; `zyggy report`; `PROTOCOL.md` content; the `PreToolUse` hook (15).
- `hub --proxy` and its `remember` tool (21); batching several explicit facts in one envelope.
- DLP filtering of context bodies, `share: true`, the work node (19, 20, 24).
- A `--to` option, a `--tag`/`--source` option, a Central route through this verb.
- Any Node-service change (a spool shipper inside `Zyggy.Node` is the recorded alternative of D2, not built).
- A `node.json` schema change (no `role`, no `agentCore`, no spool key).
- Reading `transcript_path`, any transcript parsing, any model call from a hook.
- Changes to Central's `stop.sh`, `session-start.sh`, the digest, `RememberService`'s behaviour or `secret-patterns.txt`.

---

## Open Questions

None. The five questions of the draft (sender's folder, hook shape, secret-patterns location, fact tag, `ZYGGY_HOOKS=off` in job runs) were answered by the owner on 2026-10-28 and are recorded as D1–D5 at the head of the Decision Table; their founding-spec wording is in Deliberate deviations (A12-1..A12-5). The spec is planner-ready.
