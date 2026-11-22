using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Zyggy.Core.Configuration;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Outlook;

/// <summary>
/// <c>zyggy outlook policy | count | list | read</c> (spec 58 slice 2): the read tools of the work mailbox for Claude Code on the work
/// laptop, over the internal Outlook access (COM in production, a fake in tests). Every tool reads <c>node.json</c> and the policy
/// record first (the copied <c>work-boundary.json</c> in the node directory, else the <c>outlook.policyConfirmed</c> mirror) and
/// touches Outlook only once both allow a read. Output is JSON on stdout with invariant ISO 8601 dates; mail is printed as data.
/// </summary>
/// <remarks>
/// Exit codes: 0 · 1 folder or item not found · 2 usage · 3 configuration · 5 refused (<c>policy not confirmed</c>, <c>outlook not
/// enabled</c>) · 6 Outlook not available, or Outlook asked for access (its security prompt was not answered within the timeout).
/// Reads are not actions (D-Q6): a bus job or an unattended run may call them.
/// </remarks>
public sealed class OutlookVerbHost
{
    private const string Prefix = "outlook: ";
    private const string Usage = " (usage: zyggy outlook policy | count [--folder <name>]... | list [--folder <name>] [--since <date>] [--max <n>] | read <reference>; each [--config <node.json>])";
    private const string ReadScope = "read";

    private static readonly TimeSpan DefaultSince = TimeSpan.FromDays(7);

    private static readonly JsonWriterOptions Indented = new() { Indented = true, NewLine = "\n" };

    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly Func<IOutlookAccess> _access;

    /// <summary>Creates the host over the process environment, the system clock, the machine's time zone and the real Outlook access.</summary>
    /// <param name="environment">The process environment.</param>
    public OutlookVerbHost(IReadOnlyDictionary<string, string?> environment)
        : this(environment, TimeProvider.System, TimeZoneInfo.Local, null)
    {
    }

    internal OutlookVerbHost(IReadOnlyDictionary<string, string?> environment, TimeProvider clock, TimeZoneInfo zone, Func<IOutlookAccess>? access)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zone);
        _environment = environment;
        _clock = clock;
        _zone = zone;
        _access = access ?? RealAccess;
    }

    /// <summary>
    /// Creates the host from a host's services: the <see cref="TimeProvider"/> when one is registered, and the Outlook access a test
    /// registers at the internal seam; the real COM access otherwise.
    /// </summary>
    /// <param name="services">The services, or <see langword="null"/> for the production defaults.</param>
    /// <param name="environment">The process environment.</param>
    /// <returns>The host.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="environment"/> is null.</exception>
    public static OutlookVerbHost FromServices(IServiceProvider? services, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        IOutlookAccess? access = services?.GetService<IOutlookAccess>();
        return new OutlookVerbHost(environment, services?.GetService<TimeProvider>() ?? TimeProvider.System, TimeZoneInfo.Local, access is null ? null : () => access);
    }

    /// <summary>Runs <c>outlook &lt;verb&gt; …</c>.</summary>
    /// <param name="args">The arguments after <c>outlook</c>.</param>
    /// <param name="io">The console.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        Arguments? parsed = Arguments.Parse(args, _zone, out string? usageError);
        if (parsed is null)
        {
            await io.Error.WriteAsync(Prefix + usageError + Usage + "\n").ConfigureAwait(false);
            return 2;
        }

        string path = NodeConfigurationReader.ResolvePath(parsed.Config, _environment);
        NodeConfigurationResult configuration = NodeConfigurationReader.Read(path);
        if (configuration.Configuration is not { } node)
        {
            await io.Error.WriteAsync(Prefix + "configuration error: " + configuration.Detail + "\n").ConfigureAwait(false);
            return 3;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var settings = OutlookSettings.FromNodeJson(node.Outlook);
        string copy = OutlookPolicy.CopyPath(node.EffectiveNodeDir);
        string? workBoundary = File.Exists(copy) ? await File.ReadAllTextAsync(copy, cancellationToken).ConfigureAwait(false) : null;
        OutlookPolicyDecision decision = OutlookPolicy.Evaluate(workBoundary, settings);
        if (parsed.Verb == "policy")
        {
            await io.Out.WriteAsync(PolicyText(decision, copy)).ConfigureAwait(false);
            return 0;
        }

        // The policy is checked before the access exists (AC-5): the COM object is created only inside a read the policy allowed.
        var reader = new OutlookReader(_access(), decision, settings, _zone);
        try
        {
            return parsed.Verb switch
            {
                "count" => await CountAsync(reader, parsed, io).ConfigureAwait(false),
                "list" => await ListAsync(reader, parsed, io).ConfigureAwait(false),
                _ => await ReadAsync(reader, parsed, io).ConfigureAwait(false),
            };
        }
        catch (OutlookPromptTimeoutException ex)
        {
            await io.Error.WriteAsync(Prefix + "Outlook asked for access: " + ex.Message + "; answer the prompt in Outlook or stop\n").ConfigureAwait(false);
            return 6;
        }
    }

    private static IOutlookAccess RealAccess() =>
        OperatingSystem.IsWindows() ? new OutlookComAccess(OutlookComAccess.DefaultCallTimeout) : new OutlookAccessUnavailable("Outlook runs on Windows only");

    private static string PolicyText(OutlookPolicyDecision decision, string copy)
    {
        var text = new StringBuilder();
        text.Append("policy: ").Append(decision.Source switch
        {
            OutlookPolicySource.WorkBoundaryFile => OutlookPolicy.WorkBoundaryFileName + " copy at " + copy,
            OutlookPolicySource.NodeJsonMirror => "node.json mirror (outlook.policyConfirmed)",
            _ => "none (no " + OutlookPolicy.WorkBoundaryFileName + " copy at " + copy + ", no outlook.policyConfirmed in node.json)",
        }).Append('\n');
        if (decision.Record is { } record)
        {
            text.Append("date: ").Append(record.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("scope: ").Append(string.Join(", ", OutlookPolicyRecord.KnownScopes.Where(record.Scope.Contains))).Append('\n');
        }

        text.Append("enabled: ").Append(decision.Enabled ? "true" : "false").Append('\n');
        text.Append("read: ").Append(decision.RefusalFor(ReadScope) is { } refusal ? "refused (" + OutlookReader.Describe(refusal) + ")" : "allowed").Append('\n');
        return text.ToString();
    }

    private static async Task<int> CountAsync(OutlookReader reader, Arguments parsed, VerbIo io)
    {
        OutlookResult<IReadOnlyList<OutlookFolderCount>> result = reader.Count(parsed.Folders);
        if (result.Value is not { } counts)
        {
            return await RefusedAsync(result, io).ConfigureAwait(false);
        }

        await WriteJsonAsync(io, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("folders");
            foreach (OutlookFolderCount count in counts)
            {
                writer.WriteStartObject();
                writer.WriteString("folder", count.Folder);
                writer.WriteNumber("count", count.Count);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ListAsync(OutlookReader reader, Arguments parsed, VerbIo io)
    {
        string folder = parsed.Folders.Count == 0 ? "Inbox" : parsed.Folders[0];
        DateTimeOffset since = parsed.Since ?? TimeZoneInfo.ConvertTime(_clock.GetUtcNow() - DefaultSince, _zone);
        OutlookResult<IReadOnlyList<OutlookMailSummary>> result = reader.List(folder, since, parsed.Max);
        if (result.Value is not { } items)
        {
            return await RefusedAsync(result, io).ConfigureAwait(false);
        }

        await WriteJsonAsync(io, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("folder", folder);
            writer.WriteString("since", since.ToString(OutlookDates.Format, CultureInfo.InvariantCulture));
            writer.WriteStartArray("items");
            foreach (OutlookMailSummary item in items)
            {
                writer.WriteStartObject();
                WriteSummary(writer, item);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ReadAsync(OutlookReader reader, Arguments parsed, VerbIo io)
    {
        OutlookResult<OutlookMail> result = reader.Read(parsed.Reference!);
        if (result.Value is not { } mail)
        {
            return await RefusedAsync(result, io).ConfigureAwait(false);
        }

        await WriteJsonAsync(io, writer =>
        {
            writer.WriteStartObject();
            WriteSummary(writer, mail.Summary);
            writer.WriteStartArray("attachments");
            foreach (string name in mail.AttachmentNames)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteNumber("attachmentCount", mail.AttachmentCount);
            if (mail.Body is null)
            {
                writer.WriteNull("body");
            }
            else
            {
                writer.WriteString("body", mail.Body);
            }

            writer.WriteBoolean("bodyTruncated", mail.BodyTruncated);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
        return 0;
    }

    private static void WriteSummary(Utf8JsonWriter writer, OutlookMailSummary item)
    {
        writer.WriteString("reference", item.Reference);
        writer.WriteString("senderName", item.SenderName);
        writer.WriteString("senderAddress", item.SenderAddress);
        writer.WriteString("subject", item.Subject);
        writer.WriteString("received", item.Received);
        writer.WriteString("importance", item.Importance);
        writer.WriteString("flag", item.Flag);
        writer.WriteBoolean("isRead", item.IsRead);
        writer.WriteBoolean("hasAttachments", item.HasAttachments);
        writer.WriteBoolean("isProtected", item.IsProtected);
    }

    private static async Task WriteJsonAsync(VerbIo io, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented))
        {
            write(writer);
        }

        await io.Out.WriteAsync(Encoding.UTF8.GetString(buffer.ToArray()) + "\n").ConfigureAwait(false);
    }

    private static async Task<int> RefusedAsync<T>(OutlookResult<T> result, VerbIo io)
        where T : class
    {
        (string line, int exit) = result.Refusal switch
        {
            OutlookRefusal.PolicyNotConfirmed or OutlookRefusal.NotEnabled => ("refused: " + result.Message, 5),
            OutlookRefusal.OutlookNotAvailable => (result.Message + (result.Detail is null ? string.Empty : " (" + result.Detail + ")"), 6),
            _ => (result.Message!, 1),
        };
        await io.Error.WriteAsync(Prefix + line + "\n").ConfigureAwait(false);
        return exit;
    }

    /// <summary>The parsed arguments: the verb, its options and <c>--config</c>.</summary>
    private sealed record Arguments(string Verb, string? Config, IReadOnlyList<string> Folders, DateTimeOffset? Since, int? Max, string? Reference)
    {
        private static readonly HashSet<string> Verbs = new(["policy", "count", "list", "read"], StringComparer.Ordinal);

        public static Arguments? Parse(IReadOnlyList<string> args, TimeZoneInfo zone, out string? error)
        {
            error = null;
            if (args.Count == 0)
            {
                error = "no verb given";
                return null;
            }

            string verb = args[0];
            if (!Verbs.Contains(verb))
            {
                error = $"unknown verb '{ShellText.Prefix(verb, 40)}'";
                return null;
            }

            string? config = null;
            var folders = new List<string>();
            DateTimeOffset? since = null;
            int? max = null;
            string? reference = null;
            for (var i = 1; i < args.Count; i++)
            {
                string arg = args[i];
                string? value = i + 1 < args.Count ? args[i + 1] : null;
                switch (arg)
                {
                    case "--config":
                    case "--folder":
                    case "--since":
                    case "--max":
                        if (string.IsNullOrEmpty(value))
                        {
                            error = arg + " needs a value";
                            return null;
                        }

                        i++;
                        break;
                    default:
                        if (verb == "read" && reference is null && !arg.StartsWith("--", StringComparison.Ordinal))
                        {
                            reference = arg;
                            continue;
                        }

                        error = $"unexpected argument '{ShellText.Prefix(arg, 40)}'";
                        return null;
                }

                switch (arg)
                {
                    case "--config":
                        config = value;
                        break;
                    case "--folder":
                        if (verb is not ("count" or "list"))
                        {
                            error = "--folder is for count and list";
                            return null;
                        }

                        folders.Add(value!);
                        break;
                    case "--since":
                        if (verb != "list" || !OutlookDates.TryParseSince(value!, zone, out DateTimeOffset parsedSince))
                        {
                            error = "--since takes YYYY-MM-DD or an ISO 8601 date with offset, for list";
                            return null;
                        }

                        since = parsedSince;
                        break;
                    default:
                        if (verb != "list" || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedMax) || parsedMax < 1)
                        {
                            error = "--max takes a positive integer, for list";
                            return null;
                        }

                        max = parsedMax;
                        break;
                }
            }

            if (verb == "read" && reference is null)
            {
                error = "read needs <reference>";
                return null;
            }

            if (verb == "list" && folders.Count > 1)
            {
                error = "list takes one --folder";
                return null;
            }

            return new Arguments(verb, config, folders, since, max, reference);
        }
    }
}
