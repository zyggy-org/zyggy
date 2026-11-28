<!--
Fragment for the project's CLAUDE.md. The setup skill fills it in and merges it there.
The workflow itself is in .claude/rules/spec-driven.md and is not repeated in CLAUDE.md.
-->

## Project settings

The development workflow is in `.claude/rules/spec-driven.md`. The skills and agents read the values below.

<!-- One of the two lines below, as agreed during setup; remove both when the project has no other workflow. -->
In this repository that workflow leads. Do not use <the competing skills, by name> to plan or build here, also when their own instructions say they are mandatory.
<Or: the spec-driven workflow applies to <scope, e.g. features started with /new-feature>; everything else follows <the existing workflow, where it is described>.>

- **Reference feature**: `<the feature whose structure new features mirror>` <!-- or: none yet; the first feature built becomes the reference -->
- **Risk areas** (a human go before code): <auth, personal data, database schema, shared contracts, public API routes and DTOs, file storage>
- **Specs folder**: `_specs/`
- **Protected branches** (never build on them): `<main>`
- **Spec branch**: `<feature/<short-name>>` from `<base branch>`; one per spec, created by Claude when the spec starts, merged through a pull request
- **Commit format**: `<type>(<scope>): <desc>`
- **Guidelines**: <the roles of the installed guidelines, e.g. testing> <!-- only when a pack installs guidelines; the skills find the files by their first line, not by this line -->
- **Models** (recommended since kit 2.6.5): `build-slice` on Sonnet 5.5 (benchmark exp18: the same quality floor as Opus at 3.22 against 5.78 USD); spec writing (`investigate`, `write-spec`, `amend-spec`) and review (`spec-critic`, `spec-reviewer`, the final review) stay on the stronger model, Opus 5.5 <!-- or: the models this project uses -->

### Test levels

- **Outer test** (the integration test first in every slice, in the Integration test project, with only the stubs it needs to compile, and seen failing for the right reason, the behavior missing, before any production code or unit test; an integration test, at least one per slice: at least one integration test that tests the behavior of the slice, more where useful; data-row cases count as tests): <the integration test below that decides whether a slice works, e.g. a Presentation integration test of the page for a browser-only feature, an API integration test for an endpoint>
- **Integration test** (every slice's outer test; mocked view models do not count): <what it goes through in this project. Two examples: for a browser-only feature, a Presentation integration test that enters through the page or component the user uses, not through the view model, rendered on the real DI container with the real view model and the real services, and fakes only the browser edge (file download, JS interop), the clock, auth and the backend client, so the page's own wiring (grid rows, sort, paging) is under test; for an endpoint, the API integration test against the real backend host, e.g. an HTTP call with a real database in a container>
- **Integration test location** (where each slice's integration test class goes, and the slice gate's `--slice <the class>` finds it): <the integration test project or folder, e.g. `src/Test/Integration/<App>.Integration.Tests/`>. For a browser-only feature: a shared presentation test host there, built by the first slice that needs it, that wires the container the way the app's entry point does (the same registrations as `Program.cs`), with only the browser edge (file download, JS interop) and the backend client faked; later slices reuse it. For an endpoint: the API integration test against the real backend host. <!-- or: not established yet; the first slice creates it -->
- **End-to-end browser test** (optional): <the browser test, e.g. Playwright, or: none>; at most once per feature, in the final review, never a slice's outer test
- **UI test** (a component or page test, e.g. bUnit, only for what the integration test cannot observe, such as rendering or button state; a component or page test is written before its code and seen red, like every test; never a slice's outer test: a stub slice's test is the same Presentation integration test with the stub as the faked backend client, not a separate bUnit test): <how a page is tested on a stubbed service, e.g. a component test or a browser test with the API faked; how the stub is switched on for a human check> <!-- or: no user interface -->
- **Unit test** (for each rule or calculation, a unit test seen failing before its code, then the smallest code to pass, then refactor; only the unit tests the change needs: new logic, each rule the spec names; data-row cases count as tests; no tests of helper edge cases that no criterion or named rule asks for): <what it covers and what it replaces with fakes>
- **Human check**: <how the user starts the application and where it answers, e.g. the run command and the local URL>
- **Browser tool**: <what Claude uses to open a page and take a screenshot when it runs a human check, e.g. the Playwright MCP server> <!-- or: none; Claude reports page checks as not carried out -->
- **Criterion ids on tests**: <how a test carries `AC-<n>`, e.g. a trait or category, a tag, or the test name>

### Code analysis

- **Analysis** (run once per feature, in the final review, after the feature gate, on the feature's changed code; never per slice): <the project's analysis commands, e.g. for .NET `dotnet build -warnaserror -v q -nologo` with the built-in analyzers on, then `dotnet format --verify-no-changes`> <!-- or: none; the final review then says "no code analysis declared" --> Plain .NET practice, no server and no account: the analyzers ship with the .NET SDK and run in the build (`EnableNETAnalyzers` and `AnalysisLevel`, e.g. `latest-recommended`, and `EnforceCodeStyleInBuild` in `Directory.Build.props`, the rule severities in `.editorconfig`, `TreatWarningsAsErrors` where the project chooses it). It runs on the machine only and sends no code anywhere.
- **Fix analysis**: <how the findings are fixed, e.g. `dotnet format` for style and the analyzers' code fixes, by hand for the rest> <!-- or: none; the final review fixes them by hand -->
- **Formatter** (what fixes a failed format check in the final review, before the feature gate runs again; the slice gate runs no format check): <e.g. `dotnet format <solution>`> <!-- or: none; the format check's own output says what to fix -->
- **Warnings in the build**: every slice leaves no new warning in the files it touched (compiler and analyzers); a rule is never switched off in `.editorconfig`, `Directory.Build.props` or with a suppression to get a build through. <!-- or: what the build runs instead -->

### Pack values

<!-- Only with a pack installed: keep the lines of the installed packs, remove the others, and without a pack remove this heading. The pack's agents, skills and rules read the values by the label; no placeholder may remain. -->

- **Test projects**: `<each test project's .csproj, in backticks, with forward slashes>` <!-- pack dotnet-shared: every test project of the solution (unit, UI, integration); the mutation check runs Stryker.NET with all of them -->
- **Mutation threshold**: `80` <!-- pack dotnet-shared: the lowest mutation score a slice may end with, in percent; default 80 -->

### Verification Commands

The commands are in `.claude/spec-driven.json` under `verify.commands`, in the order build, unit tests, UI tests, integration tests, format or lint check that changes nothing. That file is the only place they are listed; a hook reads it too. The format check runs only in the feature gate, once per feature in the final review, never in the slice gate: `verify.mjs` leaves out of the slice gate every `dotnet format` command, or what `verify.format` (a regular expression) matches for another formatter. `verify.integration` names the integration command and how to filter it to named tests: <the integration command; the filter, e.g. `--filter "{tests}"` with `FullyQualifiedName~{name}` joined by `|`, or a trait such as `Category={name}`> <!-- or: not set; the slice gate then runs every integration test -->

Every command is quiet and still lists warnings, errors and failing tests: for .NET the build as `dotnet build -v q -nologo`, and the test executables with their quiet options that show output only for failed tests (for Microsoft.Testing.Platform runners, e.g. `--no-ansi --progress off --show-stdout Failed --show-stderr Failed`; check the names with the runner's `--help`). `verify.mjs` prints one line per command and, for a failing one, the failing tests with their first assertion line and at most 30 lines of its output; `--verbose` prints everything.

Two gates run them:

- **Slice gate**, at the end of every slice: `node .claude/hooks/verify.mjs --slice <test> [<test>...]`, with the integration tests written in that slice, normally `--slice <the slice's integration test class>`: the build, all unit tests, the UI tests, only those integration tests; no format check (it runs once per feature, in the final review, like the analyzers). A filter that matches zero tests means the slice is not done: the gate is red when the integration command's output says no test ran (by default the messages of `dotnet test`, the Microsoft testing platform, Jest and pytest; for another runner set `verify.integration.noTests` to its message, a regular expression). The full integration suite runs only in the final review.
- **Mutation check** (pack dotnet-shared), after the slice gate, the proof that the slice's tests can fail: `node scripts/mutation-check.mjs --slice <n> --base <the slice's base commit>` runs Stryker.NET (the local tool `dotnet-stryker`, pinned in `.config/dotnet-tools.json`, on the Microsoft testing platform runner) on the lines the slice added or modified in its production files under `src/` (no tests, generated code, markup-only `.razor` or resources; a new file whole; other mutants are counted as `outside the slice: n (not scored)`), with every test project of Pack values **Test projects**; the report goes to `artifacts/mutation/<n>/` (git-ignored), the evidence to `_specs/<FeatureName>/mutation-<n>.md` (committed with the slice, each survivor classified there). It is red below Pack values **Mutation threshold** or with a mutant without coverage; when `dotnet-stryker` cannot be restored it stops with "dotnet-stryker not available" (exit code 3), never skipping.
- **Feature gate**, once in the final review, and after any change to source code outside a slice: `node .claude/hooks/verify.mjs --feature` (or without argument): every command, every integration test.

- Not established yet: <the levels that have no command yet, or: none>
- Single test or class: `<test command with a filter>` <!-- every command here and in spec-driven.json with forward slashes: Git Bash, PowerShell and the unattended jobs all run that form -->
- Commands are written with forward slashes (`src/Test/...`), never with backslashes.
- A step that changes no source file (docs, specs) does not need this gate; run that step's own check instead.
- After a dependency or analyzer change, run a clean build for the proof: an incremental build can hide warnings.
