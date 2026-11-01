using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// <c>zyggy archive</c> (founding spec §3 and §9 as amended by deliverable 41, W41-4/W41-14): a laptop hands one file to Central's
/// project archive as a signed item envelope. Ordering, option reading and rendering only — every check is in Core
/// (<see cref="ItemRequest"/>, <see cref="ItemSubmitter"/>). Exit 0 sent, 2 usage / <c>central</c> / round-trip refusal, 3 configuration,
/// 5 refused, 6 not sent, 7 committed locally.
/// </summary>
internal static class ArchiveCommand
{
    public static Command Create(CliEnvironment environment, VerbIo io, Action<IServiceCollection>? configure)
    {
        var project = Text("--project", "The project the item is archived under (a slug).");
        var name = Text("--name", "The item's name, one line, up to 100 characters.");
        var description = Text("--description", "What the item is, one line, up to 149 characters.");
        var file = Text("--file", "The absolute path of the file to send.");
        var slug = Text("--slug", "The item's slug (default: derived from --name).");
        var via = Text("--via", "bus: send through the bus even when a OneDrive archive folder is configured.");
        var command = new Command("archive", "Send one file to Central's project archive from a laptop (archive skill): checked here, then through your OneDrive archive folder or the bus.")
        {
            project, name, description, file, slug, via, BusHost.ConfigOption, BusHost.VerboseOption,
        };
        command.SetAction((parseResult, ct) => RunAsync(
            environment,
            io,
            configure,
            parseResult,
            (parseResult.GetValue(project), parseResult.GetValue(name), parseResult.GetValue(description), parseResult.GetValue(file), parseResult.GetValue(slug), parseResult.GetValue(via)),
            ct));
        return command;
    }

    private static Option<string?> Text(string option, string description) => new(option) { Description = description, Arity = ArgumentArity.ExactlyOne };

    private static async Task<int> RunAsync(
        CliEnvironment environment,
        VerbIo io,
        Action<IServiceCollection>? configure,
        ParseResult parseResult,
        (string? Project, string? Name, string? Description, string? File, string? Slug, string? Via) options,
        CancellationToken ct)
    {
        if (ItemRequest.TryCreate(options.Project, options.Name, options.Description, options.File, options.Slug, options.Via, out ItemRequest? request) is { } usage)
        {
            await io.Error.WriteLineAsync("zyggy: " + usage.Detail);
            return ExitCodes.Usage;
        }

        // AC-11: an unattended run (the job runner sets ZYGGY_HOOKS=off) never archives; the file is not opened.
        if (environment.Variables.TryGetValue("ZYGGY_HOOKS", out string? hooks) && hooks == "off")
        {
            await io.Error.WriteLineAsync("zyggy: refused: " + ArchiveRefusalWire.ToWire(ArchiveRefusal.Unattended));
            return ExitCodes.Refused;
        }

        (NodeConfiguration? configuration, int exitCode) = BusHost.LoadConfiguration(parseResult, environment, io);
        if (configuration is null)
        {
            return exitCode;
        }

        if (ItemRequest.CheckSender(configuration.Machine) is { } sender)
        {
            await io.Error.WriteLineAsync("zyggy: " + sender.Detail);
            return ExitCodes.Usage;
        }

        if (configuration.SignWith is null)
        {
            return BusHost.ConfigurationError(io, "signWith missing");
        }

        string nodeJsonPath = NodeConfigurationReader.ResolvePath(parseResult.GetValue(BusHost.ConfigOption), environment.Variables);
        string? patternsError = null;
        await using ServiceProvider services = BusHost.BuildServices(configuration, parseResult.GetValue(BusHost.VerboseOption), environment, s =>
        {
            patternsError = s.AddSecretPatterns(environment.Variables);
            s.AddSingleton(new ItemSubmitterOptions { Environment = environment.Variables, NodeJsonPath = nodeJsonPath });
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

        ItemSubmission submission = await services.GetRequiredService<ItemSubmitter>().SubmitAsync(request!, ct);
        return await RenderAsync(io, configuration, submission);
    }

    /// <summary>Renders the outcome: on 0/7 the item line and the AC-18 line; a refusal, usage fault or OneDrive failure as one stderr line; every other line as <c>zyggy context</c> prints it.</summary>
    private static async Task<int> RenderAsync(VerbIo io, NodeConfiguration configuration, ItemSubmission submission)
    {
        switch (submission.Result)
        {
            case ItemRefused refused:
                await io.Error.WriteLineAsync("zyggy: refused: " + Refusal(refused));
                return ExitCodes.Refused;
            case ItemUsage usage:
                await io.Error.WriteLineAsync("zyggy: " + usage.Message);
                return ExitCodes.Usage;
            case OneDriveUnavailable unavailable:
                await io.Error.WriteLineAsync($"zyggy: OneDrive folder {unavailable.Folder} is not available ({unavailable.Reason}): fix the folder, or archive through the bus with --via bus");
                return ExitCodes.Failed;
            case OneDriveWriteFailed writeFailed:
                await io.Error.WriteLineAsync("zyggy: OneDrive write failed: " + writeFailed.Detail);
                return ExitCodes.Failed;
            default:
                return await ContextCommand.RenderAsync(io, configuration, submission.Result, ItemLine(submission), noun: "item");
        }
    }

    /// <summary>
    /// The second output line (spec 41 AC-18): <c>&lt;project&gt;/&lt;drive_name&gt; "&lt;name&gt;" (&lt;media type&gt;, &lt;size&gt;) — via bus|OneDrive — check: zyggy status --id &lt;id&gt;</c>;
    /// <see langword="null"/> unless the item was published.
    /// </summary>
    private static string? ItemLine(ItemSubmission submission)
    {
        EnvelopeId? id = submission.Result switch
        {
            Submitted s => s.Id,
            SubmitDeferred d => d.Id,
            _ => null,
        };
        if (id is null)
        {
            return null;
        }

        string route = submission.Route == ItemRoute.OneDrive ? "OneDrive" : "bus";
        return $"{submission.ItemPath} \"{submission.Name}\" ({submission.MediaType}, {submission.SizeText}) — via {route} — check: zyggy status --id {id.Value}";
    }

    // secret_pattern's detail is "<pattern> (line <n>|name|description)" (spec 41 AC-15); every other detail goes in parentheses.
    private static string Refusal(ItemRefused refused)
    {
        string word = ArchiveRefusalWire.ToWire(refused.Refusal);
        return refused.Detail is null ? word
            : refused.Refusal == ArchiveRefusal.SecretPattern ? word + " " + refused.Detail
            : word + " (" + refused.Detail + ")";
    }
}
