---
name: bash-heredoc-limitations
description: Multi-line shell heredocs in this harness mangle escapes/quotes; use the Write tool instead.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
In this Claude Code harness, Bash heredocs that embed literal backslash-escapes, quotes, or multiple files reliably corrupt (escape collapsing, stray-quote parse failures, or hangs when a heredoc only feeds one half of a piped `cat > a || cat > b` command) — seen across sessions on 2026-09-28, 2026-10-07 and 2026-10-09 (patch scripts, multi-file C# writes, Python edit scripts on CRLF files). **Why:** the harness's heredoc handling is not a plain POSIX shell pass-through, so anything beyond a short single-quoted line is unreliable. **How to apply:** for any multi-line file content (scripts, source edits, patches), write it with the Write tool or edit it with the Edit tool instead of a Bash/PowerShell heredoc; reserve heredocs for short, single-purpose literal text.
