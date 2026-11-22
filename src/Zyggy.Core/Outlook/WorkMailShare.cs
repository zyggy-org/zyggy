using System.Globalization;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Outlook;

/// <summary>The facts the session wrote about one briefed mail, as <c>zyggy outlook share</c> reads them on stdin.</summary>
/// <param name="Reference">The mail's item reference.</param>
/// <param name="Facts">The facts: what the mail is about and asks, and body parts worth keeping; one memory line each.</param>
/// <param name="Doubt">Whether the session marked the mail in doubt (it may carry never-list data).</param>
internal sealed record WorkMailShareRequest(string Reference, IReadOnlyList<string> Facts, bool Doubt);

/// <summary>
/// The rules of <c>zyggy outlook share</c> (spec 58 slice 4): the stdin shape, the context lines work mail becomes
/// (<c>- [observed] &lt;today&gt; [work-mail &lt;received&gt;]: &lt;fact&gt;</c>, the provenance the dream files on the business side only), and
/// the kept-on-laptop count (<c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/outlook/kept.json</c>), which crosses once per past day as the summary text
/// of one line (D-Q8), never a mail's field.
/// </summary>
internal static class WorkMailShare
{
    /// <summary>The provenance source of every work mail line; the dream's checks key on it.</summary>
    public const string Source = "work-mail";

    /// <summary>The term list's file name in the node directory (D-Q9: the one shared, owner-approved list of the instance).</summary>
    public const string TermsFileName = "sensitive-terms.txt";

    private static readonly JsonWriterOptions Indented = new() { Indented = true, NewLine = "\n" };

    /// <summary>Returns the path of the kept count of a tenant on this machine.</summary>
    /// <param name="nodeDir">The node directory.</param>
    /// <param name="tenant">The tenant.</param>
    /// <returns>The path.</returns>
    public static string KeptPath(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        return Path.Join(nodeDir, "work", tenant.Value, "outlook", "kept.json");
    }

    /// <summary>Parses stdin: <c>{ "mails": [ { "reference": "…", "facts": ["…"], "doubt": false } ] }</c>; blank stdin is no mail.</summary>
    /// <param name="stdin">The text, or <see langword="null"/> when stdin is not redirected.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The requests, or <see langword="null"/> when refused.</returns>
    public static List<WorkMailShareRequest>? Parse(string? stdin, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(stdin))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(stdin);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mails", out var mails)
                || mails.ValueKind != JsonValueKind.Array)
            {
                error = "no mails array";
                return null;
            }

            var requests = new List<WorkMailShareRequest>();
            foreach (var mail in mails.EnumerateArray())
            {
                string where = "mail " + (requests.Count + 1).ToString(CultureInfo.InvariantCulture);
                if (mail.ValueKind != JsonValueKind.Object
                    || !mail.TryGetProperty("reference", out var reference) || reference.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(reference.GetString())
                    || !mail.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Array)
                {
                    error = where + " needs a reference and a facts array";
                    return null;
                }

                var texts = new List<string>();
                foreach (var fact in facts.EnumerateArray())
                {
                    string collapsed = fact.ValueKind == JsonValueKind.String ? TextCollapse.Line(fact.GetString()!) : string.Empty;
                    if (collapsed.Length == 0)
                    {
                        error = where + " has an empty or non-text fact";
                        return null;
                    }

                    texts.Add(collapsed);
                }

                bool doubt = mail.TryGetProperty("doubt", out var d) && d.ValueKind == JsonValueKind.True;
                requests.Add(new WorkMailShareRequest(reference.GetString()!, texts, doubt));
            }

            return requests;
        }
        catch (JsonException)
        {
            error = "not JSON";
            return null;
        }
    }

    /// <summary>Formats one work mail fact as a context line.</summary>
    /// <param name="today">Today (UTC), the line's date.</param>
    /// <param name="received">The mail's received date, the provenance date.</param>
    /// <param name="fact">The fact, one line.</param>
    /// <returns>The line.</returns>
    public static string Line(DateOnly today, DateOnly received, string fact) =>
        FactLine.Format("observed", string.Empty, Source + " " + Iso(received), Iso(today), fact);

    /// <summary>Formats the kept-on-laptop count of one day as the summary text of a context line (D-Q8).</summary>
    /// <param name="day">The day counted.</param>
    /// <param name="count">How many mails stayed on the laptop that day.</param>
    /// <returns>The line.</returns>
    public static string CountLine(DateOnly day, int count) =>
        Line(day, day, count.ToString(CultureInfo.InvariantCulture) + " work mails kept on the laptop (never-list or in doubt)");

    /// <summary>Reads the kept counts per day; empty when there is none or it cannot be read.</summary>
    /// <param name="path">The kept file.</param>
    /// <returns>The counts.</returns>
    public static SortedDictionary<DateOnly, int> LoadKept(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var kept = new SortedDictionary<DateOnly, int>();
        if (!File.Exists(path))
        {
            return kept;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("days", out var days) && days.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in days.EnumerateObject())
                {
                    if (DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        && day.Value.ValueKind == JsonValueKind.Number && day.Value.TryGetInt32(out var count) && count > 0)
                    {
                        kept[date] = count;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A damaged count file starts again at zero: a count is a summary, never a reason to fail a share.
        }

        return kept;
    }

    /// <summary>Writes the kept counts atomically.</summary>
    /// <param name="path">The kept file.</param>
    /// <param name="kept">The counts.</param>
    public static void SaveKept(string path, SortedDictionary<DateOnly, int> kept)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(kept);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Indented))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("days");
            foreach (var (day, count) in kept)
            {
                writer.WriteNumber(Iso(day), count);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        MemoryFileWriter.WriteAtomically(path, Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
