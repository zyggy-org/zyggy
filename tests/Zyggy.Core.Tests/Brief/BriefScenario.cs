using System.Text.Json;

using Zyggy.Core.Brief;
using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Brief;

/// <summary>
/// The 10-mail scenario of spec 35 Step 5 (plus one mail the model omitted and one discard item), in Europe/Brussels (+02:00) on
/// 2026-10-06: the pre-pass result, the message locations the run gathered, and the model's answer from <c>golden/brief/mail-output-10.json</c>.
/// </summary>
internal static class BriefScenario
{
    public const string Inbox = "AQMkInbox0001";
    public const string Drafts = "AQMkDrafts0001";
    public const string Owner = "Alice Example";
    public const string Watermark = "2026-10-05T04:30:00Z";

    public static readonly DateOnly Date = new(2026, 10, 6);
    public static readonly DateTimeOffset Generated = new(2026, 10, 6, 4, 31, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");

    public static SecretPatterns Secrets { get; } = SecretPatterns.Load(Path.Combine(Infrastructure.Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    public static PrepassResult Prepass() => new(
        Watermark,
        [
            Mail("m01", "2026-10-06T05:12:00Z", "Carol Example", "carol@example.org", "Invoice 2026-41", "c01", true),
            Mail("m02", "2026-10-06T06:05:00Z", "Bob Example", "bob@example.org", "Lunch on Thursday?", "c02", false),
            Mail("m03", "2026-10-06T06:40:00Z", "Dana Example", "dana@example.org", "Q3 report draft", "c03", true, "09:10"),
            Mail("m04", "2026-10-06T07:15:00Z", "Erin Example", "erin@example.org", "Team offsite dates", "c04", false),
            Mail("m05", "2026-10-06T07:30:00Z", "Weekly Newsletter", "news@example.org", "Weekly digest", "c05", false),
            Mail("m06", "2026-10-06T07:45:00Z", "Shop Example", "shop@example.org", "Your order has shipped", "c06", false),
            Mail("m07", "2026-10-06T08:00:00Z", "Gus Example", "gus@example.org", "Statement September", "c07", true),
            Mail("m08", "2026-10-06T08:20:00Z", "Hal Example", "hal@example.org", "Old quote", "c08", false),
            Mail("m09", "2026-10-06T08:35:00Z", "Ivy Example", "ivy@example.org", "Can you call me?", "c09", false),
            Mail("m10", "2026-10-06T08:50:00Z", "Jon Example", "jon@example.org", "Subscription renewal notice", "c10", true),
            Mail("m11", "2026-10-06T09:05:00Z", "Kim Example", "kim@example.org", "FYI minutes", "c11", false),
        ],
        [new DiscardItem("d09", "RE: Old thread", "08:00")]);

    public static Dictionary<string, MessageLocation> Locations() => new(StringComparer.Ordinal)
    {
        ["m05"] = Location("m05", Inbox, "c05"),
        ["m06"] = Location("m06", Inbox, "c06"),
        ["m07"] = Location("m07", Inbox, "c07"),
        ["m08"] = Location("m08", Inbox, "c08"),
        ["d02"] = Location("d02", Drafts, "c02"),
        ["d03"] = Location("d03", Drafts, "c03"),
    };

    public static ValidationContext Context(int suggestionCap = 10, IReadOnlyDictionary<string, MessageLocation>? locations = null) =>
        new(Date, Generated, Inbox, Drafts, Owner, locations ?? Locations(), Secrets, suggestionCap, Zone);

    public static MailRunOutput Output()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Infrastructure.Golden.Directory, "brief", "mail-output-10.json")));
        var (output, rejection) = MailRunOutput.TryParse(document.RootElement.Clone());
        return output ?? throw new InvalidOperationException(rejection);
    }

    public static BriefDocument Document(int suggestionCap = 10)
    {
        var (document, rejection) = MailRunValidator.Validate(Output(), Prepass(), Context(suggestionCap), Watermark);
        return document ?? throw new InvalidOperationException(rejection);
    }

    /// <summary>Parses a model answer written as JSON text in a test.</summary>
    public static (MailRunOutput? Output, string? Rejection) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MailRunOutput.TryParse(document.RootElement.Clone());
    }

    public static PrepassMail Mail(string id, string received, string name, string address, string subject, string conversation, bool attachments, string? answered = null) =>
        new(new InboxMessage(id, subject, name, address, received, conversation, attachments), answered);

    public static MessageLocation Location(string id, string folder, string conversation, string received = "2026-10-06T05:00:00Z") =>
        new(true, id, folder, conversation, "RE: x", Owner, received);

    /// <summary>A generated brief with <paramref name="urgent"/> urgent, <paramref name="important"/> important and <paramref name="other"/> other mails plus <paramref name="files"/> files, every important mail with a Z move and a "you" line.</summary>
    public static BriefDocument Generated_(int urgent, int important, int other, int files, int seed = 1, bool actions = true)
    {
        var random = new Random(seed);
        var mails = new List<PrepassMail>();
        var entries = new List<string>();
        var locations = new Dictionary<string, MessageLocation>(StringComparer.Ordinal);
        var n = 0;
        void Add(string cls, string action)
        {
            n++;
            var id = $"g{n:000}";
            var received = new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero).AddMinutes(n * 3 + random.Next(0, 2)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            mails.Add(Mail(id, received, $"Sender {n:000}", $"s{n}@example.org", $"Subject {n:000} " + new string('s', random.Next(0, 30)), $"c{n}", false));
            locations[id] = Location(id, Inbox, $"c{n}");
            var summary = "Summary " + new string('x', 40 + random.Next(0, 80));
            entries.Add(action switch
            {
                "z" => $$"""{"id":"{{id}}","class":"{{cls}}","summary":"{{summary}}","action":"z","z":{"kind":"move","destination":"archive","why":"why {{n}}"} }""",
                "you" => $$"""{"id":"{{id}}","class":"{{cls}}","summary":"{{summary}}","action":"you","you":{"kind":"other","action":"do thing {{n}}","why":"why {{n}}"} }""",
                _ => $$"""{"id":"{{id}}","class":"{{cls}}","summary":"{{summary}}","action":"nothing"}""",
            });
        }

        for (var i = 0; i < urgent; i++)
        {
            Add("urgent", i % 2 == 0 ? "z" : "you");
        }

        for (var i = 0; i < important; i++)
        {
            Add("important", actions ? (i % 3) switch { 0 => "z", 1 => "you", _ => "nothing" } : "nothing");
        }

        for (var i = 0; i < other; i++)
        {
            Add("other", "nothing");
        }

        var fileEntries = Enumerable.Range(1, files).Select(i =>
            $$"""{"name":"file-{{i:00}}.docx","drive":"OneDrive","folder":"/Work","modified":"2026-10-06T0{{(i % 9) + 1}}:00:00Z","by":"Colleague {{i}}","about":"about {{i}} {{new string('a', 20 + random.Next(0, 60))}}","youAction":"look at it"}""");
        var json = $$"""{"mail":[{{string.Join(",", entries)}}],"files":[{{string.Join(",", fileEntries)}}],"replies":0,"facts":0}""";
        var (output, rejection) = Parse(json);
        var prepass = new PrepassResult(Watermark, mails, []);
        var (document, rejection2) = MailRunValidator.Validate(output ?? throw new InvalidOperationException(rejection), prepass, Context(1000, locations), Watermark);
        return document ?? throw new InvalidOperationException(rejection2);
    }
}
