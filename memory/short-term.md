# Short-term memory

<!-- Working memory of the agent, injected at every session start. Write here during a session what may be worth
     remembering: a project fact, a convention, a gotcha, a correction of a long-term memory (name its file), an
     open question. One bullet each, dated, short. At the next /evolve:propose, consolidation keeps what matters in
     long-term memory and clears this file. Limit: 200 lines / 25 KB; the Stop hook asks you to compress it when over. -->
- 2026-09-29: On the work laptop the user-level NuGet config disables nuget.org (only riziv-inami enabled), so `dotnet build Zyggy.slnx` fails with NU1101 (MinVer, Ulid). Fixed by the repo nuget.config (2026-09-29).
- 2026-09-29: `dotnet run --file x.cs` scripts outside the repo miss the repo nuget.config (work laptop has nuget.org disabled) and try to restore AOT runtime packs; add `#:property RestoreConfigFile=<repo>/nuget.config` and `#:property PublishAot=false`. `gh` is not installed and api.github.com is unreachable from the agent shell, so CI status must come from the owner.
- 2026-09-29: VSTest trx names collide when test projects finish in the same second -> `...[1].trx`; in PowerShell read such paths with `-LiteralPath` (plain `-Path` treats `[1]` as a wildcard). Hit in ci.yml "Check OS-specific facts ran".
