using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Zyggy.Core.M365.Guard;

namespace Zyggy.Core.Outlook;

/// <summary>How one action call ended (spec 58 "Log").</summary>
internal enum OutlookActionOutcome
{
    /// <summary>Refused by the guard, before any prompt.</summary>
    Refused,

    /// <summary>The owner denied the permission prompt.</summary>
    Denied,

    /// <summary>Done in Outlook.</summary>
    Done,

    /// <summary>Outlook failed the call.</summary>
    Failed,
}

/// <summary>
/// One row of the outlook action log: time, action, outcome or refusal reason, item reference, and for a send the recipients and
/// subject — never the body (spec 58 "Log").
/// </summary>
/// <param name="Time">When the call ended.</param>
/// <param name="Action">The action.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Reason">The refusal or failure reason, if any.</param>
/// <param name="Reference">The item reference (the moved mail, or the mail a reply answers), if any.</param>
/// <param name="Recipients">For a send: the To and Cc recipients.</param>
/// <param name="Subject">For a send: the subject.</param>
/// <param name="Destination">For a move: the destination folder.</param>
internal sealed record OutlookActionLogRow(
    DateTimeOffset Time,
    OutlookAction Action,
    OutlookActionOutcome Outcome,
    string? Reason,
    string? Reference,
    IReadOnlyList<string> Recipients,
    string? Subject,
    string? Destination)
{
    /// <summary>Returns the row as one compact JSON line, with an invariant ISO 8601 UTC time and no body.</summary>
    /// <returns>The JSON line, without a line break.</returns>
    public string ToJsonLine()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("action", Action == OutlookAction.Send ? "send" : "move");
            writer.WriteString("outcome", Outcome switch
            {
                OutlookActionOutcome.Refused => "refused",
                OutlookActionOutcome.Denied => "denied",
                OutlookActionOutcome.Done => "done",
                _ => "failed",
            });
            WriteIfPresent(writer, "reason", Reason);
            WriteIfPresent(writer, "reference", Reference);
            if (Action == OutlookAction.Send)
            {
                writer.WriteStartArray("to");
                foreach (var recipient in Recipients)
                {
                    writer.WriteStringValue(recipient);
                }

                writer.WriteEndArray();
                WriteIfPresent(writer, "subject", Subject);
            }
            else
            {
                WriteIfPresent(writer, "destination", Destination);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>Where the outlook action rows go; the laptop's file in production, a list in tests.</summary>
internal interface IOutlookActionLog
{
    /// <summary>Appends one row.</summary>
    /// <param name="row">The row.</param>
    void Append(OutlookActionLogRow row);
}

/// <summary>
/// The outlook action log on the laptop: <c>actions.jsonl</c> in the given state directory, one JSON line per action call, appended
/// with the m365 log's lock and owner-only permissions (<see cref="ActionLog.Append"/>). The log never leaves the laptop; its
/// directory is chosen where the action tools are wired (slice 7).
/// </summary>
/// <param name="stateDirectory">The directory of the log (the laptop's outlook state directory).</param>
internal sealed class OutlookActionLog(string stateDirectory) : IOutlookActionLog
{
    private readonly ActionLog _file = new(stateDirectory);

    /// <summary>Gets the log file's path.</summary>
    public string FilePath => _file.FilePath;

    /// <inheritdoc/>
    public void Append(OutlookActionLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _file.Append(row.ToJsonLine());
    }
}
