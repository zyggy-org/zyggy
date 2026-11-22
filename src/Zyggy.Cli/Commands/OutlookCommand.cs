using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Memory;
using Zyggy.Core.Outlook;
using Zyggy.Core.Verbs;

using ContextRequest = Zyggy.Core.Bus.ContextRequest;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy outlook policy | count | list | read | brief | brief-done | drafts | share | send | move | guard | settle</c> (spec 58 slices 2–7): the work mailbox's tools
/// on the work laptop. Argument parsing, the policy check, the reads and the sensitive-data filter are Core's <see cref="OutlookVerbHost"/>;
/// this class gives it the console, the context path to Central for <c>share</c> (the <c>zyggy context</c> path: signed context envelope,
/// secret patterns, bus checkout) and, in tests, the Outlook access and clock the service hook registers.
/// </summary>
internal static class OutlookCommand
{
    /// <summary>The <c>--help</c> entry; <see cref="RunAsync"/> runs the verb with its own parser.</summary>
    public static Command Create() =>
        new("outlook", "The work mailbox on the work laptop, read through classic Outlook (zyggy outlook policy | count | list | read | brief | brief-done | drafts | share | send | move | guard | settle ...); refuses without the policy record; never saves anything in Outlook; share sends only what passes the sensitive-data filter; send and move run only in an attended session, through the guard hook and one permission prompt each.");

    /// <summary>Runs the verb with the arguments after <c>outlook</c>.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        if (configure is null)
        {
            var host = new OutlookVerbHost(environment.Variables) { ContextSender = Sender(environment, null) };
            return await host.RunAsync(args, io, ct);
        }

        var probe = new ServiceCollection();
        configure(probe);
        await using ServiceProvider configured = probe.BuildServiceProvider();
        OutlookVerbHost tested = OutlookVerbHost.FromServices(configured, environment.Variables);
        tested.ContextSender = Sender(environment, configure);
        return await tested.RunAsync(args, io, ct);
    }

    // The zyggy context path for share's filtered lines: the same checks, signer, secret patterns and rendering as `zyggy context`.
    private static Func<NodeConfiguration, ContextRequest, VerbIo, CancellationToken, Task<int>> Sender(CliEnvironment environment, Action<IServiceCollection>? configure) =>
        async (configuration, request, io, ct) =>
        {
            if (ContextRequest.CheckSender(configuration.Machine) is { } sender)
            {
                await io.Error.WriteLineAsync($"zyggy: {sender.Field}: {sender.Detail}");
                return ExitCodes.Usage;
            }

            if (configuration.SignWith is null)
            {
                return BusHost.ConfigurationError(io, "signWith missing");
            }

            string? patternsError = null;
            await using ServiceProvider services = BusHost.BuildServices(configuration, verbose: false, environment, s =>
            {
                patternsError = s.AddSecretPatterns(environment.Variables);
                configure?.Invoke(s);
            });
            if (patternsError is not null)
            {
                return BusHost.ConfigurationError(io, patternsError);
            }

            int checkout = await BusHost.CheckCheckoutAsync(services, configuration, io, ct);
            if (checkout != ExitCodes.Ok)
            {
                return checkout;
            }

            SubmitResult result = await services.GetRequiredService<ContextSubmitter>().SubmitAsync(request, ct);
            return await ContextCommand.RenderAsync(io, configuration, result, storedLine: null);
        };
}
