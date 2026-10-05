using System.Text.Json.Nodes;

using NSubstitute;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Tools;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// A temporary Central for the m365 runs: the stubbed Graph and token, a memory tree, a checkout with the tool partition, a state
/// directory, a download root, and a model substitute.
/// </summary>
internal sealed class RunFixture : IDisposable
{
    private readonly MemoryTree _tree = new();

    public RunFixture(string? configPatch = null)
    {
        Graph = new GraphFixture(configPatch);
        var tools = Path.Combine(Root, "checkout", ".claude", "skills", "m365", "tools");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.EnumerateFiles(M365Run.Golden("tools")))
        {
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)));
        }

        Directory.CreateDirectory(Path.Combine(Root, "checkout", "instance"));
        var instance = M365Environment.Load(new Dictionary<string, string?>
        {
            ["ZYGGY_INSTANCE_DIR"] = Path.Combine(Root, "checkout", "instance"),
            ["ZYGGY_STATE_DIR"] = Path.Combine(Root, "state"),
            ["HOME"] = Path.Combine(Root, "home"),
        }).Environment!;
        var brussels = TimeZoneInfo.CreateCustomTimeZone("Europe/Brussels", TimeSpan.FromHours(2), "Test/Brussels", "Test/Brussels");
        var patterns = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;
        Session = new M365Session(_tree.Paths, brussels, instance, Graph.Configuration, null, patterns);
        Partition = M365ToolPartition.Load(instance.Checkout).Partition!;
        Directory.CreateDirectory(State);
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public GraphFixture Graph { get; }

    public M365Session Session { get; }

    public M365ToolPartition Partition { get; }

    public IModelRunner Model { get; } = Substitute.For<IModelRunner>();

    public List<ModelRunRequest> Requests { get; } = [];

    public string State => Path.Combine(Root, "state", "m365");

    public string DownloadRoot => Path.Combine(Root, "home", ".cache", "zyggy-m365-downloads");

    public M365State StateFiles => new(Session.Instance.Paths, Graph.Clock);

    /// <summary>The model acts through <paramref name="act"/> (as the real model does with the state and facts verbs) and answers its result text.</summary>
    public void Acts(Func<ModelRunRequest, int, string> act, decimal cost = 0.10m, int turns = 4)
    {
        var call = 0;
        Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(info =>
        {
            var request = info.Arg<ModelRunRequest>();
            Requests.Add(request);
            info.Arg<CancellationToken>().ThrowIfCancellationRequested();
            var text = act(request, call++);
            return Ok(text, cost, turns);
        });
    }

    public static ModelRunResult Ok(string text, decimal cost = 0.10m, int turns = 4) =>
        new(ModelRunOutcome.Succeeded, null, null, text, null, cost, turns, TimeSpan.FromSeconds(1), null, null, null, 0, 0);

    public static ModelRunResult Error(decimal cost = 0.05m, int turns = 3) =>
        new(ModelRunOutcome.Failed, null, "error_during_execution", string.Empty, null, cost, turns, TimeSpan.FromSeconds(1), null, null, null, 0, 0);

    public JsonObject Checkpoint(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(State, name)))!.AsObject();

    public void Dispose()
    {
        Graph.Stub.Violations.Should().BeEmpty();
        Graph.Dispose();
        _tree.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
