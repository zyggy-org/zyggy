# Spec: 03 — Envelope parse, canonicalise, sign and verify (the probe payload)

> Founding-spec sections: §4 Envelope, Signature, Write rules (file name = ULID), One repository per tenant; §8 Secrets table and the P0 file-store paragraph; §9 `Tenancy/`, `Envelope/`, `Secrets/`, Packages (`YamlDotNet`, `Ulid`), design rules, Versioning (`schema: 1`, `schema_unsupported`); §11 `rotate-hmac.md`; §12 Definition of done; §13 decisions "HMAC-signed envelopes", "Tenancy shape from P0", "One bus repository per tenant"; §14 Tenancy, Pluggable edges (`ISecretStore`). Roadmap entry: `_plans/ROADMAP.md` #03 and its hand-off brief. Repo conventions honoured: `tests/golden/README.md`, `.claude/instructions/public-api.md`, `.claude/instructions/tests.md`, `.claude/skills/bus-protocol/SKILL.md`, `_specs/01-solution-scaffolding.md` (style and shipped contracts).
>
> Status: **approved 2026-09-28 — zero Open Questions; planner-ready.** The user answered the three questions raised in the draft on 2026-09-28: Q1 (hex `sig`), Q2 (mandatory-per-type field list) and Q3 (canonical-form paragraph) all **yes**; the user also considered JSON front matter and decided to keep YAML. The resulting §4 amendments are ready to paste under "Proposed §4 amendments" below; only the user applies them to the founding spec. Every one of the ten questions in the hand-off brief is decided in the Decision Table with a rationale; decisions the user may reasonably overturn later are marked *overturnable*.

## Current state (verified against the repo on 2026-09-28)

| Item | State today |
|------|-------------|
| `src/Zyggy.Core` | `Zyggy.Core.csproj` only (no source, no packages); `GenerateDocumentationFile` on, warnings as errors. |
| `tests/Zyggy.Core.Tests` | `Infrastructure/TestStackSmokeTests.cs` only; the golden `None` item (`../golden/**` → `golden/`) is wired. |
| `tests/golden/` | `README.md` only; states `<case>.md` + `<case>.canonical` + `<case>.sig` ("expected HMAC (hex)"), `-text`, key id and secret "fixed by `_specs/03-envelope-signing.md`". |
| `Directory.Packages.props` | Test stack + Hosting 10.0.12 + MinVer; no YamlDotNet, no Ulid, no Options/DI abstractions. |
| `.claude/instructions/tests.md` | Golden example still names key id `geoffrey/test` (line 104) — contradicts §9 (`geoffrey` only in configuration/fixtures) and the brief (tenant `acme` everywhere); see Findings forwarded. |
| Conflicts inherited (all resolved 2026-09-28 by the user) | §4 `sig: hmac-sha256:BASE64...` vs README "hex" → hex; mandatory-per-type field list delegated to this spec by O1 → adopted as written; §11 lists `num_turns` as a report field, §4 does not → added to §4. Amendment texts in "Proposed §4 amendments". |

## User Story

**As** Central, a Node, or the work node (any machine reading its own `tenants/<org>/nodes/<me>/jobs/`),
**I want** every envelope file to be parsed into a typed model and verified — tenant first, then schema, then fields, then the HMAC over a canonical form that never drifts —
**So that** an envelope from another tenant, a future schema, a missing or wrong key, or a single tampered byte is refused before any model call (§8 "rejected before any model call"), and 04 can carry exactly this payload over transport A through the corporate proxy (P0 gate).

**As** a sender (Central's `submit`, a node writing a report or context envelope, the P0 probe),
**I want** to build an envelope from typed values, sign it with my tenant's current key and serialise it in the canonical style,
**So that** what I write is byte-for-byte what every receiver re-canonicalises, and a human can recompute the signature with `openssl`.

---

## Acceptance Criteria

Test key material (fixed by this spec, used by every golden case and unit test; obviously synthetic, never a real credential):

| Key id | Secret (32 bytes = UTF-8 of the string) | Hex form (as stored by `FileSecretStore`) |
|--------|------------------------------------------|-------------------------------------------|
| `acme/1` | `zyggy-golden-test-key-acme-00001` | `Convert.ToHexStringLower` of those bytes |
| `acme/2` | `zyggy-golden-test-key-acme-00002` | idem |

Tenant `acme` in every test, golden case and fixture; `geoffrey` appears nowhere under `src/` or `tests/`.

| # | Given | When | Then |
|---|-------|------|------|
| AC-1 | `golden/job.md` (the §4 example re-homed to tenant `acme`, `from: central`, `to: home-laptop`, `key_id: acme/1`) | `EnvelopeParser.Parse(bytes, TenantId "acme")` | Accepted; `Envelope.Type == Job`, `Job.Project == "calizr"`, `Job.Agent == "env-debugger"`, `Job.Worktree == true`, `Job.AllowedTools` = the five strings incl. `Bash(dotnet *)`, `Job.ReportBack` = `[summary, diff, files_changed]`, `Job.TimeoutMinutes == 30`, `Job.Attempt == 1`, `Header.Priority == Normal`, `Header.Created == 2026-09-27T14:05:00Z`, `Job.Deadline == 2026-09-29T18:00:00Z`, `KeyId == acme/1`, `Signature` = the file's `sig` value, `Body` = the two body lines byte-exact. |
| AC-2 | Every `golden/<case>.md` (five cases in the Contracts section) enumerated by `[Theory]` + `[MemberData]` | `EnvelopeSigner.Canonicalize(parsed)` | Bytes equal `golden/<case>.canonical` exactly (compared as byte arrays, not strings). |
| AC-3 | Every golden case, an `InMemorySecretStore` holding `acme/1` and `acme/2` | `SignAsync(parsed, <the case's key_id>)` (re-signing replaces the existing `sig`) | The resulting `sig` equals the file's own `sig` value and equals `hmac-sha256:` + the contents of `golden/<case>.sig`; the `.sig` file holds 64 lowercase hex characters and no newline; `tests/golden/README.md` records the independent command that produced it (`openssl dgst -sha256 -hmac '<secret string>' <case>.canonical`), i.e. no `.sig` was generated by the code under test. |
| AC-4 | Every golden case, same store | `ParseAndVerifyAsync(bytes, "acme")` | Accepted (`IsAccepted == true`, `Rejection == null`). |
| AC-5 | The canonical-style golden cases (`job`, `report`, `context`, `context-crlf-body`) | `EnvelopeWriter.Serialize(parsed)` | Bytes equal `golden/<case>.md` exactly (round trip is byte-identical, including the unknown fields and the CRLF body). |
| AC-6 | `golden/job-noncanonical.md` (hand-written: unsorted keys, CRLF front matter, block sequence, quoted booleans, unknown scalar + nested mapping + sequence, a scalar needing single quotes, one needing double quotes, an empty string, `null`) | `Serialize(Parse(md))` then `Parse` again then `Canonicalize` | Equals `golden/job-noncanonical.canonical`; the second parse verifies `Accepted`; the serialised bytes contain every unknown key (`x_experimental`, `x_meta`) — unknown fields survive the round trip (§4). |
| AC-7 | `golden/job.md`; for each top-level front-matter key except `sig` a mutated copy whose value is changed to a different **well-formed** value (`[Theory]` over the key names) | `ParseAndVerifyAsync` | `InvalidSignature` for every key, except: `tenant` → `TenantMismatch`; `key_id` with a different tenant part → `KeyIdTenantMismatch`, with a different number → `UnknownKey`; `schema` → `SchemaUnsupported` (only value 1 is well-formed and supported). Mutating `sig` itself → `InvalidSignature`. |
| AC-8 | `golden/job.md`; for every byte offset of the body a copy with that byte XOR-ed (`[MemberData]` over offsets) | `ParseAndVerifyAsync` | `InvalidSignature` for every offset. |
| AC-9 | `golden/job.md` with (a) one unknown key `x_added: 1` appended, (b) the optional key `agent` removed, (c) a body byte appended, (d) the body's final newline removed | `ParseAndVerifyAsync` | `InvalidSignature` in all four cases (unknown fields and body length are covered by the signature). |
| AC-10 | A valid envelope with `tenant: globex`, `key_id: globex/1`, an `ISecretStore` substitute | `ParseAndVerifyAsync(bytes, "acme")` | `TenantMismatch`; the substitute received **no** call (`DidNotReceiveWithAnyArgs().GetAsync(...)`). |
| AC-11 | `tenant: acme` but `key_id: globex/1` | `ParseAndVerifyAsync(bytes, "acme")` | `KeyIdTenantMismatch`, `Field == "key_id"`; the store received no call. |
| AC-12 | A valid envelope signed with `acme/3`; store holds `acme/1`, `acme/2` | `ParseAndVerifyAsync` | `UnknownKey`, `Field == "key_id"`. |
| AC-13 | `golden/context.md` (signed with `acme/2`) | Verified with a store holding both keys / holding only `acme/1` | Accepted / `UnknownKey` — rotation acceptance is "whatever the store holds", no date logic (§4, §11). |
| AC-14 | `schema: 2` (otherwise valid) | `ParseAndVerifyAsync` | `SchemaUnsupported`; `EnvelopeWire.ToWire(EnvelopeRejectionReason.SchemaUnsupported) == "schema_unsupported"`; store not called. `schema: 0`, `schema: -1`, `schema: 1.1`, `schema: one` → `InvalidField` (`Field == "schema"`). |
| AC-15 | `sig` absent / `sig: hmac-sha1:…` / `sig: hmac-sha256:abc` (wrong length) / non-hex digest | `Parse` | `MissingSignature` / `InvalidSignature` / `InvalidSignature` / `InvalidSignature`, each with `Field == "sig"` and a `Detail` that does not contain the offending value. |
| AC-16 | For each type, each mandatory field removed one at a time (`[Theory]`: common `schema, id, tenant, type, from, to, created, key_id`; job `project`; report `in_reply_to, status`; context `scope`) | `Parse` | `MissingField` with `Field` = that key. |
| AC-17 | Report with `status: done` + `reason: timeout` / `status: failed` without `reason` / `status: failed` + `reason: "Not A Token"` | `Parse` | `InvalidField` (`reason`) / `MissingField` (`reason`) / `InvalidField` (`reason`). |
| AC-18 | Files that: lack the opening `---` line; lack the closing `---`; have YAML the parser cannot read; use an anchor, alias, explicit tag, merge key, a `%YAML` directive or a second document; whose root is a sequence or scalar; repeat a key; contain invalid UTF-8 in the front matter or the body | `Parse` | `Malformed` with a `Detail` naming which rule failed. |
| AC-19 | `golden/context-crlf-body.md`: CRLF body, a body line that is exactly `---`, no trailing newline | `Parse` | `Body` is byte-identical to the file bytes after the closing delimiter line; canonical (AC-2) and round trip (AC-5) hold; the body's `---` line did not end the front matter. |
| AC-20 | A valid envelope file with a UTF-8 BOM; an envelope whose body is empty | `Parse` / `ParseAndVerifyAsync` | Both accepted; `Body.Length == 0` for the second; the BOM is not part of the body and the writer never emits one. |
| AC-21 | An unsigned `Envelope.CreateJob(header, fields, body)` with `Header.Created` carrying sub-second precision and a `+02:00` offset | `SignAsync(env, acme/1)` then `Serialize` then `Parse` | `created` is emitted as `yyyy-MM-ddTHH:mm:ssZ` (UTC, whole seconds) and the parsed `Header.Created` equals the created envelope's `Header.Created` (the factory already truncated and converted); `KeyId == acme/1`; `Signature` present; the file verifies. |
| AC-22 | `SignAsync` with a key absent from the store / with a stored key shorter than 32 bytes | — | `UnknownKey` (`Detail` says "absent" or "shorter than 32 bytes"; never the key bytes). `SignAsync(env, globex/1)` on an `acme` envelope → `ArgumentException` (caller bug, not a protocol outcome). |
| AC-23 | `TenantId`, `UserId`, `MachineName` (`[Theory]`) | `TryParse` | `acme`, `a`, `home-laptop`, `x1-2y`, 63 chars → true; `""`, `Acme`, `-acme`, `acme-`, `ac me`, `acme/1`, `a..b`, 64 chars, `..`, `geoffrey/` → false; `Parse` of an invalid value throws `FormatException`; the types expose no `Default`, no parameterless constructor and no implicit conversion from `string` (compile-time: a reflection test asserts the absence of a public parameterless constructor and of a `Default` member). |
| AC-24 | `KeyId` | `TryParse` / `ToString` / `SecretName` | `acme/1` → `(acme, 1)` → `"acme/1"` → `SecretName "hmac/1"`; `acme/0`, `acme/01`, `acme`, `acme/1/2`, `Acme/1`, `acme/-1` → false. |
| AC-25 | `EnvelopeId` | `TryParse` / `New(DateTimeOffset)` | `01J8Y3N7Q2X9Z4A5B6C7D8E9F0` → true; 25 or 27 chars, lowercase, `I`/`L`/`O`/`U`, first char `8` → false; `New(2026-09-27T14:05:00.123Z)` yields 26 uppercase Crockford chars whose `Timestamp` equals the given instant at millisecond precision; two calls with the same instant differ. |
| AC-26 | `FileSecretStore` with `RootDirectory` = a temp dir, on Windows **and** Linux (CI runs both) | `SetAsync(acme, hmac/1, bytes)` → `GetAsync` → `RemoveAsync` → `GetAsync` | Returns the same 32 bytes; the file is `<root>/acme/hmac/1` containing the lowercase hex of the bytes and a trailing `\n`; after `RemoveAsync` (true) the second `GetAsync` returns `null`; `RemoveAsync` again → false. `GetAsync` of an absent name → `null`, no exception. |
| AC-27 | The file written by AC-26 | Inspected on Linux (`[Fact(SkipUnless = IsLinux)]`) / on Windows (`SkipUnless = IsWindows`) | Linux: `File.GetUnixFileMode` = `UserRead \| UserWrite` (0600), `<root>/acme` and `<root>/acme/hmac` = 0700. Windows: the file's DACL is protected (`AreAccessRulesProtected == true`) and holds exactly one access rule, `Allow FullControl` for the current user's SID. The same assertions hold after `SetAsync` overwrote an existing file. |
| AC-28 | `FileSecretStore` | `GetAsync` on a file containing non-hex text | Throws `InvalidDataException` naming the path (infrastructure fault, not a protocol outcome; never swallowed). `SecretName.TryParse("../x")`, `"/x"`, `"x/"`, `"a//b"`, `"Hmac/1"` → false, so no traversal outside `<root>/<tenant>/` is expressible. |
| AC-29 | Every member of `EnvelopeType`, `EnvelopePriority`, `ReportStatus`, `ContextScopeKind`, `EnvelopeRejectionReason` | `EnvelopeWire.ToWire` / `TryFromWire` | Exactly the snake_case strings in the Contracts tables; every member has a test row; an unknown string → `TryFromWire` false. |
| AC-30 | `new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = tmp).AddEnvelopeSigning()` | Build provider | `ISecretStore` resolves to `FileSecretStore` (singleton), `EnvelopeSigner` resolves; `AddEnvelopeSigning()` without any `ISecretStore` registration → resolving `EnvelopeSigner` throws the DI's usual `InvalidOperationException` (no hidden default store). |
| AC-31 | `src/` after this deliverable | Reviewed / grepped | No `Default` member on any tenancy type, no overload of `Parse`/`VerifyAsync`/`ParseAndVerifyAsync` without `TenantId expectedTenant`, no `static` mutable field, no `DateTime.UtcNow`/`TimeProvider.System`/`Process`/`HttpClient`/`ILogger` in `Zyggy.Core` (03 has no clock, no I/O beyond `FileSecretStore`, no logging); every public type and member documented (build passes with `GenerateDocumentationFile`). |
| AC-32 | Clean clone, Windows and Linux, CI | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on `Zyggy.slnx` | All green; `Directory.Packages.props` carries `YamlDotNet` 18.1.0, `Ulid` 1.4.1, `Microsoft.Extensions.Options` 10.0.12, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12; the `win-x64`/`linux-x64` single-file publish and smoke run from 01 still pass (Core now carries packages). |
| AC-33 | Any `EnvelopeRejection.Detail`, any exception message from `EnvelopeSigner` or `FileSecretStore` | Inspected in tests | Never contains key bytes, the hex of a key, or the `sig` digest (asserted for `InvalidSignature`, `UnknownKey`, `InvalidDataException`). |
| AC-34 | `tests/golden/README.md` | Reviewed | States: `.sig` = 64 lowercase hex chars, no newline, produced by the recorded `openssl` command; the two key ids and secrets above; the five cases and which ACs each serves; the rule that a new case is added in RED. |

---

## Decision Table

| Founding-spec / brief item | Verdict | Target type / library | Justification |
|----------------------------|---------|-----------------------|---------------|
| §9 `Tenancy/`: `TenantId`, `UserId`, `Principal` | Keep | `Zyggy.Core.Tenancy.TenantId`, `UserId`, `Principal` (sealed records, `Parse`/`TryParse`, validating constructor, no `Default`) | §13/§14 tenancy shape; reference records (not structs) so `default(T)` cannot produce an unvalidated instance and NRT catches a missing tenant at compile time. |
| Machine names in `from`/`to` (§4 `nodes/<machine>/`) | Reshape (*overturnable*) | `Zyggy.Core.Tenancy.MachineName`, same label syntax | `From`/`To` typed as a validated label now avoids a public-API change in 04 when `BusPaths` needs the same type for `nodes/<machine>/`; 10 lines, no new capability. |
| §4 envelope format: YAML front matter + Markdown body (JSON considered) | Keep — **decided 2026-09-28 by the user** | YAML front matter exactly as §4 shows; no JSON variant | Readable on GitHub, the same convention as Claude Code agent and memory files, and the body is signed byte-for-byte without any escaping. |
| §4 envelope model; §9 `Envelope`, `EnvelopeType` | Reshape | One immutable `Envelope` class holding `FrontMatter` (parsed tree, authoritative for signing) + `Body` bytes + typed views `Header`, `Job?`/`Report?`/`Context?` (exactly one non-null); `EnvelopeType` enum | Signing must be computed from the **parsed scalar text**, never from re-formatted typed values (`attempt: 01`, `cost_usd: 0.10`, `True` would otherwise fail verification on untampered files). Typed views are derived once; no `with` on typed fields, so tree and view cannot disagree. |
| §4 "unknown fields preserved on round trip" | Keep | `FrontMatterNode` tree (`FrontMatterScalar` / `FrontMatterSequence` / `FrontMatterMapping`) exposed as `Envelope.FrontMatter` | The tree is the only representation the parser, canonicaliser and writer share; unknown keys are ordinary entries. Keeps `YamlDotNet` types out of the public API. |
| §4 YAML parsing/emission; §9 package `YamlDotNet` | Library | `YamlDotNet` 18.1.0 — `YamlDotNet.Core` `Parser` (events) and `Emitter` + `EmitterSettings.WithNewLine("\n")`; **not** the reflection `Serializer`/`Deserializer` | A generic key tree needs no object graph; scalar style per node is only controllable at event level; the reflection serializer has no sorted-keys option (checked `SerializerBuilder.cs` on 2026-09-28). Removes a YAML tokenizer/emitter Zyggy would otherwise own. |
| §4 canonical form (`sig` removed, keys sorted, `\n`, `\n---\n`, body byte-for-byte); §9 `EnvelopeSigner.Canonicalize` single source of truth | Keep (normative details fixed below) — **clarifications decided 2026-09-28 by the user as a §4 paragraph** | `EnvelopeSigner.Canonicalize(Envelope) : byte[]` (static, pure); the writer's `Serialize` calls the same internal emitter | The shared contract of every machine; golden files freeze it; the §4 paragraph (amendment (c)) makes the bytes specifiable by an implementer in any language. |
| §4 HMAC-SHA256 signature | Library (BCL) | `System.Security.Cryptography.HMACSHA256.HashData` + `CryptographicOperations.FixedTimeEquals` | Nothing to own. |
| §4 `sig` value format `hmac-sha256:BASE64...` | Reshape — **decided 2026-09-28 by the user (ex-Q1)** | `hmac-sha256:` + 64 lowercase hex; `.sig` golden = the 64 hex chars; §4 amended per amendment (a) | Hex is a plain YAML scalar with no quoting risk (`+`/`/`/`=` avoided), `openssl dgst` prints it directly, `Convert.ToHexStringLower`/`FromHexString` are BCL, and `tests/golden/README.md` already says hex (README unchanged). |
| §4 receiver rejection rules (missing/unknown/invalid signature, tenant ≠ own, `key_id` tenant ≠ `tenant`, tenant check before key lookup); §9 result types, "tenant checks in Core" | Keep | `EnvelopeParser.Parse(bytes, expectedTenant)`, `EnvelopeSigner.VerifyAsync(envelope, expectedTenant, ct)`, `ParseAndVerifyAsync(bytes, expectedTenant, ct)` → `EnvelopeResult` with closed `EnvelopeRejectionReason` | §8 "rejected before any model call"; §9 "no parameterless overloads"; the combined method fixes the §4 order (tenant → schema → fields → key) in Core so callers cannot reorder it. |
| §4 `to` ≠ own machine → reject | Defer to 04 | `Envelope.Header.To` (typed) is all 03 provides | Central's sweep, `dream ingest`, `verify` and `job_status` legitimately read envelopes addressed to other machines, so the check is per caller, not a property of the envelope; the own-machine identity arrives with `node.json` binding (04/07). |
| §4 tenant ≠ `<org>` of the path | Defer to 04 | `expectedTenant` parameter is the seam | The path is `BusPaths`' knowledge (04); 04 passes the tenant of the subtree it read from. |
| §9 versioning: `schema: 1`, reject higher major → `schema_unsupported` | Keep | `Envelope.Schema` (int), `EnvelopeRejectionReason.SchemaUnsupported` with wire string `schema_unsupported` | 04/06 map it without re-parsing (brief). `schema` is a single integer; non-integers are `InvalidField` (normative in §4 per amendment (c), decided 2026-09-28). |
| O1 mandatory-per-type field list | Keep (list fixed below) — **decided 2026-09-28 by the user (ex-Q2)** | Validation in `EnvelopeParser` per `EnvelopeType`; §4 amended per amendment (b) | Delegated to this spec by O1; adopted as written, including `num_turns`, `reason` iff `status` ≠ `done`, optional `priority`/`attempt`/`worktree` with defaults. |
| §4 report fields incl. `reason` (closed enum, §9) and §11 `num_turns` | Reshape | `ReportFields.Reason : string?` validated as a snake_case token; `NumTurns : int?` added | O12/brief: 03 must not define the `JobFailureReason` enum (06 does) nor a competing one; `string?` lets 04 write rejection reports and 06 swap in the enum (pre-1.0 rename rule). `num_turns` is in §11's report list but was missing from §4 — added by amendment (b), decided 2026-09-28. |
| §4 `id` = ULID, file name `<ulid>.md`; §9 package `Ulid` | Library + thin wrapper | `Ulid` 1.4.1 behind `EnvelopeId` (`TryParse`, `New(DateTimeOffset)`, `Timestamp`, `ToString()` 26 uppercase Crockford chars) | Generation (48-bit ms + 80-bit CSPRNG) and Crockford base32 validation are ~80 error-prone lines to own; the wrapper keeps the package out of the public API and gives 04's `BusPaths` one validated id type. `New` takes the instant explicitly (no ambient clock). File-name composition itself is `BusPaths` (04). |
| Brief Q9: `TimeProvider` for `created`/ULID | Reshape | No `TimeProvider` inside 03: `EnvelopeHeader.Created` and `EnvelopeId.New(DateTimeOffset)` take the instant; callers pass `timeProvider.GetUtcNow()` | 03 has no service that needs a clock; injecting one into pure value factories would be ambient-state-by-another-name. Callers (04's probe, 06, Hub) own the `TimeProvider`. |
| §4 timestamps (`created`, `deadline`, `started`, `finished`) | Keep | Emitted `yyyy-MM-ddTHH:mm:ssZ` (UTC, whole seconds); parsed as RFC 3339 `date-time` (`T`, seconds mandatory, optional fraction, `Z` or `±hh:mm`) with `DateTimeOffset.TryParseExact` + invariant culture, normalised to UTC | Second precision as §4 shows; liberal-but-bounded parsing so hand-written probe files work; canonical bytes are unaffected (text is signed). |
| §8 Secrets: `ISecretStore` keyed by tenant, names `zyggy/<tenant>/hmac/<n>`; §14 "Azure Key Vault later" | Keep (seam), Reshape (API) | `ISecretStore { GetAsync, SetAsync, RemoveAsync }(TenantId, SecretName, …)`; `SecretName` validated relative name; store composes `zyggy/<tenant>/<name>`; `KeyId.SecretName == hmac/<n>` | Brief asked for `TenantId + key number`; §8 lists four secret kinds under the same prefix (HMAC, PAT, OAuth, bot token), so the seam takes a validated relative name and stays tenant-scoped with no raw path. **Async** because §14 names a remote store (Key Vault) as a pluggable edge; turning a sync seam async later touches every host. |
| §8/O20 P0 file-based store (0600 / user-profile ACL) | Keep | `FileSecretStore` (`internal sealed`), `FileSecretStoreOptions.RootDirectory`, `AddFileSecretStore(...)`; Linux `FileStreamOptions.UnixCreateMode` 0600 + `Directory.CreateDirectory(path, 0700)`; Windows `FileSystemAclExtensions.Create(FileInfo, …, FileSecurity)` with a protected owner-only DACL | Both are BCL (`UnixCreateMode` since .NET 7; `System.IO.FileSystem.AccessControl` is part of the shared framework since .NET 6 — learn.microsoft.com "older framework versions dropped", checked 2026-09-28), no package. Key file content is hex so a human can seed the three P0 machines with `openssl rand -hex 32`. |
| Test double for the seam | Keep | `InMemorySecretStore` (public sealed, in `Zyggy.Core.Secrets`) | tests.md: integration tests swap "the secret store (in-memory)"; must be public for other test projects; instance state only. |
| OS secret stores, `zyggy secret`, `signWith`, rotation execution (§8, §11) | Defer to 09/07 | — | Brief. 03 takes the signing `KeyId` as an explicit `SignAsync` parameter; "accept both keys for 7 days" is simply "verify with whatever `key_id` the store holds" — no dates in code. |
| `IEnvelopeSigner` interface (public-api.md doc example) | Defer (not created) | `EnvelopeSigner` is a public sealed class registered by `AddEnvelopeSigning()` | public-api.md forbids a sixth single-implementation abstraction; consumers' tests use the real signer with `InMemorySecretStore` (pure, fast). The doc example is illustrative. |
| §9 DI, `IOptions<T>`, `Add<Feature>` extension methods | Library | `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12, `Microsoft.Extensions.Options` 10.0.12 | First packages in `Zyggy.Core`; both already flow transitively through Hosting in `Zyggy.Node`; MIT. |
| §9 golden-file tests | Keep | `tests/golden/{job,report,context,job-noncanonical,context-crlf-body}.{md,canonical,sig}`; `[Theory]` + `[MemberData]` enumerating `golden/*.md` | Brief requires three; the two extra cases are the drift and tamper proof (unknown fields, quoting ladder, CRLF/`---` body) and cost only fixture bytes. |
| §11 logging (`tenant`, `machine`, `phase`) | Defer to 04 | none in 03 | 03 is a pure library; it returns reasons, the caller logs once per rejection (§11). |
| §12 runbook entry | Reshape | Troubleshooting section in `src/Zyggy.Core/README.md` for `FileSecretStore` faults | Brief: 03 introduces no runtime failure mode; the operational runbook directory does not exist until 10 (same choice as spec 01). |
| `BusPaths`, `BusRepository`, `GitClient`, poller, `IBusProvider`, `jobs/rejected/` move, `BusRepoFixture` seeding | Defer to 04 | — | Brief. |
| `JobRunner`, `IModelRunner`, `JobFailureReason` enum, `PromptTemplate` | Defer to 06 | — | Brief. |
| CLI verbs, Hub tools, `PROTOCOL.md`, `policy.yaml` signing, registry files (O24) | Defer to 08/11/15/18/14 | — | Brief. |

---

## Contracts

### Types and interfaces

All types public unless stated; every public member XML-documented citing the §4 rule it implements (public-api.md). No static mutable state anywhere.

#### `Zyggy.Core.Tenancy`

| Type | Shape | Rules |
|------|-------|-------|
| `TenantId` | `sealed record`; `string Value`; `static Parse(string)` (throws `FormatException`), `static bool TryParse(string?, [NotNullWhen(true)] out TenantId?)`; `ToString() == Value` | Syntax (shared by the three labels): `^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$` — lowercase ASCII letters, digits, inner hyphens, 1–63 chars. Rationale: safe as a path segment on case-insensitive and case-sensitive file systems, in secret names, in `key_id` (`/` excluded), as a plain YAML scalar and in commit messages. No `Default`, no parameterless constructor, no implicit conversion. |
| `UserId` | same shape | same syntax (memory paths `memory/<tenant>/<user>/`, §7). |
| `MachineName` | same shape | same syntax (`nodes/<machine>/`, §4). |
| `Principal` | `sealed record Principal(TenantId Tenant, UserId User)` | Used by 11+ (Hub); defined now because §9 lists it with the tenancy types and it is three lines. |

#### `Zyggy.Core.Envelope` — identifiers and enums

| Type | Shape | Wire form / rules |
|------|-------|-------------------|
| `EnvelopeId` | `sealed record`; `string Value`; `DateTimeOffset Timestamp`; `static New(DateTimeOffset instant)`; `Parse`/`TryParse`; `ToString() == Value` | 26 uppercase Crockford base32 chars (`0-9A-HJKMNP-TV-Z`), first char `0`–`7`; implemented over `Ulid` (internal). `Timestamp` is the 48-bit ms part in UTC. |
| `KeyId` | `sealed record KeyId(TenantId Tenant, int Number)`; `Parse`/`TryParse`; `ToString()`; `SecretName SecretName` | Wire `<tenant>/<n>`, `n` matches `^[1-9][0-9]*$` and fits `int`. `SecretName` = `hmac/<n>`. |
| `EnvelopeType` | enum `Job, Report, Context` | `job`, `report`, `context` |
| `EnvelopePriority` | enum `Low, Normal, High` | `low`, `normal`, `high` |
| `ReportStatus` | enum `Done, Failed, Timeout, Rejected` | `done`, `failed`, `timeout`, `rejected` |
| `ContextScopeKind` / `ContextScope` | enum `Project, Machine, General`; `sealed record ContextScope(ContextScopeKind Kind, string? ProjectName)` with `Parse`/`TryParse`/`ToString()` | `project:<name>` (name non-empty, no whitespace, no `:`), `machine`, `general` |
| `EnvelopeRejectionReason` | enum `Malformed, TenantMismatch, SchemaUnsupported, KeyIdTenantMismatch, MissingField, InvalidField, MissingSignature, UnknownKey, InvalidSignature` (closed; adding a member is a protocol change) | `malformed`, `tenant_mismatch`, `schema_unsupported`, `key_id_tenant_mismatch`, `missing_field`, `invalid_field`, `missing_signature`, `unknown_key`, `invalid_signature` |
| `EnvelopeWire` | `static class`; `ToWire(...)` / `TryFromWire(...)` for each enum above | The single serialisation helper (public-api.md "closed enums and wire strings"); one test row per member. |

#### `Zyggy.Core.Envelope` — model

```csharp
public abstract record FrontMatterNode;                                                        // closed hierarchy
public sealed record FrontMatterScalar(string Value) : FrontMatterNode;                        // scalar text exactly as parsed
public sealed record FrontMatterSequence(IReadOnlyList<FrontMatterNode> Items) : FrontMatterNode;
public sealed record FrontMatterMapping(IReadOnlyDictionary<string, FrontMatterNode> Entries) : FrontMatterNode; // enumeration order = ordinal key order (planner: ImmutableSortedDictionary + StringComparer.Ordinal)

public sealed record EnvelopeHeader(TenantId Tenant, EnvelopeId Id, MachineName From, MachineName To, DateTimeOffset Created,
                                    EnvelopePriority Priority = EnvelopePriority.Normal, EnvelopeId? InReplyTo = null);
public sealed record JobFields(string Project, string? Agent = null, bool Worktree = false,
                               IReadOnlyList<string>? AllowedTools = null, IReadOnlyList<string>? ReportBack = null,
                               int? TimeoutMinutes = null, DateTimeOffset? Deadline = null, int Attempt = 1);
public sealed record ReportFields(ReportStatus Status, string? Reason = null, DateTimeOffset? Started = null, DateTimeOffset? Finished = null,
                                  int? DurationSeconds = null, decimal? CostUsd = null, int? InputTokens = null, int? OutputTokens = null,
                                  int? NumTurns = null, string? Model = null, IReadOnlyList<string>? FilesChanged = null, string? DiffRef = null);
public sealed record ContextFields(ContextScope Scope);

public sealed class Envelope                         // immutable; equality by reference (documented)
{
    public int Schema { get; }                       // 1 for every envelope this version creates
    public EnvelopeType Type { get; }
    public EnvelopeHeader Header { get; }
    public JobFields? Job { get; }                   // exactly one of Job / Report / Context is non-null, matching Type
    public ReportFields? Report { get; }
    public ContextFields? Context { get; }
    public KeyId? KeyId { get; }                     // null until signed
    public string? Signature { get; }                // the full `sig` value (`hmac-sha256:<hex>`); null until signed
    public FrontMatterMapping FrontMatter { get; }   // every field incl. unknown ones, key_id and sig; authoritative for signing
    public ReadOnlyMemory<byte> Body { get; }        // bytes after the closing delimiter line, untouched
    public string BodyText { get; }                  // UTF-8 decode of Body (valid by construction)

    public static Envelope CreateJob(EnvelopeHeader header, JobFields job, ReadOnlyMemory<byte> body);
    public static Envelope CreateReport(EnvelopeHeader header, ReportFields report, ReadOnlyMemory<byte> body);
    public static Envelope CreateContext(EnvelopeHeader header, ContextFields context, ReadOnlyMemory<byte> body);
}
```

- Factories validate the same rules as the parser (throwing `ArgumentException` — a caller bug), truncate every timestamp to whole seconds in UTC, build `FrontMatter` in canonical scalar text (rules below), omit optional fields whose value is null, always emit `schema: 1`, `priority`, and for jobs `attempt` and `worktree`; empty lists are emitted as `[]`. Body must be valid UTF-8 (`ArgumentException` otherwise).
- The parser builds `Header`/`Job`/`Report`/`Context` **from** `FrontMatter`; the factories build `FrontMatter` **from** the records. There is no other constructor and no mutation, so the two never disagree.
- Modifying a field of an existing envelope (Central's sweep `attempt: n+1`, later deliverable) will be a `With(key, FrontMatterNode)` that returns an **unsigned** envelope; not in 03.

#### `Zyggy.Core.Envelope` — parse, sign, verify, write

```csharp
public sealed record EnvelopeRejection(EnvelopeRejectionReason Reason, string? Field, string Detail);   // Detail: short, never a key or digest

public sealed class EnvelopeResult                    // one shape for parse, verify, sign
{
    public Envelope? Envelope { get; }
    public EnvelopeRejection? Rejection { get; }
    [MemberNotNullWhen(true, nameof(Envelope))] [MemberNotNullWhen(false, nameof(Rejection))]
    public bool IsAccepted { get; }
}

public static class EnvelopeParser
{
    public static EnvelopeResult Parse(ReadOnlyMemory<byte> file, TenantId expectedTenant);          // no overload without expectedTenant; no string overload
}

public static class EnvelopeWriter
{
    public static byte[] Serialize(Envelope envelope);   // requires Signature != null, else InvalidOperationException (never write unsigned)
}

public sealed class EnvelopeSigner(ISecretStore secrets)  // registered by AddEnvelopeSigning(); no interface
{
    public static byte[] Canonicalize(Envelope envelope);                                                    // pure; the single source of truth (§9)
    public Task<EnvelopeResult> SignAsync(Envelope envelope, KeyId signWith, CancellationToken ct);         // sets key_id + sig (replacing any existing); UnknownKey when absent/short
    public Task<EnvelopeResult> VerifyAsync(Envelope envelope, TenantId expectedTenant, CancellationToken ct);
    public Task<EnvelopeResult> ParseAndVerifyAsync(ReadOnlyMemory<byte> file, TenantId expectedTenant, CancellationToken ct); // Parse then VerifyAsync
}
```

Check order (first failure wins; `Parse` never touches `ISecretStore`):

1. `Malformed` — BOM handling, delimiters, UTF-8, YAML subset (below), root mapping, duplicate keys.
2. `tenant` — missing → `MissingField`; not a valid `TenantId` → `InvalidField`; ≠ `expectedTenant` → `TenantMismatch`.
3. `schema` — missing → `MissingField`; not a positive integer → `InvalidField`; `> 1` → `SchemaUnsupported`.
4. `type` — missing → `MissingField`; not `job|report|context` → `InvalidField`.
5. `key_id` — missing → `MissingField`; not `<tenant>/<n>` → `InvalidField`; tenant part ≠ `tenant` → `KeyIdTenantMismatch`.
6. Common then per-type fields (table below) — `MissingField` / `InvalidField` with `Field` = key.
7. `sig` — missing/null → `MissingSignature`; not `hmac-sha256:` + 64 hex chars (either case accepted) → `InvalidSignature`.

`VerifyAsync`: (a) `envelope.Header.Tenant != expectedTenant` → `TenantMismatch` (no key lookup); (b) `Signature == null` → `MissingSignature`; (c) `secrets.GetAsync(envelope.Header.Tenant, envelope.KeyId.SecretName)` — `null` or shorter than 32 bytes → `UnknownKey`; (d) `HMACSHA256.HashData(key, Canonicalize(envelope))` compared with `CryptographicOperations.FixedTimeEquals` against the decoded digest → `InvalidSignature` or accepted. Store exceptions (I/O, corrupt file) propagate: they are infrastructure faults, not protocol outcomes.

`SignAsync`: `signWith.Tenant != envelope.Header.Tenant` → `ArgumentException`; key absent/short → `UnknownKey`; else returns a new `Envelope` whose `FrontMatter` has `key_id` = `signWith` and `sig` = `hmac-sha256:` + lowercase hex, the rest untouched.

#### `Zyggy.Core.Secrets`

```csharp
public sealed record SecretName(string Value);       // Parse/TryParse; syntax ^[a-z0-9-]+(/[a-z0-9-]+)*$, ≤ 64 chars; no leading/trailing/double slash, no `.` segments

/// The only type that may reference a secret store (§9 seam). Every lookup is tenant-scoped; the full name is zyggy/<tenant>/<name> (§8, §9 naming map).
public interface ISecretStore
{
    Task<ReadOnlyMemory<byte>?> GetAsync(TenantId tenant, SecretName name, CancellationToken ct);   // null = absent (never throws for absence)
    Task SetAsync(TenantId tenant, SecretName name, ReadOnlyMemory<byte> value, CancellationToken ct); // empty value → ArgumentException; overwrites atomically
    Task<bool> RemoveAsync(TenantId tenant, SecretName name, CancellationToken ct);                  // false when absent
}

public sealed class InMemorySecretStore : ISecretStore { public InMemorySecretStore(); }            // instance dictionary keyed by (tenant, name)

public sealed class FileSecretStoreOptions { public string? RootDirectory { get; set; } }          // null → default below; validated non-empty at construction

public static class ServiceCollectionExtensions   // Zyggy.Core.Secrets
{
    public static IServiceCollection AddFileSecretStore(this IServiceCollection services, Action<FileSecretStoreOptions>? configure = null);
}
public static class ServiceCollectionExtensions   // Zyggy.Core.Envelope
{
    public static IServiceCollection AddEnvelopeSigning(this IServiceCollection services);        // EnvelopeSigner singleton; requires an ISecretStore registration
}
```

`FileSecretStore` (`internal sealed`, one instance, no cache — a rotated key is visible on the next lookup):

| Aspect | Contract |
|--------|----------|
| Path | `<RootDirectory>/<tenant>/<name>` — e.g. `<root>/acme/hmac/1`; both segments are validated types, so no traversal is expressible. |
| Default root | `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "zyggy", "secrets")` → `%LOCALAPPDATA%\zyggy\secrets` on Windows, `$XDG_DATA_HOME/zyggy/secrets` or `~/.local/share/zyggy/secrets` on Linux (user-profile on both, as §8 asks). If the folder resolves to an empty string (service without `HOME`), construction throws `InvalidOperationException` telling the operator to set `RootDirectory`. |
| Content | Lowercase hex of the raw key bytes, one line, trailing `\n`; on read, surrounding whitespace is trimmed and either hex case accepted; non-hex or odd length → `InvalidDataException` naming the path. Humans seed P0 keys with `openssl rand -hex 32 > <root>/<tenant>/hmac/1` (Windows: the same file created by the operator; 04's probe documents the exact steps). |
| Linux permissions | Directories created with `Directory.CreateDirectory(path, UnixFileMode.UserRead \| UserWrite \| UserExecute)` (0700); the file created through `FileStreamOptions { Mode = CreateNew, UnixCreateMode = UserRead \| UserWrite }` (0600, atomic at creation). |
| Windows permissions | The file created through `FileSystemAclExtensions.Create(FileInfo, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security)` where `security` has inheritance disabled (`SetAccessRuleProtection(true, false)`) and one rule: current user (`WindowsIdentity.GetCurrent().User`) `FullControl`, `Allow`. The `UnixCreateMode` setter is `[UnsupportedOSPlatform("windows")]`, so the two paths are selected by `OperatingSystem.IsWindows()`. |
| Atomic overwrite | Write `<file>.tmp` (same permissions), then `File.Move(tmp, file, overwrite: true)`; the final path never exists with permissive permissions. |
| Never | logs, caches, reads outside `<root>/<tenant>/`, returns a key for another tenant, or includes key bytes in an exception message. |

### Envelope / file formats

#### On-bus file (what `EnvelopeWriter.Serialize` produces and `EnvelopeParser` accepts)

```
---\n
<front matter: one YAML block mapping, keys in ordinal order, canonical scalar style, LF endings, including key_id and sig in their sorted positions>
---\n
<body bytes, untouched>
```

Example (`golden/job.md`; the digest is computed in RED with the recorded `openssl` command, not invented here):

```
---
agent: env-debugger
allowed_tools: [Read, Grep, Glob, Bash(dotnet *), Bash(kubectl get *)]
attempt: 1
created: 2026-09-27T14:05:00Z
deadline: 2026-09-29T18:00:00Z
from: central
id: 01J8Y3N7Q2X9Z4A5B6C7D8E9F0
key_id: acme/1
priority: normal
project: calizr
report_back: [summary, diff, files_changed]
schema: 1
sig: hmac-sha256:<64 lowercase hex>
tenant: acme
timeout_minutes: 30
to: home-laptop
type: job
worktree: true
---
Investigate why staging returns HTTP 502 on /api/bookings since Friday.
Do not change code; report root cause and a proposed fix.
```

Reading rules (the parser works on **bytes**, never on a decoded string):

1. An optional UTF-8 BOM (`EF BB BF`) at offset 0 is skipped; the writer never emits one.
2. The file must then start with `---` followed by `\n` or `\r\n` (opening delimiter). The front matter is every byte up to the first subsequent line that is exactly `---` terminated by `\n`, `\r\n` or end of file (closing delimiter). No closing delimiter → `Malformed`. This split is textual and precedes YAML parsing, so a front matter can never contain a line that is exactly `---`.
3. The body is every byte after the closing delimiter's terminator, unchanged: CRLF stays CRLF, a missing final newline stays missing, a body line `---` is body. Empty body is allowed. The body must be valid UTF-8 (`Malformed` otherwise) so `BodyText` is lossless.
4. Front-matter bytes must be valid UTF-8 and one YAML 1.2 document whose root is a mapping with unique string keys. Values are scalars, sequences (flow or block) or mappings, recursively. Anchors, aliases, merge keys, explicit tags, directives, a second document, complex (non-scalar) keys → `Malformed`. Comments are dropped (they are not part of the representation and therefore not signed — a comment is not data).
5. Every scalar is kept as its **text** (`FrontMatterScalar.Value`), whatever its original style; typed views interpret text per field (below). Line endings inside the front matter may be LF or CRLF.

#### Canonical form — `EnvelopeSigner.Canonicalize` (the bytes that are signed)

```
<yaml line 1>\n<yaml line 2>\n…<yaml line N>\n---\n<body bytes>
```

i.e. the front matter re-emitted with the rules below **without** the top-level `sig` key, every line terminated by `\n` (the last one included), then `---\n`, then the body byte-for-byte. This is the same byte sequence as §4's "YAML re-serialised with `\n` line endings, then `\n---\n`, then the body" read as "YAML lines joined by `\n`, then the separator". No opening `---`, no BOM, UTF-8. For a Zyggy-written file the canonical form is therefore the file minus its first line and minus the `sig:` line, which a human can produce with a text editor and check with `openssl dgst -sha256 -hmac '<secret>'`.

Emission rules (decided 2026-09-28 by the user as normative §4 text, ex-Q3 — amendment (c); frozen by the golden files; any change = new case in RED, protocol change after 04 ships):

| Rule | Decision |
|------|----------|
| Key order | Ordinal (`StringComparer.Ordinal`) on the key string, at every mapping level. Duplicates are impossible (rejected at parse). |
| Mappings | Block style, 2-space indent for nested mappings (`key:` then indented `child: value` lines). |
| Sequences | Flow style `[a, b, c]` when every item is a scalar (the §4 shape); block style (`- item`, indented 2) when any item is a sequence or mapping. Empty sequence: `[]`. |
| Scalars | Requested style `Plain`; YamlDotNet's emitter (libyaml algorithm, checked in `Emitter.cs` on 2026-09-28) downgrades: plain when allowed in the context → else single-quoted when the value can be single-quoted → else double-quoted with YAML escapes. Consequences: `Bash(dotnet *)` is plain inside a flow sequence; `a: b`, `#x`, ` lead`, `-x`, `true or` … become `'a: b'`; a value containing `'` or a line break becomes double-quoted; the empty string is `''`. `null`, `true`, `1`, `2026-09-27T14:05:00Z` are plain text like any other scalar. |
| Line width | Unlimited (`BestWidth = int.MaxValue`, the EmitterSettings default) — never folded. |
| Encoding / newline | UTF-8 without BOM; `EmitterSettings.WithNewLine("\n")`. |
| Unknown fields | Included, in sorted position, recursively — so adding, removing or reordering any key changes the signature (AC-7/AC-9). |
| Excluded | Only the top-level key `sig`. `key_id` is signed. |

`EnvelopeWriter.Serialize` = `---\n` + the same emission **with** `sig` + `---\n` + body. Both go through one internal emitter function; there is no second serialiser.

#### `sig` field (decided 2026-09-28 by the user, ex-Q1)

`sig: hmac-sha256:<64 hex chars>` — the HMAC-SHA256 digest over the canonical bytes, keyed by the secret named by `key_id`. Written lowercase; read either case. Only `hmac-sha256` exists; any other prefix → `InvalidSignature`. Golden `<case>.sig` = the 64 lowercase hex characters, no newline.

#### Mandatory-per-type field list (decided 2026-09-28 by the user, ex-Q2; §4 amendment (b))

Common to all types:

| Key | Mandatory | Type / rule |
|-----|-----------|-------------|
| `schema` | yes | positive integer; this version supports exactly `1`; higher → `schema_unsupported` |
| `id` | yes | ULID (26 Crockford chars) |
| `tenant` | yes | `TenantId` syntax; must equal the receiver's tenant |
| `type` | yes | `job` \| `report` \| `context` |
| `from`, `to` | yes | `MachineName` syntax |
| `created` | yes | RFC 3339 date-time (see Decision Table row) |
| `key_id` | yes | `<tenant>/<n>`, tenant part = `tenant` |
| `sig` | yes | `hmac-sha256:<hex>` |
| `in_reply_to` | job: optional (`null` or absent); **report: mandatory**; context: optional | ULID |
| `priority` | optional | `low` \| `normal` \| `high`; default `normal` |

Job (`type: job`):

| Key | Mandatory | Type / rule |
|-----|-----------|-------------|
| `project` | **yes** | non-empty string (registry name or absolute path, O18) |
| `agent` | optional | non-empty string |
| `worktree` | optional | boolean (`true`/`false`, YAML 1.2 core forms accepted); default `false` |
| `allowed_tools` | optional | sequence of non-empty strings; default empty (= the policy default, 06/18) |
| `report_back` | optional | sequence of non-empty strings; default empty |
| `timeout_minutes` | optional | integer > 0; default from `node.json` `claude.defaultTimeoutMinutes` (06) |
| `deadline` | optional | RFC 3339 date-time |
| `attempt` | optional | integer ≥ 1; default `1`; written by the sender, incremented by Central's sweep (which re-signs) |

Report (`type: report`):

| Key | Mandatory | Type / rule |
|-----|-----------|-------------|
| `in_reply_to` | **yes** | the job's ULID |
| `status` | **yes** | `done` \| `failed` \| `timeout` \| `rejected` |
| `reason` | **yes when `status` ≠ `done`; must be absent when `done`** | snake_case token `^[a-z][a-z0-9_]*$`; the closed enum values are defined by §9 (06) |
| `started`, `finished` | optional | RFC 3339 date-time |
| `duration_seconds`, `input_tokens`, `output_tokens`, `num_turns` | optional | integer ≥ 0 (`num_turns` per §11 cost tracking) |
| `cost_usd` | optional | decimal ≥ 0, invariant format |
| `model` | optional | non-empty string |
| `files_changed` | optional | sequence of non-empty strings; default empty |
| `diff_ref` | optional | non-empty string |

Context (`type: context`):

| Key | Mandatory | Type / rule |
|-----|-----------|-------------|
| `scope` | **yes** | `project:<name>` \| `machine` \| `general` |

Everything not listed is an unknown field: preserved, signed, never interpreted. Typed interpretation of scalar text: integers `int.TryParse` (invariant, optional leading sign); decimals `decimal.TryParse` (invariant, `AllowDecimalPoint`); booleans the YAML 1.2 core set (`true|True|TRUE|false|False|FALSE`); null the core set (`null|Null|NULL|~`) or an empty scalar; a sequence/mapping where a scalar is expected, or vice versa → `InvalidField`.

### Golden cases (`tests/golden/`)

| Case | Written by | Key | Purpose (ACs) |
|------|-----------|-----|---------------|
| `job` | canonical style (as `Serialize` emits) | `acme/1` | §4 example on tenant `acme`; typed parse, canonical, sig, round trip, every tamper theory (AC-1..5, 7..9) |
| `report` | canonical style | `acme/1` | `status: failed`, `reason: timeout`, all optional metrics incl. `num_turns`, `files_changed` flow list, `diff_ref`, `in_reply_to` = the job id (AC-2..5) |
| `context` | canonical style | `acme/2` | `scope: project:calizr`, body = two `[observed]` lines; rotation (AC-13) |
| `job-noncanonical` | by hand, deliberately non-canonical | `acme/1` | unsorted keys, CRLF front matter, block `allowed_tools`, `worktree: "true"`, `deadline` with fraction and `+02:00`, unknown `x_experimental: 'a: b'`, `x_empty: ''`, `x_quote: "it's: here"`, `x_null: null`, nested `x_meta` mapping with a sequence; proves sorting, the quoting ladder, unknown-field preservation (AC-6) |
| `context-crlf-body` | canonical style | `acme/1` | CRLF body, a body line `---`, no final newline (AC-19) |

`<case>.canonical` is derived by hand from the rules above; `<case>.sig` is produced by `openssl dgst -sha256 -hmac '<secret string>' <case>.canonical` (the command and the two secrets are recorded in `tests/golden/README.md`). The test enumerates `golden/*.md`, loads the siblings by name, and needs no logic beyond that.

### Configuration

| Key | Where | Default | Override rule |
|-----|-------|---------|---------------|
| `FileSecretStoreOptions.RootDirectory` | options class (`AddFileSecretStore(o => …)`); 07 binds it from `node.json` `secrets.root` | `<LocalApplicationData>/zyggy/secrets` | Any absolute path; never a default tenant inside it. |
| Signing key id | explicit `SignAsync(…, KeyId signWith, …)` parameter | none | 07/09 supply it from `node.json` `signWith` (§11); 04's probe passes it explicitly. |
| Expected tenant | explicit parameter of `Parse` / `VerifyAsync` / `ParseAndVerifyAsync` | none — no overload without it | 04 passes the tenant of the subtree it read from; the CLI/Node take it from `node.json` `tenant`. |
| Supported schema major | constant `1` in `EnvelopeParser` | `1` | Changing it is a protocol change (new golden cases, `PROTOCOL.md`). |
| Minimum HMAC key length | constant 32 bytes in `EnvelopeSigner` | 32 | Not configurable (RFC 2104: keys shorter than the hash output are discouraged). |

---

## Behaviors & Conventions

- The signature covers the parsed **text** of every front-matter scalar and the body bytes; typed values are a view. Override: none — this is what makes hand-written and cross-implementation envelopes verifiable.
- Zyggy always writes canonical style, so a Zyggy-written file's front matter minus the `sig:` line is its canonical front matter. Override: none; any other writer is accepted as long as it signs what it writes.
- A writer signs **after** serialising its fields and never edits the text afterwards; any field change (sweep `attempt`) is followed by re-signing. Override: none.
- Optional fields with a null value are omitted on write and accepted as absent, `null`, `~` or empty on read.
- Rotation: `VerifyAsync` accepts any `key_id` the store holds for the envelope's tenant; the 7-day overlap is an operational rule (`rotate-hmac.md`, 09), not code. Override: remove the old key from the store.
- `Parse` is pure and synchronous; only `VerifyAsync`/`SignAsync` touch the seam. `Parse` never calls the store, so a foreign tenant's key is never looked up (§4).
- 03 emits no log line, reads no clock, spawns no process, opens no socket; `FileSecretStore` is the only I/O.
- Every rejection carries a `Reason` from the closed enum, a `Field` when one key is at fault, and a short `Detail`; callers log once per rejection (§11).
- `InMemorySecretStore` is the substitute for unit and integration tests; `NSubstitute.For<ISecretStore>()` is used only to assert "never called" (AC-10/11/14).

---

## Failure modes

| Situation | Observable outcome | Runbook entry |
|-----------|--------------------|---------------|
| File is not an envelope (no delimiters, bad YAML, unsupported YAML feature, invalid UTF-8, root not a mapping, duplicate key) | `EnvelopeResult.Rejection` = `Malformed`, `Detail` names the rule | none needed in 03 (04 logs and moves to `jobs/rejected/`) |
| `tenant` ≠ expected | `TenantMismatch`; store never called | 04 |
| `schema` > 1 | `SchemaUnsupported` (wire `schema_unsupported`); store never called | 04/06 map to the report reason |
| `key_id` tenant part ≠ `tenant` | `KeyIdTenantMismatch`; store never called | 04 |
| Mandatory field missing / field of the wrong shape, `status`/`reason` inconsistency | `MissingField` / `InvalidField` with `Field` | 04 |
| `sig` missing / malformed | `MissingSignature` / `InvalidSignature` | 04 |
| Key absent or shorter than 32 bytes | `UnknownKey` (`Detail` says which, never the bytes) | 09 ("signing key absent at start-up" is 09's runbook) |
| Any byte of the canonical form differs | `InvalidSignature` | 04 (`INotifier` alert "Signature failures" is 19) |
| `SignAsync` with a key of another tenant | `ArgumentException` (caller bug) | — |
| `Serialize` of an unsigned envelope | `InvalidOperationException` (caller bug) | — |
| `FileSecretStore` root not writable / cannot set permissions / corrupt hex file | `IOException` / `UnauthorizedAccessException` / `InvalidDataException` naming the path, propagated (not a protocol outcome) | `src/Zyggy.Core/README.md` → "Secrets: file store" troubleshooting (location per OS, expected content, permissions, how to reseed) |
| `FileSecretStore` constructed without a resolvable `RootDirectory` (no `HOME`) | `InvalidOperationException` at construction | same |
| YamlDotNet upgrade changes emission | Golden `[Theory]` red on `.canonical` bytes — by design; a protocol decision, never a silent fix | `tests/golden/README.md` |

---

## Dependencies

All verified on nuget.org / GitHub on 2026-09-28; all MIT; all pure managed IL with no native assets, so the §9 single-file self-contained publish (no trimming) is unaffected. Pinned in `Directory.Packages.props`.

| Package | Version | License | Projects | Why (what bespoke code it removes) |
|---------|---------|---------|----------|------------------------------------|
| `YamlDotNet` | 18.1.0 (released 2026-06-26; targets `net10.0` explicitly; no dependencies) | MIT | `Zyggy.Core` | YAML 1.2 parser and emitter for the front matter — replaces a hand-written tokenizer/emitter (§9 names it). Only `YamlDotNet.Core` (`Parser`, `Emitter`, `EmitterSettings`) is used; no reflection serializer. Actively maintained (aaubry/YamlDotNet). ⚠️ new dependency; its emitter's style selection is frozen by the golden files. |
| `Ulid` | 1.4.1 (released 2025-07-28; `net8.0` asset used on net10; no dependencies) | MIT | `Zyggy.Core` (internal use behind `EnvelopeId`) | ULID generation and Crockford base32 parse/format (§9 names it). Cysharp/neuecc; release cadence is fix-driven (1.3.x–1.4.x over 2023–2025) which is adequate for a finished 26-char format; wrapped so it can be replaced locally. Uses `unsafe`/SIMD internally — fine for single-file. ⚠️ new dependency. |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.12 (2026-09-08) | MIT | `Zyggy.Core` | `IServiceCollection` for the `Add*` extension methods (public-api.md). Already transitive in `Zyggy.Node`. |
| `Microsoft.Extensions.Options` | 10.0.12 (2026-09-08) | MIT | `Zyggy.Core` | `IOptions<FileSecretStoreOptions>` so 07 can bind `node.json` without an API change. |

BCL used, no package: `HMACSHA256.HashData`, `CryptographicOperations.FixedTimeEquals`, `Convert.ToHexStringLower`/`FromHexString`, `FileStreamOptions.UnixCreateMode` (.NET 7+), `Directory.CreateDirectory(string, UnixFileMode)`, `File.GetUnixFileMode`, `FileSystemAclExtensions`/`FileSecurity` (in the shared framework since .NET 6; the planner confirms at the first build that no `System.IO.FileSystem.AccessControl` reference is required — if one is, it is Microsoft/MIT 5.0.0 and is added to the props with a note).

Not added: `Meziantou.Framework.Win32.CredentialManager` (09), Serilog (07), anything else from §9.

---

## Deliberate deviations from the founding spec and the hand-off brief

- **`sig` digest in hex, not base64** (§4 example) — decided 2026-09-28 by the user (ex-Q1); §4 amendment (a).
- **Mandatory-per-type field list and canonical-form details added to §4** — decided 2026-09-28 by the user (ex-Q2, ex-Q3); amendments (b) and (c).
- **`ISecretStore` takes `(TenantId, SecretName)`** rather than the brief's `(TenantId, key number)`: same tenant scoping, one seam for all four §8 secret kinds. Decision Table row "§8 Secrets".
- **`to` is not checked in 03** (§4 lists it among receiver rejections): a caller-specific routing check done by 04 on `Header.To`. Decision Table row "§4 `to`".
- **`MachineName` added to `Tenancy/`** (brief lists three types): avoids a 04 API change. *Overturnable.*
- **No `TimeProvider` in 03** (brief Q9): factories take the instant; callers own the clock.
- **`num_turns` added as an optional report field** (§11 lists it, §4 did not) — part of amendment (b).
- **`Reason` on reports is a validated string in 03**, becoming the §9 enum in 06 (brief: 03 must not define the enum).
- **Runbook entries are README troubleshooting sections** (§12), as in spec 01, because no runbook directory exists before 10.

---

## Edge Cases

| Case | Expected behavior |
|------|-------------------|
| Front matter with CRLF, body with LF (or the reverse) | Front matter re-emitted with `\n`; body bytes untouched; verifies if signed that way. |
| `sig` written in uppercase hex by another implementation | Accepted (digest compared as bytes). |
| Two `---` lines in the body | Both are body; only the first `---` line after the opening delimiter closes the front matter. |
| `in_reply_to: null` vs absent on a job | Both parse to `InReplyTo == null`; Zyggy writes neither. |
| `priority` absent | `Normal`; the writer always emits `priority` so its files are explicit. |
| `attempt: 01` (leading zero) | Parses as 1; canonical text stays `01`; verifies. A re-signed rewrite would emit `1`. |
| `allowed_tools: []` | Accepted, empty list; canonical `allowed_tools: []`. |
| A scalar value `true` under an unknown key | Kept as text `true`; nobody interprets it. |
| Unknown key that sorts before `agent` (e.g. `_x`) | Emitted first; signed. |
| 64-char tenant, `Acme`, `acme_1` in `tenant` | `InvalidField` (`tenant`), before any tenant comparison. |
| `key_id: acme/1` but `sig` computed with `acme/2` | `InvalidSignature` (key found, digest differs). |
| Store returns a 31-byte key | `UnknownKey` with `Detail` "shorter than 32 bytes". |
| Body of 2 MB | Signed as-is; no size rule in 03 (DLP limits are policy, 17/18). |
| `FileSecretStore.SetAsync` twice for the same name | Second call atomically overwrites; permissions unchanged (AC-27). |
| Two processes call `SetAsync` for the same name concurrently | Last rename wins; both files were complete; no torn read. |
| `RootDirectory` on a FAT/exFAT volume (no ACL/mode) | `SetAsync` throws (permissions cannot be applied); never silently permissive. |

---

## Out of Scope

- `BusPaths` (file name `<id>.md`, `tenants/<org>/…` layout), `BusRepository`, `GitClient`, `GitHubPoller`/`PollState`, `IBusProvider`, the move to `jobs/rejected/`, the `status: rejected` report and its `reason`, the `to`-equals-own-machine check, the tenant-versus-path check, `BusRepoFixture.SeedTenantAsync` — 04.
- `JobRunner`, `IModelRunner`, `JobFailureReason` (the 9-member enum), report assembly from runs, prompt template — 06.
- `Envelope.With(...)` field modification and the sweep's `attempt: n+1` re-sign — the sweep deliverable.
- CLI verbs (`verify`, `submit`, `probe`), Hub tools, `PROTOCOL.md` — 08, 11, 15, 04.
- OS secret stores (`WindowsCredentialManagerSecretStore`, `LibSecretSecretStore`, `SystemdCredentialSecretStore`), `zyggy secret`, `signWith` binding, `rotate-hmac.md` execution, retiring the Windows file store — 09/07.
- `policy.yaml` signing (18); registry documents and O24 (14/15); DLP limits on bodies (17).
- Logging, telemetry, alerts on signature failures (07/19).
- Any `Zyggy.Node`, `Zyggy.Cli`, `Zyggy.Hub` code; any `IProcessRunner` use; any `TimeProvider` registration.
- A second signature algorithm, key derivation, encryption of bodies (§4: HMAC gives authenticity only; confidentiality is the repository boundary).

---

## Findings forwarded to later deliverables (not blocking 03)

1. **Rejection reports have no `reason` value.** §4 says a receiver writes a `reports/` entry `status: rejected` for signature/tenant/schema failures, but the closed §9 enum only contains `schema_unsupported` for that family. 04 (which owns the rejected move) must raise an Open Question: extend the §9 enum with e.g. `invalid_signature`, `unknown_key`, `tenant_mismatch`, `malformed`, or write the `EnvelopeRejectionReason` wire string. 03 keeps `ReportFields.Reason` a validated string so either answer needs no change here.
2. **`.claude/instructions/tests.md` line 104** names key id `geoffrey/test` in the golden example; the plan for 03 should correct it to `acme/1` and the two secrets above (the instruction file is not a spec; the user or the build step edits it).
3. **`.claude/instructions/public-api.md`** shows an `IEnvelopeSigner`; this spec deliberately ships no such interface. The example is documentation style only; no change required unless the user wants the text aligned.
4. **04's probe procedure** must document seeding `FileSecretStore` on the three machines by hand (`<root>/<tenant>/hmac/1`, hex content) until `zyggy secret` exists (09).

---

## Proposed §4 amendments (ready to paste)

Decided 2026-09-28 by the user (ex-Q1, ex-Q2, ex-Q3). Only the user edits `_specs/00 - Personal Agent Platform — Technical Specification.md`; the texts below are written in the founding spec's style so they can be applied by hand. `PROTOCOL.md` (15) must then match the amended §4 verbatim.

### (a) `sig` encoding — replace one example line and one sentence

In the §4 **Envelope** example, replace the line

```
sig: hmac-sha256:BASE64...
```

with

```
sig: hmac-sha256:9f2c…            # hmac-sha256:<64 lowercase hex chars> over the canonical form
```

In §4 **Signature**, replace the first bullet

> `sig` = HMAC-SHA256 over the canonical form: front matter with `sig` removed, keys sorted, YAML re-serialised with `\n` line endings, then `\n---\n`, then the body byte-for-byte.

with

> `sig` = `hmac-sha256:` followed by the HMAC-SHA256 digest, as 64 lowercase hexadecimal characters, over the canonical form: front matter with `sig` removed, keys sorted, YAML re-serialised with `\n` line endings, then `\n---\n`, then the body byte-for-byte (normative details below). A receiver accepts the digest in either case and treats any other algorithm prefix as an invalid signature.

### (b) Mandatory fields per type — insert after the Context-specific fields sentence

Insert immediately after the §4 sentence

> Context-specific fields: `scope` (`project:<name> | machine | general`), body = one `[stated]`-style fact per line.

the following:

> **Mandatory fields per type.** A receiver rejects an envelope that lacks a mandatory field or carries a field of the wrong shape (`missing_field` / `invalid_field`); every field not listed here is unknown: preserved, signed, never interpreted.
>
> | Type | Mandatory | Optional (default) |
> | --- | --- | --- |
> | all | `schema` (positive integer; a node supports exactly `1`), `id` (ULID), `tenant`, `type`, `from`, `to`, `created`, `key_id`, `sig` | `in_reply_to` (`null`; mandatory on `report`), `priority` (`normal`) |
> | `job` | `project` | `agent` (none), `worktree` (`false`), `allowed_tools` (empty = policy default), `report_back` (empty), `timeout_minutes` (`node.json` `claude.defaultTimeoutMinutes`), `deadline` (none), `attempt` (`1`) |
> | `report` | `in_reply_to` (the job id), `status`; `reason` when `status` is not `done` (must be absent when `done`) | `started`, `finished`, `duration_seconds`, `cost_usd`, `input_tokens`, `output_tokens`, `num_turns`, `model`, `files_changed` (empty), `diff_ref` |
> | `context` | `scope` | — |
>
> Timestamps are RFC 3339 date-times written as `YYYY-MM-DDTHH:MM:SSZ` (UTC, whole seconds); machine and tenant names are lowercase labels (`[a-z0-9-]`, 1–63 characters, no leading or trailing hyphen); integers and `cost_usd` use the invariant format.

Also in the §4 report-fields sentence, after `output_tokens`, insert `num_turns` (it is already listed in §11 cost tracking).

### (c) Canonical form, normative details — insert after the Signature bullets

Insert immediately after the third §4 **Signature** bullet (the one ending "…so a key from another tenant is never even looked up.") the following:

> **Canonical form, normative details.** The file is read as bytes: an optional UTF-8 BOM is skipped; the file starts with `---` and a line break; the front matter ends at the first following line that is exactly `---`; the body is every byte after that line's break, unchanged (line endings, a missing final newline and further `---` lines included; may be empty; must be valid UTF-8). The front matter is one YAML 1.2 document whose root is a mapping with unique string keys and whose values are scalars, sequences or mappings, recursively; anchors, aliases, merge keys, explicit tags, directives and a second document are rejected as malformed; comments are not data and are dropped. The canonical form re-emits that mapping without the top-level `sig` key: keys in ordinal order at every level; mappings in block style with two-space indentation; sequences in flow style (`[a, b]`) when every item is a scalar, block style otherwise, `[]` when empty; every scalar as its parsed text, plain when YAML allows it in that position, otherwise single-quoted, otherwise double-quoted (the empty string is `''`); lines terminated by `\n`, no line folding, UTF-8 without BOM; then `---\n`; then the body bytes. For a file written by `agentbus` the canonical form is therefore the file minus its first line and its `sig:` line. A writer signs the bytes it emits and never edits them afterwards; any later field change (for example the sweep's `attempt`) is followed by re-signing.
