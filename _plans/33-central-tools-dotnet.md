# Plan: 33 — Microsoft 365 and `remember` as `zyggy` verbs — On Central every Microsoft 365 tool and `remember` is the tested, pinned `zyggy` binary instead of about 3,900 lines of bash, with the same answers, the same files, the same refusals and the same calls to Graph, and the complex shell is gone from the template

## Overview

After this deliverable `zyggy` has the verbs `memory remember` and `m365 check | token-test | cert-init | auth-header | mcp-server | guard | log | verify | state | facts | parse | brief | mail-backfill | files-backfill`. Each one reproduces the script it replaces in the `zyggy-core` template (`D:\source\zyggy-core\.claude\`): same arguments, exit codes, stdout/stderr texts, files and refusals. The certificate client assertion, the per-connection token, the guard before every send or move, the action log, the Draft audit and the secret-pattern refusal each become one .NET implementation, tested on both CI runners. The template keeps only thin launchers. The binary is installed on Central in Step 21, once Gates A–I are approved; the wait for 28's third night is gone, because 28 was closed on 2026-10-05 by the owner's clean-slate decision (see "Where the work happens"). The Central steps also re-run, on the new binary, the live checks carried over from 02, 23, 27 and 28 (Step 23). The plan implements `_specs/33-central-tools-dotnet.md` (approved 2026-10-05, zero Open Questions; its Decision Table, Contracts, parity table and AC-1..AC-45 are binding). Founding-spec sections: §1, §3, §6, §7, §8, §9, §10, §11, §12, §13, §14. The W33-1..W33-8 wording comes from the 33 spec; this plan never edits `_specs/00 …`.

> **Plan approved by the owner 2026-10-05** ("approved"), with the recommended answers to the planner's questions: (1) the stdin slash-command check on Central is run by the owner (probe script) or by the agent once `az vm run-command` is allowed — it only blocks Step 18; (2) the first release with these verbs is `v0.2.0`; (3) the labelled test fact written in Step 21 is left for the dream to file like any other fact (not undone — removing inbox lines would disturb the dream's ledger).

**Reference pattern**: deliverable 28, built and running. It is mirrored everywhere:

- **Seams.** `IProcessRunner`/`ProcessRunner`, `IModelRunner`/`ClaudeCodeCliRunner`/`ClaudeArguments`/`StreamJsonReader`. Changes are additive only.
- **Memory.** `MemoryPaths`, `MemoryFileWriter.WriteAtomically`, `SecretPatterns`.
- **CLI verbs.** `DreamConfiguration`/`VersionPin`, `CliApplication`/`CliEnvironment`/`ExitCodes`, and the verbs `MemoryDigestCommand` (no host, hook contract) and `DreamCommand` (pin check, service collection).
- **Unit-test helpers.** `tests/Zyggy.Core.Tests/Infrastructure/{Golden,RecordingProcessRunner,MemoryTree,SourceHygieneTests}.cs`.
- **Integration helpers.** `tests/Zyggy.Integration/Infrastructure/{ZyggyCli,FakeClaude,ScratchDirectory,ProcessProbe,MemoryRepoFixture}.cs`.
- **Fake model and goldens.** The `tools/fake-claude` contract and the `tests/golden/README.md` rules ("never produced by the code under test").
- **Behaviour oracle.** The 33 scripts and their bats suites `m365.bats` (94 cases) and `remember.bats` (15 cases), with the fixtures under `zyggy-core/tests/{fixtures,expected}`. They are the executable description of the behaviour to keep.
- **Plan shape.** `_plans/28-central-dream-local.md` is the style reference for slices, gates, Central steps and evidence in 0002.

**Phase**: 33 serves P0b but does not close it (29 and 30 are not started), so there is no `Gates/P<n>_*.cs` slice. The last 🛑 HUMAN GATE is the definition-of-done check against `ROADMAP.md` #33 and AC-1..AC-45.

**What this plan deliberately is not** (spec Out of Scope / Defer):
- No GitHub tools, no `github-read-token`, no `GitClient` clone methods, no runbook sections 11–12 (all 34).
- No `stop.sh`, no `session-start.sh` and no dream change.
- No reimplementation of the Softeria server or MarkItDown. No command-line verbs for the Graph reads. No bearer-printing `token` verb. No built-in copy of the tool partition.
- The morning-brief timer stays off. The application key is not moved or re-created.
- Template CI does not download the real binary. No Windows behaviour for these verbs beyond compiling and exiting 3.
- No `ZYGGY_M365_ORIGIN`. No test-only switches (`ZYGGY_NOW`, `ZYGGY_M365_STUB`, `ZYGGY_RETRY_SCALE`, `ZYGGY_PARSE_TIMEOUT`) in the binary.
- `mcp-wrapper.sh` is deleted without a successor.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — `remember` is a `zyggy` verb | 1, 2 | `zyggy memory remember` writes the same inbox line and file bytes as `remember.sh`, refuses a secret the same way, and the CLI hosts verbs that keep the scripts' own usage texts and exit codes | 🛑 after Step 2 (⚠️ shared fact-line contract with the dream, CLI host change, new public API) |
| B — The model's state and fact verbs | 3, 4 | `zyggy m365 state` and `zyggy m365 facts` read and write the shell's files unchanged | 🛑 after Step 4 |
| C — Configuration and document parsing | 5, 6 | `instance/m365.json` is validated rule for rule; `zyggy m365 parse` turns a downloaded file into bounded, secret-checked text and always deletes it | 🛑 after Step 6 |
| D — The application identity | 7, 8, 9 | The key is read only through the secret store with the shell's checks; a certificate client assertion mints a token; Graph is read with the shell's retry and host rules; `token-test`, `check`, `cert-init` work | 🛑 after Step 9 (⚠️ secrets, signing) |
| E — Guard, action log, Draft audit | 10, 11, 12 | Every out-of-policy send, upload or move is refused before the permission prompt, fails closed, is logged without content; the brief's Drafts are audited | 🛑 after Step 12 (⚠️ security controls) |
| F — MCP server and per-connection token | 13, 14 | `zyggy m365 mcp-server` starts the pinned server with exactly ten variables; `auth-header` prints one fresh bearer per connection; `--probe` checks the allowlist | 🛑 after Step 14 (⚠️ secrets, process launch) |
| G — Brief and backfills | 15, 16, 17 | `zyggy m365 brief`, `mail-backfill`, `files-backfill` run the model through `IModelRunner` with the D7 deny lists, resume the shell's checkpoints, stop cleanly on a signal | 🛑 after Step 17 (⚠️ shared contract `IModelRunner`) |
| H — Prompt on the command line (**conditional**) | 18 | Only if the Central check shows a `/skill` prompt on stdin is not expanded: the three m365 runs pass the prompt as the argument, as the scripts do | 🛑 after Step 18 |
| I — Template and instance call the verbs | 19, 20 | The template has only thin launchers, settings and skills name `zyggy` verbs, template CI drives them against a stub; the instance carries the units and a version check; runbook and 0002 are ready | 🛑 after Step 20 |
| J — Central runs the binary | 21, 22, 23 | Once Gates A–I are approved: released, installed, pinned; the guard refuses a hidden-recipient send live; one attended brief; the owner's one live send; secret sweep clean; the live checks carried over from 02, 23, 27 and 28 re-run on the new binary and recorded | 🛑 after Step 21 (⚠️ first live security controls) · 🛑 after Step 23 (definition of done, covers Steps 22–23) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally (Windows). Linux is proven by CI on both runners at each gate. Steps 19–22 also need the `zyggy-core` bats/CI and instance CI.

<!--
Decomposition strategy: vertical slices, not horizontal layers.
Fake = behaviour through the seams (IProcessRunner, IModelRunner, ISecretStore, TimeProvider, a stubbed HttpMessageHandler) with unit tests.
Wire = the real edge: the built zyggy binary, real files and modes, the real ProcessRunner, tools/fake-claude, a fake markitdown,
a fake MCP server, in-process verbs over a stubbed Graph handler.
Gate placement: one per slice, plus one extra after Step 21 (first live security controls on Central).
Slice J's final gate sits after Step 23 (carried-over live checks, added 2026-10-05 by the clean-slate decision).
33 does not close a §12 phase: no Gates/P<n>_*.cs slice.
-->

---

## Where the work happens (branch choice) and the former parallel run with 28

**Update 2026-10-05 (owner's clean-slate decision, `ROADMAP.md` #33 "Clean slate"):** "Before you implement I permit you to break and finish open work of preceding plans here in .Net so that we've in the end a clean slate with all finished work. In anyway the open items in other plans are all nearly finished with last test, you can test this later in the .net version." 02, 23, 27, 28 and 32 are Done; 28's final gate is ticked. Their open live checks are carried into this plan's Central steps (Step 23; the 32 ones go to 34). Consequences for this plan:
- **Lifted:** the wait for 28's third night (old rule 4) and the "shell writers not swapped while 28's nights are measured" reason of old rule 5. The binary may be installed on Central as soon as this plan's own Gates A–I allow (Step 21).
- **Still in force:** inbox fact lines byte-identical to today's (rule 4 below); the dream keeps reading and hashing them.
- **Unchanged:** the branch. All work stays on `feature/33-m365-verbs` until Step 21, where the branch is merged to `main` and `v0.2.0` is tagged from `main`, once Gates A–I are approved.

**Choice: a branch, not additive-on-`main`.** All 33 work lives on `feature/33-m365-verbs`, in each of the three repositories:

- **This repository.** A draft pull request to `main` makes `.github/workflows/ci.yml` (which runs on `pull_request`) build and test both runners on every push.
- **`zyggy-core`.** Same branch name.
- **`zyggy-geoffrey`.** Same branch name.

**Why a branch:**
- A release cut for a dream fix must not carry unfinished 33 behaviour. On `main`, any `v*` tag for a dream fix would ship half-built m365 verbs in the binary that the dream runs every night.
- On a branch, `main` stays exactly the released dream code until Step 21.
- The template and instance branches keep Central from pulling launchers that need a binary it does not have yet (binary before template, Executor note 2).

**Rules until the merge in Step 21** (from `ROADMAP.md` #33 "Parallel-run constraints" as amended by the clean-slate decision, and spec Behaviors):
1. **A dream fix always goes first.** It lands on `main` (bugfix agent) and is released from `main`. The 33 branch then merges `main` in, never the other way round.
2. **Shared code changes only additively**: new optional members and new types. This covers `src/Zyggy.Core/{Processes,Models,Memory,Git,Secrets}/`, `src/Zyggy.Cli/{CliApplication,CliEnvironment,ExitCodes}.cs` and the release workflow. No 28 assertion is edited or deleted.
3. **28's suites are checked at every gate**: `git diff main...HEAD -- tests/Zyggy.Core.Tests/{Dream,Models,Runs,Memory,Git} tests/Zyggy.Integration/{Dream,Models,Processes,Memory,Cli}` shows only added files or added test methods (AC-2).
4. **Inbox fact lines stay byte-identical** to the shell writers' (Steps 1–4 goldens); the dream hashes them into its ledger. This rule outlives the merge.
5. **Nothing is installed on Central and no pin changes before Step 21**, and the template and instance branches are merged only there. The reason is now the install order (binary → pin → pull), no longer 28's nights.

**Merge to `main`**: Step 21, once Gates A–I are approved (Gate H is approved even when Step 18 was skipped, per its own text). It is a merge of the branch (CI green on the PR). The tag then comes from `main`.

---

## Shared rules for fixtures, goldens and tests (Steps 1–18)

- **Principal in tests**: tenant `acme`, user `alice`, everywhere under `src/` and `tests/`. Mailbox, tenant, client and site values come only from the copied fixture `m365.json`, which already uses synthetic GUIDs and `*.example` addresses (`m365.bats` "no GUID but the fixture ones").
- **Golden oracles are byte copies of the template's bats oracles, never produced by the code under test.** Copy with a byte-preserving tool (28 Executor note 5), check `git ls-files --eol tests/golden` → `i/-text`, and add one paragraph per folder to `tests/golden/README.md` naming the source path and the `zyggy-core` commit SHA:
  - `tests/golden/m365/graph/**` ← `zyggy-core/tests/fixtures/graph/**`.
  - `tests/golden/m365/fixtures/**` ← `zyggy-core/tests/fixtures/m365/**` (every file **except** the bats stubs `*.sh`).
  - `tests/golden/m365/expected/**` ← `zyggy-core/tests/expected/m365-*`.
  - `tests/golden/memory/remember/inbox-after-remember.md` ← `zyggy-core/tests/expected/inbox-after-remember.md`.
  - `tests/golden/m365/tools/{enabled,excluded,actions,auth}.txt` + `server-version.txt`. Derived **by hand** from `zyggy-core/tests/fixtures/m365/{enabled-tools,excluded-tools,tools-0.157.2}.txt` and the `ZY_M365_ACTION_TOOLS`/`ZY_M365_AUTH_TOOLS` arrays of `m365-lib.sh`. These five files are also the content Step 19 puts into the template.
- **Dates.** The bats oracles were produced with `ZYGGY_NOW` (read the value from each bats case). Unit and in-process tests use `FakeTimeProvider` set to that instant plus a custom time zone (28 Shared rules: `TimeZoneInfo.CreateCustomTimeZone`; IANA only in `_OnLinux` facts). Binary-level tests use the real clock and compare only date-free bytes, or normalise the date in the test (never in the code).
- **Three test levels, chosen per edge:**
  - **U**: Core types with NSubstitute / `RecordingProcessRunner` / `StubGraphHandler` / temp dirs.
  - **I-in-process**: the whole verb (arguments → exit code + stdout + stderr) run inside the test through the Core verb classes with string writers, real files, the real `ProcessRunner`, fake-claude, and **only the Graph `HttpMessageHandler` stubbed**. This is the only way to reach Graph-dependent paths, because the binary has no test switch (spec deviation 2).
  - **I-binary**: `ZyggyCli.RunAsync` on the built `zyggy` for every path that needs no network: usage, configuration, refusals, hooks, files, `cert-init`, `parse`, `state`, `facts`, the send guard, the log, the server launch.
- **Never the real `claude`, `markitdown`, `ms-365-mcp-server`, Graph or the login host.** The fakes:
  - `claude` is `FakeClaude.ExecutablePath` with hand-written scenarios.
  - `markitdown` is a test-written `#!/bin/sh` script on Linux (`_OnLinux` facts; the verb exits 3 on Windows).
  - `ms-365-mcp-server` is a test-written script on Linux plus `tests/Zyggy.Integration/Infrastructure/FakeMcpServer.cs`, a `TcpListener` HTTP/1.1 responder on `127.0.0.1:<ephemeral>`.
  - The login host and Graph are `tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs`, linked into `Zyggy.Integration` like `TestKeys.cs`. It holds a route table modelled on `tests/golden/m365/graph/routes.tsv`. It records method, URI and headers, and fails the test on any host other than the two contracted ones, any `/me` path, any method other than GET (and the one token POST), or a followed redirect.
- **Test keys**: a throw-away RSA 2048 key + self-signed certificate generated per test run in a temp dir (`TestCertificates.Create()` in `tests/Zyggy.Core.Tests/Infrastructure/TestCertificates.cs`, linked into `Zyggy.Integration`). An EC key for the refusal case. No key material is committed.

---

## Step 1 — A fact the owner states becomes byte-for-byte the same inbox line and file as `remember.sh` writes; an existing inbox file keeps every byte except its `updated:` value; a secret-shaped fact or source is refused by name, never echoed (in-process verb, temp memory tree)

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Verbs/VerbIo.cs` *(create, public sealed record)*: `VerbIo(TextReader In, TextWriter Out, TextWriter Error, bool InputRedirected)` + `static VerbIo FromConsole()`. Out and Error write UTF-8 without BOM with `\n` newlines. It is the console of every 33 verb, so in-process tests capture exact bytes.
- `src/Zyggy.Core/Memory/TextCollapse.cs` *(create, internal static)*:
  - `Line(string)`: `zy_collapse_line`. CR, LF and TAB become a space, runs of spaces become one, one leading and one trailing space are removed; no other character is touched.
  - `CharCount(string)`: `wc -m`, Unicode scalar values via `EnumerateRunes`.
- `src/Zyggy.Core/Memory/FactLineWriter.cs` *(create, internal static)*: `Append(string path, string frontMatterName, string frontMatterDescription, DateOnly today, IReadOnlyList<string> lines)`. Exactly `zy_atomic_append`:
  - **Existing file**: re-emitted line by line with `\n` (a missing final newline is added, as awk does). Inside a front matter that starts at line 1 with `---`, a line starting `updated:` becomes `updated: <today>`. Every other byte is unchanged; CR bytes are kept.
  - **New file**: exactly `---\nname: <name>\ndescription: <description>\nupdated: <today>\n---\n`, with no YAML quoting.
  - Then each line + `\n`.
  - Written through `MemoryFileWriter.WriteAtomically` (28, reused unchanged). Never git.
- `src/Zyggy.Core/Memory/MemoryPaths.cs` *(modify, additive)*: `public string InboxFile(string fileName)`. It refuses separators, `.`, `..` and empty names (`ArgumentException`). It is the only place an inbox file path is built.
- `src/Zyggy.Core/Memory/SecretPatternsLocation.cs` *(create, internal static)*: `Resolve(IReadOnlyDictionary<string,string?> env) → string?`:
  1. `ZYGGY_SECRET_PATTERNS`;
  2. else `<instance>/../.claude/hooks/secret-patterns.txt`, where the instance dir is `ZYGGY_INSTANCE_DIR`, else `$CLAUDE_PROJECT_DIR/instance` (spec Configuration table).
- `src/Zyggy.Core/Memory/RememberVerb.cs` *(create, public sealed)*: `RememberVerb(IReadOnlyDictionary<string,string?> environment, TimeProvider clock)`, `Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken)`. Internal `RememberArguments.Parse` (the script's loop and every usage text, with `zyggy memory remember` in place of `remember.sh` in the usage line). The order is exactly the script's:
  1. `ZYGGY_HOOKS=off` → 0, no output.
  2. Principal/config (`remember: configuration error: …`, exit 3, `zy_require_config` texts incl. the time-zone check).
  3. Arguments (exit 4).
  4. Secret check of the fact, then of the source. Patterns are loaded here; a missing file → exit 3 `remember: configuration error: <path> is missing`. A match → exit 2 with stderr exactly `refused: matches secret pattern <name>`.
  5. Line `- [<tag>] <date><hint><provenance>: <fact>`.
  6. `FactLineWriter.Append(…, "remember <date>", "facts stated by the owner on <date> (remember skill)", …)`.
  7. Stdout `remembered: <absolute path>\n<line>\n`.
- `src/Zyggy.Core/Memory/RememberService.cs` *(create, internal sealed)*: steps 4–6 without the hooks gate, for the brief's memory line (Step 15).
- `tests/golden/memory/remember/inbox-after-remember.md` *(copy)*; `tests/golden/README.md` *(modify)*.
- Tests *(create)*: `tests/Zyggy.Core.Tests/Memory/TextCollapseTests.cs`, `FactLineWriterTests.cs`, `RememberVerbTests.cs`, `tests/Zyggy.Core.Tests/Infrastructure/VerbConsole.cs` (a `VerbIo` over `StringReader`/`StringWriter`, with `Stdout`/`Stderr` strings).

**Seams**: `TimeProvider` (`FakeTimeProvider` at the bats `ZYGGY_NOW`, custom `Test/Brussels` zone). A temp memory tree via `MemoryTree`. `SecretPatterns` over the 28 golden copy `tests/golden/secret-patterns/`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~TextCollapseTests|FullyQualifiedName~FactLineWriterTests|FullyQualifiedName~RememberVerbTests"`, fails at compile time). One test per `remember.bats` case (parity table row 1):
- `RememberVerbTests`:
  - `Run_Default_PrintsRememberedPathAndLineAndWritesStatedLine`.
  - `Run_FiveAc27Variants_FileByteEqualsGolden` (the five variants of the bats case in order → bytes equal `inbox-after-remember.md`).
  - `Run_ExistingFile_KeepsLinesAndRewritesUpdated`.
  - `Run_LocalDateFromTimeZone`.
  - `Run_FactWithCrLfTab_CollapsedToOneLine`.
  - `Run_EmbeddedStatedPrefix_StoredAsTextInOneBullet`.
  - `Run_Fact1000Characters_Accepted`.
  - `[Theory] Run_UsageError_ExitsFourOneStderrLineNothingWritten`: every row of the bats usage case, each with the exact message.
  - `[Theory] Run_SecretSample_ExitsTwoNamesPatternNeverEchoesNothingWritten` (`[MemberData]` over the copied positive samples).
  - `Run_SecretInSource_Refused`.
  - `[Theory] Run_BenignSample_Appended`.
  - `Run_TenantUnset_ExitsThree`.
  - `Run_HooksOff_ExitsZeroNoOutputNothingWritten`.
  - `Run_NoTempFileSurvives`.
  - `Run_ConfigurationBeforeUsage_ExitsThreeEvenWithBadArguments` (the script's order).
  - `Run_SecretPatternsMissing_ExitsThree`.
- `FactLineWriterTests` (AC-9):
  - `Append_NewFile_ExactFrontMatterThenLine`.
  - `Append_Existing_PreservesEveryByteButUpdatedValue` (a fixture with CRLF body lines, a quoted `description`, an unknown key and blank lines).
  - `Append_UpdatedOutsideFrontMatter_Untouched`.
  - `Append_NoFrontMatter_LinesAppendedOnly`.
  - `Append_ExistingWithoutFinalNewline_AddsOneLikeAwk`.
  - `Append_NeverLeavesTempFile`.
- `TextCollapseTests`: CR/LF/TAB rows, double spaces, leading/trailing, a non-breaking space kept, `CharCount` with multi-byte characters.

**GREEN**: as Scope. `MemoryPaths` (28) gives `PrincipalDirectory` and `InboxFile`. The line is built from parsed values only, never from the raw argument vector.

**Contract impact**: ⚠️ The inbox fact-line format and the inbox file bytes are the dream's ledger input (hashed by `LineHash`). The spec requires byte identity (AC-10), proven against the copied oracle. ⚠️ New public API: `VerbIo`, `RememberVerb`, `MemoryPaths.InboxFile`.

**VERIFY**: the failing-run command passes; build/test/format green; `git diff main...HEAD` on 28's test folders shows additions only.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — `zyggy memory remember` works from the built binary with the script's exit codes 0/2/3/4, silent under `ZYGGY_HOOKS=off`, never runs git, and the CLI routes the 33 verbs to their own argument parsers while `dream` and `memory digest` behave exactly as before

- [x] Done

**Scope**:
- `src/Zyggy.Cli/RawVerbs.cs` *(create, internal static)*: `bool TryDispatch(string[] args, CliEnvironment environment, out Task<int> run)`. Raw verbs: `memory remember …` → `RememberVerb`; `m365 …` → `M365VerbHost` (registered from Step 4 on; until then `m365` falls through to System.CommandLine, which exits 2). The verb receives the remaining tokens **unchanged**, `--` included, so its own parser reproduces the script's usage texts and exit 4. Raw verbs build no generic host (AC-7).
- `src/Zyggy.Cli/CliApplication.cs` *(modify, additive)*: `RawVerbs.TryDispatch` is consulted first. A `remember` subcommand and an `m365` command with descriptions only are added so `zyggy --help` and `zyggy memory --help` list them (System.CommandLine never runs them). The parse, the exit-2 mapping and the `dream`/`memory digest` registration are unchanged.
- `tests/Zyggy.Integration/Memory/RememberCommandTests.cs` *(create)*.

**Seams**: none new. Wires the CLI to `RememberVerb` over the real file system and real clock.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~RememberCommandTests|FullyQualifiedName~ZyggyCliTests|FullyQualifiedName~MemoryDigestCommandTests|FullyQualifiedName~DreamCliTests"`):
- `RememberCommandTests` (AC-8 I, AC-6, AC-7):
  - `Remember_Default_ExitZeroStdoutRememberedAndFileLine` (date compared to the real local date read before and after the run).
  - `Remember_SecretSample_ExitTwoStderrNamesPatternNothingWritten`.
  - `Remember_UnknownOption_ExitFourUsageNamesVerb` (stderr line contains `usage: zyggy memory remember`).
  - `Remember_TenantUnset_ExitThree`.
  - `Remember_HooksOff_ExitZeroNoOutput`.
  - `Remember_DoubleDashThenDashedWords_FactKeptVerbatim` (`-- --not-an-option text`).
  - `Remember_NeverInvokesGit` (`PATH` with a `git` shim that records any call → no record; `_OnLinux`).
  - `Remember_OnLinux_FileMode0644AndNoTempFile` (same mode as `mv` of a `umask 022` file).
- `ZyggyCliTests`, `MemoryDigestCommandTests`, `DreamCliTests` (28, unchanged) stay green: the regression guard for the host change.

**GREEN**: as Scope. `Program.cs` is unchanged.

**Contract impact**: new CLI verb `zyggy memory remember` (W33-1). The CLI host change is additive. ⚠️ Shared host with 28: reviewed at Gate A.

**VERIFY**: the failing-run command passes; build/test/format green; CI green on the PR (both runners).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (`remember` is a `zyggy` verb) *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: show that `zyggy memory remember` run by hand writes the same line and the same file bytes as the shell version. Show the copied oracle next to the test output, the secret refusal (stderr names the pattern, the value never appears), and `ZYGGY_HOOKS=off` staying silent. CI is green on both runners.
- [x] Contract review: the line format and the new-file front matter match spec AC-8/AC-9. Every exit code and usage text matches `remember.sh`, with only the usage line naming the verb. `dream` and `memory digest` still answer exactly as before.
- [x] ⚠️ Risk review: the dream hashes these lines, so byte identity is the safety net. 28's test folders gained files and methods only. The new public types are `VerbIo`, `RememberVerb` and `MemoryPaths.InboxFile`. The CLI change is additive and the routing table is small.
- [x] User approved — implementation may continue past this gate — 2026-10-05 owner: "approved"

---

## Step 3 — The model's named state (watermarks, cursors, replied ids) and its candidate facts are read and written exactly as `state.sh` and `facts.sh` do, files the shell wrote are read unchanged, and every refused fact is counted by reason and never echoed (in-process verbs)

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/M365Paths.cs` *(create, internal sealed)*:
  - The state dir `<ZYGGY_STATE_DIR or ~/.local/state/zyggy>/m365`.
  - `StateFile(string name)`: a closed set of name shapes — `mail-watermark`, `backfill-<arg>.watermark`, `drive-<arg>.token`, `files-backfill-<arg>.watermark`, `replied-<date>.ids`, `actions.jsonl`, `brief.jsonl`, `brief-<date>.json`, `mail-backfill.json`, `files-backfill.json`. `<arg>` must match `^[A-Za-z0-9!_=-]{1,200}$`.
  - The download root `$HOME/.cache/zyggy-m365-downloads`.
  - The key and certificate paths (`ZYGGY_M365_KEY_FILE`/`ZYGGY_M365_CER_FILE`, else `${XDG_CONFIG_HOME:-~/.config}/zyggy/m365-app.{key,cer}`).
  - It is the only builder of m365 paths. It is created here with the state members; the others are used from Steps 5–14.
- `src/Zyggy.Core/M365/M365Grammar.cs` *(create, internal static)*: the regexes of `m365-lib.sh` (GUID, UPN, DATE, ISO, ID, DRIVE_ID, ITEM_ID, CURSOR) as `[GeneratedRegex]`.
- `src/Zyggy.Core/M365/M365State.cs` *(create, internal sealed)*: `Get/Set/Reset(StateKey, string? arg, string? value)`.
  - Grammars: `iso` / `cursor` / `id`.
  - Absent `mail-watermark` → now − 24 h.
  - `replied` appends each id once.
  - Writes are atomic, the temp file `<file>.tmp` at 0600 in a 0700 dir (`UnixCreateMode` on Linux).
- `src/Zyggy.Core/M365/StateVerb.cs` *(create, internal sealed)*: `state get|set|reset <key> [<arg>] [<value>]`. The script's parser, usage line and messages (prefix `m365-state:`). The removed D6 verbs (`list`, `mark`) → 4.
- `src/Zyggy.Core/Memory/FactValidator.cs` *(create, internal static)*: the `facts.sh` rules in its order:
  1. empty;
  2. non-letter start (Unicode letter);
  3. e-mail address;
  4. URL (case-insensitive);
  5. secret pattern `<name>`;
  6. phone (the `facts.sh` candidate regex + digit counts — **not** 28's `ContactDetailPatterns`).
  Plus the control-character removal and `Cut240` (239 characters + `…`, counted in Unicode scalar values).
- `src/Zyggy.Core/M365/FactsVerb.cs` *(create, internal sealed)*: `facts --kind … --source … [--max n]`.
  - The argument and source checks run in the script's order, including the secret check on `--source` → usage 4.
  - `zy_require_config`.
  - Duplicates are dropped against the file (sed `^- \[observed\] [0-9-]* \[[^]]*\]: `) and against the input.
  - The `--max` cap → lines up to the cap written, exit 5 `facts: cap <n> reached`.
  - `FactLineWriter.Append(…, "m365 <kind> <date>", "facts observed by the m365 <kind> run on <date> (facts.sh)", …)`.
  - The stderr counts line byte-equal to the script's (reason order, then secret reasons in first-met order, `duplicate`/`duplicates`). Stdout empty.
- `src/Zyggy.Core/M365/M365VerbHost.cs` *(create, public sealed)*: `M365VerbHost(IReadOnlyDictionary<string,string?> environment)` and an **internal** constructor taking `TimeProvider` and `Action<IServiceCollection>? configureServices` (the test hook; `Zyggy.Integration` already sees Core internals). `Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken)`. Dispatch table: verb name → verb class. An unknown verb → exit 4 `m365: unknown verb '<v>' (usage: zyggy m365 <verb> …)`. This step registers `state` and `facts`; later steps add theirs.
- `tests/golden/m365/**` *(copy, see Shared rules)*; `tests/golden/README.md` *(modify)*.
- Tests *(create)*: `tests/Zyggy.Core.Tests/M365/M365StateTests.cs`, `StateVerbTests.cs`, `tests/Zyggy.Core.Tests/Memory/FactValidatorTests.cs`, `tests/Zyggy.Core.Tests/M365/FactsVerbTests.cs`.

**Seams**: `TimeProvider` (`FakeTimeProvider`). Temp state dir and memory tree.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~M365StateTests|FullyQualifiedName~StateVerbTests|FullyQualifiedName~FactValidatorTests|FullyQualifiedName~FactsVerbTests"`):
- `M365StateTests` / `StateVerbTests` (AC-28; the three `state:` bats cases):
  - `Get_MailWatermarkAbsent_NowMinus24h`.
  - `SetThenGet_EveryKey_RoundTripsAndFileNameAsShell` (`[Theory]` over the five keys).
  - `Set_Replied_AppendsEachIdOnce`.
  - `[Theory] Set_BadValue_ExitsFourWithShellMessage` (iso, cursor, id grammars).
  - `[Theory] UnknownKeyOrMissingArg_ExitsFour`.
  - `RemovedD6Verbs_ListMark_ExitFour`.
  - `FilesBackfillWatermark_PlainIsoAndIsoPipeId_BothAccepted`.
  - `Get_FileWrittenByShell_ReturnedUnchanged` (fixture bytes with a trailing LF).
  - `Set_LeavesNoTmp`.
  - `Set_OnLinux_File0600Dir0700` (`SkipUnless = IsLinux`).
- `FactValidatorTests` (AC-12):
  - one `[Theory]` row per reason;
  - the IBAN, 16-digit, `www.`, emoji-only and long-token lines of the bats case;
  - `Phone_Date01102026_Passes`;
  - `Phone_Plus8Digits_Refused`;
  - `Phone_Leading0Nine_Refused`;
  - `Cut_241Characters_239PlusEllipsis`.
- `FactsVerbTests` (the four `facts:` bats cases):
  - `Run_BriefFixture_ExitZeroNoStdoutFileByteEqualsExpected` (`m365-facts-brief.md`).
  - `Run_BackfillFixture_ByteEqualsExpected`.
  - `Run_RefusedFixture_CountsLineByteEqualRefusedTextNeverEchoed`.
  - `Run_Max3FourGoodLines_ThreeWrittenExitFive_SecondRunAppendsDropsDuplicates`.
  - `[Theory] Run_BadArguments_ExitFourWithShellMessage` (no `--kind`, `--kind x`, no `--source`, a source with a newline, brackets, an e-mail, a URL or a secret; a bad `--max`).
  - `Run_HooksOff_Accepted`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ The state file names and grammars, and the `m365-<kind>-<date>.md` fact lines, are shared contracts (skills, dream ledger, 23 evidence). They are reproduced byte for byte. ⚠️ New public type `M365VerbHost`.

**VERIFY**: the failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — `zyggy m365 state` and `zyggy m365 facts` work from the built binary: the shell's state files on disk are read and advanced in place, facts read from stdin land in the inbox file, a cap stops at exit 5, and an unknown verb or option exits 4

- [x] Done

**Scope**:
- `src/Zyggy.Cli/RawVerbs.cs` *(modify)*: `m365 …` → `M365VerbHost`.
- `tests/Zyggy.Integration/M365/StateCommandTests.cs`, `FactsCommandTests.cs` *(create)*.
- `tests/Zyggy.Integration/Fixtures/m365-state/**` *(create)*: the shell's files in their exact shapes (`mail-watermark`, `backfill-AAMk….watermark`, `files-backfill-b!x.watermark` plain ISO and `ISO|id`, `replied-2026-10-01.ids`). The values come from the copied bats fixtures.

**Seams**: none new; the real binary, real files, real clock.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~StateCommandTests|FullyQualifiedName~FactsCommandTests"`):
- `StateCommandTests`:
  - `State_GetShellWrittenFiles_PrintedUnchanged`.
  - `State_SetCursorThenGet_RoundTrips`.
  - `State_ReplacedFile_OnLinux_Mode0600NoTmp`.
  - `State_UnknownKey_ExitFourUsageNamesVerb`.
  - `State_TenantUnset_ExitThree`.
  - `M365_UnknownVerb_ExitFour`.
- `FactsCommandTests`:
  - `Facts_StdinLines_ExitZeroStdoutEmptyStderrCountsLineInboxLines` (the date in the counts line and file name is the real local date; the test derives the expected file from the oracle by replacing the oracle's date — test-side only).
  - `Facts_MaxReached_ExitFive`.
  - `Facts_BadKind_ExitFour`.
  - `Facts_ExistingShellWrittenFile_FrontMatterKeptDuplicateDropped`.

**GREEN**: as Scope.

**Contract impact**: new CLI verbs `zyggy m365 state`, `zyggy m365 facts` (W33-1).

**VERIFY**: the failing-run command passes; build/test/format green; CI green on both runners.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (the model's state and fact verbs) *(covers Steps 3–4)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: the shell's own state files are read and updated by the binary without any conversion. A run of `zyggy m365 facts` on the brief fixture produces the same inbox file as the shell oracle. The refusal counts line names reasons but never the refused text. CI is green.
- [x] Contract review: the state file names, value grammars and 0600/0700 modes match `state.sh`. The facts rules, including the shell's phone rule (not the dream's), and the 240-character cut match `facts.sh`. The exit codes are 0/3/4/5.
- [x] ⚠️ Risk review: these files are read by the skills, the backfills and the dream; nothing changed shape. 28's suites are unchanged.
- [x] User approved — implementation may continue past this gate — 2026-10-05 owner: "approved"

---

## Step 5 — `instance/m365.json` is refused with the shell's first message for every misconfiguration before anything else happens, the certificate expiry warns or stops as today, and a downloaded document becomes bounded, secret-checked text with the input always deleted and anything outside the run directory left alone (fake process runner)

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/M365Environment.cs` *(create, internal sealed record)*:
  - Principal and memory root (the `zy_require_config` rules shared with Step 1).
  - The instance dir (`ZYGGY_INSTANCE_DIR` → `$CLAUDE_PROJECT_DIR/instance` → exit 3) and the checkout (its parent).
  - The config path (`ZYGGY_M365_CONFIG` → `<instance>/m365.json`), the secret-patterns path, `M365Paths`, the port (`ZYGGY_M365_PORT`, 1024–65535, default 47365), `ZYGGY_CLAUDE_PATH`, `HOME`, the time zone.
  - `static M365EnvironmentLoad Load(IReadOnlyDictionary<string,string?> env, bool principalFromSettings)`. With `principalFromSettings` (backfills only), unset `ZYGGY_MEMORY_ROOT|TENANT|USER|TIMEZONE` are taken from `<checkout>/.claude/settings.local.json` `env` (or `ZYGGY_M365_SETTINGS`); a set variable wins and no other key is read.
- `src/Zyggy.Core/M365/M365Configuration.cs` *(create, internal sealed record + `static M365ConfigurationLoad Load(string path, bool baseOnly, DateOnly todayUtc)`)*: rule for rule `zy_m365_load_config`.
  - Base subset: tenant_id, mailbox, timezone, language, cert.subject, cert.days.
  - Full: client_id, sp_object_id, cert.expires, `drives.*`, `actions.*` (incl. the obsolete `consent`), `brief.*`, `mail_backfill.*`, `files_backfill.*`.
  - Same order, same first message `configuration error: <text>`.
  - Expiry ≤ 30 days → warning text `certificate expires in <n> days — runbook 13 "Rotate the certificate"`; past → `certificate expired <date> — runbook 13 "Rotate the certificate"` (exit 3).
  - Integers must be JSON numbers with `floor == value` within bounds.
- `src/Zyggy.Core/M365/DocumentParser.cs` *(create, internal sealed)*: `ParseAsync(string input, ParseContext, CancellationToken) → ParseResult(int Exit, string Stdout, string Stderr)`.
  - Containment on the lexical path and on the resolved path (`Path.GetFullPath` + `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`). A symlink inside is deleted, never its target; outside → 5, left alone.
  - Then regular file → size ≤ the smaller `file_max_bytes` → type (`docx xlsx pptx pdf txt md csv json html htm`, any case) → `markitdown` resolution (`PATH`, then `$HOME/.local/bin`; missing → 3 `markitdown not found — runbook 13 "MarkItDown"`) → `prlimit` resolution (missing → 3).
  - Run through `IProcessRunner`: `prlimit --as=2147483648 -- <markitdown> <file>`, timeout 120 s, stdin closed, stdout cap = text cap + 4096 + margin.
  - Timeout → 6 `markitdown timed out after 120 s`. Non-zero → 6 `markitdown failed (<first stderr line, control chars removed, collapsed, secret-guarded, ≤ 200 chars>)`.
  - Text: control characters and CR removed; every line matching a secret pattern replaced by `[line withheld: matches secret pattern <name>]`; cut at the smaller `file_text_cap_bytes` with the newline rule and `[cut at <n> bytes]`.
  - Stderr `parse: <name> <n> lines, <w> withheld[, cut at <n> bytes]`.
  - The input is deleted in a `finally` once known to be inside.
  - On Windows → 3 `parse: not supported on this platform` (spec Edge Cases).
- `src/Zyggy.Core/M365/ParseVerb.cs` *(create, internal sealed)*: `parse <file>`, `ZYGGY_M365_RUN_DIR` checks (3), usage (4), config load. Registered in `M365VerbHost`.
- Tests *(create)*: `tests/Zyggy.Core.Tests/M365/M365EnvironmentTests.cs`, `M365ConfigurationTests.cs`, `DocumentParserTests.cs`.

**Seams**: `IProcessRunner` (`RecordingProcessRunner`, scripted stdout/stderr/exit/timeout), `TimeProvider`. Temp run dir.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~M365EnvironmentTests|FullyQualifiedName~M365ConfigurationTests|FullyQualifiedName~DocumentParserTests"`):
- `M365ConfigurationTests` (AC-5; bats "misconfiguration", "cert.expires", "fixture validates"):
  - `Load_FixtureM365Json_Valid`.
  - `[Theory] Load_Misconfigured_FirstMessageAsShell`: one row per bats sub-case (missing file, invalid JSON, not an object, each GUID, UPN, time zone, language, cert.subject, cert.days, both site forms, `sites_granted` empty while `sites` set, `exclude_*`, `actions` missing, `consent` present, `actions.enabled` outside the set or duplicated, every integer and number, extensions, `write_drive_id` with upload enabled, every `brief`/`mail_backfill`/`files_backfill` key).
  - `Load_BaseOnly_IgnoresFullKeys`.
  - `Load_Expires20Days_WarningNamesRunbook`.
  - `Load_ExpiresToday_ZeroDays`.
  - `Load_ExpiredYesterday_ConfigurationErrorExpired`.
- `M365EnvironmentTests`:
  - `Load_InstanceDirFromEnvThenProjectDir`.
  - `Load_NoInstanceDir_Error`.
  - `[Theory] Load_PortOutOfRange_Error`.
  - `Load_PrincipalFromSettings_OnlyUnsetKeysAndOnlyThose` (a settings file that also holds `ZYGGY_HOOKS` and `ZYGGY_NOW` → ignored).
  - `Load_PrincipalFromSettings_NotUsedWhenFlagFalse`.
- `DocumentParserTests` (AC-29; the seven `parse:` bats cases at unit level):
  - `Parse_ReportDocx_SecretLineWithheldStderrSummaryInputDeleted` (stdout = `parsed-report.docx.txt` with the expected withheld line).
  - `Parse_BigPdf_CutAt20000WithMarker`.
  - `[Theory] Parse_Outside_RefusedFiveLeftAlone` (outside, `../`, a symlink inside pointing outside → the link deleted, the target kept; a symlinked directory).
  - `Parse_OverMaxBytes_Refused`.
  - `[Theory] Parse_TypeNotParsable_Refused` (`.exe .zip .jpg`, none).
  - `Parse_MarkitdownFails_ExitSixFirstErrorLine`.
  - `Parse_MarkitdownErrorMatchesSecret_Withheld`.
  - `Parse_Timeout_ExitSix`.
  - `Parse_Arguments_PrlimitAsThenMarkitdownThenFile` (recorded `ProcessSpec`: `FileName` = prlimit path, `Arguments` = `--as=2147483648`, `--`, markitdown path, file; `Timeout` = 120 s).
  - `Parse_MarkitdownMissing_ExitThreeNamesRunbook`.
  - `Parse_RunDirUnset_ExitThree`.
  - `Parse_InputDeletedOnEveryPathOnceInside`.

**GREEN**: as Scope. The secret check runs per line (the shell's halving is an optimisation only; the result is identical).

**Contract impact**: deviation 3 (`prlimit` instead of `ulimit -v`) as specified. The configuration keys are unchanged.

**VERIFY**: the failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — `zyggy m365 parse` runs a fake MarkItDown under a real `prlimit` on Linux, prints the bounded text and deletes the input; a bad configuration exits 3 before anything runs

- [x] Done

**Scope**:
- `tests/Zyggy.Integration/M365/ParseCommandTests.cs` *(create)*.
- `tests/Zyggy.Integration/Infrastructure/M365InstanceFixture.cs` *(create)*:
  - A temp checkout `<root>/checkout/{instance/m365.json, .claude/hooks/secret-patterns.txt, .claude/skills/m365/tools/*.txt}` from the goldens, plus a memory tree `acme/alice`, a `HOME` with `.local/bin`, a state dir.
  - `Env()` → the variable dictionary for `ZyggyCli`.
  - `WriteScript(string name, string body)` for Linux fakes (`chmod 755`).
- A fake `markitdown` script (written by the test): prints a fixture text, or fails with a stderr line, or sleeps.

**Seams**: wires `IProcessRunner` (real) with `prlimit` (util-linux on `ubuntu-latest`) and the fake MarkItDown.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ParseCommandTests"`):
- `Parse_OnLinux_DocxInRunDir_ExitZeroTextWithheldLineInputDeleted`.
- `Parse_OnLinux_MarkitdownOnlyInHomeLocalBin_Found`.
- `Parse_OnLinux_MarkitdownFails_ExitSixFirstLine`.
- `Parse_OnLinux_FileOutsideRunDir_ExitFiveFileKept`.
- `Parse_RunDirUnset_ExitThree` (both OSes).
- `Parse_OnWindows_ExitThreeNotSupported`.
- `Parse_NoArgument_ExitFour`.

**GREEN**: as Scope; fixes only inside `M365/`.

**Contract impact**: new CLI verb `zyggy m365 parse`.

**VERIFY**: the failing-run command passes on Windows (Windows rows) and in CI on ubuntu (Linux rows); build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C (configuration and document parsing) *(covers Steps 5–6)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: every misconfiguration of `instance/m365.json` from the shell suite gets the same first message before anything else happens. A document parsed on Linux returns bounded text with secret-shaped lines replaced, and the downloaded file is gone afterwards. A file outside the run directory is refused and left untouched. CI is green on ubuntu, where the Linux-only cases run.
- [x] Contract review: the configuration rules and messages match `m365-lib.sh`. The parse types, caps, messages and exit codes match `parse.sh`. The memory cap is now applied with `prlimit` (accepted deviation 3).
- [x] ⚠️ Risk review: no document text is logged or kept. Symlinks inside the run directory are removed without touching their target.
- [x] User approved — implementation may continue past this gate — 2026-10-05 owner: "approved"

---

## Step 7 — The application key is read only through the secret store, with the shell's location order and checks (mode, owner, header, tenant); a certificate client assertion is built and signed exactly as `graph.sh` builds it; a token request is one POST with no secret, and every auth failure gets the shell's message (stubbed login host)

- [x] Done

**Scope**:
- `src/Zyggy.Core/Secrets/CredentialFileSecretStore.cs` *(create, internal sealed : ISecretStore)*:
  - A read-only store built for one `TenantId` (the configured principal's) and one name → file table: `m365-app-key` → `$CREDENTIALS_DIRECTORY/m365-app-key` (source "credentials directory", modes 0600 or 0400) else the key file (source "file", 0600); `m365-app-key/new` → `<key file>.new` (source "file"). 34 adds `github-read-token` as one more row.
  - `internal Task<CredentialRead> ReadAsync(TenantId, SecretName, CancellationToken)` → `CredentialRead(byte[]? Value, CredentialSource Source, string? Refusal)`. Refusals are the shell texts: `key: not found in credentials directory or file`, `key: <path> is not a regular file`, `must be mode 0600 (is <mode>)`, `must be owned by <user>`, `is empty`, `has no PEM private-key header` (first line `-----BEGIN (RSA |EC )?PRIVATE KEY-----`).
  - `GetAsync` delegates: absent → `null`; refusal → `CredentialRefusedException(message)`; a different tenant → `CredentialRefusedException("key: tenant <t> is not this instance's")`.
  - `SetAsync`/`RemoveAsync` → `NotSupportedException`.
  - Never caches, never logs, puts no byte in a message. Mode and owner checks are Linux-only: the mode through `File.GetUnixFileMode`, the owner uid through `lstat` via `[LibraryImport("libc")]` (Assumption 5). On Windows `ReadAsync` → refusal `not supported on this platform`.
- `src/Zyggy.Core/Secrets/CredentialRefusedException.cs`, `CredentialSource.cs` *(create)*.
- `src/Zyggy.Core/M365/Graph/ClientAssertion.cs` *(create, internal static)*: `Build(RSA key, X509Certificate2 cert, AssertionAlgorithm alg, Guid tenantId, Guid clientId, DateTimeOffset now, Func<Guid> newJti) → string`.
  - Header `{"alg":"PS256","typ":"JWT","x5t#S256":<b64url sha256 DER>}` or `{"alg":"RS256","typ":"JWT","x5t":<b64url sha1 DER>}`, written with `Utf8JsonWriter` in this key order.
  - Claims in order `aud, iss, sub, jti, nbf, iat, exp` (exp = now + 300 s); `jti` UUID-v4 shaped.
  - Signature `RSA.SignData(…, SHA256, Pss|Pkcs1)`; base64url without padding.
- `src/Zyggy.Core/M365/Graph/GraphEndpoints.cs` *(create, internal static)*: the **only** file naming `https://login.microsoftonline.com`, `https://graph.microsoft.com/v1.0` and the scope `https://graph.microsoft.com/.default`.
- `src/Zyggy.Core/M365/Graph/GraphTokenClient.cs` *(create, internal sealed)*: `MintAsync(TokenRequest, CancellationToken) → TokenResult(string? AccessToken, DateTimeOffset? ExpiresAt, int ExitCode, string? Error)`.
  - Reads the key through `CredentialFileSecretStore.ReadAsync` and imports it with `RSA.ImportFromPem` (PKCS#8 `BEGIN PRIVATE KEY` and PKCS#1 `BEGIN RSA PRIVATE KEY`; EC → exit 3 `key: … is not an RSA key`). The key buffer is cleared with `CryptographicOperations.ZeroMemory` in `finally`.
  - Loads the certificate (`ZYGGY_M365_CER_FILE`; missing → 3 `certificate <path> not found`).
  - One form POST to `<login>/<tenant>/oauth2/v2.0/token` (`grant_type=client_credentials`, `scope`, `client_id`, `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`, `client_assertion`).
  - `AADSTS700024` → 6 `auth failed (clock skew, AADSTS700024) — check timedatectl on this machine`; another 4xx → 6 `auth failed (<error>) — runbook 13 "Certificate rejected"`; no `access_token` → 6.
  - The token is held only in the returned record. It never goes into `ToString`, an exception or a log.
- `src/Zyggy.Core/M365/Graph/GraphHttp.cs` *(create, internal sealed)*: owns the `HttpClient` built from the keyed `HttpMessageHandler` `"m365-graph"` (default `SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }`, 60 s timeout). HTTPS URIs only (an `http:` URI → `InvalidOperationException` before sending).
- `src/Zyggy.Core/M365/ServiceCollectionExtensions.cs` *(create)*: `AddZyggyM365(this IServiceCollection, M365Environment)` registers `M365Paths`, `CredentialFileSecretStore` (also as `ISecretStore`), `GraphHttp`, `GraphTokenClient`, `TimeProvider.System` (`TryAdd`), `AddProcessRunner()`.
- `tests/Zyggy.Core.Tests/Infrastructure/StubGraphHandler.cs`, `TestCertificates.cs` *(create)*; `tests/Zyggy.Integration/Zyggy.Integration.csproj` *(modify: link both, like `TestKeys.cs`)*.
- Tests *(create)*: `tests/Zyggy.Core.Tests/Secrets/CredentialFileSecretStoreTests.cs`, `tests/Zyggy.Core.Tests/M365/ClientAssertionTests.cs`, `GraphTokenClientTests.cs`.

**Seams**: `ISecretStore` (the new store over temp files), `TimeProvider` (`FakeTimeProvider`), the keyed `HttpMessageHandler` (`StubGraphHandler`).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~CredentialFileSecretStoreTests|FullyQualifiedName~ClientAssertionTests|FullyQualifiedName~GraphTokenClientTests"`):
- `CredentialFileSecretStoreTests` (AC-4; bats "token reads `$CREDENTIALS_DIRECTORY`…"):
  - `Read_CredentialsDirectoryFirst_SourceCredentialsDirectory`.
  - `Read_NoCredentialsDirectory_KeyFile_SourceFile`.
  - `Read_NewName_ReadsDotNewFile`.
  - `Read_Absent_NotFoundRefusal`.
  - `[Theory] Read_OnLinux_BadMode_Refused` (0644; 0400 for the file → refused; 0400 in the credentials directory → accepted).
  - `Read_OnLinux_Directory_NotRegularFile`.
  - `Read_Empty_Refused`.
  - `Read_NoPemHeader_Refused`.
  - `Get_OtherTenant_Refused`.
  - `SetAndRemove_NotSupported`.
  - `Read_NeverCaches_RotatedFileSeenNextRead`.
  - `Refusal_MessageNeverContainsKeyBytes`.
- `ClientAssertionTests` (AC-13):
  - `Build_Ps256_HeaderExactAndThumbprintSha256OfDer`.
  - `Build_Rs256_HeaderExactAndThumbprintSha1OfDer`.
  - `Build_Claims_AudIssSubJtiNbfIatExp300`.
  - `Build_Jti_UuidV4Shaped`.
  - `Build_Ps256_SignatureVerifiesWithCertificatePssSaltDigestLength`.
  - `Build_Rs256_SignatureVerifiesPkcs1`.
  - `Build_TamperedPayload_DoesNotVerify`.
- `GraphTokenClientTests` (AC-14):
  - `Mint_OnePostToTenantTokenEndpoint_FormFieldsExactNoSecret` (recorded request body parsed; no `client_secret`).
  - `Mint_ClockSkew_ExitSixNamesTimedatectl` (`token-clock-skew.json`).
  - `[Theory] Mint_Other4xx_ExitSixRunbookCertificateRejected` (`token-invalid-client.json`, `token-unauthorized-client.json`).
  - `Mint_NoAccessToken_ExitSix`.
  - `Mint_KeyFileMtimeUnchanged`.
  - `Mint_Pkcs1Key_Works`.
  - `Mint_EcKey_ExitThree`.
  - `Mint_CertificateMissing_ExitThree`.
  - `Mint_KeyBufferCleared` (the store returns a buffer the test keeps; all zero afterwards).
  - `Mint_HttpUri_Refused`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ Secrets: the key is read by new code; the §9 rule "only through `ISecretStore`" holds (the store is the only reader). ⚠️ Signing: the assertion shape is pinned by tests derived from `graph.sh`, not from the code.

**VERIFY**: the failing-run command passes; build/test/format green; CI green (the Linux mode rows).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — Graph is read only on the contracted hosts and paths with the shell's retry, re-mint and refusal rules, and the seven read operations return what `graph.sh` returned for the same fixture responses; a source-hygiene test keeps every Graph host and every `claude` reference in its one file (stubbed Graph)

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/Graph/IGraphReader.cs` *(create, internal interface)*: `MailFoldersAsync`, `DrivesAsync`, `DriveFilesAsync(string driveId)`, `DraftsSinceAsync(DateTimeOffset)`, `MessageSenderAsync(string id)`, `ItemExistsAsync(drive, parent, name)`, `ItemKindAsync(drive, item)`, plus `RawGetAsync(path, tolerated)` for `check`. Results are records (`MailFolder(Id, DisplayName, WellKnownName, TotalItemCount, Excluded)`, `Drive(Id, Name, Site)`, `DriveFile(Id, Path, Size, Modified)`, `DraftMessage(…)`, `MessageSender(From, ReplyTo, ConversationId)`, `ItemPresence {Exists, Absent}`, `ItemKind {Folder, File, Absent}`).
- `src/Zyggy.Core/M365/Graph/GraphReader.cs` *(create, internal sealed)*: the **only** implementation.
  - `/users/<mailbox>`, `/drives`, `/sites` only; never `/me`; GET only.
  - 429/503 retried up to 5 times after `Retry-After` seconds else 2^n s, through `TimeProvider.Delay`; a tolerated status is returned after the last retry.
  - One re-mint on 401; a second 401 → 6 `unauthorized (401) after a fresh token — runbook 13 "Certificate rejected"`.
  - 403 → 6 `forbidden (<code>) — runbook 13 "Scope or grant missing"`; throttled → 6 `throttled (<status>) after 5 retries — runbook 13 "Throttling"`; others → 6 `Graph request failed (<status>[ <code>])`.
  - A `@odata.nextLink` outside the Graph base → 6. More than 2,000 delta pages → 6. A drive listing 403/404 → 5 `drive <id>: <status> (not granted|not found)`.
  - A transport exception text matching a secret pattern → `request failed (curl error text withheld: matches secret pattern <name>)` (the shell's wording, kept).
  - The `drive-files` algorithm of `list_drive_files`: last occurrence wins, folders and deleted items dropped, paths rebuilt (depth ≤ 64), modified cut to seconds + `Z`, sorted by modified then id ordinal.
  - `Prefer: outlook.body-content-type="text"` on drafts; `$filter=createdDateTime ge <ISO>`, `$select`, `$top=50`.
  - `GraphReaderFailure` (exit code + message) is returned through a result type, never as a thrown exception, to the verbs.
- `src/Zyggy.Core/M365/ServiceCollectionExtensions.cs` *(modify)*: registers `IGraphReader`.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify, additive: new methods only; `Src_ContainsNoTenantLiteral` unchanged)* (AC-35):
  - `[Theory] Src_ServiceLiteral_OnlyInItsAdapter` over rows `(literal, allowed file)`: `login.microsoftonline.com` and `graph.microsoft.com` → `src/Zyggy.Core/M365/Graph/GraphEndpoints.cs`; `"claude"` as a program name → `src/Zyggy.Core/Models/ClaudeCodeOptions.cs`. 34 adds `api.github.com` as one more row.
  - `Src_ContainsNoMailboxSiteOrAccountLiteral` (no `@` address literal, no `sharepoint.com` literal, no GUID literal in `src/`).
  - `Tests_NeverResolveRealExternalPrograms`: no `ProcessSpec`/`ProcessStartInfo` in `tests/` is built with a bare `"claude"`, `"markitdown"` or `"ms-365-mcp-server"`; those names appear only as file names the tests write into temp dirs.
- Tests *(create)*: `tests/Zyggy.Core.Tests/M365/GraphReaderTests.cs`, `GraphRetryTests.cs`.

**Seams**: `HttpMessageHandler` (`StubGraphHandler` over the copied `tests/golden/m365/graph/*.json` + `retry-after-2.hdr`), `TimeProvider` (`FakeTimeProvider`, auto-advance on `Delay`), `GraphTokenClient` (real, stub login route).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~GraphReaderTests|FullyQualifiedName~GraphRetryTests|FullyQualifiedName~SourceHygieneTests"`):
- `GraphReaderTests` (AC-16; bats "mail-folders", "drive-files", "drafts-since/message-sender", "item-exists/item-kind"):
  - `MailFolders_TopLevelPlusOneChildLevel_ExcludedFromConfig`.
  - `Drives_OneDrivePlusGrantedSites_FirstIdKeptExclusionsByIdOrName`.
  - `DriveFiles_AllPagesPathsRebuiltLastWinsSorted` (`drive-delta-onedrive-1/2.json`).
  - `DriveFiles_ForeignNextLink_ExitSix` (`drive-delta-foreign-next.json`).
  - `DriveFiles_403_RefusedFiveNamed`.
  - `DriveFiles_Over2000Pages_ExitSix`.
  - `DraftsSince_FilterSelectTopAndPreferHeader`.
  - `MessageSender_LowerCasedFromReplyToConversation`.
  - `MessageSender_404_NotFoundExitSix`.
  - `ItemExists_200Exists404Absent_NameUrlEncoded`.
  - `ItemKind_FolderRootFileAbsent`.
  - `AnyRead_NeverMeNeverNonGetNeverHttp` (asserted by the stub over every test of the class).
- `GraphRetryTests` (AC-15; bats "429/503/401/403"):
  - `TwoTimes429Then200_Ok_WaitsRetryAfterOrPowerOfTwo`.
  - `503Then200_Ok`.
  - `Six429_ExitSixThrottled`.
  - `401Once_OneRemintThenOk`.
  - `401Twice_ExitSix`.
  - `403_ExitSixForbiddenCode`.
  - `Redirect302_NotFollowed_ExitSix`.
  - `TransportErrorWithSecretText_Withheld`.
- `SourceHygieneTests`: the three new methods.

**GREEN**: as Scope.

**Contract impact**: ⚠️ W33-5: the Graph adapter is an internal interface, not a seam. Reviewed at Gate D.

**VERIFY**: the failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 9 — `zyggy m365 check`, `token-test` and `cert-init` work end to end: `check` prints the shell's status lines, another mailbox being readable is reported and refused, `token-test` reports size and expiry but never the token, and `cert-init` creates, refuses, rotates and commits key pairs on disk with the shell's modes

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/CheckVerb.cs`, `TokenTestVerb.cs` *(create, internal sealed)*:
  - `check` prints exactly the `run_check` lines. Stderr `key: file|credentials directory`; the expiry warning comes first. `--counts` adds folder lines + `zyggy-drafts <k>`. `--other-mailbox` → 2xx `… — SCOPE NOT ENFORCED` + exit 5 `refused: scope not enforced — <upn> is readable; runbook 13 "Scope or grant missing"`. `--drive` → `granted|not granted|not found`.
  - `token-test [--key new] [--alg PS256|RS256]` → `token ok: <n> bytes, expires <yyyy-MM-ddTHH:mm:ssZ>`.
- `src/Zyggy.Core/M365/CertificateInit.cs`, `CertInitVerb.cs` *(create, internal sealed)*:
  - `ZYGGY_HOOKS=off` → 5 `refused: unattended run (ZYGGY_HOOKS=off)` before anything. Config base subset.
  - RSA 2048 → `ExportPkcs8PrivateKeyPem` written 0600 into a 0700 dir (`UnixCreateMode`, created `CreateNew`). Certificate from `CertificateRequest` (`CN=<cert.subject>`, `NotAfter = now + cert.days`) as PEM 0644.
  - Prints `thumbprint sha1: <UPPER HEX>`, `thumbprint sha256: …`, `expires: <yyyy-MM-dd>`, `certificate: <path>` — never the key.
  - Key exists → 5 `refused: key exists — use --rotate (<path>)`. `--rotate` writes `.new` (pending → 5). `--commit` swaps (no `.new` → 4 with the usage text).
  - On Windows → 3 `not supported on this platform`.
- `src/Zyggy.Core/M365/M365VerbHost.cs` *(modify)*: registers `check`, `token-test`, `cert-init`.
- `tests/Zyggy.Integration/M365/CheckCommandTests.cs` (I-in-process), `TokenTestCommandTests.cs` (both), `CertInitCommandTests.cs` (I-binary), `M365CommandParsingTests.cs` (I-binary) *(create)*.

**Seams**: wires the real file system, the real `CredentialFileSecretStore` over real files with real modes (Linux), and the verbs; Graph stays the stub (in-process).

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~CheckCommandTests|FullyQualifiedName~TokenTestCommandTests|FullyQualifiedName~CertInitCommandTests|FullyQualifiedName~M365CommandParsingTests"`):
- `CheckCommandTests` (AC-17):
  - `Check_StdoutByteEqualsExpectedWithStateDirNormalised` (`expected/m365-check.txt`; the test replaces its state-dir path).
  - `Check_Counts_FolderLinesAndZyggyDrafts`.
  - `Check_OtherMailboxReadable_ScopeNotEnforcedExitFive`.
  - `Check_OtherMailbox403_ScopeHolds`.
  - `[Theory] Check_Drive_GrantedNotGrantedNotFound`.
  - `Check_StderrKeySource_CredentialsDirectory`.
- `TokenTestCommandTests` (AC-18):
  - in-process: `TokenTest_PrintsSizeAndExpiryNeverToken` (the stub's token text is absent from both streams); `TokenTest_KeyNew_UsesPendingPair`; `TokenTest_Rs256_HeaderRs256` (the stub captures the assertion).
  - binary: `TokenTest_KeyMissing_ExitThree`; `TokenTest_OnLinux_KeyMode0644_ExitThreeShellMessage`; `TokenTest_OnLinux_EcKey_ExitThree`.
- `CertInitCommandTests` (AC-19, binary):
  - `CertInit_HooksOff_ExitFiveNothingCreated`.
  - `CertInit_OnLinux_CreatesKey0600Dir0700Cer0644CnSubject`.
  - `CertInit_OnLinux_KeyExists_ExitFiveUntouched`.
  - `CertInit_OnLinux_RotateThenPendingRefusedThenCommitSwaps`.
  - `CertInit_OnLinux_CommitWithoutNew_ExitFour`.
  - `CertInit_OnLinux_OpensslWrittenKeys_ReadByTokenTestPath` (a key in `BEGIN RSA PRIVATE KEY` form produced by the test with `RSA.ExportRSAPrivateKeyPem`; the store accepts it).
  - `CertInit_Stdout_NeverContainsPrivateKey`.
- `M365CommandParsingTests` (AC-6; bats "unknown verbs"):
  - `[Theory] RemovedD6Verbs_ExitFour` (`send-draft`, `move`, `delete`, `snapshot`, `get`, `sent-since`, `propose`, with and without `--approved`).
  - `[Theory] ReadVerbsHaveNoSurface_ExitFour` (`mail-folders`, `drives`, `drive-files`, `drafts-since`, `message-sender`, `item-exists`, `item-kind`, `token`).
  - `[Theory] Check_BadOption_ExitFourUsage`.
  - `TokenTest_BadAlg_ExitFour`.

**GREEN**: as Scope.

**Contract impact**: new CLI verbs `check`, `token-test`, `cert-init` (deviation 1: `token` → `token-test`). ⚠️ `cert-init` writes the key file itself; the store is read-only by spec (AC-4). It is the only code that writes a secret outside `ISecretStore` (Assumption 4).

**VERIFY**: the failing-run command passes; build/test/format green; CI green (Linux rows).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D (the application identity) *(covers Steps 7–9)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification:
  - `zyggy m365 check` output matches the shell oracle.
  - `token-test` reports a size and an expiry, and the token text appears nowhere in either output stream.
  - `cert-init` on Linux creates the key at 0600 in a 0700 folder and refuses to overwrite an existing key.
  - Every refusal of the key file (wrong mode, wrong owner, empty, no header, wrong tenant) gives the shell's message.
  - The retry tests show the waits the shell used.
- [x] Contract review:
  - The assertion header, claims and signature match `graph.sh`.
  - The token request has no secret.
  - Only the two Microsoft hosts are ever contacted, never `/me`, GET only besides the token POST, redirects not followed.
  - The exit codes are 0/3/4/5/6. `token` became `token-test`.
- [x] ⚠️ Risk review:
  - The key is read only through the secret store, never cached or logged, and its buffer is cleared.
  - `cert-init` is the one place that writes a key, outside the store, as the spec intends.
  - The source-hygiene test keeps the Microsoft hosts in one file.
  - The P/Invoke used for the owner check (Assumption 5) is reviewed.
  - No new package.
- [x] User approved — implementation may continue past this gate — 2026-10-05 owner: "approved"

---

## Step 10 — Every send, upload or move outside the instance policy is refused with the shell's exact deny line before Claude Code can prompt, a clean call produces no output, the guard never allows or asks, and each action call becomes one body-free log row with its status (fake Graph reader)

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/Guard/GuardPolicy.cs` *(create, internal sealed)*: `EvaluateAsync(JsonElement hookInput, M365Configuration, IGraphReader, CancellationToken) → GuardDecision`, where `GuardDecision = Pass | Deny(string reason) | Fail(string message)`. Port of `m365-guard.sh`:
  - Tool → action map; a non-action tool → `Pass`.
  - `actions.enabled`.
  - `expect_args` with `includeHeaders excludeResponse confirm` always allowed; the name printed only when `^[A-Za-z0-9_$-]{1,40}$`.
  - Case-insensitive body keys with the duplicate-key refusal (`JsonDocument` keeps duplicate properties; compared lower-case).
  - `send`: every rule and message. Recipients are `to ∪ cc` only (as the script); the UPN regex.
  - `upload`: the `<parent>:/<name>:` form, the name rules, extensions, base64 shape and decoded size, then `ItemKind` (must be folder), then `ItemExists` (exists → deny).
  - `move`: the three names `deleteditems archive inbox`, else an id of a non-excluded folder whose well-known name is not `recoverableitemsdeletions`/`purges`.
  - At most two Graph reads. A reader failure → `Fail("graph read failed")`.
- `src/Zyggy.Core/M365/Guard/GuardOutput.cs` *(create, internal static)*: the deny JSON, byte-exact `{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"m365-guard: refused: <reason>"}}` + `\n` (jq `-c` escaping rules: `Utf8JsonWriter` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, matching jq for `—` and quotes).
- `src/Zyggy.Core/M365/Guard/ActionLog.cs` *(create, internal sealed)*:
  - `BuildRow(JsonElement postToolUse, DateTimeOffset now) → string?` (null for another tool). The summary exactly as the jq program: subject ≤ 120, control characters → space, body length in characters, upload decoded size `len*3/4 − padding`. Status `ok` / `error: <code>` (MCP `isError`/`is_error`, or a Graph `error` object in any `text`).
  - `Append(string row)`: `FileStream(FileMode.Append, FileShare.None)` held as the exclusive lock (retry 50 × 100 ms on `IOException`), 0600 file in the 0700 state dir.
- Tests *(create)*: `tests/Zyggy.Core.Tests/M365/GuardPolicyTests.cs`, `GuardOutputTests.cs`, `ActionLogTests.cs`.

**Seams**: `IGraphReader` (NSubstitute: scripted `ItemKind`/`ItemExists`/`MailFolders`; `Received` counts ≤ 2; a throwing or failing reader). The hook inputs are the copied `tests/golden/m365/fixtures/hook-*.json`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~GuardPolicyTests|FullyQualifiedName~GuardOutputTests|FullyQualifiedName~ActionLogTests"`):
- `GuardPolicyTests` (AC-20, the full matrix; one `[Theory]` row per `hook-*.json` fixture with its expected decision and reason):
  - `send`: clean, attachment, Bcc, HTML, 4000-char body over cap, 10 vs 11 recipients, `saveToSentItems: false` / `true`, foreign `userId`, malformed address, from/sender/replyTo, headers (unknown message field), stray argument, lowercase keys, duplicate keys in different case, no content type, no recipient.
  - `upload`: clean, existing target, item-id form, foreign drive, bad name, extension, over cap, not base64, parent not a folder.
  - `move`: archive, deleteditems, inbox, a folder id, excluded id, junkemail, unknown id, recoverable, purges, other mailbox, lowercase key, no destination, malformed id.
  - `action disabled for this instance`.
  - `Evaluate_NonActionTool_PassNoRead`.
  - `Evaluate_AtMostTwoReads` (every row).
  - `Evaluate_ReaderFails_Fail`.
  - `Evaluate_NeverReturnsAllowOrAsk` (reflection: `GuardDecision` has no such case).
- `GuardOutputTests`: `Deny_BytesExact` (hand-written expected bytes for three reasons, incl. `—` and a quote).
- `ActionLogTests` (AC-22; bats "log"):
  - `[Theory] BuildRow_SendUploadMove_SummaryAsShell` (`hook-post-*.json` → hand-derived summaries).
  - `BuildRow_ErrorResult_StatusErrorCode`.
  - `BuildRow_GraphErrorInText_StatusErrorCode`.
  - `BuildRow_OtherTool_Null`.
  - `BuildRow_NeverContainsBodyOrContent`.
  - `Append_OnLinux_File0600`.
  - `Append_TwoWriters_RowsNeverInterleave` (two tasks, 200 rows each, each line parses).

**GREEN**: as Scope.

**Contract impact**: ⚠️ Security control (D7, §8 Injection): the guard's decision table and wording must not soften. The row shape of `actions.jsonl` is a shared contract (runbook reconciliation).

**VERIFY**: the failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 11 — `zyggy m365 guard` and `zyggy m365 log` work as Claude Code hooks: the binary refuses an out-of-policy send with the deny line, stays silent for a clean one, fails closed with exit 2 and one stderr line on any problem of its own, answers a non-action tool quickly, and appends one row per action even with two writers at once

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/GuardVerb.cs`, `LogVerb.cs` *(create, internal sealed)*:
  - Any argument → 2.
  - Stdin read fully (`VerbIo.In`). Not hook JSON → 2 `m365-guard: hook input is not a PreToolUse object with tool_name and tool_input`.
  - A non-action tool → 0 before any configuration is loaded.
  - Then config (any error → 2 `m365-guard: <message>`) → policy → output. `Fail` → 2 `m365-guard: graph.sh <verb> failed`, with "graph.sh" replaced by the read name.
  - Every unexpected exception → 2 `m365-guard: internal error`.
  - Runs whatever `ZYGGY_HOOKS` says.
  - The log maps the same way with prefix `m365-log:`.
  - Neither builds a service collection before it knows it needs Graph.
- `src/Zyggy.Core/M365/M365VerbHost.cs` *(modify)*: registers `guard`, `log`.
- `tests/Zyggy.Integration/M365/GuardCommandTests.cs`, `LogCommandTests.cs` *(create)*.

**Seams**: wires the real binary for every path that needs no Graph read (the whole send matrix, non-action tools, fail-closed paths, the log). Upload and move go in-process through `M365VerbHost` with the stubbed Graph handler.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~GuardCommandTests|FullyQualifiedName~LogCommandTests"`):
- `GuardCommandTests` (AC-20 I, AC-21, AC-7):
  - binary: `[Theory] Guard_SendFixture_DecisionAsShell` (every send row; deny → stdout bytes exact + exit 0; clean → no stdout, exit 0); `Guard_SendClean_HooksOffAndUnset_SameDecision`.
  - binary: `Guard_NonActionTool_ExitZeroNoOutput`.
  - binary: `[Theory] Guard_FailClosed_ExitTwoOneStderrLineNoStdout` (not JSON, no `tool_input`, `m365.json` missing, `ZYGGY_TENANT` unset).
  - binary: `Guard_AnyArgument_ExitTwo`.
  - binary: `Guard_OnLinux_NonActionTool_FinishesUnderTwoSeconds` (hook latency).
  - in-process: `[Theory] Guard_UploadAndMoveFixtures_DecisionAsShell`; `[Theory] Guard_GraphReadFails_ExitTwo` (500 and 403 from the stub).
  - in-process: `Guard_NoOutputEverContainsTokenBodyOrContent` (all rows; the stub's token and every fixture body and content string absent from stdout and stderr).
- `LogCommandTests` (AC-22 I):
  - binary: `Log_SendUploadMove_OneRowEachShape` (rows parsed: `ts`, `session_id`, `tool`, `summary`, `status`).
  - binary: `Log_OtherTool_NoRow`.
  - binary: `Log_OnLinux_File0600StateDir0700`.
  - binary: `Log_StateDirUnwritable_ExitTwoOneLine`.
  - binary: `Log_TwoProcessesAtOnce_RowsIntact` (two `zyggy m365 log` children started together, 50 calls each).

**GREEN**: as Scope; fixes only inside `M365/`.

**Contract impact**: new verbs `m365 guard`, `m365 log` (the launchers come in Step 19). ⚠️ Fail-closed property (AC-21) proven at binary level.

**VERIFY**: the failing-run command passes; build/test/format green; CI green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 12 — The Drafts the brief left in the window are audited exactly as `verify.sh` audits them: kinds, allowed recipients, URL, e-mail and secret flags, one brief, the Draft cap, and a receipt without body text; a Graph failure writes no receipt

- [x] Done

**Scope**:
- `src/Zyggy.Core/M365/Audit/DraftAudit.cs` *(create, internal sealed)*: `AuditAsync(DateOnly date, DateTimeOffset windowStart, CancellationToken) → AuditOutcome(int Exit, string StdoutLine, string? Error)`. Port of `verify.sh`:
  - Drafts since the window; the replied ids for `<date>` from `M365State`.
  - Senders per id; a 404 is skipped (allows no recipient), any other failure → 6.
  - Records in Graph's order. Kind by subject (`Zyggy — morning brief` prefix, `^re:` case-insensitive). Subject one line ≤ 80.
  - Recipients `to ∪ cc ∪ bcc` lower-cased and unique. Allowed recipients and reasons as the jq program.
  - Text above the quote separator for replies. URL, e-mail (not for the brief) and secret flags.
  - Brief count and the `reply_cap + 1` cap. Reasons in Draft order.
- `src/Zyggy.Core/M365/Audit/AuditReceipt.cs` *(create, internal)*: `brief-<date>.json`, 0600, written as `.tmp` + rename, field order `date, window_start, drafts[{id, kind, subject, recipients}], replied_ids, audit, reasons`, formatted like `jq -n` output (2-space indentation, `\n`).
- `src/Zyggy.Core/M365/VerifyVerb.cs` *(create)*: `verify <YYYY-MM-DD> <ISO>`, usage 4, config, `audit ok` (0) / `audit FLAGGED: <reasons joined by "; ">` (5), Graph failure → 6 and no receipt.
- `src/Zyggy.Core/M365/M365VerbHost.cs` *(modify)*: registers `verify`.
- Tests *(create)*: `tests/Zyggy.Core.Tests/M365/DraftAuditTests.cs`, `tests/Zyggy.Integration/M365/VerifyCommandTests.cs`.

**Seams**: `IGraphReader` substitute (unit); the stubbed handler over `drafts-ok.json`, `drafts-flagged.json`, `message-m1.json`, `message-m2.json` (in-process); the binary for usage and configuration.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DraftAuditTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~VerifyCommandTests"`):
- `DraftAuditTests` (AC-23; the four `verify:` bats cases):
  - `Audit_DraftsOkRepliedM1_OkReceiptByteEqualsExpected` (`m365-receipt-ok.json`).
  - `Audit_DraftsFlagged_FlaggedReasonsInDraftOrder` (hand-written expected line from the bats case).
  - `[Theory] Audit_DraftCases` (an outsider on another Draft or a reply, `replyTo` honoured, a reply without a recorded replied message, a reply in another conversation, text below the quote separator ignored, two briefs, no brief, over the cap).
  - `Audit_ReceiptHasNoBodyText`.
  - `Audit_Graph403_ExitSixNoReceipt`.
  - `Audit_RepliedIdNotFound_Skipped`.
  - `Audit_ReadsOnly` (reader substitute: no non-read call exists on the interface).
- `VerifyCommandTests`:
  - in-process: `Verify_Ok_ExitZeroAuditOkReceipt0600OnLinux`.
  - in-process: `Verify_Flagged_ExitFive`.
  - binary: `[Theory] Verify_BadArguments_ExitFour`.
  - binary: `Verify_ConfigMissing_ExitThree`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ Security control (§8 Injection, post-run audit). The receipt shape is a shared contract (23 evidence).

**VERIFY**: both failing-run commands pass; build/test/format green; CI green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice E (guard, action log, Draft audit) *(covers Steps 10–12)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification:
  - Every case of the shell guard's table (send, upload, move, action disabled) gets the same answer from the binary: refused with the same sentence, or silent.
  - Any problem of the guard's own ends in exit 2 with one line, never in silence.
  - A send with a hidden recipient (Bcc) is refused.
  - Two log writers at once never mix their rows.
  - The Draft audit flags the same Drafts as the shell and writes the same receipt.
- [x] Contract review:
  - The deny line is byte-exact; the guard never allows and never asks.
  - It makes at most two Graph reads, and reads only.
  - The log row and the receipt hold no mail body and no file content.
  - The exit codes are 0/2 for the hooks and 0/3/4/5/6 for `verify`.
- [x] ⚠️ Risk review:
  - These are the controls that keep the model from sending or moving mail outside the policy. Nothing was softened.
  - Fail-closed is proven on the real binary.
  - No token, body or content appears in any output (spy test).
- [x] User approved — implementation may continue past this gate — 2026-10-05 owner: "approved"

---

## Step 13 — A process can be started with exactly the variables given and nothing inherited; the tool lists come from the template's data files and give today's allow and deny lists with each script rule replaced by its verb; the server launch is planned with exactly its argv and ten variables; the headers helper retries once inside 8 seconds and never lets the token out except as its one stdout line (fakes)

- [x] Done

**Scope**:
- `src/Zyggy.Core/Processes/ProcessSpec.cs` *(modify, additive)*: `public bool ReplaceEnvironment { get; init; }` (default `false`: inherit + additions, unchanged). When `true`, the child's environment is exactly `Environment` (`null` values ignored).
- `src/Zyggy.Core/Processes/ProcessRunner.cs` *(modify, additive)*: `info.Environment.Clear()` before applying the additions when `ReplaceEnvironment`.
- `src/Zyggy.Core/M365/Tools/M365ToolPartition.cs` *(create, internal sealed)*: `Load(string checkout) → M365ToolPartitionLoad`. It reads `<checkout>/.claude/skills/m365/tools/{enabled,excluded,actions,auth}.txt` + `server-version.txt`.
  - Checks: one name per line, LF, sorted ordinal, unique, `enabled ∩ excluded = ∅`, `actions ⊆ enabled ∪ excluded`, `auth ⊆ excluded`; violation → exit 3.
  - `EnabledToolsRegex` = `^(<enabled joined by |>)$`.
  - `BriefAllow/BriefDeny`, `MailBackfillAllow/Deny`, `FilesBackfillAllow/Deny` built by the `m365-lib.sh` rules. The script rules are replaced by the verb rules: `Bash(zyggy m365 state *)`, `Bash(zyggy m365 facts *)`, `Bash(zyggy m365 parse *)`. The common deny's `Bash(.claude/skills/m365/graph.sh *)` becomes the seven `Bash(zyggy m365 auth-header*)`, `…token-test*`, `…cert-init*`, `…mcp-server*`, `…brief*`, `…mail-backfill*`, `…files-backfill*` (AC-37 successors). The rest of the common deny is unchanged.
- `src/Zyggy.Core/M365/Mcp/McpServerLaunch.cs` *(create, internal sealed)*: `Plan(M365Environment, M365Configuration, M365ToolPartition) → LaunchPlan(string ServerPath, IReadOnlyList<string> Argv, IReadOnlyDictionary<string,string> Environment) | Refusal(exit, message)`.
  - Server resolution: `PATH`, then `$HOME/.local/bin`. The real path must be under `realpath($HOME)/.local/` and executable, else 3 with the shell texts and `runbook 13 "Install or upgrade the MCP server"`.
  - Argv exactly `--org-mode --http 127.0.0.1:<port> --http-local-file-tools --no-dynamic-registration`.
  - Exactly the ten variables: `PATH=/usr/bin:/bin:$HOME/.local/bin`, `HOME`, `LC_ALL=C`, `NODE_OPTIONS=--max-old-space-size=512`, `MS365_MCP_CLIENT_ID`, `MS365_MCP_TENANT_ID`, `MS365_MCP_ORG_MODE=1`, `MS365_MCP_USE_KEYTAR=0`, `MS365_MCP_TOKEN_CACHE_PATH=<state dir>/never-written.json`, `ENABLED_TOOLS`.
  - Download root ensured 0700, not a symlink, writable, else 3.
  - Never touches the key or the token client.
- `src/Zyggy.Core/M365/Mcp/HeaderHelper.cs` *(create, internal sealed)*: `RunAsync(CancellationToken) → HeaderOutcome(int Exit, string? StdoutLine, string? StderrLine)`.
  - Deadline 8 s through `TimeProvider`; two attempts, no retry for exit 3/4.
  - Success → exactly `{"Authorization":"Bearer <token>"}` (token checked against `^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$`, else 6 `… printed no token`).
  - Failure → stderr exactly `m365: token refresh failed — runbook 13 "Certificate rejected"`. A deadline overrun → exit 6 (deviation 5).
  - Journal through `IProcessRunner`: `logger -t zyggy-m365 -- "token minted"` / `-- "token refresh failed: <reason>"`. A missing `logger` or a non-zero exit is ignored. The token is never an argument (spy).
- Tests *(create)*: `tests/Zyggy.Core.Tests/Processes/ProcessSpecReplaceEnvironmentTests.cs` (on the `ProcessStartInfo` built by an internal `ProcessRunner.StartInfo` made `internal static` — additive visibility change only), `tests/Zyggy.Core.Tests/M365/M365ToolPartitionTests.cs`, `McpServerLaunchTests.cs`, `HeaderHelperTests.cs`; `tests/golden/m365/run-lists/{brief,mail-backfill,files-backfill}-{allow,deny}.txt` *(create, hand-derived from `m365-lib.sh` with the replacements above, one rule per line)*.

**Seams**: `IProcessRunner` (`RecordingProcessRunner` for `logger`), `GraphTokenClient` behind an internal `ITokenSource` (substitute) for the helper, `TimeProvider` (`FakeTimeProvider` advanced by the substitute to simulate slow mints).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ProcessSpecReplaceEnvironmentTests|FullyQualifiedName~M365ToolPartitionTests|FullyQualifiedName~McpServerLaunchTests|FullyQualifiedName~HeaderHelperTests"`):
- `ProcessSpecReplaceEnvironmentTests` (AC-3 U): `StartInfo_Replace_ExactlyGivenVariables`, `StartInfo_Default_InheritsPlusAdditions` (the 28 behaviour).
- `M365ToolPartitionTests` (AC-27; the four partition bats cases):
  - `Load_Golden_EnabledUnionExcludedEqualsServerList` (`tools-0.157.2.txt`).
  - `Load_Disjoint_SortedUnique`.
  - `Load_ActionsSubsetAndAuthExcluded`.
  - `[Theory] RunLists_EqualHandDerivedGolden` (six lists).
  - `RunLists_ActionToolsAlwaysDenied`.
  - `EnabledToolsRegex_AnchoredEqualsShell` (the `ZY_M365_ENABLED_TOOLS` literal copied by hand).
  - `[Theory] Load_BrokenFile_ExitThree` (unsorted, duplicate, overlap, missing file).
  - `Binary_CarriesNoToolList` (no embedded resource or string array of tool names in `Zyggy.Core`: reflection over resources + a scan of `src/` for `send-shared-mailbox-mail` outside `Guard/`).
- `McpServerLaunchTests` (AC-25 U):
  - `Plan_ArgvExact`.
  - `Plan_EnvironmentExactlyTenNamesNoToken`.
  - `Plan_PortFromEnvDefault47365`.
  - `Plan_ServerOutsideHomeLocal_ExitThree` (also through a symlink).
  - `Plan_ServerNotFound_ExitThreeInstallHint`.
  - `Plan_DownloadRootCreated0700OnLinux`.
  - `Plan_DownloadRootSymlink_ExitThree`.
  - `Plan_NeverReadsKey` (store substitute: no call).
- `HeaderHelperTests` (AC-24 U):
  - `Run_Success_OneJsonLineStderrEmptyJournalMinted`.
  - `Run_FirstFailsSecondOk_OneRetry`.
  - `Run_InvalidClientTwice_ExitSixStderrExactJournalReason`.
  - `Run_ConfigurationError_NoRetry`.
  - `Run_SlowMint_StopsAt8Seconds`.
  - `Run_LoggerMissing_StillSucceeds`.
  - `Run_TokenNeverInLoggerArgumentsOrStderr`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ Shared contract `ProcessSpec` (28, 06, 34): additive optional member; 28's tests unchanged. Owner decision 8: the tool partition is template data; the binary has no copy.

**VERIFY**: the failing-run command passes; build/test/format green; 28's `ProcessRunnerTests` unchanged and green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 14 — On Linux `zyggy m365 mcp-server` becomes the (fake) server with exactly its argv and ten variables and no token, SIGTERM reaches it and its exit code is returned; `--probe` checks a running (fake) server's 401 and its tool list against the allowlist; `auth-header` prints one line or fails with the one stderr line

- [x] Done

**Scope**:
- `src/Zyggy.Core/Processes/IProcessReplacer.cs` *(create, internal interface)*, `PosixProcessReplacer.cs` *(create, internal sealed)*: `Exec(string path, IReadOnlyList<string> argv, IReadOnlyDictionary<string,string> environment)` through `execve` (`[LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]`). Returns only on failure (→ exit 3 `cannot start <path>: <errno text>`). Windows → 3 `not supported on this platform`. It is the one process start outside `IProcessRunner` (spec Contracts). `src/Zyggy.Core/Zyggy.Core.csproj` *(modify: `AllowUnsafeBlocks` only if the generator requires it; Assumption 6)*.
- `src/Zyggy.Core/M365/Mcp/McpProbe.cs` *(create, internal sealed)*: against `http://127.0.0.1:<port>/mcp` through its own `HttpClient` (keyed handler `"m365-loopback"`, real in tests; loopback only, asserted).
  - An unauthenticated `initialize` must answer 401, else 6 `an unauthenticated request got <s>, not 401 — runbook 13 "MCP server down"`.
  - With a header minted in-process (`HeaderHelper`, never argv or a file): `initialize` → 200, `tools/list` → 200. The names (sorted unique) must equal the enabled list (extra/missing → 6, up to five named).
  - Prints `tools: <n>`, the names, `listen: 127.0.0.1:<port>`, `env: <names>` (from `/proc/*/environ` of the process whose cmdline holds ` --http 127.0.0.1:<port> `, owned by this user; else `unknown`).
- `src/Zyggy.Core/M365/McpServerVerb.cs`, `AuthHeaderVerb.cs` *(create)*: `mcp-server [--probe]` (usage 4), `auth-header` (any argument → 4 `takes no argument (usage: zyggy m365 auth-header)`). `ZYGGY_HOOKS=off` accepted.
- `src/Zyggy.Core/M365/M365VerbHost.cs` *(modify)*: registers both.
- `tests/Zyggy.Integration/Infrastructure/FakeMcpServer.cs` *(create)*: a `TcpListener` HTTP/1.1 responder: 401 + `WWW-Authenticate` without `Authorization: Bearer`; JSON-RPC answers for `initialize` and `tools/list` from a configured name list. It records whether a bearer was present (never its value).
- `tests/Zyggy.Integration/Processes/ProcessRunnerReplaceEnvironmentTests.cs`, `tests/Zyggy.Integration/M365/McpServerCommandTests.cs`, `McpProbeTests.cs`, `AuthHeaderCommandTests.cs` *(create)*.

**Seams**: wires the real `ProcessRunner` (replace environment), `execve`, the loopback HTTP, the real binary.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~ProcessRunnerReplaceEnvironmentTests|FullyQualifiedName~McpServerCommandTests|FullyQualifiedName~McpProbeTests|FullyQualifiedName~AuthHeaderCommandTests"`):
- `ProcessRunnerReplaceEnvironmentTests` (AC-3 I): `RunAsync_OnLinux_ReplaceEnvironment_ChildSeesExactlyGiven` (`/usr/bin/env` as the probe child; output lines = the given variables), `RunAsync_Default_ChildInheritsParent`.
- `McpServerCommandTests` (AC-25 I, binary, `_OnLinux` unless noted):
  - `McpServer_ExecsServerWithExactArgvEnvNamesAndNoToken` (the fake script logs `$@`, the variable names and `token=absent|present` to a file).
  - `McpServer_SigtermReachesServer_ExitCodeIsServers` (the fake traps TERM and exits 42; the test checks `/proc/<pid>/cmdline` is the fake, sends SIGTERM to the zyggy pid, expects 42).
  - `McpServer_ServerOutsideHomeLocal_ExitThree`.
  - `McpServer_ConfigInvalid_ExitThreeServerNotStarted`.
  - `McpServer_BadArgument_ExitFour` (both OSes).
  - `McpServer_OnWindows_ExitThreeNotSupported`.
  - `McpServer_HooksOff_Accepted`.
- `McpProbeTests` (AC-26, in-process with `FakeMcpServer` + stub login):
  - `Probe_401ThenToolsEqualAllowlist_PrintsToolsListenEnv`.
  - `Probe_NoServer_ExitSixDownHint`.
  - `Probe_UnauthenticatedGets200_ExitSix`.
  - `Probe_ExtraOrMissingTool_ExitSixNamesUpToFive`.
  - `Probe_OutputNeverContainsToken`.
  - `Probe_BearerOnlyOnAuthenticatedRequests`.
- `AuthHeaderCommandTests` (AC-24 I):
  - in-process: `AuthHeader_Success_ExactlyOneJsonLineStderrEmpty`; `AuthHeader_InvalidClientTwice_StdoutEmptyStderrExactExitSix`.
  - binary: `AuthHeader_EnvWithoutKeyVariables_UsesDefaultKeyPath` (`ZYGGY_M365_KEY_FILE` unset, `HOME` temp, no key → exit 3 and the stderr line exact; proves the default path is used and that a configuration failure is not retried); `AuthHeader_Argument_ExitFour`; `AuthHeader_OnLinux_FinishesUnder8Seconds` (configuration failure path, timed).

**GREEN**: as Scope.

**Contract impact**: new verbs `m365 mcp-server`, `m365 auth-header` (D8 contract unchanged). ⚠️ One process start outside `IProcessRunner` (`execve`), as the spec allows.

**VERIFY**: the failing-run command passes; build/test/format green; CI green on ubuntu (Linux rows).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice F (MCP server and per-connection token) *(covers Steps 13–14)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification:
  - On Linux the server is started with exactly the shell's arguments and ten variables, with no token among them.
  - A stop signal from systemd reaches the server, and its exit code comes back.
  - The probe confirms the 401 and the exact tool list.
  - The headers helper prints one line on success, and on failure prints nothing on stdout and one fixed sentence on stderr, within 8 seconds.
  - The tool lists come from the data files, and the three run lists equal today's lists with each script rule replaced by its verb.
- [ ] Contract review:
  - D8 is unchanged: the token goes only to Claude Code on stdout, and the server never holds one.
  - The `ProcessSpec` option is additive. The partition lives in data files only.
- [ ] ⚠️ Risk review:
  - The token never appears in an argument list, a file, stderr or the journal (spy tests).
  - The process replacement (`execve`) is the one start outside the process runner.
  - The server is only accepted from `~/.local`.
  - 28's process tests are unchanged.
- [ ] User approved — implementation may continue past this gate

---

## Step 15 — A model request can carry an explicit deny list, the result names the denied tools, and the morning brief runs its pre-flight, one model run, the result checks, the audit, one memory line and its journal line exactly as `brief.sh` (fake model, fake Graph reader)

- [ ] Done

**Scope**:
- `src/Zyggy.Core/Models/ModelRunRequest.cs` *(modify, additive)*: `public IReadOnlyList<string> DisallowedTools { get; init; } = [];`.
- `src/Zyggy.Core/Models/ClaudeArguments.cs` *(modify, additive)*: when `DisallowedTools` is non-empty, one `--disallowedTools <joined>` after `--allowedTools`. When `NoMcp` is also set, `mcp__*` is the first element of that one list (never two flags). Values are dash-checked like the others. With an empty list the output is byte-identical to 28's.
- `src/Zyggy.Core/Models/ModelRunResult.cs` *(modify, additive)*: `public IReadOnlyList<string> PermissionDenialTools { get; init; } = [];` (a non-positional init property; the positional constructor is unchanged).
- `src/Zyggy.Core/Models/StreamJsonReader.cs` *(modify, additive)*: collects `permission_denials[].tool_name`.
- `src/Zyggy.Core/Models/ClaudeCodeCliRunner.cs` *(modify, additive)*: copies them into the result.
- `src/Zyggy.Core/M365/Runs/M365RunRequest.cs` *(create, internal static)*: `For(M365RunKind kind, string prompt, M365Environment, M365Configuration, M365ToolPartition, string? runDir) → ModelRunRequest`.
  - Prompt on stdin (28's rule, decision 7 default); working directory = the checkout.
  - `AllowedTools`/`DisallowedTools` from the partition. `MaxTurns`, `MaxBudgetUsd`, `Model` (null when `""`) from the kind's block.
  - `Environment = { ZYGGY_HOOKS = off [, ZYGGY_M365_RUN_DIR = <run dir>] }`; `Isolation = None`; `Timeout = 120 min`; `MaxCaptureBytes = 64 MiB` (Assumption 7).
- `src/Zyggy.Core/M365/Runs/BinaryPin.cs` *(create, internal static)*: the dream's rule for m365 model-run verbs. `ZYGGY_INSTANCE_DIR` set → `VersionPin.Check` (28, reused unchanged) on `instance/zyggy.json` with the SHA-256 of `Environment.ProcessPath` → mismatch → 3 `configuration error: version_mismatch: <detail>` before any request (AC-34).
- `src/Zyggy.Core/M365/Runs/BriefRun.cs` *(create, internal sealed)*: `RunAsync(CancellationToken) → RunOutcome(int Exit, IReadOnlyList<string> StdoutLines, IReadOnlyList<string> StderrLines)`. Order of `brief.sh`:
  1. config → `claude` resolvable (`ZYGGY_CLAUDE_PATH` or `PATH`; missing → 3 `claude not found`) → pin;
  2. token (fails fast; stderr `key: <source>`);
  3. receipt or a brief Draft of today → `brief <date>: already created` (exit 0);
  4. Inbox id, drive ids;
  5. run dir `zyggy-m365-brief-<date>.<random>` 0700 under the download root;
  6. the model run with prompt `/morning-brief <mailbox> <inbox-id> <drive ids…> <run-dir>`;
  7. run dir removed;
  8. result checks (failed or no result → 6 `claude run failed (<detail>) — runbook 13 "Model run failed"`; malformed; over turns or budget → 6 `claude run over the cap (…)`), each recorded in `brief.jsonl`;
  9. counts line (the last matching line of the result text) and denial tool names;
  10. `DraftAudit` in-process (Step 12);
  11. `RememberService` memory line `Morning brief <date> left as a Draft: <summary>, audit ok|FLAGGED` with `--tag observed --source "m365-brief <date>"`, also under `ZYGGY_HOOKS=off`; a refusal is one stderr line, never fatal;
  12. `brief.jsonl` row 0600 (field order of the jq program);
  13. last stdout line `brief <date>: …, exit <code>`.
  Cancellation → run dir removed, no receipt, `RunOutcome.Interrupted(143|130)`.
- `src/Zyggy.Core/M365/BriefVerb.cs` *(create)*: no argument (else 4); registered in `M365VerbHost`.
- Tests *(create/modify)*: `tests/Zyggy.Core.Tests/Models/ClaudeArgumentsDisallowedTests.cs` *(create; 28's `ClaudeArgumentsTests` untouched)*, `tests/Zyggy.Core.Tests/Models/StreamJsonReaderDenialsTests.cs` *(create)*, `tests/Zyggy.Core.Tests/M365/M365RunRequestTests.cs`, `BinaryPinTests.cs`, `BriefRunTests.cs` *(create)*.

**Seams**: `IModelRunner` (NSubstitute: captures the request, returns scripted results built from `claude-result-ok.json`, `-denials`, `-over-budget`, `-error`), `IGraphReader` substitute, `ITokenSource` substitute, `TimeProvider`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ClaudeArgumentsDisallowedTests|FullyQualifiedName~StreamJsonReaderDenialsTests|FullyQualifiedName~M365RunRequestTests|FullyQualifiedName~BinaryPinTests|FullyQualifiedName~BriefRunTests"`):
- `ClaudeArgumentsDisallowedTests`:
  - `Build_DisallowedTools_OneFlagAfterAllowed`.
  - `Build_DisallowedWithNoMcp_SingleFlagMcpStarFirst`.
  - `Build_EmptyDisallowed_ByteIdenticalTo28` (the 28 hand-written vectors re-asserted).
  - `Build_DisallowedValueStartingWithDash_Throws`.
- `StreamJsonReaderDenialsTests`: `Accept_PermissionDenials_ToolNamesCollectedAndCountUnchanged`.
- `M365RunRequestTests`:
  - `[Theory] For_Kind_ListsTurnsBudgetModelFromConfig` (brief, mail-backfill, files-backfill; the lists equal the Step 13 goldens).
  - `For_PromptOnStdinNeverInArguments`.
  - `For_EnvironmentHooksOffAndRunDirOnly`.
  - `For_WorkingDirectoryIsCheckout`.
  - `For_NoCredentialVariable`.
- `BinaryPinTests`: `Check_NoInstanceDir_Skipped`, `Check_Mismatch_ExitThreeVersionMismatch`, `Check_Match_Proceeds`.
- `BriefRunTests` (AC-30; the eight `brief:` bats cases at unit level):
  - `Run_HappyPath_LastLineByteEqualsJournalOk` (`expected/m365-journal-ok.txt`).
  - `Run_Request_PromptAndListsExact` (hand-written expected prompt and lists).
  - `Run_SecondRunSameDate_AlreadyCreatedNoModelCall` (receipt present; and a brief Draft of today present).
  - `Run_InvalidClient_ExitSixBeforeModelNoRunDir`.
  - `Run_IsError_ExitSixNoReceiptNoMemoryLineRunDirRemoved`.
  - `Run_OverBudget_ExitSixCapMessage`.
  - `Run_Denials_ToolNamesInLineAndRow`.
  - `Run_AuditFlagged_ExitFiveLineMatchesJournalFlagged` (`expected/m365-journal-flagged.txt`).
  - `Run_HooksOff_StillWritesMemoryLine`.
  - `Run_Cancelled_RunDirRemovedNoReceiptInterrupted143`.
  - `Run_PinMismatch_ExitThreeBeforeToken`.
  - `Run_ClaudeMissing_ExitThreeBeforeToken`.
  - `Run_MemoryLineByteIdenticalToShellLine` (AC-10).

**GREEN**: as Scope.

**Contract impact**: ⚠️ §9 seam `IModelRunner` (28, 06): two additive members. The 28 vectors are re-asserted unchanged. The brief's files (`brief.jsonl`, receipt, inbox line) are unchanged contracts.

**VERIFY**: the failing-run command passes; build/test/format green; 28's `ClaudeArgumentsTests`, `StreamJsonReaderTests`, `ClaudeCodeCliRunnerTests` unchanged and green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 16 — Both backfills loop, cap, checkpoint and resume exactly as the scripts do, read the checkpoint and watermark files the shell left on Central and continue where it stopped, and refuse to run unattended (fake model, fake Graph reader)

- [ ] Done

**Scope**:
- `src/Zyggy.Core/M365/Runs/MailBackfillCheckpoint.cs`, `FilesBackfillCheckpoint.cs` *(create, internal)*: read with `JsonDocument` (unknown fields kept and written back), totals and per-folder/per-drive fields as the jq programs. Written 0600 as `.tmp` + rename. Not an object / no `folders` (`drives`) object → 3 `configuration error: <path> is not a checkpoint (zyggy m365 mail-backfill --reset starts again)`.
- `src/Zyggy.Core/M365/Runs/MailBackfill.cs` *(create, internal sealed)*: `mail-backfill.sh` in order:
  1. hooks-off → 5 before anything;
  2. args;
  3. principal from settings;
  4. config, claude, pin;
  5. token;
  6. folders minus exclusions (`--folder` by id, display name or well-known name; excluded or unknown → 4);
  7. `--reset`;
  8. checkpoint;
  9. per folder, newest first from its watermark until a batch lists 0: cap check before every batch (budget / facts / messages; 0 = no cap) → prompt `/mail-backfill <mailbox> <folder-id> <watermark> <batch>` → result checks (a failed batch adds its turns and cost, then 6) → counts line → new watermark from `M365State` → checkpoint add → progress lines;
  10. a watermark that did not move stops the folder (exit 5 at the end);
  11. the final counts line byte-equal to the script's format.
  Cancellation → no write, `Interrupted(130|143)`.
- `src/Zyggy.Core/M365/Runs/FilesBackfill.cs` *(create, internal sealed)*: `files-backfill.sh` in order:
  - drive pre-check (`RawGet /drives/<id>/root`, 403/404 → skipped, counted forbidden);
  - `DriveFilesAsync` once;
  - the walk strictly after the cursor `<ISO>|<id>` (a plain ISO resumes at its own second);
  - skip classes type / size / path (`^/[^,[:cntrl:]]{0,199}$`, the fence tag, `drives.exclude_paths`) without a model run;
  - a fresh run dir per batch;
  - prompt `/files-backfill <drive> <run-dir> <n>` + `\n<zyggy-m365-data>\n<lines>\n</zyggy-m365-data>` exactly as the script builds it;
  - the cursor moves only when the counts line confirms n files;
  - run dir removed after each batch and on cancellation;
  - caps; the counts line.
- `src/Zyggy.Core/M365/MailBackfillVerb.cs`, `FilesBackfillVerb.cs` *(create)*; registered in `M365VerbHost`.
- `tests/Zyggy.Core.Tests/M365/Fixtures/checkpoints/{mail-backfill,files-backfill}.json` *(create)*: shell-shaped checkpoints in the exact jq output form, one with a finished folder and one unfinished folder with `batches > 0`.
- Tests *(create)*: `tests/Zyggy.Core.Tests/M365/MailBackfillTests.cs`, `FilesBackfillTests.cs`, `CheckpointCompatibilityTests.cs`.

**Seams**: `IModelRunner` substitute (a scripted sequence; a callback that writes the new watermark through `M365State`, as the model does with `zyggy m365 state set`), `IGraphReader` substitute, `ITokenSource`, `TimeProvider`.

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MailBackfillTests|FullyQualifiedName~FilesBackfillTests|FullyQualifiedName~CheckpointCompatibilityTests"`):
- `MailBackfillTests` (AC-31; the eight `mail-backfill:` bats cases):
  - `Run_HooksOff_ExitFiveBeforeArguments`.
  - `Run_FoldersMinusExclusions_NewestFirstUntilZero`.
  - `Run_Request_PromptListsCapsExact`.
  - `Run_Cancelled_DuringBatch2_CheckpointOfBatch1Stands_Interrupted130`.
  - `[Theory] Run_Cap_ExitFiveCheckpointIntactCountsLine` (budget, facts, messages).
  - `Run_FolderOption_OnlyThatFolder`.
  - `[Theory] Run_FolderExcludedOrUnknown_ExitFour`.
  - `Run_IsError_ExitSixCostCountedFolderNotAdvanced`.
  - `Run_WatermarkNotAdvanced_StopsFolderExitFiveAtEnd`.
  - `Run_Reset_ClearsCheckpointAndWatermarks`.
  - `Run_PrincipalFromSettings_WhenUnset`.
  - `Run_CountsLineByteEqualsShellFormat`.
- `FilesBackfillTests` (AC-32; the nine `files-backfill:` bats cases):
  - `Run_HooksOff_ExitFive`.
  - `Run_PromptWithFenceExact`.
  - `Run_CursorStrictlyAfterIsoPipeId_TieGroupRest`.
  - `Run_PlainIsoCursor_ResumesAtItsSecond`.
  - `Run_DrivePrecheck403_SkippedCountedForbidden`.
  - `Run_SkipClasses_NeverReachModel`.
  - `Run_Cancelled_RunDirRemovedCursorUnmoved_Interrupted143`.
  - `[Theory] Run_Cap_ExitFive`.
  - `Run_ExcludeDrivesAndDriveOption`.
  - `Run_IsError_ExitSixCursorUnmoved`.
  - `Run_UnconfirmedBatch_StopsDriveExitFive`.
- `CheckpointCompatibilityTests` (AC-33): `Mail_ShellCheckpoint_FinishedSkippedUnfinishedResumesPrintsResuming`, `Files_ShellCheckpointAndPlainIsoWatermark_Resumes`, `[Theory] NotACheckpoint_ExitThreeNamesReset`, `RoundTrip_UnknownFieldsKept`.

**GREEN**: as Scope.

**Contract impact**: ⚠️ The checkpoint and watermark files on Central are shared contracts. They are read as they are, and only the fields the scripts wrote are changed.

**VERIFY**: the failing-run command passes; build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 17 — The brief and both backfills run end to end through the real process runner and `tools/fake-claude`: the captured arguments carry the deny lists and the prompt arrives on stdin, a cancelled run leaves no model process and no run directory and exits 143/130, and from the binary the backfills refuse an unattended run and every model-run verb refuses a wrong pin before anything else

- [ ] Done

**Scope**:
- `tools/fake-claude/scenarios/m365-brief-ok.jsonl`, `m365-brief-denials.jsonl`, `m365-mail-batch.jsonl`, `m365-mail-empty.jsonl`, `m365-files-batch.jsonl` *(create, hand-written)*: `system/init`, then a `result` whose `result` text ends with the counts line for the fixed test date (in-process tests use `FakeTimeProvider`). The denials scenario lists `permission_denials[].tool_name`. `tools/fake-claude/README.md` *(modify: one row per scenario)*.
- `src/Zyggy.Core/Processes/SignalCancellation.cs` *(create, internal sealed, additive)*: `PosixSignalRegistration` for SIGTERM/SIGINT → cancels a token and records 143/130 (`context.Cancel = true`, so the verb finishes its cleanup).
- `src/Zyggy.Core/M365/Runs/*` *(modify only as the tests demand)*.
- `tests/Zyggy.Integration/Infrastructure/ActingModelRunner.cs` *(create)*: an `IModelRunner` decorator over the **real** `ClaudeCodeCliRunner` → fake-claude. After the real run it performs the side effects the bats `*-actions.sh` stubs performed: it sets the backfill watermark, records a replied id, writes facts through `FactsVerb`. Never in `src/`.
- `tests/Zyggy.Integration/M365/BriefEndToEndTests.cs`, `MailBackfillEndToEndTests.cs`, `FilesBackfillEndToEndTests.cs`, `ModelRunCommandTests.cs` *(create)*.

**Seams**: wires `IProcessRunner` (real) + `IModelRunner` (real `ClaudeCodeCliRunner` → fake-claude, wrapped by `ActingModelRunner`), real files, real run dirs; Graph is the stub.

**RED** (`dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BriefEndToEndTests|FullyQualifiedName~MailBackfillEndToEndTests|FullyQualifiedName~FilesBackfillEndToEndTests|FullyQualifiedName~ModelRunCommandTests"`):
- `BriefEndToEndTests` (AC-30 I, in-process):
  - `Brief_HappyPath_JournalLineReceiptBriefJsonlMemoryLine`.
  - `Brief_CapturedArgumentsEqualHandWrittenVectorPromptOnStdin` (capture decoded: `--disallowedTools` list, `--allowedTools`, no prompt in argv; stdin capture = `/morning-brief …`; cwd = checkout).
  - `Brief_Denials_InLine`.
  - `Brief_FakeError_ExitSixNoReceipt`.
  - `Brief_CancelledWhileFakeRuns_NoProcessLeftRunDirRemovedExit143` (`ZYGGY_FAKE_CLAUDE_DELAY_MS=30000`, cancel after the stdin capture appears; `ProcessProbe`).
- `MailBackfillEndToEndTests` (AC-31 I, AC-33 I, in-process):
  - `MailBackfill_ShellCheckpoint_PrintsResumingThenDone`.
  - `MailBackfill_CancelledDuringBatch_Exit130CheckpointIntactNextRunResumes`.
  - `MailBackfill_CapturedArguments_MailListsPromptOnStdin`.
- `FilesBackfillEndToEndTests` (AC-32 I, in-process): `FilesBackfill_Batch_CursorMovedRunDirRemoved`, `FilesBackfill_Cancelled_RunDirRemovedCursorUnmoved`.
- `ModelRunCommandTests` (binary):
  - `[Theory] Backfill_HooksOff_ExitFiveBeforeAnything` (both).
  - `[Theory] ModelRunVerb_PinMismatch_ExitThreeVersionMismatchNoRequest` (brief, mail-backfill, files-backfill; a wrong `zyggy.json`, no stdin capture file appears, no network — the pin check precedes the token).
  - `[Theory] ModelRunVerb_BadArgument_ExitFour`.
  - `Brief_ConfigMissing_ExitThree`.
  - `MailBackfill_PrincipalOnlyInSettingsLocalJson_PassesConfigurationThenStopsAtPin` (proves the settings fallback in the binary).

**GREEN**: as Scope; fixes only inside `M365/` and `Processes/SignalCancellation.cs`.

**Contract impact**: new verbs `m365 brief`, `m365 mail-backfill`, `m365 files-backfill` (deviations 4, 6, 8). The fake-claude contract gains scenarios only.

**VERIFY**: the failing-run command passes; build/test/format green; CI green on both runners.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice G (brief and backfills) *(covers Steps 15–17)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification:
  - A brief run through the stand-in model produces the same journal line, receipt and memory line as the shell.
  - One captured model call is shown: its deny list, and the prompt arriving on standard input.
  - A backfill picks up the shell's checkpoint and says "resuming folder …".
  - A stop in the middle of a run leaves no model process and no download folder, and the next run resumes.
  - From the binary: a wrong version pin stops all three verbs before anything is contacted.
- [ ] Contract review:
  - The three allow and deny lists equal today's, with the verb rules; the action tools are always denied.
  - The checkpoint, watermark, `brief.jsonl` and receipt files keep their shape.
  - The two additions to the model-runner contract are optional and leave the dream's argument list byte-identical.
- [ ] ⚠️ Risk review:
  - The shared model-runner contract changed additively only.
  - The run lists keep every send, upload and move denied.
  - Assumption 7 (64 MiB capture and a 2-hour run limit) is acknowledged.
  - The SIGTERM handling of these three verbs is proven in-process and will be proven live on Central (Assumption 8).
- [ ] User approved — implementation may continue past this gate

---

## Step 18 — **CONDITIONAL** — The three m365 runs pass their `/skill` prompt as the command-line argument, exactly as the scripts do, because the Central check showed that print mode does not expand a `/skill` command read from stdin; every other model run (the dream) keeps the prompt on stdin

- [x] Skipped — expanded (stdin kept, no command-line switch); checked on Central 2026-10-05, Claude Code 2.1.289 (spec Decisions log, decision 7). Gate H is presented together with Gate I.

**Precondition (read first, before anything else in this step):** open `_specs/33-central-tools-dotnet.md`, Decisions log item 7.
- **The placeholder sentence is still there (no result yet)**: STOP. Report at Gate H that the check has not run. Do not guess, and do not start Step 19.
- **The result reads "expanded (stdin kept, no command-line switch)"**: tick this step as *Skipped* with the recorded sentence, date and version. Nothing else changes; go to Gate H.
- **The result reads "not expanded (command-line fallback for the three m365 runs)"**: execute this step.

No earlier step depends on this outcome. Steps 15–17 deliver the prompt on stdin (28's rule), and this step changes only how three requests are built.

**Scope** (only when executed):
- `src/Zyggy.Core/Models/ModelRunRequest.cs` *(modify, additive)*: `public bool PromptAsArgument { get; init; }` (default `false`).
- `src/Zyggy.Core/Models/ClaudeArguments.cs` *(modify, additive)*: when `PromptAsArgument`, the vector starts `-p`, `<prompt>`; a prompt starting with `-` → `ArgumentException`. Otherwise unchanged.
- `src/Zyggy.Core/Models/ClaudeCodeCliRunner.cs` *(modify, additive)*: `StandardInput = null` when `PromptAsArgument`.
- `src/Zyggy.Core/M365/Runs/M365RunRequest.cs` *(modify)*: `PromptAsArgument = true` for the three kinds.
- Tests: `tests/Zyggy.Core.Tests/Models/ClaudeArgumentsPromptArgumentTests.cs` *(create)*; `tests/Zyggy.Core.Tests/M365/M365RunRequestTests.cs` and `tests/Zyggy.Integration/M365/{Brief,MailBackfill,FilesBackfill}EndToEndTests.cs` *(modify: the prompt assertions flip from stdin to argv; these are 33's own tests)*.

**Seams**: `IModelRunner` / `IProcessRunner` (unit), fake-claude (integration).

**RED** (`dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ClaudeArgumentsPromptArgumentTests|FullyQualifiedName~M365RunRequestTests"` and `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~EndToEndTests"`):
- `Build_PromptAsArgument_PIsFollowedByPromptStdinEmpty`.
- `Build_PromptAsArgumentDashStart_Throws`.
- `Build_DefaultRequest_PromptNeverInArguments` (28's guarantee re-asserted).
- `[Theory] For_M365Kind_PromptAsArgument`.
- The three end-to-end captures: the prompt is the argument after `-p`, and no stdin capture content.
- 28's `DreamEndToEndTests.Dream_HappyPath_CapturedArgumentsWorkingDirectoryAndStdinMatchContract` unchanged and green.

**GREEN**: as Scope.

**Contract impact**: ⚠️ Additive member on the `IModelRunner` request (decision 7 fallback). The prompt (mailbox, folder or drive ids, and for the files backfill, file paths) becomes visible in the process list to the same user, exactly as with the scripts today (spec deviation 8, "no deviation" under the fallback).

**VERIFY**: both failing-run commands pass; build/test/format green; 28's suites unchanged.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice H (prompt delivery, conditional) *(covers Step 18)*

*Executor: STOP here. Present the check result and, if Step 18 ran, its results; WAIT for user approval. If Step 18 was skipped, present this gate together with Gate I's material and let the owner approve both at once.*

- [ ] Behavioral verification: the result of the Central check, with its date and Claude Code version. Either the prompt stays on standard input with no change, or the three Microsoft 365 runs now pass it as the command-line argument while the dream still uses standard input (captured arguments shown).
- [ ] Contract review: only the three m365 runs are affected. The dream's argument list is byte-identical to before.
- [ ] ⚠️ Risk review: under the fallback, the prompt (ids and file paths) is visible to processes of the same user on Central, exactly as with the scripts today. Nothing else is exposed.
- [ ] User approved — implementation may continue past this gate

---

## Step 19 — The `zyggy-core` template keeps only thin launchers: the guard and log hooks call the binary and turn any failure into a block, settings and `.mcp.json` name the verbs, the tool lists are plain data files, the m365 and `remember` scripts and their bats suites are gone, and template CI proves the wiring against a `zyggy` stub — on the template branch, nothing merged

- [ ] Done

**Scope** (`D:\source\zyggy-core`, branch `feature/33-m365-verbs`):
- **Create:**
  - `.claude/skills/m365/tools/{enabled,excluded,actions,auth}.txt` + `server-version.txt` (byte-equal to this repository's `tests/golden/m365/tools/`).
  - `.claude/zyggy-min-version` (one line: the version Step 21 will tag, e.g. `0.2.0`).
- **Rewrite** `.claude/hooks/m365-guard.sh` and `m365-log.sh` as ≤ 10-line launchers (R1 reason in the README). If `zyggy` is not on `PATH` → stderr `m365-guard: zyggy not found` and exit 2. Else run `zyggy m365 guard` (stdin passed through); any exit other than 0 → exit 2 (the binary's own stderr line kept).
- **Delete:**
  - `.claude/skills/m365/{graph,m365-lib,files-backfill,mail-backfill,brief,facts,verify,parse,mcp-wrapper,state,mcp-server,mcp-auth-header}.sh` and `.claude/skills/remember/remember.sh`;
  - the m365/remember parts of `tests/m365.bats` (the file keeps only the launcher and data-file cases) and the whole of `tests/remember.bats`;
  - the bats-only fixtures that no remaining test uses (`tests/fixtures/m365/*-actions.sh`, `markitdown-stub.sh`, the curl stub).
- **Trim** `.claude/hooks/lib.sh` to the functions `stop.sh`, `inventory.sh` and `clone.sh` use (the GitHub scripts and their suites are untouched: AC-36).
- **`.claude/settings.json`**:
  - `permissions.ask` = the three action tools.
  - `permissions.deny` = the path rules + the seven `Bash(zyggy m365 <verb>*)` successors (AC-37) + one `mcp__m365__<name>` rule per line of `excluded.txt`.
  - The PreToolUse/PostToolUse matchers unchanged, pointing at the launchers.
  - The `remember` allow rule becomes `Bash(zyggy memory remember *)`.
- **`.mcp.json`**: `"headersHelper": "zyggy m365 auth-header"`.
- **Text:** the skills `m365`, `morning-brief`, `mail-backfill`, `files-backfill`, `remember` (`SKILL.md`), `.claude/rules/{security,operations,memory}.md`, `AGENTS.md` and `README.md` name the verbs. They gain exit-code tables per verb and the "remaining shell" table with each R1 reason. The D7/D8 wording is unchanged in substance.
- **`tests/repo.bats`** (rewritten m365/remember rows):
  - no m365 or remember script except the two launchers, each ≤ 30 lines with no `jq`/`sed`/`awk`/`case` on data;
  - the settings rules equal the data files;
  - `.mcp.json` names the verb;
  - every documented `zyggy` command is run against the stub;
  - the shared secret-sample fixture gives the same pattern names through `lib.sh` (AC-11 T).
- **`tests/launchers.bats`** *(create)*, with a stub `zyggy` on `PATH` that records argv and stdin:
  - guard and log pass stdin and exit 0 through;
  - a non-zero exit becomes 2;
  - a missing binary gives exit 2 and one line.
- **`.github/workflows/ci.yml`** (template): the stub on `PATH` for bats; shellcheck over the remaining scripts.

**Seams**: none (template data and CI).

**RED**: the new `repo.bats` and `launchers.bats` cases fail against the current template (`bats tests/repo.bats tests/launchers.bats` in WSL or the branch's CI run). In this repo, `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~M365ToolPartitionTests"` still passes against the goldens the template now carries (byte comparison done by hand: `git diff --no-index`).

**GREEN**: as Scope.

**Contract impact**: ⚠️ The template now depends on a binary from another repository (minimum-version file). The hook implementations move from shell to the binary. The GitHub scripts and their suites are unchanged.

**VERIFY**: `zyggy-core` CI on the branch green (bats, shellcheck, LF, settings parse) — run id recorded; the line counts of the two launchers recorded; `git diff --stat main` of the template lists exactly the deleted, rewritten and created files above.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 20 — The instance carries the units and settings that run the verbs and refuses a pin below the template's minimum; the runbook covers install, upgrade, rollback and every new failure mode; 0002 has a section 33 ready for evidence — on the instance branch, nothing merged, nothing on Central

- [ ] Done

**Scope**:
- `D:\source\zyggy-geoffrey` (branch `feature/33-m365-verbs`):
  - Merge the template branch.
  - `instance/systemd/zyggy-m365-mcp.service`: `ExecStart=/usr/local/bin/zyggy m365 mcp-server`, `Environment=ZYGGY_INSTANCE_DIR=…`; hardening, `LoadCredential=` and paths unchanged.
  - `instance/systemd/zyggy-morning-brief.service`: `ExecStart=/usr/local/bin/zyggy m365 brief`, plus `ZYGGY_INSTANCE_DIR` and `ZYGGY_CLAUDE_PATH`; the timer stays disabled.
  - `instance/settings.local.json`: `env` gains `ZYGGY_INSTANCE_DIR`.
  - Instance CI step: fail when `instance/zyggy.json` `version` < `.claude/zyggy-min-version` (semver compare). Its RED is the branch's own CI with the current pin `0.1.5` against minimum `0.2.0`. The pin bump that turns it green is made in Step 21, so the instance branch CI is expected red until then and is recorded as such.
- This repository:
  - `runbooks/central-claude-config.md` (AC-44):
    - Section 13 rewritten verb for verb: every `graph.sh`, `mcp-server.sh`, `mcp-auth-header.sh`, `verify.sh`, `state.sh`, `facts.sh`, `parse.sh`, `brief.sh`, `*-backfill.sh` command becomes its verb.
    - Section 14 entries "Install", "Upgrade", "Rollback", "Binary missing or wrong version" extended with the m365 consequences (guard blocks every action, `headersHelper` fails, brief and backfills exit 3/127).
    - New entries: "Template needs a newer binary", "Guard refused / failed", "Action without a log row", "Backfill resume", "Token refresh failed" (8 s), "Brief or backfill run failed" (names the decision 7 fallback).
    - Every `remember.sh` mention becomes `zyggy memory remember`.
  - `_plans/decisions/0002-central-productive.md`: a section 33 skeleton with evidence rows AC-40..AC-43, a "Tools on Central" row for the new version, and the P0b checklist row.

**Seams**: none (repositories, CI, documents).

**RED**: instance CI on the branch fails at the new minimum-version step with pin `0.1.5` (expected; recorded). The runbook has no "Template needs a newer binary" entry before this step.

**GREEN**: as Scope.

**Contract impact**: unit `ExecStart` lines and one new instance setting (spec "Template after 33"). Install order binary → pin → pull → units (Executor note 2).

**VERIFY**:
- The instance CI run id, red only at the minimum-version step.
- Runbook entries listed against the spec's Failure modes table (checklist in the step summary).
- `grep -n "graph.sh\|remember.sh\|mcp-auth-header.sh\|mcp-server.sh" runbooks/central-claude-config.md` returns only history lines marked as such.
- This repo's build/test/format green.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice I (template and instance call the verbs) *(covers Steps 19–20)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification:
  - Template CI is green on its branch: the two hook launchers block on any failure, every documented command runs against the stub, and the remaining shell is listed with its reasons.
  - The instance branch refuses an old pin, as designed.
  - The runbook has an entry for every new failure mode.
- [ ] Contract review:
  - The settings deny every excluded tool and the seven token- and run-producing verbs, and ask for the three action tools.
  - `.mcp.json` names `zyggy m365 auth-header`.
  - The units run `/usr/local/bin/zyggy m365 …`, and the brief timer stays off.
  - The GitHub scripts are untouched.
- [ ] ⚠️ Risk review:
  - Mixed versions: the template now needs a minimum binary, and the instance check enforces it.
  - Nothing is merged and nothing has reached Central yet.
- [ ] User approved — implementation may continue past this gate

---

## Step 21 — Once Gates A–I are approved, the release is installed on Central exactly like 28's (checksum, root-owned, pinned, previous version kept), then the template and instance are pulled and the server restarted once; the agent proves the binary there: `check`, a backfill that reads the shell's checkpoint, the probe, the guard refusing a hidden-recipient send, and one `remember` from a session

- [ ] Done

**Precondition (check first):** this plan's Gates A–I are approved (their boxes `[x]`). If not: STOP and report; do not merge, tag or install (rule 5). There is no wait for 28: 28 was closed on 2026-10-05 by the owner's clean-slate decision and its final gate is ticked (`ROADMAP.md` #28 Done); its remaining night checks are Step 23's C28-* items. If a dream fix is pending, it goes first (rule 1).

**Scope** (agent; `az vm run-command` as root, git and `zyggy` as `runuser -u zyggy -- …`; binary transfer per 28 Executor note 1):
1. **This repository:**
   - merge `main` into the branch;
   - CI green on the PR;
   - merge the PR into `main`;
   - tag `v0.2.0` (or the next minor; equal to `.claude/zyggy-min-version`) via `@git`;
   - tag CI green;
   - download `zyggy-linux-x64`;
   - `sha256sum -c SHA256SUMS`.
2. **Template and instance:**
   - `zyggy-core`: merge the branch into `main`, CI green.
   - `zyggy-geoffrey`: merge the branch; `instance/zyggy.json` → the new version and hash; instance CI green (the minimum-version step now passes); push.
3. **Install on Central** (runbook 14a, unchanged): `/opt/zyggy/<v>/zyggy` root:root 0755, previous version kept, symlink switched. **Then** `git -C /srv/agent/central pull --ff-only` (binary before template, AC-40).
4. **Units:**
   - `install -m 644` the two unit files, `systemctl daemon-reload`;
   - `systemctl restart zyggy-m365-mcp.service` once;
   - `systemctl restart claude-remote` once, after the server restart, so the remote session loads the pulled `.mcp.json` (`headersHelper` = `zyggy m365 auth-header`; the old `mcp-auth-header.sh` is gone after the pull) and the new hook launchers. Record the log line `resuming <session id>` (same id as before the restart): this is also the evidence for Step 23's VM-C2;
   - the brief unit and timer **not enabled**;
   - `systemctl show -p ExecStart` recorded.
5. **AC-41 checks, agent-run, recorded with output excerpts (no token, no mail content):**
   - `zyggy m365 check`.
   - `zyggy m365 mcp-server --probe` (= the allowlist).
   - `zyggy m365 mail-backfill` once: it reads the shell's checkpoint and reports `done`, or resumes. If work remains, it is interrupted once with SIGINT (exit 130) and resumed (`resuming folder …`).
   - **Refusal probe (decision 1):**
     - a `claude -p` session in the instance checkout as `zyggy` asks for a send to the owner's own address with a Bcc recipient;
     - the result's `permission_denials` names the send tool with the guard's reason `m365-guard: refused: Bcc is not allowed`;
     - `zyggy m365 check --counts` before and after shows no new Sent Items entry (a Graph read of Sent Items count by the agent through `check`, or recorded as the owner's look in Outlook if `check` cannot show it);
     - `actions.jsonl` has no new row.
   - One `remember` from a session: a `claude -p` session asks to remember a clearly labelled test fact; `inbox/remember-<date>.md` gains the line written by `zyggy memory remember`. Record the date (day N) and the line's text: Step 23's C28-AC32 follows this fact into its category file after night N+1.
6. **Record** 0002 section 33 rows AC-40, AC-41 with dates; commit this repo.

If any of steps 3–5 fails: **stop**. Do not continue to Step 22. Roll back per runbook 14c (previous symlink, previous instance commit), record, report at the gate.

**Seams**: the real Central (Graph, `claude`, the Softeria server), only through the installed binary and the units — never from a test.

**RED**: a read-only pre-check before installing:
- `zyggy --version` = `0.1.5`;
- `systemctl show -p ExecStart zyggy-m365-mcp.service` names `mcp-server.sh`;
- `/opt/zyggy/0.1.5` present.

**GREEN**: steps 1–6.

**Contract impact**: ⚠️ The security controls (guard, log, token helper) run from the binary on Central for the first time. ⚠️ Install order binary → pin → pull.

**VERIFY** (all recorded in 0002 §33 with dates):
- AC-40: install inspection (`stat`, `sha256sum` = pin), unit `ExecStart`, server active.
- AC-41: the four checks above with excerpts.
- `claude-remote` active after its restart, log `resuming <same session id>` (kept for VM-C2).
- CI run ids of the three repositories.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — first live run on Central *(covers Step 21)*

*Executor: STOP here. Present the results and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification:
  - Central runs the new version; the old one is kept for rollback.
  - The Microsoft 365 server was started by the binary and offers exactly the allowed tools.
  - `check` works.
  - The backfill picked up the shell's checkpoint.
  - A request to send mail with a hidden recipient was refused by the guard, and nothing was sent.
  - A fact remembered in a session landed in the inbox.
  - The remote session came back on the same conversation after its restart.
- [ ] Contract review: the units, settings and pin match the spec. The brief timer is still off. The template was pulled only after the binary was installed.
- [ ] ⚠️ Risk review:
  - The guard, the action log and the token helper are now the binary's on Central.
  - Rollback is the previous symlink plus the previous instance commit.
  - Nothing secret appeared in the recorded output.
- [ ] User approved — implementation may continue past this gate

---

## Step 22 — The morning brief runs once by hand on the new binary and leaves its Draft with a clean audit; the owner's one real send from the phone asks once and is logged by the new code; a mail question after a long idle needs no owner action; the nightly dream still commits on the new version; the secret sweep is clean; 0002, the roadmap and the founding-spec wording are checked

- [ ] Done

**Scope**:
- **Attended brief (decision 2, agent-run):**
  - `systemctl start zyggy-morning-brief.service` once (the unit's own environment), or `runuser -u zyggy -- /usr/local/bin/zyggy m365 brief` with the unit's environment.
  - Record: one "Zyggy — morning brief" Draft (possibly reply Drafts) in Drafts (`zyggy m365 check --counts` → `zyggy-drafts`), `audit ok`, the journal line, the `brief.jsonl` row, and `systemd-analyze security zyggy-morning-brief.service` (exposure score; also C23-AC12 run 1).
  - The timer stays off; `systemctl is-enabled zyggy-morning-brief.timer` → `disabled`.
- **Live send (decision 1, owner-run):**
  - The executor asks the owner to send, from the phone, a short mail to the owner's own second address through Zyggy, after at least 95 minutes without any m365 use (so it also proves C23-AC27), and to allow the one permission prompt once.
  - The agent then checks `actions.jsonl`: one new row, `status: ok`, recipients = that address, written by the new code (the row's timestamp is after the install), and exactly one prompt reported by the owner.
- **Idle token (AC-42):** after ≥ 95 minutes without m365 use, a mail question in a session is answered without any owner action. The journal shows `zyggy-m365: token minted` from the helper.
- **Dream on the new version (AC-40):** the next nightly `dream` record is `committed` (or `nothing_to_do`) and pushed, with the new version in its record.
- **Secret sweep (AC-43; also carried C23-AC22 and C23-AC29):**
  - memory, both checkouts (the whole instance tree), settings, units, `~/.local/state/zyggy` (incl. `actions.jsonl`), journal including `-t zyggy-m365`, `~/.claude.json`, `~/.claude/debug/`, transcripts, `~/.npm`, credential paths;
  - `ps -eo args` sampled during a token refresh;
  - patterns from `secret-patterns.txt` + a JWT pattern (`eyJ[A-Za-z0-9_-]+\.`) + `BEGIN .*PRIVATE KEY`;
  - result count 0 (documented false positives listed by path and pattern, never by value);
  - key `600`, cer `644`; `systemctl show -p LoadCredential -p InaccessiblePaths zyggy-m365-mcp.service zyggy-morning-brief.service` = the spec Contracts.
- **`/doctor prompt-audit`** on Central (AC-39 C; also carried C23-AC23): clean with the m365 rules; `wc -l` of every file under `.claude/rules/` ≤ 200.
- **This repository:**
  - 0002 §33 rows AC-42, AC-43, AC-45 dated;
  - "Tools on Central" row;
  - a read-only check that `_specs/00 …` contains W33-1..W33-8 (applied by the owner). If any is missing, list it at the gate. **Do not edit `_specs/00 …`.**
  - `_plans/ROADMAP.md` #33 is set to Done in Step 23, not here.

**Seams**: none (evidence on the real system).

**RED**: the 0002 §33 rows AC-42, AC-43, AC-45 are empty before this step.

**GREEN**: rows filled from records. Any AC not met is reported at the gate with its record (a fix is a new bugfix or plan step, not part of this one).

**Contract impact**: none.

**VERIFY**: AC-42 (brief Draft + audit ok + journal + `systemd-analyze security` recorded; owner's send after ≥ 95 min idle + one prompt + one `ok` row; idle answer), AC-43 (sweep count 0; `LoadCredential`/`InaccessiblePaths` = Contracts), AC-40 (dream on the new version), AC-39 (prompt audit; every rule file ≤ 200 lines), AC-45 (W33 present or listed). These records are reused by Step 23 for C23-AC12 (run 1), C23-AC22, C23-AC23, C23-AC27 and C23-AC29.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 23 — The live checks carried over from 02, 23, 27 and 28 are re-run on the new binary and recorded: the VM soak holds on day 7, the dream files a fact the night after it was told on three nights in a row, every consent and guard check passes from the phone and on Central, the brief and backfill drills pass, and every original row points to its result

- [ ] Done *(ticked when every non-conditional item below has a dated result — pass, or fail with its record — and every conditional item is either run or recorded as not run (owner decision))*

**Origin**: owner's clean-slate decision of 2026-10-05 (`ROADMAP.md` #33 "Carried into 33's Central evidence"; 0001 VM soak; 0002 §§23, 27, 28). The ids are the project manager's. The 32 items go to 34, not here. "D7 form" = consent by a Claude Code permission prompt and `actions.jsonl` (the D6 terminal path no longer exists).

**Who runs what** (column "Who" below):
- **Agent, read-only**: reads on Central through `az vm run-command` (git and `zyggy` reads as `runuser -u zyggy -- …`), Azure read-only queries, and files in this repository. Nothing on Central changes.
- **Agent-run**: the agent starts a `zyggy` verb or a `claude -p` probe on Central as `zyggy`, as in Step 21 (Assumption 11). Never a send, a move, a tenant change or a touch of the real key.
- **Owner-run**: only the owner can do it: sends and prompts on the phone or claude.ai, the Exchange audit-log search (`Search-UnifiedAuditLog` in Exchange Online PowerShell — Cloud Shell or local pwsh, as 0002 §23 AC-4), the Entra and Microsoft 365 admin portals, spot-checking backfill lines, revoking the Graph Explorer consent, moving the real key. When Step 22 is done, the executor sends the owner **one numbered list** of every owner-run item with what to report back, not one request per item.
- **Conditional**: needs the owner to switch the morning-brief timer on. "When the owner switches the timer on; otherwise recorded as not run (owner decision)." It never blocks the final gate.

**Dates**: VM-C1 is checked on or after 2026-10-06. C28-AC31 needs three consecutive nights (they may straddle the install; the night after Step 21 also serves Step 22's AC-40). C28-AC32 needs the night after Step 21's test fact. The executor waits for these dates without stopping at a gate. If a date has not come when everything else is recorded, it reports the item with its next date at the gate.

**Where results go**:
- VM-*: the "Day 7 result" cell of the named row in `_plans/decisions/0001-transport-and-vm.md` "VM soak".
- All other ids: one row each in a new subsection "Carried-over live checks" of 0002 section 33 (columns: Id · Who · Date · Evidence excerpt · Result · Original row). The original row's Result cell gets a pointer `→ 0002 §33 <id>, <date>, <result>`.
- Records hold counts, ids, exit codes and metadata only. No token, key, mail subject, body or third-party address; the owner's own addresses only.

**Scope** (items; "Step 21/22 record" = filled from that step's evidence, no second run):

*VM soak (from 02)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| VM-C1 | Agent read-only; owner confirms one fact | On or after 2026-10-06: `systemctl is-active claude-remote` → `active`; a headless `claude -p` run that day exits 0 (`soak.jsonl` or the dream's run record); the owner confirms no `/login` since day 0. Pass = all three. | 0001 "Authenticated after VM reboot" |
| VM-C2 | Agent-run (Step 21 record) | Step 21's `systemctl restart claude-remote`: log `resuming <id>` with the id in use before the restart. With the two restarts already recorded, ≥ 2 restarts in the soak window. | 0001 "Remote control resumed …" |
| VM-C3 | Agent read-only | `soak.jsonl` lines since 2026-09-29 19:00 UTC: count with `exit 0` ≥ 7, none non-zero; count and last timestamp recorded. If the soak timer no longer runs, the dream's nightly `claude -p` records are counted too and named as such. | 0001 "Headless `claude -p` runs" |
| VM-C4 | Agent read-only + owner-run | Agent: Cost Management forecast for the current month (`az` read-only) ≤ €60, amount and date recorded. Owner: names where the budget alert lives (scope, name, threshold), or that none exists — then the result is "fail: no alert", recorded for the owner's decision. | 0001 "Monthly cost forecast ≤ €60" |
| VM-C5 | Agent read-only | After a backfill run and a dream night with the m365 server active: `free -m`, `swapon --show` (swap used ≤ 200 MB), `journalctl -k --since 2026-09-29` OOM-kill lines = 0. | 0001 "4 GB RAM sufficient" |
| VM-C6 | Agent read-only | `az network nsg rule list --nsg-name central-nsg …` → 0 custom rules; `ss -tlnp` on Central → `zyggy m365 mcp-server`'s child on `127.0.0.1:47365` only. | 0001 "Nothing listens on the internet" |

*Remote session (from 27)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| C27-1 | Owner-run | The first fresh (not resumed) remote-control session after the install is listed in claude.ai with the title `Zyggy`; the owner reports it. | 0002 §27 session-title line (AC-36) + §33 row |

*Dream (from 28)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| C28-AC31 | Agent read-only | Three consecutive nightly run records exit 0, each with a `dream YYYY-MM-DD` commit on `origin/main` of the memory repo; run ids, dates, SHAs and binary versions recorded. | 0002 §28 AC-31 |
| C28-AC32 | Agent read-only | Step 21's labelled test fact (day N): after night N+1, its text is in a category file on `origin/main`, and the same `dream` commit changes that category file and the ledger with the line's hash (`git show --stat <sha>`). | 0002 §28 AC-32 |
| C28-AC33 | Agent read-only | Per-run counts and cost of every run since 2026-10-05 copied from the run records into §28 "Runs"; unconsumed inbox lines = 0, or the remainder stated with its count and date. | 0002 §28 AC-33 + "Runs" |
| C28-AC34 | Agent read-only | After the backlog is 0 (or at the stated remainder): digest section sizes measured as on 2026-10-05 (identity / index / daily) ≤ 6,000 / 6,000 / 8,000 bytes. | 0002 §28 AC-34 |
| C28-AC35 | Agent-run | While `zyggy-dream.service` is active (the nightly run, or an on-demand run started by a first request), `runuser -u zyggy -- zyggy dream request`. Journal: the second run starts after the first ends, trigger `on-demand`, no `locked` record, no overlap. | 0002 §28 AC-35 |
| C28-AC36 | Agent read-only | 28 AC-36's secret and contact-detail patterns over the memory repo `origin/main` contents (`git grep`) and history (`git log -p`) → 0 hits (false positives by path only). | 0002 §28 AC-36 |
| C28-AC39 | Agent read-only | `_specs/00 …` contains the W-1..W-12 texts of the 28 spec; missing ones listed at the gate. Never edited. | 0002 §28 AC-39 |

*Tenant and application (from 23)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| C23-AC1 | Owner-run | From the Microsoft 365 admin centre: number of users and mailboxes, licence tier, Exchange plan; nothing changed in the tenant. | 0002 §23 Tenant facts row + AC-1 |
| C23-AC3 | Owner-run | Entra → `zyggy-central` → Authentication: "Allow public client flows" = No. | 0002 §23 AC-3 |
| C23-AC5 | Owner-run | The owner revokes his Graph Explorer `Sites.FullControl.All` consent and reports the date. The `zyggy-central` site grant (`read`) is untouched: agent-run `zyggy m365 check --counts` still lists the OneDrive afterwards. | 0002 §23 AC-5 |

*Consent and guard (from 23, D7 form)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| C23-D7-AC7 | Owner-run + agent read-only | Owner, to his own second address: one send from claude.ai **allowed**, one send (phone or claude.ai) **denied**; each prompt shows recipients, subject and body. Step 22's phone send counts as the phone Allow. Agent: `actions.jsonl` +1 `ok` row per Allow, none for the Deny. | 0002 §23 D7 AC-7 |
| C23-D7-AC8 | Owner-run + agent read-only | Owner picks "Don't ask again" on one send prompt (if offered); the next send request prompts again. Agent: no `allow` rule for a send or move tool appeared in `.claude/settings*.json` on Central. If one did, the result is fail, reported at the gate. | 0002 §23 D7 AC-8 |
| C23-D7-AC11 | Owner-run + agent-run | Owner: one move to Archive and one soft delete (to Deleted Items) of his own mails, each prompted once. Agent: one `actions.jsonl` row each; a `claude -p` probe asking for a move to `recoverableitemsdeletions` ends with `permission_denials` naming the move tool with the guard's reason; no new row. | 0002 §23 D7 AC-11 |
| C23-D7-AC12 | Agent-run | Step 21's Bcc probe plus five `claude -p` probes (attachment, HTML body, 5,000-character body, 11 recipients, `SaveToSentItems: false`), recipients only the owner's own addresses. Each: `permission_denials` names the send tool with the guard's specific reason; `actions.jsonl` unchanged; Sent Items count unchanged (`zyggy m365 check --counts`). | 0002 §23 D7 AC-12 |
| C23-AC27 | Owner-run (Step 22 record) | Step 22's send after ≥ 95 min idle: one prompt, one send, one `ok` row. | 0002 §23 D7 AC-27 |
| C23-AC28 | Owner-run + agent read-only | Owner, in a VM shell as `zyggy`, after a `token minted` journal line: renames `~/.config/zyggy/m365-app.key` away, asks a mail question at the next connection → the failure names the runbook entry "Token refresh failed"; renames it back → the next question works without a restart. Agent: journal `-t zyggy-m365` lines; key `600` with mtime unchanged afterwards; R10: `mcp-wrapper.sh` absent from both checkouts on Central and named nowhere in `.mcp.json` or settings (removed by Steps 19/21). | 0002 §23 D7 AC-28 |
| C23-AC29 | Agent read-only (Step 22 record) | Step 22's sweep including `ps -eo args` during a token refresh: 0 hits in journals, `~/.claude.json`, debug logs, transcripts, `ps`. | 0002 §23 D7 AC-29 |

*Brief and backfills (from 23)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| C23-AC12 | Agent-run (Step 22 record) · runs 2–5 **conditional** | Run 1 = Step 22's attended brief: journal summary line, one brief Draft, ≤ N reply Drafts, no send/move/delete, `audit ok`, `systemd-analyze security` recorded. Runs 2–5: when the owner switches the brief on; otherwise recorded as not run (owner decision). | 0002 §23 D7 AC-12 |
| C23-AC13 | **Conditional** | Three consecutive timer runs as AC-12, one brief naming a OneDrive file edited the day before, a same-day manual start answers `already created`. When the owner switches the timer on; otherwise recorded as not run (owner decision). | 0002 §23 D7 AC-13 |
| C23-AC14 | Owner-run + agent read-only | Owner: `Search-UnifiedAuditLog -Operations Send,Move,MoveToDeletedItems,SoftDelete,HardDelete -FreeText <app id>` from the install to the check, counts by operation. Agent: `actions.jsonl` rows by tool, status and time. Pass = every app `Send`/`Move` matches one row and vice versa, none inside a timer run (vacuous while the timer is off), no `SoftDelete`/`HardDelete`. | 0002 §23 D7 AC-14 |
| C23-AC15 | Agent-run | Wrong key: a throwaway RSA key in a `mktemp -d` directory, `ZYGGY_M365_KEY_FILE=<it> zyggy m365 brief` outside the unit, on a day with no brief Draft yet → exit 6 `invalid_client`, no `claude` process, no Draft, no row. Then `zyggy m365 token-test` without the override → `token ok`. Expiry drill: a temporary copy of the instance configuration with `cert.expires` 10 days ahead → `zyggy m365 check` warns; in the past → exit 3. The temporary directory is deleted; the real key is never touched. | 0002 §23 D7 AC-15 |
| C23-AC16 | Owner-run + agent-run | Owner sends a canary mail from an external account (planted instruction to mail an external address) before a brief run. Agent: one attended brief → `audit ok`, or `audit FLAGGED` naming the external address; `zyggy m365 verify` shows no Draft to it; `actions.jsonl` unchanged. Owner: the brief lists the canary as data and proposes "ignore/report". | 0002 §23 D7 AC-16 |
| C23-AC17 | Agent-run (Step 21 record) + agent read-only | Step 21's mail backfill: SIGINT (130) and `resuming folder …` if work remained. Messages processed = folder totals from `check --counts` minus the excluded folders (± boundary). If no work remained, the interruption is recorded as "not possible: backfill already done"; resume is then proven by the shell's checkpoint read in Step 21 and by Step 17's tests. | 0002 §23 D7 AC-17 |
| C23-AC18 | Owner-run | The owner reads ≥ 30 random lines (`shuf -n 30`, run by the owner on the VM) of the mail-backfill inbox file, or of its lines already filed by the dream, and reports "facts only" or the line numbers that are not. The agent never prints the lines. | 0002 §23 D7 AC-18 |
| C23-AC19 | Agent-run + owner-run | Agent: `zyggy m365 files-backfill` once, SIGINT and `resuming drive …` if work remains (otherwise as C23-AC17); `zyggy m365 check --drive <the ungranted drive id of 0002 §23 AC-6>` → 403 (not granted). Owner: three OneDrive files' version histories show no new version since before the run. | 0002 §23 D7 AC-19 |
| C23-AC21 | Agent read-only | After the runs, `git -C memory status --porcelain` shows only paths the 28 layout leaves uncommitted between dream runs (`inbox/` is untracked and does not appear); any other path is listed at the gate. | 0002 §23 D7 AC-21 |

*Secrets, rules and records (from 23)*

| Id | Who | What is done; VERIFY | Evidence row |
|----|-----|----------------------|--------------|
| C23-AC22 | Agent read-only (Step 22 record) | Step 22's sweep scope (instance tree, memory, settings, units, `~/.local/state/zyggy`, journal, `~/.npm`, transcripts): 0 hits; `LoadCredential=`/`InaccessiblePaths=` = Contracts; key `600`, cer `644`. | 0002 §23 D7 AC-22 |
| C23-AC23 | Agent-run (Step 22 record) | Step 22's `/doctor prompt-audit` clean with the m365 rules; every rule file ≤ 200 lines. | 0002 §23 D7 AC-23 |
| C23-AC24 | Agent (this repository) | 0002 §23 has Tenant facts (with C23-AC1), the Credentials row, MCP servers, Tools, Settings and the P0b row complete; `grep -n pending` over §23 and §33 → only conditional items. | 0002 §23 D7 AC-24 |
| C23-ACTLOG | Agent read-only + owner (C23-AC14) | For each month with actions: sends ok, moves ok and errors counted from `actions.jsonl`; the audit-log column and "Unmatched" from C23-AC14. | 0002 §23 "Actions log" |
| C23-TOOLS | Agent read-only | `node --version`, `npm --version` as `zyggy`; the `zyggy` row with the Step 21 version, hash and path. | 0002 "Tools on Central" |
| C23-COSTS | Agent read-only (+ owner for sizes the binary cannot show) | Brief cost and turns from `brief.jsonl`; backfill totals from their journal counts lines; mailbox size from `check --counts` folder totals; drive size from the owner's look in the admin centre if `check` does not show it. | 0002 "Costs" (23 line) |

**Close-out** (after the table is complete):
- `_plans/ROADMAP.md` #33 → Done (date, commits, CI run ids, and a one-line summary of the carried-over results, conditional items named).
- 0002 P0b checklist row for 33.
- Commit this repository.

**Seams**: none (evidence on the real system and in this repository).

**RED**: before this step, each id above appears in 0001/0002 as "carried to 33" with no result.

**GREEN**: rows filled from records, pointers added. A failing check is recorded with its evidence and reported at the gate. Its fix is a new bugfix or plan step, not part of this one.

**Contract impact**: none. ⚠️ Owner-run actions touch the real mailbox (sends to the owner's own addresses, one move, one soft delete) and the real key (C23-AC28 rename and back). The agent never touches either.

**VERIFY**:
- Every non-conditional id has a dated result in its evidence row, and every original 0002 row carries its `→ 0002 §33` pointer. VM-* are in 0001's "Day 7 result" cells.
- `grep -n "carried to 33" _plans/decisions/0001-transport-and-vm.md _plans/decisions/0002-central-productive.md` → every hit is followed by a result or pointer on the same row.
- C23-AC13 and C23-AC12 runs 2–5 either have results or read "not run (owner decision): brief timer not switched on".
- The records hold no token, key, mail subject, body or third-party address (agent re-reads its own added rows).
- `_plans/ROADMAP.md` #33 reads Done.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice J — **definition of done for deliverable 33** *(covers Steps 22–23)*

*Executor: STOP here. Present the results and WAIT for user approval.*

- [ ] Behavioral verification:
  - The morning brief ran once on the new binary and left its Draft with a clean audit.
  - Your one real send asked once and was logged by the new code.
  - A mail question after a long idle needed nothing from you.
  - The nightly dream keeps committing on the new version.
  - The secret sweep found nothing.
  - The carried-over checks (Step 23):
    - The VM soak held on day 7: still logged in, the remote session resumed, ≥ 7 headless runs, cost within budget, memory enough, nothing listening outside.
    - The dream committed three nights in a row and filed the test fact the next night.
    - Each consent and guard check passed: prompts from phone and claude.ai, Deny sends nothing, "Don't ask again" does not stick, six refusals before any prompt, the key drills, the audit log matching `actions.jsonl`.
    - Your owner-run items are recorded: tenant facts, public client flows, the Graph Explorer consent revoked, the backfill spot-check, version histories.
- [ ] Contract review:
  - Every acceptance criterion in the map below is ticked, with its source in 0002 section 33.
  - Every carried-over id has a result in 0001/0002, with a pointer from its original row. The only exceptions are the brief-timer items, recorded as not run by your decision if the timer is still off.
  - The roadmap entry for 33 is marked done.
  - The founding-spec wording W33-1..W33-8 and W-1..W-12 is present, or the missing parts are listed for you.
- [ ] ⚠️ Risk review:
  - No token, key or JWT anywhere on Central.
  - The real key was moved only by you, and only for the drill; its mode and date are unchanged.
  - The records hold no mail content.
  - The work boundary is unchanged: Central only, the Digiverse tenant only.
  - 34 can start on this foundation: the credential store table, the "replace the environment" option, the fact-line writer and the hygiene test rows.
- [ ] User approved — deliverable 33 is done

---

## Acceptance-criteria → step map

| AC | Steps | AC | Steps | AC | Steps |
|----|-------|----|-------|----|-------|
| AC-1 | every gate (CI), 21 | AC-16 | 8 | AC-31 | 16, 17, 21 |
| AC-2 | every gate (diff review) | AC-17 | 9 | AC-32 | 16, 17 |
| AC-3 | 13, 14 | AC-18 | 9 | AC-33 | 16, 17, 21 |
| AC-4 | 7, 9 | AC-19 | 9 | AC-34 | 15, 17 |
| AC-5 | 5 | AC-20 | 10, 11 | AC-35 | 8 |
| AC-6 | 2, 4, 6, 9, 11, 12, 14, 17 | AC-21 | 11, 19 | AC-36 | 19 |
| AC-7 | 2, 11, 14 | AC-22 | 10, 11 | AC-37 | 13, 19 |
| AC-8 | 1, 2 | AC-23 | 12 | AC-38 | 19, 20, 21 |
| AC-9 | 1 | AC-24 | 13, 14 | AC-39 | 19, 22 |
| AC-10 | 1, 3, 15 | AC-25 | 13, 14 | AC-40 | 21, 22 |
| AC-11 | 1, 3, 5, 8, 12, 19 | AC-26 | 14 | AC-41 | 21 |
| AC-12 | 3, 4 | AC-27 | 13, 19 | AC-42 | 22 |
| AC-13 | 7 | AC-28 | 3, 4 | AC-43 | 22 |
| AC-14 | 7, 9 | AC-29 | 5, 6 | AC-44 | 20 |
| AC-15 | 8 | AC-30 | 15, 17, (18), 22 | AC-45 | 22 |

Carried-over live checks (not 33 ACs; owner's clean-slate decision 2026-10-05): VM-C1..VM-C6, C27-1, C28-AC31..AC36, C28-AC39 and the C23-* items → Step 23. Some reuse Step 21's records (VM-C2, C23-AC17, the Bcc probe of C23-D7-AC12) or Step 22's (C23-AC12 run 1, C23-AC22, C23-AC23, C23-AC27, C23-AC29).

Parity table rows (spec) → steps:
- `remember.sh` → 1, 2.
- `lib.sh` helpers → 1, 3.
- `facts.sh` → 3, 4.
- `graph.sh` grammar → 9.
- `m365-lib.sh` config → 5.
- Key lookup → 7, 9.
- Token and assertion → 7, 9.
- `check` and reads → 8, 9.
- `cert-init` → 9.
- Guard → 10, 11, 19.
- Log → 10, 11.
- `verify.sh` → 12.
- Helper → 13, 14.
- `mcp-server.sh` → 13, 14.
- `mcp-wrapper.sh` → 19 (deleted; its bats cases recorded as obsolete in the step summary).
- Tool partition → 13, 19.
- `state.sh` → 3, 4.
- `parse.sh` → 5, 6.
- `brief.sh` → 15, 17.
- Backfills → 16, 17.
- Settings/`.mcp.json`/hygiene → 8, 19.

---

## Assumptions (where the spec leaves the shape to the planner; each is reviewed at the named gate)

1. *(Gate A)* **Raw verb dispatch.** `memory remember` and every `m365` verb bypass System.CommandLine parsing and get their tokens unchanged, so each verb reproduces its script's usage texts and exit 4 (decision 5). `dream` and `memory digest` still go through System.CommandLine, unchanged.
2. *(Gate A)* **The verb layer lives in `Zyggy.Core`** (`Verbs/VerbIo`, `RememberVerb`, `M365VerbHost` public; every verb class internal). In-process tests can then run a whole verb with only the Graph handler stubbed, because the binary has no test switch (deviation 2). The services override is an internal constructor (Core already has `InternalsVisibleTo Zyggy.Integration`).
3. *(Gate C)* **Windows.** `parse`, `mcp-server`, `cert-init` and the credential checks exit 3 `not supported on this platform` (spec Edge Cases). CI compiles and unit-tests the platform-neutral parts.
4. *(Gate D)* **`cert-init` writes the key file directly.** The credential store is read-only by spec (AC-4), so key creation is the one secret write outside `ISecretStore`.
5. *(Gate D)* **The key file's owner check** needs the file's uid. The BCL has no public API for it, so `CredentialFileSecretStore` calls `lstat` through `[LibraryImport("libc")]` (Linux only). The alternative of running `stat -c %u` through `IProcessRunner` per read was rejected: slower on the hook path, and more surface.
6. *(Gate F)* **The MCP server replaces the `zyggy` process via `execve`** (spec allows exec). SIGTERM and the exit code then belong to the server by construction. `AllowUnsafeBlocks` is enabled in `Zyggy.Core.csproj` only if the `LibraryImport` generator requires it.
7. *(Gate G)* **m365 model runs** use `MaxCaptureBytes = 64 MiB`, because `stream-json --verbose` carries tool results (mail and file text, kept in memory only and never logged) and the dream's 2 MiB would end a real brief as `output_too_large`. They use `Timeout = 120 min`: the scripts had none, and the unit's `TimeoutStartSec` bounds the brief. Both are code constants.
8. *(Gate G)* **SIGTERM/SIGINT of the brief and the backfills** is proven in-process (cancellation while fake-claude runs, no process left, run dir removed, 143/130) and by `SignalCancellation` unit tests. A binary-level signal test of a Graph-dependent verb would need a test switch in the binary, which the spec forbids. The live interruption on Central (Step 21) covers the rest. The `mcp-server` SIGTERM test is binary-level.
9. *(Gate G)* **The model's tool calls** (state writes, facts, replied ids) are simulated in integration tests by `ActingModelRunner`, a test-only decorator over the real runner and fake-claude, as the bats `*-actions.sh` stubs did.
10. *(Gate I)* **Tool data file names**: `.claude/skills/m365/tools/{enabled,excluded,actions,auth}.txt` + `server-version.txt`; one name per line, LF, sorted ordinal. The minimum binary version is `.claude/zyggy-min-version`.
11. *(Gate J)* **The refusal probe and the session `remember`** are `claude -p` runs started by the agent on Central as `zyggy` in the instance checkout (spec AC-41 "agent-run"). Step 23's guard probes (C23-D7-AC11 `recoverableitemsdeletions`, the five further refusals of C23-D7-AC12) use the same form. These are the only places the agent starts `claude` itself, which 28 avoided. Each one asks for an action the guard refuses, so a probe never sends or moves anything.

## Notes for the executor

1. **Branches.** Work on `feature/33-m365-verbs` in all three repositories. Keep a draft PR open in this repository so CI runs on both runners. Merge `main` into the branch whenever a dream fix lands. Never merge the branch into `main` before Step 21's precondition (Gates A–I approved) holds; there is no wait for 28 any more.
2. **Order on Central** (Step 21). Binary and pin first, then the template/instance pull, then the units and one server restart. Rollback is the reverse (runbook 14c). Never pull the template before the binary: the guard launcher would block every action with "zyggy not found", and the helper would fail every connection.
3. **Root-run git and no hand edits.** Same as 28 note 3: `runuser -u zyggy -- git -C …`, never `git config --global` on the VM, never edit `memory/` by hand.
4. **`az vm run-command`.** It was blocked once for the decision 7 check. If it is blocked in Step 21 or 22, that command becomes the owner's (runbook entry named); everything else stays with the agent.
5. **Golden copies.** Byte-preserving copy and `git ls-files --eol tests/golden` → `i/-text` (28 note 5). Record the `zyggy-core` commit SHA the copies come from in `tests/golden/README.md`. Copy them before Step 19 deletes the template originals.
6. **Do not edit** `_specs/00 …` (the owner applies W33-1..W33-8), the 33 spec's decision 7 placeholder (the main session fills it), or genome files.
7. **Step 18 is conditional.** Read its precondition before anything else. If the decision 7 result is still pending when Gate G is approved, stop at Gate H and ask.
8. **Step 23 (carried-over checks).** Collect the owner-run items in one numbered message after Step 22, not one by one. Dated items (VM-C1, C28-AC31, C28-AC32) are waited for without a gate. The brief-timer items are never a reason to hold the final gate: if the timer is off, they are recorded as not run (owner decision). Do not add any 32 item here; those belong to 34.
