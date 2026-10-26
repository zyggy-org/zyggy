using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Zyggy.Core.Configuration;
using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Verbs;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy secret set | list | remove</c> (spec 09 AC-7/AC-8/AC-11): a thin command over the three Core verbs. A value is never
/// given on the command line — it comes from a pipe, from a hidden prompt, or is generated — so it never lands in a shell history
/// or a process list; the store is the one <c>node.json</c>'s <c>secrets.store</c> names, composed like every other verb's.
/// </summary>
internal static class SecretCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var name = new Argument<string>("name") { Description = "The tenant-relative secret name, for example hmac/1 or github-pat." };
        var hex = new Option<bool>("--hex") { Description = "Standard input holds the value as hexadecimal text." };
        var generate = new Option<bool>("--generate") { Description = "Generate a 32-byte key instead of reading a value (hmac/<n> names only)." };
        var show = new Option<bool>("--show") { Description = "With --generate: print the key once, as hex, to carry it to the tenant's other machines." };
        var set = new Command("set", "Store a secret from standard input or a hidden prompt (the value is never given on the command line).") { name, hex, generate, show, BusHost.ConfigOption };
        set.SetAction((parseResult, ct) => SetAsync(environment, io, configure, parseResult, parseResult.GetValue(name)!, parseResult.GetValue(hex), parseResult.GetValue(generate), parseResult.GetValue(show), ct));

        var list = new Command("list", "The names this machine holds for its tenant, with size, fingerprint, store and role; never a value.") { BusHost.ConfigOption };
        list.SetAction((parseResult, ct) => ListAsync(environment, io, configure, parseResult, ct));

        var removeName = new Argument<string>("name") { Description = "The tenant-relative secret name to remove." };
        var remove = new Command("remove", "Remove a secret from this machine's store; the signWith key is refused.") { removeName, BusHost.ConfigOption };
        remove.SetAction((parseResult, ct) => RemoveAsync(environment, io, configure, parseResult, parseResult.GetValue(removeName)!, ct));

        return new Command("secret", "This machine's secrets (tenant-scoped; the store named by node.json secrets.store).") { set, list, remove };
    }

    private static async Task<int> SetAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string nameText, bool hex, bool generate, bool show, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (!SecretName.TryParse(nameText, out SecretName? name))
        {
            return BusHost.ConfigurationError(io, "secret name must be segments of [a-z0-9-] separated by '/'");
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, verbose: false, environment, configure);
        var verb = new SecretSetVerb(services.GetRequiredService<ISecretStore>(), configuration.Tenant, Logger<SecretSetVerb>(services));
        IHiddenInput hidden = services.GetService<IHiddenInput>() ?? new ConsoleHiddenInput();
        return await verb.RunAsync(new SecretSetRequest(name, hex, generate, show), io, io.InputRedirected ? StdinKind.Redirected : StdinKind.Terminal, hidden, ct);
    }

    private static async Task<int> ListAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, verbose: false, environment, configure);
        return await new SecretListVerb(services.GetRequiredService<ISecretStore>(), configuration.Tenant, configuration.SignWith).RunAsync(io, ct);
    }

    private static async Task<int> RemoveAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, ParseResult parseResult, string nameText, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (!SecretName.TryParse(nameText, out SecretName? name))
        {
            return BusHost.ConfigurationError(io, "secret name must be segments of [a-z0-9-] separated by '/'");
        }

        await using ServiceProvider services = BusHost.BuildServices(configuration, verbose: false, environment, configure);
        var verb = new SecretRemoveVerb(services.GetRequiredService<ISecretStore>(), configuration.Tenant, configuration.SignWith, Logger<SecretRemoveVerb>(services));
        return await verb.RunAsync(name, io, ct);
    }

    private static ILogger<T> Logger<T>(ServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger<T>() ?? NullLogger<T>.Instance;
}
