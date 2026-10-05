using System.Runtime.InteropServices;

namespace Zyggy.Core.Processes;

/// <summary>
/// Turns SIGTERM and SIGINT into a cancellation, as the scripts' traps did: the signal's default end is cancelled so the run can kill
/// its model process and remove its run directory, and the exit code the shell would have given (143 SIGTERM, 130 SIGINT) is recorded.
/// The first signal wins.
/// </summary>
internal sealed class SignalCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly PosixSignalRegistration[] _registrations;
    private int _exitCode;

    public SignalCancellation(CancellationToken cancellationToken)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
        ];
    }

    /// <summary>Gets the token cancelled by the first signal or by the caller.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets 143 after a SIGTERM, 130 after a SIGINT; <see langword="null"/> while no signal arrived.</summary>
    public int? ExitCode => Volatile.Read(ref _exitCode) is var code and not 0 ? code : null;

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _source.Dispose();
    }

    // Internal for tests: a PosixSignalContext can be built without sending a signal to the test host.
    internal void Handle(PosixSignalContext context)
    {
        context.Cancel = true;
        Interlocked.CompareExchange(ref _exitCode, context.Signal == PosixSignal.SIGINT ? 130 : 143, 0);
        _source.Cancel();
    }
}
