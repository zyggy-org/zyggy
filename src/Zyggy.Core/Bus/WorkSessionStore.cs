using System.Globalization;
using System.Text;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>What this machine remembers about one live session between two hook calls (deliverable 18): never on the bus.</summary>
/// <param name="Since">When the session was first seen.</param>
/// <param name="Published">When its last status reached the bus checkout; <see langword="null"/> when none did.</param>
/// <param name="Step">The step the session stated, when it did.</param>
/// <param name="Failing"><see langword="true"/> while publishing keeps failing, so the failure is reported once per streak.</param>
internal sealed record WorkSessionState(DateTimeOffset Since, DateTimeOffset? Published = null, string? Step = null, bool Failing = false);

/// <summary>
/// The per-session state files of the status hooks, under <c>&lt;nodeDir&gt;/work/&lt;tenant&gt;/&lt;session&gt;.state</c>, beside the context
/// spool. One small text file per session: <c>key=value</c> lines, UTF-8 without BOM, written through a temporary file.
/// </summary>
internal sealed class WorkSessionStore
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;

    public WorkSessionStore(string nodeDir, TenantId tenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        ArgumentNullException.ThrowIfNull(tenant);
        _directory = Path.Join(nodeDir, "work", tenant.Value);
    }

    /// <summary>The state of <paramref name="session"/>, or <see langword="null"/> when there is none or the file cannot be read as one.</summary>
    public WorkSessionState? Load(string session)
    {
        string path = PathOf(session);
        if (!File.Exists(path))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path, Utf8))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..];
            }
        }

        if (!values.TryGetValue("since", out string? sinceText) || !TryTimestamp(sinceText, out DateTimeOffset since))
        {
            return null;
        }

        DateTimeOffset? published = values.TryGetValue("published", out string? publishedText) && TryTimestamp(publishedText, out DateTimeOffset p) ? p : null;
        return new WorkSessionState(since, published, values.GetValueOrDefault("step"), values.ContainsKey("failing"));
    }

    public void Save(string session, WorkSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_directory);
        var text = new StringBuilder().Append("since=").Append(Timestamp(state.Since)).Append('\n');
        if (state.Published is { } published)
        {
            text.Append("published=").Append(Timestamp(published)).Append('\n');
        }

        if (state.Step is not null)
        {
            text.Append("step=").Append(state.Step).Append('\n');
        }

        if (state.Failing)
        {
            text.Append("failing=1\n");
        }

        string path = PathOf(session);
        string temporary = path + ".zyggy-tmp-" + Ulid.NewUlid();
        File.WriteAllText(temporary, text.ToString(), Utf8);
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(string session) => File.Delete(PathOf(session));

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool TryTimestamp(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private string PathOf(string session) => Path.Join(_directory, session + ".state");
}
