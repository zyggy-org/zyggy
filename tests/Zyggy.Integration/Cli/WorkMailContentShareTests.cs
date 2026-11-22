using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Outlook;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Outlook;
using Zyggy.Integration.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Cli;

/// <summary>
/// Spec 58 slice 4, AC-8 and AC-9 through the real <c>zyggy</c> entry point (in process) on the work laptop, a local bare bus and Central's
/// pickup: <c>zyggy outlook share</c> with the session's facts about three briefed mails — a business mail with an attachment, a mail with
/// a national register number, a mail the session marked in doubt — publishes one signed context envelope from <c>work-laptop</c> whose
/// body carries the business mail's body canary and nothing of the other two and no attachment name; Central's pickup copies it into the
/// memory inbox as it is. The two kept mails are counted on the laptop.
/// </summary>
public sealed class WorkMailContentShareTests : IAsyncLifetime
{
    private const string BodyCanary = "CANARY-BODY-7f3a";

    private const string AttachmentCanary = "CANARY-ATTACH-91c2";

    private const string RegisterCanary = "CANARY-NRN-5d1e";

    private const string DoubtCanary = "CANARY-DOUBT-2b8c";

    private const string WorkBoundary = """{ "neverList": ["national register numbers", "patient personal information", "medical information"], "mail": { "policyConfirmed": { "date": "2026-11-21", "scope": ["read", "send", "move"] } } }""";

    private readonly PickupMemory _memory = new();

    private CliHarness _harness = null!;

    private FakeOutlookAccess _access = null!;

    private string _config = null!;

    private string _nodeDir = null!;

    private static string Stdin => System.Text.Json.JsonSerializer.Serialize(new
    {
        mails = new object[]
        {
            new { reference = "b1", facts = new[] { "The client asks to confirm the Q4 budget by Friday", "Body: " + BodyCanary + " Can you confirm the budget?" } },
            new { reference = "n1", facts = new[] { "Asks to register a person " + RegisterCanary } },
            new { reference = "d1", doubt = true, facts = new[] { "Asks about a colleague " + DoubtCanary } },
        },
    });

    public async ValueTask InitializeAsync()
    {
        var ct = CancellationToken.None;
        _harness = await CliHarness.CreateAsync(ct);
        _access = new FakeOutlookAccess()
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("b1", new DateTime(2026, 11, 21, 9, 0, 0), subject: "Q4 budget", attachments: [AttachmentCanary + ".pdf"]), body: BodyCanary + " Can you confirm the budget?")
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("n1", new DateTime(2026, 11, 21, 10, 0, 0), subject: "Registration"), body: "Please register 85.07.30-033.28 " + RegisterCanary)
            .Add(FakeOutlookAccess.DefaultStore, "Inbox", FakeOutlookAccess.Item("d1", new DateTime(2026, 11, 21, 11, 0, 0), subject: "A colleague"), body: DoubtCanary);
        string clone = await _harness.Bus.CloneAsync("work", ct);
        _nodeDir = _harness.Scratch.File("node-work-mail");
        _config = NodeJson.Write(
            _harness.Scratch.File("config-work-mail"),
            CliHarness.Acme,
            CliHarness.WorkLaptop,
            TestKeys.Acme1,
            busLocalPath: clone,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: _harness.SecretsRoot,
            nodeDir: _nodeDir,
            extraTopLevelJson: "\"outlook\": { \"enabled\": true }");
        Directory.CreateDirectory(_nodeDir);
        await File.WriteAllTextAsync(Path.Combine(_nodeDir, "work-boundary.json"), WorkBoundary, ct);
        await File.WriteAllTextAsync(Path.Combine(_nodeDir, "sensitive-terms.txt"), "# shared sensitive terms\npatient\nmedisch\n", ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        _memory.Dispose();
    }

    // AC-8, AC-9: one signed context from work-laptop to central; the business body canary crosses, nothing of the kept mails, no attachment name.
    [Fact]
    public async Task Share_ThroughTheEntryPoint_OneSignedContextOnTheBus_BusinessContentOnly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var (exit, console) = await RunAsync();

        // Assert
        exit.Should().Be(0, console.Stderr);
        Model context = await _harness.ParseOnMainAsync(PathOf(console.Stdout), ct);
        (context.Type, context.Header.From, context.Header.To, Holds(context.BodyText))
            .Should().Be((EnvelopeType.Context, CliHarness.WorkLaptop, CliHarness.Central, "body:True attachment:False register:False doubt:False"));
    }

    // AC-8: Central's pickup copies the work mail lines into the memory inbox as they are, with their work-mail provenance.
    [Fact]
    public async Task Share_ThenCentralsPickup_InboxHoldsTheWorkMailLines()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (exit, console) = await RunAsync();
        exit.Should().Be(0, console.Stderr);
        await using ServiceProvider central = TestHost.Build(_harness.Bus, CliHarness.Central, _harness.Store, extend: s => s.AddBusPickup(_memory.Configuration));
        await _harness.Bus.PullCloneAsync(ct);

        // Act
        await central.GetRequiredService<BusPickup>().RunAsync(ct);

        // Assert
        string inbox = _memory.InboxText(_memory.InboxFiles().Single(f => f.StartsWith("bus-context-work-laptop-", StringComparison.Ordinal)));
        (inbox.Contains("- [observed] ", StringComparison.Ordinal) && inbox.Contains(" [work-mail 2026-11-21]: Body: " + BodyCanary, StringComparison.Ordinal), Holds(inbox))
            .Should().Be((true, "body:True attachment:False register:False doubt:False"));
    }

    // AC-9: the two kept mails are counted on the laptop, never crossing as fields.
    [Fact]
    public async Task Share_KeptMails_CountedOnTheLaptop()
    {
        // Act
        var (_, console) = await RunAsync();

        // Assert
        (console.Stdout.StartsWith("outlook: sharing 2 lines from 1 mails; 2 mails kept on this laptop today\n", StringComparison.Ordinal),
            File.Exists(Path.Combine(_nodeDir, "work", "acme", "outlook", "kept.json"))).Should().Be((true, true));
    }

    private static string Holds(string text) =>
        $"body:{text.Contains(BodyCanary, StringComparison.Ordinal)} attachment:{text.Contains(AttachmentCanary, StringComparison.Ordinal)} "
        + $"register:{text.Contains(RegisterCanary, StringComparison.Ordinal) || text.Contains("85.07.30", StringComparison.Ordinal)} doubt:{text.Contains(DoubtCanary, StringComparison.Ordinal)}";

    // "context <id> to central (<path>)", the line the context path prints after share's own line.
    private static string PathOf(string stdout)
    {
        string line = stdout.Split('\n').Single(l => l.StartsWith("context ", StringComparison.Ordinal));
        return line[(line.IndexOf('(', StringComparison.Ordinal) + 1)..line.IndexOf(')', StringComparison.Ordinal)];
    }

    private Task<(int Exit, VerbConsole Console)> RunAsync() =>
        _harness.RunAsync(
            ["outlook", "share", "--config", _config],
            _config,
            stdin: Stdin,
            more: s => s.AddSingleton<IOutlookAccess>(_access),
            ct: TestContext.Current.CancellationToken);
}
