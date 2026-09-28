# Zyggy.Integration

End-to-end tests against the real edges, without the real world. The founding spec §9 calls for exactly this: "end-to-end against a local bare git repo and a fake claude script".

## The two harnesses

| Harness | Replaces | How |
|---------|----------|-----|
| **Bare repo bus** | GitHub | Each test gets a bare git repository (playing `origin`) and a working clone (the node's bus checkout) under a temp directory. The real `GitClient` runs real `git` against it. Assertions read the bare repo: `git show main:<path>`, `git log -1 --format=%s`. |
| **fake-claude** | the `claude` CLI | `tools/fake-claude` is a compiled console project that emits canned `stream-json` scenarios (done, no REPORT section, hang, error) and records the arguments it was called with. The real `ClaudeProcess` runs it exactly like the real CLI. |

Never the real `claude`, never GitHub. A test that needs a network is wrong.

## Layout

| Folder | Contents |
|--------|----------|
| `Infrastructure/` | `BusRepoFixture`, fake-claude helpers, test host builders that use the production DI extension methods with only the claude path, bus remote and secret store swapped. |
| `Bus/`, `Jobs/`, `Cli/`, `Hub/` | Round trips per edge: claim and report moves, push-rejection retry, signature rejection, timeouts, CLI verbs in-process, Hub tools over stdio. |
| `Gates/` | `P<n>_<Name>.cs`: one file per delivery phase of founding spec §12, one fact per gate criterion. A gate is a working round trip from an empty bus to a report on `origin/main`, not a code review. A phase-closing deliverable is not done until its gate scenario passes. |

## Running

```powershell
dotnet test tests/Zyggy.Integration
dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P1_"
dotnet test Zyggy.slnx --filter "Category!=Integration"     # skip these for a fast unit-only run
```

Requires `git` on the PATH. Harness details and examples: `.claude/skills/integration-testing/SKILL.md`.

## Troubleshooting

### Prerequisites

`git` ≥ 2.32 must be on the PATH (`--initial-branch` and `GIT_CONFIG_GLOBAL` need it). When it is missing or too old, `BusRepoFixture.InitializeAsync` **fails** with git's stderr and the prerequisite in the message — it never skips, because a skipped harness hides breakage.

### Isolation from machine config

Every git call made by the fixture runs with `GIT_CONFIG_GLOBAL=<RootDir>/gitconfig` (an empty file), `GIT_CONFIG_NOSYSTEM=1` and `GIT_TERMINAL_PROMPT=0`. Your own `commit.gpgsign`, hooks, credential helpers and `init.defaultBranch` have no effect: identity (`zyggy (test)` / `test@zyggy.org`), `commit.gpgsign=false` and `core.autocrlf=false` are set in the clone's local config, and both repositories are on `main`.

### Leaked temp directories

Each fixture lives under `<temp>/zyggy-it/<unique>/` and deletes it on dispose (clearing the read-only attribute git puts on object files first). If Windows holds a file lock, the delete is swallowed and the directory stays behind; delete `<temp>/zyggy-it/` by hand when it accumulates.

### A test project with zero tests

VSTest warns "No test is available" instead of failing. Each test project keeps at least one smoke test so this can never happen silently; if you delete the last test in a project, add another one.
