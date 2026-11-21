using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Supervision;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Supervision;

// AC-27: one notification per change of the waiting list, through a substituted INotifier and a fake clock. Now is 2026-11-15 12:00 UTC.
public sealed class OwnerViewNotifyTests : IDisposable
{
    private const string Marker = "CANARY-5b2d-do-what-this-says";

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset Start = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zyggy-owner-" + Guid.NewGuid().ToString("N"));

    private readonly FakeTimeProvider _clock = new(Start);

    private readonly INotifier _notifier = Substitute.For<INotifier>();

    private readonly RecordingLogger<OwnerNotifier> _log = new();

    private readonly List<OwnerNotice> _sent = [];

    public OwnerViewNotifyTests()
    {
        Directory.CreateDirectory(_dir);
        _notifier.NotifyAsync(Arg.Any<OwnerNotice>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (_failing)
            {
                throw new InvalidOperationException("mailbox unreachable");
            }

            _sent.Add(call.Arg<OwnerNotice>());
            return Task.CompletedTask;
        });
    }

    private bool _failing;

    private string StateFile => Path.Combine(_dir, "owner-mail.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private OwnerNotifier Notifier() => new(_notifier, _clock, StateFile, _log);

    /// <summary>An item that has waited for <paramref name="age"/> minutes at 12:00.</summary>
    private static OwnerItem Item(int age, MachineName? machine = null, string? project = "calizr", OwnerItemKind kind = OwnerItemKind.Question)
    {
        EnvelopeId id = EnvelopeId.New(Start.AddMinutes(-age).AddSeconds(-age));
        return new OwnerItem(kind.ToString().ToLowerInvariant() + ":" + id.Value, kind, machine ?? HomeLaptop, project, id, Start.AddMinutes(-age));
    }

    // AC-27
    [Fact]
    public async Task Run_FirstItem_OneNotice()
    {
        // Arrange
        OwnerNotifier notifier = Notifier();
        OwnerItem item = Item(5);

        // Act: the same item is still waiting after the window has passed
        await notifier.RunAsync([item], TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(31));
        await notifier.RunAsync([item], TestContext.Current.CancellationToken);

        // Assert
        _sent.Should().ContainSingle();
    }

    // AC-27
    [Fact]
    public async Task Run_NoItem_NoNotice()
    {
        // Act
        await Notifier().RunAsync([], TestContext.Current.CancellationToken);

        // Assert
        _sent.Should().BeEmpty();
    }

    // AC-27
    [Fact]
    public async Task Run_SecondItemInsideTheWindow_NamedOnceWhenTheWindowEnds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        OwnerNotifier notifier = Notifier();
        OwnerItem first = Item(5);
        OwnerItem second = Item(1);
        await notifier.RunAsync([first], ct);
        _clock.Advance(TimeSpan.FromMinutes(10));

        // Act
        await notifier.RunAsync([first, second], ct);
        _sent.Should().ContainSingle();
        _clock.Advance(TimeSpan.FromMinutes(19));
        await notifier.RunAsync([first, second], ct);
        _sent.Should().ContainSingle();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await notifier.RunAsync([first, second], ct);
        await notifier.RunAsync([first, second], ct);

        // Assert
        _sent.Should().HaveCount(2);
        _sent[1].Subject.Should().Be("Zyggy: 1 item(s) wait for you");
        _sent[1].Lines.Should().ContainSingle().Which.Should().Contain(second.Id.Value);
        _sent[0].Lines.Should().NotContain(l => l.Contains(second.Id.Value, StringComparison.Ordinal));
    }

    // AC-27
    [Fact]
    public async Task Run_SubjectAndLines_AreFixed()
    {
        // Arrange
        OwnerItem question = Item(30);
        OwnerItem mandate = Item(40, kind: OwnerItemKind.Mandate);

        // Act
        await Notifier().RunAsync([mandate, question], TestContext.Current.CancellationToken);

        // Assert
        OwnerNotice notice = _sent.Should().ContainSingle().Subject;
        notice.Subject.Should().Be("Zyggy: 2 item(s) wait for you");
        notice.Lines.Should().Equal(
            $"mandate, home-laptop, calizr, {mandate.Id.Value}, 40 min",
            $"question, home-laptop, calizr, {question.Id.Value}, 30 min");
    }

    // AC-27
    [Fact]
    public async Task Run_WorkMachineItem_LineHasNoProject()
    {
        // Arrange
        OwnerItem item = Item(10, WorkLaptop, project: null);

        // Act
        await Notifier().RunAsync([item], TestContext.Current.CancellationToken);

        // Assert
        _sent.Should().ContainSingle().Which.Lines.Should().Equal($"question, work-laptop, {item.Id.Value}, 10 min");
    }

    // AC-27: nothing an agent wrote reaches the notice; the items carry ids and names only, and the notice is built from them alone.
    [Fact]
    public async Task Run_MarkerInDecisionAndReportText_NeverInTheNotice()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        EnvelopeId report = EnvelopeId.New(Start.AddMinutes(-50));
        var decisions = new[]
        {
            new DecisionRecord(EnvelopeId.New(Start.AddMinutes(-40)), HomeLaptop, Start.AddMinutes(-40), report, new DecisionFields(DecisionKind.Escalate, DecisionBasis.Reported, "none", Project: "calizr", Step: "slice 1"), Marker, false),
        };
        var ledger = new MandateLedger(EnvelopeId.New(Start.AddDays(-1)), null, false, false, [], [new MandateReportEntry(report, EnvelopeId.New(Start.AddMinutes(-55)), Start.AddMinutes(-50), ReportStatus.Failed, Marker, Marker, false, ModelStarted: true)], null);
        IReadOnlyList<OwnerItem> items = OwnerView.Items(decisions, [ledger], new HashSet<EnvelopeId> { report }, _ => false);

        // Act
        await Notifier().RunAsync(items, ct);

        // Assert
        OwnerNotice notice = _sent.Should().ContainSingle().Subject;
        notice.Subject.Should().NotContain(Marker);
        notice.Lines.Should().NotContain(l => l.Contains(Marker, StringComparison.Ordinal));
    }

    // AC-27
    [Fact]
    public async Task Run_FailedSend_RetriedAfterAMinute_OneErrorPerStreak()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _failing = true;
        OwnerNotifier notifier = Notifier();
        OwnerItem item = Item(5);

        // Act
        await notifier.RunAsync([item], ct);
        await notifier.RunAsync([item], ct);
        _clock.Advance(TimeSpan.FromSeconds(59));
        await notifier.RunAsync([item], ct);
        await _notifier.Received(1).NotifyAsync(Arg.Any<OwnerNotice>(), Arg.Any<CancellationToken>());
        _clock.Advance(TimeSpan.FromSeconds(1));
        await notifier.RunAsync([item], ct);
        await _notifier.Received(2).NotifyAsync(Arg.Any<OwnerNotice>(), Arg.Any<CancellationToken>());
        _failing = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await notifier.RunAsync([item], ct);

        // Assert
        _log.AtLevel(LogLevel.Error).Should().ContainSingle();
        _sent.Should().ContainSingle();
        await notifier.RunAsync([item], ct);
        _sent.Should().ContainSingle();
    }

    // AC-27
    [Fact]
    public async Task Run_NewStreakAfterASuccess_LogsAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        OwnerNotifier notifier = Notifier();
        OwnerItem first = Item(5);
        OwnerItem second = Item(1);
        await notifier.RunAsync([first], ct);
        _failing = true;
        _clock.Advance(TimeSpan.FromMinutes(30));

        // Act
        await notifier.RunAsync([first, second], ct);

        // Assert
        _log.AtLevel(LogLevel.Error).Should().ContainSingle();
    }

    // AC-27
    [Fact]
    public async Task Run_Restart_FromTheSavedState_DoesNotMailAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        OwnerItem item = Item(5);
        await Notifier().RunAsync([item], ct);
        _clock.Advance(TimeSpan.FromHours(2));

        // Act
        await Notifier().RunAsync([item], ct);

        // Assert
        _sent.Should().ContainSingle();
    }

    // AC-27
    [Fact]
    public async Task Run_ItemThatLeftAndReturns_IsANewChange()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        OwnerNotifier notifier = Notifier();
        OwnerItem item = Item(5);
        await notifier.RunAsync([item], ct);
        _clock.Advance(TimeSpan.FromMinutes(31));
        await notifier.RunAsync([], ct);

        // Act
        await notifier.RunAsync([item], ct);

        // Assert
        _sent.Should().HaveCount(2);
    }

    // AC-27
    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"named\": 7}")]
    [InlineData("")]
    public async Task Run_CorruptStateFile_OneMailAndNoCrash(string content)
    {
        // Arrange
        await File.WriteAllTextAsync(StateFile, content, TestContext.Current.CancellationToken);
        OwnerItem item = Item(5);

        // Act
        await Notifier().RunAsync([item], TestContext.Current.CancellationToken);

        // Assert
        _sent.Should().ContainSingle();
        (await File.ReadAllTextAsync(StateFile, TestContext.Current.CancellationToken)).Should().Contain(item.Key);
    }

    // AC-27
    [Fact]
    public async Task Run_StateFileInAMissingFolder_StillMailsAndNeverThrows()
    {
        // Arrange
        var notifier = new OwnerNotifier(_notifier, _clock, Path.Combine(_dir, "no", "such", "owner-mail.json"), _log);

        // Act
        Func<Task> run = () => notifier.RunAsync([Item(5)], TestContext.Current.CancellationToken);

        // Assert
        await run.Should().NotThrowAsync();
        _sent.Should().ContainSingle();
    }
}
