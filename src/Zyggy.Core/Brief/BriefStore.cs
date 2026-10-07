using System.Globalization;
using System.Text;

using Zyggy.Core.M365;

namespace Zyggy.Core.Brief;

/// <summary>
/// The brief state directory's files (spec 35 Contracts "Files"): atomic 0600 writes in a 0700 directory, <c>last-shown</c>, the dates
/// that have a brief and a brief's text. Never a repository, never a model.
/// </summary>
internal sealed class BriefStore(BriefPaths paths)
{
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> through a temp file and a rename; the file 0600, the directory 0700.</summary>
    /// <exception cref="StateDirectoryException">Thrown when the directory cannot be created.</exception>
    public void WriteAtomically(string path, byte[] bytes) => StateFiles.WriteAtomically(paths.Directory, path, bytes);

    /// <summary>The date in <c>last-shown</c>; <see langword="null"/> when the file is absent or not a date.</summary>
    public DateOnly? ReadLastShown()
    {
        if (!File.Exists(paths.LastShown))
        {
            return null;
        }

        var text = File.ReadAllText(paths.LastShown).Trim();
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>Writes <c>last-shown</c> as one line <c>YYYY-MM-DD</c>.</summary>
    public void WriteLastShown(DateOnly date) => WriteAtomically(paths.LastShown, Encoding.UTF8.GetBytes(BriefPaths.Iso(date) + "\n"));

    /// <summary>The dates that have a <c>brief-&lt;date&gt;.md</c>, oldest first; none when the directory is absent.</summary>
    public IReadOnlyList<DateOnly> BriefDates()
    {
        if (!Directory.Exists(paths.Directory))
        {
            return [];
        }

        var dates = new List<DateOnly>();
        foreach (var file in Directory.EnumerateFiles(paths.Directory, "brief-*.md"))
        {
            if (BriefPaths.TryParseDate(Path.GetFileName(file), out var date, out var kind) && kind == BriefFileKind.Markdown)
            {
                dates.Add(date);
            }
        }

        dates.Sort();
        return dates;
    }

    /// <summary>The brief's text, bytes decoded as UTF-8.</summary>
    public string ReadMarkdown(DateOnly date) => File.ReadAllText(paths.Markdown(date), Encoding.UTF8);

    /// <summary>The brief's generation time: the sidecar's <c>generated</c> when it is there, else the <c>.md</c>'s last write (UTC).</summary>
    public DateTimeOffset Generated(DateOnly date)
    {
        var sidecar = paths.Sidecar(date);
        if (File.Exists(sidecar))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(sidecar));
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && document.RootElement.TryGetProperty("generated", out var generated)
                    && generated.ValueKind == System.Text.Json.JsonValueKind.String
                    && DateTimeOffset.TryParse(generated.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                {
                    return at;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A sidecar that is not JSON: the file time stands.
            }
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(paths.Markdown(date)), TimeSpan.Zero);
    }
}
