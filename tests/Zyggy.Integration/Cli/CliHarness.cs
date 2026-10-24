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
/// <c>central</c> and <c>home-laptop</c>, <c>central</c> working in the sender clone and <c>home-laptop</c> in the node clone, a file secret
/// store with <c>acme/1</c> and the test PAT, every process recorded, the doorbell answered from the bare repository.
/// </summary>
internal sealed class CliHarness : IAsyncDisposable
{
    public static readonly TenantId Acme = TenantId.Parse("acme");

    public static readonly MachineName Central = MachineName.Parse("central");

    public static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

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

    /// <summary>Writes a <c>node.json</c> for <paramref name="machine"/> over <paramref name="busLocalPath"/>; signs with <c>acme/1</c> unless <paramref name="omitSignWith"/>.</summary>
    public string WriteNodeJson(MachineName machine, string busLocalPath, string name, bool omitSignWith = false, string? secretsRoot = null, string? nodeDir = null) =>
        NodeJson.Write(
            Scratch.File("config-" + name),
            Acme,
            machine,
            omitSignWith ? null : TestKeys.Acme1,
            busLocalPath: busLocalPath,
            remote: TestHost.Remote.HttpsUrl!.ToString(),
            secretsRoot: secretsRoot ?? SecretsRoot,
            claudePath: FakeClaude.ExecutablePath,
            nodeDir: nodeDir ?? Scratch.File("node-" + name));

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
        CancellationToken ct = default)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = nodeJson };
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
