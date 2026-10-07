using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.M365;
using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Runs a whole <c>zyggy m365 &lt;verb&gt;</c> inside the test (I-in-process, plan 33 Shared rules): real files, real key files, the real
/// process runner — only the Graph <see cref="HttpMessageHandler"/> is the stub. The binary has no test switch, so Graph-dependent
/// paths are proven here.
/// </summary>
internal static class M365InProcess
{
    public static async Task<(int Exit, VerbConsole Console)> RunAsync(
        IReadOnlyDictionary<string, string?> environment,
        StubGraphHandler graph,
        string[] args,
        FakeTimeProvider? clock = null,
        string? stdin = null,
        Func<string, IModelRunner>? model = null,
        CancellationToken? cancellationToken = null)
    {
        var console = new VerbConsole(stdin);
        var host = new M365VerbHost(
            environment,
            clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero)),
            FindTimeZone,
            new ProcessRunner(TimeProvider.System),
            graph,
            checkKeyOwnership: OperatingSystem.IsLinux(),
            modelRunnerFactory: model,
            checkBinaryPin: false);
        var exit = await host.RunAsync(args, console.Io, cancellationToken ?? CancellationToken.None);
        return (exit, console);
    }

    // IANA ids do not resolve off Linux in an invariant-globalization build; late-September Brussels is +02:00.
    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels" && !OperatingSystem.IsLinux()
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);
}

/// <summary>Runs a whole <c>zyggy brief &lt;verb&gt;</c> inside the test with the stubbed Graph handler (the binary has no test switch).</summary>
internal static class BriefInProcess
{
    public static async Task<(int Exit, VerbConsole Console)> RunAsync(
        IReadOnlyDictionary<string, string?> environment, StubGraphHandler graph, string[] args, FakeTimeProvider clock)
    {
        var console = new VerbConsole();
        var host = new Zyggy.Core.Brief.BriefVerbHost(environment, clock, TimeZoneInfo.FindSystemTimeZoneById, graph, checkKeyOwnership: OperatingSystem.IsLinux());
        var exit = await host.RunAsync(args, console.Io, CancellationToken.None);
        return (exit, console);
    }
}
