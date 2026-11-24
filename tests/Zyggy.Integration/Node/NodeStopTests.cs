using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Health;
using Zyggy.Core.Processes;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Node;

/// <summary>
/// Spec 61 slice 8 — stopping the task stops the node and its run; model processes of a killed node are ended at start. Real
/// <c>zyggy-node</c> processes against a local bare repository and <c>tools/fake-claude</c>, on Windows and Linux alike: the task's
/// wrapper is stood in by the OS shell (<c>powershell.exe</c> as the real task uses, <c>/bin/sh</c> on Linux), ended by the test alone.
/// </summary>
public sealed class NodeStopTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly Dictionary<string, string?> LongRun = new() { ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = "done", ["ZYGGY_FAKE_CLAUDE_DELAY_MS"] = "120000" };

    // AC-24: the wrapper ends (as Stop-ScheduledTask ends powershell.exe) → the node stops on its own: model gone, claim kept, lock free
    [Fact]
    public async Task ExitWithParent_WrapperEnded_NodeStopsModelGoneClaimKeptLockFree()
    {
        // Arrange: the node runs a two-minute job under a wrapper shell
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(agentCorePath: harness.Scratch.File("core"));
        ScratchProject project = await harness.ProjectAsync(ct);
        SeededJob job = await harness.SeedJobAsync(project, ct);
        Uri url = FreeLoopbackUrl();
        using Process wrapper = StartWrapper(harness, url);
        int nodePid = 0;
        try
        {
            nodePid = await WaitForOneRunAsync(url, ct);
            ModelProcessRecord model = await WaitForModelRecordAsync(harness, ct);

            // Act: only the wrapper is ended, never its tree
            wrapper.Kill(entireProcessTree: false);

            // Assert
            bool nodeEnded = await ExitedAsync(nodePid, TimeSpan.FromSeconds(30), ct);
            bool modelGone = !Alive(model.Pid, model.StartedUtc);
            bool claimKept = await harness.Bus.ExistsOnMainAsync(BusPaths.Claimed(NodeHostHarness.Acme, NodeHostHarness.HomeLaptop) + "/" + job.Id.Value + ".md", ct);
            (nodeEnded, modelGone, claimKept, await LockFreeAsync(harness.Bus.CloneDir, ct), (await harness.ReportsOnMainAsync(ct)).Count)
                .Should().Be((true, true, true, true, 0));
        }
        finally
        {
            Kill(wrapper);
            KillPid(nodePid);
        }
    }

    // AC-24 edge: a wrapper that starts the node detached and ends at once → the node stops by itself (Windows: the parent is already
    // gone when the node looks; Linux: gone a moment later, the orphan is re-parented) and logs it once; nothing holds the checkout after
    [Fact]
    public async Task ExitWithParent_WrapperEndsRightAway_NodeStopsByItself()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(agentCorePath: harness.Scratch.File("core"));

        // Act
        using Process wrapper = StartWrapper(harness, FreeLoopbackUrl(), detached: true);
        try
        {
            wrapper.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();

            // Assert
            bool stopped = await WaitAsync(() => StopLines(harness).Count == 1 && ListenerOwner.ReadOwner(harness.Bus.CloneDir) is null, TimeSpan.FromSeconds(60), ct);
            (stopped, StopLines(harness).Count, await LockFreeAsync(harness.Bus.CloneDir, ct)).Should().Be((true, 1, true), string.Join("\n", StopLines(harness)));
        }
        finally
        {
            // the detached node is no child of this test: end it by its owner record when it did not stop by itself
            KillPid(ListenerOwner.ReadOwner(harness.Bus.CloneDir)?.Pid ?? 0);
        }
    }

    private static List<string> StopLines(NodeHostHarness harness)
    {
        string logs = Path.Combine(harness.NodeDir, "logs");
        if (!Directory.Exists(logs))
        {
            return [];
        }

        return [.. Directory.GetFiles(logs).SelectMany(ReadShared).Where(l => l.Contains("--exit-with-parent: the process that started the node", StringComparison.Ordinal))];
    }

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

    // AC-26: a node killed from outside leaves its fake-claude running; the next start ends it before resuming the claim, and a claude
    // process of the user's own session (not recorded) is left alone
    [Fact]
    public async Task NextStart_ModelOfAKilledNodeStillRunning_EndedInteractiveClaudeLeftAlone()
    {
        // Arrange: the first node runs a two-minute job and is killed alone; an "interactive" fake-claude runs beside it
        var ct = TestContext.Current.CancellationToken;
        await using NodeHostHarness harness = await NodeHostHarness.CreateAsync(ct);
        harness.WriteNodeJson(agentCorePath: harness.Scratch.File("core"));
        ScratchProject project = await harness.ProjectAsync(ct);
        _ = await harness.SeedJobAsync(project, ct);
        Uri url = FreeLoopbackUrl();
        using Process first = StartNode(harness, url, LongRun);
        using Process interactive = StartInteractiveClaude(harness.Scratch.Path);
        Process? second = null;
        ModelProcessRecord? left = null;
        try
        {
            await WaitForOneRunAsync(url, ct);
            left = await WaitForModelRecordAsync(harness, ct);
            first.Kill(entireProcessTree: false);

            // the timed wait: the parameterless one also waits for the output pipes, which the surviving model still holds open
            first.WaitForExit(TimeSpan.FromSeconds(10)).Should().BeTrue();
            Alive(left.Pid, left.StartedUtc).Should().BeTrue("precondition: the killed node's model is still running");

            // Act
            second = StartNode(harness, url, LongRun);
            await WaitForHealthPidAsync(url, second.Id, ct);

            // Assert
            bool ended = await WaitAsync(() => !Alive(left.Pid, left.StartedUtc), TimeSpan.FromSeconds(30), ct);
            (ended, interactive.HasExited).Should().Be((true, false));
        }
        finally
        {
            Kill(first);
            if (second is not null)
            {
                Kill(second);
                second.Dispose();
            }

            Kill(interactive);
            if (left is not null)
            {
                KillPid(left.Pid);
            }
        }
    }

    private static Dictionary<string, string?> NodeEnvironment(NodeHostHarness harness, Uri healthUrl, IReadOnlyDictionary<string, string?>? extra)
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

        return environment;
    }

    private static Process StartNode(NodeHostHarness harness, Uri healthUrl, IReadOnlyDictionary<string, string?>? extra = null)
    {
        Process node = ZyggyNode.Start(["--config", harness.NodeJsonPath], NodeEnvironment(harness, healthUrl, extra), harness.Scratch.Path);
        Drain(node);
        node.StandardInput.Close();
        return node;
    }

    /// <summary>The task's wrapper: a shell that runs the node with <c>--exit-with-parent</c> and waits for it (or, detached, ends at once).</summary>
    private static Process StartWrapper(NodeHostHarness harness, Uri healthUrl, bool detached = false)
    {
        string node = ZyggyNode.ExecutablePath;
        string config = harness.NodeJsonPath;
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            string command = detached
                ? $"Start-Process -WindowStyle Hidden -FilePath '{node}' -ArgumentList '--exit-with-parent','--config','\"{config}\"'; exit 0"
                : $"& '{node}' --exit-with-parent --config '{config}'; exit $LASTEXITCODE";
            info = new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command } };
        }
        else
        {
            string command = detached
                ? $"'{node}' --exit-with-parent --config '{config}' >/dev/null 2>&1 & sleep 2; exit 0"
                : $"'{node}' --exit-with-parent --config '{config}'; exit $?";
            info = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        }

        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        info.WorkingDirectory = harness.Scratch.Path;
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("ZYGGY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        foreach (var (name, value) in NodeEnvironment(harness, healthUrl, LongRun))
        {
            info.Environment[name] = value;
        }

        Process wrapper = Process.Start(info)!;
        Drain(wrapper);
        wrapper.StandardInput.Close();
        return wrapper;
    }

    private static Process StartInteractiveClaude(string workingDirectory)
    {
        var info = new ProcessStartInfo(FakeClaude.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        info.ArgumentList.Add("-p");
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("ZYGGY_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        foreach (var (name, value) in LongRun)
        {
            info.Environment[name] = value;
        }

        Process claude = Process.Start(info)!;
        Drain(claude);
        claude.StandardInput.Close();
        return claude;
    }

    private static void Drain(Process process)
    {
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static async Task<int> WaitForOneRunAsync(Uri url, CancellationToken ct)
    {
        using var client = new HealthClient(url, TimeProvider.System);
        int pid = 0;
        await NodeHostHarness.WaitUntilAsync(
            async () =>
            {
                if (await client.ReadAsync(ct) is Reachable { Health: { JobsRunning: 1, Pid: { } answered } })
                {
                    pid = answered;
                    return true;
                }

                return false;
            },
            TimeSpan.FromSeconds(60),
            ct);
        return pid;
    }

    private static async Task WaitForHealthPidAsync(Uri url, int pid, CancellationToken ct)
    {
        using var client = new HealthClient(url, TimeProvider.System);
        await NodeHostHarness.WaitUntilAsync(async () => await client.ReadAsync(ct) is Reachable { Health.Pid: var answered } && answered == pid, TimeSpan.FromSeconds(60), ct);
    }

    private static async Task<ModelProcessRecord> WaitForModelRecordAsync(NodeHostHarness harness, CancellationToken ct)
    {
        var records = new ModelProcessRecords(ModelProcessRecords.DirectoryFor(harness.NodeDir, Acme), FakeClaude.ExecutablePath, OsProcessTable.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelProcessRecords>.Instance);
        ModelProcessRecord? found = null;
        await NodeHostHarness.WaitUntilAsync(() => Task.FromResult((found = records.Read() is [var first, ..] ? first : null) is not null), TimeSpan.FromSeconds(30), ct);
        return found!;
    }

    private static bool Alive(int pid, DateTimeOffset started) =>
        OsProcessTable.Instance.Find(pid) is { StartedUtc: { } now } && (now - started).Duration() <= TimeSpan.FromSeconds(1);

    private static Task<bool> ExitedAsync(int pid, TimeSpan within, CancellationToken ct) =>
        WaitAsync(() => OsProcessTable.Instance.Find(pid) is not { Name: "zyggy-node" }, within, ct);

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan within, CancellationToken ct)
    {
        DateTime until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200, ct);
        }

        return condition();
    }

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

    private static void KillPid(int pid)
    {
        if (pid > 0 && OsProcessTable.Instance.Find(pid) is { Name: "zyggy-node" or "fake-claude" or "claude" })
        {
            _ = OsProcessTable.Instance.EndTree(pid, TimeSpan.FromSeconds(10));
        }
    }
}
