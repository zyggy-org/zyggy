---
description: "Project-file and publishing conventions for Zyggy: shared settings live in Directory.Build.props, hosts publish as single-file self-contained binaries (zyggy-node, zyggy, zyggy-hub) for win-x64 and linux-x64, versions come from git tags. Activates when editing .csproj files."
applyTo: "**/*.csproj"
---
# Project files and publishing

## Shared settings

`Directory.Build.props` at the repo root sets, for every project: `net10.0`, `LangVersion` 14, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`. **Do not repeat these in a `.csproj`** and do not override them per project; the scaffold's per-project copies are to be removed in the scaffolding deliverable.

Add to the props file (not per project) when the scaffolding deliverable lands: `GenerateDocumentationFile` for `Zyggy.Core`, `AnalysisLevel`, `EnforceCodeStyleInBuild`, `InvariantGlobalization`.

## Project roles and references

| Project | SDK / OutputType | References |
|---------|------------------|------------|
| `Zyggy.Core` | `Microsoft.NET.Sdk`, library | NuGet only (YamlDotNet, System.Text.Json source-gen, Ulid, Microsoft.Extensions.*) |
| `Zyggy.Node` | `Microsoft.NET.Sdk.Worker`, Exe | `Zyggy.Core`; `Microsoft.Extensions.Hosting.WindowsServices`, `.Systemd`; Serilog |
| `Zyggy.Cli` | `Microsoft.NET.Sdk`, Exe (`AssemblyName` `zyggy`) | `Zyggy.Core`; System.CommandLine |
| `Zyggy.Hub` | `Microsoft.NET.Sdk`, Exe | `Zyggy.Core`; ModelContextProtocol |
| `tests/*` | `Microsoft.NET.Sdk`, `IsPackable=false` | project under test; xunit, FluentAssertions, NSubstitute, Microsoft.Extensions.TimeProvider.Testing |

Never reference `Zyggy.Node`, `Zyggy.Cli` or `Zyggy.Hub` from each other or from `Zyggy.Core`.

## Packages

- Every new `PackageReference` is listed in the deliverable's spec with its license and justification (founding spec §9 package table is the baseline).
- Pin exact versions. Prefer packages that work in a single-file, self-contained, trimmed-unfriendly-but-not-trimmed publish; verify by running the publish command below before the gate.
- No NuGet *packaging* of any project: Zyggy ships binaries, not packages. Do not add `GeneratePackageOnBuild`.

## Publishing

```powershell
dotnet publish src/Zyggy.Node -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish src/Zyggy.Node -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o artifacts/linux-x64
# same for src/Zyggy.Cli and src/Zyggy.Hub
```

Artifacts are named `zyggy-node`, `zyggy`, `zyggy-hub` (set `AssemblyName` accordingly). The release workflow, once it exists, runs these on a tag on `main` and attaches the artifacts to a GitHub Release (see `.claude/agents/git.md`).

## Versioning

- Versions come from git tags on `main` (`v0.1.0`). Never set `<Version>`, `<PackageVersion>` or `<AssemblyVersion>` by hand in a `.csproj`; the scaffolding deliverable decides the tool that injects them.
- `zyggy --version` and the health endpoint report the informational version; the `schema:` field of envelopes is versioned separately in code (§9).

## Rules

- Do not add settings to a `.csproj` that belong in `Directory.Build.props`.
- Do not add `UserSecretsId` or any secret-bearing config: secrets come from `ISecretStore` (§8).
- Keep `launchSettings.json` free of real paths or tokens.
