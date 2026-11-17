# Plan: 47 — Browsable memory on a laptop — "As the owner, I want the agents on each of my laptops to read Central's memory locally — a home laptop the memory it may read, the work laptop everything under `business/` and nothing else — from a read-only copy that Central refreshes after each dream pass, so that a laptop session or job knows what Central knows without asking Central"

> **Precondition (not a plan step).** `_specs/47-browsable-memory-laptop.md` is approved by the owner (its Status line says so) and the `spec-critic` pass has run. Until then this plan is not executed. The plan was written on 2026-11-17 in an unattended job from the spec with its 14 decisions written in; it names files from the code at `2ba708b` and the executor confirms each against the current code before the step that uses it (CLAUDE.md: files, types and tests are worked out from the current code when a slice is built).
>
> **Scheduling note.** Branch `feature/47-browsable-memory-laptop` from `main` in `zyggy`; the `memory` skill goes to `zyggy-core` on a branch `feature/47-memory-skill` (Step 7); the runbooks go to the private instance repository (Step 7). Each 🛑 HUMAN GATE ends with a proposed commit message; Claude commits the slice on the owner's approval, never pushes outside the `ship` skill. Spec 18 slice 11 (`supervision.outboundPaths`) may land before or after this plan: Step 1 reads the key with spec 18's definition and adds the reader only when it is still missing.

## Overview

After this plan Central publishes, after every dream pass and on `zyggy memory mirror publish`, one snapshot per laptop to that laptop's own private repository `zyggy-org/zyggy-mirror-<machine>`: the files of `<tenant>/<user>/` that the machine may read (`mirror.targets`; a work machine only inside `supervision.outboundPaths`, i.e. `business/`), never `inbox/`, `auto/`, `.dream/` or archived item files, each file withheld whole when a line hits the secret or contact-detail patterns, plus a fixed `MIRROR.md`. Pushes use that mirror's own SSH deploy key, one rebase retry, never a force push. Each laptop's Node keeps a depth-1, read-only clone at `mirror.path` current every `mirror.refreshMinutes`, fetching with the laptop's `mirror-pat` (fine-grained, read-only, OS secret store) handed to that one git call with credential helpers off; it refuses and deletes a clone with the wrong remote or repository name, without `MIRROR.md`, or with a path outside `mirror.allowedPaths`. `zyggy setup` wires the clone into the user-scope Claude Code settings (read-only additional directory, `ZYGGY_MEMORY_MIRROR`), writes `["business/"]` on a `discovery.detail: names` machine and names a missing `mirror-pat`; `zyggy-core` ships a `memory` skill.

Implements `_specs/47-browsable-memory-laptop.md` (decisions Q1–Q14 of 2026-11-17, all binding). Founding-spec sections: §5 (git transport, credentials), §7 (layout, dream step 7, Hub on laptops), §8 (work boundary, secrets, injection, data protection), §9 design rules, §10 (laptops), §11 (runbooks), §13 (MVP scope).

**Reference pattern**: the dream pass on Central (`_specs/28-central-dream-local.md`: `src/Zyggy.Core/Dream/DreamRunner.cs`, `DreamRunRecord.cs`, `src/Zyggy.Core/Memory/MemoryPublisher.cs` for commit-and-push with one rebase retry, `SecretPatterns.cs` + `ContactDetailPatterns.cs` for the line checks, `tests/Zyggy.Integration/Infrastructure/MemoryRepoFixture.cs`, `tests/Zyggy.Integration/Dream/DreamPushTests.cs`); the discovery registry for a Node timer that keeps one checkout current (`src/Zyggy.Node/DiscoveryTimer.cs`, `src/Zyggy.Core/Registry/RegistryPublisher.cs`, `tests/Zyggy.Integration/Node/NodeHostRegistryTests.cs`, `HealthEndpointTests.cs`); laptop setup (`src/Zyggy.Core/Setup/*`, `tests/Zyggy.Integration/Setup/SetupHarness.cs`, `CoreTemplateFixture.cs`); the git seam (`src/Zyggy.Core/Git/GitClient.cs`, `tests/Zyggy.Core.Tests/Infrastructure/RecordingProcessRunner.cs`, `tests/Zyggy.Integration/Infrastructure/HookedProcessRunner.cs`).

**Phase**: 47 closes no §12 phase; no `Gates/P<n>_*.cs` file. The last slice carries the documents, the founding-spec wording W47-1..W47-6 (pasted only after the owner approved it) and the owner-run AC-19 at its gate.

**What this plan deliberately is not** (spec Out of scope): no Hub proxy, no laptop `SessionStart` digest (21); no publish per `remember`/`archive` commit; no line-level filter and no split of `profile.md`; no signing of the mirror; no automatic history rewrite or force push; no laptop write to the mirror; no memory URL on any laptop; no new NuGet package (`SourceHygieneTests.Packages_RowCountPinned` unchanged); never the real `claude`, never GitHub, never the real secret store or `~/.claude` in a test.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — Central builds the snapshot a machine may read (fake) | 1 | for a memory tree and a target, the snapshot is computed in memory: reach, never-copied areas, sidecars only under `archive/`, symlinks skipped, withheld files, `MIRROR.md`; the configuration refuses a work reach outside `outboundPaths`, an empty `outboundPaths` and a target equal to the memory remote | 🛑 after Step 1 (⚠️ work boundary; ⚠️ public API; ⚠️ new `node.json` keys) |
| B — `zyggy memory mirror publish` pushes to each mirror (wire) | 2 | against a local bare memory remote and two bare mirrors, one `mirror YYYY-MM-DD <sha8>` commit per changed target, idempotent, removals carried, no memory history, `skipped unpushed`, foreign commit → exit 6 no force, `--dry-run`, per-target deploy key | 🛑 after Step 2 (⚠️ secrets: deploy keys; ⚠️ CLI surface) |
| C — Every dream pass refreshes the mirrors (wire) | 3 | `zyggy dream` publishes every target after any outcome, exit and outcome unchanged, run record `mirror` entries; a `remember` commit waits for the next pass or a manual publish | 🛑 after Step 3 |
| D — A laptop node decides when to clone, refresh, refuse or wait (fake) | 4 | the refresh state machine (`absent`/`current`/`failed`/`refused`) over a recorded `IProcessRunner`, an in-memory secret store and a fake clock; `mirror-pat` reaches git only through the call's environment with helpers off | 🛑 after Step 4 (⚠️ work boundary; ⚠️ secrets; ⚠️ public API) |
| E — A laptop node keeps a read-only clone current (wire) | 5 | the Node host clones a bare mirror at start, applies a newer snapshot within one interval at depth 1, discards local edits, refuses and deletes a clone it may not hold; `/health` `mirror`; `zyggy memory mirror status` / `refresh` | 🛑 after Step 5 (⚠️ work boundary; ⚠️ secrets sweep) |
| F — Agents on a laptop browse the mirror; definition of done | 6, 7 | `zyggy setup` wires the clone read-only into Claude Code, writes `allowedPaths`, names `mirror-pat` (6); `zyggy-core` `memory` skill with bats, READMEs, runbooks, founding-spec wording (7) | 🛑 after Step 7 — **definition of done** (owner-run AC-19) |

Every step ends with PROVE = `node .claude/hooks/verify.mjs` (build, unit tests, integration tests, format check), all green, 0 warnings; the `zyggy-core` side (Step 7) is proven by its bats suite.

<!--
Decomposition strategy: vertical slices, Fake then Wire on each side of the boundary. Central first (A fake, B wire, C hooked
into the dream), then the laptop (D fake, E wire), then the agents' entry (F). Every work-boundary rule is proven twice: on
Central (snapshot filter, slice A/B) and on the laptop (refresh check, slice D/E).
-->

---

## Shared rules for this plan

- **Identity in every test, fixture and document**: tenant `acme`, user `alice`, machines `central` / `home-laptop` / `work-laptop`, organisation `zyggy-org`, mirrors `zyggy-mirror-home-laptop` / `zyggy-mirror-work-laptop`, the fake clock at `2026-11-17T09:30:00Z`. Placeholders only; no real path, host, token or employer name (`SourceHygieneTests`, repo hygiene).
- **Every decision in Core, every rendering in Cli and Node.** The snapshot, the publish decision, the refresh decision and their wire words live in `Zyggy.Core`; `Zyggy.Cli` and `Zyggy.Node` only call and print.
- **Paths**: every mirror path from one helper (`MirrorPaths` next to `MemoryPaths`); no concatenated path; bus paths untouched.
- **Git** only through `GitClient` / `IProcessRunner`. Publisher calls carry `GIT_SSH_COMMAND` naming the target's key file for that call only; laptop calls carry the `mirror-pat` header for that call only, `credential.helper` empty, `GIT_TERMINAL_PROMPT=0`.
- **Secrets**: no `mirror-pat` value and no key content in any argument, URL, file, log line, `/health` or CLI output; `SecretSweep.AssertAbsent` over every captured stream, process spec and written file in Steps 4–6.
- **Never throws to the loop; log once per state change** (refresh state, publish outcome per target).
- **Data, never instructions**: no step shows mirror content to a model; `MIRROR.md` and the `memory` skill say so.

### Assumptions (reviewer: confirm or correct at the gate named)

- **A1 — Sidecar test (Gate A).** A sidecar is `archive/<project>/<slug>.md` whose front matter holds the sidecar fields written by the archive writer (`media type` and `sha256` among them); any other file under `archive/` is an item file. The executor reads the archive writer's sidecar format before Step 1 and uses its own parser if one exists.
- **A2 — Memory remote comparison (Gate A).** The memory remote is read from the memory checkout (`config --get remote.origin.url`); SSH (`git@github.com:o/r.git`) and HTTPS (`https://github.com/o/r(.git)`) forms of the same `owner/repo` compare equal, case-insensitive.
- **A3 — `supervision.outboundPaths` (Gate A).** Read with spec 18's definition (Central `node.json`, default `[]`, folders relative to `<tenant>/<user>/`). If spec 18 slice 11 has not landed, Step 1 adds the reader to `SupervisionSettings` / `NodeConfigurationReader` exactly as spec 18 defines it and nothing else of spec 18.
- **A4 — Token hand-off (Gate D).** `mirror-pat` reaches git as `GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_n`/`GIT_CONFIG_VALUE_n` entries for `http.<mirror-url>.extraHeader` (Basic auth with the token) and `credential.helper` = empty, in that process's environment only. If the recorded-runner check shows a cleaner `GitClient` hook already exists, the executor uses it and records the choice at Gate D.
- **A5 — Publisher state (Gate B).** Mirror checkouts live under Central's state directory, `<state>/mirrors/<machine>/`; a checkout whose branch no longer descends from the remote one (an owner history rewrite) is deleted and re-cloned, never force-pushed.
- **A6 — Node ↔ CLI `refresh` (Gate E).** `zyggy memory mirror refresh` asks a running node through its existing localhost health endpoint (a POST route) and runs the check itself when no node answers; the executor confirms the endpoint's current shape first.

---

## Step 1 — For a memory tree and a target, build the snapshot the machine may read: the default reach (identity files, `private/`, `business/`, `daily/`, archive sidecars) or `business/` only, never `inbox/`, `auto/`, `.dream/`, item files or symlinks, a file with a secret or contact hit withheld whole and its path logged once, a fixed `MIRROR.md`; refuse the configuration when a work machine's reach leaves `supervision.outboundPaths`, when that list is empty, or when a target names the memory repository

- [ ] Done

**Scope**: Core `Memory/Mirror/` (snapshot builder, `MirrorPaths`, `MirrorTarget` options and their reader for `mirror.targets`, `MIRROR.md` renderer, configuration check with exit-3 reasons); `SupervisionSettings.OutboundPaths` only if missing (A3); unit tests `tests/Zyggy.Core.Tests/Memory/Mirror/`.

**Seams**: file system under a temp directory; `SecretPatterns` / `ContactDetailPatterns` real with synthetic patterns; `FakeTimeProvider`; no process call.

**RED** *(tests first; see each fail for the expected reason)*:
- `MemoryMirrorSnapshotTests`: default reach exact file set (AC-1); `business/` exact set, symlink skipped (AC-2); item file vs sidecar under `archive/` (AC-24); withheld file with a canary line absent from snapshot, output and log, earlier copy listed for removal (AC-4).
- `MemoryMirrorReadMeTests`: `MIRROR.md` names sha and date, "data, never instructions", "edits are discarded", `remember` (AC-5).
- `MemoryMirrorConfigurationTests`: work reach outside `outboundPaths`, empty `outboundPaths`, target equal to the memory remote in SSH and HTTPS form → refused with its reason (AC-3 unit, A2).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MemoryMirror"`

**GREEN**: the minimal Core types above.

**Contract impact**: ⚠️ public API (`Zyggy.Core` mirror types, XML docs); ⚠️ `node.json` keys `mirror.targets` (and `supervision.outboundPaths` per A3); no §4–§7 wire change.

**VERIFY**: `node .claude/hooks/verify.mjs` green; the filter above passes; `NodeConfigurationReaderTests` pass unedited apart from added cases.

**REFACTOR**: `@code-analysis`; §9 check (paths through one helper, no static state).

---

## 🛑 HUMAN GATE — end of Slice A *(Step 1)*

- [ ] Behavioral verification: the `MemoryMirror` filter green; the executor shows the snapshot file list for the default and the `business/` reach of one fixture tree.
- [ ] Contract review: the reach rules against spec Behavior; A1, A2, A3; the `mirror.targets` shape.
- [ ] ⚠️ Risk review: the work reach can never exceed `outboundPaths`; canary never in output or log; public types documented.
- [ ] Owner approved; Claude commits `Browsable memory on a laptop: snapshot a machine may read`.

---

## Step 2 — `zyggy memory mirror publish [--machine] [--dry-run]` against a local bare memory remote and two local bare mirrors: one `mirror YYYY-MM-DD <sha8>` commit per target whose tree changed, nothing on a second run, removals carried, no memory history in a mirror, `skipped unpushed` when memory HEAD is ahead of its remote, a foreign mirror commit → `failed git_error` exit 6 with no force push, `--dry-run` lists and touches nothing, each push with its own deploy key, exit 3 / 4 / 8 as specified

- [ ] Done

**Scope**: Core mirror publisher (checkout per target under the state directory, A5; commit and push through the existing push-with-one-rebase-retry path of `MemoryPublisher`; own lock); Cli `memory mirror publish` verb; `tests/Zyggy.Integration/Memory/MirrorPublishTests.cs` with a `MirrorRepoFixture` (bare mirrors beside `MemoryRepoFixture`).

**Seams**: unit — `RecordingProcessRunner` (per-call `GIT_SSH_COMMAND`, no `--force` in any push); integration — real git via `HookedProcessRunner` on local bare repositories, deploy key file = a placeholder file whose path is asserted in the recorded `GIT_SSH_COMMAND` (local `file://` remotes do not use SSH).

**RED**:
- Unit `MirrorPublisherTests`: every push spec carries the target's key file and no force flag; missing key file fails that target only (AC-22).
- Integration `MirrorPublishTests`: happy path two targets (AC-6); second run no commit, exit 0 (AC-6); delete and move-out (AC-7); `rev-list`/object check — no memory commit or memory-only object in a mirror (AC-8); unpushed HEAD → `skipped unpushed` (AC-9); foreign commit → exit 6, foreign commit kept (AC-10); `--dry-run` lists four kinds, no repository changed (AC-11); reach refusal and memory-repository target → exit 3, nothing pushed (AC-3); lock held → exit 4; withheld file → exit 8.
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~MirrorPublish"`

**GREEN**: publisher + verb, minimal.

**Contract impact**: ⚠️ CLI surface `zyggy memory mirror publish` and its exit codes (spec CLI table); ⚠️ secrets: deploy key per mirror (path only in `node.json`).

**VERIFY**: `node .claude/hooks/verify.mjs` green; `DreamPushTests` and `ArchiveAddPushTests` unedited and green (shared push path untouched in behavior).

**REFACTOR**: `@code-analysis`; §9 check.

---

## 🛑 HUMAN GATE — end of Slice B *(Step 2)*

- [ ] Behavioral verification: `MirrorPublish` filter green; the executor shows one real publish and one `--dry-run` output on the fixture.
- [ ] Contract review: verb, options, exit codes, commit message format, A5.
- [ ] ⚠️ Risk review: never a force push (grep of the recorded specs); key content never read or logged.
- [ ] Owner approved; Claude commits `Browsable memory on a laptop: publish each snapshot to its own mirror`.

---

## Step 3 — `zyggy dream` publishes every target after the pass whatever its outcome: exit code and outcome unchanged, one `mirror` entry per target in the run record, a failed target retried by the next pass; a `remember` commit with no pass after it leaves every mirror unchanged until a pass or a manual publish

- [ ] Done

**Scope**: `DreamRunner` end step calls the mirror publisher; `DreamRunRecord` gains the `mirror` entries; `tests/Zyggy.Integration/Dream/DreamMirrorTests.cs`.

**Seams**: fake-claude through the dream's existing harness; real git on bare repositories.

**RED**:
- `DreamMirrorTests`: `committed` pass → each mirror holds the new snapshot, exit and outcome equal to the same run without targets, one entry per target (AC-12); `nothing_to_do` with a newer `remember` commit → published (AC-12); a mirror whose bare repo is removed → dream still `committed`, entry `failed`, next pass publishes (AC-13); `remember` alone → mirrors unchanged, then manual publish → published (AC-23).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~DreamMirror"`

**GREEN**: the dream hook and the record field.

**Contract impact**: dream run record gains `mirror` (additive; the record's reader tolerates its absence in older records).

**VERIFY**: `node .claude/hooks/verify.mjs` green; every existing `Dream*Tests` unedited and green.

**REFACTOR**: `@code-analysis`; §9 check.

---

## 🛑 HUMAN GATE — end of Slice C *(Step 3)*

- [ ] Behavioral verification: `DreamMirror` filter green; the executor shows a run record with `mirror` entries.
- [ ] Contract review: dream exit and outcome unchanged (AC-12 evidence).
- [ ] Owner approved; Claude commits `Browsable memory on a laptop: every dream pass refreshes the mirrors`.

---

## Step 4 — The laptop refresh decides, through a recorded `IProcessRunner`, an in-memory secret store and a fake clock: clone when absent, nothing when the remote head is unchanged, fetch + hard reset + clean when it moved; `refused` and delete on a wrong remote, a repository name other than `zyggy-mirror-<this machine>`, no `MIRROR.md`, or a path outside `mirror.allowedPaths` or under `inbox/`/`auto/`/`.dream/`; `failed` with the clone kept on a git error or a missing `mirror-pat`; `mirror.path` inside a project root, the bus checkout or `zyggy-core` refused at start; logs once per state change, never throws

- [ ] Done

**Scope**: Core `Memory/Mirror/` refresh state machine (record state + pure decision, as `PollState`), laptop options `mirror.repository`/`path`/`allowedPaths`/`refreshMinutes` and their reader, the `mirror-pat` hand-off (A4); unit tests `tests/Zyggy.Core.Tests/Memory/Mirror/MirrorRefreshTests.cs`.

**Seams**: `IProcessRunner` = `RecordingProcessRunner`; `ISecretStore` = `InMemorySecretStore`; `FakeTimeProvider`.

**RED**:
- `MirrorRefreshTests`: each transition of the spec's state diagram (AC-14); wrong name / wrong path refused at start (AC-17 unit); every recorded git spec: token only in the environment, `credential.helper` empty, no token in arguments or URL, `GIT_TERMINAL_PROMPT=0` (AC-21 unit); one log line per state change over three unchanged ticks.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MirrorRefresh"`

**GREEN**: minimal Core types.

**Contract impact**: ⚠️ public API; ⚠️ `node.json` laptop keys; ⚠️ new secret name `mirror-pat`; ⚠️ founding spec §5 exception (W47-6, pasted in Step 7).

**VERIFY**: `node .claude/hooks/verify.mjs` green.

**REFACTOR**: `@code-analysis`; §9 check (pure state machine, `TimeProvider`, no static state).

---

## 🛑 HUMAN GATE — end of Slice D *(Step 4)*

- [ ] Behavioral verification: `MirrorRefresh` unit filter green; the executor shows the recorded git specs of one fetch with the token replaced by its fingerprint.
- [ ] Contract review: state words, laptop keys and defaults, A4.
- [ ] ⚠️ Risk review: the account-wide login is never used for the mirror (helpers off); no memory URL anywhere on the laptop side.
- [ ] Owner approved; Claude commits `Browsable memory on a laptop: laptop refresh decisions`.

---

## Step 5 — The Node host against a local bare mirror: clones at start, applies a newer snapshot within one `refreshMinutes`, holds one commit (depth 1), discards a local edit and an untracked file; a snapshot with a `private/` file on a `["business/"]` node and a bare repository laid out like memory are refused with no clone left; `/health` shows `mirror`; `zyggy memory mirror status` exits 0 / 1 / 5 / 6 and `refresh` checks once

- [ ] Done

**Scope**: Node `MirrorRefreshTimer` (`BackgroundService`, as `DiscoveryTimer`), `/health` `mirror` object, Cli `memory mirror status [--json]` and `refresh` (A6); `tests/Zyggy.Integration/Node/MirrorRefreshTests.cs` with `MirrorRepoFixture`.

**Seams**: real git via `HookedProcessRunner` on local bare repositories (the token header is set but unused by `file://`; its absence from every recorded argument is asserted); file secret store over the scratch root; `FakeTimeProvider` driving the timer through the host.

**RED**:
- `MirrorRefreshTests` (integration): clone at start, newer snapshot within one interval, `rev-list --count HEAD` = 1, local edit and untracked file gone (AC-15); `private/` on a `business/` node → no clone, `/health` `refused` with the area, `status` exit 5 (AC-16); wrong name, `mirror.path` inside the bus checkout, memory-shaped bare repo → refused (AC-17); `SecretSweep` over logs, `/health`, CLI output and every process spec (AC-21).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~MirrorRefresh"`

**GREEN**: timer, health field, two verbs.

**Contract impact**: `/health` gains `mirror` (additive); CLI surface `memory mirror status` / `refresh`.

**VERIFY**: `node .claude/hooks/verify.mjs` green; `HealthEndpointTests`, `NodeHostStartupTests` green (a node without `mirror.repository` shows `absent` and starts as before).

**REFACTOR**: `@code-analysis`; §9 check (never throws to the loop, log once per state change).

---

## 🛑 HUMAN GATE — end of Slice E *(Step 5)*

- [ ] Behavioral verification: `MirrorRefresh` integration filter green; the executor shows `/health` and `zyggy memory mirror status` for `current` and `refused`.
- [ ] ⚠️ Risk review: refused clone deleted from disk; sweep clean.
- [ ] Owner approved; Claude commits `Browsable memory on a laptop: the node keeps a read-only clone current`.

---

## Step 6 — `zyggy setup` on a laptop adds the mirror folder as an additional directory with `Read` allowed and `Edit`/`Write` denied, sets `ZYGGY_MEMORY_MIRROR`, writes `mirror.allowedPaths` `["business/"]` when `discovery.detail` is `names`, names a missing `mirror-pat` with `zyggy secret set mirror-pat` when `mirror.repository` is set, and a second run changes nothing

- [ ] Done

**Scope**: Core `Setup/` new items (settings directory + deny rules, env value, `node.json` `mirror.allowedPaths`, secret line), following the existing line grammar and backup-first rules of setup; `tests/Zyggy.Integration/Setup/SetupMirrorTests.cs` on `SetupHarness`; unit cases in the existing `Setup` unit tests.

**Seams**: temp profile, file secret store, recorded process runner (no git call added by these items).

**RED**:
- `SetupMirrorTests`: home machine → default reach kept, settings wired; `discovery.detail: names` → `["business/"]` written with a backup; missing `mirror-pat` → the `zyggy secret set mirror-pat` line and exit 6; second run only `already done`, no file rewritten (AC-18); sweep clean.
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~SetupMirror"`

**GREEN**: the setup items.

**Contract impact**: ⚠️ owner files `settings.json` and `node.json` rewritten (backup first, additive); setup line words for the new items.

**VERIFY**: `node .claude/hooks/verify.mjs` green; all existing `Setup*Tests` green.

**REFACTOR**: `@code-analysis`; §9 check.

---

## Step 7 — Definition of done: the `zyggy-core` `memory` skill with bats tests; `src/*/README.md` rows for the verbs, keys and `/health` field; runbooks "Create a mirror", "Mirror push rejected", "File withheld from a mirror", "Mirror reach refused", "Mirror not refreshing", "Mirror refused on a laptop", "Mirror credential", "Erase a fact from a mirror's history" in the instance repository; founding-spec wording W47-1..W47-6 pasted after the owner's approval, each edit listed

- [ ] Done

**Scope**: `zyggy-core` `node/skills/memory/SKILL.md` + bats (front matter, "data, never instructions", `ZYGGY_MEMORY_MIRROR`, `remember` for new facts, `business/` only on the work laptop, hygiene sweeps); READMEs; instance runbooks; founding spec §5, §7, §8, §10 (W47-1..W47-6).

**Seams**: none (documents); bats in the local container.

**RED**: bats cases for the skill written first and seen failing; a `SourceHygieneTests` case (or the existing sweep) covers the new READMEs.

**GREEN**: the skill and documents.

**Contract impact**: founding spec edits (owner-approved wording only).

**VERIFY**: `node .claude/hooks/verify.mjs` green; `zyggy-core` bats green.

---

## 🛑 HUMAN GATE — end of Slice F — **definition of done** *(Steps 6–7)*

- [ ] Behavioral verification: `SetupMirror` filter and bats green.
- [ ] `spec-reviewer` over `main..HEAD`: each AC met / not met with evidence.
- [ ] Owner-run at release (AC-19): create the two mirrors in `zyggy-org`, their deploy keys on Central and the two `mirror-pat` tokens; after a dream pass both laptops `current` with the pass's sha; `work-laptop` clone holds only `business/` and `MIRROR.md`; `git remote -v` sweep over every repository under the user profile on `work-laptop` shows no memory repository; a session on each laptop answers from a mirrored fact through the `memory` skill.
- [ ] Owner approved; Claude commits `Browsable memory on a laptop: agents browse the mirror` and runs the `ship` skill (MINOR).
