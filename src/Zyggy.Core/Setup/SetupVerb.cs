using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Setup;

/// <summary>
/// <c>zyggy setup</c> (spec 42): wires a laptop as a node in one run. Items run and print in <see cref="SetupItem"/> order, one
/// <see cref="SetupLine"/> each on stdout; a <c>central</c> machine is refused (exit 2) and a <c>node.json</c> refusal (exit 3) stops the run
/// before anything else is touched (plan 42 A2). The exit is <see cref="SetupExit.Worst"/>. Secrets are read as names and fingerprints only.
/// </summary>
public sealed class SetupVerb
{
    private const string Central = "central";

    /// <summary>The node skills in output order; the two of deliverable 15 may be absent from a checkout that predates them.</summary>
    private static readonly (SetupItem Item, string Name, bool MayBeAbsent)[] Skills =
    [
        (SetupItem.SkillRemember, "remember", false),
        (SetupItem.SkillArchive, "archive", false),
        (SetupItem.SkillBus, "bus", true),
        (SetupItem.SkillDiscover, "discover", true),
    ];

    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly Func<NodeConfiguration, ISecretStore> _stores;
    private readonly CoreCheckout _checkout;

    /// <summary>Creates the verb.</summary>
    /// <param name="processes">The process runner git runs through.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="environment">The process environment (<c>PATH</c> for <c>claude.exe</c>).</param>
    /// <param name="gitEnvironment">Extra variables for every git call (the test harness's isolation hook; empty in production).</param>
    /// <param name="stores">Builds the secret store from the completed <c>node.json</c> (its <c>secrets</c> section names the kind, plan 42 A4).</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public SetupVerb(IProcessRunner processes, TimeProvider clock, IReadOnlyDictionary<string, string?> environment, IReadOnlyDictionary<string, string?> gitEnvironment, Func<NodeConfiguration, ISecretStore> stores)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(gitEnvironment);
        ArgumentNullException.ThrowIfNull(stores);
        _environment = environment;
        _stores = stores;
        _checkout = CoreCheckout.Create(processes, gitEnvironment, clock);
    }

    /// <summary>
    /// Creates the verb from a host's services: the <see cref="IProcessRunner"/>, the <see cref="TimeProvider"/> (the system clock when none is
    /// registered) and the git environment of <see cref="GitHubBusOptions"/> from <paramref name="services"/>; the secret store from the provider
    /// <paramref name="storeServices"/> builds for the completed <c>node.json</c> (the caller owns and disposes that provider).
    /// </summary>
    /// <param name="services">The host's services (process runner, options).</param>
    /// <param name="environment">The process environment.</param>
    /// <param name="storeServices">Builds the node's services for the completed <c>node.json</c>.</param>
    /// <returns>The verb.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public static SetupVerb FromServices(IServiceProvider services, IReadOnlyDictionary<string, string?> environment, Func<NodeConfiguration, IServiceProvider> storeServices)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storeServices);
        IReadOnlyDictionary<string, string?> gitEnvironment = services.GetService<IOptions<GitHubBusOptions>>()?.Value.GitEnvironment ?? new Dictionary<string, string?>();
        return new SetupVerb(
            services.GetRequiredService<IProcessRunner>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            environment,
            gitEnvironment,
            configuration => storeServices(configuration).GetRequiredService<ISecretStore>());
    }

    /// <summary>Runs every item in order, printing each line as it is produced.</summary>
    /// <param name="request">The run.</param>
    /// <param name="io">The verb's console.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code (<see cref="SetupExit"/>).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> or <paramref name="io"/> is null.</exception>
    public async Task<int> RunAsync(SetupRequest request, VerbIo io, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(io);
        if (request.Machine.Value == Central)
        {
            await io.Out.WriteLineAsync(new SetupLine(SetupItem.NodeJson, SetupOutcome.Failed, "setup is for laptops; Central is installed by install-node.md").Render(null)).ConfigureAwait(false);
            return SetupExit.Usage;
        }

        var lines = new List<SetupLine>();
        NodeJsonResult nodeJson = NodeJsonCompletion.Apply(request, ClaudeLocator.Find(_environment, request.UserProfile));
        await PrintAsync(io, lines, [nodeJson.Line], request.SignWith).ConfigureAwait(false);
        if (nodeJson.Configuration is not { SignWith: { } signWith } configuration)
        {
            return SetupExit.Worst(lines);
        }

        SetupLine core = await _checkout.ApplyAsync(request.CorePath, request.CoreRemote, ct).ConfigureAwait(false);
        await PrintAsync(io, lines, [core], signWith).ConfigureAwait(false);

        // The settings env value is written whatever the checkout's outcome; the skills need the checkout's files (plan 42 R5).
        string corePath = Path.GetFullPath(request.CorePath);
        string claudeDir = Path.GetFullPath(request.ClaudeDir);
        IReadOnlyList<SetupLine> settings = ClaudeSettingsMerge.Apply(SetupPaths.SettingsFile(claudeDir), SetupPaths.SecretPatterns(corePath), request.BackupStamp);
        await PrintAsync(io, lines, settings, signWith).ConfigureAwait(false);
        foreach ((SetupItem item, string name, bool mayBeAbsent) in Skills)
        {
            SetupLine skill = SkillInstaller.Apply(
                item,
                SetupPaths.SkillSource(corePath, name),
                SetupPaths.SkillTarget(claudeDir, name),
                SetupPaths.SkillBackup(request.LocalAppData, request.BackupStamp, name),
                mayBeAbsent ? corePath : null);
            await PrintAsync(io, lines, [skill], signWith).ConfigureAwait(false);
        }

        IReadOnlyList<SetupLine> secrets = await SecretReport.ReportAsync(_stores(configuration), request, signWith, configuration.Secrets.Store, ct).ConfigureAwait(false);
        await PrintAsync(io, lines, secrets, signWith).ConfigureAwait(false);
        return SetupExit.Worst(lines);
    }

    private static async Task PrintAsync(VerbIo io, List<SetupLine> lines, IEnumerable<SetupLine> produced, Envelope.KeyId? signWith)
    {
        foreach (SetupLine line in produced)
        {
            lines.Add(line);
            await io.Out.WriteLineAsync(line.Render(signWith)).ConfigureAwait(false);
        }
    }
}
