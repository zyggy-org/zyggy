using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Brief;
using Zyggy.Core.M365;

namespace Zyggy.Core.Tests.M365;

/// <summary>The stubbed Graph of 33 plus a temporary m365 state directory, a clock at 10:30 Brussels on 2026-10-06 and the Test/Brussels zone.</summary>
internal sealed class PrepassFixture : IDisposable
{
    public PrepassFixture(string? configPatch = null)
    {
        Graph = new GraphFixture(configPatch);
        Directory.CreateDirectory(Path.Combine(Root, "state", "m365"));
        Paths = new M365Paths(new Dictionary<string, string?> { ["ZYGGY_STATE_DIR"] = Path.Combine(Root, "state"), ["HOME"] = Root });
        File.WriteAllText(Paths.StateFile("mail-watermark"), "2026-10-06T04:30:00Z\n");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public GraphFixture Graph { get; }

    public M365Paths Paths { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 6, 8, 30, 0, TimeSpan.Zero));

    public TimeZoneInfo Zone { get; } = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");

    public MailPrepass Prepass(BriefSettings? settings = null) => new(Graph.Reader, Paths, Graph.Configuration, settings ?? BriefSettings.Defaults, Zone, Clock);

    /// <summary>A 33-shaped receipt <c>m365/brief-&lt;date&gt;.json</c> with the given drafts (<c>id</c>, <c>kind</c>).</summary>
    public void WriteReceipt(string date, params (string Id, string Kind)[] drafts)
    {
        var items = string.Join(",", drafts.Select(d => $$"""{"id":"{{d.Id}}","kind":"{{d.Kind}}","subject":"RE: x","recipients":[]}"""));
        File.WriteAllText(Paths.StateFile($"brief-{date}.json"), $$"""{"date":"{{date}}","window_start":"{{date}}T04:30:00Z","drafts":[{{items}}],"replied_ids":[],"audit":"ok","reasons":[]}""");
    }

    public void Dispose()
    {
        Graph.Stub.Violations.Should().BeEmpty();
        Graph.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
