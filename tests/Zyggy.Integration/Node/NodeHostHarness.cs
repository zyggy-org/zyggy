using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Processes;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;
using Zyggy.Node;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Integration.Node;

/// <summary>
/// The production host over the test edges (plan 07 Step 9): a bare repository seeded for <c>central</c> and <c>home-laptop</c>, a
/// scratch node directory, a file secret store holding <c>acme/1</c> and the test PAT, a <c>node.json</c> pointing <c>claude.path</c> at the
/// fake, the doorbell answered by <see cref="BareRepoHeadHandler"/>, every process recorded by <see cref="HookedProcessRunner"/>, and an
/// ephemeral loopback health port.
/// </summary>
internal sealed class NodeHostHarness : IAsyncDisposable
{
    public static readonly TenantId Acme = TenantId.Parse("acme");

    public static readonly MachineName Central = MachineName.Parse("central");

    public static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly List<ScratchProject> _projects = [];

    private NodeHostHarness()
    {
        Bus = new BusRepoFixture(Acme);
        Scratch = new ScratchDirectory();
        Head = new BareRepoHeadHandler(HeadShaOrNullAsync);
        Hooks = TestHost.Hooks();
    }

    public BusRepoFixture Bus { get; }

    public ScratchDirectory Scratch { get; }

    public BareRepoHeadHandler Head { get; }

    public HookedProcessRunner Hooks { get; }

    public string NodeDir => Scratch.File("node");

    public string SecretsRoot => Scratch.File("secrets");

    public string NodeJsonPath { get; private set; } = null!;

    public InMemorySecretStore Store { get; private set; } = null!;

    /// <summary>The fake's scenario for every run (<c>done</c> by default).</summary>
    public string Scenario { get; set; } = "done";

    /// <summary>Extra variables for the fake (<c>ZYGGY_FAKE_CLAUDE_DELAY_MS</c>, <c>_LINGER_MS</c>, <c>_AUTH</c>).</summary>
    public Dictionary<string, string> FakeEnvironment { get; } = [];

    /// <summary>
    /// The host's environment (plan 13 A6); <see langword="null"/> means the process environment plus <c>ZYGGY_SECRET_PATTERNS</c> = the
    /// golden pattern file (plan 14 A10/A11: the scanner publishes descriptions). A test that sets it keeps exactly what it set.
    /// </summary>
    public Dictionary<string, string?>? Environment { get; set; }

    /// <summary>The folder every <see cref="ProjectAsync"/> project lives in and the default <c>devRoots</c> entry (plan 14 A10).</summary>
    public string ProjectsRoot
    {
        get
        {
            string root = Scratch.File("projects");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>A short discovery interval for rescan tests (plan 14 A5); <see langword="null"/> keeps the configured minutes.</summary>
    public TimeSpan? DiscoveryInterval { get; set; }

    private IReadOnlyDictionary<string, string?> HostEnvironment
    {
        get
        {
            if (Environment is not null)
            {
                return Environment;
            }

            var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                variables[(string)entry.Key] = entry.Value as string;
            }

            variables["ZYGGY_SECRET_PATTERNS"] = Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");
            return variables;
        }
    }

    /// <summary>The single daily log file under the node directory.</summary>
    public string LogFile => Directory.GetFiles(Path.Combine(NodeDir, "logs")).Single();

    public IReadOnlyList<string> LogLines => File.Exists(Path.Combine(NodeDir, "logs")) || Directory.Exists(Path.Combine(NodeDir, "logs"))
        ? ReadLines(LogFile)
        : [];

    public static async Task<NodeHostHarness> CreateAsync(CancellationToken ct, bool seedSecrets = true)
    {
        var harness = new NodeHostHarness();
        await harness.Bus.InitializeAsync();
        await harness.Bus.SeedTenantAsync(Acme, [Central, HomeLaptop], ct);
        harness.Store = await TestHost.StoreAsync(Acme, ct);
        if (seedSecrets)
        {
            await using ServiceProvider services = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = harness.SecretsRoot).BuildServiceProvider();
            ISecretStore store = services.GetRequiredService<ISecretStore>();
            await store.SetAsync(Acme, TestKeys.Acme1.SecretName, TestKeys.Secret1, ct);
            await store.SetAsync(Acme, TestKeys.GitHubPat, Encoding.UTF8.GetBytes(TestKeys.TestPat), ct);
        }

        harness.WriteNodeJson();
        return harness;
    }

    /// <summary>Writes the host's <c>node.json</c>; every call replaces the previous file.</summary>
    public string WriteNodeJson(
        string? logLevel = "Debug",
        string? workHoursJson = null,
        int? maxConcurrentJobs = null,
        int safetyFetchMinutes = 1,
        int activeSeconds = 1,
        int idleSeconds = 1,
        string? busLocalPath = null,
        string? tenantOverride = null,
        bool omitSignWith = false,
        MachineName? machine = null,
        IReadOnlyList<string>? devRoots = null,
        string? discoveryJson = null,
        string? agentCorePath = null)
    {
        NodeJsonPath = NodeJson.Write(
            Scratch.File("config"),
            Acme,
            machine ?? HomeLaptop,
            omitSignWith ? null : TestKeys.Acme1,
            busLocalPath: busLocalPath ?? Bus.CloneDir,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: SecretsRoot,
            pollSeconds: activeSeconds,
            tenantOverride: tenantOverride,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: NodeDir,
            workHoursJson: workHoursJson,
            maxConcurrentJobs: maxConcurrentJobs,
            safetyFetchMinutes: safetyFetchMinutes,
            logLevel: logLevel,
            devRoots: devRoots ?? [ProjectsRoot],
            discoveryJson: discoveryJson,
            agentCorePath: agentCorePath);
        if (idleSeconds != activeSeconds)
        {
            string json = File.ReadAllText(NodeJsonPath).Replace(
                string.Create(CultureInfo.InvariantCulture, $"\"idleSeconds\": {activeSeconds}"),
                string.Create(CultureInfo.InvariantCulture, $"\"idleSeconds\": {idleSeconds}"),
                StringComparison.Ordinal);
            File.WriteAllText(NodeJsonPath, json);
        }

        return NodeJsonPath;
    }

    /// <summary>The test-side registrations on top of the production ones (plan 07 A8): the doorbell, the recorded processes, the fake's environment, an ephemeral health port, 200 ms jitter.</summary>
    public void Configure(IServiceCollection services, Action<IServiceCollection>? more = null)
    {
        services.Configure<GitHubBusOptions>(o =>
        {
            o.HandlerFactory = () => Head;
            o.GitEnvironment = Bus.GitEnvironment;
            o.Jitter = () => 0;
        });
        services.Configure<NodeHostOptions>(o => o.HealthPort = 0);
        services.Configure<JobRunnerOptions>(o =>
        {
            var environment = new Dictionary<string, string>(FakeEnvironment, StringComparer.Ordinal)
            {
                ["ZYGGY_FAKE_CLAUDE_SCENARIO"] = Scenario,
                ["ZYGGY_FAKE_CLAUDE_CAPTURE"] = "capture.bin",
            };
            o.ModelEnvironment = environment;
            o.TimeoutOverride = TimeSpan.FromSeconds(90);
        });
        services.AddSingleton<IProcessRunner>(Hooks);
        services.AddSingleton(sp => sp.GetRequiredService<NodeConfiguration>().Poll.ToPollOptions() with { Jitter = TimeSpan.FromMilliseconds(200) });
        if (DiscoveryInterval is { } interval)
        {
            services.Configure<DiscoveryOptions>(o => o.IntervalOverride = interval);
        }

        more?.Invoke(services);
    }

    public IHost Build(Action<IServiceCollection>? more = null) => NodeHost.Build(["--config", NodeJsonPath], s => Configure(s, more), HostEnvironment);

    /// <summary>Runs the host in process until <paramref name="cancelAfter"/> or until the loop stops it; returns the exit code.</summary>
    public async Task<int> RunAsync(TimeSpan cancelAfter, CancellationToken ct, Action<IServiceCollection>? more = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(cancelAfter);
        return await NodeHost.RunAsync(["--config", NodeJsonPath], cts.Token, s => Configure(s, more), HostEnvironment);
    }

    /// <summary>Runs the host in process until <paramref name="until"/> holds (then cancels) or <paramref name="timeout"/> elapses; returns the exit code.</summary>
    public async Task<int> RunUntilAsync(Func<Task<bool>> until, TimeSpan timeout, CancellationToken ct, Action<IServiceCollection>? more = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<int> run = NodeHost.RunAsync(["--config", NodeJsonPath], cts.Token, s => Configure(s, more), HostEnvironment);
        await WaitUntilAsync(until, timeout, ct, run);
        await cts.CancelAsync();
        return await run;
    }

    /// <summary>
    /// Creates a scratch project under <see cref="ProjectsRoot"/> (plan 14 A10: inside <c>devRoots</c>, found by the host's scan): a repository
    /// on <c>main</c> with <c>.claude/</c>, named <paramref name="name"/> or a fresh GUID label, optionally with an agent.
    /// </summary>
    public async Task<ScratchProject> ProjectAsync(CancellationToken ct, string? name = null, string? agent = null, string? agentDescription = null)
    {
        ScratchProject project = await ScratchProject.CreateInAsync(Bus, ProjectsRoot, name ?? Guid.NewGuid().ToString("N"), ct, agent, agentDescription);
        await File.WriteAllTextAsync(Path.Combine(project.Path, "README.md"), "Scratch project for a job round trip.\n", ct);
        _projects.Add(project);
        return project;
    }

    /// <summary>The registry file of <paramref name="machine"/> on <c>main</c>, parsed; <see langword="null"/> when absent.</summary>
    public async Task<RegistryDocument?> RegistryOnMainAsync(MachineName machine, CancellationToken ct)
    {
        string path = BusPaths.Registry(Acme, machine);
        return await Bus.ExistsOnMainAsync(path, ct) ? RegistryDocument.Parse(await Bus.ShowBytesAsync(path, ct)).Document : null;
    }

    /// <summary>
    /// Seeds a signed job from <c>central</c> to <c>home-laptop</c> naming <paramref name="project"/>, on <c>main</c>;
    /// <paramref name="projectField"/> replaces the job's <c>project</c> value (a registry name, or a path outside <c>devRoots</c>).
    /// </summary>
    public Task<SeededJob> SeedJobAsync(
        ScratchProject project,
        CancellationToken ct,
        EnvelopePriority priority = EnvelopePriority.Normal,
        string? body = null,
        string? projectField = null) =>
        Bus.SeedSignedJobAsync(Central, HomeLaptop, TestKeys.Acme1, Store, body ?? Body, fields: new JobFields(projectField ?? project.Path, TimeoutMinutes: 2, AllowedTools: ["Read"]), priority: priority, ct: ct);

    /// <summary>The report files on <c>main</c> for <c>home-laptop</c>, ordinal order.</summary>
    public async Task<IReadOnlyList<string>> ReportsOnMainAsync(CancellationToken ct)
    {
        string output = await Bus.RunGitAsync(Bus.BareDir, ["ls-tree", "--name-only", "main", BusPaths.Reports(Acme, HomeLaptop) + "/"], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(p => p.EndsWith(".md", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<Model> ReportAsync(string path, CancellationToken ct)
    {
        EnvelopeResult parsed = await new EnvelopeSigner(Store).ParseAndVerifyAsync(await Bus.ShowBytesAsync(path, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }

    public async Task<IReadOnlyList<string>> CommitSubjectsAsync(CancellationToken ct) =>
        (await Bus.RunGitAsync(Bus.BareDir, ["log", "--format=%s", "main"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct, Task? run = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (run is { IsCompleted: true })
            {
                await run; // surfaces a fault; a completed run means the condition can no longer come true
                throw new InvalidOperationException("the host stopped before the condition held");
            }

            if (await condition())
            {
                return;
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("the condition did not hold within " + timeout);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ScratchProject project in _projects)
        {
            project.Dispose();
        }

        Scratch.Dispose();
        await Bus.DisposeAsync();
    }

    private static string Body => "Read README.md and finish with a REPORT section; change nothing.\n";

    private static List<string> ReadLines(string path)
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

    private async Task<string?> HeadShaOrNullAsync(CancellationToken ct)
    {
        try
        {
            return await Bus.HeadShaAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
