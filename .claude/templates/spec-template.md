# Spec: <NN> — <Deliverable Name>

> Founding-spec sections: §<n> <title>, §<n> <title>. Roadmap entry: `_plans/ROADMAP.md` #<NN>.

## User Story

**As** <the user | Central | a Node | the work node>,
**I want** <capability>,
**So that** <benefit / which §12 gate this enables>.

---

## Acceptance Criteria

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | ... | ... | ... |
| AC-2 | ... | ... | ... |

<!-- Every §12 definition-of-done item and every gate condition for this deliverable is an AC.
     Every §8 security rule and §14 constraint that applies is an AC, not a remark. -->

---

## Decision Table

| Founding-spec item (section) | Verdict | Target type / library | Justification |
|------------------------------|---------|-----------------------|---------------|
| ... | Keep / Reshape / Library / Defer | ... | ... |

---

## Contracts

### Types and interfaces
<Namespaces, records, interfaces (name which of the five seams), enums with their closed member lists. Shapes and names, not implementations.>

### Envelope / file formats _(if the deliverable touches the bus)_
<Fields added or interpreted, canonical-form rules, path layout via `BusPaths`, state moves. Any change here is an Open Question until the user confirms, because `PROTOCOL.md` must match §4 verbatim.>

### CLI surface _(if the deliverable touches `zyggy`)_
`zyggy <verb> [options]` — arguments, options, exit codes, output format.

### MCP tools _(if the deliverable touches the Hub)_
| Tool | Input | Output | Notes |
|------|-------|--------|-------|

### Configuration
| Key | Where (`node.json` / `policy.yaml` / options) | Default | Override rule |
|-----|-----------------------------------------------|---------|---------------|

---

## Behaviors & Conventions

- <Default behavior> — override: <where / how>.
- ...

---

## Failure modes

| Situation | Observable outcome (`reason` code, log line, rejection move, back-off) | Runbook entry |
|-----------|------------------------------------------------------------------------|---------------|
| ... | ... | ... |

---

## Dependencies

| Package | License | Why (what bespoke code it removes) |
|---------|---------|------------------------------------|

---

## Deliberate deviations from the founding spec

- <what differs, why, which resolved Open Question decided it> — or "none".

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| ... | ... |

---

## Out of Scope

- <everything marked Defer above, plus explicit exclusions>

---

## Open Questions _(remove when resolved)_

- [ ] **Q1 — <title>.** Found: <what, with section/file refs>. Why it matters: <...>. Options: (a) ... (b) ... Recommendation: <...>.
