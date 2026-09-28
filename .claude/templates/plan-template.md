# Plan: <NN> — <Deliverable Name> — <User Story>

## Overview
<1–3 sentences: what this deliverable makes the system do, which spec (`_specs/<NN>-<Deliverable>.md`) and founding-spec sections it implements, and which existing feature is the pattern reference — or "none yet: §9 design rules + build-feature catalog".>

<!--
Decomposition strategy: vertical slices, not horizontal layers.
Each slice follows: Fake (behavior proven through the seam interfaces with substitutes, unit tests)
→ Wire (real edge: git via Process, HttpClient with mocked handler, fake-claude script, local bare repo;
integration tests). Name steps by what the system can do — never by which type is built.
Good: "Reject an envelope with a bad signature", "Claim a job from a local bare bus repo"
Bad: "Create Envelope record", "Add GitClient", "Build BusRepository"

Gate placement: steps run back-to-back WITHOUT user intervention — the executor stops ONLY
at a 🛑 HUMAN GATE block. Place one gate at the end of each vertical slice. Add an extra
gate only where earlier user judgment is essential (contract sign-off after a fake step,
or after a ⚠️ Risk Area step: signing, secrets, work boundary, shared contract, new package).
Never attach a gate to every step.

Gate slice (MANDATORY when the deliverable closes a §12 phase — planner rule 15): the LAST
slice scripts the phase gate scenario under tests/Zyggy.Integration/Gates/P<n>_<Name>.cs
(a working round trip through the real components against the bare repo + fake claude),
updates PROTOCOL.md / node.json schema / the spec where behaviour changed, and adds a
runbook entry for any new failure mode. Its RED failing-run command:
  dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~Gates.P<n>_<Name>"
The slice's 🛑 HUMAN GATE covers it.
-->

---

## Step N — <Behavior slice: what the system can do after this step>

- [ ] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `src/Zyggy.Core/<Folder>/<File>.cs` *(create | modify)*
- `tests/Zyggy.Core.Tests/<Folder>/<File>Tests.cs` *(create | modify)*

**Seams**: <which of `IProcessRunner`, `IBusProvider`, `IModelRunner`, `ISecretStore`, `INotifier`, `IPolicySource`, `TimeProvider` this step fakes or wires>

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/<Folder>/<Class>Tests.cs`
- Unit test methods: `<Method>_<Scenario>_<Expected>`
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~<Class>Tests"`
- *(When this step touches canonical serialisation or signing)*: golden file `tests/golden/<case>.md` + expected signature
- *(When this step adds or changes a real edge — process, HTTP, bus layout, host, CLI verb, MCP tool)*:
  - Integration test file: `tests/Zyggy.Integration/<Area>/<TestClassName>.cs` *(create | modify)*
  - Integration test methods: happy path + one failure path ending in a `reason`-coded report or a rejection move
  - Harness: local bare repo + clone, `tools/fake-claude` — see `.claude/skills/integration-testing/SKILL.md`
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~<TestClassName>"`

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- <What to implement — be specific about namespaces, type names, interfaces, records, enum members, method signatures, envelope fields, config keys>

**Contract impact**: *none — or which §4/§5/§6/§7 contract, `PROTOCOL.md` line, or `node.json` key changes, and the ⚠️ flag if so.*

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`)

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

<!-- Repeat the Step block above for every step in the slice, then close the slice with a gate.
     A gate is its own top-level block between steps — NOT a section inside a step. -->

## 🛑 HUMAN GATE — <end of Slice X> *(covers Steps M–N)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [ ] Behavioral verification: <how to confirm the new behavior of the covered steps — test names, CLI invocation + expected output, file expected in the bare repo>
- [ ] Contract review: <which envelope fields / reason codes / paths / signatures the reviewer should check against the spec and founding spec §4–§7>
- [ ] ⚠️ Risk review *(if any covered step is a Risk Area)*: <what to verify — canonical form unchanged, no secret in files, work-boundary rule intact, dependency license>
- [ ] User approved — implementation may continue past this gate
