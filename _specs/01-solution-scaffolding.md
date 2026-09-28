# Spec: 01 — Solution builds, tests, formats and publishes from a clean clone (thin harness)

> Founding-spec sections: §9 solution structure, Packages, design rules, Publish, Versioning; §12 Definition of done; §6 Invocation (the command line the fake must accept); §13 decisions (.NET 10 LTS, single-file self-contained binaries, naming map); §14 tenancy shape (must not be contradicted). Roadmap entry: `_plans/ROADMAP.md` #01. Repo conventions honoured: `CLAUDE.md` "Build and test", `.claude/instructions/publishing.md`, `.claude/instructions/tests.md`, `.claude/skills/integration-testing/SKILL.md`.
>
> Status: **approved 2026-09-28 — zero Open Questions; planner-ready.** Q1 (vehicle for `tools/fake-claude`) was answered by the user on 2026-09-28: the fake is a compiled .NET console project (`tools/fake-claude/FakeClaude.csproj`, `AssemblyName fake-claude`). Every other question in the hand-off brief is decided below with a rationale; each decision is marked *overturnable* where the user may reasonably prefer the other option.

## Current state (verified against the repo on 2026-09-28)

| Item | State today |
|------|-------------|
| `Zyggy.slnx` | Lists the six projects under `/src/` and `/tests/`; no `/tools/` folder. |
| `Directory.Build.props` | `net10.0`, `LangVersion` 14, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors` — correct, but every `.csproj` repeats `TargetFramework`/`Nullable`/`ImplicitUsings`. |
| `src/Zyggy.Core` | `Class1.cs`; no packages; no references. |
| `src/Zyggy.Node` | Worker SDK; `Worker.cs` (1 s log loop), `Program.cs` hosting it, `UserSecretsId`, `Microsoft.Extensions.Hosting` 10.0.4, `appsettings*.json`, `launchSettings.json`. |
| `src/Zyggy.Cli`, `src/Zyggy.Hub` | `Console.WriteLine("Hello, World!")`; no packages; no references. |
| `tests/Zyggy.Core.Tests`, `tests/Zyggy.Integration` | `UnitTest1.cs`; xunit 2.9.3, xunit.runner.visualstudio 3.1.4, Microsoft.NET.Test.Sdk 17.14.1, coverlet.collector 6.0.4; no project references; no FluentAssertions/NSubstitute/TimeProvider.Testing. |
| Missing | `tools/`, `tests/golden/`, `.editorconfig`, `.gitattributes`, `global.json`, `Directory.Packages.props`, `.github/workflows/`. |
| READMEs | All six per-project READMEs already carry product text (no `dotnet new` boilerplate). |

## User Story

**As** the developer (and the agents) building the P0 spike,
**I want** a solution that builds warning-free, tests, format-checks and publishes `zyggy` for `win-x64` and `linux-x64` from a clean clone and in CI, with a bare-repo bus fixture and a fake `claude` that records how it was invoked,
**So that** deliverables 03–05 start from a RED test on a working loop, the spike binaries can be put on all three machines, and no later deliverable has to build tooling before it can build product code (P0 prerequisite).

**As** the P0 spike operator (Central, home laptop, work laptop),
**I want** a downloadable single-file `zyggy` binary per OS from every CI run,
**So that** the probe binaries in 04 are the artefacts CI produced, not a hand-built copy.

---

## Acceptance Criteria

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | A clean clone on Windows or Linux with only the .NET 10 SDK (per `global.json`) and `git` ≥ 2.32 on PATH | `dotnet build Zyggy.slnx` | Exit code 0, zero warnings (every warning is an error via `Directory.Build.props`); all seven projects — the six product/test projects plus `tools/fake-claude/FakeClaude.csproj` — build. |
| AC-2 | The built solution | `dotnet test Zyggy.slnx` | Exit code 0; each test project runs at least one test; `dotnet test Zyggy.slnx --filter "Category!=Integration"` executes zero tests from `Zyggy.Integration` and every test from `Zyggy.Core.Tests`. |
| AC-3 | The clean clone, on Windows **and** on Linux | `dotnet format Zyggy.slnx --verify-no-changes` | Exit code 0 on both OSes (line endings are LF in git and in the working tree on both). |
| AC-4 | A push to `main` or a pull request | The GitHub Actions workflow `.github/workflows/ci.yml` runs | On `windows-latest` and `ubuntu-latest` the three commands of AC-1..3 succeed; each runner publishes `Zyggy.Cli` single-file self-contained for its own RID (`win-x64` on Windows, `linux-x64` on Linux) with exactly the §9 command line, runs the published binary once (exit code 0), and uploads it as artefact `zyggy-<rid>`. The workflow declares `permissions: contents: read`, references no `secrets.*`, and needs no repository secret. |
| AC-5 | The `fake-claude` executable at `FakeClaude.ExecutablePath` (in the `Zyggy.Integration` test output) with `ZYGGY_FAKE_CLAUDE_SCENARIO=done` and `ZYGGY_FAKE_CLAUDE_CAPTURE=<unique file under the test temp dir>` | The fake is started directly through `System.Diagnostics.Process` (`FileName` = the executable, no interpreter, no shell) on the runner's OS with an argument vector containing the §6 tokens (`-p <multi-line prompt with double quotes and a tab>`, `--output-format stream-json`, `--permission-mode auto`, `--allowedTools <list>`, `--max-turns 60`) and a chosen working directory | Stdout bytes equal `tools/fake-claude/scenarios/done.jsonl` byte for byte; exit code 0; stderr empty; the capture file decodes to (working directory, the exact argument vector, order and bytes preserved). The test passes on Windows and Linux. |
| AC-6 | The fake with `ZYGGY_FAKE_CLAUDE_SCENARIO` unset | Started as in AC-5 | Behaves as scenario `done`. |
| AC-7 | The fake with `ZYGGY_FAKE_CLAUDE_SCENARIO=does-not-exist` | Started as in AC-5 | Exit code 3, nothing on stdout, one diagnostic line on stderr naming the scenario; the capture file is still written (capture happens before scenario lookup). |
| AC-8 | `tests/Zyggy.Integration/Infrastructure/BusRepoFixture` | `InitializeAsync()` runs | A bare repository whose only branch will be `main` and a clone whose current branch is `main` exist under a unique temp root; `user.name`/`user.email` are set in the clone's local config; the fixture's git calls ignore the machine's global and system config. |
| AC-9 | An initialised `BusRepoFixture` | The smoke test writes `smoke/hello.txt` in the clone, commits and pushes through the fixture's `RunGitAsync`, then calls `ShowAsync("smoke/hello.txt")` and `HeadShaAsync()` | `ShowAsync` returns the file content read from the **bare** repository (`git show main:smoke/hello.txt`), and `HeadShaAsync` equals the clone's `HEAD` SHA. |
| AC-10 | An initialised `BusRepoFixture` | `DisposeAsync()` runs | The temp root is deleted; I/O errors during deletion do not fail the test. |
| AC-11 | The harness code (`tests/`, `tools/`) and `src/` after this deliverable | Searched for `geoffrey`, `TenantId.Default`, `Tenant =` defaults, `tenants/` path literals | No match: the fixture has no tenant member and seeds no bus layout (§14 shape is not contradicted; 04 adds `SeedTenantAsync(TenantId …)` with an explicit tenant per call). |
| AC-12 | `tests/golden/` | Reviewed | `tests/golden/README.md` states the contract (`<case>.md` + `<case>.canonical` + `<case>.sig`, byte-exact, copied to test output, enumerated by `[MemberData]`, key id and test secret fixed by 03); no case files exist; `Zyggy.Core.Tests.csproj` already carries the copy-to-output item; `.gitattributes` marks `tests/golden/**` as `-text`. |
| AC-13 | The repository after this deliverable | Reviewed | `Class1.cs`, both `UnitTest1.cs`, the hello-world `Program.cs` bodies and `Worker.cs` are gone; `Zyggy.Core` has no source files; `Zyggy.Node` `Program.cs` builds and runs a host with no hosted services; `Zyggy.Cli` and `Zyggy.Hub` `Program.cs` exit 0 with no output. |
| AC-14 | The six per-project READMEs | Searched for `Hello, World`, `dotnet new`, template wording | No match (already true today; the AC guards it). |
| AC-15 | `CLAUDE.md` "Build and test" | Compared with `Zyggy.slnx` and the test layout | It lists the solution-level commands (`build`, `test`, `test --filter "Category!=Integration"`, `format --verify-no-changes`, the per-class/per-test filters, publish) and no longer states that references, shared test packages, `tools/fake-claude` and CI are missing. |
| AC-16 | Every `.csproj` | Reviewed | None sets `TargetFramework`, `LangVersion`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `UserSecretsId`, `Version`, `AssemblyVersion` or `PackageVersion`; all of these live in `Directory.Build.props` (or come from MinVer). |
| AC-17 | `Directory.Packages.props` | `dotnet restore` | Central package management is on; every `PackageReference` in a `.csproj`/props is version-less; every `PackageVersion` is an exact version (no ranges, no floating). |
| AC-18 | Project references | Reviewed | `Node`, `Cli`, `Hub` → `Core`; `Core.Tests` → `Core`; `Integration` → `Core`, `Node`, `Cli`, `tools/fake-claude/FakeClaude.csproj`; `Core` references no project; `Node`/`Cli`/`Hub` never reference each other; `FakeClaude` references no project and no package, and no `src/` project references it. |
| AC-19 | `dotnet publish` of each host | Output inspected | Binaries are named `zyggy` (`Zyggy.Cli`), `zyggy-node` (`Zyggy.Node`), `zyggy-hub` (`Zyggy.Hub`) via `AssemblyName`; root namespaces stay `Zyggy.Cli`/`Zyggy.Node`/`Zyggy.Hub`. |
| AC-20 | Any test in the solution | Reviewed at the gate | No test starts the real `claude`, reaches GitHub or any network endpoint; process launches in tests target only `git` and the fake. |
| AC-21 | `git ls-files --eol` on the clone | Inspected | Every text file is `i/lf`; `tests/golden/**` and `tools/fake-claude/scenarios/**` are unnormalised (`-text`). |
| AC-22 | `tests/Zyggy.Core.Tests` | `dotnet test` | One harness test proves the test stack: a failing FluentAssertions assertion surfaces as an `Xunit.Sdk.XunitException` under xunit.v3 (guards the FluentAssertions 7 + xunit.v3 detection risk), a `FakeTimeProvider` advances, an NSubstitute substitute records a call. |
| AC-23 | `tools/fake-claude/FakeClaude.csproj` | Reviewed and built | Listed in `Zyggy.slnx` under `/tools/`; `OutputType=Exe`, `AssemblyName=fake-claude`, `RootNamespace=FakeClaude`, `IsPackable=false`, `IsPublishable=false`; no `PackageReference`, no `ProjectReference`; inherits the root `Directory.Build.props` unchanged (net10.0, warnings as errors, `latest-recommended` analyzers, invariant globalization) and builds warning-free under it; `scenarios/**` are `Content` items with `CopyToOutputDirectory=PreserveNewest`; CI never publishes it (AC-4 publishes `Zyggy.Cli` only). |
| AC-24 | `tests/Zyggy.Integration` after `dotnet build` | The test output directory is inspected | `fake-claude` (`.exe` on Windows, apphost without extension on Linux), `fake-claude.dll`, `fake-claude.runtimeconfig.json` and `scenarios/done.jsonl` are present, placed there by the `ProjectReference` to `FakeClaude.csproj` (referenced-Exe output and transitive content copy — no custom MSBuild target, no post-build script); `FakeClaude.ExecutablePath` resolves to that file and it exists. |

---

## Decision Table

| Founding-spec / convention item | Verdict | Target type / library | Justification |
|------------------------------|---------|-----------------------|---------------|
| §9 one solution, six projects (`Zyggy.slnx`) | Keep | `Zyggy.slnx` + `<Folder Name="/tools/">` holding `tools/fake-claude/FakeClaude.csproj` | Already correct for the six; only references, packages and the `/tools/` folder are missing. The fake is in the solution so `dotnet build Zyggy.slnx` and `dotnet format Zyggy.slnx` cover it. |
| §9 / publishing.md project reference graph | Keep | `ProjectReference` items per AC-18 | Exactly the graph the roadmap fixes; `Core` stays dependency-free within the solution. |
| §9 "C# 14, nullable, warnings as errors" | Reshape | `Directory.Build.props` as the single source; per-`.csproj` duplicates removed | Duplicated settings drift; publishing.md already forbids repeats. |
| `UserSecretsId` in `Zyggy.Node.csproj` | Defer (remove) | — | §8: secrets only via `ISecretStore`; user-secrets is a config channel we never use. |
| §9 Packages: `xunit` | Reshape | `xunit.v3` 4.0.1 + `xunit.runner.visualstudio` 4.0.0 + `Microsoft.NET.Test.Sdk` 18.10.1 (VSTest mode) | `xunit` 2.9.3 is marked deprecated on nuget.org ("all future feature work has moved onto v3", last release Jan 2025). v3 is Apache-2.0, .NET Foundation, released 2026-09-12, runs on .NET 10 under VSTest so every documented `--filter` command keeps working; the integration-testing skill's `IAsyncLifetime` sample (`ValueTask InitializeAsync`) is already the v3 signature. MTP mode is deferred (below). |
| §9 Packages: `FluentAssertions` | Keep | `FluentAssertions` 7.2.2 | 8.x is Xceed commercial (free only for non-commercial use; §14 says the platform must stay sellable). 7.x stays Apache-2.0 "indefinitely"; 7.1.0 back-ported xUnit.net v3 support; 7.2.2 is the newest 7.x on nuget.org. Fallback if 7.x ever breaks: `AwesomeAssertions` (Apache-2.0 community fork, same namespace, 9.6.0 on 2026-08-20). *Overturnable.* |
| §9 Packages: `NSubstitute` | Keep | `NSubstitute` 6.2.0 (BSD-3-Clause; transitive `Castle.Core` Apache-2.0) | Named by §9; active (2026-08-11). |
| tests.md: `Microsoft.Extensions.TimeProvider.Testing` | Keep | 10.10.0 (MIT) | tests.md forbids real time in tests; needed from 04 (`PollState`). Referenced now so both test projects are complete. |
| Scaffold: `coverlet.collector` | Keep | 10.0.1 (MIT, no deps) | Already present, zero cost, enables opt-in `--collect:"XPlat Code Coverage"` without a later project change. Coverage *thresholds* stay out of scope. *Overturnable (drop if unwanted).* |
| Scaffold: `Microsoft.Extensions.Hosting` (Node) | Keep | 10.0.12 (MIT) | §9 Worker hosting; version bumped to current 10.0.x. `Hosting.WindowsServices`/`.Systemd` are 07. |
| §9 Packages: YamlDotNet, System.CommandLine, ModelContextProtocol, Ulid, Serilog, OpenTelemetry, Meziantou CredentialManager | Defer | owning deliverables (03, 04/08, 11, 03, 07, 19, 09) | Nothing in 01 uses them; adding unused packages is cost without value. |
| publishing.md: "pin exact versions" | Library | NuGet Central Package Management (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`) | An SDK feature that enforces one exact version per package solution-wide (NU1008 on any per-project version). Zero code owned. |
| publishing.md: "versions come from git tags; the scaffolding deliverable decides the tool" | Library | `MinVer` 8.0.0 as a `GlobalPackageReference`, `MinVerTagPrefix=v` | Apache-2.0, no dependencies, build-only, shells out to `git` (already the only prerequisite). Nerdbank.GitVersioning needs a `version.json` and more machinery; "none until 08" would ship unversioned spike binaries to three machines. Untagged builds are `0.0.0-alpha.0.<height>+<sha>`, which is enough for the decision record to name the build. *Overturnable.* |
| §9 Publish command line and artefact names | Keep | CI publish step (Cli only); `AssemblyName` on all three hosts | Exact §9 flags; naming is one property per host and has no cost, so all three are set now (publishing.md), while CI publishes only `Zyggy.Cli` (Node/Hub publish is 07/11). |
| §12 "unit tests green in CI" | Keep | `.github/workflows/ci.yml` | GitHub Actions is the only CI the repo host offers for free with no secrets. |
| §12 "integration suite against the fake `claude` and a local bare repo" | Keep | `BusRepoFixture` + fake-claude `done` | The two harnesses, thin. |
| §12 gates under `Gates/P<n>_*.cs`, non-default tenant | Defer | 05 (`P0_ProbeRoundTrip.cs`) | No round trip exists to gate; the folder is created by 05. |
| §12 "runbook entry for any new failure mode" | Reshape | Troubleshooting sections in `tools/fake-claude/README.md` and `tests/Zyggy.Integration/README.md` | Every failure mode here is developer/CI-facing; the operational runbooks directory does not exist until 10. |
| §9 `tools/fake-claude/` "script that emits canned stream-json for tests" | Reshape (decided by the user 2026-09-28, ex-Q1) | Compiled console project `tools/fake-claude/FakeClaude.csproj` → `fake-claude` executable; contract fixed below | The real `claude` on Windows is a native executable, so the fake must be launchable as `claude.path` by `System.Diagnostics.Process` with no interpreter: a `.ps1` cannot be, and a `.cmd` shim or `powershell.exe -File` (Windows PowerShell 5.1, the only interpreter guaranteed without a new prerequisite) corrupts arguments containing `"` and newlines — the rendered prompt. A compiled fake gets byte-exact argv from the runtime on both OSes, keeps 06's `ClaudeCodeCliRunner` free of test-only launch logic, needs no exec bits, shebangs or execution policies, and is formatted/analysed like all other code. ~40 lines, no packages. |
| skill: scenarios `done`, `no-report`, `hang`, `error` | Keep `done` only; Defer the rest | `scenarios/done.jsonl` (placeholder) | Roadmap 06 owns the other three and the real `stream-json` capture. |
| §6 Invocation command line | Keep as capture-fidelity test vector | AC-5 | 01 proves the fake records arguments faithfully; asserting the *contract* (`ClaudeCodeCliRunner` passes the right flags) is 06. See "Findings forwarded" for the `--cwd` discrepancy. |
| skill: scenario chosen by env var *or* first positional argument | Reshape | env var `ZYGGY_FAKE_CLAUDE_SCENARIO` only | A positional argument collides with the real command line the fake must accept unchanged. |
| skill: "writes the arguments it received to a file" | Reshape | NUL-delimited UTF-8 capture at `ZYGGY_FAKE_CLAUDE_CAPTURE`, first record = working directory | Arguments contain newlines and quotes (the rendered prompt); one-per-line cannot represent them; NUL is the only byte that cannot occur in an argument on either OS. The working directory is recorded because the real CLI has no `--cwd` flag (see Findings). Per-test unique path avoids parallel collisions. |
| skill: `BusRepoFixture` with `Tenant { get; } = "geoffrey"` and a seeded `tenants/<tenant>/…` layout | Reshape | Fixture without any tenant member and without layout seeding in 01 | §9 forbids a default tenant; xUnit constructs class fixtures without parameters, so "constructor parameter, no default" is not expressible for `IClassFixture<T>`. The tenant is therefore an explicit parameter of the seeding call that 04 adds (`SeedTenantAsync(TenantId tenant, …)` built on the production `BusPaths`), matching §9's "explicit `TenantId` on every call". *Deviation from the brief's wording, same intent.* |
| tests.md: `[Trait("Category", "Integration")]` per class | Reshape | `[assembly: Trait("Category", "Integration")]` in `tests/Zyggy.Integration/AssemblyInfo.cs` | xunit.v3's `TraitAttribute` targets `Assembly | Class | Method` (src/xunit.v3.core/TraitAttribute.cs); one line, no class can forget it. |
| tests.md golden-file contract | Keep | `tests/golden/README.md`, `<None Include="../golden/**" CopyToOutputDirectory="PreserveNewest" LinkBase="golden" />` in `Zyggy.Core.Tests.csproj`, `.gitattributes -text` | The contract is protocol-critical (03); wiring it now costs nothing and lets 03 start in RED. |
| `dotnet format` gate | Keep | `.editorconfig` (new) | Required by AC-3; contents below. |
| publishing.md analyzer props | Keep | `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `InvariantGlobalization=true`, `GenerateDocumentationFile=true` for `Zyggy.Core` only | Chosen values and pre-approved suppressions below. |
| Reproducible SDK | Keep | `global.json` `{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }` | "Green from a clean clone" needs the SDK major pinned; .NET 11 previews are current in 2026. No `test.runner` entry (VSTest mode). |
| Cross-OS format check | Keep | `.gitattributes` `* text=auto eol=lf` + exceptions | Without it Windows checkouts are CRLF and `dotnet format --verify-no-changes` disagrees between OSes. |
| Template placeholders | Keep (remove) | see AC-13 | DoD item 8. |
| Per-project READMEs | Keep (verify) | AC-14 | Already product text. |
| `CLAUDE.md` "Build and test" | Keep (update) | AC-15 | DoD item 10 (O13). |
| §9 `IProcessRunner` for git/claude | Defer | 04 (first production process use) | Test infrastructure deliberately uses `System.Diagnostics.Process` directly so a future `GitClient` bug cannot mask itself (skill rule). |
| §9 no static mutable state | Keep | fixture and fake are instance/stateless; capture path per invocation | Applies to harness code too. |
| §14 tenancy shape | Keep (constraint) | AC-11 | Not exercised, never contradicted. |
| `dotnet test` MTP mode (`global.json` test runner) | Defer | follow-up when docs/commands are migrated | Cheap to switch later (one `global.json` entry, two packages removed, command syntax changes); no value for the round trip now. |
| Release workflow on tags, `zyggy --version` behaviour, Node/Hub publish in CI | Defer | 08 / 07 / 11 | Per the brief. |

---

## Contracts

### Repository layout after this deliverable

```
global.json                          SDK pin (10.0.100, rollForward latestFeature)
Directory.Build.props                shared build settings (below)
Directory.Packages.props             central package versions (below)
.editorconfig                        format/style rules (below)
.gitattributes                       LF normalisation + binary exceptions (below)
.github/workflows/ci.yml             build · format · test · publish · smoke-run · upload (below)
Zyggy.slnx                           + <Folder Name="/tools/"> containing tools/fake-claude/FakeClaude.csproj
src/Zyggy.Core/                      no source files; csproj only
src/Zyggy.Node/Program.cs            host builder, no hosted services
src/Zyggy.Cli/Program.cs             `return 0;`
src/Zyggy.Hub/Program.cs             `return 0;`
tests/Directory.Build.props          imports the root props; test-only settings and packages (below)
tests/Zyggy.Core.Tests/Infrastructure/TestStackSmokeTests.cs
tests/Zyggy.Integration/AssemblyInfo.cs                       [assembly: Trait("Category", "Integration")]
tests/Zyggy.Integration/Infrastructure/BusRepoFixture.cs
tests/Zyggy.Integration/Infrastructure/FakeClaude.cs          locator + capture decoder
tests/Zyggy.Integration/Bus/BusRepoFixtureSmokeTests.cs
tests/Zyggy.Integration/Jobs/FakeClaudeTests.cs
tests/golden/README.md
tools/fake-claude/FakeClaude.csproj  console project, AssemblyName fake-claude (contract below)
tools/fake-claude/Program.cs         capture → resolve scenario → stream bytes → exit
tools/fake-claude/scenarios/done.jsonl
tools/fake-claude/README.md          contract + PLACEHOLDER notice + troubleshooting
```

Files removed: `src/Zyggy.Core/Class1.cs`, `src/Zyggy.Node/Worker.cs`, `tests/Zyggy.Core.Tests/UnitTest1.cs`, `tests/Zyggy.Integration/UnitTest1.cs`. `appsettings*.json` and `launchSettings.json` in `Zyggy.Node` are untouched (07 owns Node configuration; they contain no secret).

### `Directory.Build.props` (root) — keys

| Key | Value | Why |
|-----|-------|-----|
| `TargetFramework` | `net10.0` | §13 |
| `LangVersion` | `14` | §9 |
| `Nullable`, `ImplicitUsings` | `enable` | §9 |
| `TreatWarningsAsErrors` | `true` | §9 |
| `AnalysisLevel` | `latest-recommended` | Catches real defects while the codebase is empty (cheapest moment); noisy rules are pre-suppressed in `.editorconfig` (below). |
| `EnforceCodeStyleInBuild` | `true` | Style rules at `warning` fail the build exactly as `dotnet format` reports them — one rule set, two enforcers. |
| `InvariantGlobalization` | `true` | Zyggy has no UI and no locale-dependent output; the Linux self-contained binary then needs no `libicu`, which matters for the Central VM/container image (§14). |
| `GenerateDocumentationFile` | `true` **only when** `MSBuildProjectName == Zyggy.Core` | public-api.md: every public `Zyggy.Core` member is documented; CS1591 becomes an error there and nowhere else. |
| `MinVerTagPrefix` | `v` | publishing.md tags are `v0.1.0`. |

Not set anywhere: `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `PackageVersion` (MinVer), `UserSecretsId`, `GeneratePackageOnBuild`.

### `tests/Directory.Build.props`

Imports the root props (`GetPathOfFileAbove`), then for every test project: `IsPackable=false`, `OutputType=Exe` (xunit.v3 test projects are executables), package references `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`, `FluentAssertions`, `NSubstitute`, `Microsoft.Extensions.TimeProvider.Testing`, and global usings `Xunit`, `FluentAssertions`. The two test `.csproj` files then contain only their project references (and `Zyggy.Core.Tests` the golden `None` item).

### `Directory.Packages.props`

`ManagePackageVersionsCentrally=true`; one `PackageVersion` per package in the Dependencies table, exact versions; `<GlobalPackageReference Include="MinVer" Version="8.0.0" />`.

### Host projects

| Project | SDK | `OutputType` | `AssemblyName` | `RootNamespace` | References | Entry point |
|---------|-----|--------------|----------------|-----------------|------------|-------------|
| `Zyggy.Core` | `Microsoft.NET.Sdk` | library | (default) | `Zyggy.Core` | — | none (empty assembly) |
| `Zyggy.Node` | `Microsoft.NET.Sdk.Worker` | Exe | `zyggy-node` | `Zyggy.Node` | `Zyggy.Core`; `Microsoft.Extensions.Hosting` | `Host.CreateApplicationBuilder(args).Build().Run();` |
| `Zyggy.Cli` | `Microsoft.NET.Sdk` | Exe | `zyggy` | `Zyggy.Cli` | `Zyggy.Core` | `return 0;` |
| `Zyggy.Hub` | `Microsoft.NET.Sdk` | Exe | `zyggy-hub` | `Zyggy.Hub` | `Zyggy.Core` | `return 0;` |

### `tools/fake-claude/FakeClaude.csproj` — contract

| Aspect | Value |
|--------|-------|
| SDK / `OutputType` | `Microsoft.NET.Sdk` / `Exe` (top-level statements in `Program.cs`) |
| `AssemblyName` / `RootNamespace` | `fake-claude` / `FakeClaude` (a hyphen is legal in an assembly name, not in a namespace) |
| `IsPackable`, `IsPublishable` | `false`, `false` — never packed, never published; CI publishes `Zyggy.Cli` only |
| Build settings | Inherits the root `Directory.Build.props` unchanged (same analyzers, warnings as errors, invariant globalization); no project-local overrides |
| References | None — no `PackageReference`, no `ProjectReference` (it must never depend on `Zyggy.Core`, or a `Core` bug could mask itself in the fake) |
| Items | `<Content Include="scenarios/**" CopyToOutputDirectory="PreserveNewest" />` so the scenarios sit next to the executable in every output directory |
| Solution | `Zyggy.slnx` → `<Folder Name="/tools/"><Project Path="tools/fake-claude/FakeClaude.csproj" /></Folder>` |
| Consumption | `Zyggy.Integration` holds a plain `<ProjectReference Include="../../tools/fake-claude/FakeClaude.csproj" />`. The SDK's referenced-Exe handling copies the apphost (`fake-claude[.exe]`), `fake-claude.dll` and `fake-claude.runtimeconfig.json` into the test output, and the default transitive content copy brings `scenarios/**` along (AC-24). No custom target, no post-build copy, no `Exec`. |
| Not a test project | Lives under `tools/`, so `tests/Directory.Build.props` (xunit packages, `Category` trait) does not apply to it |

### `.editorconfig` — contract

- `root = true`; `[*]`: `charset = utf-8`, `end_of_line = lf`, `insert_final_newline = true`, `trim_trailing_whitespace = true`, `indent_style = space`, `indent_size = 4`; `[*.{json,yml,yaml,xml,csproj,props,targets,slnx}]`: `indent_size = 2`.
- `[*.cs]`: the `dotnet new editorconfig` SDK template as baseline, with these rules raised to `warning` so the build and `dotnet format` enforce them identically: `IDE0055` (formatting), `IDE0161` (file-scoped namespaces), `IDE0005` (unnecessary usings), `dotnet_sort_system_directives_first = true`, `csharp_prefer_braces = true`.
- Pre-approved analyzer suppressions (set to `suggestion`, each with a comment), applied only if the rule fires under `latest-recommended`: `CA1848` (LoggerMessage delegates — §11 logs once per state change), `CA2007` (`ConfigureAwait` — application code, no synchronisation context), `CA1031` (catch general exception — `JobRunner` must catch everything by §9), `CA1303` (literal strings — no localisation, invariant globalization), `CA1812` (internal class never instantiated — DI-registered `internal sealed` implementations per public-api.md). For `[tests/**.cs]` additionally `CA1707` (underscores — tests.md naming) and `CA1515` (public types in an Exe — xunit needs public test classes) set to `none`.
- Any further suppression needs a one-line reason in `.editorconfig` and is reviewed at the deliverable's gate.
- `[tests/golden/**]` and `[tools/fake-claude/scenarios/**]`: `insert_final_newline = false`, `trim_trailing_whitespace = false`, `end_of_line = unset` (these bytes are a protocol, never touched by editors).

### `.gitattributes` — contract

```
* text=auto eol=lf
tests/golden/** -text
tools/fake-claude/scenarios/** -text
```

Consequence: every text file is LF in git and in every working tree (Windows included); the existing files are renormalised once in this deliverable. No file needs an executable bit: the fake is a compiled project, and its apphost is produced by the build, not committed.

### CI workflow `.github/workflows/ci.yml` — contract

| Aspect | Value |
|--------|-------|
| Triggers | `push` to `main`, `pull_request` |
| Permissions | `permissions: contents: read` (job-level default); no `secrets.*` reference anywhere; the implicit `GITHUB_TOKEN` is used only by the artefact upload action |
| Concurrency | one run per ref, `cancel-in-progress: true` |
| Job `build` | `strategy.matrix.os: [windows-latest, ubuntu-latest]`, `fail-fast: false` |
| Steps | checkout with `fetch-depth: 0` (MinVer needs tags and height) → setup-dotnet from `global.json` → `dotnet restore Zyggy.slnx` → `dotnet build Zyggy.slnx -c Release --no-restore` → `dotnet format Zyggy.slnx --verify-no-changes --no-restore` → `dotnet test Zyggy.slnx -c Release --no-build` → `dotnet publish src/Zyggy.Cli -c Release -r <rid> --self-contained -p:PublishSingleFile=true -o artifacts/<rid>` where `<rid>` is `win-x64` on Windows and `linux-x64` on Linux → run `artifacts/<rid>/zyggy[.exe]` and require exit code 0 → upload `artifacts/<rid>` as artefact `zyggy-<rid>` |
| Actions | current majors as listed on the releases pages on 2026-09-28: `actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7`; the planner re-confirms the majors when writing the file |
| Runner tools relied on | .NET SDK (installed by setup-dotnet), `git` (pre-installed); nothing else — no PowerShell/bash step is needed for the fake, which `dotnet build Zyggy.slnx` compiles as part of the solution |
| fake-claude in CI | Built and format-checked with the solution, exercised by `dotnet test` through `Zyggy.Integration`; **never** published or uploaded (the publish step targets `src/Zyggy.Cli` only and the fake sets `IsPublishable=false`) |

Design choice (brief Q4): each OS publishes and smoke-runs its own RID. Cross-RID publish from one runner would also work, but only the matching runner can *execute* the artefact, and the smoke run is the only proof that the single-file, invariant-globalization binary starts.

### fake-claude — behavioural contract (compiled `fake-claude` executable)

| Aspect | Contract |
|--------|----------|
| Location | Source in `tools/fake-claude/`; at run time the executable resolves scenarios at `<directory of the running executable (AppContext.BaseDirectory)>/scenarios/<name>.jsonl`, so it works from its own output directory and from the `Zyggy.Integration` output alike |
| Inputs | Any argument vector (never validated, never parsed); env `ZYGGY_FAKE_CLAUDE_SCENARIO` (scenario name without extension, default `done`); env `ZYGGY_FAKE_CLAUDE_CAPTURE` (absolute path of the capture file; optional — unset means no capture) |
| Order of operations | 1. write the capture file (if requested) 2. resolve the scenario 3. stream the scenario file's bytes to stdout **unchanged** (no re-encoding, no BOM, no line-ending translation) 4. exit 0 |
| Stdin | never read (a runner that pipes the prompt must not block) |
| Stderr | empty on success; one line `fake-claude: <message>` on failure |
| Exit codes | `0` success; `3` unknown scenario; `4` capture file could not be written |
| Capture format | UTF-8 (no BOM); a sequence of records each terminated by a single `\0` byte; record 0 = the fake's absolute working directory; records 1..n = the arguments in order, bytes preserved; an empty argument is an empty record; the file for zero arguments contains only record 0 |
| Concurrency | stateless; parallel invocations differ only by their capture path, which each test derives from its own temp directory |
| `scenarios/done.jsonl` | Three JSON lines, each containing `"placeholder": true`: a `system`/`init` line, an `assistant` line whose text contains a `REPORT` section (`summary`, `files_changed`, `open_questions`), and a `result` line with `cost_usd`, `duration_ms`, `num_turns`, `is_error: false`. LF endings, final LF. **This is a documented placeholder for the real `stream-json` shape, which 06 captures once from a real `claude -p` run outside the test suite and checks in; the `placeholder` markers are removed then.** |
| `README.md` | Documents the table above, the PLACEHOLDER status, how to run the fake by hand, and the troubleshooting entries from Failure modes |

Test-side helper `tests/Zyggy.Integration/Infrastructure/FakeClaude.cs` (static, stateless):

- `string ExecutablePath` — absolute path of the `fake-claude` apphost in the test output (`AppContext.BaseDirectory` + `fake-claude.exe` on Windows, `fake-claude` elsewhere); this is the value 06 passes as `claude.path`, launched exactly like the real CLI — no launcher prefix, no interpreter. Throws `FileNotFoundException` naming the path if the file is absent (a missing `ProjectReference`), so the failure is explicit rather than a confusing `Win32Exception` from `Process.Start`.
- `string ScenarioDirectory`.
- `static FakeClaudeCapture ReadCapture(string path)` → `record FakeClaudeCapture(string WorkingDirectory, IReadOnlyList<string> Arguments)`; decoding the NUL-delimited format is the only logic. 06 reuses it to assert the §6 contract.

### `BusRepoFixture` — contract (`namespace Zyggy.Integration.Infrastructure`)

```csharp
public sealed class BusRepoFixture : IAsyncLifetime          // xunit.v3: ValueTask, IAsyncDisposable
{
    public string RootDir  { get; }                           // <temp>/zyggy-it/<unique>
    public string BareDir  { get; }                           // <RootDir>/bus.git   — plays "origin" (GitHub)
    public string CloneDir { get; }                           // <RootDir>/bus       — the node's checkout

    public ValueTask InitializeAsync();                       // git init --bare --initial-branch=main; git clone; local identity; branch main
    public ValueTask DisposeAsync();                          // delete RootDir; swallow IO errors

    public Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct); // trimmed stdout; throws on non-zero exit with stderr in the message
    public Task<string> HeadShaAsync(CancellationToken ct);                       // git rev-parse main   (bare)
    public Task<string> ShowAsync(string path, CancellationToken ct);             // git show main:<path> (bare)
    public Task<string> LastCommitSubjectAsync(CancellationToken ct);             // git log -1 --format=%s main (bare)
}
```

- No tenant member, no seeding, no bus paths (04 adds `SeedTenantAsync(TenantId tenant, …)` and any `tenants/…` knowledge through the production `BusPaths`).
- Git identity set in the **clone's local config**: `user.name = zyggy (test)`, `user.email = test@zyggy.org`; also `commit.gpgsign = false`, `core.autocrlf = false`.
- Every git call from the fixture runs with `GIT_CONFIG_GLOBAL=<RootDir>/gitconfig` (an empty file), `GIT_CONFIG_NOSYSTEM=1`, `GIT_TERMINAL_PROMPT=0`, and never inherits a machine-wide identity, hook path, signing or credential helper.
- The clone's current branch is `main` regardless of the machine's `init.defaultBranch`.
- Used as `IClassFixture<BusRepoFixture>` (one bare repo + clone per test class) — and constructible directly for per-test isolation.

### `tests/golden/README.md` — contract stated

`<case>.md` (envelope as on the bus) + `<case>.canonical` (exact bytes `EnvelopeSigner.Canonicalize` must produce) + `<case>.sig` (expected HMAC hex for the fixed test key); byte-exact, `-text` in git, copied to the test output under `golden/`, enumerated by a `[Theory]` + `[MemberData]` in `Zyggy.Core.Tests`; the key id and test secret are fixed by `_specs/03-envelope-signing.md`; a new case is added in RED for any canonicalisation or field change; no cases exist before 03.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `ZYGGY_FAKE_CLAUDE_SCENARIO` | process environment of the fake | `done` | set per invocation by the test |
| `ZYGGY_FAKE_CLAUDE_CAPTURE` | process environment of the fake | unset (no capture) | set per invocation to a unique path |
| `MinVerTagPrefix` | `Directory.Build.props` | `v` | never per project |
| `MinVerSkip` | command line / CI only | unset | `-p:MinVerSkip=true` for a build without git (not used by CI) |
| SDK version | `global.json` | `10.0.100`, `latestFeature` | edited only by a deliverable that bumps the SDK |
| Test runner | `global.json` (absent) | VSTest | adding `test.runner` = switching to MTP (deferred) |

---

## Behaviors & Conventions

- One `Directory.Build.props` for the whole solution; `tests/Directory.Build.props` adds test-only settings and packages; a `.csproj` contains only `Sdk`, `OutputType`/`AssemblyName` where applicable, project references and project-specific items. Override: none — a setting that a single project needs is an Open Question in that project's deliverable.
- Every package version lives in `Directory.Packages.props`, exact. Override: none (NU1008 fails the restore).
- Warnings are errors everywhere, including analyzers and style rules at `warning`. Override: a rule-level severity in `.editorconfig` with a comment, reviewed at the gate.
- Every text file is LF; golden and scenario bytes are never normalised. Override: an explicit `.gitattributes` line per path.
- CI runs the same three commands as the RGR-Proof loop's PROVE step and publishes only `Zyggy.Cli`. Override: publish of Node/Hub is added by 07/11, a tag-triggered release workflow by a later deliverable.
- Test projects are xunit.v3 executables run through VSTest; `Category=Integration` is an assembly-level trait on `Zyggy.Integration`; `Zyggy.Core.Tests` carries no category. Override: none until the MTP migration.
- Tests never touch the network, the real `claude`, or machine-wide git configuration; the fixture isolates git through environment variables and local config.
- The fake never validates arguments; validation of the §6 contract is a test in 06 that decodes the capture.
- MinVer versions every assembly from git tags (`v*`); without a tag the version is `0.0.0-alpha.0.<height>`; CI checks out full history so heights are stable.

---

## Failure modes

| Situation | Observable outcome | Runbook entry (README section) |
|-----------|-------------------|-------------------------------|
| `git` not on PATH, or older than 2.32 (`--initial-branch`, `GIT_CONFIG_GLOBAL`) | `BusRepoFixture.InitializeAsync` throws with the git stderr and the prerequisite in the message; integration tests **fail** (never skip — a skipped harness hides breakage) | `tests/Zyggy.Integration/README.md` → "Prerequisites" |
| Machine git config sets `commit.gpgsign`, hooks, or a credential helper | No effect: fixture calls use an empty global config and no system config | same → "Isolation from machine config" |
| Temp root cannot be deleted on dispose (Windows file locks) | Swallowed; directories may accumulate under `<temp>/zyggy-it/` | same → "Leaked temp directories" |
| Unknown scenario name | Fake exits 3, stderr `fake-claude: unknown scenario '<name>'`, stdout empty, capture written | `tools/fake-claude/README.md` → "Exit codes" |
| Capture path unwritable | Fake exits 4, stderr message, stdout empty | same |
| `ZYGGY_FAKE_CLAUDE_CAPTURE` unset | No capture, normal output; a test that expected a capture fails on the missing file | same |
| `fake-claude[.exe]` missing from the `Zyggy.Integration` output (the `ProjectReference` was removed, or the build was partial) | `FakeClaude.ExecutablePath` throws `FileNotFoundException` naming the expected path; every fake-based test fails with that message | same → "Executable not found" |
| `dotnet format --verify-no-changes` reports differences | CI red with the file list; fix with `dotnet format Zyggy.slnx` | `CLAUDE.md` "Build and test" |
| CRLF committed by an editor that ignores `.gitattributes` | `dotnet format` fails on Linux; `git ls-files --eol` shows `i/crlf` | `CLAUDE.md` "Build and test" |
| A new warning under `latest-recommended` (e.g. after an SDK feature-band roll-forward) | Build red with the rule id; fix or add a commented suppression | `.editorconfig` header comment |
| MinVer cannot see git history (shallow clone, source zip) | MinVer warning, version `0.0.0-alpha.0`; MinVer's MSBuild warnings are not compiler warnings and do not fail the build | `tools/fake-claude/README.md` is not the place — noted in `CLAUDE.md` "Build and test" |
| Published binary exits non-zero on the runner | CI red at the smoke step — the single-file/self-contained/invariant-globalization publish itself is broken | `CLAUDE.md` "Build and test" |
| A test project ends up with zero tests | VSTest warns "No test is available"; each project keeps at least one smoke test so this never happens silently | `tests/Zyggy.Integration/README.md` |

---

## Dependencies

All versions and licences verified on nuget.org on 2026-09-28. Every package is MIT/Apache-2.0/BSD-3, works under a single-file self-contained publish (the test/build packages are never published), and is pinned in `Directory.Packages.props`.

| Package | Version | License | Projects | Why (what bespoke code it removes) |
|---------|---------|---------|----------|------------------------------------|
| `xunit.v3` | 4.0.1 | Apache-2.0 | tests | Test framework named by §9; v2 is deprecated on nuget.org. |
| `xunit.runner.visualstudio` | 4.0.0 | Apache-2.0 | tests | VSTest adapter so `dotnet test --filter` works as documented. |
| `Microsoft.NET.Test.Sdk` | 18.10.1 | MIT | tests | VSTest host for `dotnet test`. |
| `coverlet.collector` | 10.0.1 | MIT | tests | Opt-in coverage collector (already in the scaffold); no thresholds. |
| `FluentAssertions` | 7.2.2 | Apache-2.0 | tests | Assertions named by §9; last Apache line (8.x is commercial). ⚠️ never bump to 8.x. |
| `NSubstitute` | 6.2.0 | BSD-3-Clause (Castle.Core Apache-2.0) | tests | Substitutes at the five seams (tests.md). |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.10.0 | MIT | tests | `FakeTimeProvider`; tests.md forbids real time. |
| `Microsoft.Extensions.Hosting` | 10.0.12 | MIT | `Zyggy.Node` | §9 Worker hosting (already present; bumped from 10.0.4). |
| `MinVer` | 8.0.0 | Apache-2.0 | all (GlobalPackageReference, build-only) | Version from git tags (publishing.md); removes hand-set `<Version>`. ⚠️ new build dependency. |

Not added (deferred to owning deliverables): YamlDotNet, System.CommandLine, ModelContextProtocol, Ulid, Serilog.*, OpenTelemetry.*, Meziantou.Framework.Win32.CredentialManager, Microsoft.Extensions.Hosting.WindowsServices/.Systemd. No LibGit2Sharp, ever.

`tools/fake-claude/FakeClaude.csproj` references no package at all (the BCL's `Environment.GetCommandLineArgs`/`args`, `Console.OpenStandardOutput`, `File` and `AppContext.BaseDirectory` cover the whole contract); the compiled fake adds no dependency to the table above.

---

## Deliberate deviations from the founding spec and the hand-off brief

- **xunit v2 → xunit.v3** (§9 says `xunit`): v2 is deprecated; v3 under VSTest keeps every documented command. Decision table row "§9 Packages: xunit".
- **`BusRepoFixture` has no tenant parameter in 01** (brief: "takes the tenant as a constructor/parameter with no default"): xUnit fixtures are parameterless; the explicit, default-less tenant parameter moves to the seeding call 04 adds. Same intent (§9 no default tenant), different placement.
- **Capture format and scenario selection** differ from the integration-testing skill's sketch (env var only; NUL-delimited records with the working directory first) for argument fidelity and because the real CLI has no `--cwd` flag.
- **Assembly-level `Category=Integration` trait** instead of per-class attributes (tests.md).
- **Runbook entries are README troubleshooting sections** (§12) because operational runbooks do not exist before 10.
- **Compiled fake instead of `fake-claude.ps1` + `fake-claude.sh`** (§9 calls it a "script"; the brief's DoD item 5 names the two script files) — decided by the user on 2026-09-28 (ex-Q1). Reason: the fake must be launchable as `claude.path` by the same `Process.Start` code path as the real native `claude` executable on both OSes; a `.ps1` needs an interpreter (test-only branching in 06's runner or a `.cmd` shim) and Windows PowerShell 5.1's `-File` argument handling corrupts prompts containing `"` and newlines, which would undermine the capture as a test contract. Behaviour, scenarios, env vars and capture format are exactly what the brief asked for; only the vehicle differs. Decision table row "§9 `tools/fake-claude/`".

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| Argument containing `\n`, `\t`, `"`, or a leading `-` passed to the fake | Captured byte-exact in its own NUL-terminated record; the fake never interprets it. |
| Empty argument (`""`) passed to the fake | An empty record between two NULs; decoder returns `""` at that index. |
| Two tests run the fake concurrently | Both succeed; each reads its own capture path; `done.jsonl` is only read. |
| Test run from a directory whose path contains spaces (Windows temp paths often do) | Fixture and fake work; paths are never string-concatenated into shell commands (`ArgumentList`, not `Arguments`). |
| `init.defaultBranch=master` on the developer machine | Bare repo and clone are still on `main`. |
| Global `core.autocrlf=true` on Windows | Fixture content round-trips byte-exact (`core.autocrlf=false` locally, global config ignored). |
| The fake is started from a working directory other than its own (as the runner will do) | Scenarios are still found: lookup is relative to `AppContext.BaseDirectory`, never to the current directory; record 0 of the capture holds the caller's working directory. |
| CI runs on a fork PR without secrets | Everything still passes: the workflow uses none. |
| Build without `.git` (source zip) | MinVer warns, version `0.0.0-alpha.0`; build succeeds. |

---

## Out of Scope

- fake-claude scenarios `no-report`, `hang`, `error` and the real `stream-json` capture (06); any parsing or validation of the fake's arguments (06 asserts the §6 contract from the capture).
- §9 source folders under `Zyggy.Core` (`Tenancy/`, `Envelope/`, `Bus/`, …) — created by the first deliverable that needs each.
- Per-project README content beyond verifying no template text.
- Coverage thresholds or coverage in CI.
- Publish of `Zyggy.Node` / `Zyggy.Hub` in CI (07, 11); a release workflow on tags; GitHub Releases.
- Any production type from §4–§7: no `IProcessRunner`, `IBusProvider`, `BusPaths`, envelope types, `TenantId`, `GitClient`, `ClaudeProcess`.
- `zyggy --version` behaviour and the health endpoint's version field (08/07) — MinVer merely makes the informational version available.
- `dotnet test` MTP mode (`global.json` `test.runner`); `Gates/` folder and any gate scenario (05).
- Seeding a tenant layout in `BusRepoFixture`, a second "foreign sender" clone, push-rejection helpers (04).
- `InternalsVisibleTo` or in-process CLI invocation helpers (08).
- §1 non-goals and §13/§14 deferred items.

---

## Findings forwarded to later deliverables (not blocking 01)

1. **§6 `--cwd` does not exist.** The Claude Code CLI reference (code.claude.com/docs/en/cli-reference, read 2026-09-28) documents `--output-format text|json|stream-json`, `--permission-mode default|acceptEdits|plan|auto|dontAsk|bypassPermissions`, `--allowedTools`/`--allowed-tools`, `--max-turns`, `--add-dir`, but no `--cwd`. 06's spec must set the run directory through `ProcessStartInfo.WorkingDirectory` (and possibly `--add-dir`) and raise the §6 amendment as an Open Question there. This is why the fake records its working directory as capture record 0.
2. **`claude.path` is always a directly launchable executable** on both OSes; 06's `ClaudeCodeCliRunner` must start it through `IProcessRunner` with `FileName = claude.path` and `ArgumentList`, with no launcher prefix, interpreter or shell — the compiled fake (decided ex-Q1) guarantees the tests exercise exactly that path.
3. **xunit.v3 analyzers** require passing `TestContext.Current.CancellationToken` into methods that accept a token; the fixture signatures above already take a `CancellationToken`.

---

## Open Questions

None. Q1 (fake-claude vehicle) was resolved by the user on 2026-09-28 in favour of the compiled console project; the decision is recorded in the Decision Table and under "Deliberate deviations".
