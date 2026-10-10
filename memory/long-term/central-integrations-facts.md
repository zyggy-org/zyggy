---
name: central-integrations-facts
description: Platform facts for Central's GitHub, Microsoft Graph/M365 and LinkedIn integrations.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
GitHub: the official `github@claude-plugins-official` plugin is a remote HTTP MCP server with a session-wide bearer token and roughly 100 tools (no read-only mode); a fine-grained PAT on a personal account cannot reach organisation repos at all (resource-owner rule) — spec 31 instead uses `gh` with a 0600 token file exported as `GH_TOKEN` to the script's `gh` child only. Microsoft Graph/M365: a mailbox's own user object can be permission-denied (403) even when its OneDrive is readable — get the owner's name via `GET /users/<mailbox>/drive?$select=owner` instead of `/users/<mailbox>`; SharePoint/OneDrive hosts derive from the tenant's onmicrosoft name plus `-my` (e.g. `<tenant>-my.sharepoint.com`), not the mail domain; `@softeria/ms-365-mcp-server` URL-encodes every path parameter except where `endpoints.json` lists `skipEncoding`, which breaks `upload-file-content` for new OneDrive files; delta-listing backfills can stall forever on same-second-timestamp ties when the watermark is second-precision and the batch size is smaller than the tie group. LinkedIn: an individual self-serve app only gets "Sign In with OpenID Connect" + `w_member_social` (create posts) — no comments, no reading own posts, no refresh tokens (60-day re-auth), and the app must be linked to a LinkedIn Page.

**Why:** each fact changed a spec decision or cost a probing session (deliverables 31, 23, 28's OneDrive backfill, 36).
**How to apply:** check this before specifying or debugging a GitHub/Graph/LinkedIn-touching feature on Central.
