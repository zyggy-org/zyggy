# Short-term memory

<!-- Working memory of the agent, injected at every session start. Write here during a session what may be worth
     remembering: a project fact, a convention, a gotcha, a correction of a long-term memory (name its file), an
     open question. One bullet each, dated, short. At the next /evolve:propose, consolidation keeps what matters in
     long-term memory and clears this file. Limit: 200 lines / 25 KB; the Stop hook asks you to compress it when over. -->

- 2026-10-10: `DreamRunner` records the commit sha read *before* the push, so after a clean rebase the dream's run record names a sha that is not on the remote; `MemoryPublisher.CommitAndPushAsync` (archive) now re-reads HEAD after a rebase, the dream does not yet (plan 37 forbids dream changes) — candidate bugfix.
