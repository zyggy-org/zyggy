# Zyggy.Integration

End-to-end tests against the real edges, without the real world. The founding spec §9 calls for exactly this: "end-to-end against a local bare git repo and a fake claude script".

## The two harnesses

| Harness | Replaces | How |
|---------|----------|-----|
| **Bare repo bus** | GitHub | Each test gets a bare git repository (playing `origin`) and a working clone (the node's bus checkout) under a temp directory. The real `GitClient` runs real `git` against it. Assertions read the bare repo: `git show main:<path>`, `git log -1 --format=%s`. |
| **fake-claude** | the `claude` CLI | `tools/fake-claude` is a script that emits canned `stream-json` scenarios (done, no REPORT section, hang, error) and records the arguments it was called with. The real `ClaudeProcess` runs it exactly like the real CLI. |

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
