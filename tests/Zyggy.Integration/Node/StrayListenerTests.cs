using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Processes;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 61 slice 7 — a stray node. AC-21: a second host on a held checkout logs the holder's pid, start and version with the remedy and
/// leaves the refused-start record; <c>/health</c> carries <c>pid</c> and <c>startedUtc</c>. AC-23: <c>zyggy node stop</c> ends a real
/// <c>zyggy-node</c> process and frees the lock, says <c>no listener is running</c> when none is, and touches nothing when the recorded
/// pid is another program or a foreign listener answers on the health port.
/// </summary>
public sealed class StrayListenerTests
{
    private static readonly string Self = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    // AC-21
    [Fact]
    public async Task SecondHost_CheckoutHeld_RefusalNamesHolderRecordLeftHealthCarriesPidAndStarted()
    {
        // Arrange: the first host runs on the checkout
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IHost first = harness.Build();
        await first.StartAsync(ct);
        try
        {
            NodeStatus status = first.Services.GetRequiredService<NodeStatus>();
            await NodeHostHarness.WaitUntilAsync(() => Task.FromResult(status.Listener?.Status.LastPollUtc is not null), TimeSpan.FromSeconds(30), ct);
            ListenerRecord owner = ListenerOwner.ReadOwner(harness.Bus.CloneDir)!;
            string second = NodeJson.Write(
                harness.Scratch.File("config-2"),
                NodeHostHarness.Acme,
                NodeHostHarness.HomeLaptop,
                TestKeys.Acme1,
                busLocalPath: harness.Bus.CloneDir,
                remote: TestHost.Remote.HttpsUrl!.ToString(),
                secretsRoot: harness.SecretsRoot,
                claudePath: FakeClaude.ExecutablePath,
                nodeDir: harness.Scratch.File("node-2"),
                devRoots: [harness.ProjectsRoot]);

            // Act: a second host on the same checkout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            int exit = await NodeHost.RunAsync(["--config", second], cts.Token, s => harness.Configure(s));

            // Assert
            exit.Should().Be(6);
            string log = Directory.GetFiles(Path.Combine(harness.Scratch.File("node-2"), "logs")).Single();
            ReadShared(log).Where(l => l.Contains("[ERR]", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().EndWith(
                $"refusing to start: listener already running (pid {Self} since {Stamp(owner.StartedUtc)}, zyggy-node {owner.Version}) — stop it with: zyggy node stop");
            ListenerOwner.ReadRefused(harness.Bus.CloneDir)!.HolderPid.Should().Be(Environment.ProcessId);
            using var http = new HttpClient();
            int port = first.Services.GetRequiredService<HealthServer>().BoundPort;
            using JsonDocument health = JsonDocument.Parse(await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/health"), ct));
            (health.RootElement.GetProperty("pid").GetInt32(), health.RootElement.GetProperty("startedUtc").GetString())
                .Should().Be((Environment.ProcessId, Stamp(owner.StartedUtc)));
        }
        finally
        {
            await first.StopAsync(ct);
        }
    }

    // AC-23: a real zyggy-node process
    [Fact]
    public async Task NodeStop_RealNodeProcess_EndedLockFreeRecordsCleared()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(agentCorePath: harness.Scratch.File("core"));
        Uri url = FreeLoopbackUrl();
        using Process node = StartNode(harness, url);
        try
        {
            await WaitForHealthPidAsync(url, node.Id, ct);

            // Act
            var (exit, console) = await NodeStopAsync(harness, url, ct);

            // Assert
            exit.Should().Be(0, console.Stdout + console.Stderr);
            console.Stdout.Should().Be($"stopped zyggy-node pid {node.Id} (0 run(s) interrupted; their claims resume at the next start)\n");
            node.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue();
            (await LockFreeAsync(harness.Bus.CloneDir, ct)).Should().BeTrue();
            ListenerOwner.ReadOwner(harness.Bus.CloneDir).Should().BeNull();
        }
        finally
        {
            Kill(node);
        }
    }

    // AC-23 with a run in progress: the run is interrupted, its claim stays on the bus for the next start (or the sweep), no report
    [Fact]
    public async Task NodeStop_RealNodeWithARunningJob_OneRunInterruptedClaimKeptNoReport()
    {
        // Arrange: the model runs two minutes
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(agentCorePath: harness.Scratch.File("core"));
        ScratchProject project = await harness.ProjectAsync(ct);
        SeededJob job = await harness.SeedJobAsync(project, ct);
        Uri url = FreeLoopbackUrl();
        using Process node = StartNode(harness, url, new Dictionary<string, string?> { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done", ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "120000" });
        try
        {
            using (var client = new HealthClient(url, TimeProvider.System))
            {
                await NodeHostHarness.WaitUntilAsync(async () => await client.ReadAsync(ct) is Reachable { Health.JobsRunning: 1 }, TimeSpan.FromSeconds(60), ct);
            }

            // Act
            var (exit, console) = await NodeStopAsync(harness, url, ct);

            // Assert
            exit.Should().Be(0, console.Stdout + console.Stderr);
            console.Stdout.Should().Be($"stopped zyggy-node pid {node.Id} (1 run(s) interrupted; their claims resume at the next start)\n");
            node.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue();
            (await harness.Bus.ExistsOnMainAsync(BusPaths.Claimed(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop) + "/" + job.Id.Value + ".md", ct)).Should().BeTrue("the claim stays for the next start");
            (await harness.ReportsOnMainAsync(ct)).Should().BeEmpty();
        }
        finally
        {
            Kill(node);
        }
    }

    // AC-23: a foreign listener on the health port — the real node is left running
    [Fact]
    public async Task NodeStop_ForeignListenerOnHealthPort_ExitFiveNodeStillRunning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(agentCorePath: harness.Scratch.File("core"));
        Uri url = FreeLoopbackUrl();
        using Process node = StartNode(harness, url);
        await using var foreign = new StubHealthServer("{\"version\":\"9.9.9\",\"tenant\":\"acme\",\"machine\":\"home-laptop\",\"jobsRunning\":0,\"jobsClaimedByMe\":0,\"pid\":1}");
        try
        {
            await WaitForHealthPidAsync(url, node.Id, ct);

            // Act: the CLI's health URL is answered by another program
            var (exit, console) = await NodeStopAsync(harness, foreign.Url, ct);

            // Assert
            exit.Should().Be(5, console.Stdout + console.Stderr);
            console.Stdout.Should().StartWith($"the listener on {foreign.Url} is not zyggy-node pid {node.Id} of acme/home-laptop").And.EndWith("; nothing was touched\n");
            node.HasExited.Should().BeFalse();
            ListenerOwner.ReadOwner(harness.Bus.CloneDir)!.Pid.Should().Be(node.Id);
        }
        finally
        {
            Kill(node);
        }
    }

    // AC-23: the recorded pid belongs to another program (this test process holds the lock and is named in the record)
    [Fact]
    public async Task NodeStop_RecordedPidIsAnotherProgram_ExitFiveNothingTouched()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        using IDisposable? held = await BusCheckoutLock.AcquireAsync(harness.Bus.CloneDir, BusCheckoutLock.ListenLockName, TimeSpan.Zero, new FakeTimeProvider(), ct);
        ProcessFacts me = OsProcessTable.Instance.Self();
        ListenerOwner.WriteOwner(harness.Bus.CloneDir, new ListenerRecord(me.Pid, me.StartedUtc!.Value, "0.30.1", DateTimeOffset.UtcNow));

        // Act
        var (exit, console) = await NodeStopAsync(harness, FreeLoopbackUrl(), ct);

        // Assert: this very process is still running the assertion, and the record is untouched
        exit.Should().Be(5, console.Stdout + console.Stderr);
        console.Stdout.Should().StartWith($"pid {Self} is not this user's zyggy-node started at ").And.EndWith("; nothing was touched\n");
        ListenerOwner.ReadOwner(harness.Bus.CloneDir).Should().NotBeNull();
    }

    // AC-23: nothing holds the checkout
    [Fact]
    public async Task NodeStop_NoListener_ExitZeroNoListenerIsRunning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);

        // Act
        var (exit, console) = await NodeStopAsync(harness, FreeLoopbackUrl(), ct);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Should().Be("no listener is running\n");
    }

    private static Process StartNode(NodeHostHarness harness, Uri healthUrl, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var environment = new Dictionary<string, string?>(harness.Bus.GitEnvironment)
        {
            ["ZYGGY_NODE_HEALTH_PORT"] = healthUrl.Port.ToString(CultureInfo.InvariantCulture),
            ["ZYGGY_BUS_API_BASE"] = FreeLoopbackUrl().GetLeftPart(UriPartial.Authority) + "/",
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, string?>())
        {
            environment[name] = value;
        }

        Process node = ZyggyNode.Start(["--config", harness.NodeJsonPath], environment, harness.Scratch.Path);
        node.OutputDataReceived += (_, _) => { };
        node.ErrorDataReceived += (_, _) => { };
        node.BeginOutputReadLine();
        node.BeginErrorReadLine();
        node.StandardInput.Close();
        return node;
    }

    private static async Task WaitForHealthPidAsync(Uri url, int pid, CancellationToken ct)
    {
        using var client = new HealthClient(url, TimeProvider.System);
        await NodeHostHarness.WaitUntilAsync(
            async () => await client.ReadAsync(ct) is Reachable { Health.Pid: var answered } && answered == pid,
            TimeSpan.FromSeconds(60),
            ct);
    }

    private static Task<(int Exit, VerbConsole Console)> NodeStopAsync(NodeHostHarness harness, Uri healthUrl, CancellationToken ct) =>
        ZyggyCliInProcess.RunAsync(
            ["node", "stop"],
            new Dictionary<string, string?> { ["ZYGGY_NODE_JSON"] = harness.NodeJsonPath, ["ZYGGY_HEALTH_URL"] = healthUrl.ToString() },
            ct: ct);

    private static async Task<bool> LockFreeAsync(string checkout, CancellationToken ct)
    {
        using IDisposable? probe = await BusCheckoutLock.AcquireAsync(checkout, BusCheckoutLock.ListenLockName, TimeSpan.FromSeconds(10), TimeProvider.System, ct);
        return probe is not null;
    }

    private static Uri FreeLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/health");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static List<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
