using Zyggy.Core.M365;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>Runs <c>zyggy m365 &lt;verb&gt; …</c> in process through <see cref="M365VerbHost"/>, with a console the test can read.</summary>
internal static class M365Run
{
    public static async Task<(int Exit, VerbConsole Console)> RunAsync(
        IReadOnlyDictionary<string, string?> environment, TimeProvider clock, string[] args, string? stdin = null)
    {
        var console = new VerbConsole(stdin);
        var exit = await new M365VerbHost(environment, clock, FindTimeZone).RunAsync(args, console.Io, CancellationToken.None);
        return (exit, console);
    }

    public static string Golden(params string[] parts) => Path.Combine([Infrastructure.Golden.Directory, "m365", .. parts]);

    // Europe/Brussels in late September is CEST (+02:00); IANA ids do not resolve off Linux in an invariant-globalization build.
    private static TimeZoneInfo FindTimeZone(string id) =>
        id == "Europe/Brussels"
            ? TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels")
            : TimeZoneInfo.FindSystemTimeZoneById(id);
}
