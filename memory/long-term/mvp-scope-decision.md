---
name: mvp-scope-decision
description: Zyggy's MVP scope decision (2026-09-29) — what to keep vs. cut in every future spec/plan.
since: 2026-10-10
recalls: 0
last_recalled: never
strength: 1
idle_cycles: 0
---
The owner decided (2026-09-29) that Zyggy is a personal assistant for one user, not a multi-tenant product: founding-spec §14 (commercialisation) became "kept open, not built". Rule for every future spec/plan: keep the multi-tenant shape wherever it is already built or free (tenant field, path prefixes, `key_id`, seam interfaces), but cut anything that would need new code/tests/infra only for a second tenant or a paying customer (tenant-gate enforcement, an HTTP Hub, signed `policy.yaml`, budget caps, an SDK-based runner, init/join flows, Docker/Bicep packaging, a diagnose command, OpenTelemetry). The tenant label stays `geoffrey`; the product name is `Zyggy`.

**Why:** an early pass over-cut free rules (cross-tenant transitions, registry scoping) before this line was stated clearly; restating the keep/cut rule up front fixed it.
**How to apply:** when a spec or plan touches tenancy, policy, budgets, or packaging, check which side of this line the feature falls on before designing it.
