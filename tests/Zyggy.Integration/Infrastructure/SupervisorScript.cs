using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Models;
using Zyggy.Integration.Node;

namespace Zyggy.Integration.Infrastructure;

/// <summary>What <c>tools/fake-claude</c> does in one supervisor run (spec 18): its scripted tool calls, a delay, its scenario and exit code, or a runner that throws.</summary>
internal sealed record SupervisorStepScript(IReadOnlyList<string> ToolCalls, int? DelayMs = null, string Scenario = "done", int? Exit = null, bool Throws = false);

/// <summary>One supervisor run as the scripted runner saw it.</summary>
internal sealed record SupervisorRunRecord(string Event, DateTimeOffset Started, DateTimeOffset Ended, ModelRunRequest Request);

/// <summary>
/// The production model runner of a node host with a per-run script for <c>tools/fake-claude</c> (spec 18 slices 10 and 11): every
/// supervisor run gets the environment that makes the fake perform the scripted tool calls through the real tool guard and start
/// the real <c>zyggy</c> binary against Central's <c>node.json</c>. Runs that are not a supervisor step pass through untouched.
/// </summary>
internal sealed partial class SupervisorScript(NodeHostHarness harness) : IModelRunner
{
    private readonly List<SupervisorRunRecord> _runs = [];
    private IModelRunner _inner = null!;
    private int _calls;

    /// <summary>Gets or sets the script: run number (0-based) and event id to what the fake does.</summary>
    public Func<int, string, SupervisorStepScript> Script { get; set; } = (_, _) => new SupervisorStepScript([]);

    /// <summary>Gets the file the fake logs <c>allowed &lt;tool&gt;</c> / <c>blocked &lt;tool&gt;</c> lines to.</summary>
    public string ToolLog => harness.Scratch.File("tool-log.txt");

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<SupervisorRunRecord> Runs
    {
        get
        {
            lock (_runs)
            {
                return [.. _runs];
            }
        }
    }

    /// <summary>One scripted tool call; with <paramref name="run"/>, an allowed call starts <c>zyggy</c> with those arguments.</summary>
    public static string Call(string tool, JsonObject input, string[]? run = null)
    {
        var call = new JsonObject { ["tool_name"] = tool, ["tool_input"] = input };
        if (run is not null)
        {
            call["run"] = new JsonArray([.. run.Select(a => (JsonNode)a)]);
        }

        return call.ToJsonString();
    }

    /// <summary>A <c>Bash</c> call of <c>zyggy</c> with <paramref name="args"/>, quoted as a model would write them.</summary>
    public static string Zyggy(params string[] args) =>
        Call("Bash", new JsonObject { ["command"] = "zyggy " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? "\"" + a + "\"" : a)) }, args);

    /// <summary>Wraps the production <see cref="IModelRunner"/> registration; pass it as the host's test registrations.</summary>
    public void Register(IServiceCollection services)
    {
        ServiceDescriptor production = services.Last(d => d.ServiceType == typeof(IModelRunner));
        services.AddSingleton<IModelRunner>(sp =>
        {
            _inner = (IModelRunner)production.ImplementationFactory!(sp);
            return this;
        });
    }

    public Task<bool?> CheckAuthAsync(CancellationToken cancellationToken) => _inner.CheckAuthAsync(cancellationToken);

    public async Task<ModelRunResult> RunAsync(ModelRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Match match = EventId().Match(request.Prompt);
        if (!request.Prompt.StartsWith("You are Central's supervisor.", StringComparison.Ordinal) || !match.Success)
        {
            return await _inner.RunAsync(request, cancellationToken);
        }

        int call = Interlocked.Increment(ref _calls) - 1;
        string id = match.Groups[1].Value;
        SupervisorStepScript step = Script(call, id);
        if (step.Throws)
        {
            throw new InvalidOperationException("the runner broke");
        }

        var environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal);
        foreach ((string name, string value) in Environment(step))
        {
            environment[name] = value;
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            return await _inner.RunAsync(request with { Environment = environment }, cancellationToken);
        }
        finally
        {
            lock (_runs)
            {
                _runs.Add(new SupervisorRunRecord(id, started, DateTimeOffset.UtcNow, request));
            }
        }
    }

    private Dictionary<string, string> Environment(SupervisorStepScript step)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = step.Scenario,
            ["ZYGGY_FAKE_CLAUDE_ZYGGY"] = ZyggyCli.ExecutablePath,
            ["ZYGGY_FAKE_CLAUDE_TOOL_LOG"] = ToolLog,
            ["ZYGGY_NODE_JSON"] = harness.NodeJsonPath,
            ["ZYGGY_SECRET_PATTERNS"] = PickupMemory.PatternsPath,
        };
        foreach ((string name, string? value) in harness.Bus.GitEnvironment)
        {
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        if (step.ToolCalls.Count > 0)
        {
            environment["ZYGGY_FAKE_CLAUDE_TOOL_CALLS"] = string.Join('\n', step.ToolCalls);
        }

        if (step.DelayMs is { } delay)
        {
            environment["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = delay.ToString(CultureInfo.InvariantCulture);
        }

        if (step.Exit is { } exit)
        {
            environment["ZYGGY_FAKE_CLAUDE_EXIT"] = exit.ToString(CultureInfo.InvariantCulture);
        }

        return environment;
    }

    [GeneratedRegex("on event ([0-9A-Z]{26})", RegexOptions.CultureInvariant)]
    private static partial Regex EventId();
}
