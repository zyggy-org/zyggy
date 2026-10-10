---
name: ci-test-tooling-gotchas
description: Recurring CI/build/test-environment quirks (trx naming, mawk, podman Linux-proof command, env vars in binary tests).
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
Build/test-environment quirks seen repeatedly: VSTest names a second same-second trx file `..._net10.0[1].trx`; PowerShell's `-Path` treats `[1]` as a wildcard, so reading such a file needs `-LiteralPath`. The CI container's `awk` is `mawk`, which silently ignores `{n,}` regex intervals — use `grep -E` or bash `[[ =~ ]]` instead. To get real Linux test facts without a CI run: `podman run --rm -v <repo>:/w -w /w mcr.microsoft.com/dotnet/sdk:10.0 bash -c '... dotnet test Zyggy.slnx --filter "FullyQualifiedName~_OnLinux"'`, copying the tree with `obj`/`bin`/`.git` excluded (a live `-v` mount must not be edited while tests run — use `git stash push --keep-index` to isolate one step first). GitHub Actions' Linux runners ignore SIGPIPE, so `cmd | head` inside a bats `run` can emit "write error: Broken pipe" into `$output`; capture to a file first. Binary tests that spawn the real CLI must pin `XDG_CONFIG_HOME`/`XDG_STATE_HOME` and strip `CREDENTIALS_DIRECTORY`, or a test can silently reach a real external host (happened once in CI, fixed in a45909b). A `dotnet run --file` script outside the repo misses the repo's `nuget.config` and tries to restore AOT runtime packs — add `#:property RestoreConfigFile=<repo>/nuget.config` and `#:property PublishAot=false`. zyggy-org's private-repo Actions minutes run out mid-month; `zyggy`/`zyggy-core` are public (free minutes) — batch docs-only commits to conserve the private repo's budget.

**Why:** each of these caused a real CI failure or a wasted debugging turn (the ci.yml OS-fact check, podman Linux-proof runs, deliverable 33's CI secret leak).
**How to apply:** check this list before debugging a red CI run or writing a new binary/integration test.
