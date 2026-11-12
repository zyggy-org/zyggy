using System.CommandLine;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Health;
using Zyggy.Core.Memory;
using Zyggy.Core.Policy;
using Zyggy.Core.Registry;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy discover</c> (founding spec §3, §4 "Registry file" as amended by A14-1; spec 14 CLI surface): scans this machine's <c>devRoots</c>
/// and shows (<c>--dry-run</c>) the registry document it would publish. Ordering, option reading and rendering only — the scan and the
/// document are Core's (<see cref="DiscoveryScanner"/>, <see cref="RegistryDocument"/>). Notices go to stderr, one line each, never file content.
/// </summary>
internal static class DiscoverCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the registry document this machine would publish; write nothing." };
        var command = new Command("discover", "Scan this machine's devRoots for Claude Code projects and publish its registry file (--dry-run prints it instead).")
        {
            dryRun, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(environment, io, configure, parseResult, parseResult.GetValue(dryRun), ct));
        return command;
    }

    private static async Task<int> RunAsync(
        CliEnvironment environment,
        VerbIo io,
        Action<IServiceCollection>? configure,
        ParseResult parseResult,
        bool dryRun,
        CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        string? patternsError = null;
        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, s =>
        {
            patternsError = s.AddSecretPatterns(environment.Variables);
            configure?.Invoke(s);
        });

        int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
        if (checkout != ExitCodes.Ok)
        {
            return checkout;
        }

        TimeProvider clock = services.GetRequiredService<TimeProvider>();
        HealthLevel? health = null;
        if (!dryRun)
        {
            (bool loopback, health) = await ReadHealthAsync(environment, services, clock, ct);
            if (!loopback)
            {
                return BusHost.ConfigurationError(io, "health url must be loopback");
            }
        }

        if (patternsError is not null && configuration.Discovery.Detail == DiscoveryDetail.Full)
        {
            await io.Error.WriteLineAsync($"zyggy: discover: secret patterns not registered ({patternsError}); publishing names only");
        }

        NodePolicy policy = await services.GetRequiredService<IPolicySource>().GetAsync(ct);
        ScanResult scan = await services.GetRequiredService<DiscoveryScanner>().ScanAsync(policy.DevRoots, configuration.Discovery.Detail, ct);
        foreach (ScanNotice notice in scan.Notices)
        {
            if (NoticeLine(notice) is { } line)
            {
                await io.Error.WriteLineAsync(line);
            }
        }

        RegistryDocument document = RegistryDocument.FromScan(configuration, scan, clock.GetUtcNow(), health: null);
        if (dryRun)
        {
            await io.Out.WriteAsync(Encoding.UTF8.GetString(document.Serialize()));
            return ExitCodes.Ok;
        }

        RegistryPublishResult result = await services.GetRequiredService<RegistryPublisher>().PublishAsync(document, health, ct);
        int count = document.Projects.Count;
        switch (result.Outcome)
        {
            case PublishOutcome.Published:
                await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"registry: {count} project(s), published"));
                return ExitCodes.Ok;
            case PublishOutcome.Unchanged:
                await io.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"registry: {count} project(s), unchanged"));
                return ExitCodes.Ok;
            case PublishOutcome.Busy:
                await io.Error.WriteLineAsync("zyggy: registry: checkout busy");
                return ExitCodes.NoRun;
            default:
                await io.Error.WriteLineAsync("zyggy: registry: push failed: " + result.Detail);
                return ExitCodes.NoRun;
        }
    }

    /// <summary>
    /// The local node's verdict for the <c>health</c> key (spec 14): green, amber or red when a node answers on the loopback health URL,
    /// <see langword="null"/> when none does (the publisher keeps the bus value or omits the key). A non-loopback URL is refused.
    /// </summary>
    private static async Task<(bool Loopback, HealthLevel? Health)> ReadHealthAsync(CliEnvironment environment, ServiceProvider services, TimeProvider clock, CancellationToken ct)
    {
        Uri url;
        HealthProbe probe;
        try
        {
            url = environment.Get(BusHost.HealthUrlVariable) is { } text ? new Uri(text) : HealthClient.DefaultUrl;
            using var client = new HealthClient(url, clock);
            probe = await client.ReadAsync(ct);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return (false, null);
        }

        if (probe is not Reachable reachable)
        {
            return (true, null);
        }

        HealthVerdict verdict = HealthRules.Evaluate(reachable.Health, clock.GetUtcNow(), services.GetRequiredService<PollOptions>(), BusHost.InformationalVersion(), url.ToString());
        return (true, verdict.Level is HealthLevel.Green or HealthLevel.Amber or HealthLevel.Red ? verdict.Level : null);
    }

    /// <summary>One stderr line per notice (spec 14, Failure modes); the patterns notice is the verb's own line above, so it prints nothing here.</summary>
    internal static string? NoticeLine(ScanNotice notice) => notice.Kind switch
    {
        ScanNoticeKind.DuplicateName => $"discover: duplicate name {notice.Subject}: {notice.Detail}",
        ScanNoticeKind.RootMissing => $"discover: root missing {notice.Subject}",
        ScanNoticeKind.RootUnreadable => $"discover: root unreadable {notice.Subject}",
        ScanNoticeKind.NameUnusable => $"discover: name unusable for {notice.Subject}",
        ScanNoticeKind.ProjectCapReached => $"discover: project cap reached ({notice.Detail})",
        ScanNoticeKind.EntryCapReached => $"discover: entry cap reached in {notice.Subject} ({notice.Detail})",
        ScanNoticeKind.SecretRedacted => $"discover: secret pattern {notice.Detail} redacted in {notice.Subject}",
        ScanNoticeKind.SlowScan => $"discover: scan took {notice.Detail}",
        ScanNoticeKind.PatternsUnavailable => null,
        _ => $"discover: {notice.Kind} {notice.Subject}",
    };
}
