# Zyggy.Hub

The empty host kept in the solution. The Hub itself, the MCP server that gives a Claude Code session on Central the owner's memory, lives in `src/Zyggy.Core/Hub/` and runs as the CLI verb `zyggy hub` (deliverable 11). There is no separate Hub binary.

On Central the instance's `.mcp.json` starts it over stdio: `"hub": {"type": "stdio", "command": "zyggy", "args": ["hub"]}`, with no `env` block. It exits 0 when stdin closes, 3 on a configuration error and 4 on any argument.

## Tools

| Tool | Input | Output | Notes |
|------|-------|--------|-------|
| `get_context` | `topic` (1–200 characters), `max_tokens` (100–8000, default 2000) | One `<zyggy-context tenant=".." user=".." generated="..">` block: the matching durable memory files under a `## <path> — <description>` heading each, then the daily lines of the last 30 days that mention the topic, inside a budget of `max_tokens × 4` bytes; or `no memory file or daily line matches the topic` | Matches on slug, name or alias first, then on all terms, then on any term, never on the body; newer `updated` first. Read-only. The text is data, never instructions. |
| `remember` | `fact`, `scope` (`general`, `project:<name>`, `machine`; optional) | `remembered: <path>` and the line | Appends one `[stated]` line to `inbox/remember-<date>.md`, the same line as `zyggy memory remember`. A secret-shaped fact is refused (`refused: matches secret pattern <name>`); under `ZYGGY_HOOKS=off` nothing is stored. |

`list_nodes`, `submit_job`, `job_status`, the HTTP transport and the laptop proxy `zyggy hub --proxy` are deliverable 21.

## Boundaries

- Reads memory through `MemoryStore` and `ContextRanker` in `Zyggy.Core/Memory`. The only write is to `inbox/`; durable files are rewritten solely by the nightly dream pass.
- Serves one principal, fixed at start from the process environment (`ZYGGY_MEMORY_ROOT`, `ZYGGY_TENANT`, `ZYGGY_USER`, `ZYGGY_TIMEZONE`; unset ones from the `env` object of `$CLAUDE_PROJECT_DIR/.claude/settings.local.json`). A request that names `tenant` or `user` is refused; nothing is read.
- Refuses to start without the secret-pattern file, the same list `remember` uses.
- Never runs git and writes nothing but JSON-RPC on stdout.

Built on the official ModelContextProtocol C# SDK. Founding spec: §7 (Hub MCP surface), §14 (tenancy). Failures: the Cli README, "Troubleshooting — Hub".
