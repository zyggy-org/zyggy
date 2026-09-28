---
name: add-public-api
description: "Add a new public type, interface, or method to Zyggy.Core (the library consumed by Zyggy.Node, Zyggy.Cli and Zyggy.Hub) with full test coverage and XML documentation. Follows Red-Green-Refactor per step with HUMAN GATE. Use when adding a new seam interface, implementation, options record, or extension method to the library's API surface."
argument-hint: "What to add, e.g. 'INotifier interface with SendAsync method and a Telegram implementation'"
---

# Add Public API — Implementation Skill

Add a new public type, interface, or method to `Zyggy.Core` with full test coverage, XML documentation, and DI registration in the hosts that need it.

## Prerequisites

- Know **what** to add: the public type name, its purpose, its expected API surface, and whether it is one of the five §9 seams (`IBusProvider`, `IModelRunner`, `ISecretStore`, `INotifier`, `IPolicySource`) or a supporting type.
- Identify a **reference pattern** — an existing seam in the repo whose shape you will follow. If none exists yet, follow the templates in `.claude/skills/build-feature/SKILL.md` and say so.
- If ≥ 3 files are involved or a shared contract changes, a `_plans/<NN>-<Deliverable>.md` should exist and be approved. For smaller additions (single type + test), proceed directly.

## Mandatory Workflow Per Step

```
1. READ — understand what to add and which reference pattern to follow
2. RED — write a failing test FIRST
3. RUN — dotnet test tests/Zyggy.Core.Tests --filter "FullyQualifiedName~<TestClass>" → confirm FAIL
4. GREEN — write minimal production code to pass
5. RUN — same command → confirm PASS
6. REFACTOR — clean up, ensure XML docs are complete on all public members
7. CODE ANALYSIS — fix all violations:
   a. dotnet build Zyggy.slnx 2>&1 | Select-String -Pattern ": (warning|error) (CA|CS|IDE|xUnit)\d+" | Sort-Object | Get-Unique
   b. dotnet format Zyggy.slnx
   c. Fix remaining violations manually
   d. Repeat a–c until clean
8. PROVE:
   - dotnet build Zyggy.slnx → zero warnings/errors
   - dotnet test Zyggy.slnx → all pass
   - dotnet format Zyggy.slnx --verify-no-changes → exit 0
9. 🛑 STOP — present results, wait for user approval
10. MARK DONE — update _plans/ checkboxes if applicable
```

**Never batch steps. Never skip RED. Never skip CODE ANALYSIS. Never proceed past 🛑 without user confirmation.**

---

## Workflow Steps

### Step 1 — Define the Contract

Create the public interface with full XML documentation.

**Location**: `src/Zyggy.Core/<Folder>/I<Name>.cs` (folder per §9: `Envelope`, `Bus`, `Registry`, `Jobs`, `Memory`, `Secrets`)

```csharp
namespace Zyggy.Core.<Folder>;

/// <summary>
/// Sends user-facing alerts. Telegram in v1; the only type allowed to reference a chat provider.
/// </summary>
public interface INotifier
{
    /// <summary>
    /// Sends <paramref name="message"/> to the configured channel.
    /// </summary>
    /// <param name="message">Plain-text message; never contains envelope bodies or secrets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the message is accepted by the provider.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="message"/> is empty.</exception>
    Task SendAsync(string message, CancellationToken cancellationToken);
}
```

**Rules**:
- `public` only for the intended API surface — everything else `internal`.
- Every public member gets `<summary>`, `<param>`, `<returns>`, `<exception>`.
- `CancellationToken` as the last parameter on every async method.
- Seal classes by default; records for values (`ProcessResult`, `PollState`, envelope models).
- A seam interface names the one thing outside code may not reference directly (GitHub, `claude`, Telegram, a secret store).

---

### Step 2 — RED: Write Failing Test

**Location**: `tests/Zyggy.Core.Tests/<Folder>/<Name>Tests.cs`

```csharp
namespace Zyggy.Core.Tests.<Folder>;

public sealed class <Name>Tests
{
    [Fact]
    public async Task SendAsync_EmptyMessage_Throws()
    {
        // Arrange
        var sut = new <Implementation>(/* substitutes */);

        // Act
        var act = () => sut.SendAsync("", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
```

**Rules**:
- Test method naming: `<Method>_<Scenario>_<Expected>`. AAA. One Act.
- Substitute only at seams (NSubstitute). No real process, network, or secret store.
- Run the test → confirm it **fails** (RED).

---

### Step 3 — GREEN: Implement

**Location**: `src/Zyggy.Core/<Folder>/<Name>.cs`

```csharp
namespace Zyggy.Core.<Folder>;

/// <summary>Telegram implementation of <see cref="INotifier"/>.</summary>
internal sealed class TelegramNotifier(HttpClient http, IOptions<TelegramOptions> options) : INotifier
{
    /// <inheritdoc />
    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        // ...
    }
}
```

**Rules**:
- `internal sealed` by default — consumers use the interface.
- Guard clauses on all public method parameters.
- Run the test → confirm it **passes** (GREEN).

---

### Step 4 — REFACTOR

- XML docs complete on all public members.
- Naming follows the reference pattern.
- No new NuGet dependency without the spec listing it (license, justification).

---

### Step 5 — DI Registration

Register in the host(s) that need it, through an extension method in `Zyggy.Core` so hosts stay thin.

**Location**: `src/Zyggy.Core/<Folder>/ServiceCollectionExtensions.cs`

```csharp
/// <summary>Adds the Telegram <see cref="INotifier"/>.</summary>
public static IServiceCollection AddTelegramNotifier(this IServiceCollection services, Action<TelegramOptions>? configure = null)
{
    if (configure is not null)
    {
        services.Configure(configure);
    }

    services.AddHttpClient<INotifier, TelegramNotifier>();
    return services;
}
```

**Test**: resolution test

```csharp
[Fact]
public void AddTelegramNotifier_RegistersINotifier()
{
    var services = new ServiceCollection();
    services.AddTelegramNotifier();
    using var provider = services.BuildServiceProvider();
    provider.GetRequiredService<INotifier>().Should().BeOfType<TelegramNotifier>();
}
```

---

### Step 6 — Options Record (if applicable)

**Location**: `src/Zyggy.Core/<Folder>/<Feature>Options.cs`

```csharp
namespace Zyggy.Core.<Folder>;

/// <summary>Settings bound from node.json; never holds a secret (tokens come from <see cref="ISecretStore"/>).</summary>
public sealed class TelegramOptions
{
    /// <summary>Chat id that receives alerts.</summary>
    public required string ChatId { get; init; }
}
```

Validate with `IValidateOptions<T>`; a test covers the invalid case.

---

### Step 7 — CODE ANALYSIS + PROOF + 🛑 HUMAN GATE

1. Fix all CA/CS/IDE/xUnit violations.
2. Run the full proof trilogy: build → test → format.
3. Present results to the user.
4. **🛑 STOP and wait for approval.**
