using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Brief;
using Zyggy.Core.M365;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Brief;

/// <summary>A temporary state root with a brief directory and an m365 directory, a fake clock at 07:00 Brussels on 2026-10-06 and the Test/Brussels zone.</summary>
internal sealed class BriefFixture : IDisposable
{
    public static readonly DateOnly Today = new(2026, 10, 6);

    public BriefFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, "state", "brief"));
        Directory.CreateDirectory(Path.Combine(Root, "state", "m365"));
        Directory.CreateDirectory(Path.Combine(Root, "instance"));
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(Root, "home"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(Root, "state"),
            ["ZYGGY_TIMEZONE"] = "Europe/Brussels",
            ["ZYGGY_INSTANCE_DIR"] = Path.Combine(Root, "instance"),
        };
        Paths = new BriefPaths(Environment);
        Store = new BriefStore(Paths);
        M365 = new M365Paths(Environment);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public Dictionary<string, string?> Environment { get; }

    public BriefPaths Paths { get; }

    public BriefStore Store { get; }

    public M365Paths M365 { get; }

    /// <summary>07:00 Europe/Brussels (+02:00 in the test zone) on 2026-10-06.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 6, 5, 0, 0, TimeSpan.Zero));

    public TimeZoneInfo Zone { get; } = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");

    public static string Golden(string name) => Path.Combine(Infrastructure.Golden.Directory, "brief", name);

    public static string GoldenText(string name) => Encoding.UTF8.GetString(File.ReadAllBytes(Golden(name)));

    public TimeZoneInfo FindTimeZone(string id) => id == "Europe/Brussels" ? Zone : throw new TimeZoneNotFoundException(id);

    public BriefShow Show(BriefSettings? settings = null) => new(Store, settings ?? BriefSettings.Defaults, Zone, Clock, M365);

    /// <summary>A show whose clock reads <paramref name="utc"/> (a fake clock cannot be turned back).</summary>
    public BriefShow ShowAt(DateTimeOffset utc) => new(Store, BriefSettings.Defaults, Zone, new FakeTimeProvider(utc), M365);

    /// <summary>The golden weekday brief as <c>brief-&lt;date&gt;.md</c>, with a sidecar whose <c>generated</c> is 04:31Z of that date.</summary>
    public void WriteGoldenBrief(DateOnly date)
    {
        File.Copy(Golden("brief-weekday.md"), Paths.Markdown(date), overwrite: true);
        File.WriteAllText(Paths.Sidecar(date), $$"""{"schema":1,"date":"{{BriefPaths.Iso(date)}}","generated":"{{BriefPaths.Iso(date)}}T04:31:00Z"}""");
    }

    public void WriteWatermark(string iso) => File.WriteAllText(M365.StateFile("mail-watermark"), iso + "\n");

    public void WriteBriefJsonlRow(string row) => File.AppendAllText(M365.StateFile("brief.jsonl"), row + "\n");

    public void WriteLastShown(string text) => File.WriteAllText(Paths.LastShown, text);

    public async Task<(int Exit, VerbConsole Console)> RunAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await new BriefVerbHost(Environment, Clock, FindTimeZone).RunAsync(["show", .. args], console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    /// <summary>Runs <c>zyggy brief &lt;args&gt;</c> in process (any verb).</summary>
    public async Task<(int Exit, VerbConsole Console)> RunBriefAsync(params string[] args)
    {
        var console = new VerbConsole();
        var exit = await new BriefVerbHost(Environment, Clock, FindTimeZone).RunAsync(args, console.Io, TestContext.Current.CancellationToken);
        return (exit, console);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A locked file in a fault test: the temp folder is cleaned by the OS.
        }
    }
}
