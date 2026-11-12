using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Cli;

/// <summary>
/// The bus and the two machines' <c>node.json</c> files for the in-process CLI verbs (plan 08 Step 3): a bare repository seeded for
/// <c>central</c> and <c>home-laptop</c> (<c>work-laptop</c> only on request, <see cref="AddWorkLaptopAsync"/>: <c>zyggy status</c> lists every machine under <c>nodes/</c>), <c>central</c> working in the sender clone and <c>home-laptop</c> in the node clone, a file secret
/// store with <c>acme/1</c> and the test PAT, every process recorded, the doorbell answered from the bare repository.
/// </summary>
internal sealed class CliHarness : IAsyncDisposable
{
    public static readonly TenantId Acme = TenantId.Parse("acme");

    public static readonly MachineName Central = MachineName.Parse("central");

    public static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    public static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private CliHarness()
    {
        Bus = new BusRepoFixture(Acme);
        Scratch = new ScratchDirectory();
        Hooks = TestHost.Hooks();
    }

    public BusRepoFixture Bus { get; }

    public ScratchDirectory Scratch { get; }

    public HookedProcessRunner Hooks { get; }

    public InMemorySecretStore Store { get; private set; } = null!;

    public string SecretsRoot => Scratch.File("secrets");

    /// <summary>The golden secret-pattern file every run reads unless a test withholds it (spec 12).</summary>
    public static string PatternsPath => Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");

    public string CentralNodeJson { get; private set; } = null!;

    public string HomeLaptopNodeJson { get; private set; } = null!;

    public static async Task<CliHarness> CreateAsync(CancellationToken ct, bool seedSecrets = true)
    {
        var harness = new CliHarness();
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

        harness.CentralNodeJson = harness.WriteNodeJson(Central, harness.Bus.SenderDir, "central");
        harness.HomeLaptopNodeJson = harness.WriteNodeJson(HomeLaptop, harness.Bus.CloneDir, "home-laptop");
        return harness;
    }

    /// <summary>
    /// Opt-in third machine (plan 41 Step 5): clones the bare repository for <c>work-laptop</c> and writes its <c>node.json</c> — no
    /// <c>archive.oneDriveFolder</c>, <c>dlp.enabled: true</c>, optionally a lower <c>archive.itemMaxBytes</c> — and returns its path. Nothing
    /// is seeded under <c>nodes/work-laptop/</c>, so the tests that do not call this see the bus exactly as before.
    /// </summary>
    public async Task<string> AddWorkLaptopAsync(CancellationToken ct, long? archiveItemMaxBytes = null, string? oneDriveFolder = null)
    {
        string clone = await Bus.CloneAsync("work", ct);
        return NodeJson.Write(
            Scratch.File("config-work-laptop"),
            Acme,
            WorkLaptop,
            TestKeys.Acme1,
            busLocalPath: clone,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: Scratch.File("node-work-laptop"),
            archiveItemMaxBytes: archiveItemMaxBytes,
            oneDriveFolder: oneDriveFolder,
            dlpEnabled: true);
    }

    /// <summary>The checkout <see cref="AddWorkLaptopAsync"/> cloned for <c>work-laptop</c>.</summary>
    public string WorkLaptopDir => Path.Combine(Bus.RootDir, "work");

    /// <summary>Writes a <c>node.json</c> for <paramref name="machine"/> over <paramref name="busLocalPath"/>; signs with <c>acme/1</c> unless <paramref name="omitSignWith"/>.</summary>
    public string WriteNodeJson(
        MachineName machine,
        string busLocalPath,
        string name,
        bool omitSignWith = false,
        string? secretsRoot = null,
        string? nodeDir = null,
        IReadOnlyList<string>? devRoots = null,
        string? discoveryJson = null) =>
        NodeJson.Write(
            Scratch.File("config-" + name),
            Acme,
            machine,
            omitSignWith ? null : TestKeys.Acme1,
            busLocalPath: busLocalPath,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: secretsRoot ?? SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: nodeDir ?? Scratch.File("node-" + name),
            devRoots: devRoots,
            discoveryJson: discoveryJson);

    /// <summary>The test-side registrations on top of the production ones: the doorbell, the fixture's git configuration, no jitter, recorded processes.</summary>
    public void Configure(IServiceCollection services)
    {
        services.Configure<GitHubBusOptions>(o =>
        {
            o.HandlerFactory = () => new BareRepoHeadHandler(HeadShaOrNullAsync);
            o.GitEnvironment = Bus.GitEnvironment;
            o.Jitter = () => 0;
        });
        services.AddSingleton<IProcessRunner>(Hooks);
    }

    /// <summary>Runs a verb in process with <paramref name="nodeJson"/> as <c>ZYGGY_NODE_JSON</c>.</summary>
    public Task<(int Exit, VerbConsole Console)> RunAsync(
        string[] args,
        string nodeJson,
        string? stdin = null,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        Action<IServiceCollection>? more = null,
        bool withPatterns = true,
        CancellationToken ct = default)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = nodeJson };
        if (withPatterns)
        {
            environment["ZYGGY_SECRET_PATTERNS"] = PatternsPath;
        }

        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string?>())
        {
            environment[name] = value;
        }

        return ZyggyCliInProcess.RunAsync(
            args,
            environment,
            s =>
            {
                Configure(s);
                more?.Invoke(s);
            },
            stdin,
            ct);
    }

    /// <summary>The <c>nodeDir</c> <see cref="WriteNodeJson"/> gave the machine named <paramref name="name"/>.</summary>
    public string NodeDirOf(string name) => Scratch.File("node-" + name);

    public Task<string> PorcelainAsync(string checkout, CancellationToken ct) => Bus.RunGitAsync(checkout, ["status", "--porcelain"], ct);

    public async Task<int> CommitCountOnMainAsync(CancellationToken ct) =>
        int.Parse(await Bus.RunGitAsync(Bus.BareDir, ["rev-list", "--count", "main"], ct), CultureInfo.InvariantCulture);

    public async Task<Zyggy.Core.Envelope.Envelope> ParseOnMainAsync(string relativePath, CancellationToken ct)
    {
        EnvelopeResult parsed = await new EnvelopeSigner(Store).ParseAndVerifyAsync(await Bus.ShowBytesAsync(relativePath, ct), Acme, ct);
        parsed.IsAccepted.Should().BeTrue(parsed.Rejection?.ToString());
        return parsed.Envelope!;
    }

    public async ValueTask DisposeAsync()
    {
        Scratch.Dispose();
        await Bus.DisposeAsync();
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
