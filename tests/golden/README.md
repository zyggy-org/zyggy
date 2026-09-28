# Golden files — canonical envelopes and their signatures

Each case is three files that must stay **byte-exact**:

| File | Contents |
|------|----------|
| `<case>.md` | The envelope exactly as it sits on the bus (front matter + body). |
| `<case>.canonical` | The exact bytes `EnvelopeSigner.Canonicalize` must produce for `<case>.md` (founding spec §4 "Canonical form, normative details"): the front matter re-emitted without the top-level `sig` key, keys in ordinal order at every level, every line ending in `\n`, then `---\n`, then the body bytes unchanged. No opening `---`, no BOM. |
| `<case>.sig` | The HMAC-SHA256 of `<case>.canonical` under the case's test key: **64 lowercase hexadecimal characters, no newline**. The `.md` carries the same digest as `sig: hmac-sha256:<hex>`. |

## Test keys

Synthetic, fixed by `_specs/03-envelope-signing.md`; never a real credential. Each secret is the UTF-8 bytes of the string (32 bytes).

| Key id | Secret |
|--------|--------|
| `acme/1` | `zyggy-golden-test-key-acme-00001` |
| `acme/2` | `zyggy-golden-test-key-acme-00002` |

## Cases

All `schema: 1`, `tenant: acme`, LF line endings unless stated.

| Case | Style | Key | Serves |
|------|-------|-----|--------|
| `job` | canonical style | `acme/1` | the §4 example: typed parse, canonical bytes, signature, round trip, every tamper theory (AC-1..5, AC-7..9) |
| `report` | canonical style | `acme/1` | `status: failed` + `reason: timeout`, every optional metric incl. `num_turns`, `files_changed`, `diff_ref`, `in_reply_to` = the job id (AC-2..5) |
| `context` | canonical style | `acme/2` | `scope: project:calizr`; the second key proves rotation (AC-13) |
| `job-noncanonical` | by hand, **CRLF** front matter, unsorted | `acme/1` | sorting, block-to-flow sequence, `worktree: "true"` emitted plain, `deadline` with fraction and offset kept as text, the quoting ladder (`'a: b'`, `''`, `"it's: here"`), `x_null: null`, nested `x_meta` with a block sequence of mappings; unknown fields preserved and signed (AC-6) |
| `context-crlf-body` | canonical style | `acme/1` | CRLF body, a body line `---`, no final newline (AC-19) |

## How the oracle files are produced — never by the code under test

1. Write `<case>.md` by hand, leaving the `sig:` line for step 4.
2. Derive `<case>.canonical` **by hand** from §4 "Canonical form, normative details". For a canonical-style `.md` this is exactly the file minus its first line and minus its `sig:` line; produce it with a byte-preserving tool (`sed '1d;/^sig: /d'` on Linux, `[System.IO.File]::ReadAllBytes`/`WriteAllBytes` in PowerShell), never with an editor that may add a BOM, a final newline or CRLF. `job-noncanonical.canonical` is re-derived rule by rule.
3. Compute the digest with openssl (on Windows, `openssl` from Git for Windows):

   ```sh
   openssl dgst -sha256 -hmac 'zyggy-golden-test-key-acme-00001' job.canonical | awk '{printf "%s", $2}' > job.sig
   ```

   `context` uses `zyggy-golden-test-key-acme-00002`. Check: each `.sig` is 64 bytes.
4. Write `sig: hmac-sha256:<those 64 hex chars>` into `<case>.md`, in its sorted position for canonical-style cases.

Pasting `Zyggy.Core` output into a `.canonical` or `.sig` file is forbidden: that would make the code its own oracle. If a golden test is red, the only admissible fixture fix is a hand re-derivation against §4, recorded at the plan's human gate with the rule that was misapplied.

## Rules

- `tests/golden/**` is marked `-text` in `.gitattributes` and is excluded from line-ending and whitespace normalisation in `.editorconfig`: these bytes are a protocol. Never let an editor touch them.
- The files are copied to the test output under `golden/` by the `None` item in `tests/Zyggy.Core.Tests/Zyggy.Core.Tests.csproj` and enumerated by a `[Theory]` + `[MemberData]` over `golden/*.md`.
- Any change to canonicalisation or to an envelope field starts with a **new case added in RED**.
- A YamlDotNet upgrade that changes emission turns the golden theory red by design: that is a protocol decision, never a silent fixture fix.
