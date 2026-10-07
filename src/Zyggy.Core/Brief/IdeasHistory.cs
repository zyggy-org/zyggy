using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Zyggy.Core.M365;

namespace Zyggy.Core.Brief;

/// <summary>One row of <c>ideas.jsonl</c>: <c>shown</c> (put in a brief) or <c>answer</c> (the owner's reply, Step 12).</summary>
internal sealed record IdeaRow(DateOnly Date, string Kind, string Id, string Area, DateOnly? Deadline, string? Answer, DateOnly? Until);

/// <summary>
/// <c>brief/ideas.jsonl</c> (spec 35 AC-35, AC-42): read, appended and pruned under an exclusive lock (the action log's shape), 0600.
/// </summary>
internal sealed class IdeasHistory(BriefPaths paths)
{
    private const int LockAttempts = 50;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public IReadOnlyList<IdeaRow> Read()
    {
        if (!File.Exists(paths.IdeasLog))
        {
            return [];
        }

        return [.. File.ReadAllLines(paths.IdeasLog, Encoding.UTF8).Select(Parse).OfType<IdeaRow>()];
    }

    /// <summary>One <c>shown</c> row per kept suggestion.</summary>
    /// <exception cref="IOException">Thrown when the rows cannot be appended.</exception>
    public void AppendShown(DateOnly date, IEnumerable<(string Id, string Area, DateOnly? Deadline)> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var rows = shown.Select(s => Row(new IdeaRow(date, "shown", s.Id, s.Area, s.Deadline, null, null))).ToList();
        if (rows.Count > 0)
        {
            Locked(stream =>
            {
                stream.Seek(0, SeekOrigin.End);
                stream.Write(Encoding.UTF8.GetBytes(string.Concat(rows.Select(r => r + "\n"))));
            });
        }
    }

    /// <summary>The owner's answer to a suggestion (spec 35 AC-36), dated the day it was given.</summary>
    /// <exception cref="IOException">Thrown when the row cannot be appended.</exception>
    public void AppendAnswer(DateOnly date, string id, string area, string answer, DateOnly? until)
    {
        var row = Row(new IdeaRow(date, "answer", id, area, null, answer, until));
        Locked(stream =>
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write(Encoding.UTF8.GetBytes(row + "\n"));
        });
    }

    /// <summary>Drops rows older than <paramref name="suppressDays"/>, except a <c>later</c> answer whose date is still ahead.</summary>
    public void Prune(DateOnly today, int suppressDays)
    {
        if (!File.Exists(paths.IdeasLog))
        {
            return;
        }

        var oldest = today.AddDays(-suppressDays);
        Locked(stream =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var kept = lines.Where(line => Parse(line) is { } row && (row.Date >= oldest || (row.Answer == "later" && row.Until > today))).ToList();
            if (kept.Count == lines.Length)
            {
                return;
            }

            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes(string.Concat(kept.Select(l => l + "\n"))));
        });
    }

    private void Locked(Action<FileStream> work)
    {
        StateFiles.EnsureDirectory(paths.Directory);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(paths.IdeasLog, options))
                {
                    work(stream);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(paths.IdeasLog, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                return;
            }
            catch (IOException) when (attempt < LockAttempts)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static IdeaRow? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            DateOnly? Date(string name) =>
                Text(name) is { } t && DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

            return Date("date") is { } date && Text("kind") is { } kind && Text("id") is { } id && Text("area") is { } area
                ? new IdeaRow(date, kind, id, area, Date("deadline"), Text("answer"), Date("until"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Row(IdeaRow row)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WriteString("date", BriefPaths.Iso(row.Date));
            writer.WriteString("kind", row.Kind);
            writer.WriteString("id", row.Id);
            writer.WriteString("area", row.Area);
            if (row.Deadline is { } deadline)
            {
                writer.WriteString("deadline", BriefPaths.Iso(deadline));
            }

            if (row.Answer is { } answer)
            {
                writer.WriteString("answer", answer);
            }

            if (row.Until is { } until)
            {
                writer.WriteString("until", BriefPaths.Iso(until));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
