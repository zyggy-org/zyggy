using Zyggy.Core.M365.Graph;
using Zyggy.Core.Secrets;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 check [--counts] [--other-mailbox &lt;upn&gt;] [--drive &lt;id&gt;]</c>: <c>graph.sh check</c>'s <c>run_check</c> (spec 33 AC-17).
/// Exit 0 · 3 configuration or key · 4 usage · 5 scope not enforced · 6 identity or Graph failure.
/// </summary>
internal sealed class CheckVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365: ";
    private const string Usage = " (usage: zyggy m365 check [--counts] [--other-mailbox <upn>] [--drive <id>])";
    private const string BriefSubject = "Zyggy — morning brief";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        var counts = false;
        string? other = null;
        string? drive = null;
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : string.Empty;
            switch (args[i])
            {
                case "--counts":
                    if (counts)
                    {
                        return await UsageAsync(io, "--counts given twice").ConfigureAwait(false);
                    }

                    counts = true;
                    break;
                case "--other-mailbox":
                    if (!M365Grammar.Upn().IsMatch(value))
                    {
                        return await UsageAsync(io, "--other-mailbox needs a user principal name").ConfigureAwait(false);
                    }

                    if (other is not null)
                    {
                        return await UsageAsync(io, "--other-mailbox given twice").ConfigureAwait(false);
                    }

                    other = value;
                    i++;
                    break;
                case "--drive":
                    if (!M365Grammar.DriveId().IsMatch(value))
                    {
                        return await UsageAsync(io, "--drive needs a drive id").ConfigureAwait(false);
                    }

                    if (drive is not null)
                    {
                        return await UsageAsync(io, "--drive given twice").ConfigureAwait(false);
                    }

                    drive = value;
                    i++;
                    break;
                default:
                    return await UsageAsync(io, $"check: unexpected argument '{args[i]}'").ConfigureAwait(false);
            }
        }

        var (session, exit, error) = M365Session.Load(context, baseOnly: false);
        if (session is null)
        {
            return await FailAsync(io, exit, error!).ConfigureAwait(false);
        }

        if (session.Warning is not null)
        {
            await io.Error.WriteAsync(session.Warning + "\n").ConfigureAwait(false);
        }

        using var graph = session.CreateGraph(context);
        var reader = graph.Reader;
        var signIn = await reader.SignInAsync(cancellationToken).ConfigureAwait(false);
        if (reader.KeySource != CredentialSource.None)
        {
            await io.Error.WriteAsync($"key: {reader.KeySource.ToText()}\n").ConfigureAwait(false);
        }

        if (signIn is not null)
        {
            return await FailAsync(io, signIn).ConfigureAwait(false);
        }

        var folders = await reader.MailFoldersAsync(cancellationToken).ConfigureAwait(false);
        if (folders.Failure is not null)
        {
            return await FailAsync(io, folders.Failure).ConfigureAwait(false);
        }

        var drives = await reader.DrivesAsync(cancellationToken).ConfigureAwait(false);
        if (drives.Failure is not null)
        {
            return await FailAsync(io, drives.Failure).ConfigureAwait(false);
        }

        var config = session.Configuration;
        await io.Out.WriteAsync(
            $"m365: app {config.CertificateSubject} (tenant {config.TenantId}), mailbox {config.Mailbox}: " +
            $"folders {folders.Value!.Count} ({folders.Value.Count(f => f.Excluded)} excluded), " +
            $"drives {drives.Value!.Count} ({string.Join(", ", drives.Value.Select(d => d.Name))}), " +
            $"token minted {reader.TokenMinted}, state dir {session.Instance.Paths.StateDirectory}\n").ConfigureAwait(false);
        foreach (var d in drives.Value)
        {
            await io.Out.WriteAsync($"drive {d.Id} {d.Name} site={d.Site}\n").ConfigureAwait(false);
        }

        if (counts)
        {
            foreach (var f in folders.Value)
            {
                await io.Out.WriteAsync($"folder {f.Id} {f.DisplayName} {f.WellKnownName ?? "-"} {f.TotalItemCount}{(f.Excluded ? " excluded" : string.Empty)}\n")
                    .ConfigureAwait(false);
            }

            var drafts = await reader.GetAsync($"/users/{config.Mailbox}/mailFolders/drafts/messages?$select=id,subject&$top=50", _ => false, cancellationToken)
                .ConfigureAwait(false);
            if (drafts.Failure is not null)
            {
                return await FailAsync(io, drafts.Failure).ConfigureAwait(false);
            }

            await io.Out.WriteAsync($"zyggy-drafts {BriefDrafts(drafts.Value!.Body)}\n").ConfigureAwait(false);
        }

        if (other is not null)
        {
            var probe = await reader.GetAsync($"/users/{other}/mailFolders/inbox", status => status is 403 or 404, cancellationToken).ConfigureAwait(false);
            if (probe.Failure is not null)
            {
                return await FailAsync(io, probe.Failure).ConfigureAwait(false);
            }

            if (probe.Value!.Status is >= 200 and <= 299)
            {
                await io.Out.WriteAsync($"other mailbox {other}: {probe.Value.Status} — SCOPE NOT ENFORCED\n").ConfigureAwait(false);
                return await FailAsync(io, 5, $"refused: scope not enforced — {other} is readable; runbook 13 \"Scope or grant missing\"").ConfigureAwait(false);
            }

            await io.Out.WriteAsync($"other mailbox {other}: {probe.Value.Status} (expected: scope holds)\n").ConfigureAwait(false);
        }

        if (drive is not null)
        {
            var root = await reader.GetAsync($"/drives/{drive}/root", status => status is 403 or 404, cancellationToken).ConfigureAwait(false);
            if (root.Failure is not null)
            {
                return await FailAsync(io, root.Failure).ConfigureAwait(false);
            }

            var status = root.Value!.Status;
            var meaning = status switch
            {
                403 => "not granted",
                404 => "not found",
                _ => "granted",
            };
            await io.Out.WriteAsync($"drive {drive}: {status} ({meaning})\n").ConfigureAwait(false);
        }

        return 0;
    }

    private static int BriefDrafts(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
            ? value.EnumerateArray().Count(m =>
                m.TryGetProperty("subject", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                && s.GetString()!.StartsWith(BriefSubject, StringComparison.Ordinal))
            : 0;
    }

    private static async Task<int> UsageAsync(VerbIo io, string message)
    {
        await io.Error.WriteAsync(Prefix + message + Usage + "\n").ConfigureAwait(false);
        return 4;
    }

    private static Task<int> FailAsync(VerbIo io, GraphFailure failure) => FailAsync(io, failure.ExitCode, failure.Message);

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}
