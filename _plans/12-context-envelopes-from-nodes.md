# Plan: 12 — Nodes feed memory: `zyggy context` and the `Stop` hook emit signed context envelopes — "As the owner in a hand-run Claude Code session on the home laptop, I want a fact I state ("remember that …") and a mechanical note of each session to travel to Central as a signed `context` envelope on the bus, without anything durable being written on the laptop, so that laptop sessions feed the one memory Central owns and the P1 walking-skeleton gate has its memory half ready for 13 to close"

> **Scheduling note.** Branch `feature/12-context-envelopes` from `main` once 11 (`feature/11-hub`) is merged: this plan builds on 11's `src/Zyggy.Core/Hub/RememberTool.cs`, `RememberService`, `SecretPatternsLocation` and the `TreeFingerprint` helper, and on 08's `JobSubmitter` / `BusHost` / `SubmitCommand`. The sibling template repository `zyggy-core` (`D:\source\zyggy-core`) gets its own branch `feature/12-node-context`, **created by the owner**, for Step 9; both repositories are left **uncommitted** at every 🛑 HUMAN GATE for the owner's review (auto-memory "Owner reviews before commit"); the agent never commits, pushes or tags. The five founding-spec amendments **A12-1..A12-5** are **already pasted** into `_specs/00 …` (§3 Hooks and Skills, §4 Write rules, §6 Invocation, §7 step 1 and Rules); this plan assumes them and edits the founding spec nowhere. The roadmap wording of entry 12 ("`tenants/<org>/nodes/central/context/`" → the sender's own folder, D1) is the project-manager's edit, not this plan's.

## Overview

After this plan a laptop has three ways onto the bus and none into memory. `zyggy context [--scope …] -- "<fact>"` builds one `[stated]` line, scans it against `secret-patterns.txt` (fail closed: no patterns, no envelope), signs a `context` envelope with the machine's `signWith` key, proves the bytes round-trip (08's self-check), and writes it into the **sender's own** `tenants/<tenant>/nodes/<me>/context/<ULID>.md` addressed `to: central` (D1) through the one §5 write sequence (`BusRepository.PublishContextAsync`: lock, pull, add, commit `<tenant> context <id> <from>→central`, push ×4, `Pushed | Deferred | WriteFailed`). `zyggy context --stop-hook` (the laptop `Stop` hook, D2) appends one `[observed]` line per turn to a per-session **spool** `<nodeDir>/context/<tenant>/<session_id>.md` — milliseconds, no key, no git, no network, capped at 150 lines, notes cut at 240 characters, secret-shaped notes refused by pattern name. `zyggy context --flush` (the `SessionEnd` hook) ships the session's spool — and any spool older than 60 minutes — as one signed envelope each, one fact per line, deleting a spool only after `Pushed` or `Deferred`. The job runner sets `ZYGGY_HOOKS=off` in every `claude -p` job environment (D5) so none of this fires inside a job. `zyggy verify` on Central reports the file `OK` (the writer is the folder's machine), `zyggy status` counts it under `context`. The `zyggy-core` template gains the laptop material under `node/` — two ≤ 10-line launchers, the laptop `remember` skill calling `zyggy context`, and `node/README.md` describing the wiring 15 installs — proven by bats against the `zyggy` stub.

Implements `_specs/12-context-envelopes-from-nodes.md` (approved 2026-10-29, zero Open Questions; D1–D5 and the Contracts are binding: AC-1..AC-28, the Decision Table's Keep/Reshape/Defer rows). Founding-spec sections: §3 Components (Skills `remember` node side, Hooks `Stop` and `SessionEnd`), §4 Bus protocol (Repository layout, Write rules as amended, Envelope context fields, Mandatory fields, Signature, Canonical form), §5 retry rule, §6 Invocation (`ZYGGY_HOOKS=off`), §7 (nodes never write durable memory; Rules), §8 Secrets and Audit, §9 folder map and design rules, §10 `node.json` and Laptops, §11 Runbooks, §12 P1 definition of done, §13 decisions, §14 Tenancy.

**Reference pattern** (read across all layers; mirrored here): **deliverable 08's sender side** — `src/Zyggy.Core/Bus/JobRequest.cs` (`TryCreate` → `JobRequestError(Field, Detail)`, every option checked by shape before anything is signed), `JobSubmitter.cs` (`BuildAsync`: header from `bus.Me`, `EnvelopeId.New(now)`, sign, serialise, `ParseAndVerifyAsync`, `Differs` over every §4 field and the `Section4Keys` set; `SubmitAsync`: the `Built | BuildRefused` → `Submitted | SubmitDeferred | SubmitFailed | SubmitRefused` mapping, `SigningFailedException` → stage `sign`, `SecretStoreUnavailableException` → stage `secret-store`; `LoggerMessage` lines that never carry the body), `BusRepository.SubmitAsync` + the private `WriteAsync` (checkout lock, `PullUnlockedAsync`, change, `CommitOnlyAsync`, push with `MaxPushAttempts = 4` and jitter, `RestoreAsync`, `AssertMayWrite(path, folder)`, `Subject(action, id, from, to)`), `SubmitResult.cs` (the result records), `Bus/ServiceCollectionExtensions.cs` (`TryAddSingleton` factory for `JobSubmitter` over `KeyId`), `src/Zyggy.Cli/Commands/SubmitCommand.cs` (System.CommandLine, `BusHost.LoadConfiguration` → `signWith missing` → `BuildServices` → `CheckCheckoutAsync` → stdin when the positional is absent → `TryCreate` → the exit-code switch with the 08 lines verbatim), `BusHost.cs`, `ExitCodes.cs`, `CliApplication.cs`; unit tests `tests/Zyggy.Core.Tests/Bus/JobSubmitterBuildTests.cs` / `JobSubmitterSubmitTests.cs` (`TempCheckout`, `RecordingProcessRunner`, `Substitute.For<IBusProvider>()`, `RecordingLogger<T>`, `FakeTimeProvider`, `InMemorySecretStore` via `TestKeys.StoreWithBothAsync`, `ThrowingSecretStore`); integration tests `tests/Zyggy.Integration/Cli/SubmitCommandTests.cs` over `CliHarness` (`CentralNodeJson` on `Bus.SenderDir`, `HomeLaptopNodeJson` on `Bus.CloneDir`, `WriteNodeJson(..., nodeDir:)`, `Hooks.Override` for the four refused pushes, `Bus.DeleteBare()` for the pull failure, `ParseOnMainAsync`, `PorcelainAsync`, `CommitCountOnMainAsync`, `SecretSweep.AssertAbsent`), `VerifyCommandTests` / `StatusCommandTests` (the `OK` lines and the `queued claimed rejected reports context` table). Memory side: `RememberService` (the line `- [<tag>] <date><hint><provenance>: <fact>`, the one refusal), `RememberArguments` (the scope messages and the `^[a-z0-9][a-z0-9-]*$` project label), `RememberVerb` (the `ZYGGY_HOOKS=off` early exit, `SecretPatternsLocation.Resolve` + `SecretPatterns.Load` fail-closed sentences), `FactValidator.Cut` (240 characters), `TextCollapse.Line`, `src/Zyggy.Core/Hub/RememberTool.cs` (the Hub's refusal text), `tests/Zyggy.Core.Tests/Memory/SecretPatternsTests.cs` (`SecretSamples` / `BenignSamples` from `tests/golden/secret-patterns/`). Runner side: `JobRunner.PipelineAsync` (`Environment = _options.ModelEnvironment`), `ClaudeCodeCliRunner.RunAsync` (`request.Environment` → `ProcessSpec.Environment`), `tests/Zyggy.Core.Tests/Jobs/JobRunnerInvocationTests.cs` (`ClaudeSpec().Environment`), `tests/Zyggy.Integration/Jobs/JobRunnerRoundTripTests.cs` (`TestHost.BuildRunnerListener(..., hooks:)`, `HookedProcessRunner.Calls`). Template side (`zyggy-core`): `.claude/hooks/session-start.sh` (the thin-launcher shape), `.claude/hooks/stop.sh` (Central's note rule: first non-empty line, 240 characters, secret refusal, cap marker), `tests/launchers.bats` (argv, stdin pass-through, every exit mapped, ≤ 10 lines, no `jq`/`sed`/`awk`/`case`/`source`), `tests/stop.bats`, `tests/fixtures/zyggy-stub.sh` + `tests/helpers.bash` (`install_zyggy_stub`, `ZYGGY_STUB_LOG`/`_STDIN`/`_STDOUT`/`_STDERR`/`_EXIT`), `tests/repo.bats` (the hygiene sweeps, the "documented commands" test, the spec-11 Hub test at line 983), `.claude/skills/remember/SKILL.md`, `README.md` ("Node-side material (deliverables 12/15) goes under `node/`, with instruction templates under another file name"), `.claude/rules/operations.md`. Sibling-repo step shape: `_plans/11-hub-context-remember.md` Step 7.

**Phase**: 12 serves P1 but does not close it (13 does: it appends part 2 — context envelope → ingest → `get_context` returns it — to `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`). That file is **never edited** here (`git diff tests/Zyggy.Integration/Gates/` empty at every gate). Planner rule 15 is therefore adapted as in plan 11: the last slice is the definition-of-done slice (D5, the template, the runbook rows, the hygiene sweeps) and its 🛑 HUMAN GATE closes the deliverable. The round trips the roadmap's DoD names — `zyggy context` lands a signed, verifiable context envelope on the bare repo with tenant `acme`; a hook invocation (`--stop-hook` then `--flush`) does the same; `verify` accepts both — are Steps 3 and 7.

**What this plan deliberately is not** (spec Out of Scope / Defer): no ingest into `inbox/`, no archive move, no change to the dream or to `Gates/P1_WalkingSkeleton.cs` (13); no laptop install, no `discover --link`, no `zyggy report`, no `PROTOCOL.md` content, no `PreToolUse` hook, no edit of the template's `.claude/settings.json` (15 installs the user-scope hooks); no `hub --proxy` or its `remember` tool (21); no batching of several explicit facts in one envelope; no DLP, no `share: true`, no work node (19/20/24); no `--to`, `--tag` or `--source` option; no Central route through this verb (AC-12 refuses it); no Node-service change (the spool shipper inside `Zyggy.Node` was D2's rejected alternative); no `node.json` key (no `role`, no `agentCore`, no spool key); no `transcript_path` read, no model call from a hook; no change to Central's `stop.sh`, `session-start.sh`, the digest, `RememberService`'s behaviour (byte for byte — its existing tests), `secret-patterns.txt`, `BusAuditor`, `BusInventory`, `AssertMayWrite`, `EnvelopeSigner.Canonicalize` or any golden file; **no new NuGet package** (`Directory.Packages.props` stays at 24 rows — `Packages_RowCountPinned` is the guard; the hook JSON is read with `System.Text.Json` source generation from the shared framework).

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — A stated fact becomes a signed context envelope in the sender's own folder | 1, 2, 3 | one `[stated]` line is built, scanned, signed and self-checked (1); it reaches `tenants/acme/nodes/home-laptop/context/<id>.md` through the §5 write sequence with every outcome named and nothing written on a refusal (2); `zyggy context` does it from `home-laptop`'s `node.json` on a bare repository, `verify` on `central` says `OK`, `status` counts it, every refusal and failure has its exit code and line (3) | 🛑 after Step 3 (⚠️ §4 shared contract D1; ⚠️ secrets; ⚠️ public API A1) |
| B — A turn's note is spooled on the node without touching the bus | 4, 5 | the `Stop` JSON becomes one `[observed]` line in `<nodeDir>/context/acme/<session>.md` with the scope derived from `cwd`, refused notes never land, the 150-line cap writes its marker once, unparseable input is ignored (4); `zyggy context --stop-hook` does it in process with no git process started and no secret-store access (5) | 🛑 after Step 5 (⚠️ secrets: the note refusal; the spool is node state, never memory) |
| C — A session's spool ships as one envelope at `SessionEnd` | 6, 7 | the named session's spool and every stale spool become one envelope each, hand-edited secret lines are dropped by name, a spool is deleted only after `Pushed`/`Deferred` and kept on a failed write (6); `zyggy context --flush` lands the envelope on the bare repository, `verify` says `OK`, offline keeps the spool, deferred deletes it and the next write pushes both (7) | 🛑 after Step 7 (⚠️ §4 shared contract: the body 13 copies verbatim) |
| D — Job runs are unattended, the laptop template, and the definition of done | 8, 9, 10 | every `claude -p` job run carries `ZYGGY_HOOKS=off` (8); `zyggy-core` holds `node/hooks/stop.sh`, `node/hooks/session-end.sh`, `node/skills/remember/SKILL.md`, `node/README.md`, the stub knows `context`, bats pins it all (9); the Cli README verb row and troubleshooting rows, the Core README rows and the hygiene facts exist (10) | 🛑 after Step 10 — **definition of done** (⚠️ §6 shared contract D5; public hygiene in both repositories) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally; the `zyggy-core` side (Step 9) is proven by its bats suite in the local container (`memory/sibling-repos-and-goldens.md`: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/"`).

<!--
Decomposition strategy: vertical slices, not horizontal layers. Slice A proves the explicit fact through the seams (a substituted
IBusProvider, a RecordingProcessRunner, an InMemorySecretStore, a FakeTimeProvider — Steps 1–2) and then wires `zyggy context`
over the real git against the bare repository (Step 3). Slice B is the pure spool (Step 4) and its verb mode (Step 5): the hook path
never reaches the bus, which the wire step proves by an empty process log. Slice C ships the spool (fake in Step 6, bare repo in
Step 7). Slice D is D5 (one production line, unit + integration), the template content proven by bats against the stub, and the
definition of done. Never the real claude, never GitHub, never geoffrey in any test.
-->

---

## Shared rules for this plan

- **Identity in every test, fixture and example**: tenant `acme`, machines `central` / `home-laptop`, project `calizr`, session id `0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e` (the template's `stop-input.json` fixture; its first 8 characters `0b7c3d1e`), key `acme/1` (`TestKeys.Acme1`), the fake clock at `2026-10-28T10:42:07Z` unless a test says otherwise. `geoffrey`, a GUID that is not the fixture session id, a mail address or an absolute home path appear nowhere (`SourceHygieneTests`; Step 10's sweep; `zyggy-core`'s `hygiene_paths` / `hygiene_principal` / `hygiene_words`). README and runbook paths are placeholders (`C:/agents/zyggy-core`, `<bus.localPath>`, `<nodeDir>`).
- **Every decision in Core, every rendering in Cli** (08's rule for `submit`): `ContextCommand` reads options, picks the mode, calls one Core entry point and prints the result; no validation text is born in `Zyggy.Cli`.
- **Order of checks in every mode** (A2): `ZYGGY_HOOKS=off` → exit 0, nothing (before `node.json`); `node.json` (exit 3, `BusHost.LoadConfiguration`); this machine is `central` → exit 2 `zyggy: to: this machine is central; use zyggy memory remember`; then per mode — explicit: `signWith missing` (3) → secret patterns (3, `SecretPatternsRegistration.AddSecretPatterns`, A1) → `BusHost.BuildServices` + `CheckCheckoutAsync` (3) → fact and scope (2) → `ContextSubmitter.SubmitAsync`; `--stop-hook`: secret patterns (3) → `BuildServices` (no checkout check, no bus) → `ContextStopHook.Run`; `--flush`: `signWith missing` (3) → secret patterns (3) → `BuildServices` + `CheckCheckoutAsync` (3) → `ContextFlusher.FlushAsync`.
- **Secrets**: the signing key is read only inside `EnvelopeSigner.SignAsync` through `ISecretStore`; a refused fact or note appears in no stdout, stderr, log line, process argument or file — only the pattern **name**; git stderr is redacted by `BusRepository.RedactedDetail` as 08 does. `SecretSweep.AssertAbsent` runs over every output in the integration tests.
- **Paths**: every bus path through `BusPaths` (`Context(tenant, me)`, `EnvelopeFile`); the spool directory through the one builder `ContextSpool.DirectoryFor(nodeDir, tenant)` (A6, the `TranscriptPaths` precedent for node state); no memory path anywhere in this deliverable (`MemoryPaths`, `MemoryEnvironment`, `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER` are never named — Step 10's fact).
- **Shared-contract edits**: none on the wire format (`EnvelopeSigner`, `EnvelopeWriter`, `BusPaths`, `PROTOCOL.md`, `node.json` schema, `tests/golden/*` untouched — `git diff tests/golden/` empty at every gate); `BusAuditor`, `BusInventory`, `AssertMayWrite`, `RememberService`'s bytes, `RememberArguments`, `FactLineWriter`, `SecretPatterns`, `SecretPatternsLocation` unedited (`RememberService` is refactored onto `FactLine.Format` with its tests unchanged — Step 1). `Gates/P1_WalkingSkeleton.cs` unedited.
- **Owner-facing wording** (`memory/owner-facing-wording.md`): "signing key", never "hash"; PowerShell 5.1 snippets where a laptop command is shown.
- **Two repositories, two branches, nothing committed**: `zyggy` on `feature/12-context-envelopes`, `zyggy-core` on `feature/12-node-context` (owner-created); the executor reports per repository at each gate.

### Assumptions (reviewer: confirm or correct at Gate A)

- **A1** — ⚠️ **public API**: `SecretPatterns` and `SecretPatternsLocation` stay `internal` (`Zyggy.Core` has no `InternalsVisibleTo("Zyggy.Cli")`). The CLI reaches them through one new public extension in `src/Zyggy.Core/Memory/SecretPatternsRegistration.cs`: `public static string? AddSecretPatterns(this IServiceCollection services, IReadOnlyDictionary<string, string?> environment)` — resolves the location as `RememberVerb` does (`SecretPatternsLocation.Resolve`, else the sentence `ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)`), loads the file, registers the `SecretPatterns` singleton and returns `null`, or returns the load error without registering (fail closed). `ContextSubmitter`, `ContextStopHook` and `ContextFlusher` are public classes with **internal constructors** (they take `SecretPatterns`), built by DI factories inside Core (`Bus/ServiceCollectionExtensions.AddGitHubBus`, the `JobSubmitter` shape) and resolved by the verb with `GetRequiredService<T>()`. Rejected alternative: making `SecretPatterns` public (a wider surface for one caller).
- **A2** — The `central` refusal (AC-12) runs in **all three modes**, right after `node.json` loads and before any key, pattern or checkout is touched (a Central machine needs none of them for this verb); the launchers turn its exit 2 into 0 on a mis-wired Central, so no session is ever blocked. Order per mode as in Shared rules.
- **A3** — Scope messages are `RememberArguments`' words, field `scope`: `invalid project name in --scope` (AC-5, the `^[a-z0-9][a-z0-9-]*$` label) and `--scope must be general, project:<name> or machine` for any other word; `general` and `machine` are the only bare words, case-sensitive.
- **A4** — Fact messages, field `fact`: `empty fact` (also for whitespace only), `fact longer than 1000 characters` (Unicode scalars after `TextCollapse.Line`), `given both as an argument and on stdin`, and — when no positional is given and stdin is a terminal — `give the fact as the last argument or on stdin` (the `submit` sentence with `fact` for `body`). The stdin text is read to its end and collapsed to one line (`TextCollapse.Line`), so a multi-line paste becomes one fact (the multi-line body is the flush's — spec Defer).
- **A5** — D5 is one line in `JobRunner.PipelineAsync`: the request's `Environment` is `_options.ModelEnvironment` plus `["ZYGGY_HOOKS"] = "off"`, the latter **always winning** (a test option cannot switch the hooks back on); `JobRunnerOptions` and `ClaudeCodeCliRunner` are unedited.
- **A6** — The spool directory `<nodeDir>/context/<tenant>/` is built in exactly one place, `ContextSpool.DirectoryFor(string nodeDir, TenantId tenant)` = `Path.Join(nodeDir, "context", tenant.Value)` — the `TranscriptPaths.TenantDirectory` precedent (node state beside `runs/`, never a bus path, so `BusPaths` is the wrong home); a new hygiene fact pins the literal to that file (and `BusPaths.cs`, which parses the bus folder word). The spool file name is `<session_id>.md`; a session id that is not `[A-Za-z0-9-]{1,128}` is treated as unparseable input (nothing spooled).
- **A7** — A spool's age is its file's `LastWriteTimeUtc` against the `now` passed to `Due(sessionId, now)`; due = the named session's file (any age) plus every other file older than `StaleSpoolAge` (60 min, strictly older). Flush order: the named session first, then the stale ones by file name (ordinal). The `now` comes from the injected `TimeProvider` in the verb; the unit tests set `File.SetLastWriteTimeUtc` and pass `now` explicitly.
- **A8** — `ContextFlusher.FlushAsync` processes **every** due spool even after one fails and returns one `FlushOutcome(SpoolFile File, SubmitResult Result, IReadOnlyList<string> DroppedPatterns)` per spool; the verb prints one `context <id> to central (<path>)` line per `Submitted`/`SubmitDeferred` (the deferral suffix on the latter), one stderr line per dropped pattern (`context: line dropped (pattern <name>)`), the 08 failure line per `SubmitFailed` (`zyggy: context failed at <stage>: <detail>`, the lock/sign/secret-store variants verbatim) with the spool kept, and `zyggy: refusing context: the envelope does not round-trip (<field>)` per `SubmitRefused` with the spool kept. Exit: 6 when any outcome is `SubmitFailed`, else 2 when any is `SubmitRefused`, else 0 (also when nothing was due or the directory does not exist). A spool whose every line was dropped ships nothing and is deleted (AC-21).
- **A9** — Hook-mode stderr lines (the spec names the content, not the exact words): `context: note refused (pattern <name>)` (AC-16, the `stop.sh` shape), `context: cap of 150 hook lines reached for session <id8>` (AC-17, once), `context: unparseable hook input` (AC-15). Nothing on stderr for `stop_hook_active: true`, an empty message, a terminal on stdin, or an already-capped spool.
- **A10** — `node/README.md` states the first release that ships `zyggy context` for the laptop install as **`0.11.0`** (the next tag after `0.10.0`; the executor writes the string the owner names at Gate C); `.claude/zyggy-min-version` stays `0.10.0` (Central's instance does not need the verb — spec Behaviors).
- **A11** — Template tests: the `zyggy` stub accepts `context` with **any** second word (`"context "*` in its `case`: `--stop-hook`, `--flush`, `--scope` and `--` all appear in position 2); the node launchers, the skill and `node/README.md` are tested in a new `tests/node.bats` (the `launchers.bats` shape), and `repo.bats`'s per-script facts (`#!/usr/bin/env bash` + `set -euo pipefail`, shellcheck `-S style`, mode `100755` + LF, "no git invocation") extend their file set from `.claude/**/*.sh` to `node/hooks/*.sh`; the hygiene sweeps (`hygiene_paths`, `hygiene_principal`, `hygiene_words`) cover `node/` as template-owned. The "documented commands" test keeps its regex; `node.bats` runs the three documented `zyggy context` forms against the stub itself.
- **A12** — File homes: Core production under `src/Zyggy.Core/Bus/` (`ContextEnvelopeOptions.cs`, `ContextRequest.cs`, `ContextSubmitter.cs`, `ContextSpool.cs`, `ContextStopHook.cs`, `ContextFlusher.cs`, `HookInput.cs` holding `StopHookInput`, `SessionEndInput`, `StopHookNote` and the source-generated `HookJsonContext`), `src/Zyggy.Core/Memory/FactLine.cs` and `SecretPatternsRegistration.cs`, `src/Zyggy.Cli/Commands/ContextCommand.cs`; unit tests under `tests/Zyggy.Core.Tests/Bus/Context*Tests.cs`; integration tests `tests/Zyggy.Integration/Cli/ContextCommandTests.cs`, `ContextStopHookCommandTests.cs`, `ContextFlushCommandTests.cs`.
- **A13** — `node/README.md` shows the user-scope `~/.claude/settings.json` fragment 15 installs, placeholders only: `Stop` → `C:/agents/zyggy-core/node/hooks/stop.sh` `timeout: 10`, `SessionEnd` → `C:/agents/zyggy-core/node/hooks/session-end.sh` `timeout: 30`, `env.ZYGGY_SECRET_PATTERNS` = `C:/agents/zyggy-core/.claude/hooks/secret-patterns.txt`, `permissions.deny` += `Bash(zyggy secret*)`, `Bash(zyggy context --stop-hook*)`, `Bash(zyggy context --flush*)`; it says the launchers need `bash` on PATH (Git for Windows ships it) and that the install itself is deliverable 15.
- **A14** — `ContextEnvelopeOptions` is a `sealed record` with `init` properties and the spec's defaults, registered as a singleton by `AddNodeServices` with `new()`; no environment or `node.json` knob (spec Configuration).
- **A15** — Stdin detection is `VerbIo.InputRedirected` (as `submit`): explicit mode without a positional and without redirected stdin → exit 2 (A4); `--stop-hook` without redirected stdin → exit 0, nothing (AC-15 "a terminal on stdin"); `--flush` without redirected stdin → `sessionId = null`, stale spools only (spec CLI surface: "optional: by hand, stale spools only").
- **A16** — The hook line's `[session <id8> <HH:MM>Z]` time and the line's date are the UTC clock at append time (`TimeProvider.GetUtcNow()`); the envelope's `created` is the UTC clock at flush time. No time zone is read anywhere in this deliverable.
- **A17** — `StopHookInput.Parse` and `SessionEndInput.SessionId` take a `TextReader` (the `VerbIo.In` every verb has) rather than the spec's `Stream`; they read to the end and deserialise with the source-generated `HookJsonContext` (`JsonSerializerContext` over `StopHookEnvelope { string? session_id; string? cwd; bool? stop_hook_active; string? last_assistant_message; }` and `SessionEndEnvelope { string? session_id; }`, unknown members ignored, `JsonException` → `null`).
- **A18** — Mode exclusivity: `--stop-hook`, `--flush` and a positional fact (or redirected stdin in explicit mode) are mutually exclusive — more than one → exit 2 `zyggy: mode: --stop-hook, --flush and a fact are mutually exclusive`, checked before `node.json` so the usage error wins.
- **A19** — The secret refusal of `ContextSubmitter.BuildAsync` is a typed result, `public sealed record BuildRefusedByPattern(string PatternName) : BuildResult` (in `SubmitResult.cs`, beside `Built` and `BuildRefused`), which `SubmitAsync` maps to the spec's `ContextRefused(PatternName) : SubmitResult`; the spec's `BuildRefused("fact", "matches secret pattern <name>")` wording is kept as the result's `ToString()`-free meaning, not parsed from a string. The CLI output is exactly the spec's (`zyggy: refused: matches secret pattern <name>`, exit 5).
- **A20** — `ExitCodes` gains `public const int Refused = 5;` (the same number as `Aborted`, documented as the bus-verb family's "refused by policy", as `m365`/`linkedin` use it).

---

## Step 1 — Build a stated fact into a signed context envelope that reads back as itself, or refuse it before anything is signed: `ContextRequest.TryCreateStated` turns `--scope` and the fact into exactly one line `- [stated] <UTC date><hint>: <fact>` (hint `""` / ` (machine)` / ` (project:<name>)`; the fact collapsed, 1–1,000 characters) or names the field and detail; `ContextSubmitter.BuildAsync` scans every line against the secret patterns (refused by pattern name, the text nowhere), refuses a body over 64 KiB, builds the envelope with `Envelope.CreateContext` (`tenant`/`from` from `bus.Me`, `to: central`, `created` and the ULID from the clock, `priority: normal`, no `in_reply_to`), signs it with `signWith`, serialises, re-parses, re-verifies and compares every §4 context field, the body and the key set; `RememberService` writes its line through the same `FactLine.Format` byte for byte

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/FactLine.cs` *(create)*: `internal static class FactLine { public static string Format(string tag, string hint, string source, string date, string fact); }` → `- [<tag>] <date><hint>[ [<source>]]: <fact>` (the provenance bracket only when `source.Length > 0`).
- `src/Zyggy.Core/Memory/RememberService.cs` *(modify)*: the interpolated line becomes `FactLine.Format(request.Tag, request.Hint, request.Source, date, request.Fact)`; nothing else changes.
- `src/Zyggy.Core/Tenancy/MachineName.cs` *(modify)*: `public static MachineName Central { get; } = new("central");` — the §4 label of the memory owner's node folder (a protocol constant, not a tenant default; documented as such).
- `src/Zyggy.Core/Bus/ContextEnvelopeOptions.cs` *(create)*: `public sealed record ContextEnvelopeOptions { public int MaxFactChars { get; init; } = 1000; public int MaxNoteChars { get; init; } = 240; public int MaxHookLinesPerSession { get; init; } = 150; public TimeSpan StaleSpoolAge { get; init; } = TimeSpan.FromMinutes(60); public int MaxBodyBytes { get; init; } = 65_536; public string CapMarker { get; init; } = "- [observed] cap reached: no further hook lines this session"; }` (A14).
- `src/Zyggy.Core/Bus/ContextRequest.cs` *(create)*: `public sealed record ContextFactError(string Field, string Detail);` `public sealed record ContextRequest(ContextScope Scope, IReadOnlyList<string> Lines)` with `public static ContextFactError? TryCreateStated(string? scope, string? fact, DateOnly utcDate, out ContextRequest? request)` (scope `null` → `general`; A3/A4 messages; the line via `FactLine.Format("stated", hint, string.Empty, date, fact)`), `public static ContextFactError? CheckSender(MachineName machine)` (→ `("to", "this machine is central; use zyggy memory remember")` when `machine == MachineName.Central`, else `null` — A2), `internal static bool IsProjectLabel(string name)` (`^[a-z0-9][a-z0-9-]*$`, `RememberArguments`' regex shared or duplicated as a `GeneratedRegex`), `internal byte[] Body()` (lines joined with `\n` plus one trailing `\n`, UTF-8 without BOM).
- `src/Zyggy.Core/Bus/SubmitResult.cs` *(modify)*: `public sealed record BuildRefusedByPattern(string PatternName) : BuildResult;` and `public sealed record ContextRefused(string PatternName) : SubmitResult;` (A19).
- `src/Zyggy.Core/Bus/ContextSubmitter.cs` *(create)*: `public sealed partial class ContextSubmitter` with `internal ContextSubmitter(BusRepository bus, EnvelopeSigner signer, KeyId signWith, SecretPatterns patterns, ContextEnvelopeOptions options, TimeProvider clock, ILogger<ContextSubmitter> log)` (A1), `internal Func<Model, byte[]> Serializer { get; init; } = EnvelopeWriter.Serialize;` (08's A1 test hook), `public Task<BuildResult> BuildAsync(ContextRequest request, CancellationToken ct)`: (1) `patterns.TryMatch(line, out name)` over every line → `BuildRefusedByPattern(name)` + `LogRefusingPattern(name)` (Warning `refusing context: matches secret pattern {Name}`); (2) `request.Body().Length > options.MaxBodyBytes` → `BuildRefused("body", "body is longer than 64 KiB")`; (3) `header = new EnvelopeHeader(me.Tenant, EnvelopeId.New(now), me.Machine, MachineName.Central, now, EnvelopePriority.Normal)`, `Model.CreateContext(header, new ContextFields(request.Scope), body)` (an `ArgumentException` → `BuildRefused(ex.ParamName ?? "envelope", ex.Message)`); (4) `_signer.SignAsync` (not accepted → `SigningFailedException`), `Serializer`, `ParseAndVerifyAsync(bytes, me.Tenant)`, `Differs(parsed, signed, request)` over `id`, `tenant`, `type == Context`, `from`, `to == Central`, `created` (seconds), `priority == Normal`, `in_reply_to is null`, `scope == request.Scope`, the body bytes, and the key set `{schema, id, tenant, type, from, to, created, in_reply_to, priority, scope, key_id, sig}`; `LogBuilt(id)` (Debug `built context {Id}`) → `Built(envelope, bytes)`. `SubmitAsync` is Step 2.
- `tests/Zyggy.Core.Tests/Memory/FactLineTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Bus/ContextRequestTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Bus/ContextSubmitterBuildTests.cs` *(create)*.

**Seams**: `ISecretStore` faked (`InMemorySecretStore` from `TestKeys.StoreWithBothAsync`; `ThrowingSecretStore` for the unavailable store), `TimeProvider` faked (`FakeTimeProvider`), `IProcessRunner` faked (`RecordingProcessRunner` — nothing may be called in this step), `IBusProvider` substituted (NSubstitute — nothing may be called). The secret patterns from `tests/golden/secret-patterns/secret-patterns.txt`.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/FactLineTests.cs`:
  - `Format_StatedNoHintNoSource_DashTagDateColonFact` (`- [stated] 2026-10-28: Marie prefers tea`)
  - `Format_ObservedWithHintAndSource_HintThenBracketedSource` (`- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: note`)
  - `Format_RememberServiceLine_ByteIdenticalToBefore` (a `RememberService` over a `MemoryTree`, the fake clock and the golden patterns writes `- [stated] 2026-09-30 (machine) [the owner]: fact` exactly as `RememberVerbTests` expects — the refactor leaves `RememberVerbTests`, `RememberCommandTests` and `HubServerRememberTests` green and unedited)
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ContextRequestTests.cs` (date `2026-10-28`):
  - `TryCreateStated_NoScope_GeneralAndLineWithoutHint` (AC-5)
  - `TryCreateStated_ScopeMachine_HintMachine` and `TryCreateStated_ScopeProject_HintProjectName` (`project:calizr` → ` (project:calizr)`)
  - `TryCreateStated_ScopeProjectInvalidLabel_ScopeInvalidProjectName` (`[Theory]`: `project:`, `project:Has Space`, `project:Calizr`, `project:-x` → `("scope", "invalid project name in --scope")`)
  - `TryCreateStated_ScopeUnknownWord_ScopeMustBe` (`people/marie`, `Machine`, `` → `("scope", "--scope must be general, project:<name> or machine")`, A3)
  - `TryCreateStated_FactEmptyOrWhitespace_FactEmpty` (`[Theory]`: `null`, `""`, `" \t\r\n "` → `("fact", "empty fact")`)
  - `TryCreateStated_FactOver1000Chars_FactTooLong` (1,001 `a` → `("fact", "fact longer than 1000 characters")`; 1,000 accepted; a 1,000-rune non-ASCII fact accepted — Unicode scalars, not UTF-16 units)
  - `TryCreateStated_FactWithCrLfAndTabs_CollapsedToOneLine` (`"a\r\n b\t\tc"` → `a b c`)
  - `Body_LinesJoinedWithLfAndTrailingLf_NoBom`
  - `CheckSender_Central_ToThisMachineIsCentral` and `CheckSender_HomeLaptop_Null` (AC-12 text verbatim)
  - `MachineName_Central_IsTheLabelCentral` (`MachineName.Central.Value == "central"`, equals `MachineName.Parse("central")`)
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ContextSubmitterBuildTests.cs` (the `JobSubmitterBuildTests` shape: `TempCheckout(Acme, HomeLaptop)`, `RecordingProcessRunner`, `Substitute.For<IBusProvider>()`, `BusRepository` with `BusIdentity(Acme, HomeLaptop)`, `EnvelopeSigner(store)`, `FakeTimeProvider(2026-10-28T10:42:07Z)`, `SecretPatterns.Load(golden).Patterns!`, `RecordingLogger<ContextSubmitter>`):
  - `BuildAsync_StatedFact_SignedContextToCentralFromMeWithScopeAndOneLineBody` (AC-2 in process: `Built.Envelope` has `Type == Context`, `Header.Tenant == acme`, `From == home-laptop`, `To == MachineName.Central`, `InReplyTo == null`, `Priority == Normal`, `Context!.Scope == project:calizr`, `KeyId == acme/1`, `Signature` matches `^hmac-sha256:[0-9a-f]{64}$`, `Created == clock`, `Header.Id.Timestamp` within the clock's second; `BodyText == "- [stated] 2026-10-28 (project:calizr): The staging database is restored from Sunday's snapshot\n"`)
  - `BuildAsync_Bytes_FrontMatterKeysAreExactlyTheSection4ContextSet_InCanonicalOrder` (the serialised text's keys: `created, from, id, key_id, priority, schema, scope, sig, tenant, to, type` — the spec's Envelope block — and nothing else; `ParseAndVerifyAsync` with `acme/1` accepts the bytes)
  - `BuildAsync_SecretShapedLine_BuildRefusedByPatternNamesPattern_TextNowhere_NothingSigned` (`[Theory]` over `SecretPatternsTests.SecretSamples`: result `BuildRefusedByPattern` with the sample's name; `_processes.Calls` empty; `_provider.ReceivedCalls()` empty; the sample absent from every `_log.Messages`; `SecretSweep.AssertAbsent(_log.Messages)`; the one Warning is `refusing context: matches secret pattern <name>`)
  - `BuildAsync_BenignSamples_Built` (`[Theory]` over `BenignSamples`)
  - `BuildAsync_BodyOver64KiB_BuildRefusedBody` (a `ContextRequest` with 300 lines of 240 characters → `BuildRefused("body", …)`)
  - `BuildAsync_SerializerAltersScope_BuildRefusedScopeOrSig` (plan 08 A1: the serializer replaces `scope: project:calizr` by `scope: machine` → `BuildRefused` with `Field` in `{scope, sig}`)
  - `BuildAsync_KeyAbsent_ThrowsSigningFailed` and `BuildAsync_StoreUnavailable_ThrowsSecretStoreUnavailable` (the 08 contract: the caller maps them)
  - `BuildAsync_NeverLogsTheFactOrTheKey` (a token in the fact absent from `_log.Messages`; `SecretSweep.AssertAbsent`)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~FactLineTests|FullyQualifiedName~ContextRequestTests|FullyQualifiedName~ContextSubmitterBuildTests"`

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- The files above. `Differs` mirrors `JobSubmitter.Differs` for the context fields; `Section4ContextKeys` is a private static `HashSet<string>`. `MachineName`'s private constructor serves `Central`. No DI registration yet (Step 2).

**Contract impact**: ⚠️ **§4 shared contract (D1 / A12-1)**: the sender's own folder with `to: central` is fixed here by `header.To = MachineName.Central` and in Step 2 by the path; the body line grammar (`FactLine.Format`) is what 13 copies verbatim. No wire-format, canonical-form or `PROTOCOL.md` change (`git diff tests/golden/` empty). ⚠️ **Secrets**: the scan before signing is the control.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`); `FactLineTests`, `ContextRequestTests`, `ContextSubmitterBuildTests` pass; `RememberVerbTests`, `RememberCommandTests`, `HubServerRememberTests`, `JobSubmitterBuildTests` still pass **unedited**; `git diff tests/golden/ tests/Zyggy.Integration/Gates/` empty; `git diff src/Zyggy.Core/Memory/RememberService.cs` shows the one-line change only.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — Publish the built envelope into the sender's own `context/` folder through the one §5 write sequence and name every outcome: `BusRepository.PublishContextAsync(signedContext)` writes `tenants/<tenant>/nodes/<from>/context/<id>.md` (`AssertMayWrite(path, BusFolder.Context)` — never another machine's folder, never another tenant), commits `<tenant> context <id> <from>→central` and pushes with the four-attempt retry (`Pushed | Deferred | WriteFailed`), throwing only for a caller bug (unsigned, foreign tenant, `from ≠ Me`); `ContextSubmitter.SubmitAsync` is `BuildAsync` + `PublishContextAsync` → `Submitted | SubmitDeferred | SubmitFailed(stage) | SubmitRefused | ContextRefused(pattern)`, never throwing except cancellation, writing nothing on any refusal; `ContextSubmitter` is registered in the composition root

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/BusRepository.cs` *(modify)*: `public Task<WriteOutcome> PublishContextAsync(Model signedContext, CancellationToken ct)` — `ArgumentNullException`; `InvalidOperationException` when `signedContext.Type != Context`, `Signature is null`, `Header.Tenant != _me.Tenant` or `Header.From != _me.Machine` (messages `write rule: the context envelope …`); `bytes = EnvelopeWriter.Serialize`; `path = BusPaths.EnvelopeFile(BusPaths.Context(_me.Tenant, _me.Machine), id)`; `AssertMayWrite(path, BusFolder.Context)`; `WriteAsync(Subject("context", id, From, To), [path], write file + git add)` — the `SubmitAsync` body with the context folder.
- `src/Zyggy.Core/Bus/ContextSubmitter.cs` *(modify)*: `public async Task<SubmitResult> SubmitAsync(ContextRequest request, CancellationToken ct)` — `BuildAsync` inside the 08 `try` (`SigningFailedException` → `SubmitFailed("sign", …)`, `SecretStoreUnavailableException` → `SubmitFailed("secret-store", …)` + `LogStoreUnavailable`), `BuildRefusedByPattern` → `ContextRefused(name)`, `BuildRefused` → `SubmitRefused` + `LogRefusing(field)` (`refusing context: the envelope does not round-trip ({Field})`), else `PublishContextAsync` → `Pushed` → `Submitted(id, path, attempts)` + `LogPublished(id)` (Information `published context {Id} to central`); `Deferred` → `SubmitDeferred(id, path, attempts, detail)` + `LogDeferred` (Warning `published context {Id} to central: committed locally, not pushed ({Detail})`); `WriteFailed` → `SubmitFailed(stage, detail)`.
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: `TryAddSingleton(sp => new ContextSubmitter(sp.GetRequiredService<BusRepository>(), sp.GetRequiredService<EnvelopeSigner>(), sp.GetRequiredService<KeyId>(), sp.GetRequiredService<SecretPatterns>(), sp.GetService<ContextEnvelopeOptions>() ?? new(), sp.GetRequiredService<TimeProvider>(), log))` beside `JobSubmitter` (resolving without a `KeyId` or `SecretPatterns` throws — the verb checks both first).
- `src/Zyggy.Core/Configuration/ServiceCollectionExtensions.cs` *(modify)*: `services.TryAddSingleton(new ContextEnvelopeOptions());` in `AddNodeServices` (A14).
- `src/Zyggy.Core/Memory/SecretPatternsRegistration.cs` *(create, A1)*: `public static class SecretPatternsRegistration { public static string? AddSecretPatterns(this IServiceCollection services, IReadOnlyDictionary<string, string?> environment); }`.
- `tests/Zyggy.Core.Tests/Bus/BusRepositoryWriteTests.cs` *(modify — the context rows)*; `tests/Zyggy.Core.Tests/Bus/ContextSubmitterSubmitTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Memory/SecretPatternsRegistrationTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Configuration/NodeServicesRegistrationTests.cs` *(modify)*.

**Seams**: `IBusProvider` substituted (`PullAsync`/`PushAsync` scripted: success, `Rejected("fetch first")` four times, `BusSyncResult(false, …)`), `IProcessRunner` faked (`RecordingProcessRunner`: `add`, `commit` recorded; `add` scripted to fail once), `ISecretStore` faked, `TimeProvider` faked (the retry delays advanced as `JobSubmitterSubmitTests` does).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/BusRepositoryWriteTests.cs` (append):
  - `PublishContextAsync_SignedContextFromMe_FileUnderMyContextFolderAddCommitPushWithSubject` (the file at `BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), id)` holds the exact bytes; `add` has the path; the commit's stdin starts with `acme context <id> home-laptop→central`; `Pushed(1)`)
  - `PublishContextAsync_Unsigned_ThrowsInvalidOperation`, `PublishContextAsync_ForeignTenant_Throws`, `PublishContextAsync_FromAnotherMachine_Throws` (a context signed as `central` published by `home-laptop`'s repository → `InvalidOperationException`; nothing written — D1 enforced in Core, not in the caller)
  - `PublishContextAsync_NotAContextEnvelope_Throws` (a signed job)
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ContextSubmitterSubmitTests.cs` (the `JobSubmitterSubmitTests` shape, identity `home-laptop`):
  - `SubmitAsync_PushSucceeds_SubmittedWithContextPathAndTheFileVerifies` (`Submitted.RelativePath == tenants/acme/nodes/home-laptop/context/<id>.md`; `ParseAndVerifyAsync` accepts; `To == central`; the Information log line `published context <id> to central`)
  - `SubmitAsync_PushRejectedFourTimes_SubmitDeferredAttemptsFour_FileStays` (AC-11 through the seam)
  - `SubmitAsync_PullFails_SubmitFailedPull_NoFileNoAdd` (AC-10)
  - `SubmitAsync_AddFails_SubmitFailedAdd_CheckoutRestored`
  - `SubmitAsync_KeyAbsent_SubmitFailedSign_NothingWritten` and `SubmitAsync_StoreUnavailable_SubmitFailedSecretStore` (AC-9 through the seam)
  - `SubmitAsync_SecretShapedFact_ContextRefusedNamesPattern_BusNeverCalled_NothingWritten` (AC-7: `_processes.Calls` empty, `_provider.ReceivedCalls()` empty, `_checkout.FilesUnder(BusPaths.Context(Acme, HomeLaptop))` empty, the fact absent from `_log.Messages`)
  - `SubmitAsync_SelfCheckRefuses_SubmitRefused_BusNeverCalled`
  - `SubmitAsync_LockHeldByAnother_SubmitFailedLock`
  - `SubmitAsync_NeverLogsFactOrKey`
- Unit test file: `tests/Zyggy.Core.Tests/Memory/SecretPatternsRegistrationTests.cs`:
  - `AddSecretPatterns_ExplicitPath_RegistersSingletonReturnsNull` (resolving `SecretPatterns` gives the 27 patterns)
  - `AddSecretPatterns_DerivedFromClaudeProjectDir_WhenExplicitUnset`
  - `AddSecretPatterns_NoVariable_ReturnsTheRememberSentence_NothingRegistered` (AC-8: `GetService<SecretPatterns>()` is null)
  - `AddSecretPatterns_FileMissingOrMalformed_ReturnsLoadError` (`[Theory]`: missing → `<path> is missing`; a line without a tab → `<path>: malformed line '…'`)
- Unit test file: `tests/Zyggy.Core.Tests/Configuration/NodeServicesRegistrationTests.cs` (append): `AddNodeServices_WithKeyAndPatterns_ResolvesContextSubmitter` and `AddNodeServices_WithoutPatterns_ResolvingContextSubmitterThrows` (fail closed by construction); `AddNodeServices_RegistersContextEnvelopeOptionsDefaults`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~BusRepositoryWriteTests|FullyQualifiedName~ContextSubmitterSubmitTests|FullyQualifiedName~SecretPatternsRegistrationTests|FullyQualifiedName~NodeServicesRegistrationTests"`

**GREEN**:
- The changes above. `WriteAsync`, `AssertMayWrite`, `RestoreAsync`, `Subject` are reused unchanged; `PublishContextAsync` is a 20-line caller.

**Contract impact**: ⚠️ **§4 shared contract (D1 / A12-1)**: the path `tenants/<tenant>/nodes/<from>/context/<id>.md` and the commit subject `<tenant> context <id> <from>→central` are fixed here (13's ingest walks every `nodes/<machine>/context/` and filters `to: central`; `BusAuditor` already judges the writer by the folder — unedited). ⚠️ **Public API (A1)**: `SecretPatternsRegistration.AddSecretPatterns`, `BusRepository.PublishContextAsync`, `ContextSubmitter`, `ContextRequest`, `ContextFactError`, `ContextEnvelopeOptions`, `ContextRefused`, `BuildRefusedByPattern`, `MachineName.Central` are new public surface — reviewed at Gate A.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; the four test classes pass; `JobSubmitterSubmitTests`, `BusRepositoryIntakeTests`, `BusAuditorTests`, `BusInventoryTests` still pass unedited; `git diff src/Zyggy.Core/Bus/BusAuditor.cs src/Zyggy.Core/Bus/BusInventory.cs src/Zyggy.Core/Bus/BusPaths.cs tests/golden/` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 3 — `zyggy context` lands a signed, verifiable context envelope on the bare repository from `home-laptop`'s `node.json`: exit 0 with exactly two stdout lines `context <ULID> to central (tenants/acme/nodes/home-laptop/context/<ULID>.md)` and the stored line, the file on `main` verified with `acme/1`, one commit `acme context <ULID> home-laptop→central`, the clone clean; `zyggy verify` on `central` reports it `OK` and `zyggy status` counts `1` under `context` for `home-laptop`; `--scope` and the fact refused with exit 2 and one `zyggy: <field>: <detail>` line, a secret-shaped fact refused with exit 5 and the text nowhere, unresolvable patterns exit 3, the 08 key/store/checkout lines verbatim, pull failure exit 6 with nothing written, four refused pushes exit 7 with the deferral suffix, `central`'s `node.json` exit 2, `ZYGGY_HOOKS=off` exit 0 and nothing; no memory variable is read and a memory tree beside the checkout is byte-identical afterwards

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/ContextCommand.cs` *(create)*: `internal static class ContextCommand { public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure); }` — options `--scope <general|project:<name>|machine>` (`Option<string?>`), `--stop-hook` (`Option<bool>`), `--flush` (`Option<bool>`), `fact` (`Argument<string?>`, `ZeroOrOne`), `BusHost.ConfigOption`, `BusHost.VerboseOption`; description "Send one stated fact to Central's memory as a signed context envelope (remember skill on laptops); --stop-hook spools a turn's note, --flush ships the session's spool (the laptop hooks)." `RunAsync`: `ZYGGY_HOOKS == "off"` → 0 (every mode, before anything); mode exclusivity (A18, exit 2); `LoadConfiguration` (3); `ContextRequest.CheckSender(configuration.Machine)` → `zyggy: to: …` exit 2; then the explicit path: `SignWith is null` → `ConfigurationError(io, "signWith missing")`; `var services = new ServiceCollection()`? — no: `BusHost.BuildServices(configuration, verbose, environment, s => { patternsError = s.AddSecretPatterns(environment.Variables); configure?.Invoke(s); })` with `patternsError is not null` → `ConfigurationError(io, patternsError)` exit 3 **before** `CheckCheckoutAsync`; `CheckCheckoutAsync` (3); the fact: positional, else `io.InputRedirected ? await io.In.ReadToEndAsync(ct) : null` → A4 refusals; `TryCreateStated(scope, fact, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), out request)` with `clock = services.GetRequiredService<TimeProvider>()` → `zyggy: {Field}: {Detail}` exit 2; `SubmitAsync` → `Submitted`: `context {id} to central ({path})\n{request.Lines[0]}\n`, 0; `SubmitDeferred`: line 1 + ` (committed locally, not yet pushed: the next bus write on this machine pushes it)`, line 2, exit 7; `ContextRefused`: `zyggy: refused: matches secret pattern {name}`, exit 5 (`ExitCodes.Refused`, A20); `SubmitFailed { Stage: "sign" }`: the 08 `signing key … run: zyggy secret set … --hex` line, 6; `"secret-store"`: `zyggy: secret store unavailable: {detail}`, 6; `"lock"`: `zyggy: the bus checkout is busy: another zyggy process held its lock`, 6; other stages: `zyggy: context failed at {stage}: {detail}`, 6; `SubmitRefused`: `zyggy: refusing context: the envelope does not round-trip ({field})`, 2. `--stop-hook` and `--flush` print `zyggy: mode: --stop-hook is Step 5 of this plan` / `--flush is Step 7` and exit 2 **in this step only** (replaced in Steps 5 and 7; no test asserts these interim lines).
- `src/Zyggy.Cli/ExitCodes.cs` *(modify)*: `public const int Refused = 5;` (A20).
- `src/Zyggy.Cli/CliApplication.cs` *(modify)*: `ContextCommand.Create(environment, io, configure)` added to the root's children (between `SubmitCommand` and `StatusCommand`).
- `tests/Zyggy.Integration/Cli/CliHarness.cs` *(modify)*: `public string PatternsPath => Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");` and `RunAsync(..., bool withPatterns = true, ...)` adding `ZYGGY_SECRET_PATTERNS` to the environment unless `withPatterns` is false (every existing caller unchanged); `public string NodeDirOf(string name)` → `Scratch.File("node-" + name)` (what `WriteNodeJson` wrote).
- `tests/Zyggy.Integration/Cli/ContextCommandTests.cs` *(create)*.

**Seams**: the real edge — the real `git` through `HookedProcessRunner` over the production `ProcessRunner`, the bare repository (`BusRepoFixture`), the file secret store under `Scratch.File("secrets")` holding `acme/1`, the real clock (dates derived from the output as `RememberCommandTests` does). Never GitHub, never `claude`.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Cli/ContextCommandTests.cs` (one `CliHarness` per test; `home-laptop` over `Bus.CloneDir`, `central` over `Bus.SenderDir`; `Fact = "The staging database is restored from Sunday's snapshot"`; a `GeneratedRegex` `^context (?<id>[0-9A-HJKMNP-TV-Z]{26}) to central \((?<path>\S+)\)`):
  - `Context_ScopeProjectFact_ExitZeroTwoLinesFileOnMainSubjectCleanClone` (AC-1: stdout exactly `context {id} to central ({path})\n- [stated] {today} (project:calizr): {Fact}\n` where `today` is the UTC date of the run; `path == BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), id)`; `ExistsOnMainAsync(path)`; `LastCommitSubjectAsync == $"acme context {id} home-laptop→central"`; `PorcelainAsync(CloneDir)` empty; `SecretSweep.AssertAbsent([stdout, stderr, file text, git log -p])`)
  - `Context_FileOnMain_Section4ContextFieldsAndBodyExactly` (AC-2: `ParseOnMainAsync` → `Schema 1`, `Type Context`, `Tenant acme`, `From home-laptop`, `To == MachineName.Central`, `InReplyTo null`, `Priority Normal`, `Scope project:calizr`, `KeyId acme/1`, `Signature` 64 hex, `Created` within the test window, file name = `id`; the body bytes = the stored line + `\n`; the front-matter key set exactly the §4 context set)
  - `Context_ThenVerifyOnCentral_OkAndExitZero` (AC-3: `RunGitAsync(SenderDir, ["pull", "--rebase", "origin", "main"])`, then `["verify", "--verbose"]` with `CentralNodeJson` → exit 0, stdout contains `OK {path}` and no `FAIL`)
  - `Context_ThenStatusOnCentral_CountsOneContextForHomeLaptop` (AC-4: the `home-laptop` row of the bus table has `1` in the `context` column; the header line is the 08 one unchanged)
  - `Context_NoScope_GeneralNoHint_ScopeMachine_HintMachine` (AC-5 through the verb: two runs, two files, `Scope general` / `Scope machine`, the lines without / with ` (machine)`)
  - `Context_InvalidScope_ExitTwoOneLineNothingWritten` (`[Theory]`: `project:Has Space` → `zyggy: scope: invalid project name in --scope\n`; `people/marie` → `zyggy: scope: --scope must be general, project:<name> or machine\n`; `CommitCountOnMainAsync` unchanged; porcelain empty; `Hooks.Calls` holds no `add`/`commit`/`push`)
  - `Context_FactEmptyWhitespaceOrOver1000_ExitTwo` (`[Theory]`, AC-6: the `zyggy: fact: <detail>` lines)
  - `Context_FactOnStdin_IdenticalToPositional` and `Context_FactBothPositionalAndStdin_ExitTwo` (AC-6: `zyggy: fact: given both as an argument and on stdin\n`) and `Context_NoFactNoStdin_ExitTwo` (A4)
  - `Context_SecretShapedFact_ExitFiveNamesPattern_FactNowhere_CheckoutUnchanged` (AC-7: `AKIAABCDEFGHIJKLMNOP` → exit 5, stderr `zyggy: refused: matches secret pattern aws-access-key\n`, stdout empty; the value absent from stdout, stderr, every file under `CloneDir`, `git log -p --all` of the bare repository and `Hooks.Calls` texts; `CommitCountOnMainAsync` unchanged; porcelain unchanged)
  - `Context_PatternsUnresolvable_ExitThreeFailClosed` (`[Theory]`, AC-8: `withPatterns: false` and no `CLAUDE_PROJECT_DIR` → `zyggy: configuration error: ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)\n`; a missing file → `… <path> is missing`; a malformed file → `… malformed line`; `Hooks.Calls` empty — the checkout was never touched, nothing signed)
  - `Context_SignWithMissing_ExitThree`, `Context_KeyAbsentFromStore_ExitSixHintNothingCommitted`, `Context_StoreUnavailable_ExitSixOneLine` (AC-9: the 08 lines verbatim via `omitSignWith`, an empty secrets root, `more: s => s.AddSingleton<ISecretStore>(new ThrowingSecretStore())`)
  - `Context_LocalPathNotARepository_ExitThree`, `Context_CheckoutOnAnotherBranch_ExitThree`, `Context_NoTenantDirectory_ExitThree` (the 04/08 checkout lines)
  - `Context_PullFails_ExitSixNothingWrittenWorkingCopyAsItWas` (AC-10: `Bus.DeleteBare()` → exit 6, stderr starts with `zyggy: context failed at pull:`, stdout empty, no `*.md` under the clone's `context/`, porcelain unchanged, `SecretSweep.AssertAbsent([stderr])`)
  - `Context_EveryPushRefused_ExitSevenSuffixCommitLocal_NextWritePushesIt` (AC-11: `Hooks.Override` refuses `push` → exit 7, stdout line 1 ends with the suffix, line 2 the stored line, the local `log -1` subject starts with `acme context `, `CommitCountOnMainAsync` unchanged, four pushes; then `Hooks.Override = null` and a second `context` run → both files on `main`, `RevListCountAsync("origin/main..HEAD") == 0`)
  - `Context_ThisMachineIsCentral_ExitTwoUseMemoryRemember_NothingTouched` (AC-12: `CentralNodeJson` → exit 2, stderr `zyggy: to: this machine is central; use zyggy memory remember\n`, `Hooks.Calls` empty)
  - `Context_HooksOff_ExitZeroNothingOnStdoutNothingWritten` (AC-13: `extraEnvironment ZYGGY_HOOKS=off`; also without `ZYGGY_SECRET_PATTERNS` and with a bad `node.json` path — the early exit wins)
  - `Context_ConfigurationErrorWinsOverArgumentError` (a bad `--scope` with `omitSignWith` → 3)
  - `Context_MemoryTreeBesideCheckoutByteIdentical_NoPrincipalVariableNeeded` (AC-22: a small `memory/acme/alice/` tree written inline under `Scratch.File("memory")` with `profile.md`, `inbox/remember-2026-10-27.md`, `daily/2026-10-27.md`; the environment carries **no** `ZYGGY_MEMORY_ROOT`/`ZYGGY_TENANT`/`ZYGGY_USER`/`ZYGGY_TIMEZONE`; the run succeeds; `TreeFingerprint` equal before and after; the only new files under the scratch root are inside the bus clone)
  - `Context_EveryOutputAndLogLine_NoKeyNoRefusedText` (AC-23: `--verbose` run plus a refused run; `SecretSweep.AssertAbsent` over stdout, stderr and `Hooks.Calls.SelectMany(SecretSweep.TextsOf)`; a `://x:y@` remote URL in a second `node.json` whose pull fails shows `://<redacted>@` on stderr)
  - `Context_StopHookOrFlushWithFact_ExitTwoMutuallyExclusive` (A18)
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContextCommandTests"` (RED: the verb is unknown to the root command — every test fails at exit 2 "unrecognized command" until `ContextCommand` exists)

**GREEN**:
- `ContextCommand`, the `ExitCodes` constant, the `CliApplication` registration, the harness additions. No production change in Core is expected: a defect found here is fixed in `src/Zyggy.Core/Bus/` with a unit test added to Step 1's or 2's class first.

**Contract impact**: CLI surface — the new verb `context` with exit codes 0 · 2 · 3 · 5 · 6 · 7 (the 08 family plus 5 "refused by a secret pattern"); no bus-format change. ⚠️ **Secrets** (AC-7, AC-23) and ⚠️ **§4 D1** (AC-3: `verify` accepts the sender's own folder with `to: central` — `BusAuditor` unedited).

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ContextCommandTests` pass; `SubmitCommandTests`, `VerifyCommandTests`, `StatusCommandTests` pass unedited; `dotnet run --project src/Zyggy.Cli -- --help` lists `context`; `dotnet test Zyggy.slnx --filter "Category!=Integration"` green; `git diff tests/Zyggy.Integration/Gates/ tests/golden/ Directory.Packages.props` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A *(covers Steps 1–3)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~FactLineTests|FullyQualifiedName~ContextRequestTests|FullyQualifiedName~ContextSubmitter|FullyQualifiedName~BusRepositoryWriteTests|FullyQualifiedName~SecretPatternsRegistrationTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContextCommandTests"` green; the executor pastes the two stdout lines of one real run, the file's front matter and body as `git show main:<path>` prints them, the `verify --verbose` `OK` line and the `status` table row, the exit-5 stderr line of the refused fact and the exit-7 stdout of the deferred run.
- [x] Contract review (⚠️ §4 shared contract, D1 / A12-1): the file sits in `tenants/acme/nodes/home-laptop/context/` with `to: central`; the front-matter key set is exactly the spec's Envelope block (`created, from, id, key_id, priority, schema, scope, sig, tenant, to, type`), no `in_reply_to`; the body is one `- [stated] <UTC date><hint>: <fact>` line plus `\n` (what 13 copies verbatim); the commit subject `acme context <id> home-laptop→central`; `BusAuditor`, `BusInventory`, `BusPaths`, `AssertMayWrite` unedited; the exit-code table of the spec's CLI surface (0 · 2 · 3 · 5 · 6 · 7) and the 08 lines verbatim.
- [x] ⚠️ Risk review (secrets): a secret-shaped fact appears in no stdout, stderr, log line, process argument, file or git object (the theory over the golden samples in Step 1, AC-7 in Step 3); no envelope is ever signed without the patterns file (AC-8, `SecretPatternsRegistration` fail closed); the key is read only inside `EnvelopeSigner.SignAsync`; git stderr redacted.
- [x] ⚠️ Risk review (public API, A1): the new public types and members listed in Step 2's Contract impact; `SecretPatterns` stays internal; every public member documented.
- [x] Assumptions A1–A20 read; A1 (the registration facade), A2 (the `central` check in every mode and its position), A3/A4 (messages), A8 (flush outcome rules), A10 (`0.11.0`), A19 (typed refusal result) confirmed or corrected before Slice B.
- [x] `RememberService` refactored onto `FactLine.Format` with `RememberVerbTests`, `RememberCommandTests`, `HubServerRememberTests` unedited and green; `git diff tests/golden/ tests/Zyggy.Integration/Gates/ Directory.Packages.props` empty; nothing committed.
- [x] User approved — implementation may continue past this gate

---

## Step 4 — Spool one `[observed]` line per turn from the `Stop` JSON without any key, network or checkout: `StopHookInput.Parse` reads `session_id`, `cwd`, `stop_hook_active` and `last_assistant_message`, keeps the first non-empty line (CR removed, whitespace collapsed, cut to 240 characters with `…`) and answers `null` for unparseable input, `stop_hook_active: true`, no non-empty line or an unsafe session id; the scope is `project:<lower-cased last segment of cwd>` when that is a label, else `general`; `ContextStopHook.Run` refuses a secret-shaped note by pattern name (nothing spooled, the text nowhere), appends `- [observed] <UTC date><hint> [session <id8> <HH:MM>Z]: <note>` to `<nodeDir>/context/<tenant>/<session_id>.md` whose first line is `scope: <scope>`, writes the cap marker once at 150 lines and nothing after; `ContextSpool.Due` returns the named session's file plus every file older than 60 minutes, parsed into scope and lines; `SessionEndInput.SessionId` reads the `SessionEnd` JSON

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/HookInput.cs` *(create, A17)*: `public sealed record StopHookNote(string SessionId, string Cwd, string FirstLine);` `public static class StopHookInput { public static StopHookNote? Parse(TextReader stdin, ContextEnvelopeOptions options); }` (`FirstLine` = the first line of `last_assistant_message` that is non-empty after `TextCollapse.Line` with `\r` removed; cut with `FactValidator.Cut` when over `MaxNoteChars` — the 240-character rule); `public static class SessionEndInput { public static string? SessionId(TextReader stdin); }`; `internal sealed class StopHookEnvelope`, `SessionEndEnvelope`; `[JsonSerializable(...)] internal sealed partial class HookJsonContext : JsonSerializerContext` (`JsonSourceGenerationOptions(PropertyNamingPolicy = SnakeCaseLower)` or explicit `JsonPropertyName`s). A session id outside `[A-Za-z0-9-]{1,128}` → `null` (A6).
- `src/Zyggy.Core/Bus/ContextSpool.cs` *(create)*: `public sealed class ContextSpool(string nodeDir, TenantId tenant, TimeProvider clock, ContextEnvelopeOptions options)`; `public static string DirectoryFor(string nodeDir, TenantId tenant)` (A6); `public string Directory { get; }`; `public enum SpoolAppendResult { Appended, CapReached, AlreadyCapped }`; `public SpoolAppendResult Append(string sessionId, ContextScope scope, string line)` (creates the directory and the file with `scope: <scope>\n` when absent; counts the body lines; `>= MaxHookLinesPerSession` → marker once; the marker present → `AlreadyCapped`; written atomically: temp file + `File.Move(overwrite)` as `MemoryFileWriter` does, UTF-8 without BOM, LF); `public IReadOnlyList<SpoolFile> Due(string? sessionId, DateTimeOffset now)` (A7; a file whose first line is not `scope: <valid scope>` is read as `general` with every line as body); `public void Delete(SpoolFile file)`; `public sealed record SpoolFile(string Path, string SessionId, ContextScope Scope, IReadOnlyList<string> Lines);`.
- `src/Zyggy.Core/Bus/ContextStopHook.cs` *(create)*: `public sealed class ContextStopHook` with `internal ContextStopHook(ContextSpool spool, SecretPatterns patterns, TimeProvider clock, ContextEnvelopeOptions options)` (A1); `public enum StopHookResult { Spooled, Ignored, Unparseable, Refused, CapReached, AlreadyCapped }`; `public sealed record StopHookOutcome(StopHookResult Result, string? PatternName, string? SessionId8);` `public StopHookOutcome Run(TextReader stdin, bool inputRedirected)` — `!inputRedirected` → `Ignored`; `Parse` null → `Unparseable` when the text is not JSON / not an object, `Ignored` otherwise (`stop_hook_active`, empty message); scope from `cwd` (`cwd.Replace('\\', '/')`, trailing `/` trimmed, last segment lower-cased; `ContextRequest.IsProjectLabel` → `project:<name>`, else `general`); `patterns.TryMatch(note)` → `Refused(name)`; line = `FactLine.Format("observed", hint, $"session {id8} {HH:mm}Z", date, note)` with the UTC clock (A16); `spool.Append` → `Spooled | CapReached | AlreadyCapped`.
- `src/Zyggy.Core/Configuration/ServiceCollectionExtensions.cs` *(modify)*: `services.TryAddSingleton(sp => new ContextSpool(configuration.EffectiveNodeDir, configuration.Tenant, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ContextEnvelopeOptions>()))` and `services.TryAddSingleton(sp => new ContextStopHook(sp.GetRequiredService<ContextSpool>(), sp.GetRequiredService<SecretPatterns>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ContextEnvelopeOptions>()))` (`TimeProvider.System` `TryAdd`ed as `AddJobRunner` does).
- `tests/Zyggy.Core.Tests/Bus/HookInputTests.cs`, `ContextSpoolTests.cs`, `ContextStopHookTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Infrastructure/HookJson.cs` *(create: the `stop-input.json` fixture text as a C# constant plus `Stop(message, active = false, cwd = "/tmp/project", sessionId = …)` and `SessionEnd(sessionId, reason = "exit")` builders — the template's fixture byte for byte for the base case)*.

**Seams**: `TimeProvider` faked (`FakeTimeProvider` at `2026-10-28T10:42:07Z`); the file system under a scratch `nodeDir` (`Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid, "node")`, the `JobRunnerHarness` shape); the patterns from the golden file. No `IProcessRunner`, no `ISecretStore`, no `IBusProvider` exist in these tests at all — the types are not constructed.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/HookInputTests.cs`:
  - `Parse_FixtureInput_SessionCwdAndCollapsedFirstLine` (the template fixture: `FirstLine == "Done: the plan for deliverable 27 is written and reviewed."`, `SessionId == 0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e`, `Cwd == /tmp/project`)
  - `Parse_BlankLinesThenText_FirstNonEmptyLine`, `Parse_CrLfAndLeadingBareCr_NotNotes`, `Parse_OnlyCodeFence_FenceLine` (the `stop.bats` cases)
  - `Parse_Over240Chars_CutWithEllipsis_Exactly240Kept` (`[Theory]`: 321 → 239 + `…`; exactly 240 → unchanged — `FactValidator.Cut`'s rule)
  - `Parse_StopHookActiveTrue_Null`, `Parse_EmptyOrWhitespaceMessage_Null`, `Parse_MessageAbsent_Null`
  - `Parse_NotJsonOrNotAnObject_Null` (`not json`, `[]`, `""`)
  - `Parse_UnknownMembers_Ignored` (`transcript_path`, `permission_mode`, `stop_reason` present — never read)
  - `Parse_SessionIdWithPathCharacters_Null` (`../x`, `a/b`, `a\b`, empty, 129 characters — A6)
  - `SessionId_SessionEndJson_Id`, `SessionId_UnparseableOrAbsent_Null`
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ContextSpoolTests.cs`:
  - `DirectoryFor_NodeDirAndTenant_ContextTenantUnderNodeDir` (`Path.Join(nodeDir, "context", "acme")`)
  - `Append_FirstLine_CreatesDirectoryAndFileWithScopeHeader_Appended` (bytes `scope: project:calizr\n<line>\n`, UTF-8 no BOM, LF)
  - `Append_SecondLine_AppendedAfterTheFirst_HeaderOnce`
  - `Append_150LinesPresent_MarkerOnceCapReached_ThenAlreadyCapped` (AC-17: the 151st call writes the marker, the file has 152 lines; the 152nd call changes nothing and answers `AlreadyCapped`)
  - `Append_149Lines_OneMoreAppendedNoMarker`
  - `Due_NamedSessionAnyAge_Included` and `Due_UnnamedFresh_Excluded` and `Due_UnnamedOlderThan60Minutes_Included` (`File.SetLastWriteTimeUtc(now - 61 min)` vs `- 59 min`; strictly older)
  - `Due_OrderNamedFirstThenStaleByName`
  - `Due_DirectoryAbsent_Empty` and `Due_SessionIdNull_StaleOnly`
  - `Due_ParsesScopeHeaderAndBodyLines_InOrder`, `Due_MalformedFirstLine_GeneralAndEveryLineIsBody`
  - `Delete_RemovesTheFile_OtherSpoolsUntouched`
  - `Append_NoTempFileSurvives`
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ContextStopHookTests.cs` (patterns from the golden file, `FakeTimeProvider`, a scratch spool):
  - `Run_FixtureInput_SpoolsObservedLineWithProjectScopeFromCwd` (AC-14: `cwd` `/home/alice/src/Calizr` → file `scope: project:calizr`, line `- [observed] 2026-10-28 (project:calizr) [session 0b7c3d1e 10:42Z]: Done: the plan for deliverable 27 is written and reviewed.`)
  - `Run_CwdNotALabel_ScopeGeneralNoHint` (`[Theory]`: `C:\Users\alice\My Project`, `/`, `C:\`, `/tmp/x_y` → `scope: general`, line without hint)
  - `Run_WindowsAndTrailingSeparators_LastSegmentLowerCased` (`C:/src/Calizr/` → `project:calizr`)
  - `Run_StopHookActive_Ignored_NothingWritten`, `Run_EmptyMessage_Ignored`, `Run_InputNotRedirected_Ignored_StdinNeverRead` (AC-15)
  - `Run_Unparseable_Unparseable_NothingWritten`
  - `Run_SecretShapedFirstLine_RefusedNamesPattern_NothingSpooled_TextNowhere` (`[Theory]` over `SecretSamples`, AC-16; a second theory: a secret on a **later** line never reaches the file — only the first line is used)
  - `Run_BenignSamples_Spooled`
  - `Run_SpoolAt150_CapReachedMarkerOnce_ThenAlreadyCapped` (AC-17)
  - `Run_TwoSessionsSameProject_TwoSpools` (edge case)
  - `Run_NoteContainsStatedTag_KeptAsText_LineTagIsObserved` (edge case)
  - `Run_2330Z_DateAndTimeAreUtc` (clock `2026-10-28T23:30:00Z` → `2026-10-28 … 23:30Z`; no zone — A16)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~HookInputTests|FullyQualifiedName~ContextSpoolTests|FullyQualifiedName~ContextStopHookTests"`

**GREEN**:
- The files above; `NodeServicesRegistrationTests` gains `AddNodeServices_ResolvesContextSpoolUnderEffectiveNodeDir` and `AddNodeServices_WithPatterns_ResolvesContextStopHook` in this step's GREEN (so the registration is proven before the verb uses it).

**Contract impact**: none on the bus; the spool file format (`scope: <scope>` then body lines) is node state (spec Envelope / file formats); the hook line grammar is the §4 body line 13 copies. No `node.json` key. ⚠️ **Secrets**: the note refusal.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; the three test classes and `NodeServicesRegistrationTests` pass; `Src_BusPathLiterals_OnlyInBusPaths` still green (the literal `"context"` without a slash is outside its pattern — Step 10 adds the dedicated fact); `git diff tests/golden/` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 5 — `zyggy context --stop-hook` spools the turn in process with nothing else happening: exit 0 and empty stdout for the fixture JSON, one line appended to `<nodeDir>/context/acme/<session_id>.md` under `scope: project:<name>`, **no process started** (the recorded process log is empty) and **no secret-store access** (a throwing store is never opened); `stop_hook_active`, an empty message or a terminal on stdin → nothing; unparseable input → one stderr line; a secret-shaped note → exit 0, `context: note refused (pattern <name>)`, nothing spooled; the cap marker once with one stderr line; `ZYGGY_HOOKS=off` → nothing; unresolvable patterns → exit 3; `central` → exit 2

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/ContextCommand.cs` *(modify)*: the `--stop-hook` branch — after the shared checks (hooks off → config → `central` → `AddSecretPatterns` inside `BuildServices` → exit 3 on error; **no** `CheckCheckoutAsync`, no `signWith` check): `services.GetRequiredService<ContextStopHook>().Run(io.In, io.InputRedirected)` → `Spooled`/`Ignored`/`AlreadyCapped`: nothing; `Unparseable`: `context: unparseable hook input\n` on stderr; `Refused`: `context: note refused (pattern {name})\n`; `CapReached`: `context: cap of 150 hook lines reached for session {id8}\n` (A9); always exit 0.
- `tests/Zyggy.Integration/Cli/ContextStopHookCommandTests.cs` *(create)*.

**Seams**: the real edge — the verb in process over `home-laptop`'s `node.json` with `nodeDir` under the scratch directory; `HookedProcessRunner` records every process (must stay empty); `ThrowingSecretStore` registered through `more:` (must never be opened); the real clock (the line's date/time read back from the file).

**RED**:
- Integration test file: `tests/Zyggy.Integration/Cli/ContextStopHookCommandTests.cs` (`HookJson` builders linked from `tests/Zyggy.Core.Tests/Infrastructure/HookJson.cs` — *(modify `Zyggy.Integration.csproj`: one `Compile Include` link)*; spool path = `ContextSpool.DirectoryFor(_harness.NodeDirOf("home-laptop"), Acme)`):
  - `StopHook_FixtureJson_ExitZeroNothingOnStdout_OneLineSpooledUnderProjectScope_NoProcessNoStoreAccess` (AC-14: the file's line 1 `scope: project:project` for `cwd` `/tmp/project`; line 2 matches `^- \[observed\] \d{4}-\d{2}-\d{2} \(project:project\) \[session 0b7c3d1e \d{2}:\d{2}Z\]: Done: the plan for deliverable 27 is written and reviewed\.$`; `_harness.Hooks.Calls` empty; the store is `ThrowingSecretStore` and nothing threw)
  - `StopHook_SecondTurnSameSession_SecondLineAppended`
  - `StopHook_StopHookActiveOrEmptyMessage_ExitZeroNothingWrittenNoStderr` (`[Theory]`, AC-15)
  - `StopHook_TerminalOnStdin_ExitZeroNothing` (`stdin: null` → `InputRedirected false`)
  - `StopHook_UnparseableStdin_ExitZeroOneStderrLine_NothingWritten` (AC-15)
  - `StopHook_SecretShapedFirstLine_ExitZeroStderrNamesPattern_NothingSpooled_TextNowhere` (AC-16: `Stored the key AKIAABCDEFGHIJKLMNOP in the vault.` → stderr `context: note refused (pattern aws-access-key)\n`; the value absent from stderr and from every file under the scratch root)
  - `StopHook_150LinesThenOneMore_MarkerOnceOneStderrLine_FurtherCallsNothing` (AC-17: the spool pre-written with 150 hook lines; the 151st call → marker + `context: cap of 150 hook lines reached for session 0b7c3d1e\n`; the 152nd → no change, no stderr)
  - `StopHook_HooksOff_ExitZeroNothing_EvenWithoutPatterns` (AC-13)
  - `StopHook_PatternsUnresolvable_ExitThreeFailClosed_NothingSpooled` (AC-8)
  - `StopHook_CentralNodeJson_ExitTwoUseMemoryRemember` (AC-12, A2)
  - `StopHook_NoBusCheckoutNeeded_WorksWhenLocalPathIsNotARepository` (the hook path never checks the checkout: a `node.json` whose `bus.localPath` is an empty directory still spools — proves D2's "no checkout")
  - `StopHook_WindowsCwd_ProjectLowerCased` (`cwd` `C:\\src\\Calizr` → `scope: project:calizr`)
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContextStopHookCommandTests"`

**GREEN**:
- The `--stop-hook` branch of `ContextCommand`; the csproj link. No Core change expected (a defect goes to Step 4's classes first).

**Contract impact**: CLI surface `zyggy context --stop-hook` (exit 0 always, 3 configuration — and 2 on `central`, A2). No bus write, no memory write.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ContextStopHookCommandTests` pass; `ContextCommandTests` still pass; `dotnet test Zyggy.slnx --filter "Category!=Integration"` green.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B *(covers Steps 4–5)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~HookInputTests|FullyQualifiedName~ContextSpoolTests|FullyQualifiedName~ContextStopHookTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContextStopHookCommandTests"` green; the executor pastes one spool file's content after two turns (the `scope:` header and two `[observed]` lines), the cap marker case's tail and stderr line, and the refused note's stderr line.
- [x] Contract review: the hook line grammar `- [observed] <UTC date><hint> [session <id8> <HH:MM>Z]: <note>` against the spec's Envelope / file formats; the scope rule (lower-cased last segment of `cwd` when `^[a-z0-9][a-z0-9-]*$`, else `general`); the 240-character cut and the 150-line cap with the spec's `CapMarker` text; the spool path `<nodeDir>/context/<tenant>/<session_id>.md` (node state beside `runs/`, never committed, A6); `--stop-hook` exit codes (0 always, 3 configuration, 2 `central` per A2).
- [x] ⚠️ Risk review (secrets; D2 "never the bus"): the refused note appears nowhere; the hook path starts no process, opens no secret store, needs no checkout; nothing under any memory root is touched (no memory type is referenced — Step 10's fact will pin it).
- [x] A6 (spool path builder), A7 (age rule), A9 (stderr wording), A16 (UTC), A17 (`TextReader`) confirmed or corrected before Slice C.
- [x] Nothing committed in either repository; `git diff tests/golden/ tests/Zyggy.Integration/Gates/ Directory.Packages.props` empty.
- [x] User approved — implementation may continue past this gate

---

## Step 6 — Ship due spools as one signed envelope each through the fakes: `ContextFlusher.FlushAsync(sessionId)` takes `ContextSpool.Due(sessionId, now)`, drops any line matching a secret pattern (named, never echoed), builds a `ContextRequest(scope, lines)` per spool, runs `ContextSubmitter.SubmitAsync`, deletes the spool on `Submitted` or `SubmitDeferred`, keeps it on `SubmitFailed`/`SubmitRefused`, deletes a spool left with no line without shipping, processes every due spool even after a failure, and returns one `FlushOutcome` per spool; the stale rule ships a crashed session's spool and leaves a fresh one alone

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Bus/ContextFlusher.cs` *(create)*: `public sealed partial class ContextFlusher` with `internal ContextFlusher(ContextSpool spool, ContextSubmitter submitter, SecretPatterns patterns, TimeProvider clock, ILogger<ContextFlusher> log)` (A1); `public sealed record FlushOutcome(SpoolFile File, SubmitResult? Result, IReadOnlyList<string> DroppedPatterns);` (`Result` null when nothing was left to ship); `public async Task<IReadOnlyList<FlushOutcome>> FlushAsync(string? sessionId, CancellationToken ct)` — per A8: `Due(sessionId, clock.GetUtcNow())`; per spool: lines kept / dropped (`patterns.TryMatch`), `LogDropped(name)` (Warning `context flush: line dropped (pattern {Name})`); none left → `Delete`, outcome with `Result null`; else `SubmitAsync(new ContextRequest(file.Scope, kept))` → `Submitted`/`SubmitDeferred` → `Delete` + `LogShipped(id, sessionId8)` (Information `shipped context {Id} for session {Session}`); `SubmitFailed`/`SubmitRefused`/`ContextRefused` → kept + `LogKept(sessionId8, stage)` (Warning); never throws except cancellation.
- `src/Zyggy.Core/Bus/ServiceCollectionExtensions.cs` *(modify)*: `TryAddSingleton(sp => new ContextFlusher(sp.GetRequiredService<ContextSpool>(), sp.GetRequiredService<ContextSubmitter>(), sp.GetRequiredService<SecretPatterns>(), sp.GetRequiredService<TimeProvider>(), log))`.
- `tests/Zyggy.Core.Tests/Bus/ContextFlusherTests.cs` *(create)*; `tests/Zyggy.Core.Tests/Configuration/NodeServicesRegistrationTests.cs` *(modify: `AddNodeServices_WithKeyAndPatterns_ResolvesContextFlusher`)*.

**Seams**: `IBusProvider` substituted, `IProcessRunner` faked (`RecordingProcessRunner`), `ISecretStore` faked, `TimeProvider` faked (`FakeTimeProvider` — AC-19's 60-minute rule driven by `File.SetLastWriteTimeUtc` against the fake `now`); the spool under a scratch `nodeDir`; the real `ContextSubmitter` and `BusRepository` over `TempCheckout(Acme, HomeLaptop)`.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Bus/ContextFlusherTests.cs`:
  - `FlushAsync_NamedSessionWithThreeLines_OneEnvelopeScopeAndLinesInOrder_SpoolDeleted` (AC-18 through the seam: the file under `BusPaths.Context(Acme, HomeLaptop)` parses and verifies; `Scope == project:calizr`; `BodyText == line1\nline2\nline3\n`; the spool file gone; the commit subject `acme context <id> home-laptop→central`; `Submitted`)
  - `FlushAsync_StaleSpoolOfAnotherSession_ShippedToo_FreshOneLeftAlone` (AC-19: three spools — the named session (fresh), `S2` written 61 minutes before `now`, `S3` written 5 minutes before → two envelopes, two deletions, `S3` untouched; two `FlushOutcome`s)
  - `FlushAsync_SessionIdNull_StaleOnly`
  - `FlushAsync_PullFails_SubmitFailedPull_SpoolKeptUntouched` (AC-20: the spool's bytes and `LastWriteTimeUtc` unchanged; the next `FlushAsync` with the pull restored ships it)
  - `FlushAsync_PushRefusedFourTimes_SubmitDeferred_SpoolDeleted_CommitLocal` (AC-20)
  - `FlushAsync_OneSpoolFailsAnotherShips_BothOutcomesReturned` (A8: the first spool's add fails (`_processes.On("add", Fail, Ok)`), the second ships)
  - `FlushAsync_HandEditedSecretLine_DroppedNamedRestShips` (AC-21: `DroppedPatterns == ["aws-access-key"]`; the body holds the other lines; the value absent from the body, from `_log.Messages` and from every process spec)
  - `FlushAsync_EverySecretSample_Dropped` (`[Theory]` over `SecretSamples`)
  - `FlushAsync_SpoolLeftWithNoLine_NothingShippedSpoolDeleted` (AC-21: `Result null`, `_provider.ReceivedCalls()` empty)
  - `FlushAsync_EmptySpoolFileHeaderOnly_NothingShippedDeleted`
  - `FlushAsync_SelfCheckRefuses_SpoolKept` (the altering serializer → `SubmitRefused`, the file stays)
  - `FlushAsync_CapMarkerIsTheLastBodyLine_Shipped` (the marker travels as a line — the spec's "cap marker: last line of a capped spool")
  - `FlushAsync_DirectoryAbsent_EmptyOutcomes`
  - `FlushAsync_NeverLogsALineOrTheKey`
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ContextFlusherTests|FullyQualifiedName~NodeServicesRegistrationTests"`

**GREEN**:
- `ContextFlusher` and its registration.

**Contract impact**: ⚠️ **§4 shared contract**: the multi-line body (one fact per line, in spool order, the cap marker last) is what 13 copies verbatim into `inbox/context-<id>.md`; `scope` from the spool header. No wire change.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ContextFlusherTests` pass; `git diff tests/golden/` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 7 — `zyggy context --flush` ships a session's spool to the bare repository at `SessionEnd`: with the `SessionEnd` JSON on stdin and a spool of three lines under `scope: project:calizr`, exit 0, one stdout line `context <ULID> to central (<path>)`, one signed envelope on `main` with that scope and the three lines as its body, the spool deleted, `zyggy verify` on `central` `OK`; a stale spool ships alongside, a fresh one stays; offline → exit 6 `zyggy: context failed at pull: …` and the spool kept for the next flush; four refused pushes → exit 7 with the suffix, the spool deleted, the next bus write pushes it; a hand-edited secret line dropped with one stderr line; nothing due → exit 0 and nothing; `ZYGGY_HOOKS=off` → nothing

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/Commands/ContextCommand.cs` *(modify)*: the `--flush` branch — shared checks, `signWith missing` (3), patterns (3), `BuildServices`, `CheckCheckoutAsync` (3), `sessionId = io.InputRedirected ? SessionEndInput.SessionId(io.In) : null` (A15), `FlushAsync(sessionId, ct)`, then per outcome in order: each `DroppedPatterns` name → `context: line dropped (pattern {name})\n` on stderr; `Submitted` → `context {id} to central ({path})\n`; `SubmitDeferred` → the same line with the deferral suffix; `SubmitFailed` → the Step 3 failure lines; `SubmitRefused` → the round-trip line; `ContextRefused` → the exit-5 line (defensive — the flusher pre-scans); exit per A8 (6 > 2 > 7 > 0: when every spool shipped and at least one was deferred, exit 7, as the explicit verb).
- `tests/Zyggy.Integration/Cli/ContextFlushCommandTests.cs` *(create)*.

**Seams**: the real edge — the real `git` on the bare repository, the file secret store, the real clock; spools seeded through `ContextSpool.Append` (production) or raw bytes (the hand-edited case) under `_harness.NodeDirOf("home-laptop")`.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Cli/ContextFlushCommandTests.cs`:
  - `Flush_SessionEndJsonAndThreeLineSpool_ExitZeroOneLine_EnvelopeOnMainWithScopeAndLines_SpoolDeleted` (AC-18: stdout exactly one `context <id> to central (<path>)` line; `ParseOnMainAsync` → `Scope project:calizr`, `BodyText` = the three lines each + `\n`, `From home-laptop`, `To central`; `File.Exists(spool)` false; `PorcelainAsync(CloneDir)` empty; `LastCommitSubjectAsync` = `acme context <id> home-laptop→central`)
  - `Flush_ThenVerifyOnCentral_Ok` (AC-18 second half: `OK <path>`, exit 0 after the sender pulls)
  - `Flush_StaleSpoolShipsToo_FreshSpoolStays` (AC-19 on disk: `File.SetLastWriteTimeUtc(stale, UtcNow - 61 min)`; two stdout lines, two files on `main`, the fresh spool present)
  - `Flush_NothingDue_ExitZeroNothing` (no directory; an empty directory; only a fresh spool of another session)
  - `Flush_NoStdinRedirected_StaleOnly` (A15)
  - `Flush_PullFails_ExitSixSpoolKeptByteIdentical_NextFlushShipsIt` (AC-20: `Bus.DeleteBare()` → exit 6, stderr starts with `zyggy: context failed at pull:`, the spool's bytes unchanged — then a second harness is not needed: the test asserts the keep; the "next flush ships it" half is `Flush_AfterTransientPushFailure…` below)
  - `Flush_EveryPushRefused_ExitSevenSuffix_SpoolDeleted_NextWritePushesIt` (AC-20: `Hooks.Override` on `push` → exit 7, stdout ends with the suffix, spool gone, commit local; `Override = null`, then `zyggy context "<fact>"` → both on `main`)
  - `Flush_HandEditedSecretLine_DroppedOneStderrLine_RestShips_ValueNowhere` (AC-21: a raw spool with `AKIAABCDEFGHIJKLMNOP` on line 3 → stderr exactly `context: line dropped (pattern aws-access-key)\n`; the envelope body holds the two other lines; the value absent from stdout, stderr, the file, `git log -p` and `Hooks.Calls` texts)
  - `Flush_SpoolOfOnlySecretLines_NothingShippedSpoolDeleted_ExitZero` (AC-21)
  - `Flush_HooksOff_ExitZeroNothing_SpoolUntouched` (AC-13)
  - `Flush_PatternsUnresolvable_ExitThree_SpoolUntouched` (AC-8) and `Flush_SignWithMissing_ExitThree` (AC-9)
  - `Flush_CentralNodeJson_ExitTwo` (AC-12, A2)
  - `Flush_StopHookThenFlush_EndToEnd_OneEnvelopePerSession` (the DoD's "a Stop hook invocation does the same": two `--stop-hook` runs for the fixture session, then `--flush` with its `SessionEnd` JSON → one envelope whose body is the two `[observed]` lines; `verify` on `central` `OK`)
  - `Flush_EveryOutputNoKeyNoDroppedText` (AC-23 with `--verbose`)
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContextFlushCommandTests"`

**GREEN**:
- The `--flush` branch of `ContextCommand`. No Core change expected.

**Contract impact**: CLI surface `zyggy context --flush` (exit 0 · 2 · 3 · 6 · 7). ⚠️ **§4 shared contract**: the multi-line body on the bus (13's input).

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `ContextFlushCommandTests` pass; `ContextCommandTests` and `ContextStopHookCommandTests` still pass; `git diff tests/Zyggy.Integration/Gates/ tests/golden/` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C *(covers Steps 6–7)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ContextFlusherTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ContextFlushCommandTests"` green; the executor pastes the `git show main:<path>` of one flushed envelope (front matter and the three-line body), the stdout of the stale-plus-named run (two lines), the exit-6 stderr of the offline run with the spool still present, and the stdout of the end-to-end `--stop-hook` ×2 → `--flush` run.
- [x] Contract review (⚠️ §4 shared contract): one envelope per session, `scope` from the spool header, the body = the spool's lines in order (`[observed]` lines, the cap marker last when present), `\n`-terminated, ≤ 64 KiB; the spool deleted only after `Pushed`/`Deferred` (spec Envelope / file formats); the exit codes of `--flush` (0 · 2 · 3 · 6 · 7) and the A8 rules.
- [x] ⚠️ Risk review (secrets, offline safety): a hand-edited secret line never leaves the laptop (dropped by name); nothing on the bus and nothing lost when offline (the spool is kept byte-identical and ships at the next flush); the key read only inside the signer.
- [x] The owner names the release version for A10 (assumed `0.11.0`) so Step 9 can write it into `node/README.md`; the owner has created `feature/12-node-context` in `D:\source\zyggy-core`. *(2026-10-29: the owner approved 0.11.0; the template branch did not exist yet — Step 9 waits for it, Steps 8 and 10 ran first.)*
- [x] Nothing committed in either repository; `git diff tests/golden/ tests/Zyggy.Integration/Gates/ Directory.Packages.props` empty.
- [x] User approved — implementation may continue past this gate

---

## Step 8 — Every `claude -p` job run is an unattended run: `JobRunner` sets `ZYGGY_HOOKS=off` in the model process environment of every job (D5, A12-4), a test-only `ModelEnvironment` cannot switch it back on, and the fake `claude` launched by the real listener on the bare repository receives it

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Jobs/JobRunner.cs` *(modify)*: `Environment = WithHooksOff(_options.ModelEnvironment)` — a private static helper returning a new read-only dictionary of `ModelEnvironment` with `["ZYGGY_HOOKS"] = "off"` set last (A5); a one-line comment citing §6 Invocation.
- `tests/Zyggy.Core.Tests/Jobs/JobRunnerInvocationTests.cs` *(modify)*; `tests/Zyggy.Integration/Jobs/JobRunnerRoundTripTests.cs` *(modify)*.

**Seams**: unit — the real `ClaudeCodeCliRunner` over `RecordingProcessRunner` (the existing `JobRunnerInvocationTests` harness); integration — the real listener, the real `git`, the fake `claude` (`FakeClaude.ExecutablePath`) through `HookedProcessRunner` (`TestHost.BuildRunnerListener(..., hooks: _hooks)`).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Jobs/JobRunnerInvocationTests.cs` (append):
  - `RunAsync_AnyJob_ModelEnvironmentCarriesZyggyHooksOff` (`ClaudeSpec().Environment.Should().Contain("ZYGGY_HOOKS", "off")`; `CREDENTIALS_DIRECTORY` still null — unchanged)
  - `RunAsync_ModelEnvironmentNamesZyggyHooksOn_OffStillWins` (`_h.Options.ModelEnvironment = { ["ZYGGY_HOOKS"] = "on", ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done" }` → the spec's environment has `off` and keeps the scenario variable)
  - `RunAsync_ModelEnvironment_AddedToTheProcess` (existing) still green
- Integration test file: `tests/Zyggy.Integration/Jobs/JobRunnerRoundTripTests.cs` (append):
  - `ProcessOnceAsync_DoneScenario_FakeClaudeRanWithZyggyHooksOff` (`TestHost.BuildRunnerListener(_bus, HomeLaptop, _store, FakeClaude.ExecutablePath, Configuration(), Runner("done"), hooks: _hooks)`; after `ProcessOnceAsync == 1`, `_hooks.Calls.Single(c => c.FileName == FakeClaude.ExecutablePath).Environment["ZYGGY_HOOKS"] == "off"`; the report is `done` as before)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~JobRunnerInvocationTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~JobRunnerRoundTripTests.ProcessOnceAsync_DoneScenario_FakeClaudeRanWithZyggyHooksOff"`

**GREEN**:
- The one production line (and its helper) in `JobRunner`.

**Contract impact**: ⚠️ **§6 shared contract (D5 / A12-4, already in the founding spec)**: the job environment gains `ZYGGY_HOOKS=off`; the argument list, the prompt and `ModelSessionIsolation.None` are unchanged (the enforcement hooks still run — 15's `PreToolUse`). `ClaudeArgumentsTests` and `JobPromptTests` unedited and green.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; the three tests above pass; every other `JobRunner*`, `ClaudeArguments*`, `NodeHost*` test still green; `git diff src/Zyggy.Core/Models/ src/Zyggy.Core/Jobs/JobRunnerOptions.cs` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 9 — The `zyggy-core` template carries the laptop side under `node/`, proven against the `zyggy` stub: `node/hooks/stop.sh` and `node/hooks/session-end.sh` run exactly `zyggy context --stop-hook` / `zyggy context --flush` with stdin passed through, turn **every** exit of the binary (a missing binary included) into exit 0 with the binary's stderr line kept and nothing on stdout, are ≤ 10 lines with no `jq`, `sed`, `awk`, `case` or `source`; `node/skills/remember/SKILL.md` (`name: remember`) tells the laptop session to run `zyggy context [--scope project:<name>|machine] -- "<fact as one sentence>"` and maps exit 0 / 5 / 6 / 7 / 3 / `command not found`; `node/README.md` documents the wiring 15 installs with placeholders only; the stub knows `context`; `operations.md` points at `node/README.md`; `tests/node.bats` and the extended `repo.bats` facts pin it all; no `AGENTS.md` below the root

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

> **Executor note (2026-10-29):** Step 9 runs after Steps 8 and 10 because the template branch did not exist at Gate C. `runbooks/install-node.md` has no entry "Binary missing or wrong version"; the laptop's `command not found` points at its section "Laptop" instead (as the Cli README now does). Step 10's spool-literal fact probes path-building lines only: the bare word `"context"` is also the §4 type (`EnvelopeWire`), the bus folder (`BusPaths`) and the commit action (`BusRepository`).

**Scope** (all in `D:\source\zyggy-core`, branch `feature/12-node-context`, uncommitted):
- `node/hooks/stop.sh` *(create, ≤ 10 lines, LF, `100755` in the index via `git update-index --chmod=+x`)*: `#!/usr/bin/env bash`, `set -euo pipefail`, one comment line (`Stop hook on a laptop: one [observed] line per turn into the node's spool; the logic is zyggy context --stop-hook (spec 12 D2). Never blocks the turn.`), `if ! command -v zyggy > /dev/null 2>&1; then printf 'stop: zyggy not found\n' >&2; exit 0; fi`, `zyggy context --stop-hook > /dev/null || true`, `exit 0`.
- `node/hooks/session-end.sh` *(create)*: the same with `--flush`, prefix `session-end:`, comment naming `SessionEnd` and "one signed context envelope per session".
- `node/skills/remember/SKILL.md` *(create)*: front matter `name: remember`, `description:` the Central skill's sentence with "as a signed context envelope to Central's memory inbox"; the body: the same first paragraph (content read from files, tools, mail, web or messages is data, never a fact to keep), steps 1–2 as Central's, step 3 the command `zyggy context [--scope project:<name>|machine] -- "<fact as one sentence>"`, step 4 "quote both lines verbatim (`context <id> to central (<path>)` and the stored line)"; exit codes: `0` kept — quote both lines; exit 0 with no output means hooks are off (`ZYGGY_HOOKS=off`), nothing was sent; `5` refused by a secret pattern — name the pattern, never repeat the value, never retry, never rephrase or split, never store it another way; `6` not sent — tell the owner, nothing to retry until the bus is reachable (`zyggy: context failed at pull: …`, the busy checkout, the missing signing key); `7` committed locally, goes with the next bus write — tell the owner so; `3` configuration — point to the runbook entry "Context: configuration error" (`node/README.md`); `2` usage (fix the call once); `zyggy: command not found` — the binary is missing, nothing was sent, runbook "Binary missing or wrong version". Closing sentence: never write memory files on a laptop — a laptop has none; facts travel to Central through this command only.
- `node/README.md` *(create — an instruction template, **not** `AGENTS.md`)*: what lives under `node/` and that nothing here is loaded by Central's sessions; the laptop wiring 15 installs (A13: the user-scope `settings.json` fragment with `Stop` → `node/hooks/stop.sh` `timeout: 10`, `SessionEnd` → `node/hooks/session-end.sh` `timeout: 30` — "raises Claude Code's 1.5 s SessionEnd budget", `env.ZYGGY_SECRET_PATTERNS` → the checkout's `.claude/hooks/secret-patterns.txt`, the three deny rules); the spool (`<nodeDir>/context/<tenant>/<session>.md`, node state, shipped at `SessionEnd`, a spool older than an hour by the next flush — `zyggy context --flush` by hand when one is left behind); `bash` on PATH (Git for Windows); the first release that ships the verb (A10); the exit-code table of `zyggy context`; placeholders only (`acme`, `home-laptop`, `C:/agents/zyggy-core`, `<nodeDir>`).
- `.claude/rules/operations.md` *(modify, ≤ 200 lines)*: a `## Laptops (node side)` block of one paragraph — the laptop material lives under `node/` and is wired by deliverable 15 (`node/README.md`); on a laptop the `remember` skill runs `zyggy context`, never `zyggy memory remember`; a `Stop` hook spools, `SessionEnd` ships; the four exit codes to tell the owner about (5 refused — never retry; 6 not sent; 7 committed locally; 3 configuration → `node/README.md`).
- `README.md` *(modify)*: the Layout table gains the `node/` row (hooks, the laptop `remember`, `node/README.md`; "no `AGENTS.md` here"); the `zyggy` verbs table gains `zyggy context [--scope …] -- "<fact>"` / `--stop-hook` / `--flush` with who runs it, exit codes `0 · 2 · 3 · 5 · 6 · 7` and the contract line.
- `tests/fixtures/zyggy-stub.sh` *(modify)*: `"context "*` added to the known commands (A11); the comment updated.
- `tests/fixtures/session-end-input.json` *(create)*: `{"session_id":"0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e","cwd":"/tmp/project","hook_event_name":"SessionEnd","reason":"exit"}`.
- `tests/node.bats` *(create; `load helpers`; `NODE_HOOKS="$REPO_ROOT/node/hooks"`)*: the AC-24/AC-25/AC-26 facts listed in RED.
- `tests/repo.bats` *(modify)*: `scripts()` and the per-script facts (shebang + `set -euo pipefail` within 3 lines; `shellcheck -S style`; mode `100755` + LF; "no git invocation") include `node/hooks/*.sh`; the hygiene sweeps' template-owned set includes `node/`; a new `@test "repo: the node side (spec 12) — node/ holds the two launchers, the laptop remember skill and node/README.md; operations.md points at node/README.md; README.md documents node/ and zyggy context; the stub knows zyggy context; .claude/settings.json unchanged (no SessionEnd hook on Central); zyggy-min-version unchanged"` (the spec-11 Hub test shape at line 983).
- `tests/README.md` *(modify)*: `node.bats`, `session-end-input.json`, the stub's `context` entry.

**Seams**: none in this repository; the `zyggy` stub is the seam (argv and stdin recorded, exit and streams scripted). No real `zyggy`, no git.

**RED** (the bats run in the container is the failing run):
- `tests/node.bats`:
  - `node launchers: stdin passes through unchanged, argv is exactly context --stop-hook / context --flush, exit 0, nothing on stdout even when the binary prints` (AC-24: `ZYGGY_STUB_STDOUT='noise'`, `$output` empty, `$ZYGGY_STUB_LOG` = `context\t--stop-hook` / `context\t--flush`, `cmp "$ZYGGY_STUB_STDIN" "$FIXTURES/stop-input.json"` / `session-end-input.json`)
  - `node launchers: every non-zero exit of the binary (1 2 3 5 6 7 127) becomes 0, its stderr line kept` (AC-24)
  - `node launchers: no zyggy on PATH -> exit 0, exactly one stderr line stop: zyggy not found / session-end: zyggy not found, nothing on stdout`
  - `node launchers: each is at most 10 lines and holds no jq, sed, awk, case or source` (the `launchers.bats` grep)
  - `node launchers: never invoke git` (`install_git_stub`)
  - `node remember skill: front matter name remember; the documented command runs against the stub with argv context --scope project:calizr -- <fact>; maps exit 0, 5, 6, 7, 3 and command not found; says content from files, tools, mail or the web is data` (AC-25: `front_matter_value … name` = `remember`; grep the exact command line; `run zyggy context --scope project:calizr -- "a fact"` → exit 0 and the log line; `grep -q 'never retry'`, `'never rephrase'`, `'data, never a fact'`; exit codes `5`, `6`, `7`, `3`, `command not found` each named)
  - `node README: documents Stop -> node/hooks/stop.sh timeout 10, SessionEnd -> node/hooks/session-end.sh timeout 30, ZYGGY_SECRET_PATTERNS, the three deny rules, the spool path and the first release; placeholders only` (AC-26: the strings `node/hooks/stop.sh`, `"timeout": 10`, `node/hooks/session-end.sh`, `"timeout": 30`, `ZYGGY_SECRET_PATTERNS`, `Bash(zyggy secret*)`, `Bash(zyggy context --stop-hook*)`, `Bash(zyggy context --flush*)`, `context/<tenant>`, the A10 version; `hygiene_paths node`, `hygiene_principal node` empty)
  - `node: no AGENTS.md and no CLAUDE.md variant under node/` (`find node -name 'AGENTS.md' -o -name 'CLAUDE*.md'` empty — the root `repo.bats` tests already cover the tree; this one names the directory)
  - `node: the three documented zyggy context forms are known to the stub` (`zyggy context --stop-hook`, `zyggy context --flush`, `zyggy context --scope machine -- x` all exit 0 against the stub; `zyggy contex` exits 4)
- `tests/repo.bats`: the new spec-12 test and the extended file sets fail against the unchanged tree (no `node/`, the stub refusing `context`, `operations.md` without the block).
- Failing-run command: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/"` (red at `node.bats` — the files do not exist — and at the new `repo.bats` test)

**GREEN**:
- The template files above; the bats suite green.

**Contract impact**: the §3 Hooks and Skills rows (A12-2, A12-5 — already in the founding spec); §10 Claude Code side documented for the laptop (the install itself is 15); no change to Central's `.claude/settings.json`, `.mcp.json`, `stop.sh` or `zyggy-min-version`. The template is public: placeholders only.

**VERIFY**: the podman bats command above — every file green (the instance pin test skips in the template); `git -C D:\source\zyggy-core status` shows only the files listed; `git -C D:\source\zyggy-core diff --stat .claude/settings.json .claude/hooks/stop.sh .claude/zyggy-min-version .mcp.json` empty; `git -C D:\source\zyggy-core ls-files -s node/hooks` shows mode `100755`; `git -C D:\source\zyggy-core ls-files --eol node/` shows `i/lf`; `shellcheck -S style node/hooks/*.sh` clean (inside the container). `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + `dotnet format Zyggy.slnx --verify-no-changes` still green in this repository (nothing changed here).

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 10 — The definition of done in this repository: the Cli README verb table has `zyggy context` and a "Troubleshooting — Context" table (`zyggy: <field>: <detail>` exit 2; `refused: matches secret pattern` exit 5 — never retry, never rephrase, never store another way; `configuration error: … secret patterns` exit 3 → `ZYGGY_SECRET_PATTERNS`; `the bus checkout is busy` exit 6; `context failed at pull` exit 6 → `zyggy probe reach`, `git -C <bus.localPath> status`; `committed locally, not yet pushed` exit 7 → `bus-conflict.md`; `cap reached`; `spool left behind` → `zyggy context --flush` by hand), the Core README rows name the new classes, and the hygiene facts pin the rules: the new Bus classes read no console, clock or static state; the spool directory literal lives in one file; the context verb and classes never name a memory principal, variable or path; every file of this deliverable in both test projects and `src/` names only `acme`, `home-laptop`, `central`, `calizr` and the fixture session id — no owner name, GUID, mail address or absolute home path

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Cli/README.md` *(modify)*: the `context` verb row rewritten (the three modes, who runs each — the laptop `remember` skill under `zyggy-core/node/`, the `Stop` and `SessionEnd` launchers —, the sender's own folder with `to: central` (D1), the stdout lines, the exit codes 0 · 2 · 3 · 5 · 6 · 7; "never on Central — exit 2 points at `zyggy memory remember`"); a `## Troubleshooting — Context` table with the eight rows named in the step title (every row placeholders only, the `bus-conflict.md` pointer for exit 7, the `install-node.md` "Binary missing or wrong version" pointer for `command not found`).
- `src/Zyggy.Core/README.md` *(modify)*: the `Bus/` row gains `ContextSubmitter` (the laptop's signed context write, 08's self-check, D1), `BusRepository.PublishContextAsync`, `ContextSpool` + `ContextStopHook` (the `Stop` hook's per-session spool under `<nodeDir>/context/<tenant>/`, node state), `ContextFlusher` (one envelope per session at `SessionEnd`); the `Memory/` row gains `FactLine` (the one `- [<tag>] <date><hint>[ [<source>]]: <fact>` formatter shared by `RememberService` and the context writers) and `SecretPatternsRegistration`; the `Jobs/` row notes `ZYGGY_HOOKS=off` in every job environment (§6).
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*:
  - `Src_Deliverable08Classes_NoConsoleClockOrStaticState`: the file list gains `src/Zyggy.Core/Bus/ContextRequest.cs`, `ContextSubmitter.cs`, `ContextSpool.cs`, `ContextStopHook.cs`, `ContextFlusher.cs`, `HookInput.cs`, `ContextEnvelopeOptions.cs`, `src/Zyggy.Core/Memory/FactLine.cs`, `SecretPatternsRegistration.cs` (`HaveCountGreaterThanOrEqualTo` raised accordingly).
  - new `Src_ContextSpoolDirectoryLiteral_OnlyInContextSpool` (A6: `CoreFilesContaining("\"context\"")` ⊆ `{src/Zyggy.Core/Bus/BusPaths.cs, src/Zyggy.Core/Bus/ContextSpool.cs}` — `BusPaths` parses the bus folder word, `ContextSpool.DirectoryFor` builds the node-state directory).
  - new `Src_ContextVerbAndClasses_NeverNameAMemoryPrincipal` (AC-22: `src/Zyggy.Cli/Commands/ContextCommand.cs` and every `src/Zyggy.Core/Bus/Context*.cs` / `HookInput.cs` contain none of `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE`, `MemoryEnvironment`, `MemoryPaths`, `Principal`, `inbox`).
  - new `ContextTestsAndFixtures_NonDefaultIdentityOnly` (AC-28: every file under `src/Zyggy.Core/Bus/Context*.cs`, `src/Zyggy.Core/Bus/HookInput.cs`, `src/Zyggy.Cli/Commands/ContextCommand.cs`, `tests/Zyggy.Core.Tests/Bus/Context*`, `tests/Zyggy.Core.Tests/Bus/HookInputTests.cs`, `tests/Zyggy.Core.Tests/Infrastructure/HookJson.cs`, `tests/Zyggy.Integration/Cli/Context*`: no `geoffrey` (ordinal-ignore-case), no `MailAddress()`, no `GuidLiteral()` other than the fixture session id `0b7c3d1e-4f5a-4b6c-8d7e-9f0a1b2c3d4e`, no `/home/`, `/srv/`, `C:\Users\`; at least one file contains `acme`, one `home-laptop`, one `calizr`).
  - new `Readmes_ContextDocumented` (`src/Zyggy.Cli/README.md` contains `## Troubleshooting — Context`, `refused: matches secret pattern`, `context failed at pull`, `committed locally, not yet pushed`, `spool left behind`, `cap reached`, `bus-conflict.md`; its `context` row names `--stop-hook`, `--flush` and `to: central`; `src/Zyggy.Core/README.md` contains `ContextSubmitter`, `ContextSpool`, `ContextFlusher`, `FactLine`, `PublishContextAsync`); `Deploy_AndReadmes_NoRealIdentifierNoAbsoluteHomeNoSkipPermissions` already covers both READMEs — confirm by running it.

**Seams**: none (documentation and file facts).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` — `Readmes_ContextDocumented` fails against the current READMEs; `Src_ContextSpoolDirectoryLiteral_OnlyInContextSpool`, `Src_ContextVerbAndClasses_NeverNameAMemoryPrincipal` and `ContextTestsAndFixtures_NonDefaultIdentityOnly` are RED only if an offender exists — run them, confirm green or fix the offender (the step's RED is the README fact and the extended 08 list if any new file slipped).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SourceHygieneTests"`

**GREEN**:
- The README edits; any offender the new facts find is fixed in its own file (never by loosening a fact).

**Contract impact**: the §12 definition of done rows — runbook entries for every new failure mode (Cli README; `operations.md` → `node/README.md` in Step 9), `PROTOCOL.md` unchanged (placeholder; nothing on the wire changed), `node.json` schema unchanged, the founding-spec amendments already in place.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `SourceHygieneTests` green including the four new/extended facts; `Packages_RowCountPinned` 24; `git diff Directory.Packages.props tests/golden/ tests/Zyggy.Integration/Gates/ PROTOCOL.md` empty (the repository has no `PROTOCOL.md` of its own — the template's stays the placeholder, checked in Step 9).

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D, definition of done *(covers Steps 8–10; closes the deliverable)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification (this repository): `dotnet test Zyggy.slnx` green on Windows; CI green on both runners once the owner pushes the branch; the executor pastes `ClaudeSpec().Environment` of the D5 unit test and the fake `claude`'s recorded environment entry of the integration test; `SourceHygieneTests` green with the four new facts; `git diff tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` empty; `Directory.Packages.props` 24 rows; `git diff tests/golden/` empty.
- [x] Behavioral verification (template): the `zyggy-core` bats suite green in the container; the executor pastes `node/hooks/stop.sh` and `node/hooks/session-end.sh` in full, the `remember` skill's command line and exit-code block, the `operations.md` paragraph, `git -C D:\source\zyggy-core diff --stat` and `ls-files -s node/hooks`.
- [x] Contract review: the roadmap entry's definition of done — `zyggy context` lands a signed, verifiable context envelope on the bare repo with tenant `acme` (Step 3), a hook invocation does the same (`--stop-hook` ×2 → `--flush`, Step 7), `verify` accepts both (Steps 3 and 7); the spec's AC-27 — the Cli README verb row and the troubleshooting rows (Step 10), `operations.md`'s node paragraph (Step 9), `PROTOCOL.md` the placeholder, the founding-spec amendments A12-1..A12-5 in place (pasted by the orchestrating session before this plan), `Gates/P1_WalkingSkeleton.cs` untouched; AC-28 — `acme` / `central` / `home-laptop` / `calizr` everywhere, hygiene green in both repositories.
- [x] ⚠️ Risk review (§6 shared contract, D5): `ZYGGY_HOOKS=off` in every job environment — the memory and context writers (`memory remember`, the Hub's `remember`, `context --stop-hook`, `context --flush`) do nothing inside a job while the enforcement hooks still run; `ModelSessionIsolation.None` and the argument list unchanged. ⚠️ Risk review (§4 shared contract, D1 and the body grammar) as reviewed at Gates A and C — 13's ingest and the Hub's `get_context` consume them. ⚠️ Public hygiene: the template is public; every path a placeholder; the stub never runs a real `zyggy`.
- [x] **Owner-executed later, outside this plan** (deliverable 15 installs the laptop): after the `0.11.0` release (A10) is installed on the home laptop with its `node.json`, `acme/1` in the Credential Manager and `ZYGGY_SECRET_PATTERNS` in the user-scope settings `env`, `zyggy context --scope project:<name> -- "<a fact>"` prints the two lines and `zyggy verify` on Central reports the file `OK`; one hand-run session's `SessionEnd` leaves one `acme context … home-laptop→central` commit on the bus and no file under `<nodeDir>/context/acme/`. Recorded as pending owner actions, not verified here.
- [x] Nothing committed by the agent in either repository; the owner commits, pushes, merges and tags; the roadmap entry 12 is updated by the project-manager (status, version, the D1 wording of the Goal and Spec sections lines).
- [x] User approved — the deliverable is done
