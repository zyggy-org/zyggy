using System.CommandLine;
using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Configuration;
using Zyggy.Core.Secrets;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy secret set &lt;name&gt; [--hex]</c> (spec 04, ex-OQ2; the first slice of deliverable 09): stores a secret for the
/// node's tenant, read from standard input only — a value on the command line does not exist as a form, so it never lands in a
/// shell history or a process list. Prints the name and the byte count, never the value.
/// </summary>
internal static class SecretCommand
{
    public static Command Create(CliEnvironment environment)
    {
        var name = new Argument<string>("name") { Description = "The tenant-relative secret name, for example hmac/1 or github-pat." };
        var hex = new Option<bool>("--hex") { Description = "Standard input holds the value as hexadecimal text." };
        var set = new Command("set", "Store a secret read from standard input (the value is never given on the command line).") { name, hex, BusHost.ConfigOption };
        set.SetAction((parseResult, ct) => SetAsync(environment, parseResult, parseResult.GetValue(name)!, parseResult.GetValue(hex), ct));
        return new Command("secret", "This machine's secrets (file store, tenant-scoped).") { set };
    }

    private static async Task<int> SetAsync(CliEnvironment environment, ParseResult parseResult, string nameText, bool hex, CancellationToken ct)
    {
        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment);
        if (configuration is null)
        {
            return exitCode;
        }

        if (!SecretName.TryParse(nameText, out SecretName? name))
        {
            return BusHost.ConfigurationError("secret name must be segments of [a-z0-9-] separated by '/'");
        }

        string input = await Console.In.ReadToEndAsync(ct);
        if (input.EndsWith("\r\n", StringComparison.Ordinal))
        {
            input = input[..^2];
        }
        else if (input.EndsWith('\n'))
        {
            input = input[..^1];
        }

        if (input.Length == 0)
        {
            Console.Error.WriteLine("zyggy: no value on standard input");
            return ExitCodes.Usage;
        }

        byte[] bytes;
        if (hex)
        {
            try
            {
                bytes = Convert.FromHexString(input.Trim());
            }
            catch (FormatException)
            {
                Console.Error.WriteLine("zyggy: value is not hexadecimal");
                return ExitCodes.Usage;
            }
        }
        else
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(input);
        }

        await using ServiceProvider services = new ServiceCollection().AddFileSecretStore(o => o.RootDirectory = configuration.Secrets.Root).BuildServiceProvider();
        await services.GetRequiredService<ISecretStore>().SetAsync(configuration.Tenant, name, bytes, ct);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stored {configuration.Tenant.Value}/{name.Value} ({bytes.Length} bytes)"));
        return ExitCodes.Ok;
    }
}
