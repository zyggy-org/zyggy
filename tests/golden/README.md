# Golden files — canonical envelopes and their signatures

Each case is three files that must stay **byte-exact**:

| File | Contents |
|------|----------|
| `<case>.md` | The envelope exactly as it sits on the bus (front matter + body). |
| `<case>.canonical` | The exact bytes `EnvelopeSigner.Canonicalize` must produce for `<case>.md`: front matter minus `sig`, keys sorted, `\n` line endings, then `\n---\n`, then the body. |
| `<case>.sig` | The expected HMAC (hex) of `<case>.canonical` for the fixed test key. |

## Rules

- `tests/golden/**` is marked `-text` in `.gitattributes` and is excluded from line-ending and whitespace normalisation in `.editorconfig`: these bytes are a protocol. Never let an editor touch them.
- The files are copied to the test output under `golden/` by the `None` item in `tests/Zyggy.Core.Tests/Zyggy.Core.Tests.csproj` and enumerated by a `[Theory]` + `[MemberData]` in `Zyggy.Core.Tests`.
- The key id and the test secret are fixed by `_specs/03-envelope-signing.md`.
- Any change to canonicalisation or to an envelope field starts with a **new case added in RED**.
- No case exists before deliverable 03; this README is the only file here until then.
