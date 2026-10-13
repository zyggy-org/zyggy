using Zyggy.Core.Processes;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// An <see cref="IProcessRunner"/> decorator over the production runner (the integration-testing skill's "inject a hook through
/// decoration"): <see cref="BeforeRun"/> runs before a matching command (to move the remote between the SUT's pull and its push),
/// <see cref="Override"/> replaces a command's result (to make every push fail). Records every spec for the secret sweep.
/// </summary>
internal sealed class HookedProcessRunner(IProcessRunner inner) : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Func<ProcessSpec, Task>? BeforeRun { get; set; }

    public Func<ProcessSpec, ProcessResult?>? Override { get; set; }

    public static string SubCommand(ProcessSpec spec)
    {
        var i = 0;
        while (i + 1 < spec.Arguments.Count && spec.Arguments[i] == "-c")
        {
            i += 2;
        }

        return spec.Arguments.Count > i ? spec.Arguments[i] : string.Empty;
    }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Calls.Add(spec);
        if (BeforeRun is not null)
        {
            await BeforeRun(spec).ConfigureAwait(false);
        }

        return Override?.Invoke(spec) ?? await inner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
    }
}
