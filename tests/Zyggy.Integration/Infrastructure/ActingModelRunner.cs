using Zyggy.Core.Models;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A decorator over the real <see cref="ClaudeCodeCliRunner"/> launching <c>tools/fake-claude</c>: per call it picks the scenario, the
/// delay and the argument and stdin capture files, and after the run performs the side effects the model would have performed (the bats
/// <c>*-actions.sh</c> stubs) — setting a watermark, recording a replied id, writing facts.
/// </summary>
internal sealed class ActingModelRunner(IModelRunner inner, string captureDirectory) : IModelRunner
{
    private int _calls;

    /// <summary>Gets the scenario of call <c>n</c> (0-based).</summary>
    public Func<int, string> Scenario { get; init; } = _ => "m365-brief-ok";

    /// <summary>Gets the delay in milliseconds before call <c>n</c> streams; <see langword="null"/> for none.</summary>
    public Func<int, int?> DelayMs { get; init; } = _ => null;

    /// <summary>Gets the side effects of call <c>n</c>, performed after a completed run.</summary>
    public Action<ModelRunRequest, ModelRunResult, int>? Act { get; init; }

    /// <summary>Gets the requests as the decorated runner received them, in order.</summary>
    public List<ModelRunRequest> Requests { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public string ArgumentsCapture(int call) => Path.Combine(captureDirectory, $"call-{call}-args.bin");

    public string StdinCapture(int call) => Path.Combine(captureDirectory, $"call-{call}-stdin.bin");

    public async Task<ModelRunResult> RunAsync(ModelRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var call = Interlocked.Increment(ref _calls) - 1;
        Directory.CreateDirectory(captureDirectory);
        var environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal)
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = Scenario(call),
            ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = ArgumentsCapture(call),
            ["ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE"] = StdinCapture(call),
        };
        if (DelayMs(call) is { } delay)
        {
            environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = delay.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Requests.Add(request);
        var result = await inner.RunAsync(request with { Environment = environment }, cancellationToken);
        Act?.Invoke(request, result, call);
        return result;
    }
}
