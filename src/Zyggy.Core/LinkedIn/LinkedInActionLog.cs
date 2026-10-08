using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Zyggy.Core.M365;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// One row of <c>actions.jsonl</c> (spec 36 AC-22): <c>{schema:1, ts, tool, urn, visibility, chars, sha256, text, status}</c>. A refusal
/// for content stores no text (spec 36 Assumption 2); <c>urn</c> only after a published post. A call with an image (plan 36b D7) adds
/// <c>image_sha256</c>, <c>image_bytes</c> and, once uploaded, <c>image_urn</c> — absent otherwise, so text rows keep their bytes — and
/// its <c>sha256</c> is the duplicate key <c>sha256(text + "\n" + image_sha256)</c>.
/// </summary>
internal sealed record ActionRow(
    int Schema,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset Ts,
    string Tool,
    string? Urn,
    string Visibility,
    int Chars,
    string Sha256,
    string? Text,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageSha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ImageBytes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrn = null)
{
    public const string Ok = "ok";
}

/// <summary>
/// The LinkedIn action log <c>&lt;ZYGGY_STATE_DIR&gt;/linkedin/actions.jsonl</c> with 33's append discipline: one line per call, 0600 in
/// the 0700 state directory, an exclusive lock held for the append (50 attempts, 100 ms apart). It is also the record the duplicate
/// check reads.
/// </summary>
internal sealed class LinkedInActionLog(LinkedInPaths paths)
{
    private const int LockAttempts = 50;

    private static readonly JsonSerializerOptions Options = new(LinkedInJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath => paths.ActionLog;

    /// <summary>Appends one row.</summary>
    /// <exception cref="IOException">Thrown when the row cannot be appended.</exception>
    /// <exception cref="StateDirectoryException">Thrown when the state directory cannot be created.</exception>
    public void Append(ActionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        StateFiles.EnsureDirectory(paths.StateDirectory);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row, Options.GetTypeInfo(typeof(ActionRow))) + "\n");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(FilePath, options))
                {
                    stream.Write(bytes);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                return;
            }
            catch (IOException) when (attempt < LockAttempts)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The newest <c>ok</c> row for the text whose SHA-256 is <paramref name="sha256"/> at or after <paramref name="since"/>.</summary>
    public ActionRow? RecentOk(string sha256, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (!File.Exists(FilePath))
        {
            return null;
        }

        string text;
        using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        ActionRow? found = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parse(line) is { Status: ActionRow.Ok } row && row.Sha256 == sha256 && row.Ts >= since)
            {
                found = row;
            }
        }

        return found;
    }

    private static ActionRow? Parse(string line)
    {
        try
        {
            return (ActionRow?)JsonSerializer.Deserialize(line, Options.GetTypeInfo(typeof(ActionRow)));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
