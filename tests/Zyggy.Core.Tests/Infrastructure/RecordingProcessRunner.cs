using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A hand-written <see cref="IProcessRunner"/> for git: records every <see cref="ProcessSpec"/> in order and answers from a script
/// keyed by the git sub-command (the first argument after any <c>-c key=value</c> pairs). Unscripted calls succeed with empty output,
/// except <c>rev-parse</c> (a fixed SHA) and <c>symbolic-ref</c> (<c>main</c>).
/// </summary>
internal sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, Queue<ProcessResult>> _script = new(StringComparer.Ordinal);

    public List<ProcessSpec> Calls { get; } = [];

    public Func<ProcessSpec, ProcessResult?>? Hook { get; set; }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false, false, false, TimeSpan.FromMilliseconds(5));

    public static ProcessResult Fail(int code, string stderr) => new(code, "", stderr, false, false, false, TimeSpan.FromMilliseconds(5));

    public static string SubCommand(ProcessSpec spec)
    {
        var i = 0;
        while (i + 1 < spec.Arguments.Count && spec.Arguments[i] == "-c")
        {
            i += 2;
        }

        return spec.Arguments.Count > i ? spec.Arguments[i] : string.Empty;
    }

    public RecordingProcessRunner On(string subCommand, params ProcessResult[] results)
    {
        if (!_script.TryGetValue(subCommand, out var queue))
        {
            queue = new Queue<ProcessResult>();
            _script[subCommand] = queue;
        }

        foreach (var result in results)
        {
            queue.Enqueue(result);
        }

        return this;
    }

    public IEnumerable<ProcessSpec> CallsOf(string subCommand) => Calls.Where(c => SubCommand(c) == subCommand);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(spec);
        if (Hook?.Invoke(spec) is { } hooked)
        {
            return Task.FromResult(hooked);
        }

        var sub = SubCommand(spec);
        if (_script.TryGetValue(sub, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        return Task.FromResult(sub switch
        {
            "rev-parse" => Ok("0123456789abcdef0123456789abcdef01234567\n"),
            "symbolic-ref" => Ok("main\n"),
            _ => Ok(),
        });
    }
}
