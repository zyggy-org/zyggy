---
name: fix-violations
description: "Use when fixing compiler diagnostics and analyzer findings in Zyggy with modern .NET 10 / C# 14 best practices. Prefer the native compiler, SDK analyzers, and idiomatic code — this repo uses no StyleCop."
metadata:
  argument-hint: "Warning code or symptom, e.g. 'CA1859', 'nullable warning', 'xUnit1013'"
---

# Fix Violations — Modern .NET Code Analysis

Use this skill to clean up warnings and analyzer violations. The default approach is to trust the native C# compiler, the .NET SDK analyzers, and broadly accepted community practices. This repo intentionally has **no StyleCop** — style is enforced by `dotnet format` and any `.editorconfig` present.

## Core principles

- Warnings are errors: `TreatWarningsAsErrors` is set in the root `Directory.Build.props` (fixed — do not modify). A plain `dotnet build` fails on any warning.
- Prefer the compiler and SDK analyzers over custom style rule churn.
- Favor modern C# and .NET idioms: nullable reference types, `required` members, pattern matching, switch expressions, collection expressions, primary constructors, file-scoped namespaces, `using` declarations, `await using`, `TimeProvider`.
- Prefer clarity and correctness over unnecessary abstraction.
- Only suppress a warning when there is a strong, documented reason.

## Preferred workflow

1. Build (per project while `Zyggy.slnx` is being populated, otherwise the solution):

```powershell
dotnet build Zyggy.slnx
```

2. Apply formatting and style fixes:

```powershell
dotnet format Zyggy.slnx
```

3. Review the remaining diagnostics and fix them in this order:
   - compiler errors and warnings
   - nullable warnings
   - SDK analyzer findings (`CA*`, `IDE*`)
   - test analyzer findings (`xUnit*`)
   - formatting issues

4. Re-run the build and verify the workspace is clean.

## Modern rule families

### Compiler and nullable issues

| Warning | Typical fix |
|--------|-------------|
| `CS8600`, `CS8602`, `CS8604`, `CS8618` | Fix nullability with guards, annotations, `required`, `?`, `!`, or better null-handling flow. |
| `CS0618` | Replace the obsolete API with the supported alternative. |
| `CS1998` | Remove `async` when no await is used, or add the missing await. |
| `CS4014` | Await the task or explicitly ignore the result when intentional. |

### Common .NET analyzer rules

| Rule | Fix |
|------|-----|
| `CA1001` | Ensure disposable types are properly disposed. |
| `CA1031` | Avoid broad catch blocks — except at `JobRunner`'s outer boundary, which must map every failure to a `reason`-coded report; document that catch. |
| `CA1305` | Use culture-aware formatting; envelope timestamps are ISO-8601 UTC with `CultureInfo.InvariantCulture`. |
| `CA1307`, `CA1310` | Specify `StringComparison.Ordinal` — machine names, key ids and reason codes are ordinal. |
| `CA1508` | Simplify complex conditional logic or extract helper methods. |
| `CA1822` | Mark helpers as `static` when they do not use instance state. |
| `CA1848` | Use `LoggerMessage` patterns on the poll loop and job phases. |
| `CA1859` | Prefer concrete types over interface/abstract types for locals, fields, and private returns. |
| `CA1860` | Prefer `Count`/`Length`/`IsEmpty` checks over `Enumerable.Any()`. |
| `CA1861` | Extract constant arrays to a `static readonly` field or use a collection expression. |
| `CA2000` | Dispose objects created by `new` when ownership is clear (`Process`, `HttpResponseMessage`). |
| `CA2208` | Throw `ArgumentException`/`ArgumentNullException` with the correct constructor overload. |
| `CA2249` | Prefer `ArgumentNullException.ThrowIfNull` over ad-hoc null checks. |

### Modern IDE style rules

| Rule | Fix |
|------|-----|
| `IDE0055` | Apply formatter output and consistent spacing. |
| `IDE0060` | Remove unused parameters when the API allows it. |
| `IDE0290` | Prefer primary constructors for simple types. |
| `IDE0300`, `IDE0301` | Use collection expressions such as `[]`. |
| `IDE0161` | Use file-scoped namespaces. |

### Test analyzer rules (xunit.analyzers)

| Rule | Fix |
|------|-----|
| `xUnit1004` | Skipped test needs a reason — or delete it. |
| `xUnit1013` | Public method in a test class without `[Fact]`/`[Theory]` — make it private or a helper. |
| `xUnit1026` | `[Theory]` parameter unused — use it or drop it. |
| `xUnit1030` | Do not call `ConfigureAwait(false)` in tests. |
| `xUnit2xxx` | Assertion usage — prefer FluentAssertions `Should()`; when using `Assert.*`, use the specific overload suggested. |

## Practical patterns to prefer

- `ArgumentNullException.ThrowIfNull(value)` and `ArgumentException.ThrowIfNullOrWhiteSpace(value)`.
- `string.Equals(a, b, StringComparison.Ordinal)` for identifiers.
- `await using` for `IAsyncDisposable`; `using var` for block-scoped disposables.
- `switch` expressions over state (`PollState`, envelope type, reason code).
- `record` for envelope models, `ProcessResult`, `PollState`.
- `sealed` classes by default.

## What to avoid

- Adding StyleCop packages, `stylecop.json`, or SA-rule suppressions.
- Adding suppressions without a clear reason.
- "Cleanups" that change `EnvelopeSigner.Canonicalize` output, reason-code strings, or bus paths.
- Using old patterns just because they are familiar if the compiler and analyzers already guide a better option.

## Verification

```powershell
dotnet build Zyggy.slnx
dotnet format Zyggy.slnx --verify-no-changes
```

If warnings remain, fix them until both commands succeed.
