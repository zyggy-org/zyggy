# Plan: 01 — Solution scaffolding — Solution builds, tests, formats and publishes from a clean clone (thin harness)

## Overview

After this deliverable `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx` and `dotnet format Zyggy.slnx --verify-no-changes` are green from a clean clone on Windows and Linux and in GitHub Actions, CI publishes a runnable single-file `zyggy` for `win-x64` and `linux-x64`, the integration project can push a commit into a local bare bus repository and read it back (`BusRepoFixture`), and a compiled `fake-claude` streams a canned `done` scenario byte-for-byte while recording its working directory and argument vector — so deliverables 03–05 start from a RED test on a working loop. It implements `_specs/01-solution-scaffolding.md` (approved 2026-09-28, zero Open Questions; its Decision Table, Contracts and Dependencies table are binding — versions are not changed here) against founding-spec §9 (solution structure, Packages, design rules, Publish, Versioning), §12 (definition of done), §6 (the command line the fake must accept) and §13/§14 (.NET 10, single-file binaries, tenancy shape not contradicted).

**Reference pattern**: none yet — this is the first deliverable. The pattern is §9 design rules + the `build-feature` reference catalog, plus the `BusRepoFixture` sketch in `.claude/skills/integration-testing/SKILL.md` adapted per the spec's recorded deviations (no tenant member, no layout seeding, env-var-only scenario selection, NUL-delimited capture, compiled fake instead of scripts).

**What this plan deliberately is not**: no production type from §4–§7 (no `IProcessRunner`, `IBusProvider`, `BusPaths`, envelope, `TenantId`), no `Gates/` folder (05), no fake-claude scenarios beyond `done` (06), no Node/Hub publish in CI (07/11). Harness code (`tests/`, `tools/`) launches `git` and the fake through `System.Diagnostics.Process` directly — a spec decision so that a future `GitClient` bug cannot mask itself; the executor's REFACTOR §9 check must treat this as intended, not as a violation.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — Green baseline from a clean clone | 1 | Build, test and format-check green solution-wide; test stack proven | 🛑 after Step 1 (⚠️ new packages) |
| B — Integration harness | 2, 3 | Push/read back through a bare bus repo; launch the fake `claude` and trust its capture | 🛑 after Step 3 |
| C — CI and documentation | 4 | Same three commands + publish + smoke run on both runners; docs match the repo | 🛑 after Step 4 (definition of done) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green on the developer's Windows machine; Linux is proven by CI in Step 4. Step 1 is large by necessity: the three commands cannot all be green until project references, packages and `.editorconfig` exist, so it establishes the whole baseline in one RGR cycle.

<!--
Decomposition strategy: vertical slices, not horizontal layers.
Each slice follows: Fake (behavior proven through the seam interfaces with substitutes, unit tests)
→ Wire (real edge: git via Process, HttpClient with mocked handler, fake-claude script, local bare repo;
integration tests). Name steps by what the system can do — never by which type is built.
Good: "Reject an envelope with a bad signature", "Claim a job from a local bare bus repo"
Bad: "Create Envelope record", "Add GitClient", "Build BusRepository"

Gate placement: steps run back-to-back WITHOUT user intervention — the executor stops ONLY
at a 🛑 HUMAN GATE block. Place one gate at the end of each vertical slice. Add an extra
gate only where earlier user judgment is essential (contract sign-off after a fake step,
or after a ⚠️ Risk Area step: signing, secrets, work boundary, shared contract, new package).
Never attach a gate to every step.

Gate slice (MANDATORY when the deliverable closes a §12 phase — planner rule 15): the LAST
slice scripts the phase gate scenario under tests/Zyggy.Integration/Gates/P<n>_<Name>.cs
(a working round trip through the real components against the bare repo + fake claude),
updates PROTOCOL.md / node.json schema / the spec where behaviour changed, and adds a
runbook entry for any new failure mode. Its RED failing-run command:
  dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P<n>_<Name>"
The slice's 🛑 HUMAN GATE covers it.

Deliverable 01 does not close a phase (05 closes P0), so there is no gate slice; the final
🛑 HUMAN GATE is the definition-of-done check against ROADMAP.md #01 and AC-1..AC-24.
-->

---

## Step 1 — Solution builds, tests and format-checks green from a clean clone: one props file, pinned packages, the §9 reference graph, no template code, and the xunit.v3 + FluentAssertions 7 + NSubstitute + FakeTimeProvider stack proven by a test

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `global.json` *(create)* — `{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }`; no `test.runner` entry (VSTest mode).
- `Directory.Build.props` *(modify)* — root, the single source of shared settings (keys below).
- `Directory.Packages.props` *(create)* — central package management, exact versions from the spec's Dependencies table.
- `tests/Directory.Build.props` *(create)* — imports the root props via `GetPathOfFileAbove`; test-only settings and packages.
- `.editorconfig` *(create)* — spec contract "`.editorconfig` — contract".
- `.gitattributes` *(create)* — `* text=auto eol=lf`, `tests/golden/** -text`, `tools/fake-claude/scenarios/** -text`; followed by `git add --renormalize .`.
- `src/Zyggy.Core/Zyggy.Core.csproj` *(modify)* — `Sdk="Microsoft.NET.Sdk"` only; no properties, no references.
- `src/Zyggy.Core/Class1.cs` *(delete)* — `Zyggy.Core` has no source files after this step.
- `src/Zyggy.Node/Zyggy.Node.csproj` *(modify)* — `Sdk="Microsoft.NET.Sdk.Worker"`, `AssemblyName=zyggy-node`, `RootNamespace=Zyggy.Node`, `ProjectReference` → `Zyggy.Core`, version-less `PackageReference Microsoft.Extensions.Hosting`; `UserSecretsId`, `TargetFramework`, `Nullable`, `ImplicitUsings` removed.
- `src/Zyggy.Node/Worker.cs` *(delete)*.
- `src/Zyggy.Node/Program.cs` *(modify)* — `Host.CreateApplicationBuilder(args).Build().Run();` and nothing else (no hosted services). `appsettings*.json` and `Properties/launchSettings.json` untouched.
- `src/Zyggy.Cli/Zyggy.Cli.csproj` *(modify)* — `OutputType=Exe`, `AssemblyName=zyggy`, `RootNamespace=Zyggy.Cli`, `ProjectReference` → `Zyggy.Core`; duplicates removed.
- `src/Zyggy.Cli/Program.cs` *(modify)* — `return 0;`.
- `src/Zyggy.Hub/Zyggy.Hub.csproj` *(modify)* — `OutputType=Exe`, `AssemblyName=zyggy-hub`, `RootNamespace=Zyggy.Hub`, `ProjectReference` → `Zyggy.Core`; duplicates removed.
- `src/Zyggy.Hub/Program.cs` *(modify)* — `return 0;`.
- `tests/Zyggy.Core.Tests/Zyggy.Core.Tests.csproj` *(modify)* — `Sdk`, `ProjectReference` → `Zyggy.Core`, and `<None Include="../golden/**" CopyToOutputDirectory="PreserveNewest" LinkBase="golden" />`; everything else moves to `tests/Directory.Build.props`.
- `tests/Zyggy.Core.Tests/UnitTest1.cs` *(delete)*.
- `tests/Zyggy.Core.Tests/Infrastructure/TestStackSmokeTests.cs` *(create)* — the RED test of this step.
- `tests/Zyggy.Integration/Zyggy.Integration.csproj` *(modify)* — `Sdk`, `ProjectReference` → `Zyggy.Core`, `Zyggy.Node`, `Zyggy.Cli` (the `FakeClaude` reference is added in Step 3); everything else moves to `tests/Directory.Build.props`.
- `tests/Zyggy.Integration/AssemblyInfo.cs` *(create)* — `[assembly: Trait("Category", "Integration")]`.
- `tests/Zyggy.Integration/UnitTest1.cs` *(keep for now, delete in Step 2)* — keeps "each test project runs at least one test" true at this step's VERIFY; Step 2 replaces it with the first real integration test. Update it only if the xunit.v3 analyzers require it to compile.
- `tests/golden/README.md` *(create)* — the golden-file contract per AC-12.

**Seams**: none. This step declares no interface: the five seams and `IProcessRunner` are introduced by 03/04. It only makes the seams *testable* later: `NSubstitute` for the interfaces, `FakeTimeProvider` for `TimeProvider`.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Infrastructure/TestStackSmokeTests.cs` (namespace `Zyggy.Core.Tests.Infrastructure`, `public sealed class TestStackSmokeTests`; a `public interface ITestSeam { void Ping(string value); }` declared in the same file for the NSubstitute case)
- Unit test methods:
  - `Be_FailingAssertion_SurfacesAsXunitException` — `Action act = () => 1.Should().Be(2); act.Should().Throw<Xunit.Sdk.XunitException>();` (proves FluentAssertions 7 detected xunit.v3 — the risk AC-22 guards; without detection it would throw FluentAssertions' own `AssertionFailedException`).
  - `GetUtcNow_AfterAdvance_ReturnsAdvancedTime` — `new FakeTimeProvider(start)`, `Advance(TimeSpan.FromMinutes(5))`, `GetUtcNow()` equals `start + 5 min`.
  - `Received_AfterCallOnSubstitute_RecordsTheCall` — `Substitute.For<ITestSeam>()`, call `Ping("x")`, `Received(1).Ping("x")`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~TestStackSmokeTests"` — fails at **compile time** today (CS0246 `FakeTimeProvider`, CS0103 `Substitute`, missing namespace `FluentAssertions`): the packages, the global usings and xunit.v3 do not exist yet.
- Concrete before-state the executor records before GREEN (infrastructure has no other failing test):
  - `dotnet list src/Zyggy.Node/Zyggy.Node.csproj reference` prints no project reference (same for Cli, Hub, both test projects).
  - `Test-Path src/Zyggy.Cli/bin/Debug/net10.0/zyggy.exe` is `False` after `dotnet build src/Zyggy.Cli` (the apphost is still `Zyggy.Cli.exe`).
  - `git ls-files --eol` lists `i/crlf` entries and no `.gitattributes` exists.
  - `dotnet build Zyggy.slnx` succeeds today but with `xunit` 2.9.3 and per-project duplicated settings — the point of the step is not "build fails" but "build is not the contract yet".
- No integration test in this step (no real edge is added; the bare repo and the fake arrive in Steps 2–3).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `Directory.Build.props` (root): `TargetFramework=net10.0`, `LangVersion=14`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `InvariantGlobalization=true`, `MinVerTagPrefix=v`, and `GenerateDocumentationFile=true` **only** under `Condition="'$(MSBuildProjectName)' == 'Zyggy.Core'"`. Never `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `PackageVersion`, `UserSecretsId`, `GeneratePackageOnBuild`.
- `Directory.Packages.props`: `ManagePackageVersionsCentrally=true`; `PackageVersion` items with exactly these versions (spec Dependencies table, do not change): `xunit.v3` 4.0.1, `xunit.runner.visualstudio` 4.0.0, `Microsoft.NET.Test.Sdk` 18.10.1, `coverlet.collector` 10.0.1, `FluentAssertions` 7.2.2, `NSubstitute` 6.2.0, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0, `Microsoft.Extensions.Hosting` 10.0.12; `<GlobalPackageReference Include="MinVer" Version="8.0.0" />`. No floating or range versions.
- `tests/Directory.Build.props`: `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`; then `IsPackable=false`, `OutputType=Exe`; version-less `PackageReference` for the seven test packages; `<Using Include="Xunit" />`, `<Using Include="FluentAssertions" />`. Executor note: if the build reports a duplicate entry point (CS0017/CS8892) because `Microsoft.NET.Test.Sdk` and xunit.v3 both generate one, set `<GenerateProgramFile>false</GenerateProgramFile>` here — a test-only setting this props file owns; record it in the gate summary.
- `.editorconfig` exactly per the spec contract: `root = true`; `[*]` utf-8, lf, final newline, trim trailing whitespace, spaces, indent 4; `[*.{json,yml,yaml,xml,csproj,props,targets,slnx}]` indent 2; `[*.cs]` = the `dotnet new editorconfig` baseline with `IDE0055`, `IDE0161`, `IDE0005` at `warning`, `dotnet_sort_system_directives_first = true`, `csharp_prefer_braces = true`; pre-approved suppressions (each with a one-line comment, added **only if the rule fires**): `CA1848`, `CA2007`, `CA1031`, `CA1303`, `CA1812` → `suggestion`; `[tests/**.cs]` additionally `CA1707`, `CA1515` → `none`; `[tests/golden/**]` and `[tools/fake-claude/scenarios/**]` → `insert_final_newline = false`, `trim_trailing_whitespace = false`, `end_of_line = unset`. Any further suppression needs a comment and is listed at the gate. Executor note: in-build enforcement of `IDE0005` only works where `GenerateDocumentationFile` is true (so: `Zyggy.Core`); elsewhere `dotnet format` enforces it — accepted, no extra props.
- `.gitattributes` per contract, then `git add --renormalize .` so every existing text file becomes LF in the index and the working tree (this touches every CRLF file in the repo, content-neutral).
- The six `.csproj` files, `Program.cs` ×3, deletions and `AssemblyInfo.cs` as listed in Scope. Root namespaces stay `Zyggy.Cli`/`Zyggy.Node`/`Zyggy.Hub` (set `RootNamespace` explicitly because `AssemblyName` changes).
- `tests/golden/README.md`: states `<case>.md` + `<case>.canonical` + `<case>.sig`, byte-exact, `-text` in git, copied to the test output under `golden/` by the `None` item in `Zyggy.Core.Tests.csproj`, enumerated by a `[Theory]` + `[MemberData]` in `Zyggy.Core.Tests`, key id and test secret fixed by `_specs/03-envelope-signing.md`, a new case is added in RED for any canonicalisation or field change, no cases exist before 03. Write it with LF endings by hand (the `-text` attribute means git will not normalise it).
- No source folders under `src/Zyggy.Core/` are created (Out of Scope).

**Contract impact**: none for §4–§7. ⚠️ Risk Area — new NuGet dependencies (xunit.v3 replaces the deprecated xunit 2; `FluentAssertions` pinned to the last Apache-2.0 line 7.2.2 — never 8.x; `NSubstitute`; `Microsoft.Extensions.TimeProvider.Testing`; `MinVer` as a build-only global reference) and analyzer suppressions — reviewed at the gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus, specific to this step:
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~TestStackSmokeTests"` → 3 passed.
- `dotnet test Zyggy.slnx --filter "Category!=Integration"` → runs every `Zyggy.Core.Tests` test and zero tests from `Zyggy.Integration` (the assembly-level trait works).
- `dotnet build Zyggy.slnx` output contains zero warnings; `src/Zyggy.Cli/bin/Debug/net10.0/zyggy.exe`, `src/Zyggy.Node/bin/Debug/net10.0/zyggy-node.exe`, `src/Zyggy.Hub/bin/Debug/net10.0/zyggy-hub.exe` exist (AssemblyName in effect); `tests/Zyggy.Integration/bin/Debug/net10.0/` contains `Zyggy.Core.dll`, `zyggy.dll` and `zyggy-node.dll` (reference graph in effect).
- `dotnet run --project src/Zyggy.Cli; $LASTEXITCODE` → `0` with no output; same for `src/Zyggy.Hub`; `dotnet run --project src/Zyggy.Node` starts, logs "Application started", stays up with no `Worker running` lines, stops on Ctrl+C.
- `git ls-files --eol` → every text file `i/lf`; `tests/golden/README.md` shows `i/-text`.
- `Select-String -Path (git ls-files '*.csproj') -Pattern 'TargetFramework|LangVersion|Nullable|ImplicitUsings|TreatWarningsAsErrors|UserSecretsId|<Version>|AssemblyVersion|PackageVersion|Version='` → no match (AC-16, AC-17).
- `dotnet build Zyggy.slnx -p:MinVerVerbosity=normal` shows MinVer computing `0.0.0-alpha.0.<height>` (no tag yet) — the versioning tool is wired.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (green baseline) *(covers Step 1)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: the three PROVE commands green on the developer machine; `TestStackSmokeTests` (3 tests) green; `--filter "Category!=Integration"` runs zero Integration tests; `zyggy.exe`/`zyggy-node.exe`/`zyggy-hub.exe` produced; `zyggy` and `zyggy-hub` exit 0 silently; `zyggy-node` hosts an empty host; `git ls-files --eol` all `i/lf`.
- [x] Contract review: `Directory.Build.props` keys match the spec table (incl. `GenerateDocumentationFile` scoped to `Zyggy.Core`, `MinVerTagPrefix=v`); no `.csproj` sets any shared key or `UserSecretsId` (AC-16); `Directory.Packages.props` has exact versions only (AC-17); reference graph exactly AC-18 minus the `FakeClaude` reference (Step 3); `tests/golden/README.md` states the AC-12 contract and the `None` item + `.gitattributes -text` line exist; `Class1.cs`, `Worker.cs`, `Core.Tests/UnitTest1.cs` and the hello-world bodies are gone (AC-13, except `Integration/UnitTest1.cs`, which Step 2 removes).
- [x] ⚠️ Risk review: the new packages are exactly the spec's Dependencies table (names, versions, licences: xunit.v3/xunit.runner.visualstudio Apache-2.0, Microsoft.NET.Test.Sdk MIT, coverlet MIT, FluentAssertions **7.2.2** Apache-2.0 — confirm no 8.x anywhere in the lock/asset files, NSubstitute BSD-3, TimeProvider.Testing MIT, Hosting MIT, MinVer Apache-2.0); every `.editorconfig` suppression that was actually added has a reason comment and is listed by the executor; `GenerateProgramFile` was or was not needed (recorded); the renormalisation diff is content-neutral (line endings only).
- [x] User approved — implementation may continue past this gate

---

## Step 2 — An integration test pushes a commit into a local bare bus repository and reads it back from `origin/main` through `BusRepoFixture`, isolated from the machine's git configuration

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs` *(create)*
- `tests/Zyggy.Integration/Bus/BusRepoFixtureSmokeTests.cs` *(create)*
- `tests/Zyggy.Integration/UnitTest1.cs` *(delete)* — replaced by the tests above.
- `tests/Zyggy.Integration/README.md` *(modify, minimal)* — add the Troubleshooting sections named by the spec's Failure modes table: "Prerequisites" (`git` ≥ 2.32 on PATH; the fixture **fails**, never skips), "Isolation from machine config", "Leaked temp directories" (`<temp>/zyggy-it/`), "A test project with zero tests". Correct the one sentence that calls the fixture's counterpart a "script" only if it is touched anyway; full README content is out of scope.

**Seams**: none. The fixture launches `git` through `System.Diagnostics.Process` directly (spec decision: test infrastructure must not depend on the production `GitClient`/`IProcessRunner` that 04 introduces). No `BusPaths`, no tenant member, no layout seeding.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Integration test file: `tests/Zyggy.Integration/Bus/BusRepoFixtureSmokeTests.cs` *(create)* — namespace `Zyggy.Integration.Bus`, `public sealed class BusRepoFixtureSmokeTests(BusRepoFixture bus) : IClassFixture<BusRepoFixture>`; every async call passes `TestContext.Current.CancellationToken` (xunit.v3 analyzer xUnit1051).
- Integration test methods (one Act each; each test that needs a commit writes its own uniquely named file under `smoke/` in the Arrange so tests stay order-independent):
  - Happy path `ShowAsync_AfterCommitAndPush_ReturnsContentFromBareRepository` — Arrange: write `smoke/hello.txt` in `CloneDir`, `RunGitAsync(CloneDir, ["add","--all"])`, `["commit","-m","smoke: hello"]`, `["push","-u","origin","main"]`; Act: `bus.ShowAsync("smoke/hello.txt", ct)`; Assert: equals the written content (trimmed, since `RunGitAsync` trims stdout). (AC-9)
  - `HeadShaAsync_AfterPush_EqualsCloneHead` — Act: `bus.HeadShaAsync(ct)`; Assert: equals `RunGitAsync(CloneDir, ["rev-parse","HEAD"])`. (AC-9)
  - `LastCommitSubjectAsync_AfterPush_ReturnsSubjectOfPushedCommit` — subject `smoke: <unique>` read back from the bare repo.
  - `InitializeAsync_FreshFixture_BareAndCloneAreOnMain` — `RunGitAsync(BareDir, ["symbolic-ref","--short","HEAD"])` and the same in `CloneDir` both return `main` (AC-8; holds even with `init.defaultBranch=master` on the machine because the global config is ignored).
  - `InitializeAsync_FreshFixture_IdentityComesFromCloneLocalConfig` — `RunGitAsync(CloneDir, ["config","--show-origin","--get","user.email"])` returns a line whose origin is `CloneDir/.git/config` and whose value is `test@zyggy.org` (AC-8 isolation).
  - `InitializeAsync_FreshFixture_CloneContainsOnlyGitMetadata` — `Directory.EnumerateFileSystemEntries(bus.CloneDir)` contains only `.git`: no `tenants/` or any seeded layout (AC-11 behavioural half). Use a fresh `new BusRepoFixture()` initialised and disposed inside the test so the shared fixture's pushed files do not interfere.
  - `Fixture_PublicSurface_HasNoTenantMember` — `typeof(BusRepoFixture).GetMembers()` has no member whose name contains `Tenant` (AC-11).
  - Failure path `RunGitAsync_NonZeroExit_ThrowsWithStderrInMessage` — `RunGitAsync(CloneDir, ["rev-parse","--verify","refs/heads/does-not-exist"])` throws `InvalidOperationException` whose message contains `fatal` (git's stderr) and the failing arguments.
  - `DisposeAsync_AfterInitialize_DeletesRootDir` — construct `new BusRepoFixture()` directly, `InitializeAsync`, `DisposeAsync`, `Directory.Exists(RootDir)` is `false` (AC-10).
- Harness: this step *creates* Harness 1 of `.claude/skills/integration-testing/SKILL.md`; there is no fake-claude involvement.
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusRepoFixtureSmokeTests"` — fails at compile time (`BusRepoFixture` does not exist).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs`, `namespace Zyggy.Integration.Infrastructure`, `public sealed class BusRepoFixture : IAsyncLifetime` exactly per the spec contract:
  - `string RootDir` = `Path.Combine(Path.GetTempPath(), "zyggy-it", <unique>)`; `string BareDir` = `<RootDir>/bus.git`; `string CloneDir` = `<RootDir>/bus`; all assigned in the constructor (parameterless — `IClassFixture<T>` requires it).
  - `ValueTask InitializeAsync()`: create `RootDir` and an empty `<RootDir>/gitconfig`; `git init --bare --initial-branch=main <BareDir>`; `git clone <BareDir> <CloneDir>`; in the clone: `git symbolic-ref HEAD refs/heads/main` (unborn branch is `main` regardless of the remote's advertisement), `git config user.name "zyggy (test)"`, `git config user.email test@zyggy.org`, `git config commit.gpgsign false`, `git config core.autocrlf false` — all **local** config.
  - `Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)`: `ProcessStartInfo("git")` with `ArgumentList` (never a concatenated `Arguments` string — temp paths contain spaces), `WorkingDirectory`, redirected stdout/stderr, `UseShellExecute=false`; environment `GIT_CONFIG_GLOBAL=<RootDir>/gitconfig`, `GIT_CONFIG_NOSYSTEM=1`, `GIT_TERMINAL_PROMPT=0`; returns trimmed stdout; non-zero exit → `InvalidOperationException` with the arguments and stderr; `Win32Exception` on start → `InvalidOperationException("git >= 2.32 must be on PATH …", inner)` (Failure modes: fail, never skip).
  - `Task<string> HeadShaAsync(ct)` = `git rev-parse main` in `BareDir`; `Task<string> ShowAsync(string path, ct)` = `git show main:<path>` in `BareDir`; `Task<string> LastCommitSubjectAsync(ct)` = `git log -1 --format=%s main` in `BareDir`.
  - `ValueTask DisposeAsync()`: clear the `ReadOnly` attribute on every file under `RootDir` first (git object files are read-only on Windows and `Directory.Delete(recursive: true)` would throw), then delete `RootDir`; swallow `IOException`/`UnauthorizedAccessException` (directories may accumulate — README entry).
  - No static state; no `Tenant` member; no bus-layout knowledge; no `"tenants/"` literal.
- Delete `tests/Zyggy.Integration/UnitTest1.cs`.
- `tests/Zyggy.Integration/README.md` Troubleshooting sections as listed in Scope.

**Contract impact**: none for §4–§7. The fixture's public surface (`RootDir`, `BareDir`, `CloneDir`, `RunGitAsync`, `HeadShaAsync`, `ShowAsync`, `LastCommitSubjectAsync`) is the harness contract 04 extends with `SeedTenantAsync(TenantId tenant, …)`; do not add a tenant here.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~BusRepoFixtureSmokeTests"` → 9 passed.
- `dotnet test Zyggy.slnx --filter "Category!=Integration"` still executes zero Integration tests.
- After the run, `<temp>/zyggy-it/` contains no directory from this run (dispose works on Windows).
- `Select-String -Path (git ls-files 'tests/**/*.cs','tools/**/*.cs','src/**/*.cs') -Pattern 'geoffrey|TenantId\.Default|Tenant =|tenants/'` → no match (AC-11 review half; the smoke tests themselves must not contain these literals).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 3 — The integration project launches the compiled `fake-claude` exactly like the real CLI (no interpreter, no shell) and receives the `done` scenario byte-for-byte plus a faithful capture of its working directory and argument vector

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `tools/fake-claude/FakeClaude.csproj` *(create)* — `Microsoft.NET.Sdk`, `OutputType=Exe`, `AssemblyName=fake-claude`, `RootNamespace=FakeClaude`, `IsPackable=false`, `IsPublishable=false`, `<Content Include="scenarios/**" CopyToOutputDirectory="PreserveNewest" />`; no `PackageReference`, no `ProjectReference`, no other property (inherits the root props unchanged; the MinVer `GlobalPackageReference` applies implicitly, by design).
- `tools/fake-claude/Program.cs` *(create)* — top-level statements: capture → resolve scenario → stream bytes → exit.
- `tools/fake-claude/scenarios/done.jsonl` *(create)* — placeholder `stream-json`, LF, final LF.
- `tools/fake-claude/README.md` *(create)* — contract table, PLACEHOLDER notice, how to run by hand, troubleshooting ("Exit codes", "Executable not found", capture unset).
- `Zyggy.slnx` *(modify)* — `<Folder Name="/tools/"><Project Path="tools/fake-claude/FakeClaude.csproj" /></Folder>`.
- `tests/Zyggy.Integration/Zyggy.Integration.csproj` *(modify)* — add `<ProjectReference Include="../../tools/fake-claude/FakeClaude.csproj" />` (plain reference; no custom target, no post-build copy).
- `tests/Zyggy.Integration/Infrastructure/FakeClaude.cs` *(create)* — locator + capture decoder.
- `tests/Zyggy.Integration/Jobs/FakeClaudeTests.cs` *(create)*.

**Seams**: none. The tests start the fake through `System.Diagnostics.Process` with `FileName = FakeClaude.ExecutablePath` and `ArgumentList` — the exact launch shape 06's `ClaudeCodeCliRunner` will use through `IProcessRunner` (spec Finding 2). `IModelRunner` itself is 06.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Integration test file: `tests/Zyggy.Integration/Jobs/FakeClaudeTests.cs` *(create)* — namespace `Zyggy.Integration.Jobs`, `public sealed class FakeClaudeTests`; a private helper `RunAsync(string? scenario, string? capturePath, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)` returning `(int ExitCode, byte[] Stdout, string Stderr)` that reads stdout from `process.StandardOutput.BaseStream` as raw bytes (never through a `StreamReader`) and stderr as text, concurrently; each test uses its own temp directory under `Path.GetTempPath()/zyggy-it/` for the working directory and the capture file and deletes it afterwards. The §6 argument vector used by the AC-5 tests: `["-p", "Line one \"quoted\"\n\tline two", "--output-format", "stream-json", "--permission-mode", "auto", "--allowedTools", "Read,Edit,Bash(git *)", "--max-turns", "60"]` (no `--cwd`: the real CLI has none — spec Finding 1; the run directory is `ProcessStartInfo.WorkingDirectory`).
- Integration test methods:
  - `ExecutablePath_AfterBuild_PointsToExistingApphost` — `File.Exists(FakeClaude.ExecutablePath)`; the file name is `fake-claude.exe` on Windows, `fake-claude` elsewhere; `fake-claude.dll`, `fake-claude.runtimeconfig.json` and `scenarios/done.jsonl` sit next to it in `AppContext.BaseDirectory` (AC-24).
  - Happy path `Run_DoneScenario_StreamsScenarioBytesExitsZeroWithEmptyStderr` — stdout bytes equal `File.ReadAllBytes(Path.Combine(FakeClaude.ScenarioDirectory, "done.jsonl"))`, exit 0, stderr empty (AC-5).
  - `Run_DoneScenario_CapturesWorkingDirectoryAndArgumentsByteExact` — `FakeClaude.ReadCapture(path)` returns `WorkingDirectory` equal to the chosen directory (`Path.GetFullPath` on both sides) and `Arguments` sequence-equal to the vector above, order and bytes preserved including the `"`, `\n` and `\t` (AC-5).
  - `Run_ScenarioUnset_BehavesAsDone` — `scenario: null` → same stdout bytes as `done`, exit 0 (AC-6).
  - Failure path `Run_UnknownScenario_ExitsThreeWritesDiagnosticAndStillCaptures` — `does-not-exist` → exit 3, stdout empty, stderr is one line `fake-claude: unknown scenario 'does-not-exist'`, and the capture file exists and decodes (capture precedes lookup) (AC-7).
  - Failure path `Run_CapturePathIsDirectory_ExitsFourWithDiagnostic` — capture path = an existing directory → exit 4, stdout empty, one stderr line starting `fake-claude:`.
  - `Run_EmptyArgument_CapturedAsEmptyRecord` — args `["-p", "", "--max-turns", "60"]` → `Arguments[1] == ""` (Edge case).
  - `Run_NoArguments_CaptureHoldsOnlyWorkingDirectory` — `Arguments` empty, `WorkingDirectory` set.
  - `Run_TwoConcurrentInvocations_EachReadsItsOwnCapture` — `Task.WhenAll` of two runs with distinct capture paths and distinct argument vectors; each capture matches its own vector (Edge case; stateless fake).
- Harness: Harness 2 of `.claude/skills/integration-testing/SKILL.md`, as reshaped by the spec (compiled executable, env-var scenario selection, NUL-delimited capture with the working directory as record 0).
- Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~FakeClaudeTests"` — fails at compile time (`FakeClaude`, `FakeClaudeCapture` do not exist); after adding only the helper it fails at `ExecutablePath_AfterBuild_PointsToExistingApphost` with `FileNotFoundException` naming `<output>/fake-claude.exe` (no project, no reference yet).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `tools/fake-claude/Program.cs` (~40 lines, BCL only, no argument parsing or validation):
  1. `ZYGGY_FAKE_CLAUDE_CAPTURE` set and non-empty → write, with `new UTF8Encoding(false)`, record 0 = `Environment.CurrentDirectory` (absolute), then each `args[i]` in order, every record terminated by a single `\0`; on `IOException`/`UnauthorizedAccessException` → stderr `fake-claude: cannot write capture '<path>': <message>`, exit **4**, nothing on stdout.
  2. Scenario = `ZYGGY_FAKE_CLAUDE_SCENARIO` or `done` when unset/empty; path = `Path.Combine(AppContext.BaseDirectory, "scenarios", $"{scenario}.jsonl")` (never relative to the current directory).
  3. Missing → stderr `fake-claude: unknown scenario '<name>'`, exit **3**, nothing on stdout.
  4. Otherwise copy the file's bytes to `Console.OpenStandardOutput()` unchanged (no `Console.Out`, no BOM, no newline translation), flush, exit **0**. Stdin is never read. Stderr empty on success.
- `tools/fake-claude/scenarios/done.jsonl`: three JSON lines, each with `"placeholder": true` — a `{"type":"system","subtype":"init",…}` line; an `{"type":"assistant",…}` line whose text contains a `REPORT` section with `summary`, `files_changed`, `open_questions`; a `{"type":"result","subtype":"success","is_error":false,"cost_usd":…,"duration_ms":…,"num_turns":…}` line. LF endings, final LF. The README states that 06 replaces this with a real capture and removes the markers.
- `tools/fake-claude/README.md`: the spec's behavioural-contract table (location, inputs, order of operations, stdin, stderr, exit codes 0/3/4, capture format, concurrency, `done.jsonl` placeholder), how to run by hand (`dotnet run --project tools/fake-claude -- -p hello` with the env vars), and the troubleshooting entries from the Failure modes table ("Exit codes", "`ZYGGY_FAKE_CLAUDE_CAPTURE` unset", "Executable not found" → the `ProjectReference` in `Zyggy.Integration.csproj`).
- `tests/Zyggy.Integration/Infrastructure/FakeClaude.cs`, `namespace Zyggy.Integration.Infrastructure`, `public static class FakeClaude`:
  - `static string ExecutablePath` — `Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude")`; throws `FileNotFoundException` naming the path when absent.
  - `static string ScenarioDirectory` — `Path.Combine(AppContext.BaseDirectory, "scenarios")`.
  - `static FakeClaudeCapture ReadCapture(string path)` — read all bytes, split on `0x00`, decode each record as UTF-8, drop exactly the trailing empty segment produced by the final terminator; record 0 → `WorkingDirectory`, the rest → `Arguments`.
  - `public sealed record FakeClaudeCapture(string WorkingDirectory, IReadOnlyList<string> Arguments)`.
- `Zyggy.slnx` `/tools/` folder and the `ProjectReference` in `Zyggy.Integration.csproj`. Nothing under `src/` references the fake.

**Contract impact**: none for §4–§7. ⚠️ Test-side contract that 06 consumes: the env-var names, exit codes and the NUL-delimited capture format (working directory first). Documented in `tools/fake-claude/README.md`; a change later is a change to 06's invocation tests.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~FakeClaudeTests"` → 9 passed.
- `dotnet build Zyggy.slnx` builds seven projects (the fake included) with zero warnings; `dotnet format Zyggy.slnx --verify-no-changes` covers `tools/fake-claude/Program.cs`.
- `tests/Zyggy.Integration/bin/Debug/net10.0/` contains `fake-claude.exe`, `fake-claude.dll`, `fake-claude.runtimeconfig.json`, `scenarios/done.jsonl` (AC-24) — placed by the plain `ProjectReference`; confirm no `Target`/`Exec` was added to any csproj.
- By hand: `$env:ZYGGY_FAKE_CLAUDE_SCENARIO='nope'; & tests/Zyggy.Integration/bin/Debug/net10.0/fake-claude.exe -p x; $LASTEXITCODE` → `3` with the one-line diagnostic.
- `Select-String -Path tools/fake-claude/FakeClaude.csproj -Pattern 'PackageReference|ProjectReference'` → no match (AC-23); `Select-String -Path (git ls-files 'src/**/*.csproj') -Pattern 'FakeClaude'` → no match (AC-18).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (integration harness) *(covers Steps 2–3)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `BusRepoFixtureSmokeTests` (9) and `FakeClaudeTests` (9) green; `dotnet test Zyggy.slnx` green; `--filter "Category!=Integration"` runs zero Integration tests; `<temp>/zyggy-it/` left clean; the by-hand `fake-claude.exe` run exits 3 for an unknown scenario and 0 for `done`.
- [x] Contract review: `BusRepoFixture` public surface equals the spec's C# contract (no tenant member, no seeding, local identity `zyggy (test)` / `test@zyggy.org`, `commit.gpgsign=false`, `core.autocrlf=false`, `GIT_CONFIG_GLOBAL`/`GIT_CONFIG_NOSYSTEM`/`GIT_TERMINAL_PROMPT` on every call, `ArgumentList` never `Arguments`); fake-claude contract table in `tools/fake-claude/README.md` matches the spec (env vars, order of operations, exit codes 0/3/4, NUL capture with record 0 = working directory, stdin never read); `FakeClaude.csproj` has no package/project reference, `IsPublishable=false`, `Content` scenarios; `Zyggy.slnx` has the `/tools/` folder; `done.jsonl` carries the `placeholder` markers and the README's PLACEHOLDER notice names 06 as the owner of the real capture.
- [x] ⚠️ Risk review: no test reaches the network, GitHub or the real `claude` — process launches in `tests/` target only `git` and `FakeClaude.ExecutablePath` (AC-20: `Select-String -Path (git ls-files 'tests/**/*.cs') -Pattern 'ProcessStartInfo\(|FileName\s*='` shows only those two); the AC-11 grep (`geoffrey|TenantId\.Default|Tenant =|tenants/` over `src/`, `tests/`, `tools/`) is empty; the capture format is accepted as the contract 06 will assert the §6 command line against (the founding spec's `--cwd` is known not to exist — spec Finding 1 — and is left for 06 to raise as a §6 amendment).
- [x] User approved — implementation may continue past this gate

---

## Step 4 — CI proves the three commands on Windows and Linux, publishes a runnable single-file `zyggy` per RID as a downloadable artefact without any secret, and the repository's own documentation describes what now exists

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `.github/workflows/ci.yml` *(create)*.
- `CLAUDE.md` *(modify)* — "What this is" last sentence and the whole "Build and test" section (AC-15).
- `src/Zyggy.Core/README.md`, `src/Zyggy.Node/README.md`, `src/Zyggy.Cli/README.md`, `src/Zyggy.Hub/README.md`, `tests/Zyggy.Core.Tests/README.md`, `tests/Zyggy.Integration/README.md` *(verify only, AC-14)* — no `Hello, World`, `dotnet new` or template wording; already true, nothing to write unless the grep matches.
- No csproj, props or source change.

**Seams**: none.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- This step is infrastructure and documentation (planner rule 6 exception: CI workflows are verified by running them). No automated test can prove a GitHub Actions run locally; the concrete before/after checks are:
  - Before: `.github/workflows/` does not exist; `Select-String -Path CLAUDE.md -Pattern 'no project references|no shared test packages|no .tools/fake-claude.|no CI yet|deliverable 01 adds'` matches (the section still describes the scaffold).
  - Before: `Test-Path artifacts/win-x64/zyggy.exe` is `False`.
  - After GREEN, the local publish + smoke run below succeeds and the CLAUDE.md grep is empty; the cross-OS run is checked on GitHub at the gate.
- The local proxy for the CI publish/smoke steps (host RID only): `dotnet publish src/Zyggy.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/win-x64` then `& artifacts/win-x64/zyggy.exe; $LASTEXITCODE` → `0` (exactly the §9 command line the workflow runs; `artifacts/` is already git-ignored).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `.github/workflows/ci.yml` per the spec contract:
  - `on: push: branches: [main]`, `pull_request`; top-level `permissions: contents: read`; `concurrency: { group: ci-${{ github.ref }}, cancel-in-progress: true }`.
  - Job `build`, `runs-on: ${{ matrix.os }}`, `strategy: { fail-fast: false, matrix: { os: [windows-latest, ubuntu-latest] } }`, with matrix `include` entries mapping `windows-latest → rid: win-x64, exe: zyggy.exe` and `ubuntu-latest → rid: linux-x64, exe: zyggy`.
  - Steps: `actions/checkout` with `fetch-depth: 0` (MinVer needs tags and height) → `actions/setup-dotnet` with `global-json-file: global.json` → `dotnet restore Zyggy.slnx` → `dotnet build Zyggy.slnx -c Release --no-restore` → `dotnet format Zyggy.slnx --verify-no-changes --no-restore` → `dotnet test Zyggy.slnx -c Release --no-build` → `dotnet publish src/Zyggy.Cli -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -o artifacts/${{ matrix.rid }}` → run `artifacts/${{ matrix.rid }}/${{ matrix.exe }}` (a step whose failure is the non-zero exit) → `actions/upload-artifact` with `name: zyggy-${{ matrix.rid }}`, `path: artifacts/${{ matrix.rid }}`, `if-no-files-found: error`.
  - Action majors: the spec lists `actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7` as current on 2026-09-28 and asks that they be re-confirmed when the file is written — the executor checks each action's GitHub releases page and uses the current major; record what was used in the gate summary.
  - No `secrets.*` reference anywhere; no PowerShell/bash step for the fake (it is part of the solution build); the publish step targets `src/Zyggy.Cli` only (never Node, Hub or the fake).
- `CLAUDE.md`:
  - "What this is": replace "The repo is currently a scaffold: every project is a `dotnet new` template with no real code and no project references yet." with one sentence stating that the solution, references, shared props/packages, `tools/fake-claude`, the integration harness and CI exist and that `src/Zyggy.Core` is still empty until 03.
  - "Build and test": replace the paragraph and command block with: `Zyggy.slnx` lists the six product/test projects plus `tools/fake-claude/FakeClaude.csproj`; settings in `Directory.Build.props` (root, `tests/`), versions in `Directory.Packages.props`, SDK in `global.json`; the commands `dotnet build Zyggy.slnx`, `dotnet test Zyggy.slnx`, `dotnet test Zyggy.slnx --filter "Category!=Integration"`, `dotnet format Zyggy.slnx --verify-no-changes` (fix with `dotnet format Zyggy.slnx`), the per-class and per-test `--filter "FullyQualifiedName~…"` forms, `dotnet run --project src/Zyggy.Node`, and the publish line for `src/Zyggy.Cli` (both RIDs); keep the warnings-as-errors and xUnit notes but state the stack as xunit.v3 + FluentAssertions 7 + NSubstitute + `FakeTimeProvider`; add the four short runbook notes the spec's Failure modes table routes here: format differences → run `dotnet format Zyggy.slnx`; CRLF committed → `git ls-files --eol` shows `i/crlf`, renormalise; MinVer without history → `0.0.0-alpha.0`, an MSBuild warning that does not fail the build; CI red at the smoke step → the single-file/self-contained/invariant-globalization publish itself is broken. Remove every statement that references, shared test packages, `tools/fake-claude` or CI are missing.
- READMEs: run the AC-14 grep; edit only on a match.

**Contract impact**: none for §4–§7. ⚠️ Risk Area — secrets: the workflow must need and hold none (`permissions: contents: read`, no `secrets.*`; only the implicit token used by the upload action).

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus, locally:
- The exact CI command sequence run by hand in Release: `dotnet restore Zyggy.slnx`, `dotnet build Zyggy.slnx -c Release --no-restore`, `dotnet format Zyggy.slnx --verify-no-changes --no-restore`, `dotnet test Zyggy.slnx -c Release --no-build`, the publish line for `win-x64`, `& artifacts/win-x64/zyggy.exe; $LASTEXITCODE` → `0`; `artifacts/win-x64/` contains a single-file `zyggy.exe` (plus `zyggy.pdb` at most).
- `Select-String -Path .github/workflows/ci.yml -Pattern 'secrets\.'` → no match; `permissions:` present with `contents: read`; `Select-String -Pattern 'publish' .github/workflows/ci.yml` shows only `src/Zyggy.Cli`.
- `ci.yml` parses as YAML (the executor may use `actionlint` if installed; otherwise the GitHub Actions editor or a YAML load in PowerShell/`python -c "import yaml"` if available — record which).
- `Select-String -Path CLAUDE.md -Pattern 'no project references|no shared test packages|no CI yet|deliverable 01 adds|currently a scaffold'` → no match; `Select-String -Path (git ls-files '*/README.md') -Pattern 'Hello, World|dotnet new'` → no match.
- Cross-OS proof is **not** local: after committing and pushing, the gate below checks the GitHub run.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C and definition of done for deliverable 01 *(covers Step 4, and the whole deliverable against ROADMAP.md #01 and AC-1..AC-24)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification on GitHub: the `ci` workflow run for the pushed commit is green on **both** `windows-latest` and `ubuntu-latest`; each job shows build with zero warnings, `dotnet format --verify-no-changes` exit 0 (AC-3 on Linux), `dotnet test` with `Zyggy.Core.Tests` (3 tests) and `Zyggy.Integration` (18 tests) passing on both OSes (AC-5 "passes on Windows and Linux"), the smoke step running the published binary with exit 0; artefacts `zyggy-win-x64` and `zyggy-linux-x64` are downloadable and contain `zyggy.exe` / `zyggy` respectively (AC-4); the run used no repository secret (Settings → Secrets is empty or untouched).
- [x] Contract review — definition of done from `_plans/ROADMAP.md` #01: the three commands green locally and in CI; publish artefacts downloadable for both RIDs; fake-claude `done` runs on Windows and Linux and records its arguments; `BusRepoFixture` smoke test green; template placeholders gone; `CLAUDE.md` "Build and test" matches the populated `Zyggy.slnx`. And the AC coverage table below: every row has its evidence.
- [x] ⚠️ Risk review: CI declares `permissions: contents: read`, references no `secrets.*` and needed no secret; the action majors used are recorded and current; only `src/Zyggy.Cli` is published (the fake and Node/Hub are not); the full package list with licences is unchanged since the Slice A gate.
- [x] Roadmap bookkeeping to confirm with the user: `_plans/ROADMAP.md` #01 may be set to `Done` with Spec/Plan links filled; the technical-analyst's forwarded findings (§6 `--cwd` does not exist; `claude.path` is a directly launchable executable; xunit.v3 `TestContext.Current.CancellationToken`) are noted for 06/03.
- [x] User approved — deliverable 01 complete

---

## Acceptance-criteria coverage

| AC | Covered by | Evidence at the gate |
|----|-----------|----------------------|
| AC-1 build, zero warnings, seven projects | Step 1 (six projects), Step 3 (fake) | `dotnet build Zyggy.slnx` local + CI both OSes |
| AC-2 test, ≥1 test per project, category filter | Step 1 (Core.Tests, assembly trait), Step 2 (first Integration test) | `dotnet test Zyggy.slnx`, `--filter "Category!=Integration"` |
| AC-3 format green on Windows and Linux | Step 1 (`.editorconfig`, `.gitattributes`), Step 4 (Linux via CI) | local + CI |
| AC-4 CI workflow, publish, smoke run, artefacts, no secrets | Step 4 | GitHub run, artefact download |
| AC-5 fake: byte-exact stdout, exit 0, empty stderr, faithful capture, both OSes | Step 3 | `FakeClaudeTests` locally + CI matrix |
| AC-6 scenario unset = done | Step 3 | `Run_ScenarioUnset_BehavesAsDone` |
| AC-7 unknown scenario: exit 3, stderr line, capture still written | Step 3 | `Run_UnknownScenario_ExitsThreeWritesDiagnosticAndStillCaptures` |
| AC-8 fixture creates bare + clone on `main`, local identity, machine config ignored | Step 2 | `InitializeAsync_FreshFixture_BareAndCloneAreOnMain`, `…IdentityComesFromCloneLocalConfig` |
| AC-9 push then read back from the bare repo; HEAD SHA equals clone HEAD | Step 2 | `ShowAsync_AfterCommitAndPush_ReturnsContentFromBareRepository`, `HeadShaAsync_AfterPush_EqualsCloneHead` |
| AC-10 dispose deletes the temp root, IO errors swallowed | Step 2 | `DisposeAsync_AfterInitialize_DeletesRootDir` |
| AC-11 no `geoffrey`/`TenantId.Default`/`Tenant =`/`tenants/` in src, tests, tools; fixture has no tenant, seeds nothing | Step 2 (tests + grep), Step 3 (grep re-run at gate) | `Fixture_PublicSurface_HasNoTenantMember`, `InitializeAsync_FreshFixture_CloneContainsOnlyGitMetadata`, grep at Slice B gate |
| AC-12 golden README, `None` item, `-text` | Step 1 | review at Slice A gate |
| AC-13 placeholders gone; Node empty host; Cli/Hub exit 0 | Step 1 (all but `Integration/UnitTest1.cs`), Step 2 (that file) | `dotnet run` checks; review |
| AC-14 READMEs without template text | Step 4 (grep) | grep empty |
| AC-15 CLAUDE.md "Build and test" | Step 4 | grep empty + review |
| AC-16 no shared keys in any csproj | Step 1 | grep over `*.csproj` |
| AC-17 central package management, exact versions | Step 1 | `Directory.Packages.props` review, restore green |
| AC-18 reference graph | Step 1 (six), Step 3 (fake) | `dotnet list … reference`, grep for `FakeClaude` in `src/` |
| AC-19 `AssemblyName` zyggy / zyggy-node / zyggy-hub, root namespaces kept | Step 1 | apphost names in `bin/`, publish output in Step 4 |
| AC-20 no test starts real `claude`, GitHub or network | Steps 2–3 | grep for `ProcessStartInfo`/`FileName` at Slice B gate |
| AC-21 `git ls-files --eol` all `i/lf`; golden + scenarios `-text` | Step 1 (attributes), Step 3 (scenario file) | `git ls-files --eol` |
| AC-22 test stack proven under xunit.v3 | Step 1 | `TestStackSmokeTests` |
| AC-23 `FakeClaude.csproj` shape, in `/tools/`, no refs, inherits props, content scenarios, never published | Step 3, Step 4 (publish targets Cli only) | csproj grep, `Zyggy.slnx`, CI review |
| AC-24 fake apphost + dll + runtimeconfig + scenarios in the Integration output via plain `ProjectReference` | Step 3 | `ExecutablePath_AfterBuild_PointsToExistingApphost`, directory listing |

## Notes for the executor (things the planner found while reading the repo and spec)

- `Zyggy.Core` ends this deliverable with **no** `.cs` file and `GenerateDocumentationFile=true`; an empty assembly and an empty XML doc file are expected, not a warning.
- Git object files are read-only on Windows: `BusRepoFixture.DisposeAsync` must clear `FileAttributes.ReadOnly` before `Directory.Delete(recursive: true)`, otherwise AC-10's test fails on the developer machine while passing on Linux.
- `tests/golden/README.md` is matched by the `../golden/**` `None` glob and will be copied to the test output under `golden/`; harmless, and the spec's glob is kept as written.
- With `.gitattributes` `tests/golden/** -text`, the README under `tests/golden/` is not normalised by git — write it with LF by hand (the `.editorconfig` also leaves that folder alone).
- Every text file in the repo (including `_specs/`, `_plans/`, `.claude/`) is renormalised in Step 1; commit that renormalisation as its own commit ("chore: normalise line endings to LF") so the content diff of Step 1 stays readable.
- Harness code (`BusRepoFixture`, `FakeClaudeTests`, the fake itself) uses `System.Diagnostics.Process` and `Environment.CurrentDirectory` directly by spec decision; `@code-analysis` must not "route it through `IProcessRunner`" — that seam does not exist until 04.
- The spec's action majors (`checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`) could not be verified by the planner (no network); confirm on the releases pages before writing `ci.yml`.
