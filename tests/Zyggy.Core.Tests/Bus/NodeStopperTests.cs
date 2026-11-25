using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Unreachable = Zyggy.Core.Health.Unreachable;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 61 slice 7, AC-23 (unit half): <c>zyggy node stop</c> over a substituted process table — it ends only this user's <c>zyggy-node</c>
/// with the recorded start time, never another program on the recorded pid nor a process behind a foreign answer on the health port,
/// and names the remedy for Windows and Linux when it cannot end it.
/// </summary>
public sealed class NodeStopperTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ProcessStart = T.AddMinutes(-3);

    private static readonly Uri Url = new("http://localhost:4711/health");

    private static readonly HealthProbe Refused = new Unreachable("connection refused");

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly IProcessTable _table = Substitute.For<IProcessTable>();

    private readonly FakeTimeProvider _clock = new(T);

    private IDisposable? _heldLock;

    public NodeStopperTests() => Directory.CreateDirectory(Path.Combine(_checkout.Root, ".git"));

    public void Dispose()
    {
        _heldLock?.Dispose();
        _checkout.Dispose();
    }

    [Fact]
    public async Task Stop_LockFree_NoListenerRunningNothingEnded()
    {
        // Arrange: a stale record of a pid that runs another program now — but nothing holds the checkout
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.1", T));
        _table.Find(4242).Returns(new ProcessFacts(4242, "notepad", T, true));

        // Act
        NodeStopOutcome outcome = await Stopper().StopAsync(Refused, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Should().Be(new NodeStopOutcome(NodeStopResult.NotRunning, "no listener is running"));
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
    }

    [Fact]
    public async Task Stop_OwnNodeHealthAgrees_EndsTreeFreesLockClearsRecords()
    {
        // Arrange
        await HoldAsync();
        Live();
        ListenerOwner.WriteRefused(_checkout.Root, new RefusedStart(T, 4242));
        HealthProbe health = new Reachable(Health(pid: 4242, running: 2));

        // Act
        NodeStopOutcome outcome = await Stopper().StopAsync(health, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Should().Be(new NodeStopOutcome(NodeStopResult.Stopped, "stopped zyggy-node pid 4242 (2 run(s) interrupted; their claims resume at the next start)"));
        _table.Received(1).EndTree(4242, Arg.Any<TimeSpan>());
        (ListenerOwner.ReadOwner(_checkout.Root), ListenerOwner.ReadRefused(_checkout.Root)).Should().Be((null, null));
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("timeout")]
    public async Task Stop_OwnNodeHealthSilent_EndsOnProcessIdentity(string detail)
    {
        // Arrange: a hung node, or one still starting, answers nothing; the process identity alone decides
        await HoldAsync();
        Live();

        // Act
        NodeStopOutcome outcome = await Stopper().StopAsync(new Unreachable(detail), Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Should().Be(new NodeStopOutcome(NodeStopResult.Stopped, "stopped zyggy-node pid 4242 (0 run(s) interrupted; their claims resume at the next start)"));
    }

    // The recorded pid belongs to another program, another user's node, or a later process start: nothing is touched (exit 5)
    [Theory]
    [InlineData("chrome", true, 0)]
    [InlineData("zyggy-node", false, 0)]
    [InlineData("zyggy-node", true, 90)]
    public async Task Stop_RecordedPidNotThisUsersNode_ForeignNothingTouched(string name, bool mine, int startShiftMinutes)
    {
        // Arrange
        await HoldAsync();
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.1", T));
        _table.Find(4242).Returns(new ProcessFacts(4242, name, ProcessStart.AddMinutes(startShiftMinutes), mine));

        // Act
        NodeStopOutcome outcome = await Stopper().StopAsync(Refused, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Result.Should().Be(NodeStopResult.Foreign);
        outcome.Line.Should().StartWith("pid 4242 is not this user's zyggy-node started at 2026-11-24T09:57:00Z").And.EndWith("; nothing was touched");
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
        ListenerOwner.ReadOwner(_checkout.Root).Should().NotBeNull();
    }

    // A foreign listener on the health port: another pid, another machine's node, or something that is no node at all
    [Theory]
    [MemberData(nameof(ForeignAnswers))]
    public async Task Stop_ForeignAnswerOnHealthPort_ForeignNothingTouched(HealthProbe answer)
    {
        // Arrange
        await HoldAsync();
        Live();

        // Act
        NodeStopOutcome outcome = await Stopper().StopAsync(answer, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Result.Should().Be(NodeStopResult.Foreign);
        outcome.Line.Should().StartWith("the listener on http://localhost:4711/health is not zyggy-node pid 4242 of acme/home-laptop").And.EndWith("; nothing was touched");
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
    }

    public static TheoryData<HealthProbe> ForeignAnswers() =>
    [
        new Reachable(Health(pid: 777, running: 0)),
        new Reachable(Health(pid: null, running: 0)),
        new Reachable(Health(pid: 4242, running: 0) with { Machine = "work-laptop" }),
        new Unreachable("unparsable body"),
        new Unreachable("HTTP 404"),
    ];

    [Theory]
    [InlineData(true, "could not end zyggy-node pid 4242 (Win32Exception: Access is denied.); end it with: Stop-Process -Id 4242 -Force")]
    [InlineData(false, "could not end zyggy-node pid 4242 (Win32Exception: Access is denied.); end it with: kill 4242")]
    public async Task Stop_EndFails_FailedWithRemedyPerOs(bool windows, string line)
    {
        // Arrange
        await HoldAsync();
        Live();
        _table.EndTree(4242, Arg.Any<TimeSpan>()).Returns("Win32Exception: Access is denied.");

        // Act
        NodeStopOutcome outcome = await Stopper(windows: windows).StopAsync(Refused, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Should().Be(new NodeStopOutcome(NodeStopResult.Failed, line));
    }

    [Theory]
    [InlineData(true, "Stop-Process -Name zyggy-node -Force")]
    [InlineData(false, "pkill -u \"$(id -u)\" -x zyggy-node")]
    public async Task Stop_LockHeldNoRecord_FailedNamesTheRemedy(bool windows, string remedy)
    {
        // Arrange: a node before 0.30.1 holds the checkout and wrote no record
        await HoldAsync();

        // Act
        NodeStopOutcome outcome = await Stopper(windows: windows).StopAsync(Refused, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Should().Be(new NodeStopOutcome(
            NodeStopResult.Failed,
            "the bus checkout is held by a listener that left no owner record (a zyggy-node before 0.30.1); end it with: " + remedy));
        _table.DidNotReceiveWithAnyArgs().EndTree(default, default);
    }

    [Fact]
    public async Task Stop_EndedButLockStillHeld_Failed()
    {
        // Arrange: the end call reports success, the lock stays taken
        await HoldAsync();
        Live();
        _table.EndTree(4242, Arg.Any<TimeSpan>()).Returns((string?)null);

        // Act
        NodeStopOutcome outcome = await Stopper(releaseOnEnd: false).StopAsync(Refused, Acme, HomeLaptop, Url, TestContext.Current.CancellationToken);

        // Assert
        outcome.Should().Be(new NodeStopOutcome(NodeStopResult.Failed, "zyggy-node pid 4242 was ended but the bus checkout is still held; end it with: kill 4242"));
    }

    private static NodeHealth Health(int? pid, int running) =>
        new("0.30.1", "acme", "home-laptop", T, null, T, "0123", running, running, null, 4999, null, true, null, pid, T);

    private void Live()
    {
        ListenerOwner.WriteOwner(_checkout.Root, new ListenerRecord(4242, ProcessStart, "0.30.1", T));
        _table.Find(4242).Returns(new ProcessFacts(4242, "zyggy-node", ProcessStart.AddMilliseconds(300), true));
        _table.EndTree(4242, Arg.Any<TimeSpan>()).Returns((string?)null);
    }

    private async Task HoldAsync() =>
        _heldLock = await BusCheckoutLock.AcquireAsync(_checkout.Root, BusCheckoutLock.ListenLockName, TimeSpan.Zero, _clock, TestContext.Current.CancellationToken);

    private NodeStopper Stopper(bool windows = false, bool releaseOnEnd = true)
    {
        if (releaseOnEnd)
        {
            _table.When(t => t.EndTree(4242, Arg.Any<TimeSpan>())).Do(_ => _heldLock?.Dispose());
        }

        return new NodeStopper(_checkout.Root, _table, _clock, lockWait: TimeSpan.Zero, windows: windows);
    }
}
