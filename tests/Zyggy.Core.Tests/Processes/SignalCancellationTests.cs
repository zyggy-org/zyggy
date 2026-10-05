using System.Runtime.InteropServices;

using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Processes;

public sealed class SignalCancellationTests
{
    [Theory]
    [InlineData(PosixSignal.SIGTERM, 143)]
    [InlineData(PosixSignal.SIGINT, 130)]
    public void Handle_Signal_CancelsDefaultEndCancelsTokenRecordsExit(PosixSignal signal, int exit)
    {
        // Arrange
        using var signals = new SignalCancellation(TestContext.Current.CancellationToken);
        var context = new PosixSignalContext(signal);

        // Act
        signals.Handle(context);

        // Assert
        context.Cancel.Should().BeTrue();
        signals.Token.IsCancellationRequested.Should().BeTrue();
        signals.ExitCode.Should().Be(exit);
    }

    [Fact]
    public void Handle_SecondSignal_FirstWins()
    {
        // Arrange
        using var signals = new SignalCancellation(TestContext.Current.CancellationToken);
        signals.Handle(new PosixSignalContext(PosixSignal.SIGTERM));

        // Act
        signals.Handle(new PosixSignalContext(PosixSignal.SIGINT));

        // Assert
        signals.ExitCode.Should().Be(143);
    }

    [Fact]
    public void ExitCode_NoSignal_NullAndCallerCancellationFlowsThrough()
    {
        // Arrange
        using var caller = new CancellationTokenSource();
        using var signals = new SignalCancellation(caller.Token);

        // Act
        caller.Cancel();

        // Assert
        signals.Token.IsCancellationRequested.Should().BeTrue();
        signals.ExitCode.Should().BeNull();
    }
}
