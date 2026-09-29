# Plan: 03 — Envelope parse, canonicalise, sign and verify — Any §4 envelope file is parsed, canonicalised, signed and verified with a per-tenant HMAC key, and any tampered byte, foreign tenant, unknown key or future schema is refused before any model call (the probe payload)

## Overview

After this deliverable `Zyggy.Core` turns any envelope file (YAML front matter + Markdown body, read as bytes) into a typed, tenant-checked `Envelope` or an `EnvelopeRejection` from the closed `EnvelopeRejectionReason` enum; produces the canonical signing bytes that five golden cases freeze byte-for-byte; signs with the tenant's key obtained through the `ISecretStore` seam and writes the file in canonical style; verifies — tenant first, then schema, fields, key, digest — so that a foreign tenant's key is never looked up and a single changed byte anywhere is `InvalidSignature`; and stores keys on disk with owner-only permissions on Windows and Linux through `FileSecretStore`, composed by `AddFileSecretStore().AddEnvelopeSigning()`. It implements `_specs/03-envelope-signing.md` (approved 2026-09-28, zero Open Questions; its Decision Table, Contracts and Acceptance Criteria AC-1..AC-34 are binding) against founding-spec §4 (Envelope, the "Mandatory fields per type" table, Signature, "Canonical form, normative details" — all amended today and treated as current), §8 (secrets table, P0 file store), §9 (`Tenancy/`, `Envelope/`, `Secrets/`, packages, design rules, versioning), §13 (HMAC-signed envelopes, tenancy shape from P0, one repository per tenant) and §14 (tenancy, `ISecretStore` as a pluggable edge).

**Reference pattern**: none yet — `src/Zyggy.Core` holds no source file; this is the first production code. The pattern is the §9 design rules plus the `build-feature` reference catalog (seam interface + fake + real implementation, closed enum + wire strings, golden-file `[Theory]` + `[MemberData]`, `Add<Feature>` extension methods) and `.claude/instructions/public-api.md` (internal-and-sealed by default, XML docs on every public member, `TryParse` with `[NotNullWhen(true)]`, result records instead of exceptions for protocol outcomes). Test-side patterns that already exist and are mirrored: `tests/Zyggy.Core.Tests/Infrastructure/TestStackSmokeTests.cs` (namespace `Zyggy.Core.Tests.<Folder>`, sealed classes, AAA, one Act), `tests/Zyggy.Integration/Jobs/FakeClaudeTests.cs` (temp directories under `<temp>/zyggy-it/`, `TestContext.Current.CancellationToken` on every async call, xunit.v3 analyzers).

**What this plan deliberately is not** (spec Out of Scope): no `BusPaths`, `BusRepository`, `GitClient`, poller, `IBusProvider`, `jobs/rejected/` move or `BusRepoFixture` seeding (04); no `JobRunner`, `IModelRunner`, `JobFailureReason` enum (06); no `Envelope.With(...)`; no CLI verb, Hub tool or `PROTOCOL.md`; no OS secret store, `zyggy secret`, `signWith` binding or rotation execution (07/09); no `IEnvelopeSigner` interface (public-api.md forbids a sixth single-implementation abstraction); no logging, no `TimeProvider`, no `IProcessRunner`, no code in `Zyggy.Node`/`Cli`/`Hub`. 03 does not close a §12 phase (05 closes P0), so there is no gate slice; the final 🛑 HUMAN GATE is the definition-of-done check against `ROADMAP.md` #03 and AC-1..AC-34.

**Slices and gates**:

| Slice | Steps | What the system can do afterwards | Gate |
|-------|-------|-----------------------------------|------|
| A — A file becomes a typed, tenant-checked envelope or a reason | 1, 2 | Refuse malformed labels, key ids, envelope ids and secret names; map every closed enum to its §4 wire string; parse the five golden `.md` cases into typed models; reject every non-envelope, missing or ill-shaped field, foreign tenant, future schema and malformed `sig` with the reason and field at fault, in the §4 order | 🛑 after Step 2 (⚠️ new packages `Ulid`, `YamlDotNet`; parse contract) |
| B — The canonical bytes and byte-identical writing | 3, 4 | `Canonicalize` reproduces every golden `.canonical`; a Zyggy-built envelope serialises in canonical style and a parsed file round-trips byte-identically with its unknown fields | 🛑 after Step 4 (⚠️ canonical form = the protocol) |
| C — Sign and verify through the `ISecretStore` seam (in-memory fake) | 5, 6 | Sign with the tenant's key so the digest equals the `openssl`-produced `.sig`; verify accepts the goldens and rejects every tampered key or body byte, tenant before key lookup, rotation = whatever the store holds | 🛑 after Step 6 (⚠️ signing, secrets, public seam) |
| D — Real key storage on disk and host composition | 7, 8 | Keys persist under `<root>/<tenant>/hmac/<n>` with 0600 / owner-only ACL, read back, are removed; a host composes signing over the file store with two calls and signs the golden job with a key seeded the way an operator would | 🛑 after Step 8 (⚠️ secrets on disk, `Microsoft.Extensions.*` packages; definition of done) |

Every step ends with PROVE = `dotnet build Zyggy.slnx` · `dotnet test Zyggy.slnx` · `dotnet format Zyggy.slnx --verify-no-changes`, all green on the developer's Windows machine; Linux is proven by CI (both runners) at the Slice D gate. Step 2 is the largest cycle by necessity: the spec's check order (Malformed → tenant → schema → type → key_id → fields → sig) is one ladder whose halves have no observable meaning alone, and the typed views cannot exist without it.

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

Deliverable 03 does not close a phase (05 closes P0 with Gates/P0_ProbeRoundTrip.cs), so there
is no gate slice; the final 🛑 HUMAN GATE is the definition-of-done check against ROADMAP.md #03
and AC-1..AC-34.
-->

---

## Test key material and golden fixtures (shared by every step)

Fixed by the spec (Acceptance Criteria header); obviously synthetic, never a real credential:

| Key id | Secret (32 bytes = UTF-8 of the string) | `FileSecretStore` file content (Step 7/8) |
|--------|------------------------------------------|-------------------------------------------|
| `acme/1` | `zyggy-golden-test-key-acme-00001` | `Convert.ToHexStringLower` of those 32 bytes + `\n` |
| `acme/2` | `zyggy-golden-test-key-acme-00002` | idem |

Tenant `acme` everywhere under `src/` and `tests/`; `geoffrey` nowhere. The two secrets live in one test helper (`tests/Zyggy.Core.Tests/Infrastructure/TestKeys.cs`, Step 5) and in `tests/golden/README.md` (Step 2) — nowhere else.

**How the golden oracle files are produced (independently of the code under test — never by it):**

1. `<case>.md` is written by hand (canonical style for `job`, `report`, `context`, `context-crlf-body`; deliberately non-canonical for `job-noncanonical`), with the `sig:` line left for step 4 below.
2. `<case>.canonical` is derived **by hand** from founding-spec §4 "Canonical form, normative details": every top-level and nested key in ordinal order, `sig` removed, block mappings with 2-space indent, flow sequences `[a, b]` for scalar items, block `- item` (indented 2) otherwise, `[]` for empty, scalars as their parsed text in plain → single-quoted → double-quoted order, every line `\n`-terminated, then `---\n`, then the body bytes unchanged (CRLF stays, missing final newline stays). No opening `---`, no BOM. For a canonical-style `.md` this is exactly the file minus its first line and minus its `sig:` line — produce it that way with a byte-preserving tool (`[System.IO.File]::ReadAllBytes` / `WriteAllBytes` in PowerShell, or `sed '1d;/^sig: /d'` on Linux); never through an editor that may add a BOM, a final newline or CRLF.
3. `<case>.sig` = the 64 lowercase hex characters printed by `openssl dgst -sha256 -hmac 'zyggy-golden-test-key-acme-00001' <case>.canonical` (`acme/2` secret for `context`), stored **without** a trailing newline: on Linux `openssl dgst -sha256 -hmac '<secret>' <case>.canonical | awk '{printf "%s", $2}' > <case>.sig`; on Windows use `openssl.exe` from Git for Windows (`C:\Program Files\Git\usr\bin\openssl.exe`) or WSL, then `[System.IO.File]::WriteAllText('<case>.sig', '<hex>', [System.Text.UTF8Encoding]::new($false))`. Check: `(Get-Item tests/golden/<case>.sig).Length` → `64`.
4. Write `sig: hmac-sha256:<those 64 hex chars>` into `<case>.md` — in its sorted position for the canonical-style cases, anywhere for `job-noncanonical`.
5. `git ls-files --eol tests/golden` must show `i/-text` for every file (the `.gitattributes` from 01 applies); `.editorconfig` leaves `tests/golden/**` alone.

If a later step's golden `[Theory]` is red, the **only** admissible fixture fix is a hand re-derivation against §4 (step 2 above), recorded in the gate summary with the rule that was misapplied. Pasting the code's output into a `.canonical` or `.sig` file is forbidden — that would make the code its own oracle. If the emitter provably cannot produce the §4 form for a construct, that is a protocol question for the user at the gate, not a fixture edit.

The five cases (spec "Golden cases"), all `schema: 1`, `tenant: acme`, LF line endings unless stated:

| Case | Style / key | Front matter (canonical order shown; `sig` inserted at step 4) | Body |
|------|-------------|-----------------------------------------------------------------|------|
| `job` | canonical, `acme/1` | exactly the spec's example: `agent: env-debugger`, `allowed_tools: [Read, Grep, Glob, Bash(dotnet *), Bash(kubectl get *)]`, `attempt: 1`, `created: 2026-09-27T14:05:00Z`, `deadline: 2026-09-29T18:00:00Z`, `from: central`, `id: 01J8Y3N7Q2X9Z4A5B6C7D8E9F0`, `key_id: acme/1`, `priority: normal`, `project: calizr`, `report_back: [summary, diff, files_changed]`, `schema: 1`, `sig: …`, `tenant: acme`, `timeout_minutes: 30`, `to: home-laptop`, `type: job`, `worktree: true` | `Investigate why staging returns HTTP 502 on /api/bookings since Friday.\nDo not change code; report root cause and a proposed fix.\n` |
| `report` | canonical, `acme/1` | `cost_usd: 0.42`, `created: 2026-09-27T14:40:00Z`, `diff_ref: 3f2a9c1`, `duration_seconds: 1830`, `files_changed: [src/Api/Bookings.cs, README.md]`, `finished: 2026-09-27T14:39:30Z`, `from: home-laptop`, `id: 01J8Y3Q0AAAAAAAAAAAAAAAAAA` (any valid ULID ≠ the job id), `in_reply_to: 01J8Y3N7Q2X9Z4A5B6C7D8E9F0`, `input_tokens: 12345`, `key_id: acme/1`, `model: test-model-1`, `num_turns: 7`, `output_tokens: 2345`, `priority: normal`, `reason: timeout`, `schema: 1`, `sig: …`, `started: 2026-09-27T14:09:00Z`, `status: failed`, `tenant: acme`, `to: central`, `type: report` | `## REPORT\nsummary: The run exceeded timeout_minutes while reproducing the 502.\n` |
| `context` | canonical, `acme/2` | `created: 2026-09-27T15:00:00Z`, `from: home-laptop`, `id: <valid ULID>`, `key_id: acme/2`, `priority: normal`, `schema: 1`, `scope: project:calizr`, `sig: …`, `tenant: acme`, `to: central`, `type: context` | `[observed] staging uses the shared Postgres pool since 2026-09-20.\n[observed] /api/bookings times out when the pool is exhausted.\n` |
| `job-noncanonical` | by hand, **CRLF** front matter, `acme/1`, unsorted | in this order: `type: job`, `tenant: acme`, `schema: 1`, `id: <valid ULID>`, `to: home-laptop`, `from: central`, `created: 2026-09-27T14:05:00Z`, `key_id: acme/1`, `sig: …`, `project: calizr`, `worktree: "true"`, `deadline: 2026-09-29T18:00:00.500+02:00`, `allowed_tools:` as a block sequence (`  - Read`, `  - Bash(dotnet *)`), `x_experimental: 'a: b'`, `x_empty: ''`, `x_quote: "it's: here"`, `x_null: null`, `x_meta:` nested mapping with `owner: qa`, `tags: [b, a]` (sequence order is preserved, only keys sort) and `steps:` a sequence of two mappings (`- name: one`, `- name: two`) | `Body with LF.\n` |
| `context-crlf-body` | canonical, `acme/1` | `created: 2026-09-27T15:10:00Z`, `from: home-laptop`, `id: <valid ULID>`, `key_id: acme/1`, `priority: normal`, `schema: 1`, `scope: machine`, `sig: …`, `tenant: acme`, `to: central`, `type: context` | `[observed] line one\r\n---\r\n[observed] line three` (**no** final newline) |

Expected `job-noncanonical.canonical` front matter (hand-derived; this is the drift proof), one key per line in this order: `allowed_tools: [Read, Bash(dotnet *)]`, `created: 2026-09-27T14:05:00Z`, `deadline: 2026-09-29T18:00:00.500+02:00` (parsed text, not normalised), `from: central`, `id: …`, `key_id: acme/1`, `project: calizr`, `schema: 1`, `tenant: acme`, `to: home-laptop`, `type: job`, `worktree: true` (the file's `"true"` is a quoted *style* of the text `true`; canonical emits the parsed text plain), `x_empty: ''`, `x_experimental: 'a: b'`, `x_meta:` / `  owner: qa` / `  steps:` / `    - name: one` / `    - name: two` / `  tags: [b, a]`, `x_null: null`, `x_quote: "it's: here"`. The executor re-derives this by hand from §4 (c) before trusting it.

---

## Step 1 — Tenant, user and machine labels, key ids, envelope ids and secret names are accepted only when well-formed; every closed enum maps to exactly its §4 wire string and back

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `Directory.Packages.props` *(modify)* — add `<PackageVersion Include="Ulid" Version="1.4.1" />` (MIT; spec Dependencies table).
- `src/Zyggy.Core/Zyggy.Core.csproj` *(modify)* — `<PackageReference Include="Ulid" />` (version-less, central management).
- `src/Zyggy.Core/Tenancy/Label.cs` *(create, internal)* — the one validator for the shared label syntax `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$` (a `[GeneratedRegex]` or a hand loop; no allocation on the hot path).
- `src/Zyggy.Core/Tenancy/TenantId.cs`, `UserId.cs`, `MachineName.cs`, `Principal.cs` *(create)*.
- `src/Zyggy.Core/Envelope/EnvelopeId.cs`, `KeyId.cs`, `EnvelopeType.cs`, `EnvelopePriority.cs`, `ReportStatus.cs`, `ContextScope.cs` (holds `ContextScopeKind` and `ContextScope`), `EnvelopeRejectionReason.cs`, `EnvelopeWire.cs` *(create)*.
- `src/Zyggy.Core/Secrets/SecretName.cs` *(create)* — needed now because `KeyId.SecretName` returns it.
- `tests/Zyggy.Core.Tests/Tenancy/LabelTypesTests.cs`, `tests/Zyggy.Core.Tests/Envelope/KeyIdTests.cs`, `EnvelopeIdTests.cs`, `ContextScopeTests.cs`, `EnvelopeWireTests.cs`, `tests/Zyggy.Core.Tests/Secrets/SecretNameTests.cs` *(create)*.

**Seams**: none. Pure value types; no clock (`EnvelopeId.New` takes the instant), no I/O.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- `tests/Zyggy.Core.Tests/Tenancy/LabelTypesTests.cs` (namespace `Zyggy.Core.Tests.Tenancy`) — one `[Theory]` per type over the same data, AC-23:
  - `TryParse_ValidLabel_ReturnsTrueWithValue` — `[InlineData]` `acme`, `a`, `home-laptop`, `x1-2y`, a 63-char label → `true`, `Value` equals input, `ToString()` equals input; for `TenantId`, `UserId`, `MachineName`.
  - `TryParse_InvalidLabel_ReturnsFalse` — `""`, `Acme`, `-acme`, `acme-`, `ac me`, `acme/1`, `a..b`, a 64-char label, `..`, `geoffrey/`, `null` → `false` and `out` null.
  - `Parse_InvalidLabel_ThrowsFormatException` — for each type.
  - `Equals_SameValue_IsEqual` — two `Parse("acme")` results are equal and have equal hash codes (record semantics).
  - `PublicSurface_HasNoDefaultNoParameterlessConstructorNoImplicitConversion` — reflection over the three types: `GetConstructor(Type.EmptyTypes)` is null, `GetConstructors()` contains no public constructor, `GetMember("Default")` is empty, no `op_Implicit` method (AC-23 compile-time half).
  - `Principal_TwoIds_ExposesBoth` — `new Principal(TenantId.Parse("acme"), UserId.Parse("alice"))` → `Tenant.Value == "acme"`, `User.Value == "alice"`.
- `tests/Zyggy.Core.Tests/Envelope/KeyIdTests.cs` — AC-24: `TryParse_TenantSlashNumber_ReturnsTenantAndNumber` (`acme/1` → `(acme, 1)`, `ToString() == "acme/1"`, `SecretName.Value == "hmac/1"`); `TryParse_Invalid_ReturnsFalse` over `acme/0`, `acme/01`, `acme`, `acme/1/2`, `Acme/1`, `acme/-1`, `acme/99999999999` (does not fit `int`), `""`.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeIdTests.cs` — AC-25: `TryParse_TwentySixCrockfordChars_ReturnsTrue` (`01J8Y3N7Q2X9Z4A5B6C7D8E9F0`; `Value` equals input; `Timestamp` is a UTC instant); `TryParse_Invalid_ReturnsFalse` over a 25-char and a 27-char string, the lowercase form, strings containing `I`, `L`, `O`, `U`, and one whose first char is `8`; `New_GivenInstant_YieldsUppercaseCrockfordWithThatMillisecond` (`New(2026-09-27T14:05:00.123Z)` → 26 chars matching `^[0-7][0-9A-HJKMNP-TV-Z]{25}$`, `Timestamp == 2026-09-27T14:05:00.123Z`); `New_SameInstantTwice_Differs`.
- `tests/Zyggy.Core.Tests/Envelope/ContextScopeTests.cs` — `TryParse_ProjectScope_ReturnsKindAndName` (`project:calizr`), `TryParse_MachineAndGeneral_ReturnsKindWithoutName`, `TryParse_Invalid_ReturnsFalse` (`project:`, `project:a b`, `project:a:b`, `Project:calizr`, `other`), `ToString_RoundTrips`.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeWireTests.cs` — AC-29, one `[Theory]` row per member of every enum: `ToWire_Member_ReturnsSnakeCase` and `TryFromWire_SnakeCase_ReturnsMember` for `EnvelopeType` (`job`, `report`, `context`), `EnvelopePriority` (`low`, `normal`, `high`), `ReportStatus` (`done`, `failed`, `timeout`, `rejected`), `ContextScopeKind` (`project`, `machine`, `general`), `EnvelopeRejectionReason` (`malformed`, `tenant_mismatch`, `schema_unsupported`, `key_id_tenant_mismatch`, `missing_field`, `invalid_field`, `missing_signature`, `unknown_key`, `invalid_signature`); `TryFromWire_Unknown_ReturnsFalse` for each enum (`Job`, `DONE`, `""`); `EveryMember_HasATestRow` — `Enum.GetValues<T>()` count equals the number of rows (guards against a member added without a row).
- `tests/Zyggy.Core.Tests/Secrets/SecretNameTests.cs` — AC-28 second half: `TryParse_Valid_ReturnsTrue` (`hmac/1`, `github-pat`, `a/b/c`, 64 chars), `TryParse_Invalid_ReturnsFalse` (`../x`, `/x`, `x/`, `a//b`, `Hmac/1`, `a.b`, `""`, 65 chars, `a b`).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~LabelTypesTests|FullyQualifiedName~KeyIdTests|FullyQualifiedName~EnvelopeIdTests|FullyQualifiedName~ContextScopeTests|FullyQualifiedName~EnvelopeWireTests|FullyQualifiedName~SecretNameTests"` — fails at compile time (none of the types exist).
- No golden file and no integration test in this step (no real edge).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `Zyggy.Core.Tenancy.TenantId`, `UserId`, `MachineName`: `public sealed record` with a **private** constructor, `public string Value { get; }`, `public static T Parse(string value)` (throws `FormatException` naming the syntax, not echoing the value beyond its length), `public static bool TryParse(string? value, [NotNullWhen(true)] out T? result)`, `public override string ToString() => Value`. No positional parameter (a positional record would expose an unvalidated public constructor), no `Default`, no implicit conversion. Validation shared through `internal static class Label { static bool IsValid(ReadOnlySpan<char>) }` in `Tenancy/Label.cs`. XML docs cite §4 ("lowercase labels `[a-z0-9-]`, 1–63 characters") and §14.
- `Principal`: `public sealed record Principal(TenantId Tenant, UserId User)` with `ArgumentNullException.ThrowIfNull` in the constructor body (records allow a body).
- `EnvelopeId`: `public sealed record` over a private `Ulid` field (package type never in the public surface); `TryParse` **pre-validates** `^[0-7][0-9A-HJKMNP-TV-Z]{25}$` (uppercase only, first char `0`–`7`) before `Ulid.TryParse` — Cysharp's decoder is lenient and must not decide; `Value` = the 26-char string, `Timestamp` = `ulid.Time` (UTC, ms precision), `New(DateTimeOffset instant)` = `Ulid.NewUlid(instant)` (48-bit ms + 80-bit CSPRNG), `ToString() == Value`.
- `KeyId`: `public sealed record KeyId(TenantId Tenant, int Number)` with `Number ≥ 1` enforced in the constructor body (`ArgumentOutOfRangeException`), `Parse`/`TryParse` on `<tenant>/<n>` where `n` matches `^[1-9][0-9]*$` and `int.TryParse` (invariant) succeeds, `ToString() => $"{Tenant}/{Number}"`, `SecretName SecretName => SecretName.Parse($"hmac/{Number}")` (invariant formatting).
- `SecretName` (`Zyggy.Core.Secrets`): `public sealed record` with private constructor, `Parse`/`TryParse` on `^[a-z0-9-]+(/[a-z0-9-]+)*$`, ≤ 64 chars, `ToString() == Value`. XML doc: "relative name; the store composes `zyggy/<tenant>/<name>` (§8)".
- Enums exactly as the spec's Contracts tables; `ContextScope`: `public sealed record ContextScope(ContextScopeKind Kind, string? ProjectName)` with `Parse`/`TryParse`/`ToString()` (`project:<name>` requires a non-empty name with no whitespace and no `:`; `machine`/`general` require `ProjectName == null`).
- `EnvelopeWire`: `public static class` with `ToWire(EnvelopeType)`, `TryFromWire(string, out EnvelopeType)` and the same pair for `EnvelopePriority`, `ReportStatus`, `ContextScopeKind`, `EnvelopeRejectionReason`; `switch` expressions with no default fallthrough (an unmapped member throws `ArgumentOutOfRangeException`, so a member added without a wire string fails the very first test). Exact, case-sensitive matching.

**Contract impact**: ⚠️ first public API of `Zyggy.Core` (public-api.md: every public API change is a Risk Area) — the tenancy value types are what every later deliverable (`BusPaths`, `MemoryPaths`, Hub principal) takes as parameters; the enum wire strings are §4 protocol strings. ⚠️ new NuGet package `Ulid` 1.4.1 (MIT). Reviewed at the Slice A gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- The failing-run command above → all listed tests pass (roughly 60 rows across the six classes).
- `dotnet build src/Zyggy.Core` emits zero warnings with `GenerateDocumentationFile` on (every public type and member documented).
- `Select-String -Path (git ls-files 'src/**/*.cs') -Pattern 'Default|op_Implicit|static '` → read every hit: none may be a `Default` member, an implicit conversion or a static mutable field (`static readonly` of an immutable value and `static` methods/classes are fine) — AC-31 first check.
- `Select-String -Path (git ls-files 'src/**' 'tests/**') -Pattern 'geoffrey'` → no match.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 2 — Any §4 envelope file is parsed from bytes into a typed model or refused with the reason and the field at fault, in the §4 order (tenant first, never touching a secret store); the five golden `.md` cases parse and every unknown field survives in the tree

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `Directory.Packages.props` *(modify)* — add `<PackageVersion Include="YamlDotNet" Version="18.1.0" />` (MIT).
- `src/Zyggy.Core/Zyggy.Core.csproj` *(modify)* — `<PackageReference Include="YamlDotNet" />`.
- `src/Zyggy.Core/Envelope/FrontMatterNode.cs` *(create)* — `FrontMatterNode` (abstract record), `FrontMatterScalar`, `FrontMatterSequence`, `FrontMatterMapping`.
- `src/Zyggy.Core/Envelope/Envelope.cs` *(create)* — the immutable model with an `internal` constructor (factories arrive in Step 4).
- `src/Zyggy.Core/Envelope/EnvelopeHeader.cs`, `JobFields.cs`, `ReportFields.cs`, `ContextFields.cs`, `EnvelopeRejection.cs`, `EnvelopeResult.cs`, `EnvelopeParser.cs` *(create)*.
- `src/Zyggy.Core/Envelope/FrontMatterReader.cs` *(create, internal)* — byte-level split + YamlDotNet event stream → `FrontMatterMapping`.
- `src/Zyggy.Core/Envelope/FieldReader.cs` *(create, internal)* — typed interpretation of scalar text (int, decimal, bool, null, timestamp, string list) returning `EnvelopeRejection?`.
- `tests/golden/job.md`, `job.canonical`, `job.sig`, `report.md`, `report.canonical`, `report.sig`, `context.md`, `context.canonical`, `context.sig`, `job-noncanonical.md`, `job-noncanonical.canonical`, `job-noncanonical.sig`, `context-crlf-body.md`, `context-crlf-body.canonical`, `context-crlf-body.sig` *(create — fixtures per "Test key material and golden fixtures" above; only the `.md` files are consumed in this step, the sidecars are consumed by Steps 3 and 5)*.
- `tests/golden/README.md` *(modify)* — AC-34: the two key ids and secrets, `.sig` = 64 lowercase hex chars without newline produced by the recorded `openssl` command, the five cases and the ACs each serves, the rule that a new case is added in RED, the hand-derivation procedure above.
- `tests/Zyggy.Core.Tests/Infrastructure/Golden.cs` *(create)* — `public static class Golden`: `string Directory` (= `Path.Combine(AppContext.BaseDirectory, "golden")`), `TheoryData<string> Cases()` enumerating `*.md` case names, `byte[] Md(string case)`, `Canonical(string case)`, `Sig(string case)` (`File.ReadAllBytes`; `Sig` returns the ASCII string).
- `tests/Zyggy.Core.Tests/Infrastructure/EnvelopeText.cs` *(create)* — text-level mutation helper for canonical-style golden files: `EnvelopeText.From(byte[] md)` → `WithValue(key, value)`, `WithoutKey(key)`, `WithKey(key, value)` (appended before the closing `---`), `WithBody(byte[])`, `WithBomPrefix()`, `ToBytes()`. Operates on the LF-separated top-level lines of the front matter; never re-serialises through the code under test.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeParserTests.cs`, `EnvelopeParserGoldenTests.cs` *(create)*.

**Seams**: none — `Parse` is pure and synchronous and by contract never calls `ISecretStore` (which does not exist yet; Step 6 proves the "never called" rule with a substitute).

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Golden fixtures: author all fifteen files per the procedure above **before** any production code (the `.md` files are the RED input of this step; `.canonical`/`.sig` are oracle files for Steps 3 and 5 and are created now because the `sig:` line in each `.md` is derived from them).
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeParserGoldenTests.cs`:
  - `Parse_EveryGoldenCase_IsAccepted` — `[Theory] [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]`: `EnvelopeParser.Parse(Golden.Md(case), TenantId.Parse("acme")).IsAccepted` is `true`, `Rejection` null.
  - `Parse_GoldenJob_YieldsTypedJobFields` — AC-1, every assertion listed there (`Type == Job`, `Job.Project == "calizr"`, `Agent == "env-debugger"`, `Worktree == true`, `AllowedTools` = the five strings incl. `Bash(dotnet *)`, `ReportBack` = `[summary, diff, files_changed]`, `TimeoutMinutes == 30`, `Attempt == 1`, `Header.Priority == Normal`, `Header.Created == 2026-09-27T14:05:00Z`, `Job.Deadline == 2026-09-29T18:00:00Z`, `KeyId == acme/1`, `Signature` == the file's `sig` value, `Body` byte-equal to the two body lines, `Report`/`Context` null).
  - `Parse_GoldenReport_YieldsTypedReportFields` — `Status == Failed`, `Reason == "timeout"`, `NumTurns == 7`, `CostUsd == 0.42m`, `FilesChanged` two entries, `DiffRef == "3f2a9c1"`, `Header.InReplyTo == 01J8Y3N7Q2X9Z4A5B6C7D8E9F0`, `Started`/`Finished` parsed.
  - `Parse_GoldenContext_YieldsScope` — `Context.Scope == ContextScope(Project, "calizr")`, `KeyId == acme/2`.
  - `Parse_GoldenJobNoncanonical_KeepsUnknownFieldsAndParsedText` — AC-6 parse half: `FrontMatter.Entries` contains `x_experimental` (scalar text `a: b`), `x_empty` (`""`), `x_quote` (`it's: here`), `x_null` (`null`), `x_meta` (a `FrontMatterMapping` whose `steps` is a `FrontMatterSequence` of mappings); `Job.Worktree == true` (from the quoted `"true"`); `Job.Deadline == 2026-09-29T16:00:00.5Z` (offset normalised to UTC, fraction kept in the typed value); the scalar text of `deadline` in the tree is unchanged.
  - `Parse_GoldenContextCrlfBody_BodyIsByteExactAndFrontMatterEndedAtFirstDelimiter` — AC-19: `Body` equals the file bytes after the first `---\n` closing line, contains `\r\n---\r\n`, has no trailing newline, and `Context` is non-null.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeParserTests.cs` (inputs built inline with `"…"u8` literals or from `EnvelopeText.From(Golden.Md("job"))`):
  - Malformed (AC-18), `[Theory]` `Parse_NotAnEnvelope_ReturnsMalformedNamingTheRule` over: no opening `---` line; no closing `---`; unreadable YAML (`schema: [`); an anchor (`&a`); an alias (`*a`); an explicit tag (`!!str`); a merge key (`<<:`); a `%YAML 1.2` directive; a second document (`...` then more content, or a `--- ` line with trailing content); root is a sequence; root is a scalar; a repeated key; invalid UTF-8 in the front matter (`0xFF`); invalid UTF-8 in the body — each → `Reason == Malformed`, `Field == null`, `Detail` contains the rule word (`delimiter`, `yaml`, `anchor`, `alias`, `tag`, `merge`, `directive`, `document`, `mapping`, `duplicate`, `UTF-8`).
  - `Parse_BomPrefixed_IsAcceptedAndBodyExcludesBom`, `Parse_EmptyBody_IsAcceptedWithZeroLengthBody` (AC-20 parse half).
  - `Parse_TwoDelimiterLinesInBody_BothAreBody` (Edge case).
  - Ladder order and reasons: `Parse_ForeignTenant_ReturnsTenantMismatchBeforeAnythingElse` (`tenant: globex` **and** `schema: 2` **and** no `sig` → `TenantMismatch`); `Parse_TenantNotALabel_ReturnsInvalidFieldTenant` (`Acme`, `acme_1`, 64 chars → `InvalidField`, `Field == "tenant"`); `Parse_SchemaTwo_ReturnsSchemaUnsupported`; `Parse_SchemaNotPositiveInteger_ReturnsInvalidField` over `0`, `-1`, `1.1`, `one` (AC-14 parse half); `Parse_TypeUnknown_ReturnsInvalidFieldType`; `Parse_KeyIdOfOtherTenant_ReturnsKeyIdTenantMismatch` (AC-11 parse half, `Field == "key_id"`); `Parse_KeyIdMalformed_ReturnsInvalidFieldKeyId`.
  - Mandatory fields (AC-16), `[Theory]` `Parse_MandatoryFieldRemoved_ReturnsMissingFieldNamingIt` over `schema, id, tenant, type, from, to, created, key_id` on `job`, `project` on `job`, `in_reply_to`, `status` on `report`, `scope` on `context`; plus `Parse_ReportInReplyToNull_ReturnsMissingField` (null counts as absent).
  - Shapes (`InvalidField`): `Parse_IdNotUlid_ReturnsInvalidField`, `Parse_FromNotALabel_ReturnsInvalidField`, `Parse_CreatedNotRfc3339_ReturnsInvalidField` (`2026-09-27 14:05:00`, `2026-09-27T14:05Z`), `Parse_AllowedToolsScalar_ReturnsInvalidField` (a scalar where a sequence is expected), `Parse_ProjectSequence_ReturnsInvalidField` (a sequence where a scalar is expected), `Parse_TimeoutZero_ReturnsInvalidField`, `Parse_AttemptZero_ReturnsInvalidField`, `Parse_WorktreeYes_ReturnsInvalidField` (`yes` is not YAML 1.2 core), `Parse_CostUsdNegative_ReturnsInvalidField`, `Parse_AllowedToolsWithEmptyItem_ReturnsInvalidField`.
  - Report `status`/`reason` (AC-17): `Parse_ReportDoneWithReason_ReturnsInvalidFieldReason`, `Parse_ReportFailedWithoutReason_ReturnsMissingFieldReason`, `Parse_ReportFailedReasonNotToken_ReturnsInvalidFieldReason` (`"Not A Token"`).
  - Optional values (Behaviors, Edge cases): `Parse_PriorityAbsent_DefaultsToNormal`, `Parse_InReplyToNullOnJob_ParsesAsNull`, `Parse_AttemptLeadingZero_ParsesAsOneAndKeepsText` (`attempt: 01` → `Attempt == 1`, tree scalar text `01`), `Parse_AllowedToolsEmptyFlow_ParsesAsEmptyList`, `Parse_OptionalAbsentAndNull_BothParseAsNull` (`deadline`).
  - `sig` shape (AC-15), `[Theory]` `Parse_SigAbsentOrMalformed_ReturnsMissingOrInvalidSignatureWithoutEchoingValue` over: absent → `MissingSignature`; `sig: null` → `MissingSignature`; `hmac-sha1:<64 hex>` → `InvalidSignature`; `hmac-sha256:abc` → `InvalidSignature`; `hmac-sha256:` + 64 non-hex chars → `InvalidSignature`; each `Field == "sig"` and `Detail` does not contain the offending value. `Parse_SigUppercaseHex_IsAccepted` (Edge case).
  - `Parse_FrontMatterCrlfBodyLf_IsAccepted` (Edge case: line endings inside the front matter may be CRLF).
  - `Parse_CommentInFrontMatter_IsDroppedFromTree` (`# comment` line → not an entry).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeParser"` — fails at compile time (`EnvelopeParser`, `Envelope`, `Golden` do not exist).
- No integration test in this step (no real edge; the parser is pure).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `FrontMatterNode.cs`: `public abstract record FrontMatterNode` with a private-protected constructor (closed hierarchy); `public sealed record FrontMatterScalar(string Value)`; `public sealed record FrontMatterSequence(IReadOnlyList<FrontMatterNode> Items)`; `public sealed record FrontMatterMapping(IReadOnlyDictionary<string, FrontMatterNode> Entries)` whose constructor copies the input into an ordinal-sorted read-only dictionary (`SortedDictionary<string, FrontMatterNode>(StringComparer.Ordinal)` wrapped, or `ImmutableSortedDictionary` with `StringComparer.Ordinal` — both BCL, no package) so enumeration order is the canonical order by construction; `bool TryGetScalar(string key, out string? text)` convenience is `internal`.
- `FrontMatterReader` (internal static): (1) skip `EF BB BF`; (2) require `---` + `\n`/`\r\n` at offset 0 else `Malformed("missing opening delimiter")`; (3) scan line by line for the first line exactly `---` terminated by `\n`, `\r\n` or EOF; none → `Malformed("missing closing delimiter")`; (4) `Body` = bytes after that terminator; `Utf8.IsValid(body)` else `Malformed("body is not valid UTF-8")`; (5) decode the front-matter bytes with `new UTF8Encoding(false, throwOnInvalidBytes: true)` → `Malformed("front matter is not valid UTF-8")` on `DecoderFallbackException`; (6) `new YamlDotNet.Core.Parser(new StringReader(text))` consumed event by event: `StreamStart`, exactly one `DocumentStart` (`Version != null` or `Tags` non-empty → `Malformed("directives are not allowed")`; a second `DocumentStart` → `Malformed("more than one document")`), root must be `MappingStart` (else `Malformed("root is not a mapping")`); on every `NodeEvent`: non-empty `Anchor` → `Malformed("anchors are not allowed")`, an explicit non-empty `Tag` → `Malformed("explicit tags are not allowed")`; `AnchorAlias` → `Malformed("aliases are not allowed")`; a mapping key that is not a `Scalar` → `Malformed("complex keys are not allowed")`; a key whose text is `<<` → `Malformed("merge keys are not allowed")`; a repeated key at any level → `Malformed("duplicate key '<key>'")` (key names are not secrets); `YamlException` → `Malformed("yaml: <message>")`; scalars become `FrontMatterScalar(scalar.Value)` whatever their style; empty stream (no root) → `Malformed("root is not a mapping")`. Comments never surface as events. The executor confirms the exact YamlDotNet 18.1.0 event type and property names (`Scalar.Tag.IsEmpty`, `NodeEvent.Anchor.IsEmpty`, `DocumentStart.Version`) against the package's XML docs or context7 before coding — do not code from memory.
- `FieldReader` (internal static): `Int(text)` = `int.TryParse(text, NumberStyles.AllowLeadingSign, InvariantCulture)`; `Decimal` = `NumberStyles.AllowLeadingSign | AllowDecimalPoint`; `Bool` = exactly `true|True|TRUE|false|False|FALSE`; `IsNull` = `null|Null|NULL|~` or empty; `Timestamp` = `DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], InvariantCulture, DateTimeStyles.AdjustToUniversal)` (RFC 3339 `date-time`: `T`, seconds mandatory, optional fraction, `Z` or `±hh:mm`); `StringList` = a `FrontMatterSequence` of non-empty scalars; a node of the wrong kind → `InvalidField`.
- `EnvelopeParser.Parse(ReadOnlyMemory<byte> file, TenantId expectedTenant)` — `ArgumentNullException.ThrowIfNull(expectedTenant)`; the ladder in the spec's order, first failure wins: Malformed → `tenant` (missing → `MissingField`; not a label → `InvalidField`; ≠ expected → `TenantMismatch`) → `schema` (missing; not a positive integer → `InvalidField`; `> 1` → `SchemaUnsupported`; constant `SupportedSchema = 1`) → `type` → `key_id` (missing; not `<tenant>/<n>` → `InvalidField`; tenant part ≠ `tenant` → `KeyIdTenantMismatch`) → common fields `id`, `from`, `to`, `created`, `in_reply_to`, `priority` → per-type fields per the spec's tables (`reason` mandatory iff `status ≠ done`, must be absent/null when `done`; token `^[a-z][a-z0-9_]*$`) → `sig` (absent/null → `MissingSignature`; not `hmac-sha256:` + 64 hex chars either case → `InvalidSignature`, `Detail` = "sig must be hmac-sha256: followed by 64 hexadecimal characters"). Every `Detail` is a short constant sentence; it never embeds the scalar text for `sig`, `key_id` or any value (only the key name).
- `Envelope`: `public sealed class` with the properties of the spec's Contracts (`Schema`, `Type`, `Header`, `Job`, `Report`, `Context`, `KeyId`, `Signature`, `FrontMatter`, `Body`, `BodyText`); one `internal` constructor taking all of them; `BodyText` computed once (UTF-8 decode; valid by construction). Reference equality, documented. No factories yet (Step 4).
- `EnvelopeHeader`, `JobFields`, `ReportFields`, `ContextFields`, `EnvelopeRejection`, `EnvelopeResult` exactly per the spec's Contracts (`EnvelopeResult.IsAccepted` with the two `[MemberNotNullWhen]` attributes; `internal static Accepted(Envelope)` / `Rejected(reason, field, detail)` factories).
- `tests/golden/README.md` per AC-34.

**Contract impact**: ⚠️ §4 shared contract — this step is the receiver's half of the envelope protocol (reading rules 1–5, the mandatory-per-type table, the check order and reason codes); every later deliverable and `PROTOCOL.md` (15) inherit it. ⚠️ new NuGet package `YamlDotNet` 18.1.0 (MIT; only `YamlDotNet.Core` used, never the reflection serializer). ⚠️ public API: the whole `Zyggy.Core.Envelope` model. Reviewed at the Slice A gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeParser"` → every test above passes; `Parse_EveryGoldenCase_IsAccepted` reports exactly 5 rows.
- `git ls-files --eol tests/golden` → every file `i/-text`; `(Get-Item tests/golden/*.sig | ForEach-Object Length)` → `64` five times; `Select-String -Path tests/golden/*.md -Pattern '^sig: hmac-sha256:[0-9a-f]{64}$'` → 5 matches (the `-text` files may be CRLF-terminated on `job-noncanonical.md`; use `-Pattern 'sig: hmac-sha256:[0-9a-f]{64}'`).
- `tests/Zyggy.Core.Tests/bin/Debug/net10.0/golden/` contains the fifteen files plus `README.md` (the `None` glob from 01 works).
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'YamlDotNet'` → matches only in `FrontMatterReader.cs` (and, after Step 3, `FrontMatterEmitter.cs`) — the package never leaks into the public surface.
- `Select-String -Path (git ls-files 'src/**' 'tests/**') -Pattern 'geoffrey|TenantId\.Default'` → no match.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice A (a file becomes a typed, tenant-checked envelope or a reason) *(covers Steps 1–2)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: the three PROVE commands green; the Step 1 six test classes and both `EnvelopeParser*` classes green; `Parse_EveryGoldenCase_IsAccepted` runs 5 rows; the executor demonstrates on the golden job that changing `tenant` to `globex` yields `TenantMismatch` while a simultaneously broken `sig` is not even inspected (the ladder order), and that `job-noncanonical.md` parses with `x_meta.steps` as a sequence of mappings in `FrontMatter`.
- [x] Contract review: the public surface equals the spec's Contracts tables (`Tenancy` records with private constructors, `EnvelopeId`/`KeyId`/`SecretName`/`ContextScope`, the five enums + `EnvelopeWire` strings, `FrontMatterNode` hierarchy, `Envelope` members, `EnvelopeHeader`/`JobFields`/`ReportFields`/`ContextFields` signatures and defaults, `EnvelopeRejection`/`EnvelopeResult`, `EnvelopeParser.Parse(ReadOnlyMemory<byte>, TenantId)` with no other overload); the check order in `Parse` matches the spec's numbered list 1–7 and founding-spec §4 "Mandatory fields per type"; every `Detail` is a constant sentence without the offending value; the five golden `.md` files match the fixture table (tenant `acme`, keys `acme/1`/`acme/2`, CRLF only where stated, no final newline on `context-crlf-body.md`); `tests/golden/README.md` states everything AC-34 lists.
- [x] ⚠️ Risk review: new packages are exactly `Ulid` 1.4.1 (MIT) and `YamlDotNet` 18.1.0 (MIT) with no transitive runtime dependency (`dotnet list src/Zyggy.Core package --include-transitive` shows only those two plus framework references); `YamlDotNet` types appear only in `FrontMatterReader.cs`; `Ulid` appears only in `EnvelopeId.cs`; the `.sig` files were produced by the recorded `openssl` command over hand-derived `.canonical` files — the executor shows the command transcript and confirms no `.canonical`/`.sig` byte came from `Zyggy.Core`; `geoffrey` absent from `src/` and `tests/`.
- [x] User approved — implementation may continue past this gate

---

## Step 3 — `Canonicalize` reproduces every golden `.canonical` byte-for-byte: `sig` excluded, keys ordinal at every level, block mappings, flow-or-block sequences, the plain → single → double quoting ladder, LF endings, `---\n`, body untouched

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `src/Zyggy.Core/Envelope/FrontMatterEmitter.cs` *(create, internal static)* — `byte[] Emit(FrontMatterMapping mapping, bool includeSig)` through `YamlDotNet.Core.Emitter`; the **only** YAML emitter in the solution.
- `src/Zyggy.Core/Envelope/EnvelopeSigner.cs` *(create)* — `public sealed class EnvelopeSigner` carrying, in this step, only `public static byte[] Canonicalize(Envelope envelope)`; the constructor and the async members arrive in Step 5.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeSignerCanonicalizeTests.cs` *(create)*.
- `.claude/instructions/tests.md` *(modify, one line)* — line 104: replace `key id "geoffrey/test" and the fixed test secret` with `key ids acme/1 and acme/2 and the two test secrets fixed by _specs/03-envelope-signing.md` (spec Finding 2).

**Seams**: none. `Canonicalize` is pure and static.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeSignerCanonicalizeTests.cs`:
  - `Canonicalize_EveryGoldenCase_EqualsCanonicalBytes` — `[Theory] [MemberData(nameof(Golden.Cases), MemberType = typeof(Golden))]`: `EnvelopeSigner.Canonicalize(parsed).Should().Equal(Golden.Canonical(case))` — **byte arrays**, never strings (AC-2). On a failure, print both as escaped text (`\r`, `\n` visible) and a first-difference offset in the assertion message so a hand re-derivation can start from the diverging line.
  - `Canonicalize_GoldenJob_IsFileMinusFirstLineAndSigLine` — the §4 sentence as a test: for the canonical-style `job` case, the canonical bytes equal the `.md` bytes with the first line and the `sig:` line removed (computed in the test with byte operations).
  - `Canonicalize_UnknownKeySortingBeforeAgent_IsEmittedFirst` — parse the golden job with `_x: 1` added (via `EnvelopeText.WithKey`), canonical bytes start with `_x: 1\n` (Edge case; unknown fields are signed).
  - `Canonicalize_EmptySequence_EmitsEmptyFlowBrackets` — `allowed_tools: []` → the line `allowed_tools: []`.
  - `Canonicalize_SequenceWithMappingItem_EmitsBlockStyleIndented` — `x_steps:` with `- name: one` items → block lines indented 2 under the key (freezes the §4 "block style otherwise" rule).
  - `Canonicalize_ScalarNeedingQuotes_UsesSingleThenDoubleQuotes` — `[Theory]`: `a: b` → `'a: b'`; `#x` → `'#x'`; ` lead` → `' lead'`; `-x` → `'-x'`; `it's` → `"it's"`; a value with `\n` → double-quoted with `\n` escape; `""` → `''` (the ladder in §4 (c)).
  - `Canonicalize_SigPresent_IsNotEmitted` — the golden job's canonical bytes contain no `sig:` line while `key_id:` is present.
  - `Canonicalize_CrlfFrontMatter_EmitsLf` — `job-noncanonical` canonical bytes contain no `\r` before the `---\n` separator (the body may).
  - `Canonicalize_Null_ThrowsArgumentNullException`.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerCanonicalizeTests"` — fails at compile time (`EnvelopeSigner` does not exist).
- Golden files: the five `.canonical` fixtures from Step 2 are the oracle; no new case is needed. If a row is red, follow the fixture rule in "Test key material and golden fixtures" (hand re-derivation only).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `FrontMatterEmitter.Emit(mapping, includeSig)`: `var settings = new EmitterSettings().WithNewLine("\n").WithIndentedSequences()` (indentation 2 is the default; `BestWidth` stays `int.MaxValue` — the executor confirms these defaults and the exact builder names on 18.1.0 via context7 and records them in the gate summary); `new Emitter(writer, settings)` over a `StringWriter`; events: `StreamStart`, `DocumentStart(null, null, isImplicit: true)`, then the tree: `MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block)` and for every entry in ordinal order (skipping top-level `sig` when `!includeSig`) a key `Scalar(AnchorName.Empty, TagName.Empty, key, ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: true)` and the value: scalar → the same `Scalar` shape with the parsed text (the emitter downgrades Plain → SingleQuoted → DoubleQuoted per libyaml's analysis — this is the behaviour §4 (c) froze); sequence → `SequenceStart(..., SequenceStyle.Flow)` when every item is a `FrontMatterScalar` (including the empty sequence, which yields `[]`), `SequenceStyle.Block` otherwise; mapping → block `MappingStart`; then `DocumentEnd(isImplicit: true)`, `StreamEnd`. Output encoded with `new UTF8Encoding(false)`. Because the document is implicit there is no leading `---`; assert in code (`Debug.Assert`) that the output does not start with `---`.
- `EnvelopeSigner.Canonicalize(Envelope envelope)`: `ArgumentNullException.ThrowIfNull`; `Emit(envelope.FrontMatter, includeSig: false)` + `"---\n"u8` + `envelope.Body` into one `byte[]`. XML doc: "the single source of truth for the signing input (§9); golden-file tests under `tests/golden/`".
- If the emitter output differs from a hand-derived `.canonical` on a construct where the §4 text is unambiguous and the fixture is right, the emission code (not the fixture) is adjusted — for example by choosing the scalar style explicitly from a Zyggy-side analysis rather than relying on `ScalarStyle.Plain` downgrade. If §4 itself is ambiguous for the construct, stop at the Slice B gate with the two candidate byte sequences.

**Contract impact**: ⚠️ §4 canonical form — the shared protocol contract between every machine; frozen by the five golden cases from this step on. Any later change is a protocol change requiring a new case in RED (and `PROTOCOL.md` once 15 exists). Reviewed at the Slice B gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerCanonicalizeTests"` → all pass; the golden theory reports 5 rows.
- `git status --porcelain tests/golden` → empty (no fixture changed during GREEN) **or** a listed change accompanied by the executor's written hand re-derivation note for the gate.
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'new Emitter\('` → exactly one match (`FrontMatterEmitter.cs`).
- `Select-String -Path .claude/instructions/tests.md -Pattern 'geoffrey'` → no match.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 4 — A Zyggy-built envelope serialises in canonical style (`---\n` + front matter with `sig` + `---\n` + body), a parsed file round-trips byte-identically with every unknown field, and an unsigned envelope is never written

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `src/Zyggy.Core/Envelope/Envelope.cs` *(modify)* — add `CreateJob`, `CreateReport`, `CreateContext` factories and the internal front-matter builder they share.
- `src/Zyggy.Core/Envelope/EnvelopeWriter.cs` *(create)* — `public static class EnvelopeWriter { public static byte[] Serialize(Envelope envelope); }`.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeWriterTests.cs`, `EnvelopeFactoryTests.cs` *(create)*.

**Seams**: none.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeWriterTests.cs`:
  - `Serialize_CanonicalStyleGoldenCase_RoundTripsByteIdentically` — `[Theory]` over `job`, `report`, `context`, `context-crlf-body`: `EnvelopeWriter.Serialize(Parse(md))` byte-equals `Golden.Md(case)` (AC-5).
  - `Serialize_GoldenJobNoncanonical_ReparsesAndCanonicalisesToGoldenAndKeepsUnknownKeys` — AC-6: `bytes = Serialize(Parse(md))`; `Parse(bytes)` is accepted; `Canonicalize(reparsed)` equals `Golden.Canonical("job-noncanonical")`; `bytes` contain `x_experimental` and `x_meta` as UTF-8 text.
  - `Serialize_UnsignedEnvelope_ThrowsInvalidOperationException` — an envelope from `CreateJob` (not yet signed) → `InvalidOperationException` (never write unsigned).
  - `Serialize_Null_ThrowsArgumentNullException`.
  - `Serialize_Output_StartsWithDelimiterAndHasNoBom`.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeFactoryTests.cs`:
  - `CreateJob_SubSecondOffsetCreated_EmitsUtcWholeSecondsText` — `Header.Created = 2026-09-27T16:05:00.789+02:00` → `FrontMatter.Entries["created"]` is the scalar `2026-09-27T14:05:00Z` and `Header.Created == 2026-09-27T14:05:00Z` (AC-21 factory half); same for `Deadline`.
  - `CreateJob_Defaults_EmitsSchemaPriorityAttemptWorktreeAndOmitsNulls` — front matter has `schema: 1`, `priority: normal`, `attempt: 1`, `worktree: false`, no `agent`, `deadline`, `timeout_minutes`, `in_reply_to`, `key_id`, `sig` keys; `allowed_tools`/`report_back` absent when null and `[]` when empty lists are given.
  - `CreateReport_DoneWithReason_ThrowsArgumentException`, `CreateReport_FailedWithoutReason_ThrowsArgumentException`, `CreateJob_EmptyProject_ThrowsArgumentException`, `CreateJob_InvalidUtf8Body_ThrowsArgumentException`, `CreateContext_ProjectScope_EmitsScopeText` (`scope: project:calizr`).
  - `CreateReport_CostUsd_EmitsInvariantDecimal` (`0.42m` → `cost_usd: 0.42`; `1m` → `cost_usd: 1`).
  - `Create_ExactlyOneTypedViewIsNonNull` — `[Theory]` over the three factories.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeWriterTests|FullyQualifiedName~EnvelopeFactoryTests"` — fails at compile time (`EnvelopeWriter`, `Envelope.CreateJob` do not exist).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- Factories in `Envelope`: validate with the same rules as the parser (throwing `ArgumentException` naming the field — caller bug), truncate every `DateTimeOffset` to whole seconds and convert to UTC (`Header` is re-created with the truncated `Created`, so the typed view and the tree agree), build the `FrontMatterMapping` from the records in canonical scalar text: integers and `attempt` via `ToString(InvariantCulture)`, decimals via `decimal.ToString(CultureInfo.InvariantCulture)` (no exponent; trailing zeros as given), booleans `true`/`false`, timestamps `yyyy-MM-dd'T'HH:mm:ss'Z'`, enums via `EnvelopeWire.ToWire`, lists as `FrontMatterSequence` of scalars (empty list → empty sequence, null → key omitted), always `schema: 1` and `priority`, for jobs always `attempt` and `worktree`; no `key_id`/`sig` (`KeyId`/`Signature` null). Body must satisfy `Utf8.IsValid`.
- `EnvelopeWriter.Serialize`: `ArgumentNullException.ThrowIfNull`; `Signature == null` → `InvalidOperationException("envelope is unsigned; sign it before writing (§4)")`; `"---\n"u8` + `FrontMatterEmitter.Emit(envelope.FrontMatter, includeSig: true)` + `"---\n"u8` + `Body`. There is no second serialiser: both `Serialize` and `Canonicalize` call `FrontMatterEmitter.Emit`.

**Contract impact**: ⚠️ §4 on-bus file format (the writer's half). No new package. Reviewed at the Slice B gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeWriterTests|FullyQualifiedName~EnvelopeFactoryTests"` → all pass; the round-trip theory reports 4 rows.
- `git status --porcelain tests/golden` → empty.
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'DateTime\.UtcNow|DateTimeOffset\.UtcNow|TimeProvider\.System|Process|HttpClient|ILogger'` → no match (AC-31: 03 has no clock, no I/O beyond the store, no logging).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice B (the canonical bytes and byte-identical writing) *(covers Steps 3–4)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `EnvelopeSignerCanonicalizeTests` (golden theory 5 rows + rule tests), `EnvelopeWriterTests` (round trip 4 rows + AC-6), `EnvelopeFactoryTests` green; the three PROVE commands green; the executor shows, for `job-noncanonical`, the `.md` bytes, the `.canonical` bytes and `Serialize(Parse(md))` side by side (sorted keys, LF, `worktree: true`, quoted `'a: b'`, `''`, `"it's: here"`, nested `x_meta` with the block `steps` sequence and flow `tags`).
- [x] Contract review: `Canonicalize` output matches founding-spec §4 "Canonical form, normative details" rule by rule (sig excluded, ordinal keys at every level, block mappings 2-space, flow/block/`[]` sequences, plain → single → double, `\n`, `---\n`, body untouched, no BOM, no opening `---`); `Serialize` = `---\n` + the same emission with `sig` + `---\n` + body; one emitter function only; the factories' canonical scalar text rules (UTC whole seconds, invariant numbers, `true`/`false`, `schema: 1`, `priority`, job `attempt`/`worktree` always present, null optionals omitted, `[]` for empty); `Serialize` refuses unsigned envelopes.
- [x] ⚠️ Risk review: no `.canonical` or `.sig` byte was changed during Steps 3–4 (`git log --stat -- tests/golden` shows only the Step 2 commit) — or every change is listed with its hand re-derivation and the §4 rule that had been misapplied; the `EmitterSettings` used (`WithNewLine("\n")`, indented sequences, indentation 2, unlimited width) are recorded; the executor confirms the YamlDotNet emitter's quoting decisions were verified against §4 (c), not the other way round; `.claude/instructions/tests.md` line 104 no longer names `geoffrey/test`.
- [x] User approved — implementation may continue past this gate

---

## Step 5 — An envelope is signed with its tenant's key obtained through `ISecretStore`: the digest equals the `openssl`-produced golden `.sig` for every case, re-signing replaces `key_id` and `sig`, a missing or short key is `UnknownKey` without leaking bytes, and a foreign key id is a caller bug

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `src/Zyggy.Core/Secrets/ISecretStore.cs` *(create)* — the seam.
- `src/Zyggy.Core/Secrets/InMemorySecretStore.cs` *(create)* — the public test double (instance state only).
- `src/Zyggy.Core/Envelope/EnvelopeSigner.cs` *(modify)* — primary constructor `EnvelopeSigner(ISecretStore secrets)`, `SignAsync`; `MinimumKeyLength = 32` constant.
- `src/Zyggy.Core/Envelope/Envelope.cs` *(modify)* — `internal Envelope WithSignature(KeyId keyId, string signature)` returning a copy whose `FrontMatter` has `key_id` and `sig` replaced (ordinal position preserved by the mapping's construction) and `KeyId`/`Signature` set; everything else shared.
- `tests/Zyggy.Core.Tests/Infrastructure/TestKeys.cs` *(create)* — `public static class TestKeys`: `KeyId Acme1`, `Acme2`, `byte[] Secret1` (UTF-8 of `zyggy-golden-test-key-acme-00001`), `Secret2`, `Task<InMemorySecretStore> StoreWithBothAsync(ct)`, `StoreWithAsync(KeyId, ct)`.
- `tests/Zyggy.Core.Tests/Secrets/InMemorySecretStoreTests.cs`, `tests/Zyggy.Core.Tests/Envelope/EnvelopeSignerSignTests.cs` *(create)*.

**Seams**: `ISecretStore` — **faked** with `InMemorySecretStore` (the spec's designated substitute; `NSubstitute` is reserved for "never called" assertions in Step 6).

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- `tests/Zyggy.Core.Tests/Secrets/InMemorySecretStoreTests.cs`: `GetAsync_AfterSet_ReturnsSameBytes`, `GetAsync_Absent_ReturnsNull`, `GetAsync_OtherTenantSameName_ReturnsNull` (tenant scoping), `RemoveAsync_Present_ReturnsTrueThenGetIsNull`, `RemoveAsync_Absent_ReturnsFalse`, `SetAsync_EmptyValue_ThrowsArgumentException`, `SetAsync_Twice_Overwrites`, `SetAsync_CallerMutatesBufferAfterwards_StoredValueUnchanged` (defensive copy), `TwoInstances_DoNotShareState` (no static state).
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeSignerSignTests.cs` (every async test passes `TestContext.Current.CancellationToken`):
  - `SignAsync_EveryGoldenCase_ProducesTheGoldenSignature` — `[Theory] [MemberData(Golden.Cases)]`: parse the case, `SignAsync(parsed, parsed.KeyId!, ct)` with `StoreWithBothAsync` → accepted; `Signature == "hmac-sha256:" + Golden.Sig(case)`; `Signature` equals the parsed file's own `sig` value; `Golden.Sig(case)` is 64 chars of `[0-9a-f]` with no newline (AC-3).
  - `SignAsync_UnsignedFactoryEnvelope_SetsKeyIdAndSigAndSerialisesToAParsableFile` — AC-21 full: `CreateJob` with sub-second `+02:00` `Created`, `SignAsync(env, Acme1)`, `Serialize`, `Parse` → accepted, `KeyId == acme/1`, `Signature` present, `Header.Created` equal on both sides, the serialised text contains `created: 2026-09-27T14:05:00Z`.
  - `SignAsync_AlreadySignedWithOtherKey_ReplacesKeyIdAndSig` — golden context (`acme/2`) re-signed with `acme/1` → `KeyId == acme/1`, `FrontMatter.Entries["key_id"]` is `acme/1`, `Signature` ≠ the original, `Canonicalize` output identical except the `key_id` line.
  - `SignAsync_KeyAbsent_ReturnsUnknownKeyWithDetailAbsent` — store holds only `acme/2`, sign with `acme/1` → `UnknownKey`, `Field == "key_id"`, `Detail` contains `absent`.
  - `SignAsync_KeyShorterThan32Bytes_ReturnsUnknownKeyWithDetailShort` — a 31-byte key → `UnknownKey`, `Detail` contains `shorter than 32 bytes`, and `Detail` does not contain the key's hex or text (AC-22, AC-33).
  - `SignAsync_KeyOfOtherTenant_ThrowsArgumentException` — `SignAsync(acmeEnvelope, KeyId.Parse("globex/1"))` → `ArgumentException` (caller bug, not a protocol outcome).
  - `SignAsync_DoesNotMutateInput` — the input envelope's `Signature`/`FrontMatter` unchanged after the call.
  - `SignAsync_Cancelled_ThrowsOperationCanceledException` — a pre-cancelled token; the store substitute (`Substitute.For<ISecretStore>()` returning a cancelled task) is acceptable here.
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerSignTests|FullyQualifiedName~InMemorySecretStoreTests"` — fails at compile time (`ISecretStore`, `InMemorySecretStore`, `SignAsync` do not exist).
- Golden files: `.sig` fixtures from Step 2 are the oracle (produced by `openssl`, never by this code).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `ISecretStore` exactly per the spec's Contracts (`GetAsync` returns `Task<ReadOnlyMemory<byte>?>`, null = absent, never throws for absence; `SetAsync` rejects empty; `RemoveAsync` returns false when absent). XML doc: "the only type that may reference a secret store (§9 seam); every lookup is tenant-scoped; the full name is `zyggy/<tenant>/<name>` (§8)".
- `InMemorySecretStore`: `public sealed class`, a `ConcurrentDictionary<(TenantId, SecretName), byte[]>` instance field (records give value equality for the key), copies on set and on get (`ToArray()`), honours the token (`ct.ThrowIfCancellationRequested()`), no static member.
- `EnvelopeSigner(ISecretStore secrets)`: `ArgumentNullException.ThrowIfNull`; `SignAsync(envelope, signWith, ct)`: `ThrowIfNull` both; `signWith.Tenant != envelope.Header.Tenant` → `ArgumentException`; `key = await secrets.GetAsync(envelope.Header.Tenant, signWith.SecretName, ct)`; null → `Rejected(UnknownKey, "key_id", "signing key is absent from the secret store")`; `Length < MinimumKeyLength` → `Rejected(UnknownKey, "key_id", "signing key is shorter than 32 bytes")`; then, because the canonical bytes exclude `sig` but include `key_id`, `candidate = envelope.WithSignature(signWith, signature: null)` (the internal copy with `key_id` replaced and `sig` removed) → `digest = HMACSHA256.HashData(key.Span, Canonicalize(candidate))` → `sig = "hmac-sha256:" + Convert.ToHexStringLower(digest)` → `Accepted(candidate.WithSignature(signWith, sig))`. Key bytes never appear in any message; there is no logging in 03.
- `Envelope.WithSignature` (internal): new `FrontMatterMapping` from the existing entries with `key_id` set to `signWith.ToString()` and `sig` set or removed; the typed views are reused.

**Contract impact**: ⚠️ signing (Risk Area): HMAC-SHA256 over `Canonicalize`, `sig` = `hmac-sha256:` + 64 lowercase hex (founding-spec §4 Signature, amended). ⚠️ public seam `ISecretStore` (§8/§9/§14) — a shared contract 07/09 implement; `InMemorySecretStore` public for other test projects. Reviewed at the Slice C gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerSignTests|FullyQualifiedName~InMemorySecretStoreTests"` → all pass; the golden theory reports 5 rows, proving the `openssl` oracle and `HMACSHA256.HashData` agree on every case.
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'ISecretStore'` → matches only in `Secrets/ISecretStore.cs`, `Secrets/InMemorySecretStore.cs` and `Envelope/EnvelopeSigner.cs` (the seam is referenced by the signer and implementations only).
- `Select-String -Path (git ls-files 'src/**' 'tests/**') -Pattern 'zyggy-golden-test-key'` → matches only in `tests/Zyggy.Core.Tests/Infrastructure/TestKeys.cs` and `tests/golden/README.md`.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 6 — A signed envelope verifies only when tenant, key and every canonical byte match: every golden case is accepted, tampering with any front-matter key, any body byte, an added or removed field or the body length is `InvalidSignature`, a foreign tenant or foreign `key_id` or future schema never triggers a key lookup, and rotation is whatever the store holds

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `src/Zyggy.Core/Envelope/EnvelopeSigner.cs` *(modify)* — `VerifyAsync`, `ParseAndVerifyAsync`.
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeSignerVerifyTests.cs` *(create)*.

**Seams**: `ISecretStore` — faked with `InMemorySecretStore`; `Substitute.For<ISecretStore>()` only for the "never called" assertions (AC-10, AC-11, AC-14).

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeSignerVerifyTests.cs`:
  - `ParseAndVerifyAsync_EveryGoldenCase_IsAccepted` — `[Theory] [MemberData(Golden.Cases)]` with `StoreWithBothAsync` (AC-4).
  - `ParseAndVerifyAsync_TopLevelKeyMutated_IsRejectedWithExpectedReason` — `[Theory]` over every top-level key of `golden/job.md` except `sig`, each with a different **well-formed** value: `agent` → `other-agent`, `allowed_tools` → `[Read]`, `attempt` → `2`, `created` → `2026-09-27T14:05:01Z`, `deadline` → `2026-09-29T18:00:01Z`, `from` → `home-laptop`, `id` → another ULID, `priority` → `high`, `project` → `other`, `report_back` → `[summary]`, `timeout_minutes` → `31`, `to` → `central`, `worktree` → `false` → all `InvalidSignature`; `type` → `context` → `MissingField` (`scope`), because a well-formed type change alters the mandatory field set before the digest is reached — its own row with that expected reason (Assumption 9); `tenant` → `globex` → `TenantMismatch`; `key_id` → `globex/1` → `KeyIdTenantMismatch`; `key_id` → `acme/3` → `UnknownKey`; `schema` → `2` → `SchemaUnsupported` (AC-7). Plus `ParseAndVerifyAsync_SigMutated_IsInvalidSignature` (one hex digit changed).
  - `ParseAndVerifyAsync_AnyBodyByteFlipped_IsInvalidSignature` — `[Theory] [MemberData]` over every body offset of `golden/job.md` (the two lines, ~120 rows), XOR `0x01` on that byte (skip any offset where the flip would produce invalid UTF-8 — the body is ASCII so none does) → `InvalidSignature` (AC-8).
  - `ParseAndVerifyAsync_StructuralTamper_IsInvalidSignature` — `[Theory]`: `x_added: 1` appended; `agent` removed; a body byte appended; the body's final newline removed → `InvalidSignature` (AC-9).
  - `ParseAndVerifyAsync_ForeignTenant_IsTenantMismatchAndStoreNeverCalled` — `tenant: globex`, `key_id: globex/1`, substitute store → `TenantMismatch`; `await store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default)` (AC-10).
  - `ParseAndVerifyAsync_KeyIdOfOtherTenant_IsKeyIdTenantMismatchAndStoreNeverCalled` (AC-11).
  - `ParseAndVerifyAsync_SchemaTwo_IsSchemaUnsupportedAndStoreNeverCalled`; `EnvelopeWire.ToWire(SchemaUnsupported) == "schema_unsupported"` (AC-14).
  - `ParseAndVerifyAsync_UnknownKeyNumber_IsUnknownKey` — signed with `acme/3`; store holds 1 and 2 (AC-12).
  - `ParseAndVerifyAsync_GoldenContextSignedWithKey2_AcceptedWithBothKeysRejectedWithKey1Only` — two Acts split into two tests: `…_StoreHoldsBoth_IsAccepted` and `…_StoreHoldsOnlyKey1_IsUnknownKey` (AC-13, rotation).
  - `ParseAndVerifyAsync_SigComputedWithOtherKey_IsInvalidSignature` — `key_id: acme/1` but the digest made with `acme/2` (sign the golden job with `acme/2`, then set `key_id` back to `acme/1` via `EnvelopeText`) (Edge case).
  - `ParseAndVerifyAsync_SigUppercaseHex_IsAccepted` (Edge case; digest compared as bytes).
  - `VerifyAsync_ForeignTenantEnvelope_IsTenantMismatchWithoutLookup` — `VerifyAsync(parsedGlobexEnvelope, acme)`.
  - `VerifyAsync_UnsignedEnvelope_IsMissingSignature` — a `CreateJob` envelope.
  - `VerifyAsync_StoreThrows_Propagates` — substitute throwing `IOException` → the exception propagates (infrastructure fault, not a protocol outcome).
  - `Rejections_NeverContainKeyOrDigest` — for `InvalidSignature` and `UnknownKey` outcomes, `Detail` contains neither the hex of either secret, the secret text, nor the 64-hex digest (AC-33).
- Failing-run command: `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerVerifyTests"` — fails at compile time (`VerifyAsync`, `ParseAndVerifyAsync` do not exist).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `VerifyAsync(envelope, expectedTenant, ct)`: `ThrowIfNull` both; (a) `Header.Tenant != expectedTenant` → `TenantMismatch` (no lookup); (b) `Signature == null` → `MissingSignature`; (c) `secrets.GetAsync(Header.Tenant, KeyId!.SecretName, ct)` → null or `< 32` bytes → `UnknownKey` (same constant details as `SignAsync`); (d) `expected = Convert.FromHexString(Signature["hmac-sha256:".Length..])`, `actual = HMACSHA256.HashData(key.Span, Canonicalize(envelope))`, `CryptographicOperations.FixedTimeEquals(actual, expected)` → accepted or `Rejected(InvalidSignature, "sig", "signature does not match the canonical form")`. Store exceptions propagate untouched.
- `ParseAndVerifyAsync(file, expectedTenant, ct)`: `Parse` then `VerifyAsync`; a parse rejection is returned as is (the store is therefore never touched for `Malformed`, `TenantMismatch`, `SchemaUnsupported`, `KeyIdTenantMismatch`, field and `sig`-shape failures).

**Contract impact**: ⚠️ signing / tenant isolation (Risk Area): the §4 receiver rules "missing, unknown or invalid signature", "tenant check before the signature check so a key from another tenant is never even looked up", §8 "rejected before any model call". Reviewed at the Slice C gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~EnvelopeSignerVerifyTests"` → all pass; the key-mutation theory has one row per top-level key of `job.md` except `sig` (17 keys) and the body theory one row per body byte.
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'FixedTimeEquals'` → exactly one match; `Pattern 'SequenceEqual|== expected'` near the digest comparison → none (no non-constant-time comparison of digests).
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'ParseAndVerifyAsync\(|VerifyAsync\(|Parse\('` → every declaration has a `TenantId expectedTenant` parameter (AC-31: no overload without it).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice C (sign and verify through the `ISecretStore` seam) *(covers Steps 5–6)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `InMemorySecretStoreTests`, `EnvelopeSignerSignTests` (golden theory 5 rows: every code-produced `sig` equals the `openssl` oracle) and `EnvelopeSignerVerifyTests` (17-key mutation theory, per-byte body theory, structural tampers, never-called assertions, rotation) green; the three PROVE commands green; the executor demonstrates by hand on the golden job: `openssl dgst -sha256 -hmac 'zyggy-golden-test-key-acme-00001' tests/golden/job.canonical` prints the hex that `job.md` carries.
- [x] Contract review: `ISecretStore` signature equals the spec's Contracts (`(TenantId, SecretName, CancellationToken)`, null for absence, empty value rejected, `RemoveAsync` bool); `EnvelopeSigner` public members are exactly `Canonicalize` (static), `SignAsync(Envelope, KeyId, CancellationToken)`, `VerifyAsync(Envelope, TenantId, CancellationToken)`, `ParseAndVerifyAsync(ReadOnlyMemory<byte>, TenantId, CancellationToken)`; the `VerifyAsync` order (a)–(d) and the `SignAsync` rules match the spec; `sig` written lowercase, read either case; `MinimumKeyLength` 32 is a constant, not configurable.
- [x] ⚠️ Risk review (signing, secrets, tenant isolation): digest comparison uses `CryptographicOperations.FixedTimeEquals`; no `Detail`, exception message or test output contains key bytes, key hex or a digest (`Rejections_NeverContainKeyOrDigest` plus a read of every `Rejected(...)` call site); the two test secrets appear only in `TestKeys.cs` and `tests/golden/README.md`; the tenant check precedes any store call in both `Parse` (structurally — no store reference) and `VerifyAsync` (asserted with substitutes); `ISecretStore` is referenced only by its implementations and `EnvelopeSigner`; no static state in `InMemorySecretStore`.
- [x] User approved — implementation may continue past this gate

---

## Step 7 — A key set through `AddFileSecretStore` lands in `<root>/<tenant>/hmac/<n>` as one hex line with owner-only permissions on Linux (0600, directories 0700) and Windows (protected DACL, current user only), reads back, is removed, is never resolvable outside `<root>/<tenant>/`, and a corrupt file is an infrastructure fault

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `Directory.Packages.props` *(modify)* — add `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12, `Microsoft.Extensions.Options` 10.0.12 (both MIT; spec Dependencies table) and, test-only, `Microsoft.Extensions.DependencyInjection` 10.0.12 (MIT; the container implementation the resolution tests need — see Assumptions).
- `src/Zyggy.Core/Zyggy.Core.csproj` *(modify)* — `PackageReference` for the two abstractions packages.
- `tests/Directory.Build.props` *(modify)* — `<PackageReference Include="Microsoft.Extensions.DependencyInjection" />` (test-only, alongside the seven test packages from 01).
- `src/Zyggy.Core/Secrets/FileSecretStoreOptions.cs` *(create)* — `public sealed class FileSecretStoreOptions { public string? RootDirectory { get; set; } }`.
- `src/Zyggy.Core/Secrets/FileSecretStore.cs` *(create, internal sealed)*.
- `src/Zyggy.Core/Secrets/ServiceCollectionExtensions.cs` *(create)* — `AddFileSecretStore(this IServiceCollection, Action<FileSecretStoreOptions>? configure = null)`.
- `src/Zyggy.Core/README.md` *(modify)* — a "Troubleshooting — Secrets: file store" section (spec Failure modes: location per OS, expected content, permissions, how to reseed, "no `HOME`" construction failure, corrupt hex); update the `Secrets/` and add a `Tenancy/` row in the responsibilities table to describe what now exists.
- `tests/Zyggy.Integration/Secrets/FileSecretStoreTests.cs` *(create)* — the real-edge tests.
- `tests/Zyggy.Core.Tests/Secrets/FileSecretStoreRegistrationTests.cs` *(create)* — DI resolution.

**Seams**: `ISecretStore` — **wired**: `FileSecretStore` is the real edge (file system + OS permissions), the P0 store on all three machines (O20).

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Integration test file: `tests/Zyggy.Integration/Secrets/FileSecretStoreTests.cs` (namespace `Zyggy.Integration.Secrets`, `public sealed class FileSecretStoreTests : IDisposable`; each test creates its own root under `Path.Combine(Path.GetTempPath(), "zyggy-it", "secrets-" + Guid.NewGuid().ToString("N"))` and deletes it in `Dispose`; the store is obtained through `new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = root).BuildServiceProvider().GetRequiredService<ISecretStore>()` because the type is internal; `public static bool IsLinux => OperatingSystem.IsLinux();` and `IsWindows` for `[Fact(SkipUnless = nameof(IsLinux))]` / `nameof(IsWindows)`):
  - Happy path `SetGetRemoveGet_RoundTripsThirtyTwoBytes` — `SetAsync(acme, hmac/1, 32 bytes)` → `GetAsync` returns the same bytes → `RemoveAsync` true → `GetAsync` null → `RemoveAsync` false (AC-26; split into the smallest number of tests that keep one Act each: `GetAsync_AfterSet_ReturnsSameBytes`, `RemoveAsync_Present_ReturnsTrueAndGetReturnsNull`, `RemoveAsync_Absent_ReturnsFalse`).
  - `SetAsync_WritesLowercaseHexLineAtTenantSlashName` — `File.ReadAllText("<root>/acme/hmac/1")` equals `Convert.ToHexStringLower(bytes) + "\n"`; no `.tmp` file remains.
  - `GetAsync_Absent_ReturnsNullWithoutThrowing`.
  - `GetAsync_UppercaseHexWithSurroundingWhitespace_IsAccepted` — a file written by hand (as an operator with `openssl rand -hex 32` would) with uppercase hex and `\r\n` → the bytes.
  - Failure path `GetAsync_NonHexContent_ThrowsInvalidDataExceptionNamingPath` — `InvalidDataException` whose message contains the file path and not the content (AC-28, AC-33).
  - `GetAsync_OddLengthHex_ThrowsInvalidDataException`.
  - `SetAsync_OtherTenantSameName_IsIsolated` — `acme` and `globex` files are distinct paths; `GetAsync(globex, hmac/1)` is null when only `acme` was set.
  - `[Fact(SkipUnless = nameof(IsLinux))] SetAsync_OnLinux_FileIs0600AndDirectoriesAre0700` — `File.GetUnixFileMode(file) == UserRead | UserWrite`; `<root>/acme` and `<root>/acme/hmac` == `UserRead | UserWrite | UserExecute` (AC-27).
  - `[Fact(SkipUnless = nameof(IsLinux))] SetAsync_OnLinuxOverwrite_KeepsMode`.
  - `[Fact(SkipUnless = nameof(IsWindows))] SetAsync_OnWindows_DaclIsProtectedAndOwnerOnly` — `new FileInfo(file).GetAccessControl()`: `AreAccessRulesProtected == true`; `GetAccessRules(true, false, typeof(SecurityIdentifier))` has exactly one rule: `IdentityReference == WindowsIdentity.GetCurrent().User`, `FileSystemRights.FullControl`, `AccessControlType.Allow` (AC-27).
  - `[Fact(SkipUnless = nameof(IsWindows))] SetAsync_OnWindowsOverwrite_KeepsDacl`.
  - `SetAsync_EmptyValue_ThrowsArgumentException`.
  - `Constructor_EmptyRootDirectory_ThrowsInvalidOperationException` — `AddFileSecretStore(o => o.RootDirectory = "")` then resolving `ISecretStore` → `InvalidOperationException` mentioning `RootDirectory` (the "no `HOME`" path, simulated via the option since the default cannot be blanked safely in a test).
  - Harness: temp directory only — no bare repo, no fake-claude (03 has no bus or process edge).
  - Failing-run command: `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~FileSecretStoreTests"` — fails at compile time (`AddFileSecretStore` does not exist).
- Unit test file: `tests/Zyggy.Core.Tests/Secrets/FileSecretStoreRegistrationTests.cs`: `AddFileSecretStore_ResolvesSingletonISecretStore` (two `GetRequiredService<ISecretStore>()` calls return the same instance whose type name is `FileSecretStore`); `AddFileSecretStore_WithoutConfigure_UsesLocalApplicationDataDefault` — resolve with no configure action on a machine where `Environment.GetFolderPath(LocalApplicationData)` is non-empty and assert no exception (the default path is not touched: construction performs no I/O).
- Failing-run command (unit): `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~FileSecretStoreRegistrationTests"`.

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `FileSecretStore(IOptions<FileSecretStoreOptions> options)` (`internal sealed class : ISecretStore`): root = `options.Value.RootDirectory` or `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "zyggy", "secrets")`; empty → `InvalidOperationException("FileSecretStoreOptions.RootDirectory must be set: no user profile directory is available")`. Path = `Path.Combine(root, tenant.Value, name.Value)` — both segments validated types (no `..`, no leading `/`), so no traversal is expressible; no other path composition (this is the store's own layout, not a bus path — `BusPaths` is 04 and does not apply). No cache, no logging, no static field.
  - `GetAsync`: `File.Exists` false → `null`; read text (`File.ReadAllTextAsync`), `Trim()`, `Convert.FromHexString` inside `try` → `FormatException` → `InvalidDataException($"secret file '{path}' does not contain hexadecimal text")` (never the content).
  - `SetAsync`: `value.IsEmpty` → `ArgumentException`; content = `Convert.ToHexStringLower(value.Span) + "\n"`; Linux/macOS (`!OperatingSystem.IsWindows()`): `Directory.CreateDirectory(dir, UnixFileMode.UserRead | UserWrite | UserExecute)` for `<root>/<tenant>` and each further segment (create level by level so every created directory is 0700), file created via `new FileStream(tmp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UserWrite })`; Windows (`[SupportedOSPlatform("windows")]` private method): `Directory.CreateDirectory(dir)`, `var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));` then `FileSystemAclExtensions.Create(new FileInfo(tmp), FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security)`; write the bytes, flush, dispose; then `File.Move(tmp, path, overwrite: true)` (the final path never exists with permissive permissions; the tmp name is `<name>.tmp` next to the file, `Guid`-suffixed so concurrent writers never share it). On any failure delete the tmp file and rethrow.
  - `RemoveAsync`: `File.Exists` → `File.Delete` → `true`; else `false`.
  - The executor confirms at the first build that `FileSecurity`/`FileSystemAclExtensions` compile without a package reference (shared framework); if a `System.IO.FileSystem.AccessControl` reference is required, add 5.0.0 (MIT) to the props with a comment and report it at the gate.
- `ServiceCollectionExtensions.AddFileSecretStore` (`Zyggy.Core.Secrets`): `services.AddOptions<FileSecretStoreOptions>()`, `if (configure != null) services.Configure(configure)`, `services.AddSingleton<ISecretStore, FileSecretStore>()`; returns `services`.
- `src/Zyggy.Core/README.md` sections as listed in Scope.

**Contract impact**: ⚠️ secrets on disk (Risk Area, §8 P0 file store, O20): key material in `<root>/<tenant>/hmac/<n>` with 0600 / owner-only ACL; hex content so operators can seed with `openssl rand -hex 32`. ⚠️ new packages `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12, `Microsoft.Extensions.Options` 10.0.12 (Core) and `Microsoft.Extensions.DependencyInjection` 10.0.12 (tests only). ⚠️ public API: `FileSecretStoreOptions`, `AddFileSecretStore`. Reviewed at the Slice D gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test tests/Zyggy.Integration --filter "FullyQualifiedName~FileSecretStoreTests"` → all pass on Windows; the two `IsLinux` facts are reported as skipped here and proven by CI's `ubuntu-latest` job at the gate.
- `dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~FileSecretStoreRegistrationTests"` → 2 passed.
- After the run `<temp>/zyggy-it/` contains no `secrets-*` directory (dispose works; on Windows the protected-DACL file must still be deletable by its owner — if `Directory.Delete` fails, the fixture resets the DACL first and the README notes it).
- `Select-String -Path (git ls-files 'src/Zyggy.Core/**/*.cs') -Pattern 'FileSecurity|UnixCreateMode|File\.|Directory\.'` → matches only in `Secrets/FileSecretStore.cs` (the only I/O in 03).
- By hand (Windows): `icacls <root>\acme\hmac\1` shows a single `(F)` entry for the current user and no inherited entries.

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## Step 8 — A host composes `AddFileSecretStore().AddEnvelopeSigning()` and signs and verifies the golden job with a key seeded on disk the way an operator would (`openssl rand -hex 32 > <root>/acme/hmac/1`); `AddEnvelopeSigning()` without any store registration cannot resolve a signer

- [x] Done *(checked by the executor when VERIFY passes — user approval happens at the next 🛑 HUMAN GATE)*

**Scope** *(all projects touched by this slice)*:
- `src/Zyggy.Core/Envelope/ServiceCollectionExtensions.cs` *(create)* — `AddEnvelopeSigning(this IServiceCollection)` (namespace `Zyggy.Core.Envelope`; a second static class with the same simple name in a different namespace is what the spec prescribes).
- `tests/Zyggy.Core.Tests/Envelope/EnvelopeSigningRegistrationTests.cs` *(create)*.
- `tests/Zyggy.Integration/Envelope/SignWithFileStoreTests.cs` *(create)*.

**Seams**: `ISecretStore` — wired (`FileSecretStore`) behind the production DI extension methods; `EnvelopeSigner` resolved from the container exactly as 04's probe and 07's host will resolve it.

**RED** *(write these tests first, run them, confirm they fail before writing production code)*:
- Unit test file: `tests/Zyggy.Core.Tests/Envelope/EnvelopeSigningRegistrationTests.cs` — AC-30: `AddFileSecretStoreThenAddEnvelopeSigning_ResolvesFileStoreAndSigner` (`ISecretStore` is the `FileSecretStore` singleton; `EnvelopeSigner` resolves); `AddEnvelopeSigning_WithoutSecretStore_ResolvingSignerThrowsInvalidOperationException` (the container's usual message; no hidden default store); `AddEnvelopeSigning_ReturnsSameCollection` (fluent).
- Integration test file: `tests/Zyggy.Integration/Envelope/SignWithFileStoreTests.cs` (namespace `Zyggy.Integration.Envelope`; own temp root per test as in Step 7; the golden files are **not** copied to the Integration output — read them from the repository via `Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "golden")` resolved with `Path.GetFullPath`, or add the same `<None Include="../golden/**" LinkBase="golden">` item to `Zyggy.Integration.csproj` — the executor picks the csproj item, mirroring 01's decision for `Core.Tests`, and lists the csproj in the gate summary):
  - Happy path `SignAsync_KeySeededByHandAsHexFile_ProducesGoldenSignature` — Arrange: write `<root>/acme/hmac/1` **by hand in the test** (`Directory.CreateDirectory` + `File.WriteAllText(path, Convert.ToHexStringLower(secret1) + "\n")` — exactly what an operator's `openssl rand -hex 32 > file` produces, no `SetAsync`), build `new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = root).AddEnvelopeSigning().BuildServiceProvider()`, resolve `EnvelopeSigner`, parse `golden/job.md`; Act: `SignAsync(parsed, acme/1, ct)`; Assert: `Signature == "hmac-sha256:" + <job.sig contents>`.
  - `ParseAndVerifyAsync_KeySeededByHand_AcceptsGoldenJob`.
  - Failure path `ParseAndVerifyAsync_KeySeededForOtherTenantOnly_IsUnknownKey` — only `<root>/globex/hmac/1` exists → `UnknownKey` (`Field == "key_id"`): the store never reads outside `<root>/acme/`.
  - Failure path `ParseAndVerifyAsync_CorruptKeyFile_PropagatesInvalidDataException` — `<root>/acme/hmac/1` contains `not-hex` → the exception propagates out of `ParseAndVerifyAsync` (infrastructure fault surfaces; it is not turned into a rejection).
  - Harness: temp directory only.
  - Failing-run command: `dotnet test Zyggy.slnx --filter "FullyQualifiedName~EnvelopeSigningRegistrationTests|FullyQualifiedName~SignWithFileStoreTests"` — fails at compile time (`AddEnvelopeSigning` does not exist).

**GREEN** *(minimal production code across all necessary projects to make RED pass)*:
- `AddEnvelopeSigning`: `services.AddSingleton<EnvelopeSigner>()`; returns `services`. It registers **no** `ISecretStore` (the container's `InvalidOperationException` on resolution is the intended behaviour: no hidden default store). XML doc: "requires an `ISecretStore` registration (`AddFileSecretStore` in P0; OS stores from 09)".
- `tests/Zyggy.Integration/Zyggy.Integration.csproj`: the golden `None` item if that option was chosen.

**Contract impact**: ⚠️ public API `AddEnvelopeSigning` (the composition root every host uses). No new package. Reviewed at the Slice D gate.

**VERIFY** *(after making GREEN changes, run these checks; when all green, mark this step's `Done` checkbox and continue straight to the next step — stop only when the next plan item is a 🛑 HUMAN GATE)*: `dotnet build Zyggy.slnx` + `dotnet test Zyggy.slnx` + code analysis + `dotnet format Zyggy.slnx --verify-no-changes` — all green (exact loop in `CLAUDE.md`). Plus:
- `dotnet test Zyggy.slnx --filter "FullyQualifiedName~EnvelopeSigningRegistrationTests|FullyQualifiedName~SignWithFileStoreTests"` → all pass.
- `dotnet test Zyggy.slnx --filter "Category!=Integration"` still executes zero `Zyggy.Integration` tests.
- AC-31 sweep over `src/`: `Select-String -Path (git ls-files 'src/**/*.cs') -Pattern 'DateTime\.UtcNow|DateTimeOffset\.UtcNow|TimeProvider|Process|HttpClient|ILogger|Default|static '` → read every hit: none may be a clock, a process, HTTP, a logger, a `Default` member or a static mutable field; and `Select-String -Pattern 'Parse\(|VerifyAsync\('` over the same files shows a `TenantId expectedTenant` parameter on every envelope-level declaration.
- AC-32 packages: `Select-String -Path Directory.Packages.props -Pattern 'YamlDotNet" Version="18.1.0"|Ulid" Version="1.4.1"|Microsoft.Extensions.Options" Version="10.0.12"|Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.12"'` → 4 matches.
- The publish + smoke run from 01 still passes locally: `dotnet publish src/Zyggy.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/win-x64; & artifacts/win-x64/zyggy.exe; $LASTEXITCODE` → `0` (Core now carries packages; single-file unaffected — spec Dependencies).
- Cross-OS proof is **not** local: after pushing, the Slice D gate checks the GitHub run (AC-26/27 on `ubuntu-latest`).

**REFACTOR** *(these instructions are for the executor, not the planner)*:
- Analyse the produced code with `@code-analysis` and fix any new issues before proceeding to the next step.
- Check the §9 design rules: no path building outside `BusPaths`, no `Process`/`HttpClient`/`claude`/GitHub/secret-store reference outside its seam implementation, no static mutable state, `JobRunner` never throws to the loop, log once per state change.
- Optional: additional refactorings to improve clarity or align with patterns — only after RED-GREEN-VERIFY is complete for this step.

---

## 🛑 HUMAN GATE — end of Slice D and definition of done for deliverable 03 *(covers Steps 7–8, and the whole deliverable against ROADMAP.md #03 and AC-1..AC-34)*

*Executor: STOP here. Present the results of all covered steps and WAIT for user approval — do not start the next step.*

- [x] Behavioral verification: `FileSecretStoreTests`, `FileSecretStoreRegistrationTests`, `EnvelopeSigningRegistrationTests`, `SignWithFileStoreTests` green locally; the `ci` workflow run for the pushed commit is green on **both** `windows-latest` and `ubuntu-latest` — the Linux job shows the two `IsLinux` permission facts passed and the Windows job the two `IsWindows` DACL facts (AC-26, AC-27, AC-32); the publish + smoke step still passes on both RIDs; `<temp>/zyggy-it/` is left clean; by hand: seed `<root>/acme/hmac/1` with `openssl rand -hex 32` (Git for Windows `openssl.exe`), then a two-line C# script or the integration test proves `SignAsync` → `ParseAndVerifyAsync` round trip.
- [x] Contract review — definition of done from `_plans/ROADMAP.md` #03: unit tests for parse / round trip / sign / verify / tenant mismatch / `key_id` mismatch / `schema` too high, all with tenant `acme`; golden files for `job`, `report`, `context` (plus the two extra cases) with `.md` + `.canonical` + `.sig`; verification rejects tampering of every front-matter key and every body byte; the mandatory-per-type field list is in the spec and applied to §4. And the AC coverage table below: every row has its evidence. `FileSecretStore` contract table (path, default root, content, Linux and Windows permissions, atomic overwrite, "never" list) matches the spec; `src/Zyggy.Core/README.md` has the "Secrets: file store" troubleshooting section and the updated responsibilities table.
- [x] ⚠️ Risk review: key material on disk is 0600 / protected owner-only DACL, proven on both OSes by CI; `SetAsync` writes the tmp file with the final permissions before the rename; no exception message includes key content (`InvalidDataException` names the path only); `FileSecretStore` is `internal sealed`, referenced by nothing but its own file and `AddFileSecretStore`; the packages added in this deliverable are exactly `Ulid` 1.4.1, `YamlDotNet` 18.1.0, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12, `Microsoft.Extensions.Options` 10.0.12 (Core) and `Microsoft.Extensions.DependencyInjection` 10.0.12 (tests only) — all MIT, exact versions, no `System.IO.FileSystem.AccessControl` reference unless the executor reported one; `dotnet list src/Zyggy.Core package --include-transitive` shows no native asset; the AC-31 sweep is clean; `geoffrey` absent from `src/` and `tests/`.
- [x] Roadmap bookkeeping to confirm with the user: `_plans/ROADMAP.md` #03 may be set to `Done` with Spec/Plan links filled; the spec's forwarded findings are noted for their owners (04: rejection reports have no `reason` value in the §9 enum and must raise an Open Question; 04's probe documents seeding `<root>/<tenant>/hmac/1` by hand; 03 leaves `.claude/instructions/public-api.md`'s `IEnvelopeSigner` example untouched unless the user wants it aligned).
- [x] User approved — deliverable 03 complete

---

## Acceptance-criteria coverage

| AC | Covered by | Evidence at the gate |
|----|-----------|----------------------|
| AC-1 golden job typed parse | Step 2 | `Parse_GoldenJob_YieldsTypedJobFields` |
| AC-2 canonical bytes for every case | Step 3 | `Canonicalize_EveryGoldenCase_EqualsCanonicalBytes` (5 rows) |
| AC-3 `SignAsync` equals the `openssl` `.sig`; README records the command | Step 5 (Step 2 for the README) | `SignAsync_EveryGoldenCase_ProducesTheGoldenSignature`; `tests/golden/README.md` |
| AC-4 every case verifies | Step 6 | `ParseAndVerifyAsync_EveryGoldenCase_IsAccepted` |
| AC-5 byte-identical round trip (4 canonical-style cases) | Step 4 | `Serialize_CanonicalStyleGoldenCase_RoundTripsByteIdentically` |
| AC-6 non-canonical → serialise → parse → canonical; unknown keys survive | Step 2 (parse half), Step 4 | `Parse_GoldenJobNoncanonical_KeepsUnknownFieldsAndParsedText`, `Serialize_GoldenJobNoncanonical_ReparsesAndCanonicalisesToGoldenAndKeepsUnknownKeys` |
| AC-7 every top-level key mutated | Step 6 | `ParseAndVerifyAsync_TopLevelKeyMutated_IsRejectedWithExpectedReason` (17 rows) + `…_SigMutated_…` |
| AC-8 every body byte flipped | Step 6 | `ParseAndVerifyAsync_AnyBodyByteFlipped_IsInvalidSignature` |
| AC-9 added key / removed optional / body appended / final newline removed | Step 6 | `ParseAndVerifyAsync_StructuralTamper_IsInvalidSignature` |
| AC-10 foreign tenant, store never called | Step 6 | `…_ForeignTenant_IsTenantMismatchAndStoreNeverCalled` |
| AC-11 `key_id` tenant mismatch, store never called | Step 2 (parse), Step 6 | `Parse_KeyIdOfOtherTenant_…`, `…_KeyIdOfOtherTenant_IsKeyIdTenantMismatchAndStoreNeverCalled` |
| AC-12 unknown key number | Step 6 | `…_UnknownKeyNumber_IsUnknownKey` |
| AC-13 rotation = whatever the store holds | Step 6 | the two `GoldenContextSignedWithKey2` tests |
| AC-14 `schema: 2` → `SchemaUnsupported`, wire string, store not called; non-integers `InvalidField` | Step 1 (wire), Step 2, Step 6 | `EnvelopeWireTests`, `Parse_SchemaTwo_…`, `Parse_SchemaNotPositiveInteger_…`, `…_SchemaTwo_IsSchemaUnsupportedAndStoreNeverCalled` |
| AC-15 `sig` absent / wrong algorithm / wrong length / non-hex | Step 2 | `Parse_SigAbsentOrMalformed_…` |
| AC-16 mandatory field removed per type | Step 2 | `Parse_MandatoryFieldRemoved_ReturnsMissingFieldNamingIt` |
| AC-17 `status`/`reason` interplay | Step 2 | the three `Parse_Report…Reason…` tests |
| AC-18 every Malformed rule with a `Detail` | Step 2 | `Parse_NotAnEnvelope_ReturnsMalformedNamingTheRule` |
| AC-19 CRLF body, `---` body line, no final newline | Step 2, Step 3, Step 4 | `Parse_GoldenContextCrlfBody_…`, golden theories |
| AC-20 BOM accepted and stripped; empty body | Step 2, Step 4 (writer never emits a BOM) | `Parse_BomPrefixed_…`, `Parse_EmptyBody_…`, `Serialize_Output_StartsWithDelimiterAndHasNoBom` |
| AC-21 factory truncation + sign + serialise + parse | Step 4 (factory), Step 5 | `CreateJob_SubSecondOffsetCreated_…`, `SignAsync_UnsignedFactoryEnvelope_…` |
| AC-22 `UnknownKey` details; foreign key id throws | Step 5 | `SignAsync_KeyAbsent_…`, `SignAsync_KeyShorterThan32Bytes_…`, `SignAsync_KeyOfOtherTenant_ThrowsArgumentException` |
| AC-23 label types, no `Default`/ctor/implicit | Step 1 | `LabelTypesTests` incl. the reflection test |
| AC-24 `KeyId` | Step 1 | `KeyIdTests` |
| AC-25 `EnvelopeId` | Step 1 | `EnvelopeIdTests` |
| AC-26 `FileSecretStore` round trip on both OSes | Step 7 | `FileSecretStoreTests` locally + CI matrix |
| AC-27 0600/0700 on Linux, protected DACL on Windows, kept on overwrite | Step 7 | the four `SkipUnless` facts, CI both runners |
| AC-28 non-hex → `InvalidDataException`; `SecretName` traversal impossible | Step 7, Step 1 | `GetAsync_NonHexContent_…`, `SecretNameTests` |
| AC-29 every enum member's wire string | Step 1 | `EnvelopeWireTests` incl. `EveryMember_HasATestRow` |
| AC-30 DI composition; no hidden default store | Step 7, Step 8 | `FileSecretStoreRegistrationTests`, `EnvelopeSigningRegistrationTests` |
| AC-31 no `Default`, no tenant-less overloads, no static state, no clock/process/HTTP/logger, all documented | every step's VERIFY grep, Step 8 sweep | grep output at the Slice D gate; zero-warning build with `GenerateDocumentationFile` |
| AC-32 three commands green on both OSes; four packages pinned; publish still works | Step 8 VERIFY + CI | GitHub run, `Directory.Packages.props` |
| AC-33 no key bytes / hex / digest in any detail or message | Step 5, Step 6, Step 7 | `Rejections_NeverContainKeyOrDigest`, `SignAsync_KeyShorterThan32Bytes_…`, `GetAsync_NonHexContent_ThrowsInvalidDataExceptionNamingPath` |
| AC-34 golden README | Step 2 | review at the Slice A gate |

## Assumptions (taken where the spec is silent; the user was not available for interviews)

1. **Where the `FileSecretStore` tests live.** The spec does not name the project. `FileSecretStore` is 03's only real edge (file system + OS permissions), so its behaviour tests are integration tests in `tests/Zyggy.Integration/Secrets/` (planner rule: every real edge is proven by an integration test); the DI-resolution tests stay unit tests in `tests/Zyggy.Core.Tests/Secrets/`. Both run in CI on both OSes, satisfying AC-26's "CI runs both".
2. **One extra test-only package.** Building a service provider in a test needs the container implementation `Microsoft.Extensions.DependencyInjection` (10.0.12, MIT), which is not among the spec's four packages (those are Core's). It is added to `Directory.Packages.props` and `tests/Directory.Build.props` in Step 7 as a test-only reference (`Zyggy.Integration` already gets it transitively through `Zyggy.Node` → Hosting; `Zyggy.Core.Tests` does not). Flagged ⚠️ at the Slice D gate.
3. **All three golden files per case are authored in Step 2.** A `.md` needs a real `sig`, which is derived from the hand-made `.canonical` through `openssl`; so `.canonical` and `.sig` necessarily exist before `Canonicalize` (Step 3) and `SignAsync` (Step 5) consume them. They remain oracle files: the plan forbids writing code output into them.
4. **`EnvelopeSigner` is created in Step 3 with only its static `Canonicalize`**; the `ISecretStore` constructor and async members are added in Step 5 when the seam exists. Pre-1.0 and no host consumes the type yet (public-api.md deprecation rule does not apply).
5. **`job-noncanonical` freezes the block-sequence rule** (`steps:` holding two mappings) although the spec only says "nested `x_meta` mapping with a sequence". §4 (c)'s "block style otherwise" is normative and deserves a golden line; if the executor finds that YamlDotNet cannot emit the hand-derived form, that is raised at the Slice B gate as a protocol question rather than by dropping the construct silently.
6. **Golden files in `Zyggy.Integration`.** Step 8's integration test reads `golden/job.md`; the spec wires the `None` copy item only for `Core.Tests`. The plan lets the executor add the same item to `Zyggy.Integration.csproj` (mirroring 01) and report it.
7. **The "no `HOME`" construction failure** is tested through `RootDirectory = ""` rather than by blanking the profile directory (not safely possible in-process); the default-root branch is exercised by the no-configure registration test.
8. **`Detail` for duplicate keys names the key** (`duplicate key 'schema'`): key names are protocol vocabulary, not secrets; AC-15's "does not contain the offending value" applies to values.
9. **The `type` row of the AC-7 theory**: changing `type` to another well-formed value on the golden job changes the mandatory field set (`scope`/`in_reply_to`/`status` missing → `MissingField`) before the digest is checked; the plan lets the executor either record that row's expected reason as `MissingField` or exclude `type` from the `InvalidSignature` rows with a dedicated test — either satisfies AC-7's intent (every key is covered by the signature or by an earlier rule).

## Conflicts found between the spec, the founding spec and the repository

- None blocking. The three inherited conflicts (hex `sig`, mandatory-field list, canonical details) are resolved by today's §4 amendments, which the founding spec now contains verbatim (checked lines 154, 164–173, 177, 181).
- `.claude/instructions/tests.md` line 104 still says `key id "geoffrey/test"` (spec Finding 2) — corrected in Step 3's scope.
- `.claude/instructions/public-api.md` shows an `IEnvelopeSigner` example that 03 deliberately does not ship (spec Finding 3) — left as documentation style; listed for the user at the final gate.
- `.claude/skills/integration-testing/SKILL.md` still sketches a `Tenant = "geoffrey"` fixture member and script-based fake-claude — superseded by 01's spec; irrelevant to 03 (no bus fixture used) and not edited here.

## Notes for the executor (things the planner found while reading the repo and spec)

- `Zyggy.Core` compiles with `GenerateDocumentationFile` and warnings as errors: every public type and member needs `<summary>` (and `<param>`/`<returns>`/`<exception>`) from the first commit of each step, or the GREEN build fails on CS1591.
- `IDE0005` is enforced in-build for `Zyggy.Core` only; `dotnet format` enforces it in the test projects — run PROVE before marking a step done.
- xunit.v3: pass `TestContext.Current.CancellationToken` to every async call (xUnit1051); `[Fact(SkipUnless = nameof(IsLinux))]` needs a `public static bool` property on the test class; `[MemberData]` on a static member of another class uses `MemberType = typeof(Golden)`.
- Records: a positional `record TenantId(string Value)` would expose an unvalidated public constructor — use a non-positional record with a private constructor and `Parse`/`TryParse`; override `ToString()` (the synthesized one prints `TenantId { Value = acme }`).
- `Ulid.TryParse` (Cysharp) is more lenient than Crockford base32 with a 48-bit timestamp bound; `EnvelopeId.TryParse` must pre-validate the 26-char uppercase alphabet and the `0`–`7` first character before delegating (AC-25's `8…`, lowercase and `I/L/O/U` rows).
- YamlDotNet 18.1.0: use the event-level `YamlDotNet.Core.Parser`/`Emitter` only; confirm on context7 (or the package XML docs) the exact names of `EmitterSettings.WithNewLine`, `WithIndentedSequences`, the `Scalar`/`MappingStart`/`SequenceStart` constructor overloads (`AnchorName.Empty`, `TagName.Empty`, `isImplicit` flags) and `DocumentStart.Version` before writing code — do not code from memory. Record the settings used in the Slice B gate summary.
- The YAML emitter's scalar style downgrade (plain → single → double) is what §4 (c) froze; where it disagrees with a hand-derived `.canonical`, decide by the §4 text, adjust the emission code if the text is unambiguous, and escalate at the gate if it is not. Never paste code output into a fixture.
- CA1416 (platform compatibility): guard the Windows ACL path with `OperatingSystem.IsWindows()` and mark the private method `[SupportedOSPlatform("windows")]`; guard `UnixCreateMode` with `!OperatingSystem.IsWindows()`.
- `Directory.CreateDirectory(string, UnixFileMode)` may not apply the mode to intermediate directories on every runtime — create `<root>/<tenant>` and `<root>/<tenant>/hmac` level by level so AC-27's two 0700 assertions hold.
- On Windows a file with a protected owner-only DACL is still deletable by its owner; if `Directory.Delete(recursive: true)` in a test's `Dispose` throws, clear the DACL protection first and note it in `tests/Zyggy.Integration/README.md`.
- `Convert.ToHexStringLower` is .NET 9+ (fine on net10.0); `Convert.FromHexString` accepts either case; `HMACSHA256.HashData(ReadOnlySpan<byte>, ReadOnlySpan<byte>)` is one-shot — no `HMACSHA256` instance to dispose; use `Utf8.IsValid` (`System.Text.Unicode`) for body validation.
- `git ls-files --eol tests/golden` must show `i/-text` for every fixture; write fixtures with `[System.IO.File]::WriteAllBytes` (no BOM, exact bytes) and never open them in an editor that normalises line endings.
