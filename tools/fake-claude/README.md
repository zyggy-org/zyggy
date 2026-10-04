# fake-claude

A compiled stand-in for the `claude` CLI, used by `tests/Zyggy.Integration`. It is launched exactly like the real CLI (no interpreter, no shell), streams a canned `stream-json` scenario to stdout, and records what it was called with. It never validates arguments; asserting the §6 command line is the job of the tests that decode the capture.

## Behavioural contract

| Aspect | Contract |
|--------|----------|
| Location | Source in `tools/fake-claude/`; scenarios are resolved at `<AppContext.BaseDirectory>/scenarios/<name>.jsonl`, next to the running executable, never relative to the current directory |
| Inputs | Any argument vector (never parsed, never validated); env `ZYGGY_FAKE_CLAUDE_SCENARIO` (scenario name without extension, default `done`); env `ZYGGY_FAKE_CLAUDE_CAPTURE` (absolute path of the capture file; unset means no capture); env `ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE` (absolute path; stdin is read to the end and written there byte-exact; unset means stdin is never read); env `ZYGGY_FAKE_CLAUDE_DELAY_MS` (milliseconds to sleep before streaming, the "hang" scenario); env `ZYGGY_FAKE_CLAUDE_EXIT` (exit code after streaming, default 0) |
| Order of operations | 0. validate `ZYGGY_FAKE_CLAUDE_DELAY_MS` and `ZYGGY_FAKE_CLAUDE_EXIT` (non-negative integers) 1. write the capture file (if requested) 2. write the stdin capture (if requested) 3. sleep `ZYGGY_FAKE_CLAUDE_DELAY_MS` 4. resolve the scenario 5. stream the scenario file's bytes to stdout **unchanged** (no re-encoding, no BOM, no line-ending translation) 6. exit `ZYGGY_FAKE_CLAUDE_EXIT` (default 0) |
| Stdin | read only when `ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE` is set |
| Stderr | empty on success; one line `fake-claude: <message>` on failure |
| Exit codes | `0` success (or `ZYGGY_FAKE_CLAUDE_EXIT`); `3` unknown scenario; `4` a capture file could not be written; `5` invalid `ZYGGY_FAKE_CLAUDE_DELAY_MS` or `ZYGGY_FAKE_CLAUDE_EXIT` (`fake-claude: invalid <name> '<value>'`) |
| Capture format | UTF-8 without BOM; a sequence of records each terminated by a single `\0` byte; record 0 = the fake's absolute working directory; records 1..n = the arguments in order, bytes preserved; an empty argument is an empty record; with zero arguments the file holds only record 0 |
| Concurrency | stateless; parallel invocations differ only by their capture path |
| `scenarios/success-structured.jsonl` | `system/init` (`model: fake-model-1`), one `assistant` line, `result` `success` with `total_cost_usd: 0.0123`, `num_turns: 2`, `duration_ms: 1500`, `usage`, `permission_denials: []`, `structured_output: {"ok": true}` (spec 28) |
| `scenarios/error.jsonl` | `system/init` and a `result` with `is_error: true`, `subtype: error_during_execution` (spec 28) |
| `scenarios/done.jsonl` | Three JSON lines (`system`/`init`, `assistant` with a `REPORT` section, `result` with `cost_usd`, `duration_ms`, `num_turns`, `is_error: false`), LF endings, final LF |

Test-side helper: `tests/Zyggy.Integration/Infrastructure/FakeClaude.cs` (`ExecutablePath`, `ScenarioDirectory`, `ReadCapture`).

## PLACEHOLDER notice

Every line of `scenarios/done.jsonl` carries `"placeholder": true`. It is a documented stand-in for the real `stream-json` shape. **Deliverable 06** captures a real `claude -p` run once, outside the test suite, checks it in as `done.jsonl` and removes the markers.

## Running it by hand

```powershell
$env:ZYGGY_FAKE_CLAUDE_SCENARIO = 'done'
$env:ZYGGY_FAKE_CLAUDE_CAPTURE = "$env:TEMP\fake-claude-capture.bin"
dotnet run --project tools/fake-claude -- -p hello --output-format stream-json
$LASTEXITCODE
```

Or the built apphost directly: `tests/Zyggy.Integration/bin/Debug/net10.0/fake-claude.exe -p hello`.

## Troubleshooting

### Exit codes

- `3` with `fake-claude: unknown scenario '<name>'`: no `scenarios/<name>.jsonl` next to the executable. Check `ZYGGY_FAKE_CLAUDE_SCENARIO` and that the scenario file has `CopyToOutputDirectory` (the `Content` item in `FakeClaude.csproj`). The capture file is still written.
- `4` with `fake-claude: cannot write capture '<path>': …`: the capture path is a directory, unwritable, or its parent does not exist. Nothing is written to stdout.

### `ZYGGY_FAKE_CLAUDE_CAPTURE` unset

No capture is written and the run is otherwise normal. A test that then reads the capture fails on the missing file: set the variable per invocation to a unique path under the test's own temp directory.

### Executable not found

`FakeClaude.ExecutablePath` throws `FileNotFoundException` naming `<test output>/fake-claude[.exe]`. The apphost, `fake-claude.dll`, `fake-claude.runtimeconfig.json` and `scenarios/` reach the test output through the plain `ProjectReference` to `tools/fake-claude/FakeClaude.csproj` in `tests/Zyggy.Integration/Zyggy.Integration.csproj`; restore that reference and rebuild.
