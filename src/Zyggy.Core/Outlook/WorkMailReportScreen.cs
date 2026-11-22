using System.Globalization;
using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Outlook;

/// <summary>
/// The job runner's screen of a report of a job that read work mail (spec 58 slice 5, AC-16, AC-9): runs on the laptop before the
/// report is written to the bus, only while the job's marker exists (<see cref="OutlookJobRun"/>). It reads the shared term list from
/// the node directory; a report body or whole report (<c>report.md</c>) holding a national register number or a term, or a missing term
/// list, withholds the report as <c>failed</c> / <c>dlp_filter</c> with a fixed text that names nothing of what matched; otherwise the
/// count of mails the job kept on the laptop is appended. The marker is removed in every case. It is a second net behind the read
/// tools' source screen: the model never saw a kept mail, and a report that still names a register number or a term does not leave.
/// </summary>
internal static class WorkMailReportScreen
{
    /// <summary>Screens <paramref name="reply"/> when the job read work mail; returns it unchanged otherwise.</summary>
    /// <param name="reply">The runner's reply.</param>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="job">The job's id.</param>
    /// <returns>The reply to write to the bus.</returns>
    public static JobReply Apply(JobReply reply, string nodeDir, TenantId tenant, EnvelopeId job)
    {
        ArgumentNullException.ThrowIfNull(reply);
        string marker = OutlookJobRun.MarkerPath(nodeDir, tenant, job);
        if (!File.Exists(marker))
        {
            return reply;
        }

        try
        {
            int kept = OutlookJobRun.KeptCount(marker);
            string count = string.Create(CultureInfo.InvariantCulture, $"{kept} mails kept on this laptop (never-list or in doubt)");
            string terms = Path.Join(nodeDir, WorkMailShare.TermsFileName);
            WorkMailFilter? filter = File.Exists(terms) ? WorkMailFilter.Parse(File.ReadAllText(terms)) : null;
            string body = Encoding.UTF8.GetString(reply.Body.Span);
            string? whole = reply.Attachment is { } attachment ? Encoding.UTF8.GetString(attachment.Bytes.Span) : null;
            if (filter is null || filter.IsSensitiveText(body) || (whole is not null && filter.IsSensitiveText(whole)))
            {
                string reason = RunFailureReasonWire.ToWire(RunFailureReason.DlpFilter);
                return reply with
                {
                    Status = ReportStatus.Failed,
                    Reason = reason,
                    Body = Encoding.UTF8.GetBytes(reason + ": the work mail report was withheld on the laptop by the sensitive-data filter; " + count + "\n"),
                    Attachment = null,
                };
            }

            string separator = body.Length == 0 || body.EndsWith('\n') ? string.Empty : "\n";
            return reply with { Body = Encoding.UTF8.GetBytes(body + separator + "work mail: " + count + "\n") };
        }
        finally
        {
            File.Delete(marker);
        }
    }
}
