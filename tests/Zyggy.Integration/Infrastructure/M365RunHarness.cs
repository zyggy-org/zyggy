using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Zyggy.Core.Models;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// An instance for the m365 model runs in process (plan 33 Step 17): the instance fixture, real key files, the tool partition in the
/// checkout, <c>claude</c> resolved to <c>tools/fake-claude</c>, and the stubbed Graph. The model runner is the real
/// <see cref="ClaudeCodeCliRunner"/> wrapped by an <see cref="ActingModelRunner"/>.
/// </summary>
internal sealed class M365RunHarness : IDisposable
{
    private readonly TestCertificates _pair;

    public M365RunHarness(string? configPatch = null)
    {
        _pair = TestCertificates.Create(Path.Combine(Fixture.Home, ".config", "zyggy"));
        var tools = Path.Combine(Fixture.Checkout, ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365InstanceFixture.Golden("m365", "tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }

        if (configPatch is not null)
        {
            var path = Path.Combine(Fixture.InstanceDirectory, "m365.json");
            var config = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (var (section, values) in JsonNode.Parse(configPatch)!.AsObject().ToList())
            {
                foreach (var (key, value) in values!.AsObject().ToList())
                {
                    config[section]![key] = value?.DeepClone();
                }
            }

            File.WriteAllText(path, config.ToJsonString());
        }

        Directory.CreateDirectory(State);
    }

    public M365InstanceFixture Fixture { get; } = new();

    public StubGraphHandler Graph { get; } = StubGraphHandler.FromGoldenRoutes();

    public string State => Path.Combine(Fixture.StateDirectory, "m365");

    public string Captures => Path.Combine(Fixture.Root, "captures");

    /// <summary>Gets the run directories the runs created (the fixture's own <c>run.test01</c> is not one).</summary>
    public IReadOnlyList<string> RunDirectories
    {
        get
        {
            var root = Path.Combine(Fixture.Home, ".cache", "zyggy-m365-downloads");
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root, "zyggy-m365-*").ToList() : [];
        }
    }

    public ActingModelRunner Model(Func<int, string> scenario, Func<int, int?>? delayMs = null, Action<ModelRunRequest, ModelRunResult, int>? act = null) =>
        new(new ClaudeCodeCliRunner(new ProcessRunner(TimeProvider.System), Options.Create(new ClaudeCodeOptions { Path = FakeClaude.ExecutablePath }), TimeProvider.System), Captures)
        {
            Scenario = scenario,
            DelayMs = delayMs ?? (_ => null),
            Act = act,
        };

    public Dictionary<string, string?> Env()
    {
        var env = Fixture.Env();
        env["ZYGGY_CLAUDE_PATH"] = FakeClaude.ExecutablePath;
        return env;
    }

    public Task<(int Exit, VerbConsole Console)> RunAsync(ActingModelRunner model, string[] args, CancellationToken cancellationToken) =>
        M365InProcess.RunAsync(Env(), Graph, args, model: _ => model, cancellationToken: cancellationToken);

    /// <summary>Cancels <paramref name="source"/> once call <paramref name="call"/>'s stdin capture exists: the fake is running and has its prompt.</summary>
    public static async Task CancelWhenStartedAsync(ActingModelRunner model, int call, CancellationTokenSource source)
    {
        for (var i = 0; i < 600 && !File.Exists(model.StdinCapture(call)); i++)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        await Task.Delay(200, CancellationToken.None);
        await source.CancelAsync();
    }

    public void Dispose()
    {
        Graph.Violations.Should().BeEmpty();
        _pair.Dispose();
        Fixture.Dispose();
    }
}
