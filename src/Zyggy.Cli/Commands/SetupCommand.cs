using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Processes;
using Zyggy.Core.Setup;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy setup</c> (spec 42 CLI surface): a thin command over <see cref="SetupVerb"/>. It reads the options, refuses a malformed tenant,
/// machine or key id (exit 2), resolves the paths through <see cref="SetupPaths"/>, builds the <see cref="SetupRequest"/> and returns the verb's
/// exit. Every decision and every word lives in Core; the secret store is composed from the completed <c>node.json</c> like every other verb's.
/// </summary>
internal static class SetupCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var tenant = new Option<string>("--tenant") { Description = "The tenant this laptop belongs to; node.json must name the same.", Required = true };
        var machine = new Option<string>("--machine") { Description = "This laptop's machine name; node.json must name the same (never central).", Required = true };
        var signWith = new Option<string?>("--sign-with") { Description = "The signing key <tenant>/<n> written as signWith when node.json has none." };
        var corePath = new Option<string?>("--core-path") { Description = "The zyggy-core checkout (default: C:/agents/zyggy-core on Windows, ~/agents/zyggy-core elsewhere)." };
        var claudeDir = new Option<string?>("--claude-dir") { Description = "The user-scope Claude Code directory (default: CLAUDE_CONFIG_DIR, then ~/.claude)." };
        var coreRemote = new Option<string?>("--core-remote") { Description = "The zyggy-core remote, for tests and forks (default: the public template repository)." };
        var command = new Command("setup", "Wire this laptop as a node: complete node.json, check out zyggy-core, wire the user-scope Claude Code settings and skills, name every missing secret.")
        {
            BusHost.ConfigOption, tenant, machine, signWith, corePath, claudeDir, coreRemote,
        };
        command.SetAction((parseResult, ct) => RunAsync(
            environment,
            io,
            configure,
            new Options(parseResult.GetValue(BusHost.ConfigOption), parseResult.GetValue(tenant)!, parseResult.GetValue(machine)!, parseResult.GetValue(signWith), parseResult.GetValue(corePath), parseResult.GetValue(claudeDir), parseResult.GetValue(coreRemote)),
            ct));
        return command;
    }

    private static async Task<int> RunAsync(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure, Options options, CancellationToken ct)
    {
        if (!TenantId.TryParse(options.Tenant, out TenantId? tenant))
        {
            return await UsageAsync(io, "--tenant", "must be a lowercase label");
        }

        if (!MachineName.TryParse(options.Machine, out MachineName? machine))
        {
            return await UsageAsync(io, "--machine", "must be a lowercase label");
        }

        KeyId? signWith = null;
        if (options.SignWith is not null && !KeyId.TryParse(options.SignWith, out signWith))
        {
            return await UsageAsync(io, "--sign-with", "must be <tenant>/<n>");
        }

        if (!Uri.TryCreate(options.CoreRemote ?? SetupRequest.DefaultCoreRemote, UriKind.Absolute, out Uri? remote))
        {
            return await UsageAsync(io, "--core-remote", "must be an absolute URL");
        }

        IReadOnlyDictionary<string, string?> variables = environment.Variables;
        var request = new SetupRequest(
            NodeConfigurationReader.ResolvePath(options.Config, variables),
            tenant,
            machine,
            signWith,
            options.CorePath ?? SetupPaths.DefaultCorePath(variables),
            remote,
            SetupPaths.ResolveClaudeDir(options.ClaudeDir, variables),
            SetupPaths.ResolveUserProfile(variables),
            SetupPaths.ResolveLocalAppData(variables),
            TimeProvider.System.GetUtcNow());

        // The tools (process runner, the git isolation hook of the harness) and, once node.json is complete, the node's services for its store.
        var toolServices = new ServiceCollection().AddProcessRunner();
        configure?.Invoke(toolServices);
        await using ServiceProvider tools = toolServices.BuildServiceProvider();
        var nodeServices = new List<ServiceProvider>();
        try
        {
            SetupVerb verb = SetupVerb.FromServices(tools, variables, configuration =>
            {
                ServiceProvider services = BusHost.BuildServices(configuration, verbose: false, environment, configure);
                nodeServices.Add(services);
                return services;
            });
            return await verb.RunAsync(request, io, ct);
        }
        finally
        {
            foreach (ServiceProvider services in nodeServices)
            {
                await services.DisposeAsync();
            }
        }
    }

    private static async Task<int> UsageAsync(VerbIo io, string option, string detail)
    {
        await io.Error.WriteLineAsync("zyggy: " + option + ": " + detail);
        return SetupExit.Usage;
    }

    private sealed record Options(string? Config, string Tenant, string Machine, string? SignWith, string? CorePath, string? ClaudeDir, string? CoreRemote);
}
