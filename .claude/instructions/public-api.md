---
description: "Public API design conventions for Zyggy.Core, the class library consumed by Zyggy.Node, Zyggy.Cli and Zyggy.Hub: XML documentation, internal-and-sealed by default, guard clauses, CancellationToken, the five seam interfaces, closed enums, records for values. Activates when editing Zyggy.Core source files."
applyTo: "src/Zyggy.Core/**"
---
# Public API Design Conventions — Zyggy.Core

`Zyggy.Core` is the only shared library: `Zyggy.Node`, `Zyggy.Cli` and `Zyggy.Hub` reference it and nothing else in the solution. Its public surface is therefore the contract between the spec's agents (`core-dev`, `node-dev`, `hub-dev`) and deserves the same care as a published package.

## Visibility

- `internal` by default. Only types and members the three hosts need are `public`.
- Seal classes unless designed for inheritance (`sealed class` by default). Values are `record`s (`Envelope`, `ProcessResult`, `PollState`, `RegistryDocument`).
- The **five seams** are public interfaces with exactly one v1 implementation each, and they are the only public abstractions allowed to name an external system:

  | Seam | Hides |
  |------|-------|
  | `IBusProvider` | GitHub (poll + git remote) |
  | `IModelRunner` | the Claude Code CLI (`ClaudeCodeCliRunner`; `AgentSdkRunner` later) |
  | `ISecretStore` | Windows Credential Manager / libsecret / systemd credentials |
  | `INotifier` | Telegram |
  | `IPolicySource` | signed `policy.yaml` |

  Plus `IProcessRunner`, the single path to `git` and `claude` processes. Do not add a sixth abstraction with a single implementation unless a spec Open Question decided it.
- Implementations are `internal sealed` and exposed through `Add<Feature>` extension methods on `IServiceCollection`.
- Mark `virtual` only what is intended to be overridden. Prefer sealed types plus interfaces over inheritance.

## XML Documentation

Every public type and member requires XML documentation (`GenerateDocumentationFile` is on; with warnings as errors, a missing `<summary>` fails the build):

```csharp
/// <summary>
/// Signs and verifies bus envelopes with HMAC-SHA256 over the canonical form (founding spec §4).
/// </summary>
public interface IEnvelopeSigner
{
    /// <summary>
    /// Verifies <paramref name="envelope"/> against the key named by its <c>key_id</c>.
    /// </summary>
    /// <param name="envelope">The parsed envelope, including its <c>sig</c> field.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="SignatureResult.Valid"/>, or the reason the signature was refused.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is null.</exception>
    Task<SignatureResult> VerifyAsync(Envelope envelope, CancellationToken cancellationToken);
}
```

Required tags: `<summary>` on every public type and member; `<param>` for every parameter; `<returns>` for non-void methods; `<exception>` for documented thrown exceptions; `<remarks>` when behavior is non-trivial. Cite the founding-spec section when the member implements a protocol rule.

## Nullable Reference Types

- Enabled project-wide. Never return `null` without an explicit `T?` return type.
- Use `[NotNullWhen(true)]`, `[MaybeNullWhen(false)]` on try-patterns (`TryParse`).

## Guard Clauses

All public method parameters are validated:

```csharp
public async Task<Report> RunAsync(Envelope job, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(job);
    // ...
}
```

Exception: `JobRunner.RunAsync` and the poll loop body never throw *after* argument validation — every failure becomes a report with a `reason` from the closed enum, or a logged state change.

## CancellationToken

All async public methods accept `CancellationToken` as the last parameter and propagate it through every async call, including into `IProcessRunner` so a job timeout kills the process tree.

## Closed enums and wire strings

- Reason codes, statuses, priorities, envelope types are `enum`s with a single serialisation helper that produces the exact snake_case strings the spec names (`unknown_project`, `dlp_filter`, `schema_unsupported`…). A test asserts every member's string.
- Adding a member is a protocol change: it needs a resolved Open Question and a `PROTOCOL.md` update.

## Return Types

- Return interfaces or records from public API; prefer `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` over concrete collections.
- Prefer a result record with a reason over throwing for expected failures (signature invalid, unknown project, push rejected).

## Deprecation

- Pre-1.0 the surface may change freely, but a member used by another host is renamed in the same deliverable, not left dangling.
- From 1.0: `[Obsolete("Use X instead. Will be removed in vN.")]` for one minor version before removal.

## All public API changes are a Risk Area — flag them ⚠️ in the plan and review them at the covering gate.
