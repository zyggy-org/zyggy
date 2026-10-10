# Short-term memory

<!-- Working memory of the agent, injected at every session start. Write here during a session what may be worth
     remembering: a project fact, a convention, a gotcha, a correction of a long-term memory (name its file), an
     open question. One bullet each, dated, short. At the next /evolve:propose, consolidation keeps what matters in
     long-term memory and clears this file. Limit: 200 lines / 25 KB; the Stop hook asks you to compress it when over. -->

- 2026-10-10: `DreamRunner` records the commit sha read *before* the push, so after a clean rebase the dream's run record names a sha that is not on the remote; `MemoryPublisher.CommitAndPushAsync` (archive) now re-reads HEAD after a rebase, the dream does not yet (plan 37 forbids dream changes) — candidate bugfix.
- 2026-10-10: zyggy-core bats run locally with `MSYS_NO_PATHCONV=1 podman run --rm -v 'D:\source\zyggy-core:/w' -w /w zyggy-core-test bash -c "trap '' PIPE; bats tests/"` (image `zyggy-core-test` exists on the laptop); `ZYGGY_HYGIENE_FORBIDDEN` must be passed with `-e` for the hygiene rows.
- 2026-10-10: zyggy-geoffrey (private) GitHub Actions jobs do not start since 2026-10-08: "recent account payments have failed or your spending limit needs to be increased" — run its bats in podman (`zyggy-core-test` image, `git config --global --add safe.directory /w`) until the owner fixes billing.
- 2026-10-10: Central can fetch a release binary itself (`curl -fsSL https://github.com/zyggy-org/zyggy/releases/download/v<v>/zyggy` + `SHA256SUMS`, public repo) inside `az vm run-command`; no scp/Tailscale needed for 14a step 4.
