# Zyggy.Hub

The MCP server that exposes Central's memory and the bus to Claude Code sessions. Published as the single-file binary `zyggy-hub`. On Central it runs over stdio, referenced from `.mcp.json`; an HTTP transport exists for remote callers.

## Tools

| Tool | Input | Output | Notes |
|------|-------|--------|-------|
| `get_context` | `topic`, `max_tokens` | matching memory file bodies and daily-note lines | ranked by alias/description match, then recency (`ContextRanker` in `Zyggy.Core`) |
| `remember` | `fact`, `scope`, `tag` | ok | appends to `memory/inbox/`; refuses lines matching secret patterns (keys, tokens, IBANs, card numbers) |
| `list_nodes` | – | registry summary | from `registry/*.yaml` on the bus |
| `submit_job` | envelope fields | job id | signs and pushes, same path as `zyggy submit` |
| `job_status` | `id` | state and report if any | reads the bus |

## Boundaries

- Reads memory through the read-only helpers in `Zyggy.Core/Memory`; the only write it performs is to `memory/inbox/`. Durable memory files are rewritten solely by the nightly dream pass, never by the Hub.
- Takes tenant and user from the caller's token, never from the request body (§14).
- On laptops the Hub does not run; project `.mcp.json` files point to `zyggy hub --proxy` instead, which answers from a cached copy of the bus `context/` folder plus the project's own `CLAUDE.md`.

Built on the official ModelContextProtocol C# SDK. Founding spec: §7 (Hub MCP surface), §14 (tenancy).
