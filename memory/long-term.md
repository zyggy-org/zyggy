# Long-term memory

<!-- Index of the agent's long-term memory, injected at every session start. One line per memory:
     - [Title](long-term/<slug>.md) — one-line hook: when this memory matters
     The memory itself lives in the linked file; read it before relying on it. Reads are counted: a memory that is
     never read decays and is forgotten. Written only by consolidation (/evolve:propose) and by the owner.
     Limit: 200 lines / 25 KB (evolution/evolve.json → memory). -->

- [Bash heredoc limitations](long-term/bash-heredoc-limitations.md) — before writing any multi-line script/patch via a shell heredoc
- [Central VM operations](long-term/central-vm-operations.md) — before giving Central/VM commands or debugging a stuck session or service
- [CI/test tooling gotchas](long-term/ci-test-tooling-gotchas.md) — before debugging a red CI run or writing a binary/integration test
- [Repo public hygiene](long-term/repo-public-hygiene.md) — before writing or editing any file in zyggy or zyggy-core
- [Central integrations facts](long-term/central-integrations-facts.md) — before specifying or debugging a GitHub/Graph/LinkedIn feature on Central
- [Evolve-propose plugin pin](long-term/evolve-propose-plugin-pin.md) — if /evolve:propose errors on MEMORY.md or a false path violation
- [MVP scope decision](long-term/mvp-scope-decision.md) — when a spec/plan touches tenancy, policy, budgets or packaging
- [Spec rework concurrency check](long-term/spec-rework-concurrency-check.md) — before reworking or reopening an existing spec file
