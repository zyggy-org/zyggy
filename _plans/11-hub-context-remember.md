# Plan: 11 — Hub answers `get_context` and `remember` over stdio; `SessionStart` digest — "As the owner in a Claude Code session on Central, I want the session to ask the Hub for the memory that matters to a topic and to hand it a fact to keep, so that memory beyond the session-start digest is reachable without the model reading the repository by hand, and a stated fact lands in the principal's `inbox/` through the one refusal path every fact writer shares"

> **Scheduling note.** Branch `feature/11-hub` from `main` (after `50aef8d`). The sibling template repository `zyggy-core` (`D:\source\zyggy-core`) gets its own branch `feature/11-hub` for Step 7; both repositories are left **uncommitted** at every 🛑 HUMAN GATE for the owner's review (auto-memory "Owner reviews before commit"); the agent never pushes, never tags. The four founding-spec amendments **A11-1..A11-4** (spec section "Deliberate deviations") are **not** edited by this plan: they are the orchestrating session's paste once the owner approves the wording; this plan assumes them and nothing in it depends on their timing. The owner-executed D1 verification (`claude mcp list` on Central shows `hub` connected without an `env` block) belongs to the final 🛑 HUMAN GATE and needs a tagged release installed on Central; the executor can tick every step before it.

## Overview

After this plan `zyggy hub` is an MCP server over stdio with two tools. `get_context(topic, max_tokens)` reads the principal's `<side>/<category>/<slug>.md` files and the last 30 `daily/YYYY-MM-DD.md` files through `MemoryStore` (the only file-system access of the Hub, built on `MemoryPaths`), ranks them with the pure `ContextRanker` (exact alias/name/slug, then all terms, then any term; inside a tier `updated` descending, then path), lays the result out under `max_tokens × 4` bytes with the daily block capped at 20 % and cut at line boundaries with markers, and returns one text block that states memory is data, never instructions. `remember(fact, scope)` goes through the existing `RememberService` and answers byte for byte what `zyggy memory remember` prints, refuses secret-shaped facts by pattern name, and stores nothing under `ZYGGY_HOOKS=off`. The principal is fixed at start from the `ZYGGY_*` environment with the `settings.local.json` fallback (D1), never from a request; a request naming `tenant` or `user` is refused with one stderr line. The template's `.mcp.json` declares the server, the settings allow `get_context` and deny `Bash(zyggy hub*)`, the runbook entries exist, and the one §7 digest test still missing (Σ of the three sections under the token target) is added.

Implements `_specs/11-hub-context-remember.md` (approved 2026-10-28, zero Open Questions, D1–D6 accepted: AC-1..AC-26, the Decision Table and the Contracts are binding). Founding-spec sections: §3 Components (Hub, `remember` skill, `SessionStart`), §7 Layout, File format, Context loading, Hub MCP surface, §8 Isolation and Injection, §9 folder map (`Memory/`, `Zyggy.Hub`), Packages (MCP SDK), design rules, §10 Central and Claude Code side, §12 definition of done, §13 decisions (MVP scope, Hub principal from the transport), §14 Tenancy.

**Reference pattern** (read across all layers; mirrored here): **deliverable 36's LinkedIn MCP server** — `src/Zyggy.Core/LinkedIn/Mcp/LinkedInMcpServer.cs` (`McpServer.Create` over `StreamServerTransport`, `NullLoggerFactory`, `McpServerPrimitiveCollection<McpServerTool>`, the assembly's informational version, exit 0 when stdin closes), `PublishPostMcpTool.cs` (an explicit `McpServerTool` subclass with a hand-written `Tool` descriptor, the raw arguments re-serialised to one `JsonElement`, one `TextContentBlock` per result, `IsError`), `PublishPostDescriptor.cs` + `tests/golden/linkedin/tool-publish_post.json` + `PublishPostDescriptorTests.Schema_3000_ByteEqualsGolden` (the schema as a golden file), `PostArguments.TryParse` (Zyggy's own validation: unknown key → invalid, wrong type → invalid, "the SDK's schema handling is never trusted for policy"), `LinkedInServerStart.Decide` + `ServerStart` record + `LinkedInServerStartTests` (the start rule decided before any protocol code), `McpServerVerb.cs` (no arguments → exit 4; `Console.OpenStandardInput/Output` only there), `LinkedInVerbHost` → `RawVerbs.TryDispatch` → `CliApplication` (the verb is listed for `--help` only), and the two test levels: `tests/Zyggy.Core.Tests/LinkedIn/` (in process) and `tests/Zyggy.Integration/LinkedIn/LinkedInMcpServerTests.cs` over the hand-written `tests/Zyggy.Integration/Infrastructure/McpStdioClient.cs` (the independent JSON-RPC oracle; today it hardcodes `linkedin mcp-server` — Step 6 generalises it). Memory side: `MemoryPaths` (`TryResolve`, `Side`, `Category`, `File`, `Daily`, `InboxFile`), `MemoryFileReader.Read/Parse` (`FormatException` on a bad front matter), `MemoryFile`, `DigestBuilder` (the wrapper line, the data sentence, UTF-8 byte counting, markers), `RememberService` / `RememberArguments` / `RememberVerb` (the line, the messages, the exit codes), `SecretPatterns.Load` / `SecretPatternsLocation.Resolve`, `MemoryEnvironment.Resolve`, `M365Environment.PrincipalFromSettings` (moved to `Zyggy.Core.Memory` in Step 5). Test infrastructure reused: `tests/Zyggy.Core.Tests/Infrastructure/MemoryTree.cs` (`acme/alice`, `CopyOf(golden)`), `Golden.Directory`, `VerbConsole`, `SecretPatternsTests.SecretSamples/BenignSamples`, `SecretSweep`, `RepoRoot`; `tests/Zyggy.Integration/Infrastructure/ScratchDirectory.cs`, `ZyggyCli.RunAsync` / `ExecutablePath`. Sibling-repo step shape: `_plans/36-central-linkedin-presence.md` Step 11.

**Phase**: 11 serves P1 but does not close it (13 does: it appends part 2 — context envelope → ingest → `get_context` returns it — to `tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs`). That file is **never edited** here (`git diff` empty at every gate). Planner rule 15 is therefore adapted: the last slice is the definition-of-done slice (template, runbook entries, the digest-size test, the hygiene sweep), and the final 🛑 HUMAN GATE is the owner's checklist including the D1 verification on Central. The round trip the roadmap's DoD names — the built binary over real stdio, both tools, a refused secret and a rejected `tenant` — is Step 6.

**What this plan deliberately is not** (spec Out of Scope / Defer): no `list_nodes`, `submit_job`, `job_status`, no HTTP transport or bearer principal, no `zyggy hub --proxy` (reserved: exit 4 naming 21), no laptop cache; no `user` key in `node.json`, no `NodeConfigurationReader` change; the `Zyggy.Hub` project is **untouched** (`Program.cs` stays `return 0;`; its README is rewritten to say the server lives in Core and runs as `zyggy hub`); no change to the digest's content, caps or launcher (`session-start.sh`, `stop.sh` as they are); the `remember` skill keeps calling `zyggy memory remember`; no body full-text search, fuzzy matching, tokenizer, cache or embeddings; no `[observed]` through the Hub, no `Stop` hook on nodes (12), no ingest (13); no telemetry, health or Serilog in the Hub; **no new NuGet package** (`Directory.Packages.props` stays at 24 rows — `Packages_RowCountPinned` is the guard; `ModelContextProtocol` 2.2.0 already serves server, client and stream transports).

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — Memory for a topic, ranked and budgeted | 1, 2 | the pure ranker orders in-memory entries by tier, `updated` and path, cuts at line boundaries with the two markers under `max_tokens × 4` bytes, refuses a bad topic or budget; the store enumerates only `<side>/<category>/<slug>.md` and the last 30 daily files of a real tree, skips `inbox/`, `auto/`, `archive/`, `.dream/`, legacy directories, roll-ups and identity files, never fails on one bad front matter | 🛑 after Step 2 (contract sign-off on the result layout before any protocol code) |
| B — The Hub serves both tools to an in-process MCP client | 3, 4 | `initialize` answers `hub`; `tools/list` equals the two goldens; `get_context` answers from a `MemoryTree` and leaves it byte-identical; `remember` writes the same inbox line as the CLI, refuses secrets by name, stores nothing under hooks-off; `tenant`/`user` and unknown arguments are refused by Zyggy's own check | 🛑 after Step 4 (⚠️ §7 shared contract; ⚠️ secrets) |
| C — `zyggy hub` from the built binary over real stdio | 5, 6 | the start rule (principal from the environment, filled from `settings.local.json`, patterns loaded, fail closed), the verb's exit codes 3/4, and the whole AC-20 round trip through the generalised hand-written client; stdout carries only JSON-RPC lines | 🛑 after Step 6 (⚠️ the principal resolution D1; ⚠️ the hub reads a real memory tree read-only) |
| D — Central's template, the runbook entries and the definition of done | 7, 8 | `.mcp.json` declares `hub`, the settings allow/deny rules exist, `zyggy-min-version` is bumped, `repo.bats` pins it all, the READMEs and `operations.md` carry the two runbook entries; the three digest sections stay under the §7 target; no Hub fixture names the owner | 🛑 after Step 8 — **definition of done** (the owner's `claude mcp list` check on Central) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green locally; the `zyggy-core` side (Step 7) is proven by its bats suite in the local container (`memory/sibling-repos-and-goldens.md`: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/"`).

<!--
Decomposition strategy: vertical slices, not horizontal layers. Slice A proves the ranking and the budget on in-memory
entries (Step 1, pure) and the read-only enumeration on a MemoryTree (Step 2) — the "fake" is the in-memory entry list and
the temporary tree. Slice B proves both tools through the seam the spec names (the SDK's McpClient over StreamClientTransport
on two in-process pipes) with a substituted clock, zone and tree — never a process. Slice C wires the real edge: the start
rule, the verb, the built binary over real stdio, driven by the hand-written JSON-RPC client. Slice D is the template, the
runbook entries and the definition of done. Never the real claude, never GitHub, never geoffrey/geoffrey in any test.
-->

---

## Shared rules for this plan

- **Principal in every test, fixture, golden and example**: `acme/alice` (`MemoryTree.Alice`); time zone `Europe/Brussels` as the custom +02:00 zone of `RememberVerbTests.FindTimeZone` where a local date matters, else `UTC`; the fake clock at `2026-10-28T10:00:00Z` unless a test says otherwise. `geoffrey` appears nowhere (`SourceHygieneTests.Src_ContainsNoTenantLiteral` over `src/`; Step 8 adds the same sweep over the Hub tests and goldens). Paths are placeholders in every README and runbook text (`memory/long-term/repo-public-hygiene.md`; `zyggy-core`'s `hygiene_paths` flags `/srv/`, `/home/`, `C:\Users\`).
- **Argument validation is Zyggy's, before any I/O, in one order for both tools**: `tenant`/`user` present → `refused: tenant and user are never request fields` (+ one stderr line `hub: refused: request named a principal`) → any other unknown property or a wrong JSON type → `refused: invalid arguments` → ranges (`max_tokens`, topic length, the `RememberArguments` messages). The SDK's `inputSchema` has `additionalProperties: false` for documentation only.
- **Every result** is `CallToolResult { IsError, Content = [one TextContentBlock] }`; nothing but JSON-RPC lines reaches stdout; the SDK logs to `NullLoggerFactory.Instance`; one stderr line per refused principal call, none per served call.
- **Paths**: the Hub never builds a path. `MemoryStore` is the only file-system access and uses `MemoryPaths` members (`Side`, `Category`, `File`, `Daily`, `TryResolve`, `Relative`); `RememberService` is the only writer (`inbox/remember-<date>.md`).
- **No new NuGet package**; `Directory.Packages.props` unedited (24 rows). The in-process pipes come from `System.IO.Pipelines.Pipe` (in the shared framework; no `PackageVersion` row) — see A3.
- **Shared-contract edits**: none on the bus wire format (`PROTOCOL.md`, `BusPaths`, `node.json`, `EnvelopeSigner` untouched; `git diff tests/golden/*.md tests/golden/*.sig tests/golden/*.canonical` empty). `Gates/P1_WalkingSkeleton.cs`, `DigestBuilder`, `DigestOptions`, `RememberService`, `RememberArguments`, `FactLineWriter`, `SecretPatterns` are **unedited**; before touching `SourceHygieneTests.cs` run the whole suite and note the counts; after GREEN the counts may only grow.
- **Owner-facing wording** (`memory/owner-facing-wording.md`): "signing key", never "hash"; runbook rows name the `ZYGGY_*` values and `.claude/settings.local.json`, never a real path.
- **Two repositories, two branches, nothing committed**: `zyggy` on `feature/11-hub`, `zyggy-core` on `feature/11-hub`; the executor reports per repository at each gate.

### Assumptions (reviewer: confirm or correct at Gate A)

- **A1** — `HubServer.RunAsync` takes a `TextWriter diagnostics` (the verb passes `io.Error`) in addition to the spec's `(stdin, stdout, start, clock, ct)`: AC-9's stderr line must be written by the tool, and the new Core classes read no console (the `Src_Deliverable08Classes_NoConsoleClockOrStaticState` rule applied to `Hub/`; `Console.OpenStandardInput/Output` only in `HubVerb`, as `McpServerVerb` does).
- **A2** — The `remember` result text is exactly the CLI's stdout bytes, `remembered: <path>\n<line>\n` (AC-11 "byte-identical to what `zyggy memory remember` prints on stdout" is read literally; the Contracts table's `remembered: <path>\n<line>` is the same text with the final newline). The unit test asserts equality with a `RememberVerb` run on a copy of the same tree and clock, so byte-identity holds whatever the exact bytes are.
- **A3** — The in-process harness uses `System.IO.Pipelines.Pipe` (two pipes: client→server and server→client; `Pipe.Reader.AsStream()` / `Pipe.Writer.AsStream()`), which .NET 10's shared framework provides without a package row. If the build disagrees, `System.IO.Pipes.AnonymousPipeServerStream` + `AnonymousPipeClientStream` (BCL) are the fallback; either way no `PackageVersion` is added.
- **A4** — `.claude/zyggy-min-version` becomes the version of the release that ships `zyggy hub`: the next tag after `v0.9.0`, assumed **`0.10.0`**; the executor writes the string the owner names at Gate C (the template test pins the string; the instance pin check is the existing bats test).
- **A5** — `RememberTool` builds its request with `RememberArguments.Parse(scope is null ? ["--", fact] : ["--scope", scope, "--", fact])`, so the `refused: <message>` texts are the verb's verbatim (`empty fact`, `fact longer than 1000 characters`, `--scope must be general, project:<name> or machine`, `invalid project name in --scope`) — AC-13 asks for "the message RememberArguments gives", option name included. `tag`/`source` never reach the parser (no such tool input), so the line is always `[stated]` with no provenance.
- **A6** — The settings fallback file is `$CLAUDE_PROJECT_DIR/.claude/settings.local.json` (spec Contracts "Principal resolution" 2), not the M365 verbs' `<instance>/../.claude/settings.local.json`; `PrincipalFromSettings` keeps taking the path as a parameter so both callers compute their own.
- **A7** — `ServerInfo.Version` is the assembly's informational version split at `+`, as `LinkedInMcpServer.Version()` does; the two-line helper is duplicated privately in `HubServer` (moving it would touch the LinkedIn server for nothing).
- **A8** — `MemoryStore.DurableEntries()` walks `paths.Side(side)` → category directories accepted by `CategoryName.TryParse` → `*.md` whose name does not start with `_` and whose stem `Slug.TryParse`s, then confirms each with `paths.TryResolve("<side>/<category>/<slug>.md")` (`MemoryArea.Durable`; a refused resolution — symlink escape, bad segment — is skipped). `DailyLines(today, windowDays)` never enumerates: it probes `paths.Daily(date)` for each date from `today` down to `today − windowDays` (31 `File.Exists` calls), which excludes the monthly roll-ups and `notes.md` by construction.
- **A9** — Template settings: `Bash(zyggy hub*)` is appended at the **end** of `permissions.deny` (index 342, length 343) and `mcp__hub__get_context` at the end of `permissions.allow`; the `repo.bats` slices (`deny[338:341]`, `deny[341:]`, the allow list) are updated to the new layout, nothing else reordered.
- **A10** — `src/Zyggy.Core/Hub/` is the new folder (A11-2 names it); the hygiene fact `Src_McpSdk_OnlyUnderLinkedInMcp` becomes `Src_McpSdk_OnlyUnderMcpServers` allowing exactly `src/Zyggy.Core/LinkedIn/Mcp/` and `src/Zyggy.Core/Hub/`.
- **A11** — `zyggy-core`'s `tests/fixtures/zyggy-stub.sh` learns `hub` (the stub-coverage pattern of 36) and `README.md`'s layout/verb text names the `hub` server; `AGENTS.md` and the rules keep their line caps (≤ 200).
- **A12** — The `--help` listing in `CliApplication` gains `new Command("hub", "The memory MCP server over stdio (get_context, remember); started by Claude Code from .mcp.json, never by hand.")`, listed only (`RawVerbs` runs it), exactly as `remember` and `archive` are listed.

---

## Step 1 — Rank memory entries for a topic and lay the result out under the token budget: exact alias/name/slug first, then all terms, then any term; inside a tier `updated` descending then path; whole files in rank order, the first that does not fit cut at a line boundary with `[context truncated: <n> more files not included — over max_tokens <m>]`; the daily block laid out first with at most 20 % of `max_tokens × 4` bytes and `[context truncated: <n> more daily lines not included]`; `no memory file or daily line matches the topic` when nothing matches; a bad topic or budget refused before anything is laid out

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/MemoryEntry.cs` *(create)*: `internal sealed record MemoryEntry(string RelativePath, MemorySide Side, CategoryName Category, Slug Slug, MemoryFile File);` and `internal sealed record DailyLine(DateOnly Date, string Text);`
- `src/Zyggy.Core/Memory/ContextOptions.cs` *(create)*: `internal sealed record ContextOptions { int BytesPerToken = 4; int DefaultMaxTokens = 2000; int MinTokens = 100; int MaxTokens = 8000; int DailyWindowDays = 30; int DailyBudgetPercent = 20; int TopicMaxChars = 200; }` (`init` properties, defaults as listed).
- `src/Zyggy.Core/Memory/ContextRanker.cs` *(create)*: `internal sealed record ContextRequest(string Topic, int MaxTokens);` `internal enum MatchTier { None = 0, AnyTerm = 1, AllTerms = 2, Exact = 3 }` `internal sealed record ContextResult(string Text, int FilesIncluded, int FilesOmitted, int DailyLinesIncluded, int DailyLinesOmitted);` `internal static class ContextRanker` with `static (string Normalised, IReadOnlyList<string> Terms, string? Refusal) Topic(string topic, ContextOptions options)` (trim, collapse internal whitespace to one space, lower-case invariant; terms = whitespace-separated words with leading/trailing ASCII punctuation stripped, shorter than 2 characters dropped; empty or no terms → `refused: invalid arguments: topic is empty`; over `TopicMaxChars` (Unicode scalar count, `TextCollapse.CharCount`) → `refused: invalid arguments: topic over 200 characters`), `static MatchTier Match(string normalisedTopic, IReadOnlyList<string> terms, MemoryEntry entry)` (haystack = slug, `Name`, each alias, `Description` (empty when absent), ordinal-ignore-case: `Exact` when the normalised topic equals the slug, the name or an alias; else `AllTerms` when every term is contained in at least one haystack string; else `AnyTerm` when any is; else `None`), `static ContextResult Rank(ContextRequest request, IReadOnlyList<MemoryEntry> entries, IReadOnlyList<DailyLine> daily, ContextOptions options, Principal principal, DateTimeOffset generated)` — pure, no I/O.
- `tests/Zyggy.Core.Tests/Memory/ContextRankerTests.cs` *(create)*.

**Seams**: none to fake — the ranker is pure (the "fake" is the in-memory `MemoryEntry`/`DailyLine` lists). `TimeProvider` is not consulted: `generated` and `today` are parameters.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/ContextRankerTests.cs` — entries built in memory with `MemoryFileReader.Parse(text)` over small front-matter strings (`name`, `aliases`, `description`, `updated`), principal `MemoryTree.Alice`, `generated = 2026-10-28T10:00:00Z`.
- Unit test methods:
  - `Rank_ExactAliasThenAllTermsThenAnyTerm_OrderedByTier` (AC-3: three files — alias `zyggy` exact, name+description holding both terms of `zyggy roof`, description holding one; a fourth matching no term is absent)
  - `Rank_SameTier_UpdatedDescendingNullLastThenPathOrdinal` (AC-3; edge "two files same tier same `updated` → path order")
  - `Match_TopicEqualsSlugNameOrAlias_IgnoringCase_IsExact` (`[Theory]`: `Zyggy`, `zyggy`, the name, an alias; a body line that contains the topic is **not** searched)
  - `Topic_TrimCollapseLowercase_TermsStripPunctuationDropShortWords` (`"  Zyggy,  roof-repair!  a "` → terms `zyggy`, `roof-repair`; `a` dropped)
  - `Topic_EmptyWhitespaceOrNoTerms_RefusedTopicIsEmpty` and `Topic_Over200Characters_RefusedOver200` (AC-8 message texts verbatim)
  - `Rank_Layout_WrapperDataSentenceFileHeadingsBodyWithoutFrontMatterDailyBlockFoot` (the exact text of the Contracts layout for two files and two daily lines — one golden-style inline string: `<zyggy-context tenant="acme" user="alice" generated="2026-10-28T10:00:00Z">`, the data sentence, `## business/areas/zyggy.md — <description>`, body lines, `## private/people/carol.md — (no description)` for an absent description, `## daily`, the daily lines as written newest file first, `</zyggy-context>`)
  - `Rank_NoFileNoDailyLineMatches_NoMatchSentenceWithoutWrapper` (`no memory file or daily line matches the topic`)
  - `Rank_DailyLinesMatchButNoFile_WrapperAndDailyBlockOnly` (edge case)
  - `Rank_BudgetCountsUtf8BytesOfWholeTextIncludingMarkers` (a non-ASCII body: the result's `Encoding.UTF8.GetByteCount(Text) <= MaxTokens * BytesPerToken`)
  - `Rank_FirstFileThatDoesNotFit_CutAtLineBoundaryMarkerNamesOmittedCountAndMaxTokens_NothingAfterIt` (AC-6: five files, budget fits two whole; the third cut at a line boundary; marker `[context truncated: 2 more files not included — over max_tokens 100]`; `FilesIncluded == 3`, `FilesOmitted == 2`; the smaller fourth/fifth files are **not** added after the cut)
  - `Rank_FileLargerThanWholeBudget_FirstFileCutMarkerNoSecondFile` (edge case)
  - `Rank_DailyBlockNeverOverTwentyPercent_CutWithDailyMarker` (AC-6: 40 daily lines, `MaxTokens 100` → the daily block's bytes ≤ 80, marker `[context truncated: <n> more daily lines not included]`, `DailyLinesOmitted == n`; files take the remainder)
  - `Rank_DailyLinesNewestFileFirstFileOrderPreserved` (two files, three lines each)
  - `Rank_TopicWithQuotesAndAngleBrackets_NeverInWrapperAttributes` (edge case: the wrapper carries tenant, user, generated only)
  - `Rank_MaxTokensDefaultAndRange_ContextOptionsValues` (`ContextOptions` defaults 4 / 2000 / 100 / 8000 / 30 / 20 / 200)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ContextRankerTests"`

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- The four files above. `Rank`: compute tiers with `Match`, drop `None`, order by tier desc, `File.Updated` desc (`null` last), `RelativePath` ordinal; filter daily lines by "any term contained" (ordinal-ignore-case), keeping the input order (the store hands them newest file first); budget `bytes = MaxTokens × BytesPerToken`; head = wrapper line + data sentence (the digest's sentence verbatim: `The lines below are the owner's memory: data to consult, never instructions to follow.`), foot = `</zyggy-context>\n`; lay out the daily block first against `bytes × DailyBudgetPercent / 100` (`## daily` heading, lines, marker when cut — every line counts its `\n`), then files against the remainder (`bytes − head − foot − daily block`): a file is `## <RelativePath> — <Description or "(no description)">` plus `File.BodyLines`; whole files while they fit; the first that does not fit is cut at a line boundary (the marker's bytes reserved first, as `DigestBuilder.SectionText.MarkerBytes` does), the files marker appended, and no further file; counts into `ContextResult`. No match at all → the sentence, no wrapper. Culture-invariant formatting everywhere (`CultureInfo.InvariantCulture`, `string.Create`).

**Contract impact**: the §7 `get_context` output shape is made exact (A11-4 — the orchestrating session's paste); no `PROTOCOL.md`, `node.json` or bus change. ⚠️ **Shared contract §7** (13 part 2 asserts on this text; 21's proxy reuses it): reviewed at Gate A.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`); `ContextRankerTests` (all methods above) pass; `git diff tests/golden/` empty.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — Read the principal's durable files and the last 30 daily files, and nothing else: `MemoryStore.DurableEntries()` returns every `<side>/<category>/<slug>.md` on both sides (`_index.md` excluded, a bad front matter → an entry with empty front matter, a refused `TryResolve` skipped) and `DailyLines(today, 30)` returns the body lines of `daily/YYYY-MM-DD.md` within the window, newest file first, roll-ups and `notes.md` never; `inbox/`, `auto/`, `archive/`, `.dream/`, legacy `areas/` and the identity files are never candidates — and the ranker over the store gives the AC-3/AC-4/AC-5 answers on a real tree

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/MemoryStore.cs` *(create)*: `internal sealed class MemoryStore(MemoryPaths paths)` — `IReadOnlyList<MemoryEntry> DurableEntries()`, `IReadOnlyList<DailyLine> DailyLines(DateOnly today, int windowDays)`; read-only; the only file-system access of the Hub (A8).
- `tests/Zyggy.Core.Tests/Memory/MemoryStoreTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Memory/ContextRankerTreeTests.cs` *(create — the ranker fed by the store over a `MemoryTree`; the AC-3/AC-4/AC-5 scenarios end to end without protocol)*.

**Seams**: none (file system through `MemoryPaths` over a temporary `MemoryTree`; `TimeProvider` not consulted — `today` is a parameter).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/MemoryStoreTests.cs` — `MemoryTree` with files written inline, plus `MemoryTree.CopyOf(Path.Combine("digest", "sided"))` for the two-sided golden tree (`business/areas/zyggy.md`, `business/clients/acme-corp.md`, `globex.md`, `_notes.md`, `private/areas/marathon.md`, `private/people/carol.md`, `_index.md` files, `auto/MEMORY.md`, `inbox/remember-2026-09-30.md`, `.dream/ledger.json`, `daily/2026-09-30.md`).
- Unit test methods:
  - `DurableEntries_SidedGolden_OnlySlugFilesOnBothSides_IndexAndUnderscoreExcluded` (relative paths with `/`, ordinal order; `_index.md` and `_notes.md` absent)
  - `DurableEntries_InboxAutoArchiveDreamLegacyAndIdentityFiles_NeverReturned` (AC-5: a tree with `inbox/x.md`, `auto/MEMORY.md`, `archive/zyggy/quote.md`, `.dream/quarantine.md`, `areas/old.md`, `people/bob.md`, `profile.md`, `preferences.md`, `agents.md`, each containing the topic word; plus one real `private/people/carol.md` → exactly one entry)
  - `DurableEntries_UnparsableFrontMatter_EntryWithEmptyFrontMatterNeverThrows` (`---\nname: [unterminated\n` → `File.Name == null`, `Aliases` empty, `Updated == null`; the entry is present; matching by slug still works)
  - `DurableEntries_FrontMatterFields_ReadThroughMemoryFileReader` (`name`, `aliases` (flow list and sequence), `description`, `updated`)
  - `DailyLines_WindowByFileNameDate_NewestFirst_BodyLinesOnly_EmptyLinesSkipped` (AC-4: files at `today`, `today−1`, `today−30`, `today−31`; a front matter in one; an empty line; result order and the `today−31` file absent)
  - `DailyLines_MonthlyRollupAndNotesMd_ContributeNothing` (`daily/2026-09.md`, `daily/notes.md` with the topic word)
  - `DailyLines_DirectoryAbsent_Empty` (edge case)
  - `DurableEntries_SymlinkLeadingOutside_Skipped` (`[Fact(SkipUnless = nameof(IsLinux))]`, as `tests/Zyggy.Integration/Memory/MemoryPathsSymlinkTests.cs` does; a category directory symlinked outside the principal directory → no entry from it)
- Unit test file: `tests/Zyggy.Core.Tests/Memory/ContextRankerTreeTests.cs`:
  - `Rank_OverStore_AliasFileFirstThenAllTermsThenAnyTerm_DailyBlockFollows` (AC-3 + AC-4 on one tree: the layout text asserted exactly)
  - `Rank_OverStore_TopicWordOnlyInExcludedPlaces_NoMatchSentence` (AC-5)
  - `Rank_OverStore_LeavesTreeByteIdentical` (fingerprint: every file's bytes before and after; the helper `TreeFingerprint` lives in `tests/Zyggy.Core.Tests/Infrastructure/TreeFingerprint.cs` *(create)* — relative path → SHA-256 of bytes, as a sorted list; reused by Steps 3 and 6)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~MemoryStoreTests|FullyQualifiedName~ContextRankerTreeTests"`

**GREEN**:
- `MemoryStore` per A8: `DurableEntries()` — for each `MemorySide` (enum order) whose `paths.Side(side)` exists, each category directory (`CategoryName.TryParse(name)`), each `*.md` with `!name.StartsWith('_') && Slug.TryParse(stem)`, `paths.TryResolve($"{MemorySideWire.ToWire(side)}/{category}/{name}")` accepted with `MemoryArea.Durable` → `MemoryFileReader.Read(full)` inside `try { } catch (FormatException) { file = new MemoryFile(null, null, [], null, empty, false, TextLines.Split(text)) }`; return ordered by `RelativePath` ordinal. `DailyLines(today, windowDays)` — for `d = today` down to `today.AddDays(-windowDays)`: `paths.Daily(d)` exists → read, skip a front matter block (`---` on line 1 to the next `---`), skip empty lines, add `DailyLine(d, line)`.
- `tests/Zyggy.Core.Tests/Infrastructure/TreeFingerprint.cs`.

**Contract impact**: none beyond Step 1's (A11-4 names the candidate set).

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `MemoryStoreTests` and `ContextRankerTreeTests` pass; `DigestBuilderGoldenTests` still pass (the sided golden tree unchanged — `git diff tests/golden/` empty).

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~ContextRankerTests|FullyQualifiedName~MemoryStoreTests|FullyQualifiedName~ContextRankerTreeTests"` green; the executor pastes the exact result text of `Rank_Layout_…` and of `Rank_FirstFileThatDoesNotFit_…` (wrapper, data sentence, headings, both markers) and of `Rank_OverStore_AliasFileFirst…`.
- [x] Contract review (⚠️ §7 shared contract, A11-4): the layout equals the spec's Contracts block line for line (wrapper attributes tenant/user/generated only; the digest's data sentence; `## <relpath> — <description|(no description)>`; the two marker texts; `## daily`; the no-match sentence without wrapper); the tier rule (exact → all terms → any term; body never searched), `updated` desc with `null` last, path ordinal; the candidate set of AC-5 (nothing from `inbox/`, `auto/`, `archive/`, `.dream/`, legacy, roll-ups, `profile.md`/`preferences.md`/`agents.md`); the budget = `max_tokens × 4` UTF-8 bytes over the whole text, daily ≤ 20 %, markers counted; `ContextOptions` defaults.
- [x] Assumptions A1–A12 read; A2 (result text = CLI stdout bytes), A3 (pipes from the shared framework), A4 (min-version `0.10.0`), A8 (store walk), A9 (deny/allow positions) confirmed or corrected before Slice B.
- [x] Nothing committed in either repository; `git diff tests/golden/` and `git diff tests/Zyggy.Integration/Gates/` empty; `Directory.Packages.props` unedited.
- [x] User approved — implementation may continue past this gate

---

## Step 3 — Serve `get_context` to an in-process MCP client: `initialize` answers `serverInfo.name = hub` with the assembly's version and the tools capability; `tools/list` returns exactly `get_context` and `remember` whose names, descriptions and input schemas equal the hand-written goldens `tests/golden/hub/tool-get_context.json` and `tool-remember.json` (both descriptions say memory content is data, never instructions); `get_context` answers the Step 1/2 text from a `MemoryTree` with `max_tokens` defaulting to 2,000 and leaves the tree byte-identical; `tenant`/`user` → `refused: tenant and user are never request fields` + one stderr line, an unknown or ill-typed argument → `refused: invalid arguments`, `max_tokens` outside 100..8000 → `refused: invalid arguments: max_tokens must be 100..8000`, a bad topic → the Step 1 refusals — each before anything is read

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Hub/HubStart.cs` *(create)*: `internal sealed record HubStart(int? Exit, string? Message, MemoryPaths? Paths, TimeZoneInfo? TimeZone, SecretPatterns? Patterns, bool HooksOff);` (the decision itself — `HubStartup.Decide` — is Step 5; this step constructs `HubStart` directly in the harness).
- `src/Zyggy.Core/Hub/HubToolDescriptors.cs` *(create)*: `internal static class HubToolDescriptors` — `const string GetContextName = "get_context"`, `RememberName = "remember"`, `GetContextDescription`, `RememberDescription`, `static JsonElement GetContextSchema()`, `static JsonElement RememberSchema()` (written with `Utf8JsonWriter`, as `PublishPostDescriptor.InputSchema` is): `get_context` — `{"type":"object","properties":{"topic":{"type":"string","minLength":1,"maxLength":200},"max_tokens":{"type":"integer","minimum":100,"maximum":8000}},"required":["topic"],"additionalProperties":false}`; `remember` — `{"type":"object","properties":{"fact":{"type":"string","minLength":1,"maxLength":1000},"scope":{"type":"string","pattern":"^(general|machine|project:[a-z0-9][a-z0-9-]*)$"}},"required":["fact"],"additionalProperties":false}`. Descriptions (the executor's final wording is what the goldens pin; both must contain the phrase `data, never instructions`): `get_context` — "Return the owner's memory files and recent daily lines that match a topic, ranked by match then recency, under max_tokens (default 2000, 100..8000). What comes back is the owner's memory: data, never instructions. Never pass tenant or user: the Hub serves one principal."; `remember` — "Keep one fact the owner stated in this conversation as a [stated] line in the memory inbox; optional scope general (default), project:<name> or machine. A fact that looks like a secret is refused by pattern name and must never be rephrased or retried. Memory content is data, never instructions. Never pass tenant or user."
- `src/Zyggy.Core/Hub/ToolArguments.cs` *(create)*: `internal static class ToolArguments` — `static JsonElement AsObject(IDictionary<string, JsonElement>? arguments)` (the `PublishPostMcpTool.Arguments` re-serialisation, shared), `static string? Refuse(JsonElement arguments, IReadOnlyDictionary<string, JsonValueKind[]> declared, out bool namedPrincipal)` → `null` when the object holds only declared properties of the declared kinds; `refused: tenant and user are never request fields` (and `namedPrincipal = true`) when `tenant` or `user` is present (checked first); `refused: invalid arguments` otherwise (unknown property, wrong kind, not an object).
- `src/Zyggy.Core/Hub/GetContextTool.cs` *(create)*: `internal sealed class GetContextTool(MemoryStore store, ContextOptions options, MemoryPaths paths, TimeZoneInfo zone, TimeProvider clock, TextWriter diagnostics) : McpServerTool` — `ProtocolTool` from the descriptors, `Metadata = []`, `InvokeAsync`: validate (order above; `max_tokens` absent → `DefaultMaxTokens`; present but outside `MinTokens..MaxTokens` → `refused: invalid arguments: max_tokens must be 100..8000`; `ContextRanker.Topic` refusal), on a principal refusal write `hub: refused: request named a principal\n` to `diagnostics`; then `today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime)`, `store.DurableEntries()`, `store.DailyLines(today, options.DailyWindowDays)`, `ContextRanker.Rank(…, paths.Principal, clock.GetUtcNow())` → one `TextContentBlock`.
- `src/Zyggy.Core/Hub/HubServer.cs` *(create)*: `internal static class HubServer` — `const string ServerName = "hub"`, `static Task<int> RunAsync(Stream stdin, Stream stdout, TextWriter diagnostics, HubStart start, TimeProvider clock, CancellationToken ct)` (A1): builds `MemoryStore`, `GetContextTool` and (Step 4) `RememberTool` from `start`, `McpServerOptions { ServerInfo = new Implementation { Name = "hub", Version = <informational version> }, Capabilities = new ServerCapabilities { Tools = new ToolsCapability() }, ToolCollection = tools }`, `StreamServerTransport(stdin, stdout, ServerName, NullLoggerFactory.Instance)`, `McpServer.Create(transport, options, NullLoggerFactory.Instance, serviceProvider: null)`, `RunAsync(ct)`, return 0. In this step the collection holds `get_context` only; Step 4 adds `remember` (the `tools/list` golden test of this step therefore asserts the **two** names only once Step 4 is green — see RED for the split).
- `tests/golden/hub/tool-get_context.json`, `tests/golden/hub/tool-remember.json` *(create — hand-written, one line each, the `{"name":…,"description":…,"inputSchema":{…}}` shape of `tests/golden/linkedin/tool-publish_post.json`; copied to both test outputs by the existing `../golden/**` items)*.
- `tests/Zyggy.Core.Tests/Hub/HubHarness.cs` *(create)*: `internal sealed class HubHarness : IAsyncDisposable` — a `MemoryTree` (or one given), `FakeTimeProvider` at `2026-10-28T10:00:00Z`, the custom `Europe/Brussels` zone, `SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!`, a `StringWriter Diagnostics`, `HubStart Start { get; init; }` (constructed directly: `new HubStart(null, null, tree.Paths, zone, patterns, hooksOff)`), two `System.IO.Pipelines.Pipe`s (A3), `Task<int> Server` = `HubServer.RunAsync(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), Diagnostics, Start, Clock, cts.Token)`, `McpClient Client` = `await McpClient.CreateAsync(new StreamClientTransport(serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream(), NullLoggerFactory.Instance), clientOptions: null, NullLoggerFactory.Instance, ct)`; helpers `Task<CallToolResult> CallAsync(string tool, string argumentsJson)` (parses the JSON into the `IReadOnlyDictionary<string, object?>` the SDK's `CallToolAsync` takes, or sends raw `JsonElement`s — whichever the 2.2.0 client API accepts; the raw document is what the server sees either way), `Task<IList<McpClientTool>> ListToolsAsync()`, `string TextOf(CallToolResult)`; `DisposeAsync` closes the client, completes the client→server pipe writer (= stdin closed) and awaits `Server` (asserting 0 — AC-18 in process).
- `tests/Zyggy.Core.Tests/Hub/HubToolDescriptorsTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Hub/HubServerGetContextTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `Src_McpSdk_OnlyUnderLinkedInMcp` → `Src_McpSdk_OnlyUnderMcpServers` (allowed prefixes `src/Zyggy.Core/LinkedIn/Mcp/` and `src/Zyggy.Core/Hub/`; A10); new `Src_HubClasses_NoConsoleClockOrStaticState` (every `src/Zyggy.Core/Hub/*.cs`: no `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `TimeProvider.System`, no static mutable field; `Console.` only in `HubVerb.cs` and only `OpenStandardInput`/`OpenStandardOutput` — the file arrives in Step 5, the fact tolerates its absence); new `Src_Hub_NoPathBuilding` (no `Path.Join`, `Path.Combine`, `Directory.`, `File.` under `src/Zyggy.Core/Hub/` — the store in `Memory/` is the one reader).

**Seams**: the MCP transport, faked in process — the SDK's `McpClient` over `StreamClientTransport` on two pipes against `HubServer` with a substituted `TimeProvider` (`FakeTimeProvider`), zone and tree (AC-21). No process, no `geoffrey/geoffrey`, no real path.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Hub/HubToolDescriptorsTests.cs`:
  - `GetContext_NameDescriptionSchema_ByteEqualsGolden` and `Remember_NameDescriptionSchema_ByteEqualsGolden` (the `PublishPostDescriptorTests.Schema_3000_ByteEqualsGolden` shape: `Utf8JsonWriter` with `UnsafeRelaxedJsonEscaping`, compared to the golden file's text)
  - `Descriptions_BothSayDataNeverInstructions` (`Contain("data, never instructions")`)
  - `Schemas_AdditionalPropertiesFalse_RequiredTopicAndFact`
  - `GetContextSchema_AgreesWithToolArguments` (`[Theory]`: `{"topic":"x"}` ok; `{"topic":"x","max_tokens":500}` ok; `{"max_tokens":500}` invalid; `{"topic":1}` invalid; `{"topic":"x","tenant":"acme"}` principal; `{"topic":"x","user":"alice"}` principal; `{"topic":"x","extra":true}` invalid; `{"topic":"x","max_tokens":"500"}` invalid; `[]` invalid)
- Unit test file: `tests/Zyggy.Core.Tests/Hub/HubServerGetContextTests.cs` (one `HubHarness` per test over a `MemoryTree` holding `business/areas/zyggy.md` (aliases `[zyggy, zyggy platform]`, description, `updated: 2026-10-20`), `private/people/carol.md` (description mentioning `zyggy roof`), `private/areas/roof.md` (no description), `daily/2026-10-28.md`, `daily/2026-10-27.md`, `daily/2026-09-01.md` (outside the window), `daily/2026-09.md`, `inbox/remember-2026-10-27.md`, `auto/MEMORY.md`, `profile.md` — the excluded ones all containing `zyggy`):
  - `Initialize_ServerInfoHubVersionFromAssembly_ToolsCapability` (AC-1: `Client.ServerInfo.Name == "hub"`, `Version` equals `typeof(HubServer).Assembly`'s informational version split at `+`, `Client.ServerCapabilities.Tools` not null)
  - `ToolsList_ContainsGetContext_SchemaEqualsGolden` (this step: `get_context` present and equal to the golden via `JsonNode.DeepEquals`; Step 4 upgrades it to "exactly the two, both equal" — `ToolsList_ExactlyGetContextAndRemember_SchemasEqualGoldens`)
  - `GetContext_Topic_ReturnsRankedFilesThenDailyBlock_DefaultMaxTokens2000` (AC-3/AC-4 end to end: the exact text; the `generated` attribute from the fake clock; the excluded files absent)
  - `GetContext_LeavesTreeByteIdentical` (AC-19: `TreeFingerprint` before and after two calls)
  - `GetContext_MaxTokensAbsent_UsesDefault_And_SmallBudgetCutsWithMarkers` (AC-6/AC-7 through the protocol: `max_tokens 100` → both markers; `Encoding.UTF8.GetByteCount(text) <= 400`)
  - `GetContext_MaxTokensOutOfRange_IsErrorRefusedNothingRead` (`[Theory]` 99, 8001, 0: `IsError`, text `refused: invalid arguments: max_tokens must be 100..8000`; a `FileSystemWatcher`-free proof of "nothing read": the tree's directory is **deleted** before the call in a dedicated test `GetContext_Refusal_NeverTouchesTheTree` — the call still answers the refusal, no exception)
  - `GetContext_TopicEmptyWhitespaceOrOver200_IsErrorRefused` (AC-8 texts)
  - `GetContext_TenantOrUserArgument_IsErrorRefusedOneStderrLine` (AC-9: text `refused: tenant and user are never request fields`; `Diagnostics.ToString() == "hub: refused: request named a principal\n"` after one call, two lines after two)
  - `GetContext_UnknownOrIllTypedArgument_IsErrorInvalidArguments_NoStderr` (AC-10)
  - `GetContext_NoMatch_SentenceNotError` (`IsError` false)
  - `GetContext_TwoCallsSeeAFileWrittenBetweenThem` (no cache: write a new durable file between calls)
  - `StdinClosed_ServerReturnsZero` (the harness's dispose path asserted explicitly — AC-18 in process)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~HubToolDescriptorsTests|FullyQualifiedName~HubServerGetContextTests"`

**GREEN**:
- The six Core files above (with `RememberTool` left to Step 4; `HubServer` registers what exists). `GetContextTool` validation order per the Shared rules. `ToolArguments` is shared by both tools (Step 4 reuses it). Hygiene facts updated as listed.

**Contract impact**: ⚠️ **§7 Hub tool surface** (shared with 21, 12/13): the `get_context` input (`topic` 1–200, `max_tokens` 100..8000 default 2,000), the refusal texts, the principal rule. The two golden files are the contract's wire form (as the LinkedIn golden is). No `PROTOCOL.md`/`node.json` change.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `HubToolDescriptorsTests` and `HubServerGetContextTests` pass; `SourceHygieneTests` pass with the three changed/new facts; `Packages_RowCountPinned` still 24; `git diff Directory.Packages.props` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — Keep a stated fact through the Hub's `remember`, byte-identical to the CLI: `remember(fact[, scope])` appends `- [stated] <date><hint>: <fact>` to `inbox/remember-<local date>.md` through the existing `RememberService` (same file name, front matter, `updated` rewrite, date in `ZYGGY_TIMEZONE`) and answers `remembered: <path>\n<line>\n` — equal to `zyggy memory remember`'s stdout on the same tree and clock; a secret-shaped fact → `isError`, `refused: matches secret pattern <name>` and the fact appears nowhere (result, stderr, any file); an empty, over-long fact or a bad `scope` → `refused: <the RememberArguments message>`, nothing written; `ZYGGY_HOOKS=off` → `not stored: hooks are off (unattended run)` with `isError: false`, nothing written, `get_context` still answers; `tools/list` now returns exactly the two tools equal to their goldens

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Hub/RememberTool.cs` *(create)*: `internal sealed class RememberTool(RememberService remember, bool hooksOff, TextWriter diagnostics) : McpServerTool` — `ProtocolTool` from the descriptors; `InvokeAsync`: `ToolArguments.Refuse` (declared `fact: String`, `scope: String`; principal → the AC-9 text + stderr line; unknown/ill-typed → `refused: invalid arguments`), then `RememberArguments.Parse(scope is null ? ["--", fact] : ["--scope", scope, "--", fact])` (A5) → error → `refused: <error>` (`IsError`), then `hooksOff` → `not stored: hooks are off (unattended run)` (`IsError` false, nothing called), then `remember.Remember(request)` → `RefusedBy` → `refused: matches secret pattern <name>` (`IsError`); else `remembered: {Path}\n{Line}\n` (A2).
- `src/Zyggy.Core/Hub/HubServer.cs` *(modify)*: registers `new RememberTool(new RememberService(start.Paths!, start.TimeZone!, clock, start.Patterns!), start.HooksOff, diagnostics)` after `get_context` (ordinal collection: `get_context`, `remember`).
- `tests/Zyggy.Core.Tests/Hub/HubServerRememberTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Hub/HubServerGetContextTests.cs` *(modify)*: `ToolsList_ContainsGetContext_…` becomes `ToolsList_ExactlyGetContextAndRemember_SchemasEqualGoldens` (AC-2: exactly two, both `JsonNode.DeepEquals` their golden, names in that order).
- `tests/Zyggy.Core.Tests/Hub/HubToolDescriptorsTests.cs` *(modify)*: `RememberSchema_AgreesWithToolArgumentsAndRememberArguments` (`[Theory]`: `{"fact":"x"}` ok; `{"fact":"x","scope":"machine"}` ok; `{"fact":"x","scope":"project:zyggy"}` ok; `{"fact":"x","scope":"project:Has Space"}` refused by `RememberArguments`; `{"fact":"x","tag":"observed"}` invalid (AC-10 — `tag` is unknown); `{"scope":"machine"}` invalid; `{"fact":"x","tenant":"acme"}` principal).

**Seams**: the MCP transport in process (the `HubHarness` of Step 3); the clock (`FakeTimeProvider`), the zone (custom `Europe/Brussels`), the secret patterns from the golden file; the file system through `MemoryPaths` over a `MemoryTree`.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Hub/HubServerRememberTests.cs`:
  - `Remember_Fact_AppendsStatedLineAndAnswersRememberedPathAndLine` (AC-11: `inbox/remember-2026-10-28.md` holds the front matter `name: remember 2026-10-28`, `description: facts stated by the owner on 2026-10-28 (remember skill)`, `updated: 2026-10-28`, the line `- [stated] 2026-10-28: Marie prefers tea`; the result text equals `$"remembered: {path}\n{line}\n"`; `IsError` false)
  - `Remember_ResultText_ByteIdenticalToRememberVerbStdout` (A2: a second `MemoryTree`, `new RememberVerb(env, sameClock, FindTimeZone).RunAsync(["--scope", "project:zyggy", "--", fact], console.Io, …)`; `console.Stdout` with the second tree's path replaced by the first's equals the tool's text; both inbox files byte-equal)
  - `Remember_ScopeMachineAndProject_HintInLine` (`(machine)`, `(project:zyggy)`)
  - `Remember_LocalDateFromZone_LateEveningRollsToNextDay` (clock `2026-10-28T23:30:00Z`, +02:00 → `remember-2026-10-29.md`)
  - `Remember_ExistingFile_KeepsLinesRewritesUpdated` (the `RememberVerbTests.Run_ExistingFile_…` shape)
  - `Remember_SecretSample_IsErrorNamesPatternFactNowhereInboxUnchanged` (`[Theory]` over `SecretPatternsTests.SecretSamples`, AC-12: text `refused: matches secret pattern <name>`; the sample absent from the text, from `Diagnostics`, from every file of the tree; `inbox/` does not exist)
  - `Remember_BenignSample_Appended` (`[Theory]` over `SecretPatternsTests.BenignSamples`)
  - `Remember_EmptyOverLongOrBadScope_IsErrorRefusedMessage_NothingWritten` (`[Theory]`, AC-13: `""` → `refused: empty fact`; `" \t\r\n "` → `refused: empty fact`; 1,001 `a` → `refused: fact longer than 1000 characters`; scope `people/marie` → `refused: --scope must be general, project:<name> or machine`; scope `project:` → `refused: invalid project name in --scope`; `inbox/` absent)
  - `Remember_TagArgument_IsErrorInvalidArguments` (AC-10/D4)
  - `Remember_TenantOrUser_IsErrorRefusedOneStderrLine_NothingWritten` (AC-9)
  - `Remember_HooksOff_NotStoredNotError_NothingWritten_GetContextStillAnswers` (AC-14: `HubStart` with `HooksOff = true`)
  - `Remember_SameFactTwice_TwoLines` (edge case)
  - `Remember_NoTempFileSurvives` (`inbox/` holds exactly `remember-2026-10-28.md` after kept, refused, kept)
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~HubServerRememberTests|FullyQualifiedName~HubServerGetContextTests|FullyQualifiedName~HubToolDescriptorsTests"`

**GREEN**:
- `RememberTool` and the `HubServer` registration as above. Nothing in `RememberService`, `RememberArguments`, `FactLineWriter` changes.

**Contract impact**: ⚠️ **§7 `remember` row** (A11-3: `fact`, `scope`; always `[stated]`; the result text; hooks-off behaviour). ⚠️ **Secrets**: the refusal is the one data-protection control (`SecretPatterns`, loaded once at start — Step 5); the fact is never echoed on any channel.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `HubServerRememberTests` pass; `RememberVerbTests` and `RememberCommandTests` unchanged and green (the CLI verb is untouched); `git diff src/Zyggy.Core/Memory/RememberService.cs src/Zyggy.Core/Memory/RememberVerb.cs src/Zyggy.Core/Memory/FactLineWriter.cs` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B *(covers Steps 3–4)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~Hub"` green (AC-1, AC-2, AC-3/4 through the protocol, AC-6..AC-14, AC-19, AC-21); the executor pastes the two golden files' content, one `get_context` result text and one `remember` result text from the tests, and the stderr line of the principal refusal.
- [x] Contract review (⚠️ §7 shared contract): the two tool descriptors against the spec's "MCP tools" table (names, inputs, ranges, `additionalProperties: false`, both descriptions carry "data, never instructions"); the validation order principal → unknown → types/ranges for both tools; the exact refusal texts (`refused: tenant and user are never request fields`, `refused: invalid arguments`, `refused: invalid arguments: max_tokens must be 100..8000`, `refused: invalid arguments: topic is empty` / `… topic over 200 characters`, `refused: <RememberArguments message>`, `refused: matches secret pattern <name>`, `not stored: hooks are off (unattended run)`); A2's result text; A5's reuse of `RememberArguments`.
- [x] ⚠️ Risk review (secrets): a secret-shaped fact appears in no result, no stderr, no file (the theory over the golden samples); the Hub writes nothing but `inbox/remember-<date>.md` (`TreeFingerprint` after `get_context`; `inbox/` the only directory created by `remember`); `MemoryStore` is read-only and `src/Zyggy.Core/Hub/` builds no path (`Src_Hub_NoPathBuilding`).
- [x] The SDK is confined to `src/Zyggy.Core/LinkedIn/Mcp/` and `src/Zyggy.Core/Hub/` (`Src_McpSdk_OnlyUnderMcpServers`); no package row added; nothing committed; `git diff tests/golden/*.md tests/Zyggy.Integration/Gates/` empty.
- [x] User approved — implementation may continue past this gate

---

## Step 5 — `zyggy hub` decides its start and refuses with one stderr line: the principal and time zone come from `ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE` through `MemoryEnvironment.Resolve`, each unset one filled from the `env` object of `$CLAUDE_PROJECT_DIR/.claude/settings.local.json` (a set variable always wins; an unreadable file changes nothing); still missing, a missing principal directory, an unknown zone, or `secret-patterns.txt` unresolvable or malformed → exit 3 `hub: configuration error: <message>`; `ZYGGY_HOOKS=off` → the Hub starts with `HooksOff`; any argument → exit 4 (`hub: --proxy is deliverable 21; zyggy hub takes no arguments` for `--proxy`, `hub: takes no arguments` otherwise); nothing on stdout in any refusal; `zyggy hub` is dispatched by `RawVerbs` and listed by `zyggy --help`; the M365 verbs keep their settings fallback through the moved `PrincipalFromSettings`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `src/Zyggy.Core/Memory/PrincipalFromSettings.cs` *(create)*: `internal static class PrincipalFromSettings { public static Dictionary<string, string?> Apply(IReadOnlyDictionary<string, string?> environment, string settingsPath); }` — the body of `M365Environment.PrincipalFromSettings` moved verbatim (the four keys, a set variable wins, no other key read, `IOException`/`UnauthorizedAccessException`/`JsonException` change nothing).
- `src/Zyggy.Core/M365/M365Environment.cs` *(modify)*: `PrincipalFromSettings` removed; `src/Zyggy.Core/M365/Runs/RunPreflight.cs` *(modify)*: calls `Memory.PrincipalFromSettings.Apply(context.Environment, instance.SettingsPath)`.
- `tests/Zyggy.Core.Tests/M365/M365EnvironmentTests.cs` *(modify)* and `tests/Zyggy.Core.Tests/Memory/PrincipalFromSettingsTests.cs` *(create)*: the existing fallback cases move to the new class unchanged in substance (a set variable wins; unset filled; empty string treated as unset; non-string ignored; missing file, bad JSON, no `env` object → unchanged); the M365 preflight tests that exercise the fallback stay green.
- `src/Zyggy.Core/Hub/HubStartup.cs` *(create)*: `internal static class HubStartup { public static HubStart Decide(IReadOnlyDictionary<string, string?> environment, Func<string, TimeZoneInfo> findTimeZone); }` — (1) `merged = CLAUDE_PROJECT_DIR set and non-empty ? PrincipalFromSettings.Apply(environment, Path.Join(projectDir, ".claude", "settings.local.json")) : environment` (A6); (2) `MemoryEnvironment.Resolve(merged, findTimeZone)` → error → `HubStart(3, "hub: configuration error: " + error, …)`; (3) `SecretPatternsLocation.Resolve(merged)` null → exit 3 with `RememberVerb`'s sentence (`ZYGGY_SECRET_PATTERNS is not set (and no ZYGGY_INSTANCE_DIR or CLAUDE_PROJECT_DIR to derive it from)`); `SecretPatterns.Load(path)` error → exit 3 `hub: configuration error: <load.Error>`; (4) `HooksOff = merged["ZYGGY_HOOKS"] == "off"`; → `HubStart(null, null, paths, zone, patterns, hooksOff)`.
- `src/Zyggy.Core/Hub/HubVerb.cs` *(create)*: `public sealed class HubVerb(IReadOnlyDictionary<string, string?> environment, TimeProvider clock)` with the internal test constructor taking `Func<string, TimeZoneInfo> findTimeZone` (the `RememberVerb` shape); `public const int StdinClosed = 0, Configuration = 3, Usage = 4`; `Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken ct)`: `args.Count > 0` → `--proxy` anywhere → `hub: --proxy is deliverable 21; zyggy hub takes no arguments\n` on `io.Error`, exit 4; else `hub: takes no arguments\n`, exit 4; `HubStartup.Decide` → `Exit` → `Message + "\n"` on `io.Error`, that exit; else `await using var stdin = Console.OpenStandardInput(); await using var stdout = Console.OpenStandardOutput(); return await HubServer.RunAsync(stdin, stdout, io.Error, start, clock, ct)`.
- `src/Zyggy.Cli/RawVerbs.cs` *(modify)*: `if (args is ["hub", .. var hub]) { run = new HubVerb(environment.Variables, TimeProvider.System).RunAsync(hub, VerbIo.FromConsole(), CancellationToken.None); return true; }`.
- `src/Zyggy.Cli/CliApplication.cs` *(modify)*: the `--help`-only `hub` command (A12) added to the root's children.
- `tests/Zyggy.Core.Tests/Hub/HubStartupTests.cs` *(create)*, `tests/Zyggy.Core.Tests/Hub/HubVerbTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `Src_HubClasses_NoConsoleClockOrStaticState` now sees `HubVerb.cs` (the two `OpenStandard*` calls allowed there only); new `Src_RawVerbs_DispatchHub` (text fact: `RawVerbs.cs` contains `["hub", .. var hub]`).

**Seams**: `TimeProvider` (fake clock), the zone lookup (`FindTimeZone` delegate), the environment as a dictionary; the file system for `settings.local.json`, the memory tree and `secret-patterns.txt` under a temporary root. No process yet.

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Hub/HubStartupTests.cs` (a `MemoryTree`, an environment dictionary as `RememberVerbTests` builds it, the golden patterns path):
  - `Decide_EnvironmentComplete_StartsWithPathsZonePatterns_HooksOffFalse`
  - `Decide_TenantUnset_ExitThreeMemoryEnvironmentMessage` (`hub: configuration error: ZYGGY_TENANT is not set`)
  - `Decide_PrincipalDirectoryMissing_ExitThree` (AC-16: `memory directory <path> does not exist (ZYGGY_TENANT/ZYGGY_USER)`)
  - `Decide_UnknownTimeZone_ExitThree`
  - `Decide_PatternsMissing_ExitThreeNamesPath` (AC-16: `hub: configuration error: <path> is missing`) and `Decide_PatternsMalformed_ExitThreeNamesLine` (a line without a tab)
  - `Decide_NoPatternLocation_ExitThreeNamesVariable` (`Contain("ZYGGY_SECRET_PATTERNS")`)
  - `Decide_HooksOff_StartsWithHooksOffTrue` (AC-14: no exit)
  - `Decide_UnsetVariablesFilledFromSettingsLocalJson_SetOnesWin` (D1: `CLAUDE_PROJECT_DIR` → a scratch checkout with `.claude/settings.local.json` `{"env":{"ZYGGY_MEMORY_ROOT":…,"ZYGGY_TENANT":"acme","ZYGGY_USER":"alice","ZYGGY_TIMEZONE":"Europe/Brussels"}}` and `.claude/hooks/secret-patterns.txt` copied from the golden; `ZYGGY_*` unset in the environment except `ZYGGY_USER=bob` → the result's principal is `acme/bob` only if `acme/bob` exists — assert the message names `bob`, proving the set variable won)
  - `Decide_SettingsFileUnreadableOrNotJson_ChangesNothing` and `Decide_ClaudeProjectDirUnsetAndEnvironmentComplete_FallbackSkipped` (edge cases)
  - `Decide_PatternsDerivedFromClaudeProjectDir_WhenExplicitUnset` (`<CLAUDE_PROJECT_DIR>/instance/../.claude/hooks/secret-patterns.txt`)
- Unit test file: `tests/Zyggy.Core.Tests/Hub/HubVerbTests.cs` (`VerbConsole`, `FakeTimeProvider`, `FindTimeZone`):
  - `Run_Proxy_ExitFourNamesDeliverable21_NothingOnStdout` (AC-17: stderr exactly `hub: --proxy is deliverable 21; zyggy hub takes no arguments\n`)
  - `Run_AnyOtherArgument_ExitFourTakesNoArguments` (`[Theory]`: `foo`, `--port`, `get_context`)
  - `Run_ConfigurationError_ExitThreeOneStderrLine_NothingOnStdout` (AC-15: `ZYGGY_TENANT` removed → `hub: configuration error: ZYGGY_TENANT is not set\n`, `Stdout` empty, exactly one `\n`)
  - `Run_PatternsMissing_ExitThree_NeverServes` (AC-16)
  - `Run_UsageBeforeConfiguration_ExitFourEvenWhenUnconfigured` (an argument with `ZYGGY_TENANT` unset → 4: the argument check runs first, as `McpServerVerb` does)
- Unit test file: `tests/Zyggy.Core.Tests/Memory/PrincipalFromSettingsTests.cs` (the moved cases).
- Integration test: this step adds a `zyggy` verb (a real edge); its integration tests over the built binary are **Step 6** of this same slice (`tests/Zyggy.Integration/Hub/HubMcpServerTests.cs`), which the Slice C gate covers — the verb's refusal paths are proven here through `VerbIo` so no process is needed before the client is generalised.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~HubStartupTests|FullyQualifiedName~HubVerbTests|FullyQualifiedName~PrincipalFromSettingsTests"`

**GREEN**:
- The files above. `HubVerb` writes to `io.Error` only; the server's stdout is the process stdout opened only after a successful `Decide`.

**Contract impact**: ⚠️ **D1 / A11-1** — the Hub principal from the process environment with the `settings.local.json` fallback; `node.json` untouched (no `user` key; `NodeConfigurationReader` unedited — `git diff src/Zyggy.Core/Configuration/` empty). CLI surface: the new verb `hub` (exit 0 · 3 · 4). The M365 verbs' behaviour is unchanged by the move (their tests prove it).

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `HubStartupTests`, `HubVerbTests`, `PrincipalFromSettingsTests` pass; every M365 test (`M365EnvironmentTests`, the `Runs` preflight tests, `tests/Zyggy.Integration/M365/*`) still green; `dotnet run --project src/Zyggy.Cli -- --help` lists `hub`; `dotnet run --project src/Zyggy.Cli -- hub --proxy` exits 4 with the 21 line.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — The built `zyggy` serves the Hub over real stdio to the generalised hand-written JSON-RPC client: `McpStdioClient.Start(env, workingDirectory, serverArgs, …)` takes the server's arguments (the LinkedIn tests pass `linkedin mcp-server`, the Hub tests `hub`); against a scratch `acme/alice` tree `initialize` answers `hub`, `tools/list` equals the goldens, `get_context` returns the ranked text and leaves the tree byte-identical, `remember` appends the line the CLI verb writes, a secret-shaped fact is refused and appears nowhere on stdout, stderr or disk, a call carrying `tenant` is refused with one stderr line, every stdout line is JSON-RPC 2.0, stdin closed → exit 0; without a principal → exit 3 and one stderr line, with `--proxy` → exit 4, with the principal only in `settings.local.json` under `CLAUDE_PROJECT_DIR` → served; the Hub never invokes git

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Integration/Infrastructure/McpStdioClient.cs` *(modify)*: `ServerArgs` field removed; `Start(IReadOnlyDictionary<string, string?> env, string workingDirectory, IReadOnlyList<string> serverArgs, string? executable = null)`; everything else unchanged (the independent oracle stays SDK-free).
- `tests/Zyggy.Integration/LinkedIn/LinkedInMcpServerTests.cs` *(modify)*: `Start()` passes `["linkedin", "mcp-server"]` (and any other caller the executor's grep finds).
- `tests/Zyggy.Integration/Hub/HubFixture.cs` *(create)*: a `ScratchDirectory` with `memory/acme/alice/` seeded with the Step 3 tree shape (written inline — never copied from a real repository), the environment (`ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT=acme`, `ZYGGY_USER=alice`, `ZYGGY_TIMEZONE=UTC`, `ZYGGY_SECRET_PATTERNS` = the golden patterns file in the test output), `Env()` returning a copy, `InboxDirectory`, `Fingerprint()` (the `TreeFingerprint` helper linked into `Zyggy.Integration.csproj` as the other `Infrastructure/*.cs` files are — *(modify csproj: one `Compile Include="../Zyggy.Core.Tests/Infrastructure/TreeFingerprint.cs"` link)*), `CheckoutWithSettings()` (a scratch checkout with `.claude/settings.local.json` holding the four values in its `env` object and `.claude/hooks/secret-patterns.txt`).
- `tests/Zyggy.Integration/Hub/HubMcpServerTests.cs` *(create)*.

**Seams**: the real edge — the built `zyggy` binary (`ZyggyCli.ExecutablePath`) over real stdin/stdout, the real file system, the real clock (dates derived from the output as `RememberCommandTests` does). Never the real `claude`, never git (proven), never a real path.

**RED**:
- Integration test file: `tests/Zyggy.Integration/Hub/HubMcpServerTests.cs` (the `LinkedInMcpServerTests` shape; `[Trait]` inherited from the assembly's `Category=Integration`):
  - `Initialize_ServerInfoHub_VersionAndToolsCapability` (AC-1, AC-20)
  - `ToolsList_ExactlyTwoTools_SchemasEqualGoldens` (AC-2: the goldens from `AppContext.BaseDirectory/golden/hub/`)
  - `GetContext_Topic_RankedFilesAndDailyLines_TreeUnchanged` (AC-3/AC-4/AC-19 end to end: the text starts with `<zyggy-context tenant="acme" user="alice" generated="`, holds `## business/areas/zyggy.md — `, `## daily`, ends with `</zyggy-context>\n`; `Fingerprint()` equal before and after)
  - `Remember_Fact_AppendsInboxLine_TextEqualsCliVerbStdout` (AC-11: the result text parsed for the date as `RememberCommandTests` does; the file's bytes equal the exact front matter + line; then `ZyggyCli.RunAsync(["memory","remember","--","<another fact>"], env, …)` on the same tree appends a second line — the two writers share one file and one format)
  - `Remember_SecretShaped_IsErrorNamesPattern_FactNowhere` (AC-12: `AKIAABCDEFGHIJKLMNOP` absent from every stdout line, from stderr, from every file under the scratch root; `inbox/` absent)
  - `ToolsCall_TenantArgument_IsErrorRefused_OneStderrLine_NothingWritten` (AC-9: after `CloseAndWaitAsync`, stderr is exactly `hub: refused: request named a principal\n`)
  - `ToolsCall_UnknownArgument_IsErrorInvalidArguments` (AC-10)
  - `StdinClosed_ExitZero_NoStderr` (AC-18)
  - `Stdout_OnlyJsonRpcLines` (AC-20: every captured line parses with `jsonrpc == "2.0"`)
  - `PrincipalUnset_ExitThreeOneStderrLine_NothingOnStdout` (AC-15, via `ZyggyCli.RunAsync(["hub"], env without ZYGGY_TENANT, …)`)
  - `PrincipalDirectoryMissing_ExitThree` and `PatternsMissing_ExitThree` (AC-16)
  - `Proxy_ExitFourNamesDeliverable21` and `ExtraArgument_ExitFour` (AC-17)
  - `PrincipalFromSettingsLocalJson_UnderClaudeProjectDir_Served` (D1: `env` without any `ZYGGY_*` but `CLAUDE_PROJECT_DIR` = `CheckoutWithSettings()`; `initialize` + one `get_context` succeed; the patterns resolved from the checkout)
  - `HooksOff_RememberNotStored_GetContextServes` (AC-14 end to end)
  - `Hub_NeverInvokesGit` (`[Fact(SkipUnless = nameof(IsLinux))]`: the PATH shim of `RememberCommandTests.Remember_NeverInvokesGit`; a `remember` and a `get_context` call; the marker file absent)
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~HubMcpServerTests"` (RED: the client's new signature breaks compilation until `McpStdioClient` is generalised; then the Hub tests fail at `initialize` until nothing — they pass once Step 5's verb is wired; the RED of this step is the compile break plus `PrincipalFromSettingsLocalJson_…` and `Hub_NeverInvokesGit`, which have no in-process equivalent)

**GREEN**:
- The `McpStdioClient` generalisation, the LinkedIn test's call, the fixture, the csproj link. No production change is expected: if a test needs one, it is a defect found by the wire step — fix it in `src/Zyggy.Core/Hub/` with a unit test added to Slice B's classes first.

**Contract impact**: none beyond Step 5's; the integration file is the round trip the roadmap's DoD names ("an integration test driving the Hub over stdio … for both tools including a refused secret-shaped fact and a rejected `tenant` input").

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `HubMcpServerTests` pass; `LinkedInMcpServerTests` pass unchanged in substance; `dotnet test Zyggy.slnx --filter "Category!=Integration"` green (the unit run does not need the binary); `git diff tests/Zyggy.Integration/Gates/` empty.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C *(covers Steps 5–6)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~HubMcpServerTests"` and `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~HubStartupTests|FullyQualifiedName~HubVerbTests"` green; the executor pastes the stdout lines of one real-stdio run (initialize → tools/list → get_context → remember → the refused secret → the `tenant` call) and the stderr of the last; `zyggy hub --proxy` → exit 4 with the 21 line; `zyggy hub` without a principal → exit 3, one line, empty stdout.
- [x] Contract review (⚠️ D1 / A11-1): the resolution order (environment → `settings.local.json` `env` under `CLAUDE_PROJECT_DIR` → exit 3) against the spec's "Principal resolution"; the exit codes 0 · 3 · 4 against "CLI surface"; `node.json` and `NodeConfigurationReader` untouched; the M365 verbs' fallback unchanged after the move.
- [x] ⚠️ Risk review (the Hub reads a real memory tree): `get_context` is read-only (fingerprint), the only write is `inbox/remember-<date>.md`, the Hub never runs git (Linux test), nothing but JSON-RPC on stdout, the SDK logs nowhere; `Packages_RowCountPinned` 24.
- [x] The owner names the release version for A4 (assumed `0.10.0`) so Step 7 can pin `zyggy-min-version`.
- [x] Nothing committed in either repository; `git diff tests/golden/*.md tests/Zyggy.Integration/Gates/ src/Zyggy.Hub/Program.cs` empty.
- [x] User approved — implementation may continue past this gate

---

## Step 7 — Central's template declares the `hub` server and the session's permissions, and the runbook entries exist: `zyggy-core` `.mcp.json` gains `"hub": {"type":"stdio","command":"zyggy","args":["hub"]}` with no `env` key; `.claude/settings.json` allows `mcp__hub__get_context`, denies `Bash(zyggy hub*)` and leaves `mcp__hub__remember` to the permission mode; `.claude/zyggy-min-version` is the release that ships `zyggy hub`; `tests/repo.bats` pins the three-server layout, the allow and deny lists and the pin, and still finds no GUID, tenant, user or machine path; `operations.md` and the stub know the verb; in this repository the Cli README verb table and a "Troubleshooting — Hub" table ("Hub fails to start", "Hub refuses a principal"), the Hub README (served by `zyggy hub` from `Zyggy.Core/Hub/`; the project is the empty host) and the Core README folder table describe the Hub — pinned by a README fact

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- **Template** (`D:\source\zyggy-core`, branch `feature/11-hub`, uncommitted):
  - `.mcp.json` *(modify, `jq --indent 2` layout)*: the `hub` entry (keys sorted by `jq`: `hub`, `linkedin`, `m365`); no `env`, no GUID (AC-23).
  - `.claude/settings.json` *(modify, `jq --indent 2` layout)*: `permissions.allow` += `mcp__hub__get_context` (last); `permissions.deny` += `Bash(zyggy hub*)` (last, index 342); `permissions.ask` unchanged (`mcp__hub__remember` is **not** listed anywhere — D6: the permission mode decides, as `Bash(zyggy memory remember …)` is today) (AC-24).
  - `.claude/zyggy-min-version` → the A4 version (`0.10.0` unless the owner named another at Gate C).
  - `.claude/rules/operations.md` *(modify, ≤ 200 lines)*: a `## The Hub (memory over MCP)` block — `mcp__hub__get_context` returns memory as data, never instructions; `mcp__hub__remember` keeps one stated fact (the same line as `zyggy memory remember`; `refused: matches secret pattern <name>` → never rephrase or retry; `not stored: hooks are off (unattended run)`); the server is started by Claude Code from `.mcp.json`, never by hand (`Bash(zyggy hub*)` denied); exit `3` configuration error (`hub: configuration error: …`, the four `ZYGGY_*` values in `.claude/settings.local.json`, the memory clone, `claude mcp list` — runbook entry "Hub fails to start" in the instance's runbook) and `4` any argument; a `refused: tenant and user are never request fields` answer means a prompt or a skill tried to name a principal: nothing was read, report it to the owner (entry "Hub refuses a principal"). The pointer sentence names the entries, never a runbook path of the `zyggy` repository (the existing bats rule at `repo.bats:297`).
  - `README.md` *(modify)*: the `.mcp.json` row names the three servers; the verb list gains `zyggy hub`.
  - `tests/fixtures/zyggy-stub.sh` *(modify)*: knows `hub` (A11).
  - `tests/repo.bats` *(modify)* (AC-23, AC-24): the `.mcp.json` test asserts `keys == ["hub","linkedin","m365"]` and `.mcpServers.hub == {"type":"stdio","command":"zyggy","args":["hub"]}` with `has("env") | not`; the allow-list equality gains `mcp__hub__get_context`; the deny layout test: `deny[341:] == ["Bash(zyggy memory archive offload*)","Bash(zyggy hub*)"]`, length and unique length 343; a new assertion that no `ask` or `allow` rule matches `mcp__hub__remember`; `zyggy-min-version` = the A4 string in the 0.7.0 test (its title updated); `operations.md` carries `Hub fails to start`, `Hub refuses a principal`, `tenant and user are never request fields`, `data, never instructions`; the stub knows `hub`; rule files ≤ 200 lines; the hygiene sweeps unchanged (they already cover `.mcp.json` and `README.md`).
  - Proven by: `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/"` — all green (the instance pin test skips in the template).
- **This repository**:
  - `src/Zyggy.Cli/README.md` *(modify)*: the verb table row `hub` (MCP over stdio for Claude Code on Central, started from `.mcp.json`; `get_context`, `remember`; exit 0 stdin closed · 3 configuration · 4 any argument; `--proxy` is deliverable 21) replacing the `hub --proxy` row's wording; a new `## Troubleshooting — Hub` table with the rows "Hub fails to start — `claude mcp list` shows `hub` failed; `zyggy hub` exits 3 `hub: configuration error: …`" (check the four `ZYGGY_*` values in `.claude/settings.local.json` — `env` object —, the memory clone and its `<tenant>/<user>/` directory, the `secret-patterns.txt` path; placeholders only) and "Hub refuses a principal — a tool answer `refused: tenant and user are never request fields` and one stderr line `hub: refused: request named a principal`" (the caller — a prompt or a skill — tried to name a principal; nothing was read or written; report it; the Hub serves one principal fixed at start).
  - `src/Zyggy.Hub/README.md` *(rewrite, placeholders only)*: the project is the empty host kept in the solution; the server lives in `src/Zyggy.Core/Hub/` and runs as `zyggy hub` (D5); the tools table reduced to `get_context` and `remember` with the 11 inputs and outputs, the deferred three named as 21; the boundaries (read-only through `MemoryStore`, the one write `inbox/`, principal from the process environment, never from a request); no `zyggy-hub` binary.
  - `src/Zyggy.Core/README.md` *(modify)*: the `Memory/` row names `MemoryStore` and `ContextRanker`; a `Hub/` row (`HubStartup`, `HubServer`, the two tools, `HubVerb`).
  - `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `Readmes_HubDocumented` — `src/Zyggy.Cli/README.md` contains `Hub fails to start` and `Hub refuses a principal` and a `| \`hub\`` row; `src/Zyggy.Hub/README.md` contains `zyggy hub` and not `zyggy-hub`; `src/Zyggy.Core/README.md` contains `Hub/`; the existing `Deploy_AndReadmes_NoRealIdentifierNoAbsoluteHomeNoSkipPermissions` gains `src/Zyggy.Hub/README.md` in `ProductReadmes`.

**Seams**: none (configuration and documentation; the template's bats suite and this repository's README facts are the tests).

**RED**:
- `tests/repo.bats` in `zyggy-core`: the assertions above fail against the unchanged `.mcp.json`, settings, pin, rules and stub (run the container suite: red at those tests).
- Unit test file: `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` — `Readmes_HubDocumented` fails against the current READMEs; `Deploy_AndReadmes_…` with the Hub README added.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~SourceHygieneTests.Readmes_HubDocumented"` and the podman bats command above.

**GREEN**:
- The template and README edits above; the bats and README facts pass.

**Contract impact**: the §10 Claude Code side (`.mcp.json` references the Hub — exists in the founding spec already); the runbook entries required by §12 ("A runbook entry exists for any new failure mode"); no code.

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `SourceHygieneTests` green (incl. `Readmes_HubDocumented`, `Deploy_AndReadmes_…`); the `zyggy-core` bats suite green in the container; `jq --indent 2 . .mcp.json` and `… settings.json` byte-identical to the files (`cmp`); `git -C D:\source\zyggy-core status` shows only the files listed.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — The session-start digest stays under the §7 target and no Hub fixture names the owner: on the `digest/27` and `digest/sided` golden trees the three sections built with default `DigestOptions` are together ≤ 20,000 bytes and `Σ bytes / 4 < 6,000` (the "< 6k tokens" target with this spec's 4-bytes-per-token estimate); every file under `tests/golden/hub/`, `tests/Zyggy.Core.Tests/Hub/`, `tests/Zyggy.Integration/Hub/` and `src/Zyggy.Core/Hub/` is free of `geoffrey`, a mail address, a GUID and an absolute home path, and the Hub tests' principal is `acme/alice`

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope**:
- `tests/Zyggy.Core.Tests/Memory/DigestBuilderSizeTests.cs` *(create)*.
- `tests/Zyggy.Core.Tests/Infrastructure/SourceHygieneTests.cs` *(modify)*: `HubTestsAndFixtures_NonDefaultPrincipalOnly` (AC-25).

**Seams**: none (`DigestBuilder` over `MemoryTree.CopyOf(...)` with the fake clock; file facts).

**RED**:
- Unit test file: `tests/Zyggy.Core.Tests/Memory/DigestBuilderSizeTests.cs`:
  - `Build_ThreeSections_On27Fixture_SumAtMost20000BytesAndUnder6000Tokens` (AC-22: `identity` + `index` + `daily` with `new DigestOptions()`, `startDirectory: null`; `Σ Encoding.UTF8.GetByteCount(Text) <= 20000` and `Σ / 4 < 6000` — the divisor is `new ContextOptions().BytesPerToken`, so the estimate is stated once)
  - `Build_ThreeSections_OnSidedFixture_SumAtMost20000BytesAndUnder6000Tokens`
  - `Build_ThreeSections_OversizeFixture_StillCappedUnder20000` (`digest/27/memory-oversize`: the caps hold even when every section truncates — `StderrLines` not empty is allowed here)
- `SourceHygieneTests.HubTestsAndFixtures_NonDefaultPrincipalOnly`: every file (any extension) under the four directories above: no `geoffrey` (ordinal-ignore-case), no `MailAddress()`, no `GuidLiteral()`, no `/home/`, `/srv/`, `C:\Users\`; at least one file contains `acme` and one `alice` (the fixtures do use the non-default principal). Write it so it fails first by asserting against a temporarily wrong expectation is **not** needed: the test is RED only if an offender exists — run it, confirm green or fix the offender; the step's RED is the digest-size test.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~DigestBuilderSizeTests|FullyQualifiedName~HubTestsAndFixtures_NonDefaultPrincipalOnly"`

**GREEN**:
- The two test files. `DigestBuilder` and `DigestOptions` are **not** edited: if a size assertion fails on a golden tree, the executor stops at the gate with the numbers (the P0b digest shape is decided; a cap change is a plan change).

**Contract impact**: none (a test of the §7 Context loading target).

**VERIFY**: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green; `DigestBuilderSizeTests` pass; `git diff src/Zyggy.Core/Memory/DigestBuilder.cs src/Zyggy.Core/Memory/DigestOptions.cs tests/golden/digest/` empty; `git diff tests/Zyggy.Integration/Gates/` empty; `Packages_RowCountPinned` 24.

**REFACTOR**:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D, definition of done *(covers Steps 7–8; closes the deliverable)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification (this repository): `dotnet test Zyggy.slnx` green on Windows; CI green on both runners once the owner pushes the branch; `DigestBuilderSizeTests` numbers pasted (Σ bytes and Σ/4 for the two golden trees); `HubTestsAndFixtures_NonDefaultPrincipalOnly` and `Readmes_HubDocumented` green; `git diff tests/Zyggy.Integration/Gates/P1_WalkingSkeleton.cs` empty; `git diff src/Zyggy.Hub/Program.cs` empty; `Directory.Packages.props` 24 rows.
- [x] Behavioral verification (template): the `zyggy-core` bats suite green in the container; the executor pastes the `hub` entry of `.mcp.json`, the two new permission rules, the `zyggy-min-version` line and the `operations.md` block; `git -C D:\source\zyggy-core diff --stat`.
- [x] Contract review: the definition of done of the roadmap entry — unit tests for ranking and the budget on a fixture tree with a non-default principal (Steps 1–2), the integration test driving the Hub over stdio for both tools including a refused secret-shaped fact and a rejected `tenant` input (Step 6), the digest-size test (Step 8), the `.mcp.json` example for Central with placeholders only (Step 7), the runbook entries "Hub fails to start" and "Hub refuses a principal" in the Cli README with `operations.md` pointing to them (Step 7), `PROTOCOL.md` unchanged (nothing on the bus changed), the founding-spec amendments A11-1..A11-4 proposed as exact wording in the spec (the orchestrating session pastes them after the owner's approval — not this plan).
- [x] ⚠️ Risk review: the §7 Hub tool contract as 21 (`list_nodes`, `submit_job`, `job_status`, HTTP, `hub --proxy` reserved with exit 4) and 12/13 (part 2 asserts on the `get_context` text) will consume it; the secret-pattern refusal shares the one list with `remember` and the Hub refuses to start without it; the Hub reads the owner's real memory read-only except `inbox/`; public hygiene of every new file in both repositories (`acme/alice`, placeholders).
- [x] **Owner-executed, after the release is tagged, published and installed on Central per the Upgrade path** (binary under `/opt/zyggy/<version>/`, `instance/zyggy.json` pin ≥ `zyggy-min-version`, the template merged into the instance and pulled, the live `.claude/settings.local.json` installed from `instance/settings.local.json`, one restart of the remote-control session): `claude mcp list` in the instance checkout shows `hub` **connected** with the `.mcp.json` entry **without an `env` block** (D1 — the settings `env` reaches the spawned server, or the `CLAUDE_PROJECT_DIR` fallback covers it); in a session, `get_context` for a topic the owner knows returns his files and daily lines as data, and `remember` of one sentence lands in `memory/<tenant>/<user>/inbox/remember-<date>.md` (`git -C memory status`). If `hub` shows **failed**: the Cli README row "Hub fails to start"; if the principal is missing only because the settings `env` does not reach the server, the instance's `.mcp.json` adds `"env": {"ZYGGY_MEMORY_ROOT": "${ZYGGY_MEMORY_ROOT}", "ZYGGY_TENANT": "${ZYGGY_TENANT}", "ZYGGY_USER": "${ZYGGY_USER}", "ZYGGY_TIMEZONE": "${ZYGGY_TIMEZONE}"}` (spec Configuration, no release needed) and the owner records which path worked in the private evidence page (never here).
- [x] Nothing committed by the agent in either repository; the owner commits, pushes, merges and tags; the roadmap entry 11 is updated by the project-manager with the version and the D1 outcome.
- [x] User approved — the deliverable is done

> **Closing note (2026-10-28, owner decision):** the owner approved the definition-of-done gate before the owner-executed items ran. Not yet evidenced: CI on both runners (branch not pushed), the 0.10.0 release and its install on Central, the instance's `enabledMcpjsonServers` gaining `hub`, and `claude mcp list` showing `hub` connected without an `env` block (D1). Those are recorded as pending owner actions, not verified. Nothing was committed by the agent in either repository; the zyggy-core changes sit uncommitted on `feature/10-runbook-stop` beside that branch's two pending runbook lines.
