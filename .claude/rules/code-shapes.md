---
description: "Reference catalog of Zyggy code shapes: seam + fake + real, PollState, the closed reason enum, options binding, the Node hosted service, a CLI verb, a Hub MCP tool. Loads when editing source files; test shapes are in tests.md."
paths:
  - "src/**"
---
# Zyggy code shapes — reference catalog

Not sequential steps: look up the shape a slice's Scaffold row needs. The order of work is `build-slice`'s; the test shapes (unit, golden file, integration) are in `.claude/rules/tests.md`; the design rules are in `CLAUDE.md` § Design rules.

### Seam interface + fake + real implementation (`Zyggy.Core`)

Interface in `src/Zyggy.Core/<Folder>/I<Seam>.cs`:

```csharp
namespace Zyggy.Core.Bus;

/// <summary>Runs an external process. The only path by which Zyggy invokes git or claude.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> in <paramref name="workingDirectory"/>.</summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

Real implementation, `internal sealed`, in the same folder; registered in the host's DI (`services.AddSingleton<IProcessRunner, ProcessRunner>()`). In tests use NSubstitute (`Substitute.For<IProcessRunner>()`) or a small hand-written fake that records calls when ordering matters (the git write sequence `pull --rebase → change → commit → push`).

### Pure state machine (poller)

```csharp
namespace Zyggy.Core.Bus;

public sealed record PollState(string? ETag, TimeSpan Interval, DateTimeOffset? BackoffUntil);

public static class PollPolicy
{
    public static PollState OnResponse(PollState state, PollResponse response, DateTimeOffset now, PollOptions options) => /* pure */ ...;
}
```

Test with a `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) and a `Theory` over status codes / rate-limit headers. The HTTP call lives in a thin `GitHubPoller` that takes `HttpClient`: test it with a mocked `HttpMessageHandler`.

### Closed reason enum + report assembly

```csharp
public enum JobFailureReason
{
    UnknownProject, UnknownAgent, Locked, Timeout, DlpFilter, ClaudeError, GitError, SchemaUnsupported, BudgetExceeded
}
```

Serialise as snake_case strings exactly as §6/§9/§14 name them (`unknown_project`, …). A test asserts the string of every member.

### Options / config binding

`node.json` binds to an options record in `Zyggy.Core` (validated with `IValidateOptions<T>`); `policy.yaml` is read through `IPolicySource`. Policy may only tighten `node.json`; write the merge as a pure function and test it.

### Node hosted service (`Zyggy.Node`)

```csharp
public sealed class PollLoop(IBusProvider bus, JobDispatcher dispatcher, TimeProvider clock, ILogger<PollLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) { /* poll → pull → process → sleep */ }
}
```

Keep the loop body in a testable method that takes the current `PollState` and returns the next; the `BackgroundService` only schedules it.

### CLI verb (`Zyggy.Cli`, System.CommandLine)

One file per verb under `src/Zyggy.Cli/Commands/<Verb>Command.cs`; the verb builds an envelope or reads the bus through `Zyggy.Core` types only. Exit code 0 on success, non-zero with a one-line error otherwise. The slice's integration test invokes the verb through its real entry point against the bare repo.

### Hub MCP tool (`Zyggy.Hub`, ModelContextProtocol SDK)

One tool per method, attributes from the official C# SDK; input validated; `remember` refuses secret patterns before touching disk. Verify the SDK's current attribute names and hosting API in its documentation before writing; do not code from memory.
